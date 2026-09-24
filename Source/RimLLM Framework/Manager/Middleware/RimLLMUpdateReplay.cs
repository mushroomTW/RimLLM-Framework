extern alias bclasync;
extern alias ste;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 把一組已經產生好的 <see cref="ChatResponseUpdate"/> 當成串流回放。
    /// </summary>
    /// <remarks>
    /// 中介層在「不往下呼叫就直接給出結果」時需要這個：預算靜默模擬要送出模擬文字，
    /// 沒有真正的下游串流可以轉發。
    /// 手寫列舉器而非 async iterator，是因為本組件以 extern alias 隔離
    /// IAsyncEnumerable 與 ValueTask，編譯器的 async iterator 改寫無法套用。
    /// </remarks>
    internal sealed class RimLLMUpdateReplay : bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
    {
        private readonly IList<ChatResponseUpdate> _updates;

        public RimLLMUpdateReplay(IList<ChatResponseUpdate> updates)
        {
            _updates = updates ?? new List<ChatResponseUpdate>();
        }

        /// <summary>把單一回應攤平成可回放的 update 序列。</summary>
        public static RimLLMUpdateReplay FromResponse(ChatResponse response)
        {
            return new RimLLMUpdateReplay(response?.ToChatResponseUpdates());
        }

        public bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            return new Enumerator(_updates, cancellationToken);
        }

        private sealed class Enumerator : bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
        {
            private readonly IList<ChatResponseUpdate> _updates;
            private readonly CancellationToken _cancellationToken;
            private int _index = -1;

            public Enumerator(IList<ChatResponseUpdate> updates, CancellationToken cancellationToken)
            {
                _updates = updates;
                _cancellationToken = cancellationToken;
            }

            public ChatResponseUpdate Current =>
                _index >= 0 && _index < _updates.Count ? _updates[_index] : null;

            public ste::System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _index++;
                return new ste::System.Threading.Tasks.ValueTask<bool>(_index < _updates.Count);
            }

            public ste::System.Threading.Tasks.ValueTask DisposeAsync()
            {
                return default(ste::System.Threading.Tasks.ValueTask);
            }
        }
    }
#pragma warning restore S101
}
