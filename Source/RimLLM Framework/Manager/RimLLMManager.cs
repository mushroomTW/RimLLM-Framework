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

            // 初始化並註冊供應商
            RegisterProvider(new OpenAIProvider(settings));
            RegisterProvider(new GeminiProvider(settings));
            RegisterProvider(new OpenAICompatibleProvider(settings));
            RegisterProvider(new DeepSeekProvider(settings));
            RegisterProvider(new GroqProvider(settings));
            RegisterProvider(new AnthropicProvider(settings));
            RegisterProvider(new OpenRouterProvider(settings));
            RegisterProvider(new KimiProvider(settings));
            RegisterProvider(new MiniMaxProvider(settings));
            RegisterProvider(new QwenProvider(settings));
            RegisterProvider(new NvidiaProvider(settings));
        }

        private void RegisterProvider(ILLMProvider provider)
        {
            _providers[provider.ProviderId] = provider;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public Task<string> GenerateAsync(LLMRequest request)
        {
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            return GenerateInternalAsync(request, callingAssembly, verifyCaller: true);
        }

        /// <summary>
        /// 包裝排隊佇列的 GenerateInternalAsync。
        /// </summary>
        private Task<string> GenerateInternalAsync(LLMRequest request, Assembly callingAssembly, bool verifyCaller)
        {
            return _requestQueue.EnqueueRequestAsync(request, () => GenerateInternalDirectAsync(request, callingAssembly, verifyCaller));
        }

        /// <summary>
        /// 真正的非同步生成文字邏輯。
        /// </summary>
        private async Task<string> GenerateInternalDirectAsync(LLMRequest request, Assembly callingAssembly, bool verifyCaller)
        {
            var totalStopwatch = Stopwatch.StartNew();
            DateTime startTime = DateTime.Now;

            // 1. 來源身分安全校驗 (Caller Verification)
            if (verifyCaller && callingAssembly != null)
            {
                if (!ClientRegistry.Verify(request.ModId, callingAssembly))
                {
                    throw new RimLLMException(LLMError.InvalidKey, $"[RimLLM] Security verification failed. Assembly verification for ModId '{request.ModId}' did not pass.");
                }
            }

            // 1.5 檢查是否啟用串流輸出，若有則呼叫串流通道進行文字累加
            if (request.EnableStreaming)
            {
                var sb = new StringBuilder();
                await StreamInternalDirectAsync(request, chunk =>
                {
                    sb.Append(chunk);
                    request.OnChunkReceived?.Invoke(chunk);
                }, callingAssembly, verifyCaller: false).ConfigureAwait(false);
                return sb.ToString();
            }

            // 2. 獲取全域設定的 Fallback Chain
            var fallbackChain = _settings.FallbackChain;

            if (fallbackChain == null || fallbackChain.Count == 0)
            {
                throw new RimLLMException(LLMError.ProviderOffline, "No valid API provider fallback chain configured.");
            }

            Exception lastException = null;

            // 3. 依據 Fallback Chain 進行模型級輪詢嘗試
            foreach (string entry in fallbackChain)
            {
                if (!ResolveFallbackEntry(entry, out string providerId, out string modelName))
                    continue;

                if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
                    continue;

                // 檢查該 Provider 是否啟用
                if (!_settings.IsProviderEnabled(providerId))
                    continue;

                string apiKey = _settings.GetApiKey(providerId);
                // OpenAICompatible 放寬金鑰要求，其餘則必須提供 API Key
                if (string.IsNullOrEmpty(apiKey) && providerId != "OpenAICompatible")
                    continue;

                // 3.1 評估 MinFallbackLevel 模型分級
                int minLevel = ParseMinFallbackLevel(request.MinFallbackLevel);
                if (minLevel > 0)
                {
                    int currentModelLevel = GetModelLevel(modelName);
                    if (currentModelLevel < minLevel)
                    {
                        RimLLMLog.Message($"[RimLLM] Skipped fallback entry '{entry}' because its model level ({currentModelLevel}) is lower than MinFallbackLevel ({minLevel}).");
                        continue;
                    }
                }

                // 3.2 Circuit Breaker 健康狀態檢查
                bool shouldSkip = _circuitBreaker.IsCooldown(providerId, out DateTime cdTime, out int failures);
                if (shouldSkip)
                {
                    if (!_circuitBreaker.AreAllEnabledProvidersInCooldown(fallbackChain, _settings, id => _providers.ContainsKey(id)))
                    {
                        RimLLMLog.Message($"[RimLLM] Skipping provider {providerId} because it is in cooldown until {cdTime.ToLocalTime()} due to {failures} continuous failures.");
                        continue;
                    }
                }

                int maxRetries = _settings.MaxRetries;
                float retryDelay = _settings.RetryDelay;

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    // 檢查中途是否被取消
                    if (request.CancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(request.CancellationToken);
                    }

                    try
                    {
                        if (attempt > 0)
                        {
                            RimLLMLog.Message($"[RimLLM] Attempting to call provider: {providerId} (Model: {modelName}), retrying attempt {attempt + 1}...");
                        }
                        else
                        {
                            RimLLMLog.Message($"[RimLLM] Attempting to call provider: {providerId} (Model: {modelName})");
                        }
                        
                        var requestStopwatch = Stopwatch.StartNew();
                        string result = await provider.GenerateAsync(request, modelName).ConfigureAwait(false);
                        requestStopwatch.Stop();

                        // 成功後重設健康狀態冷卻
                        _circuitBreaker.RecordSuccess(providerId);

                        _usageTracker.RecordLog(startTime, request.ModId, providerId, modelName, true, null, requestStopwatch.ElapsedMilliseconds);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;

                        // 記錄失敗並計算健康狀態
                        _circuitBreaker.RecordFailure(providerId);

                        if (attempt < maxRetries)
                        {
                            RimLLMLog.Warning($"[RimLLM] Provider {providerId} (Model: {modelName}) call failed: {ex.Message}. Retrying in {retryDelay} seconds...");
                            if (retryDelay > 0f)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(retryDelay), request.CancellationToken).ConfigureAwait(false);
                            }
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
            throw new RimLLMException(
                LLMError.Unknown, 
                $"All fallback attempts failed. Last error reason: {lastException?.Message}", 
                lastException);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public Task<T> GenerateObjectAsync<T>(LLMRequest request)
        {
            Assembly callingAssembly = Assembly.GetCallingAssembly();
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
                RimLLMLog.Warning($"[RimLLM] First JSON parse failed, attempting fallback repair. Original response: {rawResponse}\nRepaired: {repairedJson}\nError: {ex.Message}");
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
                            $"Unable to parse LLM response to target object {typeof(T).Name}.\nOriginal response: {rawResponse}\nParse error: {ex.Message}\nLLM Assisted Repair error: {repairEx.Message}", 
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

        /// <summary>
        /// 包裝排隊佇列的 StreamInternalAsync。
        /// </summary>
        private async Task StreamInternalAsync(LLMRequest request, Action<string> onChunkReceived, Assembly callingAssembly, bool verifyCaller)
        {
            await _requestQueue.EnqueueRequestAsync(request, async () =>
            {
                await StreamInternalDirectAsync(request, onChunkReceived, callingAssembly, verifyCaller).ConfigureAwait(false);
                return "";
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 真正的非同步串流生成邏輯。
        /// </summary>
        private async Task StreamInternalDirectAsync(LLMRequest request, Action<string> onChunkReceived, Assembly callingAssembly, bool verifyCaller)
        {
            var totalStopwatch = Stopwatch.StartNew();
            DateTime startTime = DateTime.Now;

            // 1. 來源身分校驗
            if (verifyCaller && callingAssembly != null)
            {
                if (!ClientRegistry.Verify(request.ModId, callingAssembly))
                {
                    throw new RimLLMException(LLMError.InvalidKey, $"[RimLLM] Source verification failed. Assembly verification for ModId '{request.ModId}' did not pass.");
                }
            }

            var fallbackChain = _settings.FallbackChain;

            if (fallbackChain == null || fallbackChain.Count == 0)
            {
                throw new RimLLMException(LLMError.ProviderOffline, "No valid API provider fallback chain configured.");
            }

            bool connected = false;
            Exception lastException = null;

            // 尋找第一個可用的 Provider 與其模型，並嘗試建立連線與串流
            foreach (string entry in fallbackChain)
            {
                if (!ResolveFallbackEntry(entry, out string providerId, out string modelName))
                    continue;

                if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
                    continue;

                if (!_settings.IsProviderEnabled(providerId))
                    continue;

                string apiKey = _settings.GetApiKey(providerId);
                if (string.IsNullOrEmpty(apiKey) && providerId != "OpenAICompatible")
                    continue;

                // 評估 MinFallbackLevel 模型分級
                int minLevel = ParseMinFallbackLevel(request.MinFallbackLevel);
                if (minLevel > 0)
                {
                    int currentModelLevel = GetModelLevel(modelName);
                    if (currentModelLevel < minLevel)
                    {
                        RimLLMLog.Message($"[RimLLM] Skipped fallback entry '{entry}' because its model level ({currentModelLevel}) is lower than MinFallbackLevel ({minLevel}).");
                        continue;
                    }
                }

                // Circuit Breaker 健康狀態檢查
                bool shouldSkip = _circuitBreaker.IsCooldown(providerId, out DateTime cdTime, out int failures);
                if (shouldSkip)
                {
                    if (!_circuitBreaker.AreAllEnabledProvidersInCooldown(fallbackChain, _settings, id => _providers.ContainsKey(id)))
                    {
                        RimLLMLog.Message($"[RimLLM] Skipping provider {providerId} because it is in cooldown until {cdTime.ToLocalTime()} due to {failures} continuous failures.");
                        continue;
                    }
                }

                try
                {
                    // 檢查是否取消
                    if (request.CancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(request.CancellationToken);
                    }

                    var requestStopwatch = Stopwatch.StartNew();
                    await provider.StreamAsync(request, modelName, onChunkReceived).ConfigureAwait(false);
                    requestStopwatch.Stop();
                    connected = true;

                    // 成功後重設健康狀態冷卻
                    _circuitBreaker.RecordSuccess(providerId);

                    _usageTracker.RecordLog(startTime, request.ModId, providerId, modelName, true, null, requestStopwatch.ElapsedMilliseconds);
                    break;
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] Stream connection failed: {providerId} ({modelName}) -> {ex.Message}, trying next fallback.");
                    lastException = ex;

                    // 記錄失敗並計算健康狀態
                    _circuitBreaker.RecordFailure(providerId);
                }
            }

            if (!connected)
            {
                totalStopwatch.Stop();
                _usageTracker.RecordLog(startTime, request.ModId, "FallbackChain", "None", false, lastException?.Message ?? "All fallbacks failed", totalStopwatch.ElapsedMilliseconds);
                throw new RimLLMException(LLMError.ProviderOffline, $"All fallback attempts failed, unable to establish stream connection. Last error: {lastException?.Message}", lastException);
            }
        }

        public async Task<TestResult> TestProviderAsync(string providerId)
        {
            if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
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
            if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
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

        private string GetSampleJson<T>()
        {
            return RimLLMJsonHelper.GetSampleJson<T>();
        }

        private string GetSampleJson(Type type)
        {
            return RimLLMJsonHelper.GetSampleJson(type);
        }

        private bool ResolveFallbackEntry(string entry, out string providerId, out string modelName)
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
