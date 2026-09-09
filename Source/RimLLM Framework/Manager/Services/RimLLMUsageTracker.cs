using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Mod;
using RimWorld;
using Verse;
#pragma warning disable S108, S1104, S2325, S3267, S3887, S2696 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀；S2696 靜態節流跨實例共享為設計意圖

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
    /// <summary>
    /// 管理並統計 API 呼叫量、Token 使用度、連線日誌記錄、API 計費預估與每日預算審查。
    /// 支援對設定檔的磁碟存檔寫入實施節流（防震）保護。
    /// </summary>
    public class RimLLMUsageTracker
    {
        private readonly IRimLLMSettings _settings;
        private static DateTime _lastLogWriteTime = DateTime.MinValue;
        private static readonly object LogLock = new object();
        private static readonly object UsageLock = new object();

        public const int ModelLevelHigh = 3;
        public const int ModelLevelMedium = 2;
        public const int ModelLevelLow = 1;

        private const float HighTierCompletionCostThreshold = 3.00f;
        private const float MediumTierCompletionCostThreshold = 0.50f;

        /// <summary>
        /// 依據模型的 API 費率（每百萬 Token 輸出費率）判定其等級：
        /// Level 3 (High): 輸出費率 >= $3.00 / 1M Tokens (如 GPT-4o, Gemini Pro, Grok, Qwen-Max)
        /// Level 2 (Medium): 輸出費率 >= $0.50 / 1M Tokens (如 Gemini Flash, DeepSeek-V4-Pro, Qwen-Plus)
        /// Level 1 (Low): 輸出費率 < $0.50 / 1M Tokens 或本地免費模型 ($0)
        /// </summary>
        public int GetModelLevel(string providerId, string modelName)
        {
            string normProvider = NormalizeProvider(providerId);
            if (normProvider == "openaicompatible" ||
                normProvider == "openai-compatible" ||
                normProvider == "local" ||
                string.IsNullOrEmpty(modelName))
            {
                return ModelLevelLow;
            }

            if (FindModelRate(providerId, modelName, out CostRate rate))
            {
                if (rate.CompletionPerMillion >= HighTierCompletionCostThreshold) return ModelLevelHigh;
                if (rate.CompletionPerMillion >= MediumTierCompletionCostThreshold) return ModelLevelMedium;
                return ModelLevelLow;
            }

            return ModelLevelMedium; // 未知雲端模型預設給予 Medium 評級
        }

        private static readonly Dictionary<string, CostRate> KnownModelRates = new Dictionary<string, CostRate>(StringComparer.OrdinalIgnoreCase)
        {
            // OpenAI
            { "openai:gpt-4o", new CostRate(2.50f, 10.00f) },
            { "openai:gpt-4o-mini", new CostRate(0.15f, 0.60f) },
            { "openai:o1", new CostRate(15.00f, 60.00f) },
            { "openai:o1-mini", new CostRate(1.10f, 4.40f) },
            { "openai:o3-mini", new CostRate(1.10f, 4.40f) },
            { "openai:gpt-4-turbo", new CostRate(10.00f, 30.00f) },
            { "openai:gpt-4", new CostRate(30.00f, 60.00f) },
            { "openai:gpt-3.5-turbo", new CostRate(0.50f, 1.50f) },

            // Google Gemini
            { "gemini:gemini-2.0-flash", new CostRate(0.10f, 0.40f) },
            { "gemini:gemini-2.0-flash-lite", new CostRate(0.075f, 0.30f) },
            { "gemini:gemini-1.5-flash", new CostRate(0.075f, 0.30f) },
            { "gemini:gemini-1.5-pro", new CostRate(1.25f, 5.00f) },
            { "gemini:gemini-2.5-pro", new CostRate(1.25f, 10.00f) },
            { "gemini:gemini-2.5-flash", new CostRate(0.30f, 2.50f) },
            { "gemini:gemini-2.5-flash-lite", new CostRate(0.10f, 0.40f) },
            { "gemini:gemini-3.1-pro-preview", new CostRate(2.00f, 12.00f) },
            { "gemini:gemini-3.1-flash-lite", new CostRate(0.25f, 1.50f) },
            { "gemini:gemini-3.5-flash", new CostRate(1.50f, 9.00f) },

            // DeepSeek
            { "deepseek:deepseek-chat", new CostRate(0.14f, 0.28f) },
            { "deepseek:deepseek-reasoner", new CostRate(0.14f, 0.28f) },
            { "deepseek:deepseek-v3", new CostRate(0.14f, 0.28f) },
            { "deepseek:deepseek-r1", new CostRate(0.14f, 0.28f) },
            { "deepseek:deepseek-v4-flash", new CostRate(0.14f, 0.28f) },
            { "deepseek:deepseek-v4-pro", new CostRate(0.435f, 0.87f) },

            // Groq
            { "groq:llama-3.3-70b-versatile", new CostRate(0.59f, 0.79f) },
            { "groq:llama-3.1-8b-instant", new CostRate(0.05f, 0.08f) },
            { "groq:mixtral-8x7b-32768", new CostRate(0.24f, 0.24f) },
            { "groq:gemma2-9b-it", new CostRate(0.20f, 0.20f) },

            // Qwen
            { "qwen:qwen-max", new CostRate(2.80f, 8.40f) },
            { "qwen:qwen-plus", new CostRate(0.40f, 1.20f) },
            { "qwen:qwen-turbo", new CostRate(0.10f, 0.20f) },

            // Moonshot / Kimi
            { "kimi:moonshot-v1-8k", new CostRate(1.68f, 1.68f) },
            { "kimi:moonshot-v1-32k", new CostRate(3.36f, 3.36f) },
            { "kimi:moonshot-v1-128k", new CostRate(8.40f, 8.40f) },

            // MiniMax
            { "minimax:minimax-m3", new CostRate(0.30f, 1.20f) },
            { "minimax:abab6.5s", new CostRate(0.14f, 0.28f) },

            // Grok (xAI)
            { "grok:grok-2", new CostRate(2.00f, 10.00f) },
            { "grok:grok-beta", new CostRate(5.00f, 15.00f) }
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
                    CountOutcome(log.Provider, log.Success);
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

            CountOutcome(provider, success);

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
        /// 將一次請求結果計入該供應商的成功／失敗計數。
        /// </summary>
        private void CountOutcome(string provider, bool success)
        {
            var stats = ProviderStatistics.GetOrAdd(provider, _ => new ProviderStats());
            if (success)
            {
                System.Threading.Interlocked.Increment(ref stats.SuccessCount);
            }
            else
            {
                System.Threading.Interlocked.Increment(ref stats.FailureCount);
            }
        }

        /// <summary>
        /// 清空所有快取的請求日誌，並儲存設定。
        /// </summary>
        public void ClearLogs()
        {
            while (RequestLogs.TryDequeue(out _)) { }
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

        /// <summary>
        /// 估算單次呼叫的美元成本。<paramref name="cachedPromptTokens"/> 由呼叫端保證已落在 [0, promptTokens] 範圍內。
        /// </summary>
        public float EstimateCost(string providerId, string modelName, int promptTokens, int completionTokens, int cachedPromptTokens)
        {
            if (!FindModelRate(providerId, modelName, out CostRate rate))
            {
                return 0f;
            }

            int fullRatePromptTokens = promptTokens - cachedPromptTokens;
            float cacheDiscount = GetCacheReadDiscount(providerId);

            float promptCost = (fullRatePromptTokens / 1000000f) * rate.PromptPerMillion
                               + (cachedPromptTokens / 1000000f) * rate.PromptPerMillion * cacheDiscount;
            float completionCost = (completionTokens / 1000000f) * rate.CompletionPerMillion;
            return promptCost + completionCost;
        }

        private bool FindModelRate(string providerId, string modelName, out CostRate rate)
        {
            string normProvider = NormalizeProvider(providerId);
            string normModel = NormalizeModel(modelName);
            string exactKey = $"{normProvider}:{normModel}";

            if (KnownModelRates.TryGetValue(exactKey, out rate))
            {
                return true;
            }

            // 前綴模糊匹配（例如 gpt-4o-2024-08-06 匹配 gpt-4o, gemini-2.0-flash-001 匹配 gemini-2.0-flash）
            string bestPrefixMatchKey = null;
            int bestPrefixLen = 0;

            string providerPrefix = normProvider + ":";
            foreach (var kvp in KnownModelRates)
            {
                if (kvp.Key.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string candidateModel = kvp.Key.Substring(providerPrefix.Length);
                    if (normModel.StartsWith(candidateModel, StringComparison.OrdinalIgnoreCase) &&
                        candidateModel.Length > bestPrefixLen)
                    {
                        bestPrefixLen = candidateModel.Length;
                        bestPrefixMatchKey = kvp.Key;
                    }
                }
            }

            if (bestPrefixMatchKey != null && KnownModelRates.TryGetValue(bestPrefixMatchKey, out rate))
            {
                return true;
            }

            rate = default;
            return false;
        }

        /// <summary>
        /// 快取命中（cache read / cachedContent）Token 相對於一般輸入 Token 的計費折扣倍率。
        /// </summary>
        private static float GetCacheReadDiscount(string providerId)
        {
            switch (NormalizeProvider(providerId))
            {
                case "gemini": return 0.25f;     // Gemini cachedContent 約為輸入價的 0.25x
                case "deepseek": return 0.02f;
                default: return 0.25f;
            }
        }

        private static string NormalizeProvider(string providerId)
        {
            return (providerId ?? "").Trim().ToLowerInvariant();
        }

        private static string NormalizeModel(string modelName)
        {
            string model = (modelName ?? "").Trim().ToLowerInvariant();
            if (model.StartsWith("models/"))
            {
                model = model.Substring("models/".Length);
            }
            return model;
        }

        #region Budget Ledger & Policy Gatekeeping

        /// <summary>
        /// 審查每日預算限額。
        /// </summary>
        internal bool CheckBudgetLimit()
        {
            CheckDailyReset();

            if (_settings.DailyBudgetLimit <= 0f || _settings.DailyAccumulatedCost < _settings.DailyBudgetLimit)
            {
                return true;
            }

            // 0=HardBlock, 1=SilentMocking, 2=FallbackToFree
            return _settings.BudgetPolicy == 1 || _settings.BudgetPolicy == 2;
        }

        /// <summary>
        /// 判斷請求是否處於靜默模擬模式並產出模擬字串。
        /// </summary>
        internal bool IsBudgetMocked(bool hasResponseType, out string mockResult)
        {
            mockResult = null;

            if (_settings.BudgetPolicy != 1 ||
                _settings.DailyBudgetLimit <= 0f ||
                _settings.DailyAccumulatedCost < _settings.DailyBudgetLimit)
            {
                return false;
            }

            if (hasResponseType)
            {
                mockResult = "{}";
                return true;
            }

            const string fallbackMock = "*AI is temporarily resting due to daily budget limits...*";
            try
            {
                mockResult = LanguageDatabase.activeLanguage != null
                    ? "RimLLM_SilentMockResponse".Translate().ToString()
                    : fallbackMock;
            }
            catch
            {
                mockResult = fallbackMock;
            }
            return true;
        }

        #endregion
    }
#pragma warning restore S101, S2342
#pragma warning restore S108, S1104, S2325, S3267, S3887, S2696
}