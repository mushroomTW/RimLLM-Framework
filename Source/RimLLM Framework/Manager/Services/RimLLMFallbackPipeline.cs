using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// 負責備用鏈（Fallback Chain）維護、Provider 失敗後自動嘗試下一個備用 Provider、
    /// 備用管道執行與熔斷器 (Circuit Breaker) 連動等邏輯的服務元件。
    /// </summary>
    public class RimLLMFallbackPipeline
    {
        private readonly IRimLLMSettings _settings;
        private readonly RimLLMCircuitBreaker _circuitBreaker;
        private readonly RimLLMUsageTracker _usageTracker;
        private readonly Func<string, ILLMProvider> _providerResolver;
        private readonly Func<string, bool> _isProviderEnabledFunc;

        private readonly ConcurrentDictionary<string, List<long>> ProviderLatencies =
            new ConcurrentDictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> ProviderFailCooldowns =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private struct ResolvedCandidate
        {
            public string Entry;
            public string ProviderId;
            public ILLMProvider Provider;
            public string ModelName;
        }

        private static readonly List<string> HighLevelKeywords = new List<string>
        {
            "pro", "opus"
        };

        private static readonly List<string> MediumLevelKeywords = new List<string>
        {
            "mini", "flash", "sonnet", "deepseek", "kimi", "minimax", "qwen"
        };

        public RimLLMFallbackPipeline(
            IRimLLMSettings settings,
            RimLLMCircuitBreaker circuitBreaker,
            RimLLMUsageTracker usageTracker,
            Func<string, ILLMProvider> providerResolver,
            Func<string, bool> isProviderEnabledFunc)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
            _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
            _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
            _isProviderEnabledFunc = isProviderEnabledFunc ?? throw new ArgumentNullException(nameof(isProviderEnabledFunc));
        }

        /// <summary>
        /// 共用的 Fallback Chain 執行核心。
        /// 依序遍歷符合資格的供應商條目，對每個條目套用相同的重試策略，
        /// 並統一處理取消檢查、熔斷記錄與用量統計。
        /// </summary>
        internal async Task<RimLLMGenerationResult> ExecuteWithFallbackAsync(
            RimLLMRequest request,
            Func<ILLMProvider, string, Task<RimLLMGenerationResult>> attemptAsync,
            LLMError exhaustedError,
            string exhaustedMessage,
            Action onAttemptStarting = null)
        {
            var totalStopwatch = Stopwatch.StartNew();
            DateTime startTime = DateTime.Now;

            var fallbackChain = GetFallbackChainSnapshot();
            if (fallbackChain == null || fallbackChain.Count == 0)
            {
                throw new RimLLMException(LLMError.ProviderOffline, "No valid API provider fallback chain configured.");
            }

            // PreferredModelId（格式 "ProviderId:ModelName"）指定的話，於 fallback chain 前優先嘗試
            var effectiveChain = new List<string>(fallbackChain);
            if (!string.IsNullOrEmpty(request.PreferredModelId))
            {
                string preferredEntry = request.PreferredModelId;
                if (!ResolveFallbackEntry(preferredEntry, out string prefProvider, out string prefModel)
                    || string.IsNullOrEmpty(prefModel))
                {
                    // 無 provider 前綴的純 model 名視為「不指定 provider」，忽略（交給 fallback chain）
                    prefProvider = null;
                }
                if (prefProvider != null && (_providerResolver(prefProvider) is ILLMProvider prefProviderInstance)
                    && IsProviderUsable(prefProvider, prefProviderInstance)
                    && !effectiveChain.Exists(e => string.Equals(e, preferredEntry, StringComparison.OrdinalIgnoreCase)))
                {
                    effectiveChain.Insert(0, preferredEntry);
                }
            }

            // 1. 解析所有符合資格的供應商候選
            var candidates = new List<ResolvedCandidate>();
            foreach (string entry in effectiveChain)
            {
                if (TryGetEligibleCandidate(entry, effectiveChain, request, out string pId, out ILLMProvider p, out string mName))
                {
                    candidates.Add(new ResolvedCandidate { Entry = entry, ProviderId = pId, Provider = p, ModelName = mName });
                }
            }

            if (candidates.Count == 0)
            {
                throw new RimLLMException(LLMError.ProviderOffline, "No eligible API providers found in the fallback chain.");
            }

            // 2. 過濾處於故障冷卻期的供應商（若全部都在冷卻中，則破例放行）
            var activeCandidates = candidates.FindAll(c => !IsInCooldown(c.ProviderId));
            if (activeCandidates.Count == 0)
            {
                activeCandidates = candidates;
            }

            // 3. 套用路由與負載均衡策略
            int strategy = _settings.RoutingStrategy;
            if (strategy == 1) // MinLatency (最小延遲優先)
            {
                activeCandidates.Sort((a, b) =>
                {
                    float latA = GetAverageLatency(a.ProviderId);
                    float latB = GetAverageLatency(b.ProviderId);
                    if (latA == 0f && latB != 0f) return -1;
                    if (latA != 0f && latB == 0f) return 1;
                    return latA.CompareTo(latB);
                });
            }
            else if (strategy == 2) // RoundRobin / Random (隨機輪詢負載均衡)
            {
                var rnd = new Random();
                for (int i = activeCandidates.Count - 1; i > 0; i--)
                {
                    int j = rnd.Next(i + 1);
                    var temp = activeCandidates[i];
                    activeCandidates[i] = activeCandidates[j];
                    activeCandidates[j] = temp;
                }
            }
            // strategy == 0 (PriorityFailover) 保留原始 fallbackChain 順序

            Exception lastException = null;
            int maxRetries = _settings.MaxRetries;
            float retryDelay = _settings.RetryDelay;

            foreach (var candidate in activeCandidates)
            {
                string providerId = candidate.ProviderId;
                ILLMProvider provider = candidate.Provider;
                string modelName = candidate.ModelName;
                bool isRetryableFailure = false;

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    // 檢查中途是否被取消
                    if (request.CancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(request.CancellationToken);
                    }

                    try
                    {
                        RimLLMLog.Message(attempt > 0
                            ? $"[RimLLM] Attempting to call provider: {providerId} (Model: {modelName}), retrying attempt {attempt + 1}..."
                            : $"[RimLLM] Attempting to call provider: {providerId} (Model: {modelName})");

                        var requestStopwatch = Stopwatch.StartNew();
                        // 通知串流累積器：本次嘗試即將開始，需捨棄前一次嘗試的殘留內容。
                        onAttemptStarting?.Invoke();

                        var attemptResult = await attemptAsync(provider, modelName).ConfigureAwait(false);
                        requestStopwatch.Stop();

                        // 成功後重設健康狀態與記錄延遲
                        _circuitBreaker.RecordSuccess(providerId);
                        RecordLatency(providerId, requestStopwatch.ElapsedMilliseconds);

                        _usageTracker.RecordLog(startTime, request.ModId, providerId, modelName, true, null, requestStopwatch.ElapsedMilliseconds);
                        return attemptResult;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        bool retryable = IsRetryableException(ex);

                        // 可重試類錯誤（網路、超時、限流等）同時視為健康度失敗，納入熔斷統計
                        if (retryable)
                        {
                            _circuitBreaker.RecordFailure(providerId);
                            isRetryableFailure = true;
                        }

                        if (retryable && attempt < maxRetries)
                        {
                            // 若伺服器透過 Retry-After 建議等待時間，取其與使用者設定延遲的較大者
                            float effectiveDelay = retryDelay;
                            if (ex is RimLLMException rimEx && rimEx.RetryAfter.HasValue)
                            {
                                effectiveDelay = Math.Min(Math.Max(effectiveDelay, (float)rimEx.RetryAfter.Value.TotalSeconds), 60f);
                            }

                            RimLLMLog.Warning($"[RimLLM] Provider {providerId} (Model: {modelName}) call failed: {RimLLMLog.SanitizeForLog(ex.Message, 300)}. Retrying in {effectiveDelay:F1} seconds...");
                            if (effectiveDelay > 0f)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(effectiveDelay), request.CancellationToken).ConfigureAwait(false);
                            }
                        }
                        else if (!retryable)
                        {
                            RimLLMLog.Warning($"[RimLLM] Provider {providerId} (Model: {modelName}) returned a non-retryable error: {RimLLMLog.SanitizeForLog(ex.Message, 300)}. Fallbacking to the next entry.");
                            break;
                        }
                        else
                        {
                            RimLLMLog.Warning($"[RimLLM] Provider {providerId} (Model: {modelName}) reached maximum retries ({maxRetries}). Fallbacking to the next entry.");
                        }
                    }
                }

                if (isRetryableFailure)
                {
                    // 只有在因為網路或暫時性錯誤（可重試錯誤）導致失敗時，才置入冷卻阻斷期
                    ProviderFailCooldowns[providerId] = DateTime.UtcNow.AddSeconds(60);
                }
            }

            totalStopwatch.Stop();
            _usageTracker.RecordLog(startTime, request.ModId, "FallbackChain", "None", false, lastException?.Message ?? "All fallbacks failed", totalStopwatch.ElapsedMilliseconds);
            throw new RimLLMException(exhaustedError, $"{exhaustedMessage} Last error: {lastException?.Message}", lastException);
        }

        internal bool ResolveFallbackEntry(string entry, out string providerId, out string modelName)
        {
            providerId = ProviderIds.ParseProviderId(entry);
            modelName = "";

            if (providerId == null)
            {
                providerId = entry;
                return false;
            }

            int colonIndex = entry.IndexOf(':');
            modelName = colonIndex > 0
                ? entry.Substring(colonIndex + 1)
                // 純供應商：取該供應商的預設模型
                : _settings.GetDefaultModel(providerId, "default");

            return true;
        }

        public void ClearCooldowns()
        {
            ProviderFailCooldowns.Clear();
            ProviderLatencies.Clear();
        }

        public bool IsInCooldown(string providerId)
        {
            return ProviderFailCooldowns.TryGetValue(providerId, out DateTime cdUntil) && DateTime.UtcNow < cdUntil;
        }

        public float GetAverageLatency(string providerId)
        {
            if (ProviderLatencies.TryGetValue(providerId, out var list) && list.Count > 0)
            {
                lock (list)
                {
                    long sum = 0;
                    foreach (var val in list) sum += val;
                    return (float)sum / list.Count;
                }
            }
            return 0f;
        }

        public void RecordLatency(string providerId, long ms)
        {
            var list = ProviderLatencies.GetOrAdd(providerId, _ => new List<long>());
            lock (list)
            {
                list.Add(ms);
                if (list.Count > 5)
                {
                    list.RemoveAt(0);
                }
            }
        }

        private List<string> GetFallbackChainSnapshot()
        {
            var chain = _settings.FallbackChain;
            return chain != null ? new List<string>(chain) : null;
        }

        private bool IsProviderUsable(string providerId, ILLMProvider provider)
        {
            if (!_isProviderEnabledFunc(providerId))
                return false;

            if (provider.RequiresApiKey && string.IsNullOrEmpty(_settings.GetApiKey(providerId)))
                return false;

            return true;
        }

        private bool TryGetEligibleCandidate(string entry, List<string> fallbackChain, RimLLMRequest request, out string providerId, out ILLMProvider provider, out string modelName)
        {
            provider = null;

            if (!ResolveFallbackEntry(entry, out providerId, out modelName))
                return false;

            provider = _providerResolver(providerId);
            if (provider == null)
                return false;

            if (!IsProviderUsable(providerId, provider))
                return false;

            // Budget fallback to free
            if (_settings.DailyBudgetLimit > 0f && _settings.DailyAccumulatedCost >= _settings.DailyBudgetLimit)
            {
                if (_settings.BudgetPolicy == 2) // FallbackToFree (0=HardBlock, 1=SilentMocking, 2=FallbackToFree, 3=DialogPrompt)
                {
                    bool isFree = providerId == ProviderIds.OpenAICompatible || modelName.ToLower().Contains("free");
                    if (!isFree)
                    {
                        return false;
                    }
                }
            }

            // 評估 MinFallbackLevel 模型分級
            int minLevel = ParseMinFallbackLevel(request.MinFallbackLevel);
            if (minLevel > 0)
            {
                int currentModelLevel = GetModelLevel(modelName);
                if (currentModelLevel < minLevel)
                {
                    RimLLMLog.Message($"[RimLLM] Skipped fallback entry '{entry}' because its model level ({currentModelLevel}) is lower than MinFallbackLevel ({minLevel}).");
                    return false;
                }
            }

            // Circuit Breaker 健康狀態檢查
            if (_circuitBreaker.IsCooldown(providerId, out DateTime cdTime, out int failures))
            {
                if (!_circuitBreaker.AreAllEligibleProvidersInCooldown(fallbackChain, id =>
                    {
                        var p = _providerResolver(id);
                        return p != null && IsProviderUsable(id, p);
                    }))
                {
                    RimLLMLog.Message($"[RimLLM] Skipping provider {providerId} because it is in cooldown until {cdTime.ToLocalTime()} due to {failures} continuous failures.");
                    return false;
                }
            }

            return true;
        }

        private int GetModelLevel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return 1;

            // 使用者明確設定的分級覆寫優先於關鍵字啟發式判斷
            int overrideLevel = _settings.GetModelLevelOverride(modelName);
            if (overrideLevel >= 1 && overrideLevel <= 3)
            {
                return overrideLevel;
            }

            string lower = modelName.ToLower();

            // 如果含有 High 關鍵字，則優先判定為 Tier 3
            foreach (var kw in HighLevelKeywords)
            {
                if (lower.Contains(kw))
                {
                    return 3;
                }
            }

            // 如果不含 High 關鍵字但含有 Medium 關鍵字，則為 Tier 2
            foreach (var kw in MediumLevelKeywords)
            {
                if (lower.Contains(kw))
                {
                    return 2;
                }
            }

            // 其餘為 Tier 1
            return 1;
        }

        private int ParseMinFallbackLevel(string levelStr)
        {
            if (string.IsNullOrEmpty(levelStr)) return 0;
            string lower = levelStr.ToLower();
            if (lower == "high" || lower == "3") return 3;
            if (lower == "medium" || lower == "2") return 2;
            if (lower == "low" || lower == "1") return 1;
            return 0;
        }

        private bool IsRetryableException(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                return false;
            }

            if (ex is RimLLMException rimEx)
            {
                switch (rimEx.Error)
                {
                    case LLMError.Timeout:
                    case LLMError.RateLimit:
                    case LLMError.ProviderOffline:
                    case LLMError.NetworkError:
                    case LLMError.QuotaExceeded:
                    case LLMError.Unknown:
                        return true;
                    default:
                        return false;
                }
            }

            // 參數、狀態與解析類例外代表呼叫本身有問題，以相同輸入重試必然再次失敗。
            if (ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is InvalidOperationException ||
                ex is Newtonsoft.Json.JsonException)
            {
                return false;
            }

            return true;
        }
    }
}
