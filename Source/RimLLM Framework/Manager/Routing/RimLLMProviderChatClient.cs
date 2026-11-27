using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 單一「供應商 + 模型」候選的 MEAI 門面。由 <see cref="RimLLMFailoverChatClient"/>
/// 每次選擇候選時建立。
/// </summary>
/// <remarks>
/// 所有依賴供應商身分的請求整形都在這一層完成——工具能力過濾、schema 方言、
/// 原生 schema 被拒後的降級重試。這也是它必須存在的原因：MEAI 的
/// <c>RoutingContext</c> 是唯讀的，router 無法逐次改寫請求，只能改由被選中的
/// 候選自己承擔。
///
/// 沒有內層 client，因此不是 DelegatingChatClient：實際的 provider client 在每次
/// 呼叫時建立並以 using 釋放。
/// </remarks>
internal sealed class RimLLMProviderChatClient : IChatClient
{
    /// <summary>
    /// 串流橋接的有界 channel 容量。消費端慢時生產端在 WriteAsync 上等待，
    /// 這個數字決定最多緩衝多少個 update；64 已足夠吸收 provider 的突發產出。
    /// </summary>
    internal const int StreamBufferCapacity = 64;

    private readonly ILLMProvider _provider;
    private readonly string _model;
    private readonly IRimLLMSettings _settings;
    private readonly Action<UsageTokens>? _onUsage;
    /// <summary>用量帳本，由路由層傳入；為 null 時不記帳（單元測試直接建立本類別時）。</summary>
    private readonly RimLLMUsageTracker? _usageTracker;

    public RimLLMProviderChatClient(
        ILLMProvider provider,
        string model,
        IRimLLMSettings settings,
        Action<UsageTokens>? onUsage = null,
        RimLLMUsageTracker? usageTracker = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = model;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _onUsage = onUsage;
        _usageTracker = usageTracker;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatOptions? effective = StripUnsupportedTools(_provider, options, out bool toolsStripped);
        var messageList = new List<ChatMessage>(messages ?? new List<ChatMessage>());

        ChatResponse response = await GenerateAsync(messageList, effective, cancellationToken).ConfigureAwait(false);

        // 只覆寫框架真正要改的欄位：把 ModelId 換成 "供應商:模型" 複合識別，
        // 讓呼叫端在 fallback 之後仍分辨得出實際是誰回的。其餘一律原樣放行。
        response.ModelId = ComposeModelId(_provider.ProviderId, _model);
        foreach (ChatMessage message in response.Messages) TagCandidate(message.Contents, response.ModelId);
        if (toolsStripped)
        {
            MarkToolsStripped(response.AdditionalProperties ??= []);
        }
        return response;
    }

