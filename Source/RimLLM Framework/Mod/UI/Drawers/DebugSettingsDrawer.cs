using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using UnityEngine;
using Verse;
using RimWorld;
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
        private static string schemaSelfTestStatus = string.Empty;
        private static bool schemaSelfTestRunning;

        /// <summary>
        /// 結構化輸出自我檢查用的型別。
        /// 刻意包含 <c>Nullable&lt;T&gt;</c>、清單與列舉：這三者正是各供應商 schema 方言差異最大的地方，
        /// 只用純量成員的話兩種方言會產出相同的 schema，測不出方言接線是否正確。
        /// </summary>
        private sealed class SchemaSelfTestPayload
        {
            public string Summary { get; set; }
            public int Score { get; set; }
            public int? OptionalScore { get; set; }
            public List<string> Tags { get; set; }
        }

        /// <summary>
        /// 獲取偵錯分頁詳細內容的滾動高度。
        /// </summary>
        public static float GetHeight(float width)
        {
            // 自我檢查按鈕，加上狀態列 —— 失敗時會多帶一段展平後的例外鏈，需要更多空間。
            float statusHeight = string.IsNullOrEmpty(schemaSelfTestStatus)
                ? 0f
                : (schemaSelfTestStatus.Contains("\n") ? 150f : 48f);
            return 690f + 42f + statusHeight;
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

            if (Widgets.ButtonText(resetUsageBtnRect, "RimLLM_ResetUsageBtn".Translate()) &&
                RimLLMProvider.TryGetManager(out var usageManager))
            {
                usageManager.ResetUsage();
                Messages.Message("RimLLM_MsgUsageReset".Translate(), MessageTypeDefOf.PositiveEvent, false);
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
            listing.Gap(8f);

            // 結構化輸出自我檢查
            Rect schemaTestRect = listing.GetRect(30f);
            if (Widgets.ButtonText(schemaTestRect, "RimLLM_SchemaSelfTest".Translate()) && !schemaSelfTestRunning)
            {
                RunSchemaSelfTest();
            }
            if (!string.IsNullOrEmpty(schemaSelfTestStatus))
            {
                listing.Gap(4f);
                listing.Label(schemaSelfTestStatus);
            }
            listing.Gap(12f);

            // 最近請求紀錄日誌
            Rect headerRect = listing.GetRect(30f);
            Rect labelRect = new Rect(headerRect.x, headerRect.y, headerRect.width - 150f, headerRect.height);
            Rect clearBtnRect = new Rect(headerRect.x + headerRect.width - 140f, headerRect.y + 2f, 140f, headerRect.height - 4f);

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(labelRect, "<b>" + "RimLLM_RecentRequests".Translate(30) + "</b>");
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonText(clearBtnRect, "RimLLM_ClearRequestsBtn".Translate()) &&
                RimLLMProvider.TryGetManager(out var logManager))
            {
                logManager.ClearLogs();
                Messages.Message("RimLLM_MsgLogsCleared".Translate(), MessageTypeDefOf.PositiveEvent, false);
            }
            listing.Gap(4f);

            if (RimLLMProvider.TryGetManager(out var manager))
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
                        // 色碼保留在程式碼中，只有文字部分抽成翻譯鍵，避免譯者需要處理富文字標記。
                        string statusText = log.Success
                            ? $"<color=#22c55e>{"RimLLM_StatusRequestSuccess".Translate(log.LatencyMs)}</color>"
                            : $"<color=#ef4444>{"RimLLM_StatusRequestFailed".Translate(RimLLMLog.SanitizeForLog(log.ErrorMessage, 160))}</color>";

                        string logLine = $"[{timeStr}] Mod: {log.ModId} | {log.Provider} ({log.Model}) | {statusText}";

                        Text.Font = GameFont.Tiny;
                        Widgets.Label(lineRect, logLine);
                        Text.Font = GameFont.Small;
                    }
                    Widgets.EndScrollView();
                }
            }
        }

        /// <summary>
        /// 結構化輸出自我檢查。分兩段：
        ///
        /// 第一段在本機完成，不發網路請求也不花錢 —— 產生兩種供應商方言的 schema，藉此確認
        /// <c>System.Text.Json</c> 的 <c>JsonSchemaExporter</c> 在 RimWorld 的 Mono 執行環境可用。
        /// 這件事單元測試驗不到（測試跑在真正的 .NET Framework 上），而失敗模式是靜默降級成舊的
        /// 反射實作，不主動檢查就不會有人發現。
        ///
        /// 第二段才真的向目前的 fallback 鏈發一次結構化請求，驗證供應商確實接受這份 schema。
        /// </summary>
        private static void RunSchemaSelfTest()
        {
            schemaSelfTestRunning = true;
            schemaSelfTestStatus = "RimLLM_SchemaSelfTestRunning".Translate();

            // 降級是永久性的（一次失敗即黏住，避免每次請求都吃例外成本），
            // 所以自我檢查必須先解除，否則第二次之後按下去都只是在讀上一次的結論而非重新探測。
            RimLLMSchemaBuilder.ForceLegacy = false;

            RimLLMSchemaResult openAiSchema;
            RimLLMSchemaResult geminiSchema;
            try
            {
                openAiSchema = RimLLMSchemaBuilder.Build(typeof(SchemaSelfTestPayload), RimLLMSchemaProfile.OpenAI);
                geminiSchema = RimLLMSchemaBuilder.Build(typeof(SchemaSelfTestPayload), RimLLMSchemaProfile.Gemini);
            }
            catch (Exception exception)
            {
                schemaSelfTestRunning = false;
                schemaSelfTestStatus = "<color=#ef4444>" +
                    "RimLLM_SchemaSelfTestBuildFailed".Translate(RimLLMLog.SanitizeForLog(exception.Message, 200)) +
                    "</color>";
                return;
            }

            Log.Message("[RimLLM] Schema self-test (OpenAI profile): " + openAiSchema.Json);
            Log.Message("[RimLLM] Schema self-test (Gemini profile): " + geminiSchema.Json);

            if (openAiSchema.UsedLegacyFallback)
            {
                // exporter 不可用時框架仍能運作，但會失去 description、精確的型別對照與 strict 相容性。
                string failure = RimLLMSchemaBuilder.LastExporterFailure;
                Log.Warning("[RimLLM] Schema exporter failure: " + (failure ?? "(未記錄)"));

                schemaSelfTestRunning = false;
                schemaSelfTestStatus = "<color=#f59e0b>" + "RimLLM_SchemaSelfTestLegacy".Translate() + "</color>";
                if (!string.IsNullOrEmpty(failure))
                {
                    schemaSelfTestStatus += "\n<color=#ef4444>" + RimLLMLog.SanitizeForLog(failure, 400) + "</color>";
                }
                return;
            }

            schemaSelfTestStatus = "RimLLM_SchemaSelfTestLocalOk".Translate();

            Task.Run(async () =>
            {
                string result;
                try
                {
                    IChatClient client = RimLLMProvider.CreateChatClient("RimLLM.SchemaSelfTest");
                    SchemaSelfTestPayload payload = await client.GetResponseObjectAsync<SchemaSelfTestPayload>(
                        new List<ChatMessage>
                        {
                            new ChatMessage(
                                ChatRole.User,
                                "Reply with a short structured self-test record. " +
                                "Summary: any single sentence. Score: any integer 0-100. " +
                                "OptionalScore: may be null. Tags: two or three short words.")
                        }).ConfigureAwait(false);

                    result = "<color=#22c55e>" + "RimLLM_SchemaSelfTestOk".Translate(
                        RimLLMLog.SanitizeForLog(payload?.Summary ?? string.Empty, 80),
                        (payload?.Score ?? 0).ToString()) + "</color>";
                }
                catch (Exception exception)
                {
                    result = "<color=#ef4444>" + "RimLLM_SchemaSelfTestRemoteFailed".Translate(
                        RimLLMLog.SanitizeForLog(exception.Message, 200)) + "</color>";
                }

                RimLLMDispatcher.EnqueueOnMainThread(() =>
                {
                    schemaSelfTestStatus = result;
                    schemaSelfTestRunning = false;
                });
            });
        }

        private static void ExportDiagnostics()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== RimLLM Framework Diagnostics ===");
                sb.AppendLine($"Export Time: {DateTime.Now}");
                sb.AppendLine($"OS: {SystemInfo.operatingSystem}");
                string maskedDeviceId;
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
                List<string> providers = RimLLMProvider.TryGetManager(out var providerManager)
                    ? providerManager.GetRegisteredProviderIds()
                    : new List<string>(ProviderIds.BuiltIn);
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
                if (RimLLMProvider.TryGetManager(out var logManager))
                {
                    var logs = logManager.RequestLogs.ToArray();
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
