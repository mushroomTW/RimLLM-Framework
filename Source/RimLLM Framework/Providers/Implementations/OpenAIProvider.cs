using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using OpenAI.Responses;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Providers;

/// <summary>
/// OpenAI API 供應商，支援 Chat Completion 與 SSE 串流。
/// </summary>
public class OpenAIProvider : ILLMProvider
{
    protected readonly IRimLLMSettings Settings;
    private readonly string _providerId;
    private readonly string _defaultEndpoint;

    public virtual string ProviderId => _providerId;
    protected virtual string DefaultEndpoint => _defaultEndpoint;

    /// <summary>
    /// 此供應商是否必須提供 API Key 才能使用。預設為 true，本地相容介面可覆寫為 false。
    /// </summary>
    public virtual bool RequiresApiKey => true;

    /// <summary>
    /// 衍生 provider 是否支援 OpenAI 相容的 <c>response_format: json_schema</c> 欄位。
    /// 預設為 true（OpenAI 官方支援 strict JSON Schema）；不支援的服務端應覆寫為 false，讓框架改走提示式 JSON fallback。
    /// </summary>
    protected virtual bool SupportsNativeJsonSchemaPayload => true;

    // 延遲建立：SupportsNativeJsonSchemaPayload 為虛擬屬性，子類覆寫在建構子期間尚不可靠。
    private LLMProviderCapabilities? _capabilities;

    public virtual LLMProviderCapabilities Capabilities => _capabilities ?? (_capabilities = new LLMProviderCapabilities
    {
        SupportsNativeStructuredOutput = SupportsNativeJsonSchemaPayload,
        SupportsStreaming = true,
        SupportsUsageMetadata = true,
        // 官方 OpenAI SDK 的 IChatClient 會把 ChatOptions.Tools 轉成 tools 欄位送出，
        // OpenAI 相容端點（DeepSeek / Grok / Qwen 等子類）同樣支援。
        SupportsFunctionCalling = true
    });

    public OpenAIProvider(IRimLLMSettings settings)
        : this(settings, ProviderIds.OpenAI, "https://api.openai.com/v1/chat/completions")
    {
    }

    protected OpenAIProvider(IRimLLMSettings settings, string providerId, string defaultEndpoint)
    {
        Settings = settings;
        _providerId = providerId;
        _defaultEndpoint = defaultEndpoint;
    }

