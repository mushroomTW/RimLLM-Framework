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
            { "anthropic:claude-fable-5", new CostRate(10.00f, 50.00f) },
            { "anthropic:claude-opus-4-8", new CostRate(5.00f, 25.00f) },
            { "anthropic:claude-sonnet-4-6", new CostRate(3.00f, 15.00f) },
            { "anthropic:claude-haiku-4-5", new CostRate(1.00f, 5.00f) },
            { "anthropic:claude-haiku-4-5-20251001", new CostRate(1.00f, 5.00f) },
            { "gemini:gemini-3.1-pro-preview", new CostRate(2.00f, 12.00f) },
            { "gemini:gemini-3.1-flash-lite", new CostRate(0.25f, 1.50f) },
            { "gemini:gemini-2.5-pro", new CostRate(1.25f, 10.00f) },
            { "gemini:gemini-2.5-flash", new CostRate(0.30f, 2.50f) },
            { "gemini:gemini-2.5-flash-lite", new CostRate(0.10f, 0.40f) }
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

        public RimLLMUsageTracker(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            
            if (_settings is RimLLMFrameworkSettings frameworkSettings && frameworkSettings.RequestLogs != null)
            {
                foreach (var log in frameworkSettings.RequestLogs)
                {
                    RequestLogs.Enqueue(log);
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

            if (_settings is RimLLMFrameworkSettings frameworkSettings)
            {
                RimLLMDispatcher.EnqueueOnMainThread(() =>
                {
                    lock (LogLock)
                    {
                        frameworkSettings.RequestLogs = new List<RimLLMManager.RequestLogEntry>(RequestLogs.ToArray());
                        // 節流：非成功或過了 15 秒以上才執行實體寫入
                        if (!success || (DateTime.UtcNow - _lastLogWriteTime).TotalSeconds > 15)
                        {
                            try
                            {
                                frameworkSettings.Write();
                                _lastLogWriteTime = DateTime.UtcNow;
                            }
                            catch (Exception ex)
                            {
                                RimLLMLog.Warning($"[RimLLM] Throttled Write failed: {ex.Message}");
                            }
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

            if (_settings is RimLLMFrameworkSettings frameworkSettings)
            {
                lock (LogLock)
                {
                    frameworkSettings.RequestLogs = new List<RimLLMManager.RequestLogEntry>();
                    try
                    {
                        frameworkSettings.Write();
                        _lastLogWriteTime = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        RimLLMLog.Warning($"[RimLLM] Clear logs Write failed: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 累加 Token 統計值，並估計該次 API 消耗的美元成本。
        /// </summary>
        public void RecordUsage(string providerId, string modelName, int promptTokens, int completionTokens)
        {
            if (promptTokens <= 0 && completionTokens <= 0) return;

            lock (UsageLock)
            {
                _settings.TotalPromptTokens += promptTokens;
                _settings.TotalCompletionTokens += completionTokens;
                
                float cost = EstimateCost(providerId, modelName, promptTokens, completionTokens);
                _settings.TotalEstimatedCost += cost;
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
                try
                {
                    _settings.Write();
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] Reset usage Write failed: {ex.Message}");
                }
            }
        }

        private float EstimateCost(string providerId, string modelName, int promptTokens, int completionTokens)
        {
            string key = $"{NormalizeProvider(providerId)}:{NormalizeModel(modelName)}";
            if (!KnownModelRates.TryGetValue(key, out var rate))
            {
                return 0f;
            }

            float promptCost = (promptTokens / 1000000f) * rate.PromptPerMillion;
            float completionCost = (completionTokens / 1000000f) * rate.CompletionPerMillion;
            return promptCost + completionCost;
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
