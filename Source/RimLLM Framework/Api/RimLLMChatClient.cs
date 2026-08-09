extern alias bclasync;
extern alias ste;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework
{
    /// <summary>
    /// 綁定單一 Mod 的 IChatClient facade。框架核心功能（fallback、預算、佇列、
    /// 防濫用、用量統計）全部保留在此 client 內部，不允許繞過。
    /// 透過 RimLLMProvider.CreateChatClient(modId) 取得。
    /// </summary>
    internal class RimLLMChatClient : IChatClient
    {
        private readonly RimLLMManager _manager;
        private readonly string _modId;

        internal RimLLMChatClient(RimLLMManager manager, string modId)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _modId = modId ?? throw new ArgumentNullException(nameof(modId));
        }

        /// <summary>內部存取（供 RimLLMClientExtensions 的結構化輸出路徑使用）。</summary>
        internal RimLLMManager Manager => _manager;
        internal string ModId => _modId;

        public ChatClientMetadata Metadata { get; } = new ChatClientMetadata("RimLLM", null, null);

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            RimLLMRequest request = Translate(messages, options, cancellationToken);
            RimLLMGenerationResult result = await _manager
                .GenerateResultAsync(request)
                .ConfigureAwait(false);
            return BuildResponse(result, options);
        }

        public bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            return new StreamUpdateEnumerable(this, messages, options, cancellationToken);
        }

        /// <summary>
        /// 把串流 chunk / restart 事件 / 完成訊號橋接成 IAsyncEnumerable。
        /// 生產端在第一次 GetAsyncEnumerator 時才啟動，避免沒有人列舉時就先送出請求。
        /// </summary>
        private sealed class StreamUpdateEnumerable : bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
        {
            private readonly RimLLMChatClient _client;
            private readonly IEnumerable<ChatMessage> _messages;
            private readonly ChatOptions _options;
            private readonly CancellationToken _cancellationToken;

            public StreamUpdateEnumerable(
                RimLLMChatClient client,
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
                var rimOptions = _options as RimLLMChatOptions;
                RimLLMRequest request = _client.Translate(_messages, _options, cancellationToken);
                Action userRestart = rimOptions?.OnStreamRestart;
                request.OnStreamRestart = () =>
                {
                    // 供應商接手（restart）時先推送 marker update，再通知使用者清空顯示內容。
                    writer.TryWrite(new ChatResponseUpdate
                    {
                        AdditionalProperties = new AdditionalPropertiesDictionary { ["rimllm_stream_restart"] = true }
                    });
                    userRestart?.Invoke();
                };

                Task.Run(async () =>
                {
                    try
                    {
                        RimLLMGenerationResult result = await _client.Manager.StreamResultAsync(
                            request,
                            chunk =>
                            {
                                if (!string.IsNullOrEmpty(chunk))
                                {
                                    writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new TextContent(chunk) }));
                                }
                            }).ConfigureAwait(false);

                        var finalUpdate = new ChatResponseUpdate
                        {
                            Role = ChatRole.Assistant,
                            ModelId = ComposeModelId(result)
                        };
                        finalUpdate.Contents.Add(new UsageContent(new UsageDetails
                        {
                            InputTokenCount = result.PromptTokens,
                            OutputTokenCount = result.CompletionTokens,
                            CachedInputTokenCount = result.CachedPromptTokens
                        }));
                        writer.TryWrite(finalUpdate);
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
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _linkedCts.Dispose();
                }
            }
        }

        /// <summary>
        /// 結構化輸出完整路徑（供 RimLLMClientExtensions 使用）：走 manager 核心流程
        /// （schema、JSON repair、LLM-assisted double-repair），框架功能不繞過。
        /// </summary>
        internal async Task<T> GenerateObjectAsync<T>(
            IEnumerable<ChatMessage> messages,
            RimLLMChatOptions options,
            CancellationToken cancellationToken)
        {
            RimLLMRequest request = Translate(messages, options, cancellationToken);
            request.ResponseType = typeof(T);
            RimLLMGenerationResult result = await _manager
                .GenerateResultAsync(request)
                .ConfigureAwait(false);
            return _manager.DeserializeStructured<T>(result.Text, _manager.Settings, request);
        }

        internal RimLLMRequest Translate(
            IEnumerable<ChatMessage> messages,
            ChatOptions options,
            CancellationToken cancellationToken)
        {
            var messagesList = new List<ChatMessage>(messages ?? new List<ChatMessage>());

            RimLLMChatOptions rimOptions = options as RimLLMChatOptions;
            string systemPrompt = null;
            if (messagesList.Count > 0 && messagesList[0]?.Role == ChatRole.System)
            {
                systemPrompt = messagesList[0].Text;
            }

            return new RimLLMRequest
            {
                ModId = _modId,
                Messages = messagesList,
                SystemPrompt = systemPrompt,
                CachedContext = rimOptions?.CachedContext,
                EnableContextCaching = rimOptions?.EnableContextCaching ?? false,
                Temperature = options?.Temperature,
                MaxOutputTokens = options?.MaxOutputTokens,
                ReasoningEffort = options?.Reasoning?.Effort,
                DisableReasoning = rimOptions?.DisableReasoning ?? false,
                Priority = rimOptions?.Priority ?? 0,
                MinFallbackLevel = rimOptions?.MinFallbackLevel,
                PreferredModelId = options?.ModelId,
                OnStreamRestart = rimOptions?.OnStreamRestart,
                CancellationToken = cancellationToken
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
            return new ChatResponse(
                new ChatMessage(ChatRole.Assistant, result.Text))
            {
                Usage = usageDetails,
                ModelId = ComposeModelId(result),
                AdditionalProperties = options?.AdditionalProperties
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

        public object GetService(Type serviceType, object serviceKey = null)
        {
            if (serviceType == typeof(ChatClientMetadata))
            {
                return Metadata;
            }
            return null;
        }

        public void Dispose()
        {
        }
    }
}
