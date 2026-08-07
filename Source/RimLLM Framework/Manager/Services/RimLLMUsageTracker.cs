using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Verse;
using RimLLM_Framework.Core;
using RimLLM_Framework.Mod;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// 管理並統計 API 呼叫量、Token 使用度、連線日誌記錄以及 API 計費預估。
    /// 支援對設定檔的磁碟存檔寫入實施節流（防震）保護。
    /// </summary>
    public class RimLLMUsageTracker
    {
        private readonly IRimLLMSettings _settings;
        private static DateTime _lastLogWriteTime = DateTime.MinValue;
        private static readonly object LogLock = new object();
        private static readonly object UsageLock = new object();
        private static readonly Dictionary<string, CostRate> KnownModelRates = new Dictionary<string, CostRate>(StringComparer.OrdinalIgnoreCase)
        {
            { "deepseek:deepseek-v4-flash", new CostRate(0.14f, 0.28f) },
            { "deepseek:deepseek-v4-pro", new CostRate(0.435f, 0.87f) },
            { "deepseek:deepseek-chat", new CostRate(0.14f, 0.28f) },
            { "deepseek:deepseek-reasoner", new CostRate(0.14f, 0.28f) },
            { "gemini:gemini-3.1-pro-preview", new CostRate(2.00f, 12.00f) },
            { "gemini:gemini-3.1-flash-lite", new CostRate(0.25f, 1.50f) },
            { "gemini:gemini-3.5-flash", new CostRate(1.50f, 9.00f) },
            { "gemini:gemini-2.5-pro", new CostRate(1.25f, 10.00f) },
            { "gemini:gemini-2.5-flash", new CostRate(0.30f, 2.50f) },
            { "gemini:gemini-2.5-flash-lite", new CostRate(0.10f, 0.40f) },
            { "groq:llama-3.3-70b-versatile", new CostRate(0.59f, 0.79f) },
            { "minimax:minimax-m3", new CostRate(0.30f, 1.20f) }
        };

        private struct CostRate
        {
            public readonly float PromptPerMillion;
            public readonly float CompletionPerMillion;

            public CostRate(float promptPerMillion, float completionPerMillion)
            {
                PromptPerMillion = promptPerMillion;
                CompletionPerMillion = completionPerMillion;
            }
        }

        /// <summary>
        /// 存放最近 API 呼叫歷史的執行緒安全佇列。
        /// </summary>
        public readonly ConcurrentQueue<RimLLMManager.RequestLogEntry> RequestLogs = 
            new ConcurrentQueue<RimLLMManager.RequestLogEntry>();

        public class ProviderStats
        {
            public int SuccessCount;
            public int FailureCount;
            public int TotalCount => SuccessCount + FailureCount;
            public float SuccessRate => TotalCount > 0 ? (float)SuccessCount / TotalCount : 1f;

            // API-side Context Caching Stats
            public long TotalPromptTokens;
            public long CachedPromptTokens;
            public float ContextCacheHitRate => TotalPromptTokens > 0 ? (float)CachedPromptTokens / TotalPromptTokens : 0f;
        }

        public readonly ConcurrentDictionary<string, ProviderStats> ProviderStatistics = 
            new ConcurrentDictionary<string, ProviderStats>(StringComparer.OrdinalIgnoreCase);

        public RimLLMUsageTracker(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            
            if (_settings is RimLLMFrameworkSettings frameworkSettings && frameworkSettings.RequestLogs != null)
            {
                foreach (var log in frameworkSettings.RequestLogs)
                {
                    RequestLogs.Enqueue(log);
                    var stats = ProviderStatistics.GetOrAdd(log.Provider, _ => new ProviderStats());
                    if (log.Success)
                    {
                        stats.SuccessCount++;
                    }
                    else
                    {
                        stats.FailureCount++;
                    }
                }
            }
        }

        /// <summary>
        /// 記錄一次請求的日誌與結果，並在背景以節流機制寫入 XML 設定檔中。
        /// </summary>
        public void RecordLog(DateTime startTime, string modId, string provider, string model, bool success, string err, long latency)
        {
            var entry = new RimLLMManager.RequestLogEntry
            {
                Timestamp = startTime,
                ModId = modId,
                Provider = provider,
                Model = model,
                Success = success,
                ErrorMessage = RimLLMLog.SanitizeForLog(err, 300),
                LatencyMs = latency
            };

            RequestLogs.Enqueue(entry);
            while (RequestLogs.Count > 30)
            {
                RequestLogs.TryDequeue(out _);
            }

            var providerStats = ProviderStatistics.GetOrAdd(provider, _ => new ProviderStats());
            if (success)
            {
                System.Threading.Interlocked.Increment(ref providerStats.SuccessCount);
            }
            else
            {
                System.Threading.Interlocked.Increment(ref providerStats.FailureCount);
            }

            if (_settings is RimLLMFrameworkSettings frameworkSettings)
            {
                RimLLMDispatcher.EnqueueOnMainThread(() =>
                {
                    lock (LogLock)
                    {
                        frameworkSettings.RequestLogs = new List<RimLLMManager.RequestLogEntry>(RequestLogs.ToArray());
                        // 節流：非成功或過了 15 秒以上才執行實體寫入（僅寫遙測 JSON，不動設定 XML）
                        if (!success || (DateTime.UtcNow - _lastLogWriteTime).TotalSeconds > 15)
                        {
                            try
                            {
                                frameworkSettings.SaveTelemetry();
                                _lastLogWriteTime = DateTime.UtcNow;
                            }
                            catch (Exception ex)
                            {
                                RimLLMLog.Warning($"[RimLLM] Throttled telemetry write failed: {ex.Message}");
                            }
                        }
                        else
                        {
                            // 被節流跳過的變更需標記為待寫入，關閉遊戲時才會強制 flush，
                            // 否則 session 最後一段用量永遠寫不進去。
                            frameworkSettings.MarkTelemetryDirty();
                        }
                    }
                });
            }
        }

        /// <summary>
        /// 清空所有快取的請求日誌，並儲存設定。
        /// </summary>
        public void ClearLogs()
        {
            while (RequestLogs.Count > 0)
            {
                RequestLogs.TryDequeue(out _);
            }
            ProviderStatistics.Clear();

            if (_settings is RimLLMFrameworkSettings frameworkSettings)
            {
                lock (LogLock)
                {
                    frameworkSettings.RequestLogs = new List<RimLLMManager.RequestLogEntry>();
                    try
                    {
                        frameworkSettings.SaveTelemetry();
                        _lastLogWriteTime = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        RimLLMLog.Warning($"[RimLLM] Clear logs telemetry write failed: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 檢查並執行跨天重置日預算累計。
        /// </summary>
        public void CheckDailyReset()
        {
            string todayStr = DateTime.Today.ToString("yyyy-MM-dd");
            lock (UsageLock)
            {
                if (string.IsNullOrEmpty(_settings.DailyBudgetResetDate) || _settings.DailyBudgetResetDate != todayStr)
                {
                    _settings.DailyAccumulatedCost = 0f;
                    _settings.DailyBudgetResetDate = todayStr;
                }
            }
        }

        /// <summary>
        /// 累加 Token 統計值，並估計該次 API 消耗的美元成本。
        /// </summary>
        /// <param name="promptTokens">本次請求的「輸入 Token 總量」，須包含被快取命中的部分，以反映真實用量。</param>
        /// <param name="cachedPromptTokens">
        /// 輸入 Token 中由上下文快取（cache read / cachedContent）命中的部分；
        /// 這些 Token 以折扣費率計價，是 Context Caching 節省成本的來源。
        /// </param>
        public void RecordUsage(string providerId, string modelName, int promptTokens, int completionTokens, int cachedPromptTokens = 0)
        {
            if (promptTokens <= 0 && completionTokens <= 0) return;

            if (cachedPromptTokens < 0) cachedPromptTokens = 0;
            if (cachedPromptTokens > promptTokens) cachedPromptTokens = promptTokens;

            CheckDailyReset();

            lock (UsageLock)
            {
                _settings.TotalPromptTokens += promptTokens;
                _settings.TotalCompletionTokens += completionTokens;

                float cost = EstimateCost(providerId, modelName, promptTokens, completionTokens, cachedPromptTokens);
                _settings.TotalEstimatedCost += cost;
                _settings.DailyAccumulatedCost += cost;
            }

            // 累加特定供應商的 prompt tokens 與 API 快取 tokens 用量
            var stats = ProviderStatistics.GetOrAdd(providerId, _ => new ProviderStats());
            lock (stats)
            {
                stats.TotalPromptTokens += promptTokens;
                stats.CachedPromptTokens += cachedPromptTokens;
            }
        }

        /// <summary>
        /// 重設所有的 Token 與費用計量器。
        /// </summary>
        public void ResetUsage()
        {
            lock (UsageLock)
            {
                _settings.TotalPromptTokens = 0;
                _settings.TotalCompletionTokens = 0;
                _settings.TotalEstimatedCost = 0f;

                foreach (var kvp in ProviderStatistics)
                {
                    lock (kvp.Value)
                    {
                        kvp.Value.TotalPromptTokens = 0;
                        kvp.Value.CachedPromptTokens = 0;
                    }
                }

                try
                {
                    if (_settings is RimLLMFrameworkSettings frameworkSettings)
                    {
                        frameworkSettings.SaveTelemetry();
                    }
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] Reset usage telemetry write failed: {ex.Message}");
                }
            }
        }

        private float EstimateCost(string providerId, string modelName, int promptTokens, int completionTokens, int cachedPromptTokens = 0)
        {
            string key = $"{NormalizeProvider(providerId)}:{NormalizeModel(modelName)}";
            if (!KnownModelRates.TryGetValue(key, out var rate))
            {
                return 0f;
            }

            if (cachedPromptTokens < 0) cachedPromptTokens = 0;
            if (cachedPromptTokens > promptTokens) cachedPromptTokens = promptTokens;

            // 快取命中的 Token 以折扣費率計價，其餘輸入 Token 走原價，藉此讓成本面板反映 Context Caching 的節省。
            int fullRatePromptTokens = promptTokens - cachedPromptTokens;
            float cacheDiscount = GetCacheReadDiscount(providerId);

            float promptCost = (fullRatePromptTokens / 1000000f) * rate.PromptPerMillion
                               + (cachedPromptTokens / 1000000f) * rate.PromptPerMillion * cacheDiscount;
            float completionCost = (completionTokens / 1000000f) * rate.CompletionPerMillion;
            return promptCost + completionCost;
        }

        /// <summary>
        /// 快取命中（cache read / cachedContent）Token 相對於一般輸入 Token 的計費折扣倍率。
        /// </summary>
        private static float GetCacheReadDiscount(string providerId)
        {
            string provider = (providerId ?? "").Trim().ToLowerInvariant();
            switch (provider)
            {
                case "anthropic": return 0.1f;  // Anthropic cache read 約為輸入價의 0.1x
                case "gemini": return 0.25f;     // Gemini cachedContent 約為輸入價의 0.25x
                case "deepseek": return 0.02f;
                default: return 0.25f;
            }
        }

        private string NormalizeProvider(string providerId)
        {
            return (providerId ?? "").Trim().ToLowerInvariant();
        }

        private string NormalizeModel(string modelName)
        {
            string model = (modelName ?? "").Trim().ToLowerInvariant();
            if (model.StartsWith("models/"))
            {
                model = model.Substring("models/".Length);
            }
            return model;
        }
    }
}
