extern alias bclasync;
extern alias ste;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Diagnostics;
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
    public class OpenAIProvider : ILLMProvider
    {
        protected readonly IRimLLMSettings Settings;
        private readonly string _providerId;
        private readonly string _defaultEndpoint;
        private readonly string _defaultTestModel;

        static OpenAIProvider()
        {
            // 初始化安全協定，解決 Unity/Mono 環境下部分舊版 HTTPS 憑證握手問題。
            // 確保任何供應商被建立時就生效（含只走官方 SDK 的路徑）。
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12;
        }

        public virtual string ProviderId => _providerId;
        protected virtual string DefaultEndpoint => _defaultEndpoint;

        /// <summary>
        /// 此供應商是否必須提供 API Key 才能使用。預設為 true，本地相容介面可覆寫為 false。
        /// </summary>
        public virtual bool RequiresApiKey => true;

        /// <summary>
        /// 衍生 provider 是否支援 OpenAI 相容的 <c>response_format: json_schema</c> 欄位。
        /// 預設為 true：OpenAI 官方支援 strict JSON Schema；不支援的服務端（Grok/Kimi/MiniMax/
        /// Nvidia/Qwen/Zai/OpenAICompatible）應覆寫為 false，讓框架改走提示式 JSON fallback。
        /// </summary>
        protected virtual bool SupportsNativeJsonSchemaPayload => true;

        public virtual LLMProviderCapabilities Capabilities => new LLMProviderCapabilities
        {
            SupportsNativeStructuredOutput = SupportsNativeJsonSchemaPayload,
            SupportsStreaming = true,
            SupportsUsageMetadata = true,
            // 官方 OpenAI SDK 的 IChatClient 會把 ChatOptions.Tools 轉成 tools 欄位送出，
            // OpenAI 相容端點（DeepSeek / Grok / Qwen 等子類）同樣支援。
            SupportsFunctionCalling = true
        };

        public OpenAIProvider(IRimLLMSettings settings)
            : this(settings, ProviderIds.OpenAI, "https://api.openai.com/v1/chat/completions", "gpt-4o-mini")
        {
        }

        protected OpenAIProvider(IRimLLMSettings settings, string providerId, string defaultEndpoint, string defaultTestModel)
        {
            Settings = settings;
            _providerId = providerId;
            _defaultEndpoint = defaultEndpoint;
            _defaultTestModel = defaultTestModel;
        }

        public virtual IChatClient CreateChatClient(string model)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            if (string.IsNullOrEmpty(apiKey) && !RequiresApiKey)
            {
                apiKey = PlaceholderApiKey;
            }
            string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);
            IChatClient rawClient = CreateOpenAiChatClient(apiKey, model, endpoint);
            return new OpenAIChatClientAdapter(rawClient, this, model);
        }

        public static IChatClient CreateOpenAiChatClient(string apiKey, string model, string endpoint = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("OpenAI API key 不得為空。", nameof(apiKey));
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("OpenAI model 不得為空。", nameof(model));

            var options = new OpenAIClientOptions();
            string normalizedEndpoint = NormalizeEndpoint(endpoint);
            if (!string.IsNullOrEmpty(normalizedEndpoint))
            {
                options.Endpoint = new Uri(normalizedEndpoint, UriKind.Absolute);
            }

            var client = new ChatClient(model, new ApiKeyCredential(apiKey), options);
            return client.AsIChatClient();
        }

        private static readonly char[] SlashChars = new char[] { '/' };

        public static string NormalizeEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            string normalized = endpoint.Trim().TrimEnd(SlashChars);
            const string suffix = "/chat/completions";
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - suffix.Length).TrimEnd(SlashChars);
            }
            return normalized;
        }

        /// <summary>
        /// 依模型類型調整 MEAI ChatOptions：推理模型清空 temperature 並對應 reasoning effort，
        /// 其餘維持 executor 已設定的基礎選項。
        /// </summary>
        protected virtual void BuildChatOptions(ChatOptions requestOptions, string model, ChatOptions options)
        {
            bool strict = false;
            if (requestOptions?.AdditionalProperties != null &&
                requestOptions.AdditionalProperties.TryGetValue("strict", out object strictVal) && strictVal is bool strictBool)
            {
                // strict 只認 RimLLMSchemaBuilder 算出的值。此處原本還有一段以字串比對
                // "additionalProperties": true 來推斷 strict 的 heuristic —— 那是死碼：
                // 產生器對開放式 map 輸出的是 value schema 物件而非字面 true，比對永遠不命中。
                strict = strictBool;
                options.AdditionalProperties["strict"] = strictBool;
            }

            // response_format 一律以 Patch 寫入原始 JSON，並確保 MEAI 這一側維持 null。
            // OpenAI SDK 的 ChatCompletionOptions.ResponseFormat 是 JsonPatch 支撐的屬性：
            // 只要框架動過同一個模型的 Patch（max_tokens／reasoning／model），RimWorld 的 Mono
            // 還原該屬性時會拿到基底 ChatResponseFormat，送出請求時序列化就會拋
            // "The WriteCore method should be invoked on an overriding type derived from ChatResponseFormat."
            options.ResponseFormat = null;
            string responseFormatJson = BuildResponseFormatJson(requestOptions, strict);

            ApplyReasoningAndSampling(requestOptions, model, options, responseFormatJson);
        }

        /// <summary>
        /// 組出 <c>response_format</c> 欄位的完整 JSON。schema 優先取框架以
        /// AdditionalProperties 傳遞的字串，其次才回頭讀 MEAI 的 ChatResponseFormatJson。
        /// 兩者都沒有、但呼叫端指定了 JSON 模式時退回 json_object；完全沒有 JSON 需求才回傳 null。
        /// </summary>
        private static string BuildResponseFormatJson(ChatOptions requestOptions, bool strict)
        {
            string schemaJson = RimLLMChatOptions.ReadAdditional<string>(requestOptions, "rimllm_response_schema", null);

            if (string.IsNullOrEmpty(schemaJson) &&
                requestOptions?.ResponseFormat is ChatResponseFormatJson jsonFormat && jsonFormat.Schema.HasValue)
            {
                schemaJson = jsonFormat.Schema.Value.GetRawText();
            }

            if (string.IsNullOrEmpty(schemaJson))
            {
                // 沒有 schema 但呼叫端仍要求 JSON 模式時要送出 json_object，
                // 否則這個選項會被靜默丟棄，模型照樣回散文而呼叫端無從察覺。
                return requestOptions?.ResponseFormat is ChatResponseFormatJson
                    ? "{\"type\":\"json_object\"}"
                    : null;
            }

            // schema 是直接拼進送出的 JSON 的，不合法就會毀掉整個 request body。
            // 先在本地解析一次，讓錯誤停在組裝階段，而不是換成服務端一句沒有線索的 400。
#pragma warning disable S108 // reason: 僅為驗證 JSON 合法性，解析成功即表示合法，無需額外操作
            using (JsonDocument.Parse(schemaJson)) { // 刻意空區塊：僅驗證 schemaJson 為合法 JSON
            }
#pragma warning restore S108

            return "{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"custom_type\"," +
                   "\"description\":\"RimLLM structured response\"," +
                   "\"strict\":" + (strict ? "true" : "false") + "," +
                   "\"schema\":" + schemaJson + "}}";
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
            string name = NormalizeModelName(modelName);
            return name.StartsWith("gpt-3.5") || name.StartsWith("gpt-4") || name.StartsWith("chatgpt-4");
        }

        /// <summary>
        /// 取出可用於前綴比對的模型名：去掉聚合服務端的 "vendor/" 前綴並轉小寫。
        /// null 或空字串一律回傳空字串，讓呼叫端的前綴比對自然地不命中。
        /// </summary>
        private static string NormalizeModelName(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return string.Empty;
            string name = modelName.Contains("/") ? modelName.Substring(modelName.LastIndexOf('/') + 1) : modelName;
            return name.ToLowerInvariant();
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
        #pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
        private void ApplyReasoningAndSampling(ChatOptions requestOptions, string model, ChatOptions options, string responseFormatJson)
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
                var chatCompletionOptions = OpenAIPatchExtensions.GetOrCreateSanitizedOptions(baseFactory, client);

                if (responseFormatJson != null)
                {
                    chatCompletionOptions.Patch.Set(
                        Encoding.UTF8.GetBytes("$.response_format"),
                        Encoding.UTF8.GetBytes(responseFormatJson));
                }

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
        #pragma warning restore S3776

        /// <summary>
        /// 解析呼叫端是否要求關閉思考。RimLLMChatOptions 直接帶屬性，框架管線則以 AdditionalProperties 轉遞。
        /// </summary>
        private static bool ResolveDisableReasoning(ChatOptions requestOptions)
        {
            return requestOptions is RimLLMChatOptions rimOptions
                ? rimOptions.DisableReasoning
                : RimLLMChatOptions.ReadAdditional(requestOptions, RimLLMChatOptions.DisableReasoningKey, false);
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

        protected sealed class OpenAIChatClientAdapter : DelegatingChatClient
        {
            private readonly OpenAIProvider _provider;
            private readonly string _model;

            public OpenAIChatClientAdapter(IChatClient innerClient, OpenAIProvider provider, string model)
                : base(innerClient)
            {
                _provider = provider;
                _model = model;
            }

            public override async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                System.Threading.CancellationToken cancellationToken = default)
            {
                var targetOptions = options?.Clone() ?? new ChatOptions();
                _provider.BuildChatOptions(options, _model, targetOptions);

                ChatResponse response;
                try
                {
                    response = await base.GetResponseAsync(messages, targetOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (ClientResultException ex)
                {
                    var mapped = LLMErrorMapper.CreateException(ex.Status, ex.Message, innerException: ex);
                    if (_provider.MarkUnsupportedParameters(_model, mapped))
                    {
                        var retryOptions = options?.Clone() ?? new ChatOptions();
                        _provider.BuildChatOptions(options, _model, retryOptions);
                        response = await base.GetResponseAsync(messages, retryOptions, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        throw mapped;
                    }
                }

                // 若由管線層 RimLLMChatClientExecutor 調度，由管線層集中記帳；
                // 若為外部直接透過 CreateChatClient / GenerateAsync 調用，則在此處記錄用量。
                bool isExecutorManaged = RimLLMChatOptions.ReadAdditional(options, RimLLMChatOptions.ExecutorManagedKey, false);
                if (!isExecutorManaged && response?.Usage != null)
                {
                    try
                    {
                        var manager = RimLLMProvider.Manager;
                        if (manager != null)
                        {
                            int promptTokens = (int)(response.Usage.InputTokenCount ?? 0);
                            int completionTokens = (int)(response.Usage.OutputTokenCount ?? 0);
                            int cachedTokens = (int)(response.Usage.CachedInputTokenCount ?? 0);
                            manager.RecordUsage(_provider.ProviderId, _model, promptTokens, completionTokens, cachedTokens);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Manager not initialized
                    }
                }

                return response;
            }

            public override bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                System.Threading.CancellationToken cancellationToken = default)
            {
                return new OpenAiStreamEnumerable(
                    (opts, ct) => base.GetStreamingResponseAsync(messages, opts, ct),
                    options,
                    _provider,
                    _model);
            }
        }

        private sealed class OpenAiStreamEnumerable : bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
        {
            private readonly Func<ChatOptions, System.Threading.CancellationToken, bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>> _streamFactory;
            private readonly ChatOptions _originalOptions;
            private readonly OpenAIProvider _provider;
            private readonly string _model;

            public OpenAiStreamEnumerable(
                Func<ChatOptions, System.Threading.CancellationToken, bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>> streamFactory,
                ChatOptions originalOptions,
                OpenAIProvider provider,
                string model)
            {
                _streamFactory = streamFactory;
                _originalOptions = originalOptions;
                _provider = provider;
                _model = model;
            }

            public bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(
                System.Threading.CancellationToken cancellationToken = default)
            {
                var targetOptions = _originalOptions?.Clone() ?? new ChatOptions();
                _provider.BuildChatOptions(_originalOptions, _model, targetOptions);
                var innerEnumerator = _streamFactory(targetOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);
                return new OpenAiStreamEnumerator(_streamFactory, innerEnumerator, _originalOptions, _provider, _model, cancellationToken);
            }
        }

        private sealed class OpenAiStreamEnumerator : bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
        {
            private readonly Func<ChatOptions, System.Threading.CancellationToken, bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>> _streamFactory;
            private bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> _inner;
            private readonly ChatOptions _originalOptions;
            private readonly OpenAIProvider _provider;
            private readonly string _model;
            private readonly System.Threading.CancellationToken _cancellationToken;
            private bool _hasYieldedAny;

            public OpenAiStreamEnumerator(
                Func<ChatOptions, System.Threading.CancellationToken, bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>> streamFactory,
                bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> inner,
                ChatOptions originalOptions,
                OpenAIProvider provider,
                string model,
                System.Threading.CancellationToken cancellationToken)
            {
                _streamFactory = streamFactory;
                _inner = inner;
                _originalOptions = originalOptions;
                _provider = provider;
                _model = model;
                _cancellationToken = cancellationToken;
            }

            public ChatResponseUpdate Current => _inner.Current;

            public async ste::System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
            {
                while (true)
                {
                    try
                    {
                        bool hasNext = await _inner.MoveNextAsync().ConfigureAwait(false);
                        if (hasNext)
                        {
                            _hasYieldedAny = true;
                        }
                        return hasNext;
                    }
                    catch (ClientResultException ex)
                    {
                        var mapped = LLMErrorMapper.CreateException(ex.Status, ex.Message, innerException: ex);
                        if (!_hasYieldedAny && _provider.MarkUnsupportedParameters(_model, mapped))
                        {
                            // 尚未產出任何分塊且為可自癒參數（如思考強度/格式），在此請求中就地重試
                            await _inner.DisposeAsync().ConfigureAwait(false);
                            var retryOptions = _originalOptions?.Clone() ?? new ChatOptions();
                            _provider.BuildChatOptions(_originalOptions, _model, retryOptions);
                            _inner = _streamFactory(retryOptions, _cancellationToken).GetAsyncEnumerator(_cancellationToken);
                            continue;
                        }
                        throw mapped;
                    }
                }
            }

            public ste::System.Threading.Tasks.ValueTask DisposeAsync()
            {
                return _inner.DisposeAsync();
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
        protected static bool IsOpenAiReasoningModel(string modelName)
        {
            string name = NormalizeModelName(modelName);
            return name.StartsWith("o1") || name.StartsWith("o3") || name.StartsWith("o4") ||
                   name.StartsWith("gpt-5");
        }

        public virtual async Task<TestResult> TestConnectionAsync()
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            if (string.IsNullOrEmpty(apiKey) && RequiresApiKey)
            {
                return new TestResult { Success = false, Provider = ProviderId, ErrorMessage = "API Key not configured", ErrorCode = LLMError.InvalidKey };
            }

            var result = new TestResult { Provider = ProviderId };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "ping") };
                // 輸出上限不能壓到極低：思考型模型會先把額度花在內部推理上，
                // 額度用盡時回傳的 content 是空的，連線其實正常卻會被判成「回傳空白內容」。
                // 因此給足額度並明確要求關閉思考，讓測試只反映「連線與金鑰是否可用」。
                var options = new ChatOptions { MaxOutputTokens = 256 };
                options.AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["rimllm_disable_reasoning"] = true
                };
                // 優先使用 DefaultTestModel 作為連線測試模型，因為這是最便宜且穩定的內建對話模型。
                // 只有在 DefaultTestModel 為 "default" (如 OpenAICompatible 本地相容介面) 時，才去讀取快取清單的第一個模型。
                string testModel = DefaultTestModel;
                if (testModel == "default")
                {
                    testModel = Settings.GetDefaultModel(ProviderId, DefaultTestModel);
                }

                using (IChatClient client = CreateChatClient(testModel))
                {
                    await client.GetResponseAsync(messages, options).ConfigureAwait(false);
                    stopwatch.Stop();

                    result.Success = true;
                    result.Model = testModel;
                    result.LatencyMs = stopwatch.ElapsedMilliseconds;
                }
            }
            catch (RimLLMException ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ErrorCode = ex.Error;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ErrorCode = LLMError.Unknown;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }

        protected virtual string DefaultTestModel => _defaultTestModel;

        /// <summary>
        /// 透過官方 SDK 的 /models 端點取得可用模型清單。
        /// 端點正規化與 chat client 共用同一份邏輯，不再手動改寫 URL。
        /// </summary>
        public virtual async Task<List<string>> FetchAvailableModelsAsync()
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = NormalizeEndpoint(
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

                foreach (OpenAIModel model in System.Linq.Enumerable.Where(models, m => !string.IsNullOrEmpty(m?.Id)))
                {
                    list.Add(model.Id);
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
#pragma warning restore S108, S2325, S3267
}