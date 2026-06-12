using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Verse;
using RimWorld;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責偵錯面板（Debug）分頁的 UI 渲染與呼叫日誌、診斷匯出管理。
    /// </summary>
    public static class DebugSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        // 偵錯面板專屬的 UI 暫存狀態
        private static Vector2 debugScrollPosition = Vector2.zero;

        /// <summary>
        /// 獲取偵錯分頁詳細內容的滾動高度。
        /// </summary>
        public static float GetHeight(float width)
        {
            return 690f;
        }

        /// <summary>
        /// 繪製偵錯設定與日誌。
        /// </summary>
        public static void DrawDebugSettings(Listing_Standard listing)
        {
            listing.Label("RimLLM_TitleDebugExplanation".Translate());
            listing.Gap(8f);

            // Token 與費用累計統計看板
            listing.Label("<b>" + "RimLLM_UsageHeader".Translate() + "</b>");
            Rect usageRect = listing.GetRect(50f);
            Widgets.DrawMenuSection(usageRect);

            Rect usageInfoRect = new Rect(usageRect.x + 8f, usageRect.y + 4f, usageRect.width - 160f, usageRect.height - 8f);
            Rect resetUsageBtnRect = new Rect(usageRect.xMax - 150f, usageRect.y + 10f, 140f, 30f);

            Text.Anchor = TextAnchor.MiddleLeft;
            string usageText = "RimLLM_UsageInfo".Translate(
                Settings.TotalPromptTokens.ToString(),
                Settings.TotalCompletionTokens.ToString(),
                Settings.TotalEstimatedCost.ToString("F4")
            );
            Widgets.Label(usageInfoRect, usageText);
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonText(resetUsageBtnRect, "RimLLM_ResetUsageBtn".Translate()))
            {
                if (RimLLMProvider.Instance is RimLLMManager managerInstance)
                {
                    managerInstance.ResetUsage();
                    Messages.Message("RimLLM_MsgUsageReset".Translate(), MessageTypeDefOf.PositiveEvent, false);
                }
            }
            listing.Gap(10f);

            bool prevDetailedLogging = Settings.DetailedLogging;
            // 詳細日誌
            bool detailedLogging = Settings.DetailedLogging;
            listing.CheckboxLabeled("RimLLM_DetailedLogging".Translate(), ref detailedLogging, "RimLLM_DetailedLoggingExplanation".Translate());
            Settings.DetailedLogging = detailedLogging;
            RimLLMLog.Enabled = detailedLogging;
            if (prevDetailedLogging != Settings.DetailedLogging)
            {
                Settings.Write();
            }
            listing.Gap(8f);

            // 匯出診斷按鈕
            Rect exportRect = listing.GetRect(30f);
            if (Widgets.ButtonText(exportRect, "RimLLM_ExportDiag".Translate()))
            {
                ExportDiagnostics();
            }
            listing.Gap(12f);

            // 最近請求紀錄日誌
            Rect headerRect = listing.GetRect(30f);
            Rect labelRect = new Rect(headerRect.x, headerRect.y, headerRect.width - 150f, headerRect.height);
            Rect clearBtnRect = new Rect(headerRect.x + headerRect.width - 140f, headerRect.y + 2f, 140f, headerRect.height - 4f);

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(labelRect, "<b>" + "RimLLM_RecentRequests".Translate(30) + "</b>");
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonText(clearBtnRect, "RimLLM_ClearRequestsBtn".Translate()))
            {
                if (RimLLMProvider.Instance is RimLLMManager managerInstance)
                {
                    managerInstance.ClearLogs();
                    Messages.Message("RimLLM_MsgLogsCleared".Translate(), MessageTypeDefOf.PositiveEvent, false);
                }
            }
            listing.Gap(4f);

            if (RimLLMProvider.Instance is RimLLMManager manager)
            {
                var logs = new List<RimLLMManager.RequestLogEntry>(manager.RequestLogs);
                logs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                if (logs.Count == 0)
                {
                    listing.Label("RimLLM_NoRequests".Translate());
                }
                else
                {
                    Rect logScrollRect = listing.GetRect(420f);
                    Widgets.DrawMenuSection(logScrollRect);

                    float contentWidth = logScrollRect.width - 16f;
                    float logHeight = 24f;
                    float viewHeight = Math.Max(420f, logs.Count * logHeight + 10f);
                    Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);
                    Widgets.BeginScrollView(logScrollRect, ref debugScrollPosition, viewRect);

                    for (int i = 0; i < logs.Count; i++)
                    {
                        var log = logs[i];
                        Rect lineRect = new Rect(4f, i * logHeight + 4f, contentWidth - 8f, logHeight - 2f);

                        string timeStr = log.Timestamp.ToString("HH:mm:ss");
                        string statusText = log.Success
                            ? $"<color=#22c55e>SUCCESS</color> ({log.LatencyMs}ms)"
                            : $"<color=#ef4444>FAILED</color> (Err: {RimLLMLog.SanitizeForLog(log.ErrorMessage, 160)})";

                        string logLine = $"[{timeStr}] Mod: {log.ModId} | {log.Provider} ({log.Model}) | {statusText}";

                        Text.Font = GameFont.Tiny;
                        Widgets.Label(lineRect, logLine);
                        Text.Font = GameFont.Small;
                    }
                    Widgets.EndScrollView();
                }
            }
        }

        private static void ExportDiagnostics()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== RimLLM Framework Diagnostics ===");
                sb.AppendLine($"Export Time: {DateTime.Now}");
                sb.AppendLine($"OS: {SystemInfo.operatingSystem}");
                string maskedDeviceId = "Masked_";
                try
                {
                    string rawId = SystemInfo.deviceUniqueIdentifier;
                    if (!string.IsNullOrEmpty(rawId) && rawId.Length > 8)
                    {
                        maskedDeviceId = rawId.Substring(0, 4) + "****" + rawId.Substring(rawId.Length - 4);
                    }
                    else
                    {
                        maskedDeviceId = rawId;
                    }
                }
                catch
                {
                    maskedDeviceId = "Unknown";
                }
                sb.AppendLine($"Device ID: {maskedDeviceId}");
                sb.AppendLine($"Max Concurrent Requests Setting: {Settings.MaxConcurrentRequests}");
                sb.AppendLine($"Timeout Setting: {Settings.ApiTimeout}s");
                sb.AppendLine($"Max Retries Setting: {Settings.MaxRetries}");
                sb.AppendLine($"Retry Delay Setting: {Settings.RetryDelay}s");
                sb.AppendLine($"Detailed Logging: {Settings.DetailedLogging}");
                sb.AppendLine();

                sb.AppendLine("=== Fallback Chain ===");
                var fallbackChain = Settings.FallbackChain;
                for (int i = 0; i < fallbackChain.Count; i++)
                {
                    sb.AppendLine($"  {i + 1}. {fallbackChain[i]}");
                }
                sb.AppendLine();
                sb.AppendLine("=== Provider Setup ===");
                List<string> providers;
                try
                {
                    providers = RimLLMProvider.Instance.GetRegisteredProviderIds();
                }
                catch (InvalidOperationException)
                {
                    providers = new List<string>
                    {
                        ProviderIds.Gemini, ProviderIds.OpenAI, ProviderIds.DeepSeek, ProviderIds.Groq,
                        ProviderIds.Anthropic, ProviderIds.OpenRouter, ProviderIds.Kimi, ProviderIds.MiniMax,
                        ProviderIds.Qwen, ProviderIds.Nvidia, ProviderIds.OpenAICompatible
                    };
                }
                foreach (var prov in providers)
                {
                    bool enabled = Settings.IsProviderEnabled(prov);
                    bool hasKey = !string.IsNullOrEmpty(Settings.GetApiKey(prov));
                    string endpoint = Settings.GetEndpoint(prov, "default");
                    sb.AppendLine($"  {prov}: Enabled={enabled}, HasKey={hasKey}, Endpoint={MaskEndpoint(endpoint)}");
                    var models = Settings.GetModelList(prov);
                    string modelPreview = models.Count > 20
                        ? string.Join(", ", models.GetRange(0, 20)) + $", ... ({models.Count - 20} more)"
                        : string.Join(", ", models);
                    sb.AppendLine($"    Cached Models ({models.Count}): {RimLLMLog.SanitizeForLog(modelPreview, 1000)}");
                }
                sb.AppendLine();
                sb.AppendLine("=== Recent Request Logs ===");
                if (RimLLMProvider.Instance is RimLLMManager manager)
                {
                    var logs = manager.RequestLogs.ToArray();
                    if (logs.Length == 0)
                    {
                        sb.AppendLine("  No requests recorded in this session.");
                    }
                    else
                    {
                        foreach (var log in logs)
                        {
                            string status = log.Success ? "SUCCESS" : $"FAILED ({RimLLMLog.SanitizeForLog(log.ErrorMessage, 200)})";
                            sb.AppendLine($"  [{log.Timestamp:yyyy-MM-dd HH:mm:ss}] Mod: {log.ModId} | Provider: {log.Provider} ({log.Model}) | {status} | Latency: {log.LatencyMs}ms");
                        }
                    }
                }
                string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimLLM_Diagnostic.txt");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Messages.Message("RimLLM_ExportDiagSuccess".Translate(path), MessageTypeDefOf.PositiveEvent, false);
            }
            catch (Exception ex)
            {
                Messages.Message("RimLLM_ExportDiagFailed".Translate(RimLLMLog.SanitizeForLog(ex.Message, 200)), MessageTypeDefOf.RejectInput, false);
            }
        }

        private static string MaskEndpoint(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return endpoint;
            try
            {
                var uri = new Uri(endpoint);
                string port = uri.IsDefaultPort ? "" : $":{uri.Port}";
                return $"{uri.Scheme}://{uri.Host}{port}/...";
            }
            catch
            {
                return RimLLMLog.SanitizeForLog(endpoint, 120);
            }
        }
    }
}
