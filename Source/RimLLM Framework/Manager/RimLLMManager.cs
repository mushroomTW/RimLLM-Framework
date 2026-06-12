using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// IRimLLM 介面的核心管理器實作。
    /// 統一調度 API 供應商、執行雙重 Fallback 容錯、校驗調用者來源。
    /// 內部邏輯委託給排隊佇列 (RequestQueue)、熔斷器 (CircuitBreaker)、JSON 輔助 (JsonHelper) 與使用統計器 (UsageTracker)。
    /// </summary>
    public class RimLLMManager : IRimLLM
    {
        private readonly IRimLLMSettings _settings;
        private readonly Dictionary<string, ILLMProvider> _providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _providerOrder = new List<string>();
        private readonly HashSet<string> _builtInProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _providerLock = new object();
        private readonly HashSet<Type> _registeredTypes = new HashSet<Type>();

        // 拆分出去的模組實體
        private readonly RimLLMRequestQueue _requestQueue;
        private readonly RimLLMCircuitBreaker _circuitBreaker;
        private readonly RimLLMUsageTracker _usageTracker;

        /// <summary>
        /// 使用量統計日誌實體，保持結構以相容 Scribe 序列化。
        /// </summary>
        public class RequestLogEntry
        {
            public DateTime Timestamp { get; set; } = DateTime.Now;
            public string ModId { get; set; }
            public string Provider { get; set; }
            public string Model { get; set; }
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public long LatencyMs { get; set; }
        }

        /// <summary>
        /// 提供外部 UI 查詢的呼叫日誌歷史記錄轉發。
        /// </summary>
        public ConcurrentQueue<RequestLogEntry> RequestLogs => _usageTracker.RequestLogs;

        public RimLLMManager(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            RimLLMLog.Enabled = _settings.DetailedLogging;

            // 建立子模組
            _requestQueue = new RimLLMRequestQueue(settings);
            _circuitBreaker = new RimLLMCircuitBreaker();
            _usageTracker = new RimLLMUsageTracker(settings);

            // 初始化並註冊內建供應商
            RegisterBuiltInProvider(new OpenAIProvider(settings));
            RegisterBuiltInProvider(new GeminiProvider(settings));
            RegisterBuiltInProvider(new OpenAICompatibleProvider(settings));
            RegisterBuiltInProvider(new DeepSeekProvider(settings));
            RegisterBuiltInProvider(new GroqProvider(settings));
            RegisterBuiltInProvider(new AnthropicProvider(settings));
            RegisterBuiltInProvider(new OpenRouterProvider(settings));
            RegisterBuiltInProvider(new KimiProvider(settings));
            RegisterBuiltInProvider(new MiniMaxProvider(settings));
            RegisterBuiltInProvider(new QwenProvider(settings));
            RegisterBuiltInProvider(new NvidiaProvider(settings));
        }

        private void RegisterBuiltInProvider(ILLMProvider provider)
        {
            lock (_providerLock)
            {
                _providers[provider.ProviderId] = provider;
                _providerOrder.Add(provider.ProviderId);
                _builtInProviderIds.Add(provider.ProviderId);
            }
        }

        /// <summary>
        /// 註冊外部供應商，供第三方 Mod 擴充自訂的 LLM 供應商。
        /// 外部供應商註冊後即視為啟用，使用者透過 Fallback Chain 控制其參與。
        /// </summary>
        /// <exception cref="InvalidOperationException">當 ProviderId 與既有供應商重複時擲出，防止覆蓋內建供應商。</exception>
        public void RegisterProvider(ILLMProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (string.IsNullOrEmpty(provider.ProviderId))
                throw new ArgumentException("ProviderId cannot be empty or null", nameof(provider));

            lock (_providerLock)
            {
                if (_providers.ContainsKey(provider.ProviderId))
                {
                    throw new InvalidOperationException($"[RimLLM] Provider ID '{provider.ProviderId}' is already registered and cannot be overridden.");
                }

                _providers[provider.ProviderId] = provider;
                _providerOrder.Add(provider.ProviderId);
            }
            RimLLMLog.Message($"[RimLLM] Registered external provider: {provider.ProviderId}");
        }

        /// <summary>
        /// 取得所有已註冊供應商的識別碼（依註冊順序）。
        /// </summary>
        public List<string> GetRegisteredProviderIds()
        {
            lock (_providerLock)
            {
                return new List<string>(_providerOrder);
            }
        }

        /// <summary>
        /// 檢查供應商是否啟用。內建供應商由設定 UI 控制；外部註冊的供應商視為註冊即啟用。
        /// </summary>
        public bool IsProviderEnabled(string providerId)
        {
            bool isBuiltIn;
            lock (_providerLock)
            {
                if (!_providers.ContainsKey(providerId))
                    return false;
                isBuiltIn = _builtInProviderIds.Contains(providerId);
            }
            return !isBuiltIn || _settings.IsProviderEnabled(providerId);
        }

        /// <summary>
        /// 執行緒安全地查找已註冊的供應商（外部註冊可能與請求併發）。
        /// </summary>
        private bool TryGetProvider(string providerId, out ILLMProvider provider)
        {
            lock (_providerLock)
            {
                return _providers.TryGetValue(providerId, out provider);
            }
        }

        private static LLMRequest CreateSimpleRequest(
            string modId,
            string prompt,
            string systemPrompt,
            string cachedContext,
            int maxTokens,
            float temperature,
            LLMReasoningEffort reasoningEffort,
            CancellationToken cancellationToken)
        {
            var request = LLMRequest.Create(modId, prompt)
                .WithSystemPrompt(systemPrompt)
                .WithSampling(maxTokens, temperature)
                .WithReasoning(reasoningEffort)
                .WithCancellation(cancellationToken);

            if (!string.IsNullOrEmpty(cachedContext))
            {
                request.WithCachedContext(cachedContext);
            }

            return request;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public Task<string> GenerateAsync(LLMRequest request)
        {
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            return GenerateInternalAsync(request, callingAssembly, verifyCaller: true);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public Task<string> GenerateAsync(
            string modId,
            string prompt,
            string systemPrompt = null,
            string cachedContext = null,
            int maxTokens = 1024,
            float temperature = 0.7f,
            LLMReasoningEffort reasoningEffort = LLMReasoningEffort.Auto,
            CancellationToken cancellationToken = default)
        {
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            var request = CreateSimpleRequest(modId, prompt, systemPrompt, cachedContext, maxTokens, temperature, reasoningEffort, cancellationToken);
            return GenerateInternalAsync(request, callingAssembly, verifyCaller: true);
        }

        /// <summary>
        /// 包裝排隊佇列的 GenerateInternalAsync。
        /// </summary>
        private Task<string> GenerateInternalAsync(LLMRequest request, Assembly callingAssembly, bool verifyCaller)
        {
            LLMRequest normalizedRequest = NormalizeRequest(request);
            return _requestQueue.EnqueueRequestAsync(normalizedRequest, () => GenerateInternalDirectAsync(normalizedRequest, callingAssembly, verifyCaller));
        }

        /// <summary>
        /// 真正的非同步生成文字邏輯。
        /// </summary>
        private async Task<string> GenerateInternalDirectAsync(LLMRequest request, Assembly callingAssembly, bool verifyCaller)
        {
            // 1. 來源身分安全校驗 (Caller Verification)
            VerifyCallerOrThrow(request, callingAssembly, verifyCaller);

            // 1.5 檢查是否啟用串流輸出，若有則呼叫串流通道進行文字累加
            if (request.EnableStreaming)
            {
                var sb = new StringBuilder();
                await StreamInternalDirectAsync(request, chunk =>
                {
                    sb.Append(chunk);
                    DispatchChunk(request.OnChunkReceived, chunk);
                }, callingAssembly, verifyCaller: false).ConfigureAwait(false);
                return sb.ToString();
            }

            // 2. 交由共用的 Fallback Chain 執行核心處理
            return await ExecuteWithFallbackAsync(
                request,
                (provider, modelName) => provider.GenerateAsync(request, modelName),
                LLMError.Unknown,
                "All fallback attempts failed.").ConfigureAwait(false);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public Task<T> GenerateObjectAsync<T>(LLMRequest request)
        {
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            return GenerateObjectInternalAsync<T>(request, callingAssembly);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public Task<T> GenerateObjectAsync<T>(
            string modId,
            string prompt,
            string systemPrompt = null,
            string cachedContext = null,
            int maxTokens = 1024,
            float temperature = 0.7f,
            LLMReasoningEffort reasoningEffort = LLMReasoningEffort.Auto,
            CancellationToken cancellationToken = default)
        {
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            var request = CreateSimpleRequest(modId, prompt, systemPrompt, cachedContext, maxTokens, temperature, reasoningEffort, cancellationToken);
            return GenerateObjectInternalAsync<T>(request, callingAssembly);
        }

        private async Task<T> GenerateObjectInternalAsync<T>(LLMRequest request, Assembly callingAssembly)
        {
            var requestClone = request.Clone();
            requestClone.ResponseType = typeof(T);

            // 在 SystemPrompt 後方附加 JSON schema 格式指示
            string schemaInstructions = $"\n\n[CRITICAL REQUIREMENT: You MUST respond ONLY with a raw JSON object matching the following structure. Do not include markdown code block syntax (like ```json). Just the raw json text. Example:\n{RimLLMJsonHelper.GetSampleJson<T>()}]";
            
            string originalSystemPrompt = requestClone.SystemPrompt ?? "";
            requestClone.SystemPrompt = originalSystemPrompt + schemaInstructions;

            // 驗證呼叫者身份
            string rawResponse = await GenerateInternalAsync(requestClone, callingAssembly, verifyCaller: true).ConfigureAwait(false);
            
            // 執行 JSON 修復
            string repairedJson = RimLLMJsonHelper.RepairJson(rawResponse);

            try
            {
                T result = JsonConvert.DeserializeObject<T>(repairedJson);
                return result;
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"[RimLLM] First JSON parse failed, attempting fallback repair. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}\nRepaired preview: {RimLLMLog.SanitizeForLog(repairedJson, 300)}\nError: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
                try
                {
                    string fallbackExtracted = RimLLMJsonHelper.ExtractJsonBlock(repairedJson);
                    T result = JsonConvert.DeserializeObject<T>(fallbackExtracted);
                    return result;
                }
                catch
                {
                    // 二次修復 (Double-Repair)
                    RimLLMLog.Message($"[RimLLM] Static JSON repair failed. Initiating Double-Repair (LLM-assisted repair)...");
                    try
                    {
                        T repairedObj = await PerformDoubleRepairAsync<T>(request, rawResponse, ex.Message).ConfigureAwait(false);
                        return repairedObj;
                    }
                    catch (Exception repairEx)
                    {
                        throw new RimLLMException(
                            LLMError.InvalidResponse, 
                            $"Unable to parse LLM response to target object {typeof(T).Name}. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}. Parse error: {RimLLMLog.SanitizeForLog(ex.Message, 200)}. LLM-assisted repair error: {RimLLMLog.SanitizeForLog(repairEx.Message, 200)}", 
                            repairEx);
                    }
                }
            }
        }

        /// <summary>
        /// 外部 API 進入點：同步包裝方法以確保 Assembly.GetCallingAssembly() 能在 async 狀態機破壞調用堆疊前正確取得呼叫端組件。
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public Task StreamAsync(LLMRequest request, Action<string> onChunkReceived)
        {
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            return StreamInternalAsync(request, onChunkReceived, callingAssembly, verifyCaller: true);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public Task<string> GenerateStreamingAsync(
            string modId,
            string prompt,
            Action<string> onChunkReceived,
            string systemPrompt = null,
            string cachedContext = null,
            int maxTokens = 1024,
            float temperature = 0.7f,
            LLMReasoningEffort reasoningEffort = LLMReasoningEffort.Auto,
            CancellationToken cancellationToken = default)
        {
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            var request = CreateSimpleRequest(modId, prompt, systemPrompt, cachedContext, maxTokens, temperature, reasoningEffort, cancellationToken)
                .WithStreaming(onChunkReceived);
            return GenerateInternalAsync(request, callingAssembly, verifyCaller: true);
        }

        /// <summary>
        /// 包裝排隊佇列的 StreamInternalAsync。
        /// </summary>
        private async Task StreamInternalAsync(LLMRequest request, Action<string> onChunkReceived, Assembly callingAssembly, bool verifyCaller)
        {
            LLMRequest normalizedRequest = NormalizeRequest(request);
            Action<string> mainThreadCallback = chunk => DispatchChunk(onChunkReceived, chunk);
            await _requestQueue.EnqueueRequestAsync(normalizedRequest, async () =>
            {
                await StreamInternalDirectAsync(normalizedRequest, mainThreadCallback, callingAssembly, verifyCaller).ConfigureAwait(false);
                return "";
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 真正的非同步串流生成邏輯。
        /// 與非串流路徑共用相同的 Fallback Chain 執行核心，因此重試與熔斷行為一致。
        /// </summary>
        private async Task StreamInternalDirectAsync(LLMRequest request, Action<string> onChunkReceived, Assembly callingAssembly, bool verifyCaller)
        {
            // 來源身分校驗
            VerifyCallerOrThrow(request, callingAssembly, verifyCaller);

            await ExecuteWithFallbackAsync(
                request,
                async (provider, modelName) =>
                {
                    await provider.StreamAsync(request, modelName, onChunkReceived).ConfigureAwait(false);
                    return "";
                },
                LLMError.ProviderOffline,
                "All fallback attempts failed, unable to establish stream connection.").ConfigureAwait(false);
        }

        /// <summary>
        /// 共用的 Fallback Chain 執行核心。
        /// 依序遍歷符合資格的供應商條目，對每個條目套用相同的重試策略，
        /// 並統一處理取消檢查、熔斷記錄與用量統計。
        /// </summary>
        private async Task<string> ExecuteWithFallbackAsync(
            LLMRequest request,
            Func<ILLMProvider, string, Task<string>> attemptAsync,
            LLMError exhaustedError,
            string exhaustedMessage)
        {
            var totalStopwatch = Stopwatch.StartNew();
            DateTime startTime = DateTime.Now;

            var fallbackChain = GetFallbackChainSnapshot();
            if (fallbackChain == null || fallbackChain.Count == 0)
            {
                throw new RimLLMException(LLMError.ProviderOffline, "No valid API provider fallback chain configured.");
            }

            Exception lastException = null;
            int maxRetries = _settings.MaxRetries;
            float retryDelay = _settings.RetryDelay;

            foreach (string entry in fallbackChain)
            {
                if (!TryGetEligibleCandidate(entry, fallbackChain, request, out string providerId, out ILLMProvider provider, out string modelName))
                    continue;

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
                        string result = await attemptAsync(provider, modelName).ConfigureAwait(false);
                        requestStopwatch.Stop();

                        // 成功後重設健康狀態冷卻
                        _circuitBreaker.RecordSuccess(providerId);

                        _usageTracker.RecordLog(startTime, request.ModId, providerId, modelName, true, null, requestStopwatch.ElapsedMilliseconds);
                        return result;
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
                        }

                        if (retryable && attempt < maxRetries)
                        {
                            // 若伺服器透過 Retry-After 建議等待時間，取其與使用者設定延遲的較大者（上限 60 秒，避免長時間卡住）
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
            }

            totalStopwatch.Stop();
            _usageTracker.RecordLog(startTime, request.ModId, "FallbackChain", "None", false, lastException?.Message ?? "All fallbacks failed", totalStopwatch.ElapsedMilliseconds);
            throw new RimLLMException(exhaustedError, $"{exhaustedMessage} Last error: {lastException?.Message}", lastException);
        }

        /// <summary>
        /// 檢查供應商是否可用：已啟用且（若需要）API Key 存在。
        /// </summary>
        private bool IsProviderUsable(string providerId, ILLMProvider provider)
        {
            if (!IsProviderEnabled(providerId))
                return false;

            if (provider.RequiresApiKey && string.IsNullOrEmpty(_settings.GetApiKey(providerId)))
                return false;

            return true;
        }

        /// <summary>
        /// 檢查單一 Fallback 條目是否具備執行資格：
        /// 供應商已註冊且可用（啟用 + 金鑰）、模型分級達標、且未處於熔斷冷卻。
        /// 若所有可用供應商都在冷卻中，則破例放行以避免完全斷線。
        /// </summary>
        private bool TryGetEligibleCandidate(string entry, List<string> fallbackChain, LLMRequest request, out string providerId, out ILLMProvider provider, out string modelName)
        {
            provider = null;

            if (!ResolveFallbackEntry(entry, out providerId, out modelName))
                return false;

            if (!TryGetProvider(providerId, out provider))
                return false;

            if (!IsProviderUsable(providerId, provider))
                return false;

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
                if (!_circuitBreaker.AreAllEligibleProvidersInCooldown(fallbackChain, id => TryGetProvider(id, out ILLMProvider p) && IsProviderUsable(id, p)))
                {
                    RimLLMLog.Message($"[RimLLM] Skipping provider {providerId} because it is in cooldown until {cdTime.ToLocalTime()} due to {failures} continuous failures.");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 來源身分安全校驗 (Caller Verification)。
        /// </summary>
        private static void VerifyCallerOrThrow(LLMRequest request, Assembly callingAssembly, bool verifyCaller)
        {
            if (!verifyCaller || callingAssembly == null)
                return;

            if (!ClientRegistry.Verify(request.ModId, callingAssembly))
            {
                throw new RimLLMException(LLMError.InvalidKey, $"[RimLLM] Caller verification failed. Assembly verification for ModId '{request.ModId}' did not pass.");
            }
        }

        public async Task<TestResult> TestProviderAsync(string providerId)
        {
            if (!TryGetProvider(providerId, out ILLMProvider provider))
            {
                return new TestResult
                {
                    Success = false,
                    Provider = providerId,
                    ErrorMessage = $"Unknown provider ID: {providerId}",
                    ErrorCode = LLMError.ProviderOffline
                };
            }

            return await provider.TestConnectionAsync().ConfigureAwait(false);
        }

        public async Task<List<string>> FetchProviderModelsAsync(string providerId)
        {
            if (!TryGetProvider(providerId, out ILLMProvider provider))
            {
                throw new RimLLMException(LLMError.ProviderOffline, $"Unknown provider ID: {providerId}");
            }

            return await provider.FetchAvailableModelsAsync().ConfigureAwait(false);
        }

        public void RegisterResponseType<T>()
        {
            Type type = typeof(T);
            lock (_registeredTypes)
            {
                _registeredTypes.Add(type);
            }
            // 預先產生 Schema 並快取，實現預熱 (Warmup)
            RimLLMJsonHelper.GetSampleJson<T>();
            RimLLMLog.Message($"[RimLLM] Registered structured response type and finished cache pre-warmup: {type.FullName}");
        }

        #region Helper Methods

        private List<string> GetFallbackChainSnapshot()
        {
            var chain = _settings.FallbackChain;
            return chain != null ? new List<string>(chain) : null;
        }

        private LLMRequest NormalizeRequest(LLMRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.ReasoningEffort != LLMReasoningEffort.Auto || _settings.DefaultReasoningEffort == LLMReasoningEffort.Auto)
            {
                return request;
            }

            var clone = request.Clone();
            clone.ReasoningEffort = _settings.DefaultReasoningEffort;
            return clone;
        }

        /// <summary>
        /// 判斷例外是否屬於暫時性錯誤（網路、超時、限流等）。
        /// 暫時性錯誤可以重試，同時也會被記入熔斷器的健康度統計；
        /// 非暫時性錯誤（如金鑰無效）直接 fallback 到下一個條目且不觸發熔斷。
        /// </summary>
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

            return true;
        }

        private void DispatchChunk(Action<string> callback, string chunk)
        {
            if (callback == null) return;
            RimLLMDispatcher.EnqueueOnMainThread(() => callback(chunk));
        }

        private string GetSampleJson<T>()
        {
            return RimLLMJsonHelper.GetSampleJson<T>();
        }

        internal string GetSampleJson(Type type)
        {
            return RimLLMJsonHelper.GetSampleJson(type);
        }

        internal bool ResolveFallbackEntry(string entry, out string providerId, out string modelName)
        {
            providerId = entry;
            modelName = "";

            if (string.IsNullOrEmpty(entry))
            {
                return false;
            }

            int colonIndex = entry.IndexOf(':');
            if (colonIndex > 0)
            {
                providerId = entry.Substring(0, colonIndex);
                modelName = entry.Substring(colonIndex + 1);
            }
            else
            {
                // 純供應商
                if (string.IsNullOrEmpty(modelName))
                {
                    modelName = _settings.GetDefaultModel(providerId, "default");
                }
            }

            return true;
        }

        #endregion

        #region Concurrency Queue & Double-Repair Methods

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

        private static readonly List<string> HighLevelKeywords = new List<string>
        {
            "pro", "opus"
        };

        private static readonly List<string> MediumLevelKeywords = new List<string>
        {
            "mini", "flash", "sonnet", "deepseek",  "kimi", "minimax", "qwen"
        };

        private int ParseMinFallbackLevel(string levelStr)
        {
            if (string.IsNullOrEmpty(levelStr)) return 0;
            string lower = levelStr.ToLower();
            if (lower == "high" || lower == "3") return 3;
            if (lower == "medium" || lower == "2") return 2;
            if (lower == "low" || lower == "1") return 1;
            return 0;
        }

        private async Task<T> PerformDoubleRepairAsync<T>(LLMRequest originalRequest, string failedResponse, string errorMessage)
        {
            var repairRequest = new LLMRequest
            {
                ModId = originalRequest.ModId,
                Temperature = 0.1f, // 低隨機性有利於修復格式
                MaxTokens = originalRequest.MaxTokens,
                CancellationToken = originalRequest.CancellationToken,
                ReasoningEffort = LLMReasoningEffort.None,
                SystemPrompt = "You are a JSON repair assistant. The user will provide a JSON string that failed to parse, along with the parser error message. Your task is to output ONLY the corrected JSON string that is syntactically valid and contains all fields. Do NOT include markdown code blocks (like ```json), explanations, or any other text.",
                Prompt = $"Failed JSON:\n{failedResponse}\n\nParser Error:\n{errorMessage}\n\nTarget Structure Sample:\n{RimLLMJsonHelper.GetSampleJson<T>()}\n\nPlease output the repaired JSON string:"
            };

            string repairResponse = await GenerateInternalDirectAsync(repairRequest, null, verifyCaller: false).ConfigureAwait(false);
            string repairedJson = RimLLMJsonHelper.RepairJson(repairResponse);
            
            return JsonConvert.DeserializeObject<T>(repairedJson);
        }

        public void ClearLogs()
        {
            _usageTracker.ClearLogs();
        }

        public void RecordUsage(string providerId, string modelName, int promptTokens, int completionTokens)
        {
            _usageTracker.RecordUsage(providerId, modelName, promptTokens, completionTokens);
        }

        public void ResetUsage()
        {
            _usageTracker.ResetUsage();
        }

        #endregion
    }
}
