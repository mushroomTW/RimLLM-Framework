extern alias bclasync;
extern alias ste;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 優先權佇列與並行限流。高 Priority 的請求先取得名額，同優先級則先到先得。
    /// </summary>
    /// <remarks>
    /// 這是堆疊中最內的一層：名額必須留給真正要打 API 的請求，因此排在快取、
    /// 防濫用與預算之後——被那三者攔下的請求不該佔用名額。
    /// </remarks>
    internal sealed class RimLLMRequestQueueChatClient : DelegatingChatClient
    {
        private readonly RimLLMRequestQueue _queue;

        public RimLLMRequestQueueChatClient(IChatClient innerClient, RimLLMRequestQueue queue)
            : base(innerClient)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            using (await _queue
                .AcquireSlotAsync(RimLLMChatOptions.GetPriority(options), cancellationToken)
                .ConfigureAwait(false))
            {
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
        }

        public override bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            // 名額必須橫跨整段列舉。若在這裡就 await 取得名額再回傳 enumerable，
            // 名額會在呼叫端還沒開始列舉時就釋放——併發上限對串流請求等同失效。
            // 因此改由列舉器在第一次 MoveNextAsync 取得名額，並在 DisposeAsync 歸還。
            return new QueuedStreamEnumerable(this, messages, options, cancellationToken);
        }

        private sealed class QueuedStreamEnumerable : bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
        {
            private readonly RimLLMRequestQueueChatClient _owner;
            private readonly IEnumerable<ChatMessage> _messages;
            private readonly ChatOptions _options;
            private readonly CancellationToken _cancellationToken;

            public QueuedStreamEnumerable(
                RimLLMRequestQueueChatClient owner,
                IEnumerable<ChatMessage> messages,
                ChatOptions options,
                CancellationToken cancellationToken)
            {
                _owner = owner;
                _messages = messages;
                _options = options;
                _cancellationToken = cancellationToken;
            }

            public bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
            {
                var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
                return new QueuedStreamEnumerator(_owner, _messages, _options, linked);
            }
        }

        private sealed class QueuedStreamEnumerator : bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
        {
            private readonly RimLLMRequestQueueChatClient _owner;
            private readonly IEnumerable<ChatMessage> _messages;
            private readonly ChatOptions _options;
            private readonly CancellationTokenSource _linkedCts;

            private IDisposable _slot;
            private bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> _inner;

            public QueuedStreamEnumerator(
                RimLLMRequestQueueChatClient owner,
                IEnumerable<ChatMessage> messages,
                ChatOptions options,
                CancellationTokenSource linkedCts)
            {
                _owner = owner;
                _messages = messages;
                _options = options;
                _linkedCts = linkedCts;
            }

            public ChatResponseUpdate Current => _inner != null ? _inner.Current : null;

            public async ste::System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
            {
                if (_inner == null)
                {
                    _slot = await _owner._queue
                        .AcquireSlotAsync(RimLLMChatOptions.GetPriority(_options), _linkedCts.Token)
                        .ConfigureAwait(false);

                    _inner = _owner
                        .StreamInner(_messages, _options, _linkedCts.Token)
                        .GetAsyncEnumerator(_linkedCts.Token);
                }

                return await _inner.MoveNextAsync().ConfigureAwait(false);
            }

            public async ste::System.Threading.Tasks.ValueTask DisposeAsync()
            {
                try
                {
                    if (_inner != null)
                    {
                        await _inner.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    // 名額務必歸還，包含呼叫端中途放棄列舉的情形。
                    _slot?.Dispose();
                    _linkedCts.Dispose();
                }
            }
        }

        /// <summary>
        /// 巢狀列舉器無法直接寫 base.GetStreamingResponseAsync，經由這裡轉呼叫。
        /// </summary>
        private bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> StreamInner(
            IEnumerable<ChatMessage> messages,
            ChatOptions options,
            CancellationToken cancellationToken)
        {
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
    }
#pragma warning restore S101
}