    private async Task<ChatResponse> GenerateAsync(
        IList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        Type? responseType = RimLLMChatOptions.GetResponseType(options);
        if ((responseType != null || HasCallerSchema(options)) && IsNativeStructuredProvider(_provider))
        {
            try
            {
                using IChatClient nativeClient = _provider.CreateChatClient(_model);
                return await RimLLMChatClientExecutor.GenerateAsync(
                    new GenerationParams(
                        nativeClient,
                        messages,
                        options,
                        _model,
                        UseNativeSchema: true,
                        _provider.ProviderId,
                        _settings.ApiTimeout,
                        cancellationToken) { UsageTracker = _usageTracker, BillsTimeouts = _provider.RequiresApiKey },
                    onUsage: _onUsage).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNativeSchemaRejected(ex))
            {
                // 原生 schema 被拒，降級成「以提示詞要求 JSON」重試一次。
                // 必須直接走提示式路徑：遞迴呼叫 GenerateAsync 本身，
                // options 沒變、判斷條件也沒變，服務端只要持續拒絕就會無限重打 API。
                return await GenerateWithPromptSchemaAsync(messages, options, responseType, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return await GenerateWithPromptSchemaAsync(messages, options, responseType, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 不送原生 schema：需要結構化輸出時改以提示詞要求 JSON（<paramref name="responseType"/> 為 null 則原樣送出）。
    /// 這是原生 schema 被拒後唯一允許的重試路徑，本身不再做任何降級重試。
    /// </summary>
    private async Task<ChatResponse> GenerateWithPromptSchemaAsync(
        IList<ChatMessage> messages,
        ChatOptions? options,
        Type? responseType,
        CancellationToken cancellationToken)
    {
        using IChatClient client = _provider.CreateChatClient(_model);
        return await RimLLMChatClientExecutor.GenerateAsync(
            new GenerationParams(
                client,
                ApplyJsonSchemaInstructions(messages, responseType, options),
                DowngradeCallerSchema(options),
                _model,
                UseNativeSchema: false,
                _provider.ProviderId,
                _settings.ApiTimeout,
                cancellationToken) { UsageTracker = _usageTracker, BillsTimeouts = _provider.RequiresApiKey },
            onUsage: _onUsage).ConfigureAwait(false);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return new StreamUpdateEnumerable(this, messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        // 帶 key 的查詢代表呼叫端要的是具名服務，這一層沒有提供任何具名服務。
        if (serviceKey != null) return null;

        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata(_provider.ProviderId, providerUri: null, _model);
        }
        if (serviceType == typeof(ILLMProvider)) return _provider;
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // provider client 在每次呼叫時建立並釋放，這裡沒有需要保留的資源。
    }

    /// <summary>
    /// 把 executor 的 <c>Action&lt;string&gt;</c> 分塊回呼橋接成 MEAI 的 IAsyncEnumerable。
    /// 生產端在第一次 GetAsyncEnumerator 時才啟動，避免沒有人列舉時就先送出請求。
    /// </summary>
    private sealed class StreamUpdateEnumerable : IAsyncEnumerable<ChatResponseUpdate>
    {
        private readonly RimLLMProviderChatClient _client;
        private readonly IEnumerable<ChatMessage> _messages;
        private readonly ChatOptions? _options;
        private readonly CancellationToken _cancellationToken;

        public StreamUpdateEnumerable(
            RimLLMProviderChatClient client,
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken)
        {
            _client = client;
            _messages = messages;
            _options = options;
            _cancellationToken = cancellationToken;
        }

        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            // GetStreamingResponseAsync 與 await foreach（WithCancellation）兩邊的 token 都要生效。
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);

            try
            {
                // 有界 channel：消費端慢時生產端在 WriteAsync 上等待，避免遠端資料被無界緩衝。
                var channel = System.Threading.Channels.Channel.CreateBounded<ChatResponseUpdate>(
                    new System.Threading.Channels.BoundedChannelOptions(StreamBufferCapacity)
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
                    });

                System.Threading.Tasks.Task producerTask = StartProducer(channel.Writer, linkedCts.Token);

                return new StreamUpdateEnumerator(
                    channel.Reader.ReadAllAsync(linkedCts.Token).GetAsyncEnumerator(linkedCts.Token),
                    linkedCts,
                    producerTask);
            }
            catch
            {
                linkedCts.Dispose();
                throw;
            }
        }

        private System.Threading.Tasks.Task StartProducer(
            System.Threading.Channels.ChannelWriter<ChatResponseUpdate> writer,
            CancellationToken cancellationToken)
        {
            ChatOptions? effective = StripUnsupportedTools(_client._provider, _options, out bool toolsStripped);
            var messageList = new List<ChatMessage>(_messages ?? new List<ChatMessage>());
            Type? responseType = RimLLMChatOptions.GetResponseType(effective);
            bool useNativeSchema = (responseType != null || HasCallerSchema(effective)) &&
                                   _client.IsNativeStructuredProvider(_client._provider);

            // 串流沒有原生 schema 的降級重試（內容一旦開始吐出就無法重來），
            // 因此不走原生 schema 時就直接把 JSON 要求寫進提示詞。
            IList<ChatMessage> providerMessages = useNativeSchema
                ? messageList
                : ApplyJsonSchemaInstructions(messageList, responseType, effective);
            if (!useNativeSchema)
            {
                effective = DowngradeCallerSchema(effective);
            }

            string composedModelId = ComposeModelId(_client._provider.ProviderId, _client._model);

            return System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    using IChatClient client = _client._provider.CreateChatClient(_client._model);
                    await RimLLMChatClientExecutor.StreamAsync(
                        new GenerationParams(
                            client,
                            providerMessages,
                            effective,
                            _client._model,
                            useNativeSchema,
                            _client._provider.ProviderId,
                            _client._settings.ApiTimeout,
                            cancellationToken) { UsageTracker = _client._usageTracker, BillsTimeouts = _client._provider.RequiresApiKey },
                        async update =>
                        {
                            // 唯一的改寫：把 ModelId 換成 "供應商:模型" 複合識別，
                            // 讓呼叫端在 fallback 之後仍分辨得出實際是誰回的。
                            // 工具呼叫、FinishReason 與 UsageContent 都由 provider 的
                            // update 自己帶著，框架不另外合成收尾 update。
                            update.ModelId = composedModelId;
                            TagCandidate(update.Contents, composedModelId);
                            if (toolsStripped)
                            {
                                MarkToolsStripped(update.AdditionalProperties ??= []);
                            }
                            await writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
                        },
                        onUsage: _client._onUsage).ConfigureAwait(false);

                    writer.TryComplete();
                }
                catch (Exception ex)
                {
                    writer.TryComplete(ex);
                }
            }, cancellationToken);
        }
    }

    /// <summary>
    /// 包住 channel 的列舉器，只做兩件事：解開 ChannelClosedException 還原生產端的原始例外
    /// （呼叫端 catch 的是 RimLLMException，不能讓它變成 channel 的內部型別），以及等待生產者收尾並釋放連動 CTS。
    /// </summary>
    private sealed class StreamUpdateEnumerator : IAsyncEnumerator<ChatResponseUpdate>
    {
        private readonly IAsyncEnumerator<ChatResponseUpdate> _inner;
        private readonly CancellationTokenSource _linkedCts;
        private readonly System.Threading.Tasks.Task _producerTask;

        public StreamUpdateEnumerator(
            IAsyncEnumerator<ChatResponseUpdate> inner,
            CancellationTokenSource linkedCts,
            System.Threading.Tasks.Task producerTask)
        {
            _inner = inner;
            _linkedCts = linkedCts;
            _producerTask = producerTask;
        }

        public ChatResponseUpdate Current => _inner.Current;

        public async ValueTask<bool> MoveNextAsync()
        {
            try
            {
                return await _inner.MoveNextAsync().ConfigureAwait(false);
            }
            catch (System.Threading.Channels.ChannelClosedException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw; // 不會執行到，僅滿足編譯器
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _linkedCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 若 CTS 已被釋放則安全忽略，不拋出異常
            }

            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // 確保背景生產者已結束 HTTP 串流與用量記帳後，外層佇列與預算才會被歸還。
                try
                {
                    await _producerTask.ConfigureAwait(false);
                }
                catch
                {
                    // 例外已透過 channel 或取消傳遞給消費端，此處只等待其收尾完成。
                }
                _linkedCts.Dispose();
            }
        }
    }

    /// <summary>掛在工具呼叫上的候選識別，續輪請求靠它認出上一步由誰回應。</summary>
    internal const string CandidateKey = "rimllm_candidate";

    private static void TagCandidate(IEnumerable<AIContent> contents, string modelId)
    {
        foreach (FunctionCallContent call in contents.OfType<FunctionCallContent>())
        {
            (call.AdditionalProperties ??= [])[CandidateKey] = modelId;
        }
    }

    internal static string ComposeModelId(string providerId, string modelName)
    {
        if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelName))
        {
            return providerId + ":" + modelName;
        }
        return modelName ?? string.Empty;
    }

    private static bool IsNativeSchemaRejected(Exception exception)
    {
        if (exception == null || exception is OperationCanceledException)
        {
            return false;
        }

        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is RimLLMException rimException && rimException.IsSchemaRejection)
            {
                return true;
            }
        }

        // 只看例外鏈的訊息，不看 ToString()：後者含堆疊，而堆疊裡的 RimLLMSchemaBuilder
        // 本身就含 "schema"，任何訊息帶 "invalid"／"400" 的例外都會被誤判成 schema 被拒。
        var messageBuilder = new System.Text.StringBuilder();
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            messageBuilder.Append(current.Message).Append('\n');
        }
        string message = messageBuilder.ToString().ToLowerInvariant();
        bool mentionsSchema = message.Contains("schema", StringComparison.Ordinal) ||
                              message.Contains("response_format", StringComparison.Ordinal) ||
                              message.Contains("response format", StringComparison.Ordinal) ||
                              message.Contains("structured output", StringComparison.Ordinal);
        bool looksLikeRejection = message.Contains("400", StringComparison.Ordinal) ||
                                  message.Contains("invalid", StringComparison.Ordinal) ||
                                  message.Contains("unsupported", StringComparison.Ordinal) ||
                                  message.Contains("not support", StringComparison.Ordinal) ||
                                  message.Contains("unrecognized", StringComparison.Ordinal);
        return mentionsSchema && looksLikeRejection;
    }

    /// <summary>
    /// 供應商不支援原生工具呼叫時移除 Tools/ToolMode，並留下警告。
    /// 直接把 tools 送給不認得的供應商會被靜默忽略，呼叫端只會拿到一段散文而不知道工具沒送出去。
    /// 回應上會額外標記 <see cref="RimLLMClientExtensions.ToolsStrippedKey"/>：
    /// 只寫 log 的話 Agent 端拿到的是一個永遠不呼叫工具的回應，程式上無從分辨。
    /// </summary>
    private static ChatOptions? StripUnsupportedTools(ILLMProvider provider, ChatOptions? options, out bool stripped)
    {
        stripped = false;
        if (options?.Tools == null || options.Tools.Count == 0) return options;
        if (provider?.Capabilities?.SupportsFunctionCalling == true) return options;
        stripped = true;

        if (RimLLMLog.Enabled)
        {
            RimLLMLog.Warning(
                $"[RimLLM] Provider {provider?.ProviderId} does not support native tool calling; {options.Tools.Count} tool(s) were stripped from this request.");
        }

        ChatOptions clone = options.Clone();
        clone.Tools = null;
        clone.ToolMode = null;
        return clone;
    }

    private static void MarkToolsStripped(AdditionalPropertiesDictionary properties)
    {
        properties[RimLLMClientExtensions.ToolsStrippedKey] = true;
    }

    /// <summary>
    /// 供應商沒有原生結構化輸出時，改以提示詞要求模型只回傳 JSON。
    /// </summary>
    private static IList<ChatMessage> ApplyJsonSchemaInstructions(IList<ChatMessage> messages, Type? responseType, ChatOptions? options)
    {
        string schemaInstructions;
        if (responseType != null)
        {
            schemaInstructions =
                "\n\n[結構化輸出要求：只能回傳符合下列結構的原始 JSON，不要加入 Markdown code fence 或其他說明。範例：\n" +
                RimLLMJsonHelper.GetSampleJson(responseType) + "]";
        }
        else if (options?.ResponseFormat is ChatResponseFormatJson { Schema: { } callerSchema })
        {
            schemaInstructions =
                "\n\n[結構化輸出要求：只能回傳符合下列 JSON Schema 的原始 JSON，不要加入 Markdown code fence 或其他說明。JSON Schema：\n" +
                callerSchema.GetRawText() + "]";
        }
        else
        {
            return messages;
        }

        var copy = new List<ChatMessage>(messages ?? []);
        int sysIdx = copy.FindIndex(m => m != null && m.Role == ChatRole.System);
        if (sysIdx >= 0)
        {
            copy[sysIdx] = new ChatMessage(ChatRole.System, (copy[sysIdx].Text ?? string.Empty) + schemaInstructions);
        }
        else
        {
            copy.Insert(0, new ChatMessage(ChatRole.System, schemaInstructions));
        }
        return copy;
    }

    /// <summary>呼叫端自行設定了帶 schema 的 ResponseFormat（例如 MEAI 的 GetResponseAsync&lt;T&gt;），而不是走框架的結構化輸出。</summary>
    private static bool HasCallerSchema(ChatOptions? options)
    {
        return RimLLMChatOptions.GetResponseType(options) == null &&
               options?.ResponseFormat is ChatResponseFormatJson { Schema: not null };
    }

    /// <summary>
    /// 不走原生 schema 時，呼叫端的 schema 已改由提示詞傳達，這裡把回應格式降成單純的 JSON 模式，
    /// 避免把供應商不接受（或剛剛拒絕）的 json_schema 再送一次。
    /// </summary>
    private static ChatOptions? DowngradeCallerSchema(ChatOptions? options)
    {
        if (options is null || !HasCallerSchema(options)) return options;

        ChatOptions clone = options.Clone();
        clone.ResponseFormat = ChatResponseFormat.Json;
        return clone;
    }

    private bool IsNativeStructuredProvider(ILLMProvider provider)
    {
        return provider?.Capabilities?.SupportsNativeStructuredOutput == true &&
               _settings.EnableNativeSchema;
    }
}
