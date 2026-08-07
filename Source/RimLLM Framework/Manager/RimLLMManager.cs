using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;
using RimLLM_Framework.Mod;
using Verse;
using RimWorld;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// IRimLLM 介面的核心管理器實作。
    /// 統一調度 API 供應商、執行雙重 Fallback 容錯、校驗調用者來源。
    /// 內部邏輯委託給排隊佇列 (RequestQueue)、熔斷器 (CircuitBreaker)、JSON 輔助 (JsonHelper) 與使用統計器 (UsageTracker)。
    /// </summary>
    public class RimLLMManager
    {
        private readonly IRimLLMSettings _settings;
        private readonly Dictionary<string, ILLMProvider> _providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _providerOrder = new List<string>();
        private readonly HashSet<string> _builtInProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _providerLock = new object();

        private readonly RimLLMRequestQueue _requestQueue;
        private readonly RimLLMCircuitBreaker _circuitBreaker;
        private readonly RimLLMUsageTracker _usageTracker;
        private readonly RimLLMEmbeddingService _embeddingService;

        public RimLLMEmbeddingService EmbeddingService => _embeddingService;
        public RimLLMUsageTracker UsageTracker => _usageTracker;

        // Anti-abuse state
        private readonly ConcurrentDictionary<string, List<DateTime>> RequestTimestamps = 
            new ConcurrentDictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> CoolDownUntil = 
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Budget Dialog state
        private string _budgetApprovalDate = "";
        private string _budgetDeclineDate = "";
        private readonly object BudgetPromptLock = new object();
        private TaskCompletionSource<bool> _activePromptTcs = null;

        /// <summary>
        /// 預算詢問對話框的最長等待時間。逾時視為拒絕，避免請求無限期佔用資源。
        /// </summary>
        private const int BudgetPromptTimeoutSeconds = 120;

        // Smart Routing state
        private struct ResolvedCandidate
        {
            public string Entry;
            public string ProviderId;
            public ILLMProvider Provider;
            public string ModelName;
        }

        private readonly ConcurrentDictionary<string, List<long>> ProviderLatencies = 
            new ConcurrentDictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> ProviderFailCooldowns = 
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

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
            RegisterBuiltInProvider(new GrokProvider(settings));
            RegisterBuiltInProvider(new OpenRouterProvider(settings));
            RegisterBuiltInProvider(new KimiProvider(settings));
            RegisterBuiltInProvider(new MiniMaxProvider(settings));
            RegisterBuiltInProvider(new QwenProvider(settings));
            RegisterBuiltInProvider(new NvidiaProvider(settings));
            RegisterBuiltInProvider(new ZaiProvider(settings));

            _embeddingService = new RimLLMEmbeddingService(settings);
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
        /// 取得指定供應商的能力描述。
        /// 未實作能力介面的舊版／自訂供應商會回傳保守的相容能力，確保既有 Provider 不需改版。
        /// </summary>
        /// <param name="providerId">供應商識別碼。</param>
        /// <returns>供應商能力；找不到供應商時回傳所有能力為 false 的物件。</returns>
        public LLMProviderCapabilities GetProviderCapabilities(string providerId)
        {
            if (string.IsNullOrEmpty(providerId))
            {
                return new LLMProviderCapabilities();
            }

            lock (_providerLock)
            {
                if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
                {
                    return new LLMProviderCapabilities();
                }

                if (provider is IChatClientProvider chatClientProvider && chatClientProvider.Capabilities != null)
                {
                    return chatClientProvider.Capabilities;
                }

                // 舊版 ILLMProvider 至少已實作既有串流介面；原生 Schema／Usage 仍視為不支援。
                return new LLMProviderCapabilities
                {
                    SupportsStreaming = true
                };
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



        /// <summary>
        /// 包裝排隊佇列的 GenerateInternalAsync。
        /// </summary>
        private async Task<RimLLMGenerationResult> GenerateInternalAsync(RimLLMRequest request, Assembly callingAssembly, bool verifyCaller)
        {
            RimLLMRequest normalizedRequest = NormalizeRequest(request, _settings);

            // 准入檢查一律在進入佇列之前執行，且整條請求路徑只執行一次。
            // 特別是預算對話框：若在佇列委派內等待，會持續佔用一個並行名額。
            if (await RunAdmissionChecksAsync(normalizedRequest, callingAssembly, verifyCaller).ConfigureAwait(false)
                is string mockResult)
            {
                return new RimLLMGenerationResult { Text = mockResult };
            }

            return await _requestQueue.EnqueueRequestAsync(normalizedRequest, () => GenerateInternalDirectAsync(normalizedRequest)).ConfigureAwait(false);
        }

        /// <summary>
        /// 執行呼叫端校驗、防濫用與預算檢查。
        /// 若預算政策指示以模擬回應取代真實請求，回傳該模擬字串；否則回傳 null 代表可繼續。
        /// </summary>
        private async Task<string> RunAdmissionChecksAsync(RimLLMRequest request, Assembly callingAssembly, bool verifyCaller)
        {
            // 1. 來源身分安全校驗 (Caller Verification)
            VerifyCallerOrThrow(request, callingAssembly, verifyCaller);

            // Anti-abuse check
            if (_settings.EnableAntiAbuse)
            {
                CheckAntiAbuse(request.ModId);
            }

            // Budget check
            bool budgetOk = await CheckBudgetLimitAsync(request).ConfigureAwait(false);
            if (!budgetOk)
            {
                throw new RimLLMException(LLMError.QuotaExceeded, "Daily budget limit exceeded.");
            }

            // Check if budget mocked
            return IsBudgetMocked(request, out string mockResult) ? mockResult : null;
        }

        /// <summary>建立綁定指定 Mod 的 IChatClient facade。呼叫端 assembly 須已註冊該 Mod。</summary>
        /// <summary>內部存取目前設定（供 facade 的結構化輸出流程使用）。</summary>
        internal IRimLLMSettings Settings => _settings;

        internal RimLLMChatClient CreateChatClient(string modId, Assembly callingAssembly)
        {
            if (string.IsNullOrEmpty(modId))
            {
                throw new ArgumentException("ModId cannot be empty or null", nameof(modId));
            }
            if (!ClientRegistry.Verify(modId, callingAssembly))
            {
                throw new RimLLMException(LLMError.InvalidKey, $"[RimLLM] Caller verification failed. Assembly verification for ModId '{modId}' did not pass.");
            }
            return new RimLLMChatClient(this, modId);
        }

        /// <summary>建立綁定指定 Mod 的 embedding generator。呼叫端 assembly 須已註冊該 Mod。</summary>
        internal RimLLMEmbeddingClient CreateEmbeddingGenerator(string modId, Assembly callingAssembly)
        {
            if (string.IsNullOrEmpty(modId))
            {
                throw new ArgumentException("ModId cannot be empty or null", nameof(modId));
            }
            if (!ClientRegistry.Verify(modId, callingAssembly))
            {
                throw new RimLLMException(LLMError.InvalidKey, $"[RimLLM] Caller verification failed. Assembly verification for ModId '{modId}' did not pass.");
            }
            return new RimLLMEmbeddingClient(this, modId);
        }

        /// <summary>執行指定 Mod 的防濫用檢查（供 embedding facade 於每次呼叫時使用）。</summary>
        internal void CheckAntiAbuseForMod(string modId)
        {
            if (_settings.EnableAntiAbuse)
            {
                CheckAntiAbuse(modId);
            }
        }

        /// <summary>
        /// 真正的非同步生成文字邏輯。呼叫前必須已通過 RunAdmissionChecksAsync。
        /// </summary>
        private Task<RimLLMGenerationResult> GenerateInternalDirectAsync(RimLLMRequest request)
        {
            // 1.5 檢查是否啟用串流輸出，若是則改走串流路徑（沿用舊版行為）。
            if (request.EnableStreaming)
            {
                return StreamInternalDirectAsync(
                    request,
                    chunk => DispatchChunk(request.OnChunkReceived, chunk));
            }

            // 交由共用的 Fallback Chain 執行核心處理
            return ExecuteWithFallbackAsync(
                request,
                (provider, modelName) => GenerateProviderAsync(provider, request, modelName),
                LLMError.Unknown,
                "All fallback attempts failed.");
        }



        /// <summary>
        /// 結構化輸出的核心流程：直接解析 → JSON repair 回退 → LLM-assisted double-repair。
        /// 供 facade（RimLLMChatClient.GenerateObjectAsync）與既有 GenerateObjectAsync 共用。
        /// </summary>
        internal T DeserializeStructured<T>(string rawResponse, IRimLLMSettings settings, RimLLMRequest request)
        {
            try
            {
                // 原生 schema provider 的回應先直接解析；只有解析失敗才進入 repair fallback。
                return DeserializeAndValidate<T>(rawResponse);
            }
            catch (Exception ex)
            {
                if (!settings.EnableJsonRepair)
                {
                    throw new RimLLMException(
                        LLMError.InvalidResponse, 
                        $"Unable to parse LLM response to target object {typeof(T).Name} (JSON Repair is disabled). Raw Response: {RimLLMLog.SanitizeForLog(rawResponse, 300)}. Parse error: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", 
                        ex);
                }

                string repairedJson = RimLLMJsonHelper.RepairJson(rawResponse);
                RimLLMLog.Warning($"[RimLLM] First JSON parse failed, attempting fallback repair. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}\nRepaired preview: {RimLLMLog.SanitizeForLog(repairedJson, 300)}\nError: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
                try
                {
                    string fallbackExtracted = RimLLMJsonHelper.ExtractJsonBlock(repairedJson);
                    return DeserializeAndValidate<T>(fallbackExtracted);
                }
                catch
                {
                    // 二次修復 (Double-Repair)
                    RimLLMLog.Message($"[RimLLM] Static JSON repair failed. Initiating Double-Repair (LLM-assisted repair)...");
                    try
                    {
                        T repairedObj = PerformDoubleRepairAsync<T>(request, rawResponse, ex.Message).GetAwaiter().GetResult();
                        ValidateStructuredObject(repairedObj);
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

        internal static T DeserializeAndValidate<T>(string json)
        {
            T result = JsonConvert.DeserializeObject<T>(json);
            ValidateStructuredObject(result);
            return result;
        }

        private static void ValidateStructuredObject<T>(T result)
        {
            if (ReferenceEquals(result, null))
            {
                throw new InvalidOperationException("Structured response deserialized to null.");
            }

            ValidateRequiredMembers(result, typeof(T), new HashSet<Type>());
        }

        private static void ValidateRequiredMembers(object value, Type type, HashSet<Type> visitedTypes)
        {
            if (value == null || type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal))
            {
                return;
            }
            if (!visitedTypes.Add(type))
            {
                return;
            }

            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable != null && type != typeof(string))
            {
                foreach (object item in enumerable)
                {
                    if (item != null)
                    {
                        ValidateRequiredMembers(item, item.GetType(), visitedTypes);
                    }
                }
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object memberValue = property.GetValue(value, null);
                if (memberValue == null && IsNullableReferenceOrNullableValue(property.PropertyType))
                {
                    throw new InvalidOperationException($"Required structured response member '{property.Name}' is null.");
                }
                if (memberValue != null)
                {
                    ValidateRequiredMembers(memberValue, property.PropertyType, visitedTypes);
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsLiteral || field.IsInitOnly)
                {
                    continue;
                }

                object memberValue = field.GetValue(value);
                if (memberValue == null && IsNullableReferenceOrNullableValue(field.FieldType))
                {
                    throw new InvalidOperationException($"Required structured response member '{field.Name}' is null.");
                }
                if (memberValue != null)
                {
                    ValidateRequiredMembers(memberValue, field.FieldType, visitedTypes);
                }
            }
        }

        private static bool IsNullableReferenceOrNullableValue(Type type)
        {
            return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
        }

        /// <summary>




        /// <summary>
        /// SDK facade 的非串流唯一入口（回傳包含實際 provider/model 與用量的結果）。
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal Task<RimLLMGenerationResult> GenerateResultAsync(
            RimLLMRequest request,
            Assembly callingAssembly = null,
            bool verifyCaller = true)
        {
            return GenerateInternalAsync(request, callingAssembly, verifyCaller);
        }

        /// <summary>
        /// SDK facade 的串流唯一入口。串流 chunk 經 <paramref name="onChunkReceived"/> 送出。
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal Task<RimLLMGenerationResult> StreamResultAsync(
            RimLLMRequest request,
            Action<string> onChunkReceived,
            Assembly callingAssembly = null,
            bool verifyCaller = true)
        {
            return StreamInternalAsync(request, onChunkReceived, callingAssembly, verifyCaller);
        }

        /// <summary>
        /// 包裝排隊佇列的 StreamInternalAsync。
        /// </summary>
        private async Task<RimLLMGenerationResult> StreamInternalAsync(
            RimLLMRequest request,
            Action<string> onChunkReceived,
            Assembly callingAssembly,
            bool verifyCaller)
        {
            RimLLMRequest normalizedRequest = NormalizeRequest(request, _settings);

            // 與 GenerateInternalAsync 一致：准入檢查在佇列之前執行且只執行一次。
            if (await RunAdmissionChecksAsync(normalizedRequest, callingAssembly, verifyCaller).ConfigureAwait(false)
                is string mockResult)
            {
                DispatchChunk(onChunkReceived, mockResult);
                return new RimLLMGenerationResult { Text = mockResult };
            }

            Action<string> mainThreadCallback = chunk => DispatchChunk(onChunkReceived, chunk);
            return await _requestQueue.EnqueueRequestAsync(normalizedRequest, () =>
                StreamInternalDirectAsync(normalizedRequest, mainThreadCallback)).ConfigureAwait(false);
        }

        /// <summary>
        /// 真正的非同步串流生成邏輯。呼叫前必須已通過 RunAdmissionChecksAsync。
        /// 與非串流路徑共用相同的 Fallback Chain 執行核心，因此重試與熔斷行為一致。
        /// 回傳成功那次嘗試所累積的完整文字。
        /// </summary>
        private async Task<RimLLMGenerationResult> StreamInternalDirectAsync(RimLLMRequest request, Action<string> onChunkReceived)
        {
            // 累積器由每次 attempt 各自擁有：若沿用同一個緩衝，
            // 「先吐出部分內容再失敗」的 provider 會讓殘留文字混進下一次嘗試的結果。
            var sink = new StreamAttemptSink(onChunkReceived, request.OnStreamRestart, DispatchRestart);

            await ExecuteWithFallbackAsync(
                request,
                (provider, modelName) => StreamProviderAsync(provider, request, modelName, sink.Append),
                LLMError.ProviderOffline,
                "All fallback attempts failed, unable to establish stream connection.",
                onAttemptStarting: sink.BeginAttempt).ConfigureAwait(false);

            return new RimLLMGenerationResult { Text = sink.Result };
        }

        private async Task<RimLLMGenerationResult> GenerateProviderAsync(ILLMProvider provider, RimLLMRequest request, string model)
        {
            if (provider is IChatClientProvider chatProvider && chatProvider.UsesIChatClient)
            {
                bool useNativeSchema = request.ResponseType != null &&
                                        _settings.EnableNativeSchema &&
                                        chatProvider.Capabilities.SupportsNativeStructuredOutput;
                if (useNativeSchema)
                {
                    try
                    {
                        if (provider is INativeStructuredOutputProvider nativeProvider)
                        {
                            var nativeMessages = RimLLMChatClientExecutor.BuildMessages(request);
                            var nativeOptions = RimLLMChatClientExecutor.BuildOptions(request, model, useNativeSchema: true, ResolveChatOptionsCustomizer(provider, request, model));
                            if (request.ResponseType != null)
                            {
                                nativeOptions.AdditionalProperties["rimllm_response_schema"] =
                                    RimLLMJsonHelper.GenerateJsonSchema(request.ResponseType, uppercaseTypes: false).ToString();
                            }
                            string nativeText = await nativeProvider.GenerateStructuredAsync(
                                nativeMessages,
                                nativeOptions,
                                model).ConfigureAwait(false);
                            return new RimLLMGenerationResult
                            {
                                Text = nativeText,
                                ProviderId = provider.ProviderId,
                                ModelName = model
                            };
                        }

                        using (IChatClient nativeClient = chatProvider.CreateChatClient(model))
                        {
                            return await RimLLMChatClientExecutor.GenerateAsync(
                                nativeClient,
                                request,
                                model,
                                useNativeSchema: true,
                                provider.ProviderId,
                                _settings.ApiTimeout,
                                ResolveChatOptionsCustomizer(provider, request, model)).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (IsNativeSchemaRejected(ex))
                    {
                        // 服務拒絕原生 schema 時，降級至既有提示式 JSON 與 repair fallback。
                        return await GenerateWithoutNativeSchemaAsync(provider, request, model).ConfigureAwait(false);
                    }
                }

                using (IChatClient client = chatProvider.CreateChatClient(model))
                {
                    // IChatClient 供應商若不支援原生 schema，仍要沿用既有的提示式 JSON fallback。
                    RimLLMRequest providerRequest = PrepareRequestForProvider(provider, request);
                    return await RimLLMChatClientExecutor.GenerateAsync(
                        client,
                        providerRequest,
                        model,
                        useNativeSchema: false,
                        provider.ProviderId,
                        _settings.ApiTimeout,
                        ResolveChatOptionsCustomizer(provider, request, model)).ConfigureAwait(false);
                }
            }

            RimLLMRequest prepReq = PrepareRequestForProvider(provider, request);
            var nonChatMessages = RimLLMChatClientExecutor.BuildMessages(prepReq);
            var nonChatOptions = RimLLMChatClientExecutor.BuildOptions(prepReq, model, useNativeSchema: false, ResolveChatOptionsCustomizer(provider, request, model));
            if (prepReq.ResponseType != null)
            {
                nonChatOptions.AdditionalProperties["rimllm_response_schema"] =
                    RimLLMJsonHelper.GenerateJsonSchema(prepReq.ResponseType, uppercaseTypes: false).ToString();
            }
            string text = await provider.GenerateAsync(
                nonChatMessages,
                nonChatOptions,
                model).ConfigureAwait(false);
            return new RimLLMGenerationResult
            {
                Text = text,
                ProviderId = provider.ProviderId,
                ModelName = model
            };
        }

        private async Task<RimLLMGenerationResult> StreamProviderAsync(
            ILLMProvider provider,
            RimLLMRequest request,
            string model,
            Action<string> onChunkReceived)
        {
            if (provider is IChatClientProvider chatProvider && chatProvider.UsesIChatClient)
            {
                using (IChatClient client = chatProvider.CreateChatClient(model))
                {
                    // IChatClient 供應商若不支援原生 schema，仍要沿用既有的提示式 JSON fallback。
                    RimLLMRequest providerRequest = PrepareRequestForProvider(provider, request);
                    return await RimLLMChatClientExecutor.StreamAsync(
                        client,
                        providerRequest,
                        model,
                        request.ResponseType != null &&
                        _settings.EnableNativeSchema &&
                        chatProvider.Capabilities.SupportsNativeStructuredOutput,
                        provider.ProviderId,
                        onChunkReceived,
                        _settings.ApiTimeout,
                        ResolveChatOptionsCustomizer(provider, request, model)).ConfigureAwait(false);
                }
            }

            RimLLMRequest prepStreamReq = PrepareRequestForProvider(provider, request);
            var streamMessages = RimLLMChatClientExecutor.BuildMessages(prepStreamReq);
            var streamOptions = RimLLMChatClientExecutor.BuildOptions(prepStreamReq, model, useNativeSchema: false, ResolveChatOptionsCustomizer(provider, request, model));
            if (prepStreamReq.ResponseType != null)
            {
                streamOptions.AdditionalProperties["rimllm_response_schema"] =
                    RimLLMJsonHelper.GenerateJsonSchema(prepStreamReq.ResponseType, uppercaseTypes: false).ToString();
            }
            await provider.StreamAsync(
                streamMessages,
                streamOptions,
                model,
                onChunkReceived).ConfigureAwait(false);
            return new RimLLMGenerationResult
            {
                ProviderId = provider.ProviderId,
                ModelName = model
            };
        }

        private async Task<RimLLMGenerationResult> GenerateWithoutNativeSchemaAsync(
            ILLMProvider provider,
            RimLLMRequest request,
            string model)
        {
            RimLLMRequest fallbackRequest = PrepareRequestForProvider(
                provider,
                request,
                forceJsonFallback: true);

            if (provider is IChatClientProvider chatProvider && chatProvider.UsesIChatClient)
            {
                using (IChatClient client = chatProvider.CreateChatClient(model))
                {
                    return await RimLLMChatClientExecutor.GenerateAsync(
                        client,
                        fallbackRequest,
                        model,
                        useNativeSchema: false,
                        provider.ProviderId,
                        _settings.ApiTimeout,
                        ResolveChatOptionsCustomizer(provider, request, model)).ConfigureAwait(false);
                }
            }

            var fallbackMessages = RimLLMChatClientExecutor.BuildMessages(fallbackRequest);
            var fallbackOptions = RimLLMChatClientExecutor.BuildOptions(fallbackRequest, model, useNativeSchema: false, ResolveChatOptionsCustomizer(provider, request, model));
            if (fallbackRequest.ResponseType != null)
            {
                fallbackOptions.AdditionalProperties["rimllm_response_schema"] =
                    RimLLMJsonHelper.GenerateJsonSchema(fallbackRequest.ResponseType, uppercaseTypes: false).ToString();
            }
            string text = await provider.GenerateAsync(
                fallbackMessages,
                fallbackOptions,
                model).ConfigureAwait(false);
            return new RimLLMGenerationResult
            {
                Text = text,
                ProviderId = provider.ProviderId,
                ModelName = model
            };
        }

        /// <summary>
        /// 將供應商實作的 IChatOptionsCustomizer 轉為 executor 用的 delegate；
        /// 未實作時回傳 null，executor 便只套用基礎選項。
        /// </summary>
        private static Action<ChatOptions> ResolveChatOptionsCustomizer(ILLMProvider provider, RimLLMRequest request, string model)
        {
            if (provider is IChatOptionsCustomizer customizer)
            {
                var options = RimLLMChatClientExecutor.BuildOptions(request, model, useNativeSchema: false, customizeOptions: null);
                return customizer.CreateChatOptionsCustomizer(options, model);
            }
            return null;
        }



        private static bool IsNativeSchemaRejected(Exception exception)
        {
            if (exception == null || exception is OperationCanceledException)
            {
                return false;
            }

            // 只有 provider 明確標記為 schema 拒絕的錯誤才觸發降級重打。
            // 先前的實作把「任何巢狀 InvalidResponse」都視為 schema 拒絕，
            // 會讓空回應等真正的失敗被誤判並靜默重打，掩蓋真實錯誤。
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is RimLLMException rimException && rimException.IsSchemaRejection)
                {
                    return true;
                }
            }

            string message = exception.ToString().ToLowerInvariant();
            bool mentionsSchema = message.Contains("schema") ||
                                  message.Contains("response_format") ||
                                  message.Contains("response format") ||
                                  message.Contains("structured output");
            bool looksLikeRejection = message.Contains("400") ||
                                      message.Contains("invalid") ||
                                      message.Contains("unsupported") ||
                                      message.Contains("not support") ||
                                      message.Contains("unrecognized");
            return mentionsSchema && looksLikeRejection;
        }

        private RimLLMRequest PrepareRequestForProvider(
            ILLMProvider provider,
            RimLLMRequest request,
            bool forceJsonFallback = false)
        {
            if (request.ResponseType == null ||
                (!forceJsonFallback && IsNativeStructuredProvider(provider)))
            {
                return request;
            }

            // 非原生 schema 供應商，或原生 schema 被拒絕時，使用既有提示式 JSON fallback。
            RimLLMRequest clone = request.Clone();
            string originalSystemPrompt = clone.SystemPrompt ?? string.Empty;
            string schemaInstructions =
                "\n\n[結構化輸出要求：只能回傳符合下列結構的原始 JSON，不要加入 Markdown code fence 或其他說明。範例：\n" +
                RimLLMJsonHelper.GetSampleJson(request.ResponseType) + "]";
            clone.SystemPrompt = originalSystemPrompt + schemaInstructions;
            if (clone.Messages != null && clone.Messages.Count > 0)
            {
                var messagesCopy = new List<ChatMessage>(clone.Messages);
                int sysIdx = messagesCopy.FindIndex(m => m.Role == ChatRole.System);
                if (sysIdx >= 0)
                {
                    messagesCopy[sysIdx] = new ChatMessage(ChatRole.System, clone.SystemPrompt);
                }
                else
                {
                    messagesCopy.Insert(0, new ChatMessage(ChatRole.System, clone.SystemPrompt));
                }
                clone.Messages = messagesCopy;
            }
            return clone;
        }

        private bool IsNativeStructuredProvider(ILLMProvider provider)
        {
            return provider is IChatClientProvider chatProvider &&
                   chatProvider.UsesIChatClient &&
                   chatProvider.Capabilities.SupportsNativeStructuredOutput &&
                   _settings.EnableNativeSchema;
        }

        /// <summary>
        /// 共用的 Fallback Chain 執行核心。
        /// 依序遍歷符合資格的供應商條目，對每個條目套用相同的重試策略，
        /// 並統一處理取消檢查、熔斷記錄與用量統計。
        /// </summary>
        private async Task<RimLLMGenerationResult> ExecuteWithFallbackAsync(
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
                if (prefProvider != null && TryGetProvider(prefProvider, out ILLMProvider prefProviderInstance)
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
                bool candidateSuccess = false;
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
                        candidateSuccess = true;
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

                if (!candidateSuccess && isRetryableFailure)
                {
                    // 只有在因為網路或暫時性錯誤（可重試錯誤）導致失敗時，才置入冷卻阻斷期
                    ProviderFailCooldowns[providerId] = DateTime.UtcNow.AddSeconds(60);
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
        private bool TryGetEligibleCandidate(string entry, List<string> fallbackChain, RimLLMRequest request, out string providerId, out ILLMProvider provider, out string modelName)
        {
            provider = null;

            if (!ResolveFallbackEntry(entry, out providerId, out modelName))
                return false;

            if (!TryGetProvider(providerId, out provider))
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
        private static void VerifyCallerOrThrow(RimLLMRequest request, Assembly callingAssembly, bool verifyCaller)
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



        #region Helper Methods

        private List<string> GetFallbackChainSnapshot()
        {
            var chain = _settings.FallbackChain;
            return chain != null ? new List<string>(chain) : null;
        }

        private static RimLLMRequest NormalizeRequest(RimLLMRequest request, IRimLLMSettings settings)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.ReasoningEffort.HasValue || settings.DefaultReasoningEffort == null)
            {
                return request;
            }

            var clone = request.Clone();
            clone.ReasoningEffort = settings.DefaultReasoningEffort;
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

        private void DispatchChunk(Action<string> callback, string chunk)
        {
            if (callback == null) return;
            RimLLMDispatcher.EnqueueOnMainThread(() => callback(chunk));
        }

        private void DispatchRestart(Action callback)
        {
            if (callback == null) return;
            RimLLMDispatcher.EnqueueOnMainThread(callback);
        }

        /// <summary>
        /// 串流的單次嘗試累積器。
        /// 每次 fallback 或重試開始前會清空緩衝，確保回傳結果只包含成功那次嘗試的內容；
        /// 若前一次嘗試已經吐出過 chunk，會通知呼叫端重設自己的顯示緩衝。
        /// </summary>
        private sealed class StreamAttemptSink
        {
            private readonly StringBuilder _buffer = new StringBuilder();
            private readonly Action<string> _forward;
            private readonly Action _onRestart;
            private readonly Action<Action> _dispatchRestart;
            private bool _emittedAnything;

            public StreamAttemptSink(Action<string> forward, Action onRestart, Action<Action> dispatchRestart)
            {
                _forward = forward;
                _onRestart = onRestart;
                _dispatchRestart = dispatchRestart;
            }

            public string Result => _buffer.ToString();

            public void BeginAttempt()
            {
                if (_emittedAnything)
                {
                    // 上一次嘗試已經送出過內容，通知呼叫端捨棄那段殘留。
                    _dispatchRestart?.Invoke(_onRestart);
                }
                _buffer.Length = 0;
                _emittedAnything = false;
            }

            public void Append(string chunk)
            {
                if (string.IsNullOrEmpty(chunk)) return;
                _buffer.Append(chunk);
                _emittedAnything = true;
                _forward?.Invoke(chunk);
            }
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

        internal async Task<T> PerformDoubleRepairAsync<T>(RimLLMRequest originalRequest, string failedResponse, string errorMessage)
        {
            var repairRequest = new RimLLMRequest
            {
                ModId = originalRequest.ModId,
                Temperature = 0.1f, // 低隨機性有利於修復格式
                MaxOutputTokens = originalRequest.MaxOutputTokens,
                CancellationToken = originalRequest.CancellationToken,
                DisableReasoning = true, // 對應舊 LLMReasoningEffort.None
                Messages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, "You are a JSON repair assistant. The user will provide a JSON string that failed to parse, along with the parser error message. Your task is to output ONLY the corrected JSON string that is syntactically valid and contains all fields. Do NOT include markdown code blocks (like ```json), explanations, or any other text."),
                    new ChatMessage(ChatRole.User, $"Failed JSON:\n{failedResponse}\n\nParser Error:\n{errorMessage}\n\nTarget Structure Sample:\n{RimLLMJsonHelper.GetSampleJson<T>()}\n\nPlease output the repaired JSON string:")
                }
            };

            // 修復重打屬於同一次邏輯請求，不再重跑呼叫端校驗、防濫用與預算檢查，
            // 避免同一次使用者操作被扣兩次額度。
            string repairResponse = (await GenerateInternalDirectAsync(repairRequest).ConfigureAwait(false)).Text;
            string repairedJson = RimLLMJsonHelper.RepairJson(repairResponse);
            
            return JsonConvert.DeserializeObject<T>(repairedJson);
        }

        public void ClearLogs()
        {
            _usageTracker.ClearLogs();
        }

        public void ClearCooldowns()
        {
            ProviderFailCooldowns.Clear();
            ProviderLatencies.Clear();
            RequestTimestamps.Clear();
            CoolDownUntil.Clear();
        }

        public void RecordUsage(string providerId, string modelName, int promptTokens, int completionTokens, int cachedPromptTokens = 0)
        {
            _usageTracker.RecordUsage(providerId, modelName, promptTokens, completionTokens, cachedPromptTokens);
        }

        public void ResetUsage()
        {
            _usageTracker.ResetUsage();
        }

        private void CheckAntiAbuse(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return;
            
            DateTime now = DateTime.UtcNow;
            if (CoolDownUntil.TryGetValue(modId, out DateTime cdTime) && now < cdTime)
            {
                throw new RimLLMException(LLMError.RateLimit, $"[RimLLM] Mod '{modId}' is in anti-abuse cooldown until {cdTime.ToLocalTime()}.");
            }

            var list = RequestTimestamps.GetOrAdd(modId, _ => new List<DateTime>());
            lock (list)
            {
                DateTime limit = now.AddSeconds(-_settings.ThrottlingWindowSeconds);
                list.RemoveAll(t => t < limit);
                list.Add(now);

                if (list.Count > _settings.MaxRequestsPerWindow)
                {
                    DateTime cdUntil = now.AddSeconds(_settings.CoolDownDurationSeconds);
                    CoolDownUntil[modId] = cdUntil;
                    RimLLMLog.Warning($"[RimLLM] Mod '{modId}' triggered anti-abuse throttling limit. Cooling down until {cdUntil.ToLocalTime()}.");
                    throw new RimLLMException(LLMError.RateLimit, $"[RimLLM] Mod '{modId}' triggered anti-abuse throttling limit. Cooling down until {cdUntil.ToLocalTime()}.");
                }
            }
        }

        private async Task<bool> CheckBudgetLimitAsync(RimLLMRequest request)
        {
            _usageTracker.CheckDailyReset();
            
            if (_settings.DailyBudgetLimit <= 0f || _settings.DailyAccumulatedCost < _settings.DailyBudgetLimit)
            {
                return true; // Budget is fine
            }

            string todayStr = DateTime.Today.ToString("yyyy-MM-dd");

            // 1. Check if already approved/declined today
            if (_budgetApprovalDate == todayStr)
            {
                return true; // Bypassed
            }
            if (_budgetDeclineDate == todayStr)
            {
                return false; // Blocked
            }

            // 2. Check if we should fall back to free (handled inside TryGetEligibleCandidate)
            if (_settings.BudgetPolicy == 2) // FallbackToFree
            {
                return true; 
            }

            if (_settings.BudgetPolicy == 0) // HardBlock
            {
                return false;
            }

            if (_settings.BudgetPolicy == 1) // SilentMocking
            {
                return true; // Handled separately via IsBudgetMocked
            }

            if (_settings.BudgetPolicy == 3) // DialogPrompt
            {
                if (Find.WindowStack == null)
                {
                    return false;
                }

                TaskCompletionSource<bool> tcs = null;
                lock (BudgetPromptLock)
                {
                    if (_activePromptTcs != null)
                    {
                        tcs = _activePromptTcs;
                    }
                    else
                    {
                        // RunContinuationsAsynchronously：避免按鈕 callback 在 Unity 主執行緒上
                        // 同步跑完整條後續請求鏈而卡住畫面。
                        tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _activePromptTcs = tcs;

                        // Show the dialog on main thread
                        RimLLMDispatcher.EnqueueOnMainThread(() =>
                        {
                            var dialog = new Dialog_BudgetPrompt(
                                "RimLLM_BudgetExceededPrompt".Translate(_settings.DailyAccumulatedCost.ToString("F4"), _settings.DailyBudgetLimit.ToString("F2")),
                                "RimLLM_BudgetExceededPrompt_Approve".Translate(), () =>
                                {
                                    lock (BudgetPromptLock)
                                    {
                                        _budgetApprovalDate = todayStr;
                                        _activePromptTcs = null;
                                    }
                                    // TrySetResult：按鈕可能與逾時、視窗關閉競爭，SetResult 會拋例外到主執行緒。
                                    tcs.TrySetResult(true);
                                },
                                "RimLLM_BudgetExceededPrompt_Decline".Translate(), () =>
                                {
                                    lock (BudgetPromptLock)
                                    {
                                        _budgetDeclineDate = todayStr;
                                        _activePromptTcs = null;
                                    }
                                    tcs.TrySetResult(false);
                                },
                                // 視窗被 ESC 或其他方式關閉時的收尾：若沒有這條，TCS 永遠不會完成，
                                // 且 _activePromptTcs 會永久卡住，之後所有請求都會掛在同一個死 TCS 上。
                                // 此處刻意不寫入 _budgetDeclineDate，讓下次請求能重新詢問。
                                () =>
                                {
                                    lock (BudgetPromptLock)
                                    {
                                        if (ReferenceEquals(_activePromptTcs, tcs))
                                        {
                                            _activePromptTcs = null;
                                        }
                                    }
                                    tcs.TrySetResult(false);
                                }
                            );
                            Find.WindowStack.Add(dialog);
                        });
                    }
                }

                return await AwaitBudgetApprovalAsync(
                    tcs.Task,
                    request.CancellationToken,
                    TimeSpan.FromSeconds(BudgetPromptTimeoutSeconds)).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>
        /// 等待預算對話框的結果，同時尊重個別請求的取消 Token 與逾時。
        /// 關鍵在於「不能取消共用的 TCS」：多個請求可能同時等待同一個對話框，
        /// 若直接取消共用 TCS，其中一個請求取消會連帶讓其他請求全部失敗。
        /// 因此每個等待者持有自己的競賽 TCS，只影響自己。
        /// </summary>
        internal static async Task<bool> AwaitBudgetApprovalAsync(
            Task<bool> sharedPromptTask,
            CancellationToken requestToken,
            TimeSpan timeout)
        {
            if (sharedPromptTask == null) throw new ArgumentNullException(nameof(sharedPromptTask));

            var waiterTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken))
            {
                linked.CancelAfter(timeout);
                using (linked.Token.Register(() => waiterTcs.TrySetResult(false)))
                {
                    Task<bool> winner = await Task.WhenAny(sharedPromptTask, waiterTcs.Task).ConfigureAwait(false);
                    if (ReferenceEquals(winner, sharedPromptTask))
                    {
                        return await sharedPromptTask.ConfigureAwait(false);
                    }

                    if (requestToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(requestToken);
                    }

                    // 逾時視為拒絕，但不寫入 _budgetDeclineDate，讓使用者稍後仍能重新被詢問。
                    return false;
                }
            }
        }

        private bool IsBudgetMocked(RimLLMRequest request, out string mockResult)
        {
            mockResult = null;
            if (_settings.DailyBudgetLimit > 0f && _settings.DailyAccumulatedCost >= _settings.DailyBudgetLimit)
            {
                if (_settings.BudgetPolicy == 1) // SilentMocking
                {
                    if (request.ResponseType != null)
                    {
                        mockResult = "{}";
                    }
                    else
                    {
                        try
                        {
                            mockResult = (LanguageDatabase.activeLanguage != null) 
                                ? "RimLLM_SilentMockResponse".Translate().ToString() 
                                : "*AI is temporarily resting due to daily budget limits...*";
                        }
                        catch
                        {
                            mockResult = "*AI is temporarily resting due to daily budget limits...*";
                        }
                    }
                    return true;
                }
            }
            return false;
        }

        private bool IsInCooldown(string providerId)
        {
            return ProviderFailCooldowns.TryGetValue(providerId, out DateTime cdUntil) && DateTime.UtcNow < cdUntil;
        }

        private float GetAverageLatency(string providerId)
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

        private void RecordLatency(string providerId, long ms)
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

        #endregion
    }
}
