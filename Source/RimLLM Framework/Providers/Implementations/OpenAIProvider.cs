using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Core;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// OpenAI API 供應商，支援 Chat Completion 與 SSE 串流。
    /// </summary>
    public class OpenAIProvider : BaseHttpProvider, IChatClientProvider, INativeStructuredOutputProvider, IChatOptionsCustomizer
    {
        private readonly string _providerId;
        private readonly string _defaultEndpoint;
        private readonly string _defaultTestModel;
        private readonly IOpenAIChatClientFactory _chatClientFactory;

        public override string ProviderId => _providerId;
        protected virtual string DefaultEndpoint => _defaultEndpoint;

        /// <summary>
        /// OpenAI 系列一律走官方 SDK + MEAI，不保留 raw HTTP 對話路徑。
        /// </summary>
        public bool UsesIChatClient => true;

        /// <summary>
        /// 衍生 provider 是否支援 OpenAI 相容的 <c>response_format: json_schema</c> 欄位。
        /// 預設為 true：OpenAI 官方支援 strict JSON Schema；不支援的服務端（Grok/Kimi/MiniMax/
        /// Nvidia/Qwen/Zai/OpenAICompatible）應覆寫為 false，讓框架改走提示式 JSON fallback。
        /// </summary>
        protected virtual bool SupportsNativeJsonSchemaPayload => true;

        public LLMProviderCapabilities Capabilities => new LLMProviderCapabilities
        {
            SupportsNativeStructuredOutput = SupportsNativeJsonSchemaPayload,
            SupportsStreaming = true,
            SupportsUsageMetadata = true
        };

        public OpenAIProvider(IRimLLMSettings settings)
            : this(settings, ProviderIds.OpenAI, "https://api.openai.com/v1/chat/completions", "gpt-4o-mini", new OpenAIChatClientFactory())
        {
        }

        protected OpenAIProvider(IRimLLMSettings settings, string providerId, string defaultEndpoint, string defaultTestModel)
            : this(settings, providerId, defaultEndpoint, defaultTestModel, new OpenAIChatClientFactory())
        {
        }

        private protected OpenAIProvider(
            IRimLLMSettings settings,
            string providerId,
            string defaultEndpoint,
            string defaultTestModel,
            IOpenAIChatClientFactory chatClientFactory)
            : base(settings)
        {
            _providerId = providerId;
            _defaultEndpoint = defaultEndpoint;
            _defaultTestModel = defaultTestModel;
            _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        }

        public virtual IChatClient CreateChatClient(string model)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);
            return _chatClientFactory.Create(apiKey, model, endpoint);
        }

        /// <summary>
        /// 供應商專屬 options 客製化鉤子：交由 <see cref="BuildChatOptions"/> 實作。
        /// </summary>
        public Action<ChatOptions> CreateChatOptionsCustomizer(ChatOptions options, string model)
        {
            return o => BuildChatOptions(options, model, o);
        }

        /// <summary>
        /// 依模型類型調整 MEAI ChatOptions：推理模型清空 temperature 並對應 reasoning effort，
        /// 其餘維持 executor 已設定的基礎選項。
        /// </summary>
        protected virtual void BuildChatOptions(ChatOptions requestOptions, string model, ChatOptions options)
        {
            if (requestOptions?.ResponseFormat != null)
            {
                options.ResponseFormat = requestOptions.ResponseFormat;
            }
            if (requestOptions?.AdditionalProperties != null)
            {
                if (requestOptions.AdditionalProperties.TryGetValue("strict", out object strictVal) && strictVal is bool strictBool)
                {
                    options.AdditionalProperties["strict"] = strictBool;
                }
                if (requestOptions.AdditionalProperties.TryGetValue("rimllm_response_schema", out object schemaVal) && schemaVal is string schemaJsonStr)
                {
                    using (System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(schemaJsonStr))
                    {
                        // strict 只認 RimLLMSchemaBuilder 算出的值。此處原本還有一段以字串比對
                        // "additionalProperties": true 來推斷 strict 的 heuristic —— 那是死碼：
                        // 產生器對開放式 map 輸出的是 value schema 物件而非字面 true，比對永遠不命中。
                        bool strict = false;
                        if (requestOptions.AdditionalProperties.TryGetValue("strict", out object sObj) && sObj is bool sBool)
                        {
                            strict = sBool;
                        }
                        options.ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(
                            document.RootElement.Clone(),
                            "custom_type",
                            "RimLLM structured response");
                        options.AdditionalProperties["strict"] = strict;
                    }
                }
            }

            ApplyReasoningAndSampling(requestOptions, model, options);
        }

        /// <summary>
        /// 此供應商表達思考強度的線上格式。預設為 OpenAI 的頂層 <c>reasoning_effort</c>，
        /// 格式不同的供應商覆寫這個屬性即可，不需要各自重寫請求組裝。
        /// </summary>
        protected virtual ReasoningWireFormat ReasoningFormat => ReasoningWireFormat.OpenAIEffort;

        /// <summary>
        /// 此供應商是否允許明確關閉思考。xAI 的推理模型無法關閉，送出關閉指令只會換來 400。
        /// </summary>
        protected virtual bool SupportsDisablingReasoning => true;

        /// <summary>
        /// 已知**不具備**思考能力的模型，對這些模型送思考參數只會白白換來一次 400。
        ///
        /// 刻意用否定表列而非肯定表列：肯定表列漏掉新模型會讓設定永久靜默失效（框架先前的 o1/o3 判斷就是如此），
        /// 否定表列漏掉的模型只會被樂觀地送出參數，服務端若不接受，框架記下來重打一次即可自癒。
        /// 換句話說，這份清單的作用只是省掉一次來回，不影響正確性。
        /// </summary>
        protected virtual bool IsKnownNonReasoningModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            string name = modelName.Contains("/") ? modelName.Substring(modelName.LastIndexOf('/') + 1) : modelName;
            name = name.ToLowerInvariant();
            return name.StartsWith("gpt-3.5") || name.StartsWith("gpt-4") || name.StartsWith("chatgpt-4");
        }

        /// <summary>
        /// 把框架的思考強度換成此供應商認得的字面值。
        /// 各家的詞彙不完全一致（例如 Kimi 只吃 low/high/max），詞彙不同的供應商覆寫這裡。
        /// </summary>
        protected virtual string MapEffortLiteral(ReasoningEffort effort)
        {
            if (effort == ReasoningEffort.Low) return "low";
            if (effort == ReasoningEffort.Medium) return "medium";
            if (effort == ReasoningEffort.High) return "high";
            return null;
        }

        /// <summary>
        /// 依供應商方言送出思考強度，並一併處理 temperature 與 max_tokens 的改寫。
        ///
        /// 思考參數一律由 Patch 掌控而不交給 MEAI 的 <c>ChatOptions.Reasoning</c>：
        /// 後者只會序列化成 OpenAI 的 <c>reasoning_effort</c>，表達不了其他家的方言。
        /// </summary>
        private void ApplyReasoningAndSampling(ChatOptions requestOptions, string model, ChatOptions options)
        {
            bool disableReasoning = ResolveDisableReasoning(requestOptions);
            ReasoningEffort? effort = requestOptions?.Reasoning?.Effort;

            // 服務端先前明確拒絕過思考參數的模型不再嘗試，避免每次請求都白白換來一次 400。
            bool reasoningAllowed = ReasoningFormat != ReasoningWireFormat.None &&
                                    !IsKnownNonReasoningModel(model) &&
                                    !RimLLMReasoningSupport.IsReasoningUnsupported(ProviderId, model);
            string effortLiteral = reasoningAllowed ? ResolveEffortLiteral(effort, disableReasoning) : null;
            bool thinkingEnabled = effortLiteral != null && effortLiteral != "none";

            options.Reasoning = null;

            if (IsOpenAiReasoningModel(model) || RimLLMReasoningSupport.IsTemperatureUnsupported(ProviderId, model))
            {
                options.Temperature = null;
            }

            // 對齊 raw 路徑：非 reasoning 模型走 max_tokens（OpenAI SDK 預設一律
            // 序列化為 max_completion_tokens），以 Patch 移除後改寫。
            int maxTokens = requestOptions?.MaxOutputTokens ?? 0;
            bool rewriteMaxTokens = maxTokens > 0 && !IsOpenAiReasoningModel(model);

            ReasoningWireFormat format = ReasoningFormat;
            Func<IChatClient, object> baseFactory = options.RawRepresentationFactory;
            options.RawRepresentationFactory = client =>
            {
                var chatCompletionOptions = baseFactory?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();

                if (rewriteMaxTokens)
                {
                    chatCompletionOptions.Patch.Remove(Encoding.UTF8.GetBytes("$.max_completion_tokens"));
                    chatCompletionOptions.Patch.Set(Encoding.UTF8.GetBytes("$.max_tokens"), maxTokens);
                }

                // 只有 OpenAIEffort 方言會自己寫回 reasoning_effort，其餘方言一律先清掉，
                // 避免 SDK 或上一層留下的欄位與方言欄位同時出現而互相矛盾。
                if (format != ReasoningWireFormat.OpenAIEffort || effortLiteral == null)
                {
                    chatCompletionOptions.Patch.Remove(Encoding.UTF8.GetBytes("$.reasoning_effort"));
                }

                if (effortLiteral == null) return chatCompletionOptions;

                switch (format)
                {
                    case ReasoningWireFormat.OpenAIEffort:
                        chatCompletionOptions.Patch.Set(
                            Encoding.UTF8.GetBytes("$.reasoning_effort"),
                            JsonSerializer.SerializeToUtf8Bytes(effortLiteral));
                        break;

                    case ReasoningWireFormat.OpenRouterReasoning:
                        chatCompletionOptions.Patch.Set(
                            Encoding.UTF8.GetBytes("$.reasoning"),
                            JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { { "effort", effortLiteral } }));
                        break;

                    case ReasoningWireFormat.ThinkingSwitch:
                        chatCompletionOptions.Patch.Set(
                            Encoding.UTF8.GetBytes("$.thinking"),
                            JsonSerializer.SerializeToUtf8Bytes(
                                new Dictionary<string, string> { { "type", thinkingEnabled ? "enabled" : "disabled" } }));
                        if (thinkingEnabled)
                        {
                            chatCompletionOptions.Patch.Set(
                                Encoding.UTF8.GetBytes("$.reasoning_effort"),
                                JsonSerializer.SerializeToUtf8Bytes(effortLiteral));
                        }
                        break;

                    case ReasoningWireFormat.EnableThinkingFlag:
                        chatCompletionOptions.Patch.Set(
                            Encoding.UTF8.GetBytes("$.enable_thinking"), thinkingEnabled);
                        if (thinkingEnabled && effort.HasValue)
                        {
                            chatCompletionOptions.Patch.Set(
                                Encoding.UTF8.GetBytes("$.thinking_budget"), ResolveThinkingBudget(effort.Value));
                        }
                        break;
                }

                return chatCompletionOptions;
            };
        }

        /// <summary>
        /// 解析呼叫端是否要求關閉思考。RimLLMChatOptions 直接帶屬性，框架管線則以 AdditionalProperties 轉遞。
        /// </summary>
        private static bool ResolveDisableReasoning(ChatOptions requestOptions)
        {
            if (requestOptions is RimLLMChatOptions rimOptions)
            {
                return rimOptions.DisableReasoning;
            }
            return requestOptions?.AdditionalProperties != null &&
                   requestOptions.AdditionalProperties.TryGetValue("rimllm_disable_reasoning", out object val) &&
                   val is bool flag && flag;
        }

        /// <summary>
        /// 算出要送出的強度字面值。回傳 null 代表不干預，交給服務端自己的預設。
        /// </summary>
        private string ResolveEffortLiteral(ReasoningEffort? effort, bool disableReasoning)
        {
            if (disableReasoning)
            {
                return SupportsDisablingReasoning ? "none" : null;
            }
            return effort.HasValue ? MapEffortLiteral(effort.Value) : null;
        }

        /// <summary>以 token 預算表達強度的方言（Qwen）使用的換算。</summary>
        private static int ResolveThinkingBudget(ReasoningEffort effort)
        {
            if (effort == ReasoningEffort.Low) return 1024;
            if (effort == ReasoningEffort.Medium) return 2048;
            return 4096;
        }

        public Task<string> GenerateStructuredAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model)
        {
            return GenerateWithChatClientAsync(messages, options, model, true);
        }

        public override Task<string> GenerateAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model)
        {
            return GenerateWithChatClientAsync(messages, options, model, options?.ResponseFormat != null && Settings.EnableNativeSchema);
        }

        public override Task StreamAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, Action<string> onChunkReceived)
        {
            return StreamWithChatClientAsync(messages, options, model, onChunkReceived);
        }

        private async Task<string> GenerateWithChatClientAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, bool useNativeSchema)
        {
            // 供應商不支援原生 JSON Schema（如 Kimi/Grok）時，
            // 即使呼叫端要求 structured output 也改走提示式 JSON fallback，
            // 與 raw 路徑 BuildRequestPayloadAsync 的判斷一致。
            bool effectiveNativeSchema = useNativeSchema && SupportsNativeJsonSchemaPayload;

            try
            {
                return await ExecuteGenerateAsync(messages, options, model, effectiveNativeSchema).ConfigureAwait(false);
            }
            catch (RimLLMException ex)
            {
                if (!MarkUnsupportedParameters(model, ex)) throw;
                // 參數已從記憶中排除，重新組裝一次不含該參數的請求。
                return await ExecuteGenerateAsync(messages, options, model, effectiveNativeSchema).ConfigureAwait(false);
            }
        }

        private async Task<string> ExecuteGenerateAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, bool effectiveNativeSchema)
        {
            RimLLMRequest translated = RimLLMChatClientExecutor.CreateFromChatOptions(messages, options, model);

            using (IChatClient client = CreateChatClient(model))
            {
                var result = await RimLLMChatClientExecutor.GenerateAsync(
                    client,
                    translated,
                    model,
                    effectiveNativeSchema,
                    ProviderId,
                    Settings?.ApiTimeout ?? 30f,
                    o => BuildChatOptions(options, model, o)).ConfigureAwait(false);
                return result.Text;
            }
        }

        private async Task StreamWithChatClientAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, Action<string> onChunkReceived)
        {
            // 只有「一個 chunk 都還沒送出」時才允許重打，否則畫面會出現前後兩段混接的內容。
            // 參數被拒是在服務端解析請求時就發生，正常情況下不會有任何 chunk 先送出。
            bool emitted = false;
            Action<string> trackingCallback = chunk =>
            {
                emitted = true;
                onChunkReceived?.Invoke(chunk);
            };

            try
            {
                await ExecuteStreamAsync(messages, options, model, trackingCallback).ConfigureAwait(false);
            }
            catch (RimLLMException ex)
            {
                if (emitted || !MarkUnsupportedParameters(model, ex)) throw;
                await ExecuteStreamAsync(messages, options, model, trackingCallback).ConfigureAwait(false);
            }
        }

        private async Task ExecuteStreamAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, Action<string> onChunkReceived)
        {
            RimLLMRequest translated = RimLLMChatClientExecutor.CreateFromChatOptions(messages, options, model);

            using (IChatClient client = CreateChatClient(model))
            {
                await RimLLMChatClientExecutor.StreamAsync(
                    client,
                    translated,
                    model,
                    options?.ResponseFormat != null && Settings.EnableNativeSchema,
                    ProviderId,
                    onChunkReceived,
                    Settings?.ApiTimeout ?? 30f,
                    o => BuildChatOptions(options, model, o)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 服務端明確拒絕思考參數或 temperature 時記下來，讓下一次組裝略過該參數。
        /// 回傳 true 代表這次記到了新資訊，值得以去掉參數的請求重打一次；
        /// 回傳 false 代表拒絕與這些參數無關（或先前已記錄過），呼叫端應直接把錯誤拋出去。
        /// </summary>
        private bool MarkUnsupportedParameters(string model, RimLLMException exception)
        {
            bool learned = false;

            if (exception.IsReasoningRejection &&
                RimLLMReasoningSupport.MarkReasoningUnsupported(ProviderId, model))
            {
                RimLLMLog.Warning($"[RimLLM] {ProviderId} 的模型 {model} 不接受思考參數，之後將不再送出。");
                learned = true;
            }

            if (exception.IsTemperatureRejection &&
                RimLLMReasoningSupport.MarkTemperatureUnsupported(ProviderId, model))
            {
                RimLLMLog.Warning($"[RimLLM] {ProviderId} 的模型 {model} 不接受 temperature，之後將不再送出。");
                learned = true;
            }

            return learned;
        }

        /// <summary>
        /// 判斷是否為 OpenAI 家的推理模型。這個判斷只用來決定「要不要清掉 temperature」——
        /// 這些模型在思考開啟時會直接以 400 拒絕取樣參數，而不是忽略它。
        ///
        /// 思考強度該不該送**不再**由這裡決定：名稱前綴無法涵蓋各家與未來的模型，
        /// 改由服務端的拒絕來判定（見 <see cref="RimLLMReasoningSupport"/>）。
        /// 這份清單漏掉新模型時，第一次請求會收到 temperature 相關的 400，
        /// 框架會記下來並自動重打，因此漏列的代價是一次重試而不是永久失敗。
        /// </summary>
        protected bool IsOpenAiReasoningModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            string name = modelName.Contains("/") ? modelName.Substring(modelName.LastIndexOf('/') + 1) : modelName;
            name = name.ToLowerInvariant();
            return name.StartsWith("o1") || name.StartsWith("o3") || name.StartsWith("o4") ||
                   name.StartsWith("gpt-5");
        }

        protected override string DefaultTestModel => _defaultTestModel;

        /// <summary>
        /// 透過官方 SDK 的 /models 端點取得可用模型清單。
        /// 端點正規化與 chat client 共用同一份邏輯，不再手動改寫 URL。
        /// </summary>
        public override async Task<List<string>> FetchAvailableModelsAsync()
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = OpenAIChatClientFactory.NormalizeEndpoint(
                Settings.GetEndpoint(ProviderId, DefaultEndpoint));

            var options = new OpenAIClientOptions();
            if (!string.IsNullOrEmpty(endpoint))
            {
                options.Endpoint = new Uri(endpoint, UriKind.Absolute);
            }

            // 本地相容伺服器多半不驗證金鑰，但 SDK 不接受空憑證。
            var credential = new ApiKeyCredential(
                string.IsNullOrEmpty(apiKey) ? PlaceholderApiKey : apiKey);

            var list = new List<string>();
            try
            {
                OpenAIModelCollection models = await new OpenAIClient(credential, options)
                    .GetOpenAIModelClient()
                    .GetModelsAsync()
                    .ConfigureAwait(false);

                foreach (OpenAIModel model in models)
                {
                    if (!string.IsNullOrEmpty(model?.Id))
                    {
                        list.Add(model.Id);
                    }
                }
            }
            catch (ClientResultException ex)
            {
                throw LLMErrorMapper.CreateException(
                    ex.Status,
                    $"Failed to fetch {ProviderId} models list: {RimLLMLog.SanitizeForLog(ex.Message, 200)}",
                    innerException: ex);
            }
            catch (Exception ex)
            {
                throw new RimLLMException(
                    LLMError.InvalidResponse,
                    $"Failed to fetch {ProviderId} models list: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", ex);
            }
            return list;
        }

        /// <summary>
        /// 本地相容伺服器未設定金鑰時使用的佔位憑證。
        /// </summary>
        private const string PlaceholderApiKey = "not-required";
    }
}
