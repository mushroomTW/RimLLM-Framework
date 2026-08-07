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
            return new StreamingEnumerable(BuildStreamingUpdatesAsync(messages, options, cancellationToken));
        }

        private async Task<List<ChatResponseUpdate>> BuildStreamingUpdatesAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options,
            CancellationToken cancellationToken)
        {
            RimLLMRequest request = Translate(messages, options, cancellationToken);
            var chunks = new List<string>();
            RimLLMGenerationResult result = await _manager
                .StreamResultAsync(request, chunk => chunks.Add(chunk), verifyCaller: false)
                .ConfigureAwait(false);

            var updates = new List<ChatResponseUpdate>();
            foreach (string chunk in chunks)
            {
                updates.Add(CreateTextUpdate(chunk));
            }

            // 供應商若完全沒吐出 chunk（例如直接給整段結果），把完整文字作為單一更新送出。
            if (chunks.Count == 0 && !string.IsNullOrEmpty(result.Text))
            {
                updates.Add(CreateTextUpdate(result.Text));
            }

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
            updates.Add(finalUpdate);
            return updates;
        }

        private static ChatResponseUpdate CreateTextUpdate(string text)
        {
            var update = new ChatResponseUpdate();
            update.Contents.Add(new TextContent(text));
            return update;
        }

        /// <summary>把預先收集好的更新清單包成 IAsyncEnumerable（C# 8 不支援 aliased IAsyncEnumerable 的 async iterator）。</summary>
        private sealed class StreamingEnumerable : bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
        {
            private readonly Task<List<ChatResponseUpdate>> _updatesTask;

            public StreamingEnumerable(Task<List<ChatResponseUpdate>> updatesTask)
            {
                _updatesTask = updatesTask;
            }

            public bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
            {
                return new StreamingEnumerator(_updatesTask);
            }

            private sealed class StreamingEnumerator : bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
            {
                private readonly Task<List<ChatResponseUpdate>> _updatesTask;
                private List<ChatResponseUpdate> _updates;
                private int _index = -1;

                public StreamingEnumerator(Task<List<ChatResponseUpdate>> updatesTask)
                {
                    _updatesTask = updatesTask;
                }

                public ChatResponseUpdate Current => _updates[_index];

                public async ste::System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
                {
                    if (_updates == null)
                    {
                        _updates = await _updatesTask.ConfigureAwait(false);
                    }
                    _index++;
                    return _index < _updates.Count;
                }

                public ste::System.Threading.Tasks.ValueTask DisposeAsync()
                {
                    return default;
                }
            }
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
            return null;
        }

        public void Dispose()
        {
        }
    }
}
