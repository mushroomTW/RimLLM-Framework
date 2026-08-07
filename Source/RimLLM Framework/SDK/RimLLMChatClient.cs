extern alias bclasync;
extern alias ste;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// 綁定單一 Mod 的 IChatClient facade。框架核心功能（fallback、預算、佇列、
    /// 防濫用、用量統計）全部保留在此 client 內部，不允許繞過。
    /// 透過 RimLLMProvider.CreateChatClient(modId) 取得。
    /// </summary>
    public class RimLLMChatClient : IChatClient
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
                .GenerateResultAsync(request, verifyCaller: false)
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

        /// <summary>把串流 chunk / restart 事件 / 完成訊號橋接成 IAsyncEnumerable（C# 8 不支援 aliased IAsyncEnumerable 的 async iterator）。</summary>
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
                return new StreamUpdateEnumerator(_client, _messages, _options, cancellationToken);
            }
        }

        private sealed class StreamUpdateBridge
        {
            private readonly System.Collections.Concurrent.ConcurrentQueue<object> _queue =
                new System.Collections.Concurrent.ConcurrentQueue<object>();
            private readonly TaskCompletionSource<bool> _completed =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Push(ChatResponseUpdate update) => _queue.Enqueue(update);
            public void Complete() => _completed.TrySetResult(true);
            public void Fail(Exception ex) => _completed.TrySetException(ex);

            public async Task<ChatResponseUpdate> WaitNextAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    if (_queue.TryDequeue(out object item))
                    {
                        if (item is Exception ex)
                        {
                            throw ex;
                        }
                        return (ChatResponseUpdate)item;
                    }
                    if (_completed.Task.IsCompleted)
                    {
                        // 佇列已清空且流程已結束：串流終止。
                        return null;
                    }
                    Task winner = await Task.WhenAny(_completed.Task, Task.Delay(10, cancellationToken)).ConfigureAwait(false);
                    if (ReferenceEquals(winner, _completed.Task))
                    {
                        continue;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        private sealed class StreamUpdateEnumerator : bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
        {
            private readonly RimLLMChatClient _client;
            private readonly IEnumerable<ChatMessage> _messages;
            private readonly ChatOptions _options;
            private readonly CancellationToken _cancellationToken;
            private readonly StreamUpdateBridge _bridge = new StreamUpdateBridge();
            private bool _started;

            public StreamUpdateEnumerator(
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

            public ChatResponseUpdate Current { get; private set; }

            public async ste::System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
            {
                if (!_started)
                {
                    _started = true;
                    StartProducer();
                }
                ChatResponseUpdate next = await _bridge.WaitNextAsync(_cancellationToken).ConfigureAwait(false);
                if (next == null)
                {
                    return false;
                }
                Current = next;
                return true;
            }

            private void StartProducer()
            {
                var rimOptions = _options as RimLLMChatOptions;
                RimLLMRequest request = _client.Translate(_messages, _options, _cancellationToken);
                Action userRestart = rimOptions?.OnStreamRestart;
                request.OnStreamRestart = () =>
                {
                    // 供應商接手（restart）時先推送 marker update，再通知使用者清空顯示內容。
                    _bridge.Push(new ChatResponseUpdate
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
                                    _bridge.Push(new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new TextContent(chunk) }));
                                }
                            },
                            verifyCaller: false).ConfigureAwait(false);

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
                        _bridge.Push(finalUpdate);
                        _bridge.Complete();
                    }
                    catch (Exception ex)
                    {
                        _bridge.Fail(ex);
                    }
                }, _cancellationToken);
            }

            public ste::System.Threading.Tasks.ValueTask DisposeAsync()
            {
                return default;
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
                .GenerateResultAsync(request, verifyCaller: false)
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