    public virtual IChatClient CreateChatClient(string model)
    {
        string apiKey = Settings.GetActiveApiKey(ProviderId);
        if (string.IsNullOrEmpty(apiKey) && !RequiresApiKey)
        {
            apiKey = PlaceholderApiKey;
        }
        string? endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);
        IChatClient rawClient = CreateOpenAiChatClient(apiKey, model, endpoint);
        IChatClient? responsesClient = PrefersResponsesApi(model)
            ? CreateOpenAiResponsesChatClient(apiKey, model, endpoint)
            : null;
        return new OpenAIChatClientAdapter(rawClient, this, model, responsesClient);
    }

    /// <summary>
    /// 優先走 Responses API。OpenAI 自 GPT-5.4 起在 Chat Completions 不支援「工具＋思考」
    /// （developers.openai.com 的 migrate-to-responses）。端點或模型不支援時由轉接層退回 Chat Completions。
    /// </summary>
    internal virtual bool PrefersResponsesApi(string model) => ProviderId == ProviderIds.OpenAI;

    /// <summary>Chat Completions 的推理內容是否送回同一個模型。只列供應商文件要求或明載支援者，其餘端點可能拒收未知欄位。</summary>
    internal virtual bool ReplaysChatReasoning => false;

    internal virtual bool RequiresThoughtSignatures(string model) => false;

    private bool UsesDefaultEndpoint => string.Equals(
        NormalizeEndpoint(Settings.GetEndpoint(ProviderId, DefaultEndpoint)) ?? NormalizeEndpoint(DefaultEndpoint),
        NormalizeEndpoint(DefaultEndpoint), StringComparison.OrdinalIgnoreCase);

    /// <summary>同一供應商可指向不同端點，是否有 /responses 以端點為準。</summary>
    internal string ResponsesKey => ProviderId + "@" + Settings.GetEndpoint(ProviderId, DefaultEndpoint);

    /// <summary>
    /// SDK 的單次請求網路逾時。實際逾時由 ApiTimeout 掌控（設定上限 120 秒），
    /// 這裡只是略高於該上限的安全網；SDK 預設的 100 秒會讓 100～120 秒的設定失效。
    /// </summary>
    private static readonly TimeSpan SdkNetworkTimeout = TimeSpan.FromSeconds(180);

    public static IChatClient CreateOpenAiChatClient(string apiKey, string model, string? endpoint = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("OpenAI API key 不得為空。", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("OpenAI model 不得為空。", nameof(model));

        var options = new OpenAIClientOptions();
        if (ParseEndpoint(endpoint) is { } uri) options.Endpoint = uri;
        ConfigurePipeline(options, options.Endpoint);

        // 唯一允許的建立點：其他地方一律呼叫本方法，RetryPolicy 與逾時才不會漏設。
#pragma warning disable RS0030
        var client = new ChatClient(model, new ApiKeyCredential(apiKey), options);
#pragma warning restore RS0030
        return client.AsIChatClient();
    }

    internal static IChatClient CreateOpenAiResponsesChatClient(string apiKey, string model, string? endpoint = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("OpenAI API key 不得為空。", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("OpenAI model 不得為空。", nameof(model));

        var options = new ResponsesClientOptions();
        if (ParseEndpoint(endpoint) is { } uri) options.Endpoint = uri;
        ConfigurePipeline(options, options.Endpoint);

        // 唯一允許的建立點，理由同 CreateOpenAiChatClient。
#pragma warning disable RS0030
        var client = new ResponsesClient(new ApiKeyCredential(apiKey), options);
#pragma warning restore RS0030
        return client.AsIChatClient(model);
    }

    private static Uri? ParseEndpoint(string? endpoint)
    {
        string? normalized = NormalizeEndpoint(endpoint);
        return string.IsNullOrEmpty(normalized) ? null : new Uri(normalized, UriKind.Absolute);
    }

    private static void ConfigurePipeline(ClientPipelineOptions options, Uri? endpoint)
    {
        // 重試由 RimLLMFailoverChatClient 統一負責。SDK 預設的 3 次內建重試會把每次嘗試
        // 放大成 4 個請求，還會在占著併發名額時等待 Retry-After，拖慢備援切換。
        options.RetryPolicy = new ClientRetryPolicy(0);
        options.NetworkTimeout = SdkNetworkTimeout;
        // 依主機而非供應商類別判斷：玩家把 OpenAI 相容供應商指向 openrouter.ai 時，
        // 對話也要和 embedding／決策一樣帶上應用歸屬標頭。
        if (OpenRouterAttributionPolicy.Targets(endpoint))
        {
            options.AddPolicy(OpenRouterAttributionPolicy.Instance, PipelinePosition.PerCall);
        }
        if (TransportOverride != null)
        {
            options.Transport = TransportOverride;
        }
    }

    /// <summary>單元測試用：以假的 HTTP 傳輸攔截對話請求；正式環境為 null，走 SDK 預設傳輸。</summary>
    internal static PipelineTransport? TransportOverride { get; set; }

    private static readonly char[] SlashChars = ['/'];

    public static string? NormalizeEndpoint(string? endpoint)
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
    protected virtual void BuildChatOptions(ChatOptions? requestOptions, string model, ChatOptions options)
    {
        bool strict = false;
        if (requestOptions?.AdditionalProperties != null &&
            requestOptions.AdditionalProperties.TryGetValue("strict", out object? strictVal) && strictVal is bool strictBool)
        {
            strict = strictBool;
            (options.AdditionalProperties ??= [])["strict"] = strictBool;
        }

        // response_format 一律以 Patch 寫入原始 JSON，並確保 MEAI 這一側維持 null。
        // OpenAI SDK 的 ChatCompletionOptions.ResponseFormat 是 JsonPatch 支撐的屬性：
        // 只要框架動過同一個模型的 Patch（max_tokens／reasoning／model），
        // 還原該屬性時會拿到基底 ChatResponseFormat，送出請求時序列化就會拋
        // "The WriteCore method should be invoked on an overriding type derived from ChatResponseFormat."
        options.ResponseFormat = null;
        string? responseFormatJson = BuildResponseFormatJson(requestOptions, strict);

        ApplyReasoningAndSampling(requestOptions, model, options, responseFormatJson);

        // prompt_cache_key 是 OpenAI 的欄位，其他 Chat Completions 相容端點不一定接受。
        if (ProviderId == ProviderIds.OpenAI && UsesDefaultEndpoint && RimLLMChatOptions.GetPromptCacheKey(requestOptions) is { } cacheKey)
        {
            options.RawRepresentationFactory = OpenAIPatchExtensions.ChainPatch(
                options.RawRepresentationFactory,
                chat => chat.Patch.Set(OpenAIPatchPaths.PromptCacheKey, cacheKey));
        }
    }

    /// <summary>不存回應（store=false）：推理以加密內容續傳，對話由呼叫端完整帶上。</summary>
    private void BuildResponsesOptions(ChatOptions? requestOptions, string model, ChatOptions options, bool includeEncryptedReasoning)
    {
        string? effortLiteral = ResolveOutgoingEffortLiteral(requestOptions, model);
        (options.AdditionalProperties ??= [])[SentEffortLiteralKey] = effortLiteral;
        options.Reasoning = null;
        options.ConversationId = null;

        if (IsOpenAiReasoningModel(model) || RimLLMReasoningSupport.IsTemperatureUnsupported(ProviderId, model))
        {
            options.Temperature = null;
        }

        bool include = includeEncryptedReasoning && !IsKnownNonReasoningModel(model) &&
                       !RimLLMReasoningSupport.IsEncryptedReasoningUnsupported(ProviderId, model);
        string? cacheKey = UsesDefaultEndpoint ? RimLLMChatOptions.GetPromptCacheKey(requestOptions) : null;
        options.RawRepresentationFactory = _ =>
        {
            var responses = new CreateResponseOptions { StoredOutputEnabled = false };
            if (include) responses.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
            if (effortLiteral != null)
            {
                responses.Patch.Set(
                    OpenAIPatchPaths.Reasoning,
                    JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>(StringComparer.Ordinal) { { "effort", effortLiteral } }));
            }
            if (cacheKey != null) responses.Patch.Set(OpenAIPatchPaths.PromptCacheKey, cacheKey);
            return responses;
        };
    }

    private static string? BuildResponseFormatJson(ChatOptions? requestOptions, bool strict)
    {
        if (requestOptions?.ResponseFormat is not ChatResponseFormatJson jsonFormat)
        {
            return null;
        }

        if (!jsonFormat.Schema.HasValue)
        {
            // 呼叫端要求 JSON 模式但沒有 schema 時仍要送 json_object，否則選項會被靜默丟棄。
            return "{\"type\":\"json_object\"}";
        }

        string schemaJson = jsonFormat.Schema.Value.GetRawText();
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
    /// 刻意用否定表列：漏列的模型只會被樂觀送出參數，服務端不接受時框架記下並重打一次即可自癒，清單只是省一次來回。
    /// </summary>
    protected virtual bool IsKnownNonReasoningModel(string modelName)
    {
        string name = NormalizeModelName(modelName);
        return name.StartsWith("gpt-3.5", StringComparison.Ordinal) || name.StartsWith("gpt-4", StringComparison.Ordinal) || name.StartsWith("chatgpt-4", StringComparison.Ordinal);
    }

    private static string NormalizeModelName(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return string.Empty;
        string name = modelName.Contains('/', StringComparison.Ordinal) ? modelName.Substring(modelName.LastIndexOf('/') + 1) : modelName;
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// 把框架的思考強度換成此供應商認得的字面值。
    /// 各家的詞彙不完全一致（例如 Kimi 只吃 low/high/max），詞彙不同的供應商覆寫這裡。
    /// </summary>
    protected virtual string? MapEffortLiteral(ReasoningEffort effort)
    {
        if (effort == ReasoningEffort.Low) return "low";
        if (effort == ReasoningEffort.Medium) return "medium";
        if (effort == ReasoningEffort.High) return "high";
        if (effort == ReasoningEffort.ExtraHigh) return "xhigh";
        return null;
    }

    /// <summary>掛在送出去的 ChatOptions 上，記錄這次請求實際送出的思考強度字面值（null 代表沒送）。</summary>
    private const string SentEffortLiteralKey = "rimllm_sent_effort_literal";

    // 思考參數一律由 Patch 掌控，不交給 MEAI 的 ChatOptions.Reasoning：後者只會序列化成 OpenAI 的 reasoning_effort，表達不了其他家的方言。
    private void ApplyReasoningAndSampling(ChatOptions? requestOptions, string model, ChatOptions options, string? responseFormatJson)
    {
        ReasoningEffort? effort = requestOptions?.Reasoning?.Effort;
        string? effortLiteral = ResolveOutgoingEffortLiteral(requestOptions, model);
        bool thinkingEnabled = effortLiteral != null && effortLiteral != "none";
        // 把這次實際送出的字面值記在請求上。事後判讀 400 要看的是「送了什麼」，不能重算：
        // 並行請求可能已經改寫學習記憶，重算會得到另一個答案而記錯項目。
        (options.AdditionalProperties ??= [])[SentEffortLiteralKey] = effortLiteral;

        options.Reasoning = null;

        if (IsOpenAiReasoningModel(model) || RimLLMReasoningSupport.IsTemperatureUnsupported(ProviderId, model))
        {
            options.Temperature = null;
        }

        // 非 reasoning 模型走 max_tokens（OpenAI SDK 預設一律序列化為 max_completion_tokens），以 Patch 移除後改寫。
        int maxTokens = requestOptions?.MaxOutputTokens ?? 0;
        // 名單漏掉的新模型系列會以 400 拒絕 max_tokens，框架記下來後改回 SDK 預設欄位（見 MarkUnsupportedParameters）。
        bool rewriteMaxTokens = maxTokens > 0 && !IsOpenAiReasoningModel(model) &&
                                !RimLLMReasoningSupport.IsMaxTokensUnsupported(ProviderId, model);

        ReasoningWireFormat format = ReasoningFormat;
        Func<IChatClient, object?>? baseFactory = options.RawRepresentationFactory;
        options.RawRepresentationFactory = client =>
        {
            var chatCompletionOptions = OpenAIPatchExtensions.GetOrCreateOptions(baseFactory, client);

            if (responseFormatJson != null)
            {
                chatCompletionOptions.Patch.Set(
                    OpenAIPatchPaths.ResponseFormat,
                    Encoding.UTF8.GetBytes(responseFormatJson));
            }

            if (rewriteMaxTokens)
            {
                chatCompletionOptions.Patch.Remove(OpenAIPatchPaths.MaxCompletionTokens);
                chatCompletionOptions.Patch.Set(OpenAIPatchPaths.MaxTokens, maxTokens);
            }

            // 只有 OpenAIEffort 方言會自己寫回 reasoning_effort，其餘方言一律先清掉，
            // 避免 SDK 或上一層留下的欄位與方言欄位同時出現而互相矛盾。
            if (format != ReasoningWireFormat.OpenAIEffort || effortLiteral == null)
            {
                chatCompletionOptions.Patch.Remove(OpenAIPatchPaths.ReasoningEffort);
            }

            if (effortLiteral == null) return chatCompletionOptions;

            ApplyReasoningPatch(chatCompletionOptions, format, effortLiteral, thinkingEnabled, effort);

            return chatCompletionOptions;
        };
    }

    private static void ApplyReasoningPatch(
        ChatCompletionOptions chatCompletionOptions,
        ReasoningWireFormat format,
        string effortLiteral,
        bool thinkingEnabled,
        ReasoningEffort? effort)
    {
        switch (format)
        {
            case ReasoningWireFormat.OpenAIEffort:
                chatCompletionOptions.Patch.Set(
                    OpenAIPatchPaths.ReasoningEffort,
                    JsonSerializer.SerializeToUtf8Bytes(effortLiteral));
                break;

            case ReasoningWireFormat.OpenRouterReasoning:
                chatCompletionOptions.Patch.Set(
                    OpenAIPatchPaths.Reasoning,
                    JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>(StringComparer.Ordinal) { { "effort", effortLiteral } }));
                break;

            case ReasoningWireFormat.ThinkingSwitch:
                chatCompletionOptions.Patch.Set(
                    OpenAIPatchPaths.Thinking,
                    JsonSerializer.SerializeToUtf8Bytes(
                        new Dictionary<string, string>(StringComparer.Ordinal) { { "type", thinkingEnabled ? "enabled" : "disabled" } }));
                if (thinkingEnabled)
                {
                    chatCompletionOptions.Patch.Set(
                        OpenAIPatchPaths.ReasoningEffort,
                        JsonSerializer.SerializeToUtf8Bytes(effortLiteral));
                }
                break;

            case ReasoningWireFormat.EnableThinkingFlag:
                chatCompletionOptions.Patch.Set(
                    OpenAIPatchPaths.EnableThinking, thinkingEnabled);
                if (thinkingEnabled && effort.HasValue)
                {
                    chatCompletionOptions.Patch.Set(
                        OpenAIPatchPaths.ThinkingBudget, ResolveThinkingBudget(effort.Value));
                }
                break;
        }
    }

    private static bool ResolveDisableReasoning(ChatOptions? requestOptions)
    {
        if (requestOptions?.Reasoning?.Effort == ReasoningEffort.None) return true;

        return requestOptions is RimLLMChatOptions rimOptions
            ? rimOptions.DisableReasoning
            : RimLLMChatOptions.ReadAdditional(requestOptions, RimLLMChatOptions.DisableReasoningKey, fallback: false);
    }

    /// <summary>算出這次請求實際送出的強度字面值；null 代表不干預。組裝請求與事後判讀 400 共用本函式，兩邊對「送了什麼」才不會分歧。</summary>
    private string? ResolveOutgoingEffortLiteral(ChatOptions? requestOptions, string model)
    {
        bool reasoningAllowed = ReasoningFormat != ReasoningWireFormat.None &&
                                !IsKnownNonReasoningModel(model) &&
                                !RimLLMReasoningSupport.IsReasoningUnsupported(ProviderId, model);
        if (!reasoningAllowed) return null;

        if (ResolveDisableReasoning(requestOptions))
        {
            // 關不掉的模型（靜態宣告或服務端拒絕過）：沿用 Grok 的作法靜默忽略關閉指令，
            // 不送任何思考參數，也不改送強度。
            bool canDisable = SupportsDisablingReasoning &&
                              !RimLLMReasoningSupport.IsDisableUnsupported(ProviderId, model);
            return canDisable ? "none" : null;
        }

        ReasoningEffort? effort = requestOptions?.Reasoning?.Effort;
        return effort.HasValue ? MapEffortLiteral(effort.Value) : null;
    }

    private static int ResolveThinkingBudget(ReasoningEffort effort)
    {
        if (effort == ReasoningEffort.Low) return 1024;
        if (effort == ReasoningEffort.Medium) return 2048;
        return 4096;
    }

    private static ReadOnlySpan<byte> StreamingReasoningPath => "$.choices[0].delta.reasoning"u8;
    private static ReadOnlySpan<byte> ResponseReasoningPath => "$.choices[0].message.reasoning"u8;

    /// <summary>
    /// 把 <c>reasoning</c> 欄位補成 <see cref="TextReasoningContent"/>（串流 update）。
    /// </summary>
    /// <remarks>
    /// SDK 補救：Microsoft.Extensions.AI.OpenAI 10.11.0（11.0.0 亦同）只解析 DeepSeek 式的 <c>reasoning_content</c>，
    /// OpenRouter、Groq、Ollama、vLLM 回傳的 <c>reasoning</c> 欄位會被丟棄（僅留在 SDK 物件的 Patch 裡）。
    /// 重現：OpenRouter 串流送出 <c>delta.reasoning</c>，update.Contents 只有空的 TextContent。
    /// 已有推理內容時不再補，避免同時回傳兩個欄位的服務重複顯示。SDK 升級後需重新驗證是否仍需要。
    /// </remarks>
    internal static void PromoteReasoningField(ChatResponseUpdate update)
    {
        if (update?.RawRepresentation is not StreamingChatCompletionUpdate raw) return;
        if (update.Contents.Any(c => c is TextReasoningContent)) return;
        if (!raw.Patch.TryGetValue(StreamingReasoningPath, out string? reasoning) || string.IsNullOrEmpty(reasoning)) return;
        update.Contents.Insert(0, new TextReasoningContent(reasoning));
    }

    internal static void PromoteReasoningField(ChatResponse response)
    {
        if (response?.RawRepresentation is not ChatCompletion raw || response.Messages.Count == 0) return;
        if (response.Messages.Any(m => m.Contents.Any(c => c is TextReasoningContent))) return;
        if (!raw.Patch.TryGetValue(ResponseReasoningPath, out string? reasoning) || string.IsNullOrEmpty(reasoning)) return;
        response.Messages[response.Messages.Count - 1].Contents.Insert(0, new TextReasoningContent(reasoning));
    }

    private static void AddCacheWriteTokens(UsageDetails? usage, ResponseResult? response)
    {
        if (usage != null && response?.Usage?.InputTokenDetails is { } details &&
            details.Patch.TryGetValue("$.cache_write_tokens"u8, out int tokens) && tokens > 0)
        {
            (usage.AdditionalCounts ??= [])[RimLLMChatClientExecutor.CacheWriteTokensKey] = tokens;
        }
    }

    protected sealed class OpenAIChatClientAdapter : DelegatingChatClient
    {
        private readonly OpenAIProvider _provider;
        private readonly string _model;
        private readonly IChatClient? _responsesClient;

        public OpenAIChatClientAdapter(IChatClient innerClient, OpenAIProvider provider, string model)
            : this(innerClient, provider, model, responsesClient: null)
        {
        }

        internal OpenAIChatClientAdapter(IChatClient innerClient, OpenAIProvider provider, string model, IChatClient? responsesClient)
            : base(innerClient)
        {
            _provider = provider;
            _model = model;
            _responsesClient = responsesClient;
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            List<ChatMessage> buffered = [.. messages];
            var state = new AttemptState();
            while (true)
            {
                WireAttempt attempt = Prepare(buffered, options, state);
                try
                {
                    ChatResponse response = await attempt.Client.GetResponseAsync(attempt.Messages, attempt.Options, cancellationToken).ConfigureAwait(false);
                    if (attempt.UsesResponses)
                    {
                        ThrowIfFailed(response.RawRepresentation, response.Messages.SelectMany(m => m.Contents));
                        AddCacheWriteTokens(response.Usage, response.RawRepresentation as ResponseResult);
                        ReasoningReplay.CaptureResponses(response.Messages.SelectMany(m => m.Contents), attempt.Origin);
                    }
                    else
                    {
                        PromoteReasoningField(response);
                        ReasoningReplay.CaptureChat(response, attempt.Origin, _provider.ReplaysChatReasoning);
                    }
                    ConfirmFallback(state);
                    // 用量一律由 RimLLMChatClientExecutor 集中記帳，adapter 不另外記。
                    return response;
                }
                catch (ClientResultException ex)
                {
                    RimLLMException mapped = LLMErrorMapper.FromClientResultException(ex);
                    if (!TryRecover(attempt, state, mapped)) throw mapped;
                }
            }
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            return new OpenAiStreamEnumerable(this, [.. messages], options, cancellationToken);
        }

        internal sealed class AttemptState
        {
            public bool ForceChat;
            public bool RememberFallback;
            public bool DropEncryptedReasoning;
        }

        internal sealed record WireAttempt(
            IChatClient Client, List<ChatMessage> Messages, ChatOptions Options, bool UsesResponses, string Origin);

        internal WireAttempt Prepare(List<ChatMessage> messages, ChatOptions? options, AttemptState state)
        {
            var target = options?.Clone() ?? new ChatOptions();
            if (_responsesClient != null && !state.ForceChat && !NeedsChatCompletions(options) && !RimLLMReasoningSupport.IsResponsesUnsupported(_provider.ResponsesKey, _model))
            {
                string responsesOrigin = ReasoningReplay.Origin(_provider.ProviderId, _model, responses: true);
                _provider.BuildResponsesOptions(options, _model, target, !state.DropEncryptedReasoning);
                return new WireAttempt(
                    _responsesClient,
                    ReasoningReplay.PrepareResponses(messages, responsesOrigin, !state.DropEncryptedReasoning),
                    target, UsesResponses: true, responsesOrigin);
            }

            string origin = ReasoningReplay.Origin(_provider.ProviderId, _model, responses: false);
            _provider.BuildChatOptions(options, _model, target);
            return new WireAttempt(
                InnerClient,
                ReasoningReplay.PrepareChat(messages, origin, _provider.RequiresThoughtSignatures(_model)),
                target, UsesResponses: false, origin);
        }

        /// <summary>Responses API 沒有這些欄位，MEAI 10.11.0 會無聲丟棄；呼叫端給的 factory 產生的也是 Chat 選項。</summary>
        private static bool NeedsChatCompletions(ChatOptions? options) =>
            options != null && (options.StopSequences is { Count: > 0 } || options.Seed != null ||
                options.FrequencyPenalty != null || options.PresencePenalty != null || options.RawRepresentationFactory != null);

        internal bool TryRecover(WireAttempt attempt, AttemptState state, RimLLMException mapped)
        {
            if (attempt.UsesResponses && !state.DropEncryptedReasoning && mapped.HttpStatusCode is 400 or 404 or 422 &&
                LLMErrorMapper.ContainsIgnoreCase(mapped.Message, "encrypted"))
            {
                state.DropEncryptedReasoning = true;
                if (LLMErrorMapper.ContainsIgnoreCase(mapped.Message, "support"))
                {
                    RimLLMReasoningSupport.MarkEncryptedReasoningUnsupported(_provider.ProviderId, _model);
                }
                return true;
            }
            if (attempt.UsesResponses && IsResponsesRejection(mapped))
            {
                state.ForceChat = true;
                state.RememberFallback = ShouldRememberFallback(mapped);
                return true;
            }
            if (_provider.MarkUnsupportedParameters(_model, mapped, attempt.Options)) return true;
            if (!attempt.UsesResponses || _provider.UsesDefaultEndpoint || mapped.Error == LLMError.ContextWindowExceeded ||
                mapped.HttpStatusCode is not (400 or 415 or 422 or 500))
            {
                return false;
            }
            // 自訂端點的 /responses 可能只實作一半，錯誤碼不一定是 404。
            state.ForceChat = true;
            state.RememberFallback = mapped.HttpStatusCode != 500;
            return true;
        }

        /// <summary>端點沒有 /responses（404、405、501），或服務端指名這個模型要走 chat/completions。</summary>
        private static bool IsResponsesRejection(RimLLMException mapped) =>
            mapped.HttpStatusCode is 404 or 405 or 501 ||
            (mapped.HttpStatusCode == 400 && LLMErrorMapper.ContainsIgnoreCase(mapped.Message, "chat/completions"));

        /// <summary>官方端點都有 /responses，404 多半是模型或路由暫時找不到（OpenRouter 的 No endpoints found），只退回這一次。</summary>
        private bool ShouldRememberFallback(RimLLMException mapped) =>
            mapped.HttpStatusCode != 404 || !_provider.UsesDefaultEndpoint ||
            LLMErrorMapper.ContainsIgnoreCase(mapped.Message, "chat/completions");

        /// <summary>Chat Completions 成功才記住：模型名稱打錯同樣回 404，那不代表端點不支援 Responses。</summary>
        internal void ConfirmFallback(AttemptState state)
        {
            if (state.ForceChat && state.RememberFallback && RimLLMReasoningSupport.MarkResponsesUnsupported(_provider.ResponsesKey, _model))
            {
                RimLLMLog.Warning($"[RimLLM] Model {_model} on {_provider.ProviderId} does not support the Responses API; Chat Completions will be used for it.");
            }
        }

        /// <summary>
        /// Responses API 的生成失敗以 HTTP 200 回報，MEAI 10.11.0 轉成 <see cref="ErrorContent"/> 而不擲例外；
        /// 不轉回例外的話會被當成成功而不備援。拒答（Refusal）是模型的回答，不在此列。
        /// </summary>
        internal static void ThrowIfFailed(object? raw, IEnumerable<AIContent> contents)
        {
            ResponseError? failure = raw switch
            {
                StreamingResponseFailedUpdate failed => failed.Response?.Error,
                ResponseResult { Status: ResponseStatus.Failed } result => result.Error,
                _ => null
            };
            ErrorContent? error = contents.OfType<ErrorContent>()
                .FirstOrDefault(e => !string.Equals(e.ErrorCode, "Refusal", StringComparison.Ordinal));
            if (failure == null && error == null && raw is not StreamingResponseFailedUpdate) return;

            string code = failure?.Code.ToString() ?? error?.ErrorCode ?? string.Empty;
            string message = failure?.Message ?? error?.Message ?? "The response failed.";
            string detail = RimLLMLog.SanitizeForLog(code + " " + message, 300) ?? string.Empty;
            LLMError kind = LLMError.ProviderOffline;
            if (LLMErrorMapper.ContainsIgnoreCase(code, "rate_limit")) kind = LLMError.RateLimit;
            else if (LLMErrorMapper.LooksLikeContextWindowRejection(code + " " + message)) kind = LLMError.ContextWindowExceeded;
            else if (LLMErrorMapper.ContainsIgnoreCase(code, "invalid")) kind = LLMError.InvalidResponse;
            throw new RimLLMException(kind, "The provider failed to generate a response: " + detail);
        }

        internal bool ReplaysReasoning => _provider.ReplaysChatReasoning;

        protected override void Dispose(bool disposing)
        {
            if (disposing) _responsesClient?.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class OpenAiStreamEnumerable(
        OpenAIChatClientAdapter adapter,
        List<ChatMessage> messages,
        ChatOptions? options,
        System.Threading.CancellationToken requestToken) : IAsyncEnumerable<ChatResponseUpdate>
    {
        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(
            System.Threading.CancellationToken cancellationToken = default)
        {
            return new OpenAiStreamEnumerator(adapter, messages, options,
                cancellationToken.CanBeCanceled ? cancellationToken : requestToken);
        }
    }

    private sealed class OpenAiStreamEnumerator : IAsyncEnumerator<ChatResponseUpdate>
    {
        private readonly OpenAIChatClientAdapter _adapter;
        private readonly List<ChatMessage> _messages;
        private readonly ChatOptions? _options;
        private readonly System.Threading.CancellationToken _cancellationToken;
        private readonly OpenAIChatClientAdapter.AttemptState _state = new();
        private OpenAIChatClientAdapter.WireAttempt _attempt;
        private IAsyncEnumerator<ChatResponseUpdate> _inner;
        private ReasoningReplay.ChatStreamCapture _capture;
        private bool _hasYieldedAny;

        public OpenAiStreamEnumerator(
            OpenAIChatClientAdapter adapter,
            List<ChatMessage> messages,
            ChatOptions? options,
            System.Threading.CancellationToken cancellationToken)
        {
            _adapter = adapter;
            _messages = messages;
            _options = options;
            _cancellationToken = cancellationToken;
            (_attempt, _inner, _capture) = Start();
        }

        private (OpenAIChatClientAdapter.WireAttempt Attempt, IAsyncEnumerator<ChatResponseUpdate> Stream, ReasoningReplay.ChatStreamCapture Capture) Start()
        {
            OpenAIChatClientAdapter.WireAttempt attempt = _adapter.Prepare(_messages, _options, _state);
            return (
                attempt,
                attempt.Client.GetStreamingResponseAsync(attempt.Messages, attempt.Options, _cancellationToken).GetAsyncEnumerator(_cancellationToken),
                new ReasoningReplay.ChatStreamCapture(attempt.Origin, _adapter.ReplaysReasoning));
        }

        public ChatResponseUpdate Current => _inner.Current;

        public async ValueTask<bool> MoveNextAsync()
        {
            while (true)
            {
                try
                {
                    bool hasNext = await _inner.MoveNextAsync().ConfigureAwait(false);
                    if (hasNext)
                    {
                        Observe(_inner.Current);
                        _hasYieldedAny = true;
                    }
                    return hasNext;
                }
                catch (ClientResultException ex)
                {
                    RimLLMException mapped = LLMErrorMapper.FromClientResultException(ex);
                    if (_hasYieldedAny || !_adapter.TryRecover(_attempt, _state, mapped)) throw mapped;
                    await _inner.DisposeAsync().ConfigureAwait(false);
                    (_attempt, _inner, _capture) = Start();
                }
            }
        }

        private void Observe(ChatResponseUpdate update)
        {
            if (_attempt.UsesResponses)
            {
                OpenAIChatClientAdapter.ThrowIfFailed(update.RawRepresentation, update.Contents);
                ReasoningReplay.CaptureResponses(update.Contents, _attempt.Origin);
                foreach (UsageContent usage in update.Contents.OfType<UsageContent>())
                {
                    AddCacheWriteTokens(usage.Details, (update.RawRepresentation as StreamingResponseCompletedUpdate)?.Response);
                }
                return;
            }
            PromoteReasoningField(update);
            _capture.Observe(update);
            if (!_hasYieldedAny) _adapter.ConfirmFallback(_state);
        }

        public ValueTask DisposeAsync()
        {
            return _inner.DisposeAsync();
        }
    }

    /// <summary>服務端明確拒絕思考參數、temperature 或 max_tokens 時記下並回傳 true（值得去掉參數重打）；與這些參數無關或先前已記錄過回傳 false。</summary>
    private bool MarkUnsupportedParameters(string model, RimLLMException exception, ChatOptions sentOptions)
    {
        bool learned = false;

        if (exception.IsReasoningRejection)
        {
            // 送出的是「關閉」指令時，學到的只是「這個模型關不掉」，不是「不吃思考參數」——
            // 兩者混記的話，連線測試（一律要求關閉）的一次 400 就會讓該模型整個 session
            // 都收不到玩家設定的強度。
            if (RimLLMChatOptions.ReadAdditional<string?>(sentOptions, SentEffortLiteralKey, fallback: null) == "none")
            {
                if (RimLLMReasoningSupport.MarkDisableUnsupported(ProviderId, model))
                {
                    RimLLMLog.Warning($"[RimLLM] Model {model} on {ProviderId} rejected disabling reasoning; disable requests will be ignored for it from now on.");
                    learned = true;
                }
            }
            else if (RimLLMReasoningSupport.MarkReasoningUnsupported(ProviderId, model))
            {
                RimLLMLog.Warning($"[RimLLM] Model {model} on {ProviderId} rejected reasoning parameters; they will no longer be sent to it.");
                learned = true;
            }
        }

        if (exception.IsTemperatureRejection &&
            RimLLMReasoningSupport.MarkTemperatureUnsupported(ProviderId, model))
        {
            RimLLMLog.Warning($"[RimLLM] Model {model} on {ProviderId} rejected temperature; it will no longer be sent to it.");
            learned = true;
        }

        if (exception.IsMaxTokensRejection &&
            RimLLMReasoningSupport.MarkMaxTokensUnsupported(ProviderId, model))
        {
            RimLLMLog.Warning($"[RimLLM] Model {model} on {ProviderId} rejected max_tokens; max_completion_tokens will be used from now on.");
            learned = true;
        }

        return learned;
    }

    /// <summary>
    /// 判斷是否為 OpenAI 家的推理模型，用來決定要不要清掉 temperature（這些模型思考開啟時以 400 拒絕取樣參數）與是否改寫 max_tokens。
    /// 名稱前綴無法涵蓋各家與未來的模型；漏列時第一次請求收到 400，框架記下後自動重打，代價只是一次重試。
    /// </summary>
    protected static bool IsOpenAiReasoningModel(string modelName)
    {
        string name = NormalizeModelName(modelName);
        return name.StartsWith("o1", StringComparison.Ordinal) || name.StartsWith("o3", StringComparison.Ordinal) || name.StartsWith("o4", StringComparison.Ordinal) ||
               name.StartsWith("gpt-5", StringComparison.Ordinal);
    }

    /// <summary>
    /// 以玩家在備援鏈為此供應商選的第一個模型測試連線。沒有預設測試模型：還沒選模型就回報失敗。
    /// </summary>
    public virtual Task<TestResult> TestConnectionAsync()
    {
        return TestConnectionAsync(GetChosenModel(Settings, ProviderId));
    }

    /// <summary>以指定模型送一則極短訊息測試連線；設定精靈在模型加入備援鏈之前用這個驗證玩家選的模型。</summary>
    public virtual async Task<TestResult> TestConnectionAsync(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return new TestResult { Success = false, Provider = ProviderId, ErrorMessage = "No model selected; add one for this provider in the Model settings tab first.", ErrorCode = LLMError.Unknown };
        }

        string apiKey = Settings.GetActiveApiKey(ProviderId);
        if (string.IsNullOrEmpty(apiKey) && RequiresApiKey)
        {
            return new TestResult { Success = false, Provider = ProviderId, ErrorMessage = "API Key not configured", ErrorCode = LLMError.InvalidKey };
        }

        var result = new TestResult { Provider = ProviderId };
        var stopwatch = Stopwatch.StartNew();
        float timeoutSeconds = ApiTimeouts.Resolve(Settings.ApiTimeout);
        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            string testModel = model.Trim();
            await PingAsync(testModel, timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            result.Success = true;
            result.Model = testModel;
            result.LatencyMs = stopwatch.ElapsedMilliseconds;
        }
        catch (RimLLMException ex)
        {
            MarkFailed(result, stopwatch, ex.Message, ex.Error);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            MarkFailed(result, stopwatch, string.Create(CultureInfo.InvariantCulture, $"Connection test timed out after {timeoutSeconds} seconds."), LLMError.Timeout);
        }
        catch (Exception ex)
        {
            MarkFailed(result, stopwatch, RimLLMLog.SanitizeForLog(ex.Message, 300), LLMError.Unknown);
        }

        return result;
    }

    private async Task PingAsync(string testModel, System.Threading.CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "ping") };
        // 輸出上限不能壓到極低：思考型模型會先把額度花在內部推理上，
        // 額度用盡時回傳的 content 是空的，連線其實正常卻會被判成「回傳空白內容」。
        // 因此給足額度並明確要求關閉思考，讓測試只反映「連線與金鑰是否可用」。
        var options = new ChatOptions { MaxOutputTokens = 256 };
        options.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["rimllm_disable_reasoning"] = true
        };

        using IChatClient client = CreateChatClient(testModel);
        await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    private static void MarkFailed(TestResult result, Stopwatch stopwatch, string? message, LLMError error)
    {
        stopwatch.Stop();
        result.Success = false;
        result.ErrorMessage = message;
        result.ErrorCode = error;
        result.LatencyMs = stopwatch.ElapsedMilliseconds;
    }

    internal static string GetChosenModel(IRimLLMSettings settings, string providerId)
    {
        string prefix = providerId + ":";
        foreach (string entry in settings?.FallbackChain ?? [])
        {
            if (entry != null && entry.Length > prefix.Length && entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                return entry.Substring(prefix.Length);
            }
        }
        return "";
    }

    /// <summary>透過官方 SDK 的 /models 端點取得可用模型清單。</summary>
    public virtual async Task<List<string>> FetchAvailableModelsAsync()
    {
        return (await FetchModelCatalogAsync().ConfigureAwait(false)).Models;
    }

    /// <summary>取得模型清單，並從同一份 /models 回應讀出上下文上限（SDK 只解析 <c>id</c>，上限欄位要從原始 JSON 取）。</summary>
    internal virtual async Task<ModelCatalog> FetchModelCatalogAsync()
    {
        string apiKey = Settings.GetActiveApiKey(ProviderId);
        string? endpoint = NormalizeEndpoint(
            Settings.GetEndpoint(ProviderId, DefaultEndpoint));
        float timeoutSeconds = ApiTimeouts.Resolve(Settings.ApiTimeout);
        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        // 模型清單是玩家在設定頁手動觸發，失敗直接回報比在逾時內默默重試三次清楚。
        var options = new OpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(0) };
        if (!string.IsNullOrEmpty(endpoint))
        {
            options.Endpoint = new Uri(endpoint, UriKind.Absolute);
        }

        // 本地相容伺服器多半不驗證金鑰，但 SDK 不接受空憑證。
        var credential = new ApiKeyCredential(
            string.IsNullOrEmpty(apiKey) ? PlaceholderApiKey : apiKey);

        var list = new List<string>();
        Dictionary<string, int> contextWindows;
        Dictionary<string, bool>? visionSupport = null;
        Dictionary<string, int>? outputLimits = null;
        try
        {
            ClientResult<OpenAIModelCollection> result = await new OpenAIClient(credential, options)
                .GetOpenAIModelClient()
                .GetModelsAsync(timeoutCts.Token)
                .ConfigureAwait(false);

            foreach (OpenAIModel model in System.Linq.Enumerable.Where(result.Value, m => !string.IsNullOrEmpty(m?.Id)))
            {
                list.Add(model.Id);
            }
            string? rawJson = result.GetRawResponse()?.Content?.ToString();
            contextWindows = ParseContextWindows(rawJson);
            visionSupport = ParseVisionSupport(rawJson);
            outputLimits = ParseOutputLimits(rawJson);
        }
        catch (ClientResultException ex)
        {
            throw LLMErrorMapper.CreateException(
                ex.Status,
                $"Failed to fetch {ProviderId} models list: {RimLLMLog.SanitizeForLog(ex.Message, 200)}",
                innerException: ex);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new RimLLMException(
                LLMError.Timeout,
                string.Create(CultureInfo.InvariantCulture, $"Fetching {ProviderId} models list timed out after {timeoutSeconds} seconds."),
                ex);
        }
        catch (Exception ex)
        {
            throw new RimLLMException(
                LLMError.InvalidResponse,
                $"Failed to fetch {ProviderId} models list: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", ex);
        }
        return new ModelCatalog(list, contextWindows, visionSupport, outputLimits);
    }

    /// <summary>從 /models 原始 JSON 讀出「模型 id → 輸出上限」。目前只有 OpenRouter 回報（<c>top_provider.max_completion_tokens</c>，可能為 null）；沒回報的不收錄，格式不符回傳空集合。</summary>
    internal static Dictionary<string, int> ParseOutputLimits(string? json)
    {
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(json)) return limits;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return limits;
            }

            foreach (JsonElement model in data.EnumerateArray())
            {
                if (model.ValueKind == JsonValueKind.Object
                    && model.TryGetProperty("id", out JsonElement idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && model.TryGetProperty("top_provider", out JsonElement top)
                    && top.ValueKind == JsonValueKind.Object
                    && top.TryGetProperty("max_completion_tokens", out JsonElement value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out int tokens)
                    && tokens > 0
                    && !string.IsNullOrEmpty(idElement.GetString()))
                {
                    limits[idElement.GetString()!] = tokens;
                }
            }
        }
        catch (JsonException)
        {
            limits.Clear();
        }
        return limits;
    }

    /// <summary>從 /models 原始 JSON 讀出「模型 id → 是否支援圖片輸入」。目前只有 OpenRouter 回報（<c>architecture.input_modalities</c>）；沒回報的不收錄，格式不符回傳空集合。</summary>
    internal static Dictionary<string, bool> ParseVisionSupport(string? json)
    {
        var support = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(json)) return support;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return support;
            }

            foreach (JsonElement model in data.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object
                    || !model.TryGetProperty("id", out JsonElement idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || !model.TryGetProperty("architecture", out JsonElement arch)
                    || arch.ValueKind != JsonValueKind.Object
                    || !arch.TryGetProperty("input_modalities", out JsonElement input)
                    || input.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                string? id = idElement.GetString();
                if (string.IsNullOrEmpty(id)) continue;

                support[id] = System.Linq.Enumerable.Any(
                    input.EnumerateArray(),
                    m => m.ValueKind == JsonValueKind.String && m.GetString() == "image");
            }
        }
        catch (JsonException)
        {
            support.Clear();
        }
        return support;
    }

    /// <summary>
    /// /models 回應中各家表示上下文上限的欄位名稱：
    /// OpenRouter 用 <c>context_length</c>、Groq 用 <c>context_window</c>、vLLM 用 <c>max_model_len</c>。
    /// OpenAI 官方不回報，這類供應商的結果為空。
    /// </summary>
    private static readonly string[] ContextWindowFields = ["context_length", "context_window", "max_model_len"];

    /// <summary>從 /models 原始 JSON 讀出「模型 id → 上下文上限」；格式不符時回傳空集合。</summary>
    internal static Dictionary<string, int> ParseContextWindows(string? json)
    {
        var windows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(json)) return windows;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return windows;
            }

            foreach (JsonElement model in data.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object
                    || !model.TryGetProperty("id", out JsonElement idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string? id = idElement.GetString();
                if (string.IsNullOrEmpty(id)) continue;

                int tokens = TryReadContextWindowTokens(model);
                if (tokens > 0)
                {
                    windows[id] = tokens;
                }
            }
        }
        catch (JsonException)
        {
            windows.Clear();
        }
        return windows;
    }

    private static int TryReadContextWindowTokens(JsonElement model)
    {
        foreach (string field in ContextWindowFields)
        {
            if (model.TryGetProperty(field, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int tokens)
                && tokens > 0)
            {
                return tokens;
            }
        }
        return 0;
    }

    private const string PlaceholderApiKey = "not-required";
}
