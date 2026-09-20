using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
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

        /// <summary>日誌保留上限。與 Trim 邏輯共用，避免魔法數字散落。</summary>
        internal const int MaxRetainedLogs = 30;

        /// <summary>
        /// 日誌佇列的近似計數。ConcurrentQueue.Count 在舊 Mono 上是加鎖列舉（O(n)），
        /// 每請求呼叫一次會隨日誌量線性變貴；此計數器以 Interlocked 維護，Trim 只看它。
        /// 多執行緒下為近似值（最終一致），僅影響保留條數上下一兩條，不影響正確性。
        /// </summary>
        private int _logCount;

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
        /// <summary>
        /// 模型分級快取：費率表為靜態唯讀，同一 provider:model 的分級永不變化。
        /// 鍵保留原始大小寫，以 OrdinalIgnoreCase 比對，省下每次的 ToLower 配置。
        /// 使用者覆寫走 <see cref="IRimLLMSettings.GetModelLevelOverride"/> 即時查詢，不進快取，
        /// 因此設定頁調整分級立即生效。
        /// </summary>
        private static readonly ConcurrentDictionary<string, int> ModelLevelCache =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int GetModelLevel(string providerId, string modelName)
        {
            if (string.IsNullOrEmpty(modelName) ||
                string.Equals(providerId, "openaicompatible", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(providerId, "openai-compatible", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(providerId, "local", StringComparison.OrdinalIgnoreCase))
            {
                return ModelLevelLow;
            }

            string cacheKey = BuildModelLevelCacheKey(providerId, modelName);
            if (ModelLevelCache.TryGetValue(cacheKey, out int cached))
            {
                return cached;
            }

            int level;
            if (FindModelRate(providerId, modelName, out CostRate rate))
            {
                if (rate.CompletionPerMillion >= HighTierCompletionCostThreshold) level = ModelLevelHigh;
                else if (rate.CompletionPerMillion >= MediumTierCompletionCostThreshold) level = ModelLevelMedium;
                else level = ModelLevelLow;
            }
            else
            {
                level = ModelLevelMedium; // 未知雲端模型預設給予 Medium 評級
            }

            ModelLevelCache[cacheKey] = level;
            return level;
        }

        /// <summary>組分級快取鍵：僅剝除 models/ 前綴，不做大小寫正規化（比對器負責）。</summary>
        private static string BuildModelLevelCacheKey(string providerId, string modelName)
        {
            string model = StripModelsPrefix(modelName?.Trim());
            return (providerId?.Trim() ?? "") + ":" + model;
        }

        private static string StripModelsPrefix(string model)
        {
            if (!string.IsNullOrEmpty(model) && model.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            {
                return model.Substring("models/".Length);
            }
            return model ?? "";
        }

        private static readonly Dictionary<string, CostRate> KnownModelRates = new Dictionary<string, CostRate>(StringComparer.OrdinalIgnoreCase)
        {
            // OpenAI
            { "openai:gpt-5", new CostRate(1.25f, 10.00f) },
            { "openai:gpt-5-mini", new CostRate(0.25f, 2.00f) },
            { "openai:gpt-5-nano", new CostRate(0.05f, 0.40f) },
            { "openai:gpt-5.1", new CostRate(1.25f, 10.00f) },
            { "openai:gpt-4.1", new CostRate(2.00f, 8.00f) },
            { "openai:gpt-4.1-mini", new CostRate(0.40f, 1.60f) },
            { "openai:gpt-4.1-nano", new CostRate(0.10f, 0.40f) },
            { "openai:gpt-4o", new CostRate(2.50f, 10.00f) },
            { "openai:gpt-4o-mini", new CostRate(0.15f, 0.60f) },
            { "openai:o1", new CostRate(15.00f, 60.00f) },
            { "openai:o1-mini", new CostRate(1.10f, 4.40f) },
            { "openai:o3", new CostRate(2.00f, 8.00f) },
            { "openai:o3-mini", new CostRate(1.10f, 4.40f) },
            { "openai:o3-pro", new CostRate(20.00f, 80.00f) },
            { "openai:o4-mini", new CostRate(1.10f, 4.40f) },
            { "openai:gpt-4-turbo", new CostRate(10.00f, 30.00f) },
            { "openai:gpt-4", new CostRate(30.00f, 60.00f) },
            { "openai:gpt-3.5-turbo", new CostRate(0.50f, 1.50f) },
            { "openai:text-embedding-3-small", new CostRate(0.02f, 0f) },
            { "openai:text-embedding-3-large", new CostRate(0.13f, 0f) },
            { "openai:text-embedding-ada-002", new CostRate(0.10f, 0f) },

            // Google Gemini
            { "gemini:gemini-embedding-001", new CostRate(0.15f, 0f) },
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

            // Groq（模型 ID 含供應商前綴時整段入鍵）
            { "groq:llama-3.3-70b-versatile", new CostRate(0.59f, 0.79f) },
            { "groq:llama-3.1-8b-instant", new CostRate(0.05f, 0.08f) },
            { "groq:mixtral-8x7b-32768", new CostRate(0.24f, 0.24f) },
            { "groq:gemma2-9b-it", new CostRate(0.20f, 0.20f) },
            { "groq:openai/gpt-oss-120b", new CostRate(0.15f, 0.75f) },
            { "groq:openai/gpt-oss-20b", new CostRate(0.10f, 0.50f) },
            { "groq:qwen/qwen3-32b", new CostRate(0.29f, 0.59f) },
            { "groq:moonshotai/kimi-k2-instruct", new CostRate(1.00f, 3.00f) },

            // Qwen
            { "qwen:qwen-max", new CostRate(2.80f, 8.40f) },
            { "qwen:qwen-plus", new CostRate(0.40f, 1.20f) },
            { "qwen:qwen-turbo", new CostRate(0.10f, 0.20f) },

            // Moonshot / Kimi
            { "kimi:kimi-k2", new CostRate(0.60f, 2.50f) },
            { "kimi:moonshot-v1-8k", new CostRate(1.68f, 1.68f) },
            { "kimi:moonshot-v1-32k", new CostRate(3.36f, 3.36f) },
            { "kimi:moonshot-v1-128k", new CostRate(8.40f, 8.40f) },

            // MiniMax
            { "minimax:minimax-m3", new CostRate(0.30f, 1.20f) },
            { "minimax:minimax-m2", new CostRate(0.30f, 1.20f) },
            { "minimax:abab6.5s", new CostRate(0.14f, 0.28f) },

            // Z.ai (GLM)
            { "z.ai:glm-4.6", new CostRate(0.60f, 2.20f) },
            { "z.ai:glm-4.5", new CostRate(0.60f, 2.20f) },
            { "z.ai:glm-4.5-air", new CostRate(0.20f, 1.10f) },
            { "z.ai:glm-4.5-flash", new CostRate(0f, 0f) },

            // Grok (xAI)
            { "grok:grok-4", new CostRate(3.00f, 15.00f) },
            { "grok:grok-4-fast", new CostRate(0.20f, 0.50f) },
            { "grok:grok-3", new CostRate(3.00f, 15.00f) },
            { "grok:grok-3-mini", new CostRate(0.30f, 0.50f) },
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
            
            if (_settings.RequestLogs != null)
            {
                foreach (var log in _settings.RequestLogs)
                {
                    RequestLogs.Enqueue(log);
                    Interlocked.Increment(ref _logCount);
                    CountOutcome(log.Provider, log.Success);
                }
                TrimLogs();
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
            Interlocked.Increment(ref _logCount);
            TrimLogs();

            CountOutcome(provider, success);

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                bool needSave;
                lock (LogLock)
                {
                    _settings.RequestLogs = new List<RimLLMManager.RequestLogEntry>(RequestLogs.ToArray());
                    // 節流：非成功或過了 15 秒以上才執行實體寫入（僅寫遙測 JSON，不動設定 XML）
                    if (!success || (DateTime.UtcNow - _lastLogWriteTime).TotalSeconds > 15)
                    {
                        _lastLogWriteTime = DateTime.UtcNow;
                        // 寫檔在背景非同步進行：先標記待寫入，讓關閉時的 FlushTelemetryIfDirty
                        // 能補上尚未完成或失敗的背景寫入；Save 成功會自行清除該標記。
                        _settings.MarkTelemetryDirty();
                        needSave = true;
                    }
                    else
                    {
                        // 被節流跳過的變更需標記為待寫入，關閉遊戲時才會強制 flush，
                        // 否則 session 最後一段用量永遠寫不進去。
                        _settings.MarkTelemetryDirty();
                        needSave = false;
                    }
                }

                // AES 加密 + JSON 序列化 + 磁碟寫入改由背景單寫者執行，
                // 不再佔用主線程派遣器的 2ms 幀預算。記憶體內的 RequestLogs 更新仍在主線程，
                // 與既有 Scribe/設定寫入互斥語意一致。
                if (needSave)
                {
                    QueueTelemetrySave(_settings);
                }
            });
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

        private static readonly object SaveQueueLock = new object();
        private static bool _telemetrySaveRunning;
        private static bool _telemetrySaveRequested;

        /// <summary>
        /// 把遙測寫檔排入背景單寫者佇列。同一時間最多一個寫檔在跑，
        /// 執行中若又有新請求，只合併為一次追寫（存檔讀的是執行當下的最新狀態，不會遺失）。
        /// 寫檔本身（Save）為例外安全且以暫存檔原子替換，失敗會保留 IsDirty 由下次 flush 重試。
        /// 背景寫檔持有 <see cref="LogLock"/>，與主線程的記憶體更新、ClearLogs 互斥，
        /// 排序與過去「主線程內寫檔」一致；關閉時的 FlushTelemetryIfDirty 仍同步執行。
        /// </summary>
        private static void QueueTelemetrySave(IRimLLMSettings settings)
        {
            lock (SaveQueueLock)
            {
                if (_telemetrySaveRunning)
                {
                    _telemetrySaveRequested = true;
                    return;
                }
                _telemetrySaveRunning = true;
            }

            Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        try
                        {
                            lock (LogLock)
                            {
                                settings.SaveTelemetry();
                            }
                        }
                        catch (Exception ex)
                        {
                            RimLLMLog.Warning($"[RimLLM] Background telemetry write failed: {ex.Message}");
                        }

                        lock (SaveQueueLock)
                        {
                            if (!_telemetrySaveRequested)
                            {
                                _telemetrySaveRunning = false;
                                return;
                            }
                            _telemetrySaveRequested = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (SaveQueueLock)
                    {
                        _telemetrySaveRunning = false;
                        _telemetrySaveRequested = false;
                    }
                    RimLLMLog.Warning($"[RimLLM] Background telemetry writer failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 以計數器為準修剪多餘日誌，避免 ConcurrentQueue.Count 的加鎖列舉。
        /// </summary>
        private void TrimLogs()
        {
            while (Volatile.Read(ref _logCount) > MaxRetainedLogs)
            {
                if (!RequestLogs.TryDequeue(out _))
                {
                    break;
                }
                Interlocked.Decrement(ref _logCount);
            }
        }

        /// <summary>
        /// 清空所有快取的請求日誌，並儲存設定。
        /// </summary>
        public void ClearLogs()
        {
            while (RequestLogs.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _logCount, 0);
            ProviderStatistics.Clear();

            lock (LogLock)
            {
                _settings.RequestLogs = new List<RimLLMManager.RequestLogEntry>();
                try
                {
                    _settings.SaveTelemetry();
                    _lastLogWriteTime = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] Clear logs telemetry write failed: {ex.Message}");
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
                    _settings.SaveTelemetry();
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
            // 字典本身為 OrdinalIgnoreCase，直接以未轉小寫的鍵查詢，省下每次的 ToLower 配置。
            string normModel = StripModelsPrefix(modelName?.Trim());
            string exactKey = (providerId?.Trim() ?? "") + ":" + normModel;

            if (KnownModelRates.TryGetValue(exactKey, out rate))
            {
                return true;
            }

            // 前綴模糊匹配（例如 gpt-4o-2024-08-06 匹配 gpt-4o, gemini-2.0-flash-001 匹配 gemini-2.0-flash）。
            // 前綴之後必須是分隔字元或結尾：單純的 StartsWith 會讓 "gpt-4" 吃到 "gpt-4.1-mini"，
            // 把 $0.40 的模型以 $30 計價。
            string bestPrefixMatchKey = null;
            int bestPrefixLen = 0;

            string providerPrefix = (providerId?.Trim() ?? "") + ":";
            foreach (var kvp in KnownModelRates)
            {
                if (kvp.Key.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string candidateModel = kvp.Key.Substring(providerPrefix.Length);
                    if (normModel.StartsWith(candidateModel, StringComparison.OrdinalIgnoreCase) &&
                        IsPrefixBoundary(normModel, candidateModel.Length) &&
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
        /// 前綴比對的版本邊界：緊接在前綴後的字元必須是分隔符（或已到結尾），
        /// 讓 "gpt-4o" 命中 "gpt-4o-2024-08-06" 但 "gpt-4" 不命中 "gpt-4.1"。
        /// </summary>
        private static bool IsPrefixBoundary(string model, int prefixLength)
        {
            if (prefixLength >= model.Length) return true;
            char next = model[prefixLength];
            return next == '-' || next == '_' || next == ':' || next == '@' || next == ' ';
        }

        /// <summary>
        /// 快取命中（cache read / cachedContent）Token 相對於一般輸入 Token 的計費折扣倍率。
        /// </summary>
        private static float GetCacheReadDiscount(string providerId)
        {
            if (string.Equals(providerId?.Trim(), "deepseek", StringComparison.OrdinalIgnoreCase)) return 0.02f;
            return 0.25f;
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