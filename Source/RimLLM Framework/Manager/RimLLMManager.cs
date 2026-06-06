using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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
    /// 統一調度 API 供應商、執行雙重 Fallback 容錯、校驗調用者來源、並對結構化輸出進行 JSON 修復。
    /// 新增優先權排隊、並行限流、Circuit Breaker 冷卻、模型分級過濾與二次 LLM 修復。
    /// </summary>
    public class RimLLMManager : IRimLLM
    {
        private static readonly Regex TrailingCommaRegex = new Regex(@",\s*([\]}])", RegexOptions.Compiled);
        private static readonly Regex JsonBlockRegex = new Regex(@"(\{.*\}|\[.*\])", RegexOptions.Compiled | RegexOptions.Singleline);

        private readonly IRimLLMSettings _settings;
        private readonly Dictionary<string, ILLMProvider> _providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Type> _registeredTypes = new HashSet<Type>();
        private readonly ConcurrentDictionary<Type, string> _sampleJsonCache = new ConcurrentDictionary<Type, string>();

        // 優先權佇列與限流屬性
        private readonly object _queueLock = new object();
        private readonly List<QueueEntry> _waitingQueue = new List<QueueEntry>();
        private int _activeRequests = 0;

        // 使用量統計日誌
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
        public readonly ConcurrentQueue<RequestLogEntry> RequestLogs = new ConcurrentQueue<RequestLogEntry>();

        // Circuit Breaker 健康狀態字典
        private class ProviderHealth
        {
            public int ContinuousFailures { get; set; }
            public DateTime CooldownEndTime { get; set; } = DateTime.MinValue;
        }
        private readonly ConcurrentDictionary<string, ProviderHealth> _providerHealth = new ConcurrentDictionary<string, ProviderHealth>(StringComparer.OrdinalIgnoreCase);
        private readonly object _healthLock = new object();

        // 佇列實體定義
        private class QueueEntry : IComparable<QueueEntry>
        {
            public LLMRequest Request { get; set; }
            public TaskCompletionSource<string> Tcs { get; set; }
            public Func<Task<string>> Action { get; set; }
            public DateTime EnqueueTime { get; set; } = DateTime.UtcNow;

            public int CompareTo(QueueEntry other)
            {
                // 優先級高（數值大）的排在前面
                int cmp = other.Request.Priority.CompareTo(this.Request.Priority);
                if (cmp == 0)
                {
                    // 優先級相同時，先入列的排在前面（FIFO）
                    return this.EnqueueTime.CompareTo(other.EnqueueTime);
                }
                return cmp;
            }
        }

        public RimLLMManager(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            RimLLMLog.Enabled = _settings.DetailedLogging;
            if (_settings is RimLLMFrameworkSettings frameworkSettings && frameworkSettings.RequestLogs != null)
            {
                foreach (var log in frameworkSettings.RequestLogs)
                {
                    RequestLogs.Enqueue(log);
                }
            }

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
            return EnqueueRequestAsync(request, () => GenerateInternalDirectAsync(request, callingAssembly, verifyCaller));
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
                bool shouldSkip = false;
                DateTime cdTime = DateTime.MinValue;
                int failures = 0;
                lock (_healthLock)
                {
                    var health = _providerHealth.GetOrAdd(providerId, _ => new ProviderHealth());
                    if (health.CooldownEndTime > DateTime.UtcNow)
                    {
                        shouldSkip = true;
                        cdTime = health.CooldownEndTime;
                        failures = health.ContinuousFailures;
                    }
                }
                if (shouldSkip)
                {
                    if (!AreAllEnabledProvidersInCooldown(fallbackChain))
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
                        lock (_healthLock)
                        {
                            if (_providerHealth.TryGetValue(providerId, out var successHealth))
                            {
                                successHealth.ContinuousFailures = 0;
                                successHealth.CooldownEndTime = DateTime.MinValue;
                            }
                        }

                        RecordLog(startTime, request.ModId, providerId, modelName, true, null, requestStopwatch.ElapsedMilliseconds);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;

                        // 記錄失敗並計算健康狀態
                        lock (_healthLock)
                        {
                            var failHealth = _providerHealth.GetOrAdd(providerId, _ => new ProviderHealth());
                            failHealth.ContinuousFailures++;
                            if (failHealth.ContinuousFailures >= 3)
                            {
                                int power = failHealth.ContinuousFailures - 3;
                                double cooldownSeconds = 60.0 * Math.Pow(2, Math.Min(power, 4));
                                failHealth.CooldownEndTime = DateTime.UtcNow.AddSeconds(cooldownSeconds);
                                RimLLMLog.Warning($"[RimLLM] Provider {providerId} has failed {failHealth.ContinuousFailures} times continuously. Cooldown set for {cooldownSeconds} seconds.");
                            }
                        }

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
            RecordLog(startTime, request.ModId, "FallbackChain", "None", false, lastException?.Message ?? "All fallbacks failed", totalStopwatch.ElapsedMilliseconds);
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
            string schemaInstructions = $"\n\n[CRITICAL REQUIREMENT: You MUST respond ONLY with a raw JSON object matching the following structure. Do not include markdown code block syntax (like ```json). Just the raw json text. Example:\n{GetSampleJson<T>()}]";
            
            string originalSystemPrompt = requestClone.SystemPrompt ?? "";
            requestClone.SystemPrompt = originalSystemPrompt + schemaInstructions;

            // 驗證呼叫者身份
            string rawResponse = await GenerateInternalAsync(requestClone, callingAssembly, verifyCaller: true).ConfigureAwait(false);
            
            // 執行 JSON 修復
            string repairedJson = RepairJson(rawResponse);

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
                    string fallbackExtracted = ExtractJsonBlock(repairedJson);
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
            await EnqueueRequestAsync(request, async () =>
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
                bool shouldSkip = false;
                DateTime cdTime = DateTime.MinValue;
                int failures = 0;
                lock (_healthLock)
                {
                    var health = _providerHealth.GetOrAdd(providerId, _ => new ProviderHealth());
                    if (health.CooldownEndTime > DateTime.UtcNow)
                    {
                        shouldSkip = true;
                        cdTime = health.CooldownEndTime;
                        failures = health.ContinuousFailures;
                    }
                }
                if (shouldSkip)
                {
                    if (!AreAllEnabledProvidersInCooldown(fallbackChain))
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

                    // 成功後重設健康狀態冷據
                    lock (_healthLock)
                    {
                        if (_providerHealth.TryGetValue(providerId, out var successHealth))
                        {
                            successHealth.ContinuousFailures = 0;
                            successHealth.CooldownEndTime = DateTime.MinValue;
                        }
                    }

                    RecordLog(startTime, request.ModId, providerId, modelName, true, null, requestStopwatch.ElapsedMilliseconds);
                    break;
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] Stream connection failed: {providerId} ({modelName}) -> {ex.Message}, trying next fallback.");
                    lastException = ex;

                    // 記錄失敗並計算健康狀態
                    lock (_healthLock)
                    {
                        var failHealth = _providerHealth.GetOrAdd(providerId, _ => new ProviderHealth());
                        failHealth.ContinuousFailures++;
                        if (failHealth.ContinuousFailures >= 3)
                        {
                            int power = failHealth.ContinuousFailures - 3;
                            double cooldownSeconds = 60.0 * Math.Pow(2, Math.Min(power, 4));
                            failHealth.CooldownEndTime = DateTime.UtcNow.AddSeconds(cooldownSeconds);
                            RimLLMLog.Warning($"[RimLLM] Provider {providerId} has failed {failHealth.ContinuousFailures} times continuously. Cooldown set for {cooldownSeconds} seconds.");
                        }
                    }
                }
            }

            if (!connected)
            {
                totalStopwatch.Stop();
                RecordLog(startTime, request.ModId, "FallbackChain", "None", false, lastException?.Message ?? "All fallbacks failed", totalStopwatch.ElapsedMilliseconds);
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
            GetSampleJson<T>();
            RimLLMLog.Message($"[RimLLM] Registered structured response type and finished cache pre-warmup: {type.FullName}");
        }

        #region Helper Methods

        private string GetSampleJson<T>()
        {
            return GetSampleJson(typeof(T));
        }

        private string GetSampleJson(Type type)
        {
            if (_sampleJsonCache.TryGetValue(type, out string json))
            {
                return json;
            }

            try
            {
                object instance = CreateDummyInstance(type);
                string generatedJson = JsonConvert.SerializeObject(instance, Formatting.None);
                _sampleJsonCache[type] = generatedJson;
                return generatedJson;
            }
            catch
            {
                return "{}";
            }
        }

        private static object CreateDummyInstance(Type type)
        {
            return CreateDummyInstance(type, new HashSet<Type>());
        }

        private static object CreateDummyInstance(Type type, HashSet<Type> visitedTypes)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return 0;
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return 0.0;
            if (type == typeof(bool)) return false;
            if (type.IsEnum)
            {
                var values = Enum.GetValues(type);
                return values.Length > 0 ? values.GetValue(0) : 0;
            }

            // 避免循環引用導致 StackOverflow
            if (visitedTypes.Contains(type))
            {
                return null;
            }
            visitedTypes.Add(type);

            try
            {
                if (type.IsArray)
                {
                    var elementType = type.GetElementType();
                    var array = Array.CreateInstance(elementType, 1);
                    array.SetValue(CreateDummyInstance(elementType, new HashSet<Type>(visitedTypes)), 0);
                    return array;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var elementType = type.GetGenericArguments()[0];
                    var listType = typeof(List<>).MakeGenericType(elementType);
                    var list = Activator.CreateInstance(listType) as System.Collections.IList;
                    if (list != null)
                    {
                        list.Add(CreateDummyInstance(elementType, new HashSet<Type>(visitedTypes)));
                    }
                    return list;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    var keyType = type.GetGenericArguments()[0];
                    var valueType = type.GetGenericArguments()[1];
                    var dictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
                    var dict = Activator.CreateInstance(dictType) as System.Collections.IDictionary;
                    if (dict != null)
                    {
                        var dummyKey = CreateDummyInstance(keyType, new HashSet<Type>(visitedTypes));
                        var dummyVal = CreateDummyInstance(valueType, new HashSet<Type>(visitedTypes));
                        if (dummyKey != null)
                        {
                            dict.Add(dummyKey, dummyVal);
                        }
                    }
                    return dict;
                }

                object instance = null;
                try
                {
                    instance = Activator.CreateInstance(type);
                }
                catch
                {
                    // 若無無參數建構子，使用 FormatterServices 進行安全實例化
                    instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
                }

                if (instance != null)
                {
                    // 遞迴填充公開欄位與屬性
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        try
                        {
                            field.SetValue(instance, CreateDummyInstance(field.FieldType, new HashSet<Type>(visitedTypes)));
                        }
                        catch {}
                    }
                    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (prop.CanWrite)
                        {
                            try
                            {
                                prop.SetValue(instance, CreateDummyInstance(prop.PropertyType, new HashSet<Type>(visitedTypes)), null);
                            }
                            catch {}
                        }
                    }
                }
                return instance;
            }
            catch
            {
                return null;
            }
        }

        private string RepairJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return json;

            json = json.Trim();

            // 0. 剝離 <think>...</think> 標籤及其內容，以避免結構化 JSON 解析失敗
            json = System.Text.RegularExpressions.Regex.Replace(json, @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();

            // 1. 移除 Markdown 標記
            if (json.StartsWith("```"))
            {
                int startIndex = json.IndexOf('\n');
                if (startIndex != -1)
                {
                    json = json.Substring(startIndex + 1);
                }
                else
                {
                    json = json.Substring(3);
                }
            }
            if (json.EndsWith("```"))
            {
                json = json.Substring(0, json.Length - 3);
            }
            json = json.Trim();

            // 2. 移除尾隨逗號 (使用編譯後的靜態 Regex 提效)
            json = TrailingCommaRegex.Replace(json, "$1");

            // 3. 補齊缺失括號 (跳過雙引號字串內部的字元)
            int braceCount = 0;
            int bracketCount = 0;
            bool inString = false;
            bool escapeNext = false;
            foreach (char c in json)
            {
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }
                if (c == '\\')
                {
                    escapeNext = true;
                    continue;
                }
                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }
                if (!inString)
                {
                    if (c == '{') braceCount++;
                    else if (c == '}') braceCount--;
                    else if (c == '[') bracketCount++;
                    else if (c == ']') bracketCount--;
                }
            }

            if (braceCount > 0)
            {
                json += new string('}', braceCount);
            }
            if (bracketCount > 0)
            {
                json += new string(']', bracketCount);
            }

            return json;
        }

        private string ExtractJsonBlock(string input)
        {
            var match = JsonBlockRegex.Match(input);
            if (match.Success)
            {
                return match.Value;
            }
            return input;
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
                if (providerId == "OpenRouter")
                {
                    modelName = "openrouter/auto";
                }

                if (string.IsNullOrEmpty(modelName))
                {
                    modelName = _settings.GetDefaultModel(providerId, "default");
                }
            }

            return true;
        }

        #endregion

        #region Concurrency Queue & Double-Repair Methods

        private async Task<string> EnqueueRequestAsync(LLMRequest request, Func<Task<string>> action)
        {
            var tcs = new TaskCompletionSource<string>();
            var entry = new QueueEntry
            {
                Request = request,
                Tcs = tcs,
                Action = action
            };

            // 如果一開始就被取消
            if (request.CancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(request.CancellationToken);
                return await tcs.Task;
            }

            CancellationTokenRegistration registration = default;
            if (request.CancellationToken != default)
            {
                registration = request.CancellationToken.Register(() =>
                {
                    lock (_queueLock)
                    {
                        if (_waitingQueue.Remove(entry))
                        {
                            tcs.TrySetCanceled(request.CancellationToken);
                        }
                    }
                });
            }

            lock (_queueLock)
            {
                _waitingQueue.Add(entry);
                _waitingQueue.Sort();
            }

            ProcessQueue();

            try
            {
                return await tcs.Task;
            }
            finally
            {
                registration.Dispose();
            }
        }

        private void ProcessQueue()
        {
            lock (_queueLock)
            {
                int limit = GetMaxConcurrentRequests();
                while (_activeRequests < limit && _waitingQueue.Count > 0)
                {
                    var entry = _waitingQueue[0];
                    _waitingQueue.RemoveAt(0);
                    _activeRequests++;

                    // 啟動排程非同步執行而不進行阻塞
                    _ = ExecuteQueuedRequestAsync(entry);
                }
            }
        }

        private async Task ExecuteQueuedRequestAsync(QueueEntry entry)
        {
            try
            {
                if (entry.Request.CancellationToken.IsCancellationRequested)
                {
                    entry.Tcs.TrySetCanceled(entry.Request.CancellationToken);
                    return;
                }

                string result = await entry.Action().ConfigureAwait(false);
                entry.Tcs.TrySetResult(result);
            }
            catch (OperationCanceledException ex)
            {
                entry.Tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                entry.Tcs.TrySetException(ex);
            }
            finally
            {
                lock (_queueLock)
                {
                    _activeRequests--;
                }
                ProcessQueue();
            }
        }

        private int GetMaxConcurrentRequests()
        {
            return _settings.MaxConcurrentRequests;
        }

        private static readonly List<string> HighLevelKeywords = new List<string>
        {
            "pro", "opus"
        };

        private static readonly List<string> MediumLevelKeywords = new List<string>
        {
            "mini", "flash", "sonnet", "deepseek",  "kimi", "minimax", "qwen"
        };

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

        private bool AreAllEnabledProvidersInCooldown(List<string> fallbackChain)
        {
            foreach (string entry in fallbackChain)
            {
                if (!ResolveFallbackEntry(entry, out string providerId, out string _)) continue;
                if (!_providers.TryGetValue(providerId, out _)) continue;
                if (!_settings.IsProviderEnabled(providerId)) continue;
                
                string apiKey = _settings.GetApiKey(providerId);
                if (string.IsNullOrEmpty(apiKey) && providerId != "OpenAICompatible") continue;

                bool inCd = false;
                lock (_healthLock)
                {
                    if (_providerHealth.TryGetValue(providerId, out var health))
                    {
                        if (health.CooldownEndTime > DateTime.UtcNow)
                        {
                            inCd = true;
                        }
                    }
                }
                if (!inCd)
                {
                    return false; // 有一個可用且不在冷卻中
                }
            }
            return true;
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

        private async Task<T> PerformDoubleRepairAsync<T>(LLMRequest originalRequest, string failedResponse, string errorMessage)
        {
            var repairRequest = new LLMRequest
            {
                ModId = originalRequest.ModId,
                Temperature = 0.1f, // 低隨機性有利於修復格式
                MaxTokens = originalRequest.MaxTokens,
                CancellationToken = originalRequest.CancellationToken,
                SystemPrompt = "You are a JSON repair assistant. The user will provide a JSON string that failed to parse, along with the parser error message. Your task is to output ONLY the corrected JSON string that is syntactically valid and contains all fields. Do NOT include markdown code blocks (like ```json), explanations, or any other text.",
                Prompt = $"Failed JSON:\n{failedResponse}\n\nParser Error:\n{errorMessage}\n\nTarget Structure Sample:\n{GetSampleJson<T>()}\n\nPlease output the repaired JSON string:"
            };

            string repairResponse = await GenerateInternalDirectAsync(repairRequest, null, verifyCaller: false).ConfigureAwait(false);
            string repairedJson = RepairJson(repairResponse);
            
            return JsonConvert.DeserializeObject<T>(repairedJson);
        }

        private static DateTime _lastLogWriteTime = DateTime.MinValue;
        private static readonly object LogLock = new object();

        private void RecordLog(DateTime startTime, string modId, string provider, string model, bool success, string err, long latency)
        {
            var entry = new RequestLogEntry
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
                        frameworkSettings.RequestLogs = new List<RequestLogEntry>(RequestLogs.ToArray());
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

        #endregion
    }
}
