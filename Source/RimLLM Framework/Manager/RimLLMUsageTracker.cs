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
                ErrorMessage = err,
                LatencyMs = latency
            };

            RequestLogs.Enqueue(entry);
            while (RequestLogs.Count > 30)
            {
                RequestLogs.TryDequeue(out _);
            }

            if (_settings is RimLLMFrameworkSettings frameworkSettings)
            {
                RimLLMDispatcher.Instance.Enqueue(() =>
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
            float promptRate = 0f;
            float completionRate = 0f;

            string provLower = (providerId ?? "").ToLower();
            if (provLower == "openai")
            {
                string modelLower = (modelName ?? "").ToLower();
                if (modelLower.Contains("mini") || modelLower.Contains("gpt-3.5"))
                {
                    promptRate = 0.150f;     // $0.15 / 1M
                    completionRate = 0.600f; // $0.60 / 1M
                }
                else
                {
                    promptRate = 2.50f;      // $2.50 / 1M (gpt-4o)
                    completionRate = 10.00f; // $10.00 / 1M
                }
            }
            else if (provLower == "gemini")
            {
                string modelLower = (modelName ?? "").ToLower();
                if (modelLower.Contains("pro"))
                {
                    promptRate = 1.25f;      // $1.25 / 1M
                    completionRate = 5.00f;  // $5.00 / 1M
                }
                else
                {
                    promptRate = 0.075f;     // $0.075 / 1M (gemini 2.5 flash)
                    completionRate = 0.300f; // $0.30 / 1M
                }
            }
            else if (provLower == "anthropic")
            {
                promptRate = 3.00f;          // Claude 3.7 Sonnet
                completionRate = 15.00f;
            }
            else if (provLower == "deepseek")
            {
                promptRate = 0.27f;          // Average cache hit / miss
                completionRate = 2.19f;
            }
            else if (provLower == "openrouter")
            {
                promptRate = 0.50f;
                completionRate = 1.50f;
            }
            else if (provLower == "groq")
            {
                promptRate = 0.59f;
                completionRate = 0.79f;
            }
            else if (provLower == "kimi" || provLower == "minimax" || provLower == "qwen")
            {
                promptRate = 0.30f;
                completionRate = 1.00f;
            }
            else
            {
                return 0f; // Free or local
            }

            float promptCost = (promptTokens / 1000000f) * promptRate;
            float completionCost = (completionTokens / 1000000f) * completionRate;
            return promptCost + completionCost;
        }
    }
}
