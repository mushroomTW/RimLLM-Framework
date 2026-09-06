extern alias bclasync;
extern alias ste;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
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
    /// 呼叫時建立並以 using 釋放，與先前 pipeline 的作法一致。
    /// </remarks>
    internal sealed class RimLLMProviderChatClient : IChatClient
    {
        private readonly ILLMProvider _provider;
        private readonly string _model;
        private readonly IRimLLMSettings _settings;
        private readonly string _modId;

        public RimLLMProviderChatClient(
            ILLMProvider provider,
            string model,
            IRimLLMSettings settings,
            string modId)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _model = model;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _modId = modId;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            RimLLMRequest request = StripUnsupportedTools(
                _provider, Translate(messages, options, cancellationToken));

            RimLLMGenerationResult result = await GenerateAsync(request).ConfigureAwait(false);
            return BuildResponse(result, options);
        }

        private async Task<RimLLMGenerationResult> GenerateAsync(RimLLMRequest request)
        {
            bool useNativeSchema = request.ResponseType != null && IsNativeStructuredProvider(_provider);
            if (useNativeSchema)
            {
                try
                {
                    using (IChatClient nativeClient = _provider.CreateChatClient(_model))
                    {
                        return await RimLLMChatClientExecutor.GenerateAsync(
                            nativeClient,
                            request,
                            _model,
                            useNativeSchema: true,
                            _provider.ProviderId,
                            _settings.ApiTimeout).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (IsNativeSchemaRejected(ex))
                {
                    return await GenerateWithoutNativeSchemaAsync(request).ConfigureAwait(false);
                }
            }

            using (IChatClient client = _provider.CreateChatClient(_model))
            {
                return await RimLLMChatClientExecutor.GenerateAsync(
                    client,
                    PrepareRequestForProvider(_provider, request),
                    _model,
                    useNativeSchema: false,
                    _provider.ProviderId,
                    _settings.ApiTimeout).ConfigureAwait(false);
            }
        }

        private async Task<RimLLMGenerationResult> GenerateWithoutNativeSchemaAsync(RimLLMRequest request)
        {
            RimLLMRequest fallbackRequest = PrepareRequestForProvider(_provider, request, forceJsonFallback: true);

            using (IChatClient client = _provider.CreateChatClient(_model))
            {
                return await RimLLMChatClientExecutor.GenerateAsync(
                    client,
                    fallbackRequest,
                    _model,
                    useNativeSchema: false,
                    _provider.ProviderId,
                    _settings.ApiTimeout).ConfigureAwait(false);
            }
        }

        public bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            return new StreamUpdateEnumerable(this, messages, options, cancellationToken);
        }

        public object GetService(Type serviceType, object serviceKey = null)
        {
            return serviceType == typeof(ILLMProvider) ? _provider : null;
        }

        public void Dispose()
        {
            // provider client 在每次呼叫時建立並釋放，這裡沒有需要保留的資源。
        }

        /// <summary>
        /// 把 executor 的 <c>Action&lt;string&gt;</c> 分塊回呼橋接成 MEAI 的 IAsyncEnumerable。
        /// 生產端在第一次 GetAsyncEnumerator 時才啟動，避免沒有人列舉時就先送出請求。
        /// </summary>
        private sealed class StreamUpdateEnumerable : bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
        {
            private readonly RimLLMProviderChatClient _client;
            private readonly IEnumerable<ChatMessage> _messages;
            private readonly ChatOptions _options;
            private readonly CancellationToken _cancellationToken;

            public StreamUpdateEnumerable(
                RimLLMProviderChatClient client,
                IEnumerable<ChatMessage> messages,
                ChatOptions options,
                CancellationToken cancellationToken)
            {
                _client = client;
                _messages = messages;
                _options = options;
                _cancellationToken = cancellationToken;
            }

            public bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
            {
                // GetStreamingResponseAsync 與 await foreach（WithCancellation）兩邊的 token 都要生效。
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);

                // 無界 channel + TryWrite：不需要 ValueTask，因此不必碰 ste 別名。
                var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>(
                    new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

                StartProducer(channel.Writer, linkedCts.Token);

                // ReadAllAsync 回傳的 IAsyncEnumerable 與 bclasync 別名指向同一顆組件，可直接沿用。
                return new StreamUpdateEnumerator(
                    channel.Reader.ReadAllAsync(linkedCts.Token).GetAsyncEnumerator(linkedCts.Token),
                    linkedCts);
            }

            private void StartProducer(
                System.Threading.Channels.ChannelWriter<ChatResponseUpdate> writer,
                CancellationToken cancellationToken)
            {
                RimLLMRequest request = StripUnsupportedTools(
                    _client._provider, _client.Translate(_messages, _options, cancellationToken));

                Task.Run(async () =>
                {
                    try
                    {
                        using (IChatClient client = _client._provider.CreateChatClient(_client._model))
                        {
                            RimLLMGenerationResult result = await RimLLMChatClientExecutor.StreamAsync(
                                client,
                                _client.PrepareRequestForProvider(_client._provider, request),
                                _client._model,
                                request.ResponseType != null && _client.IsNativeStructuredProvider(_client._provider),
                                _client._provider.ProviderId,
                                chunk =>
                                {
                                    if (!string.IsNullOrEmpty(chunk))
                                    {
                                        writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new TextContent(chunk) }));
                                    }
                                },
                                _client._settings.ApiTimeout).ConfigureAwait(false);

                            var finalUpdate = new ChatResponseUpdate
                            {
                                Role = ChatRole.Assistant,
                                ModelId = ComposeModelId(result)
                            };
                            if (result.HasToolCalls)
                            {
                                // 工具呼叫沒有文字 chunk，只能靠收尾 update 交給上層的工具執行迴圈。
                                foreach (FunctionCallContent toolCall in System.Linq.Enumerable.OfType<FunctionCallContent>(result.Contents))
                                {
                                    finalUpdate.Contents.Add(toolCall);
                                }
                                finalUpdate.FinishReason = ChatFinishReason.ToolCalls;
                            }
                            finalUpdate.Contents.Add(new UsageContent(new UsageDetails
                            {
                                InputTokenCount = result.PromptTokens,
                                OutputTokenCount = result.CompletionTokens,
                                CachedInputTokenCount = result.CachedPromptTokens
                            }));
                            writer.TryWrite(finalUpdate);
                            writer.TryComplete();
                        }
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
        /// （呼叫端 catch 的是 RimLLMException，不能讓它變成 channel 的內部型別），以及釋放連動 CTS。
        /// </summary>
        private sealed class StreamUpdateEnumerator : bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
        {
            private readonly bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> _inner;
            private readonly CancellationTokenSource _linkedCts;

            public StreamUpdateEnumerator(
                bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> inner,
                CancellationTokenSource linkedCts)
            {
                _inner = inner;
                _linkedCts = linkedCts;
            }

            public ChatResponseUpdate Current => _inner.Current;

            public async ste::System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
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

            public async ste::System.Threading.Tasks.ValueTask DisposeAsync()
            {
                try
                {
                    _linkedCts.Cancel();
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
                    _linkedCts.Dispose();
                }
            }
        }

        internal RimLLMRequest Translate(
            IEnumerable<ChatMessage> messages,
            ChatOptions options,
            CancellationToken cancellationToken)
        {
            var messagesList = new List<ChatMessage>(messages ?? new List<ChatMessage>());

            string systemPrompt = null;
            if (messagesList.Count > 0 && messagesList[0]?.Role == ChatRole.System)
            {
                systemPrompt = messagesList[0].Text;
            }

            // 框架欄位一律從 AdditionalProperties 取，不再對 RimLLMChatOptions 做型別轉換：
            // 呼叫端用純 ChatOptions 塞鍵也能生效，且前方中介層 clone 掉子類別型別不會遺失設定。
            return new RimLLMRequest
            {
                ModId = _modId,
                Messages = messagesList,
                SystemPrompt = systemPrompt,
                SourceOptions = options,
                CachedContext = RimLLMChatOptions.GetCachedContext(options),
                EnableContextCaching = RimLLMChatOptions.GetEnableContextCaching(options),
                Temperature = options?.Temperature,
                MaxOutputTokens = options?.MaxOutputTokens,
                ReasoningEffort = options?.Reasoning?.Effort,
                DisableReasoning = RimLLMChatOptions.GetDisableReasoning(options),
                Priority = RimLLMChatOptions.GetPriority(options),
                MinFallbackLevel = RimLLMChatOptions.GetMinFallbackLevel(options),
                PreferredModelId = options?.ModelId,
                ResponseType = RimLLMChatOptions.GetResponseType(options),
                CancellationToken = cancellationToken,
                Tools = options?.Tools,
                ToolMode = options?.ToolMode
            };
        }

        internal static ChatResponse BuildResponse(RimLLMGenerationResult result, ChatOptions options)
        {
            var usageDetails = new UsageDetails
            {
                InputTokenCount = result.PromptTokens,
                OutputTokenCount = result.CompletionTokens,
                CachedInputTokenCount = result.CachedPromptTokens
            };

            // 只有真的帶工具呼叫時才改用原始 Contents；否則一律沿用 result.Text，
            // 否則 executor 已組好的 <think> 推理封裝會因 ChatResponse.Text 只串接 TextContent 而遺失。
            ChatMessage assistantMessage;
            if (result.HasToolCalls)
            {
                assistantMessage = new ChatMessage(ChatRole.Assistant, result.Contents);
            }
            else
            {
                assistantMessage = new ChatMessage(ChatRole.Assistant, result.Text);
            }

            return new ChatResponse(assistantMessage)
            {
                Usage = usageDetails,
                ModelId = ComposeModelId(result),
                FinishReason = result.HasToolCalls ? ChatFinishReason.ToolCalls : (ChatFinishReason?)null
            };
        }

        /// <summary>組合成 "ProviderId:ModelName" 複合識別，供呼叫端追蹤實際使用的供應商。</summary>
        internal static string ComposeModelId(RimLLMGenerationResult result)
        {
            if (!string.IsNullOrEmpty(result.ProviderId) && !string.IsNullOrEmpty(result.ModelName))
            {
                return result.ProviderId + ":" + result.ModelName;
            }
            return result.ModelName ?? string.Empty;
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

        /// <summary>
        /// 供應商不支援原生工具呼叫時移除 Tools/ToolMode，並留下警告。
        /// 直接把 tools 送給不認得的供應商會被靜默忽略，呼叫端只會拿到一段散文而不知道工具沒送出去。
        /// </summary>
        private static RimLLMRequest StripUnsupportedTools(ILLMProvider provider, RimLLMRequest request)
        {
            if (request?.Tools == null || request.Tools.Count == 0) return request;
            if (provider?.Capabilities?.SupportsFunctionCalling == true) return request;

            RimLLMLog.Warning(
                $"[RimLLM] 供應商 {provider?.ProviderId} 不支援原生工具呼叫，本次請求的 {request.Tools.Count} 個工具已被移除。");

            RimLLMRequest clone = request.Clone();
            clone.Tools = null;
            clone.ToolMode = null;
            return clone;
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
            return provider?.Capabilities?.SupportsNativeStructuredOutput == true &&
                   _settings.EnableNativeSchema;
        }
    }
#pragma warning restore S101
}
