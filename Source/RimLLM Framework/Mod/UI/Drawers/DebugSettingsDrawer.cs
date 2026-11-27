using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>偵錯面板（Debug）分頁：呼叫日誌與診斷匯出。</summary>
public static class DebugSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private static Vector2 debugScrollPosition = Vector2.zero;
    private static string schemaSelfTestStatus = string.Empty;
    private static bool schemaSelfTestRunning;

    // 刻意包含 Nullable<T>、清單與列舉：這三者是各供應商 schema 方言差異最大處，只用純量成員會讓兩種方言產出相同 schema，測不出方言接線是否正確。
    #pragma warning disable S1144 // 透過 System.Text.Json 反序列化器賦值 init 存取子，靜態分析無法偵測，不可移除。
    private sealed class SchemaSelfTestPayload
    {
        public required string Summary { get; init; }
        public int Score { get; init; }
        public int? OptionalScore { get; init; }
        public required List<string> Tags { get; init; }
    }
    #pragma warning restore S1144

    public static float GetHeight(float width)
    {
        float statusHeight;
        if (string.IsNullOrEmpty(schemaSelfTestStatus))
        {
            statusHeight = 0f;
        }
        else if (schemaSelfTestStatus.Contains('\n', StringComparison.Ordinal))
        {
            statusHeight = 150f;
        }
        else
        {
            statusHeight = 48f;
        }
        return 690f + 42f + statusHeight;
    }

    public static void DrawDebugSettings(Listing_Standard listing)
    {
        listing.Label("RimLLM_TitleDebugExplanation".Translate());
        listing.Gap(8f);

        DrawUsageStats(listing);
        listing.Gap(10f);

        DrawDebugActions(listing);

        DrawRecentRequests(listing);
    }

    private static void DrawDebugActions(Listing_Standard listing)
    {
        bool detailedLogging = Settings.DetailedLogging;
        bool prevDetailedLogging = detailedLogging;
        listing.CheckboxLabeled("RimLLM_DetailedLogging".Translate(), ref detailedLogging, "RimLLM_DetailedLoggingExplanation".Translate());
        Settings.DetailedLogging = detailedLogging;
        RimLLMLog.Enabled = detailedLogging;
        if (prevDetailedLogging != detailedLogging)
        {
            Settings.Write();
        }
        listing.Gap(8f);

        Rect exportRect = listing.GetRect(30f);
        if (Widgets.ButtonText(exportRect, "RimLLM_ExportDiag".Translate()))
        {
            ExportDiagnostics();
        }
        listing.Gap(8f);

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
    }

    private static void DrawRecentRequests(Listing_Standard listing)
    {
        Rect headerRect = listing.GetRect(30f);
        Rect labelRect = new(headerRect.x, headerRect.y, headerRect.width - 150f, headerRect.height);
        Rect clearBtnRect = new(headerRect.x + headerRect.width - 140f, headerRect.y + 2f, 140f, headerRect.height - 4f);

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(labelRect, "<b>" + "RimLLM_RecentRequests".Translate(30) + "</b>");
        }

        if (Widgets.ButtonText(clearBtnRect, "RimLLM_ClearRequestsBtn".Translate()) &&
            RimLLMProvider.TryGetManager(out var logManager))
        {
            logManager.ClearLogs();
            Messages.Message("RimLLM_MsgLogsCleared".Translate(), MessageTypeDefOf.PositiveEvent, historical: false);
        }
        listing.Gap(4f);

        // manager 尚未初始化時不能整段不畫（面板會一片空白，像是請求沒被記錄），改退回讀持久化的遙測資料。
        bool managerAvailable = RimLLMProvider.TryGetManager(out RimLLMManager? manager);
        List<RimLLMManager.RequestLogEntry> logs = manager != null
            ? new List<RimLLMManager.RequestLogEntry>(manager.RequestLogs)
            : [];

        int persistedCount = Settings.RequestLogs != null ? Settings.RequestLogs.Count : 0;
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            listing.Label("RimLLM_RequestsSourceCounts".Translate(
                managerAvailable ? "OK" : "NULL", logs.Count, persistedCount));
        }

        // 記憶體佇列為空（核心未初始化或本 session 尚未發過請求）時退回已保存的紀錄。
        if (logs.Count == 0 && Settings.RequestLogs != null && Settings.RequestLogs.Count > 0)
        {
            logs = new List<RimLLMManager.RequestLogEntry>(Settings.RequestLogs);
            listing.Label("RimLLM_RequestsFromTelemetry".Translate());
        }

        if (logs.Count == 0)
        {
            listing.Label("RimLLM_NoRequests".Translate());
            return;
        }

        List<string> logLines = ResolveLogLines(logs);

        DrawRequestLogLines(listing, logLines);
    }

    private static void DrawRequestLogLines(Listing_Standard listing, List<string> logLines)
    {
        Rect logScrollRect = listing.GetRect(420f);
        Widgets.DrawMenuSection(logScrollRect);

        float contentWidth = logScrollRect.width - 16f;
        float logHeight = 24f;
        float viewHeight = Math.Max(420f, logLines.Count * logHeight + 10f);
        Rect viewRect = new(0f, 0f, contentWidth, viewHeight);

        // 清除歷史後內容變短，捲動位置須一併收回，否則僅存的幾列會被畫到可視範圍之外。
        float maxScrollY = Math.Max(0f, viewHeight - logScrollRect.height);
        if (debugScrollPosition.y > maxScrollY)
        {
            debugScrollPosition.y = maxScrollY;
        }

        Widgets.BeginScrollView(logScrollRect, ref debugScrollPosition, viewRect);

        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            for (int i = 0; i < logLines.Count; i++)
            {
                Rect lineRect = new(4f, i * logHeight + 4f, contentWidth - 8f, logHeight - 2f);
                Widgets.Label(lineRect, logLines[i]);
            }
        }
        Widgets.EndScrollView();
    }

    /// <summary>繪製 Token 與費用累計統計看板。</summary>
    private static void DrawUsageStats(Listing_Standard listing)
    {
        // Token 與費用累計統計看板
        listing.Label("<b>" + "RimLLM_UsageHeader".Translate() + "</b>");
        Rect usageRect = listing.GetRect(50f);
        Widgets.DrawMenuSection(usageRect);

        Rect usageInfoRect = new(usageRect.x + 8f, usageRect.y + 4f, usageRect.width - 160f, usageRect.height - 8f);
        Rect resetUsageBtnRect = new(usageRect.xMax - 150f, usageRect.y + 10f, 140f, 30f);

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            string usageText = "RimLLM_UsageInfo".Translate(
                Settings.TotalPromptTokens.ToString("N0", CultureInfo.InvariantCulture),
                Settings.TotalCompletionTokens.ToString("N0", CultureInfo.InvariantCulture),
                Settings.TotalEstimatedCost.ToString("F4", CultureInfo.InvariantCulture)
            );
            Widgets.Label(usageInfoRect, usageText);
        }

        if (RimLLMProvider.TryGetManager(out var costManager) &&
            costManager.UsageTracker.UnpricedUsageCount > 0)
        {
            listing.Label("RimLLM_UnpricedUsageNotice".Translate(costManager.UsageTracker.UnpricedUsageCount));
        }

        if (Widgets.ButtonText(resetUsageBtnRect, "RimLLM_ResetUsageBtn".Translate()) &&
            RimLLMProvider.TryGetManager(out var usageManager))
        {
            usageManager.ResetUsage();
            Messages.Message("RimLLM_MsgUsageReset".Translate(), MessageTypeDefOf.PositiveEvent, historical: false);
        }
    }

    private static readonly List<RimLLMManager.RequestLogEntry> _cachedLogSnapshot = [];
    private static readonly List<string> _cachedLogLines = [];

    /// <summary>把日誌轉成顯示列；以「同一組項目參考」判定命中，只有佇列內容真的變了才重新格式化（翻譯、Regex 遮罩與時間格式化成本高）。</summary>
    private static List<string> ResolveLogLines(List<RimLLMManager.RequestLogEntry> logs)
    {
        logs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        bool same = logs.Count == _cachedLogSnapshot.Count;
        for (int i = 0; same && i < logs.Count; i++)
        {
            same = ReferenceEquals(logs[i], _cachedLogSnapshot[i]);
        }
        if (same) return _cachedLogLines;

        _cachedLogSnapshot.Clear();
        _cachedLogSnapshot.AddRange(logs);
        _cachedLogLines.Clear();
        foreach (var log in logs)
        {
            string timeStr = log.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string statusText = log.Success
                ? $"<color=#22c55e>{"RimLLM_StatusRequestSuccess".Translate(log.LatencyMs)}</color>"
                : $"<color=#ef4444>{"RimLLM_StatusRequestFailed".Translate(RimLLMLog.SanitizeForLog(log.ErrorMessage, 160))}</color>";
            // token 拿不到（失敗、或供應商未回報用量）時省略，避免 0 誤導。
            int totalTokens = log.PromptTokens + log.CompletionTokens;
            string tokenText = totalTokens > 0 ? string.Create(CultureInfo.CurrentCulture, $" | {totalTokens:N0} tok") : string.Empty;

            _cachedLogLines.Add($"[{timeStr}] Mod: {log.ModId} | {log.Provider} ({log.Model}) | {statusText}{tokenText}");
        }
        return _cachedLogLines;
    }

    /// <summary>結構化輸出自我檢查：第一段在本機產生 schema，確認 JsonSchemaExporter 在 RimWorld 1.7 遊戲載入環境可用；第二段向目前 fallback 鏈發一次結構化請求，驗證供應商接受該 schema。</summary>
    private static void RunSchemaSelfTest()
    {
        schemaSelfTestRunning = true;
        schemaSelfTestStatus = "RimLLM_SchemaSelfTestRunning".Translate();

        RimLLMSchemaResult schema;
        try
        {
            schema = RimLLMSchemaBuilder.Build(typeof(SchemaSelfTestPayload));
        }
        catch (Exception exception)
        {
            schemaSelfTestRunning = false;
            schemaSelfTestStatus = "<color=#ef4444>" +
                "RimLLM_SchemaSelfTestBuildFailed".Translate(RimLLMLog.SanitizeForLog(exception.Message, 200)) +
                "</color>";
            return;
        }

        Log.Message("[RimLLM] Schema self-test: " + schema.Json);

        schemaSelfTestStatus = "RimLLM_SchemaSelfTestLocalOk".Translate();

        _ = Task.Run(async () =>
        {
            Func<string> applyResult;
            try
            {
                using IChatClient client = RimLLMProvider.CreateChatClient(ServiceConsumerList.SchemaSelfTestModId);
                SchemaSelfTestPayload payload = await client.GetResponseObjectAsync<SchemaSelfTestPayload>(
                    new List<ChatMessage>
                    {
                        new(
                            ChatRole.User,
                            "Reply with a short structured self-test record. " +
                            "Summary: any single sentence. Score: any integer 0-100. " +
                            "OptionalScore: may be null. Tags: two or three short words.")
                    }).ConfigureAwait(false);

                string safeSummary = RimLLMMarkdown.EscapeUntrustedUnityTags(
                    RimLLMLog.SanitizeForLog(payload?.Summary ?? string.Empty, 80) ?? string.Empty);
                string scoreText = (payload?.Score ?? 0).ToString(CultureInfo.InvariantCulture);
                applyResult = () => "<color=#22c55e>" + "RimLLM_SchemaSelfTestOk".Translate(safeSummary, scoreText) + "</color>";
            }
            catch (Exception exception)
            {
                string detail = RimLLMLog.SanitizeForLog(exception.Message, 200) ?? string.Empty;
                applyResult = () => "<color=#ef4444>" + "RimLLM_SchemaSelfTestRemoteFailed".Translate(detail) + "</color>";
            }

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                schemaSelfTestStatus = applyResult();
                schemaSelfTestRunning = false;
            });
        });
    }

    private static void ExportDiagnostics()
    {
        try
        {
            StringBuilder sb = new();
            sb.AppendLine("=== RimLLM Framework Diagnostics ===");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Export Time: {DateTime.Now}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"OS: {SystemInfo.operatingSystem}");
            string maskedDeviceId;
            try
            {
                maskedDeviceId = MaskDeviceId(Environment.MachineName);
            }
            catch
            {
                maskedDeviceId = "Unknown";
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"Device ID: {maskedDeviceId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Max Concurrent Requests Setting: {Settings.MaxConcurrentRequests}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Timeout Setting: {Settings.ApiTimeout}s");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Max Retries Setting: {Settings.MaxRetries}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Retry Delay Setting: {Settings.RetryDelay}s");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Detailed Logging: {Settings.DetailedLogging}");
            sb.AppendLine();

            sb.AppendLine("=== Fallback Chain ===");
            var fallbackChain = Settings.FallbackChain;
            for (int i = 0; i < fallbackChain.Count; i++)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {i + 1}. {fallbackChain[i]}");
            }
            sb.AppendLine();

            AppendProviderDiagnostics(sb);
            AppendRequestLogsDiagnostics(sb);

            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimLLM_Diagnostic.txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Messages.Message("RimLLM_ExportDiagSuccess".Translate(path), MessageTypeDefOf.PositiveEvent, historical: false);
        }
        catch (Exception ex)
        {
            Messages.Message("RimLLM_ExportDiagFailed".Translate(RimLLMLog.SanitizeForLog(ex.Message, 200)), MessageTypeDefOf.RejectInput, historical: false);
        }
    }

    private static void AppendProviderDiagnostics(StringBuilder sb)
    {
        sb.AppendLine("=== Provider Setup ===");
        List<string> providers = RimLLMProvider.TryGetManager(out var providerManager)
            ? providerManager.GetRegisteredProviderIds()
            : new List<string>(ProviderIds.BuiltIn);
        foreach (var prov in providers)
        {
            bool enabled = Settings.IsProviderEnabled(prov);
            bool hasKey = !string.IsNullOrEmpty(Settings.GetApiKey(prov));
            string? endpoint = Settings.GetEndpoint(prov, "default");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {prov}: Enabled={enabled}, HasKey={hasKey}, Endpoint={MaskEndpoint(endpoint)}");
            var models = Settings.GetModelList(prov);
            string modelPreview = models.Count > 20
                ? string.Join(", ", models.GetRange(0, 20)) + string.Create(CultureInfo.CurrentCulture, $", ... ({models.Count - 20} more)")
                : string.Join(", ", models);
            sb.AppendLine(CultureInfo.InvariantCulture, $"    Cached Models ({models.Count}): {RimLLMLog.SanitizeForLog(modelPreview, 1000)}");
        }
        sb.AppendLine();
    }

    private static void AppendRequestLogsDiagnostics(StringBuilder sb)
    {
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
                    int totalTokens = log.PromptTokens + log.CompletionTokens;
                    string tokenInfo = totalTokens > 0 ? string.Create(CultureInfo.CurrentCulture, $" | Tokens: {totalTokens:N0} (prompt {log.PromptTokens:N0}, completion {log.CompletionTokens:N0})") : "";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  [{log.Timestamp:yyyy-MM-dd HH:mm:ss}] Mod: {log.ModId} | Provider: {log.Provider} ({log.Model}) | {status} | Latency: {log.LatencyMs}ms{tokenInfo}");
                }
            }
        }
    }

    private static string MaskDeviceId(string rawId)
    {
        if (string.IsNullOrEmpty(rawId) || rawId.Length <= 8) return rawId;
        return string.Concat(rawId.AsSpan(0, 4), "****", rawId.AsSpan(rawId.Length - 4));
    }

    private static string? MaskEndpoint(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return endpoint;
        try
        {
            var uri = new Uri(endpoint);
            string port = uri.IsDefaultPort ? "" : string.Create(CultureInfo.CurrentCulture, $":{uri.Port}");
            return $"{uri.Scheme}://{uri.Host}{port}/...";
        }
        catch
        {
            return RimLLMLog.SanitizeForLog(endpoint, 120);
        }
    }
}
