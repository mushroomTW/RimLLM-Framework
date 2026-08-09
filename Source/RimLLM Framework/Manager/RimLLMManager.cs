using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
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
    /// 內部邏輯委託給排隊佇列 (RequestQueue)、熔斷器 (CircuitBreaker)、JSON 輔助 (JsonHelper)、使用統計器 (UsageTracker) 與備用管道 (FallbackPipeline)。
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
        private readonly RimLLMFallbackPipeline _fallbackPipeline;

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

            _fallbackPipeline = new RimLLMFallbackPipeline(
                settings,
                _circuitBreaker,
                _usageTracker,
                providerId => TryGetProvider(providerId, out var provider) ? provider : null,
                IsProviderEnabled);

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
        private async Task<RimLLMGenerationResult> GenerateInternalAsync(RimLLMRequest request)
        {
            RimLLMRequest normalizedRequest = NormalizeRequest(request, _settings);

            // 准入檢查一律在進入佇列之前執行，且整條請求路徑只執行一次。
            // 特別是預算對話框：若在佇列委派內等待，會持續佔用一個並行名額。
            if (await RunAdmissionChecksAsync(normalizedRequest).ConfigureAwait(false)
                is string mockResult)
            {
                return new RimLLMGenerationResult { Text = mockResult };
            }

            return await _requestQueue.EnqueueRequestAsync(normalizedRequest, () => GenerateInternalDirectAsync(normalizedRequest)).ConfigureAwait(false);
        }

        /// <summary>
        /// 執行防濫用與預算檢查。
        /// 若預算政策指示以模擬回應取代真實請求，回傳該模擬字串；否則回傳 null 代表可繼續。
        /// </summary>
        private async Task<string> RunAdmissionChecksAsync(RimLLMRequest request)
        {
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

        /// <summary>內部存取目前設定（供 facade 的結構化輸出流程使用）。</summary>
        internal IRimLLMSettings Settings => _settings;

        internal RimLLMChatClient CreateChatClient(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                throw new ArgumentException("ModId cannot be empty or null", nameof(modId));
            }
            return new RimLLMChatClient(this, modId);
        }

        /// <summary>建立綁定指定 Mod 的 embedding generator。modId 用於防濫用節流與遙測歸屬。</summary>
        internal RimLLMEmbeddingClient CreateEmbeddingGenerator(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                throw new ArgumentException("ModId cannot be empty or null", nameof(modId));
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
            // 交由專屬 Fallback Pipeline 執行核心處理
            return _fallbackPipeline.ExecuteWithFallbackAsync(
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
        /// SDK facade 的非串流唯一入口（回傳包含實際 provider/model 與用量的結果）。
        /// </summary>
        internal Task<RimLLMGenerationResult> GenerateResultAsync(RimLLMRequest request)
        {
            return GenerateInternalAsync(request);
        }

        /// <summary>
        /// SDK facade 的串流唯一入口。串流 chunk 經 <paramref name="onChunkReceived"/> 送出。
        /// </summary>
        internal Task<RimLLMGenerationResult> StreamResultAsync(
            RimLLMRequest request,
            Action<string> onChunkReceived)
        {
            return StreamInternalAsync(request, onChunkReceived);
        }

        /// <summary>
        /// 包裝排隊佇列的 StreamInternalAsync。
        /// </summary>
        private async Task<RimLLMGenerationResult> StreamInternalAsync(
            RimLLMRequest request,
            Action<string> onChunkReceived)
        {
            RimLLMRequest normalizedRequest = NormalizeRequest(request, _settings);

            // 與 GenerateInternalAsync 一致：准入檢查在佇列之前執行且只執行一次。
            if (await RunAdmissionChecksAsync(normalizedRequest).ConfigureAwait(false)
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
        /// 與非串流路徑共用相同的 Fallback Pipeline 執行核心，因此重試與熔斷行為一致。
        /// 回傳成功那次嘗試所累積的完整文字。
        /// </summary>
        private async Task<RimLLMGenerationResult> StreamInternalDirectAsync(RimLLMRequest request, Action<string> onChunkReceived)
        {
            // 累積器由每次 attempt 各自擁有：若沿用同一個緩衝，
            // 「先吐出部分內容再失敗」的 provider 會讓殘留文字混進下一次嘗試的結果。
            var sink = new StreamAttemptSink(onChunkReceived, request.OnStreamRestart, DispatchRestart);

            await _fallbackPipeline.ExecuteWithFallbackAsync(
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
                            ProviderCall nativeCall = BuildProviderCall(provider, request, request, model, useNativeSchema: true);
                            string nativeText = await nativeProvider.GenerateStructuredAsync(
                                nativeCall.Messages,
                                nativeCall.Options,
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

            return await InvokeRawProviderAsync(
                provider, PrepareRequestForProvider(provider, request), request, model).ConfigureAwait(false);
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

            ProviderCall streamCall = BuildProviderCall(
                provider, PrepareRequestForProvider(provider, request), request, model, useNativeSchema: false);
            await provider.StreamAsync(
                streamCall.Messages,
                streamCall.Options,
                model,
                onChunkReceived).ConfigureAwait(false);
            return new RimLLMGenerationResult
            {
                ProviderId = provider.ProviderId,
                ModelName = model
            };
        }

        /// <summary>
        /// 送往 provider 的一次呼叫所需的 messages 與 options。
        /// 刻意用具名型別而非 ValueTuple：RimWorld 的 Mono 環境對額外 BCL 型別的載入較敏感。
        /// </summary>
        private sealed class ProviderCall
        {
            public IList<ChatMessage> Messages;
            public ChatOptions Options;
        }

        /// <summary>
        /// 組出送往 provider 的 messages 與 options，並在有結構化輸出需求時附上 schema。
        /// options 客製化一律以 <paramref name="originalRequest"/> 為依據：
        /// 提示式 JSON fallback 只改寫 system prompt，不應影響 reasoning／temperature 等推導結果。
        /// </summary>
        private ProviderCall BuildProviderCall(
            ILLMProvider provider,
            RimLLMRequest preparedRequest,
            RimLLMRequest originalRequest,
            string model,
            bool useNativeSchema)
        {
            var options = RimLLMChatClientExecutor.BuildOptions(
                preparedRequest,
                model,
                useNativeSchema,
                ResolveChatOptionsCustomizer(provider, originalRequest, model));

            if (preparedRequest.ResponseType != null)
            {
                options.AdditionalProperties["rimllm_response_schema"] =
                    RimLLMJsonHelper.GenerateJsonSchemaString(preparedRequest.ResponseType);
            }

            return new ProviderCall
            {
                Messages = RimLLMChatClientExecutor.BuildMessages(preparedRequest),
                Options = options
            };
        }

        /// <summary>
        /// 呼叫未採用 IChatClient 的 provider（raw 文字路徑）。
        /// </summary>
        private async Task<RimLLMGenerationResult> InvokeRawProviderAsync(
            ILLMProvider provider,
            RimLLMRequest preparedRequest,
            RimLLMRequest originalRequest,
            string model)
        {
            ProviderCall call = BuildProviderCall(provider, preparedRequest, originalRequest, model, useNativeSchema: false);
            string text = await provider.GenerateAsync(call.Messages, call.Options, model).ConfigureAwait(false);
            return new RimLLMGenerationResult
            {
                Text = text,
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

            return await InvokeRawProviderAsync(provider, fallbackRequest, request, model).ConfigureAwait(false);
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

        internal string GetSampleJson(Type type)
        {
            return RimLLMJsonHelper.GetSampleJson(type);
        }

        internal bool ResolveFallbackEntry(string entry, out string providerId, out string modelName)
        {
            return _fallbackPipeline.ResolveFallbackEntry(entry, out providerId, out modelName);
        }

        #endregion

        #region Concurrency Queue & Double-Repair Methods

        internal async Task<T> PerformDoubleRepairAsync<T>(RimLLMRequest originalRequest, string failedResponse, string errorMessage)
        {
            var repairRequest = new RimLLMRequest
            {
                ModId = originalRequest.ModId,
                Temperature = 0.1f,
                MaxOutputTokens = originalRequest.MaxOutputTokens,
                CancellationToken = originalRequest.CancellationToken,
                DisableReasoning = true,
                Messages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, "You are a JSON repair assistant. The user will provide a JSON string that failed to parse, along with the parser error message. Your task is to output ONLY the corrected JSON string that is syntactically valid and contains all fields. Do NOT include markdown code blocks (like ```json), explanations, or any other text."),
                    new ChatMessage(ChatRole.User, $"Failed JSON:\n{failedResponse}\n\nParser Error:\n{errorMessage}\n\nTarget Structure Sample:\n{RimLLMJsonHelper.GetSampleJson<T>()}\n\nPlease output the repaired JSON string:")
                }
            };

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
            _fallbackPipeline.ClearCooldowns();
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
                return true;
            }

            string todayStr = DateTime.Today.ToString("yyyy-MM-dd");

            if (_budgetApprovalDate == todayStr)
            {
                return true;
            }
            if (_budgetDeclineDate == todayStr)
            {
                return false;
            }

            // 0=HardBlock, 1=SilentMocking, 2=FallbackToFree, 3=DialogPrompt
            // SilentMocking 於此放行，後續由 IsBudgetMocked 換成模擬回應；
            // FallbackToFree 也放行，改由 Fallback Pipeline 只挑免費供應商。
            if (_settings.BudgetPolicy == 1 || _settings.BudgetPolicy == 2)
            {
                return true;
            }

            if (_settings.BudgetPolicy == 3)
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
                        tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _activePromptTcs = tcs;

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

                    return false;
                }
            }
        }

        private bool IsBudgetMocked(RimLLMRequest request, out string mockResult)
        {
            mockResult = null;
            if (_settings.DailyBudgetLimit > 0f && _settings.DailyAccumulatedCost >= _settings.DailyBudgetLimit)
            {
                if (_settings.BudgetPolicy == 1)
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

        #endregion
    }
}
