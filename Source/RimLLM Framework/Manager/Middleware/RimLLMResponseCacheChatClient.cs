extern alias bclasync;
extern alias ste;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 相同請求的回應快取中介層。命中時完全不往下呼叫，因此不產生任何 Token 用量或費用。
    /// </summary>
    /// <remarks>
    /// 這是「逐字相同的輸入」的精確比對，不做語意相似度比對。
    /// 代價是相同輸入必然得到相同輸出，對需要每次都不一樣的敘事文本並不合適，
    /// 因此預設關閉，由玩家在設定中自行開啟。
    /// 快取只存在於記憶體中，不寫入存檔，遊戲重啟即清空。
    ///
    /// 這一層必須排在防濫用與預算檢查之前：命中時不會發出任何 API 呼叫，
    /// 而那兩者保護的都是「真的花錢的呼叫」，攔阻零成本的重播沒有意義。
    ///
    /// 沒有繼承 MEAI 的 <see cref="CachingChatClient"/>：它的 <c>GetCacheKey</c> 是抽象成員且
    /// 簽章帶 <c>ReadOnlySpan</c>，而該型別在 System.Memory 與 RimWorld 的 mscorlib 中重複定義，
    /// 覆寫時必然觸發 CS0433——與本專案已為 ValueTask、IAsyncEnumerable 動用 extern alias 的
    /// 是同一類衝突。快取的儲存後端與命中判斷本來就要全部自訂，繼承換不到多少東西。
    /// </remarks>
    internal sealed class RimLLMResponseCacheChatClient : DelegatingChatClient
    {
        /// <summary>
        /// 快取項目上限。每筆項目的 Size 都是 1，因此 SizeLimit 等同筆數上限。
        /// 這個數字不開放給玩家調整：調小省不了多少記憶體，調大也不會提高命中率——
        /// 會重複的請求本來就集中在少數幾種。
        /// </summary>
        private const int MaxEntries = 256;

        private readonly IRimLLMSettings _settings;
        private readonly MemoryCache _cache =
            new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxEntries });

        public RimLLMResponseCacheChatClient(IChatClient innerClient, IRimLLMSettings settings)
            : base(innerClient)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 帶有 Tools 的請求通常具有副作用或即時狀態查詢需求，一律繞過快取。
        /// 玩家關閉快取或把 TTL 設為 0 時同樣整層繞過。
        /// </summary>
        private bool IsCacheable(ChatOptions options)
        {
            return _settings.EnableResponseCache &&
                   _settings.ResponseCacheTtlMinutes > 0f &&
                   (options?.Tools == null || options.Tools.Count == 0);
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsCacheable(options))
            {
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }

            // 訊息可能是一次性的列舉序列，算鍵與往下傳必須用同一份具體化的副本。
            var materialized = new List<ChatMessage>(messages ?? new List<ChatMessage>());
            string key = RimLLMResponseCacheKey.Build(materialized, options);

            if (_cache.TryGetValue(key, out ChatResponse cached))
            {
                RimLLMLog.Message("[RimLLM] Response cache hit; the API call was skipped.");
                return cached;
            }

            ChatResponse response = await base
                .GetResponseAsync(materialized, options, cancellationToken)
                .ConfigureAwait(false);

            Store(key, response);
            return response;
        }

        public override bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsCacheable(options))
            {
                return base.GetStreamingResponseAsync(messages, options, cancellationToken);
            }

            var materialized = new List<ChatMessage>(messages ?? new List<ChatMessage>());
            string key = RimLLMResponseCacheKey.Build(materialized, options);

            if (_cache.TryGetValue(key, out ChatResponse cached))
            {
                RimLLMLog.Message("[RimLLM] Response cache hit; the API call was skipped.");
                // 快取沒有保留原始的分塊邊界，把整個回應攤成 update 重播即可。
                return RimLLMUpdateReplay.FromResponse(cached);
            }

            return new RecordingEnumerable(
                base.GetStreamingResponseAsync(materialized, options, cancellationToken),
                key,
                this);
        }

        /// <summary>
        /// 存活時間在寫入當下就固定下來，之後玩家調整 TTL 設定只會影響新寫入的項目。
        /// 空回應不存，以免把失敗的空結果也快取起來。
        /// </summary>
        private void Store(string key, ChatResponse response)
        {
            if (string.IsNullOrEmpty(response?.Text)) return;

            _cache.Set(key, response, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_settings.ResponseCacheTtlMinutes),
                Size = 1
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cache.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>沿路收集 update，串流正常結束後把彙整出的回應存進快取。</summary>
        private sealed class RecordingEnumerable : bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
        {
            private readonly bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> _inner;
            private readonly string _key;
            private readonly RimLLMResponseCacheChatClient _owner;

            public RecordingEnumerable(
                bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> inner,
                string key,
                RimLLMResponseCacheChatClient owner)
            {
                _inner = inner;
                _key = key;
                _owner = owner;
            }

            public bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
            {
                return new RecordingEnumerator(_inner.GetAsyncEnumerator(cancellationToken), _key, _owner);
            }
        }

        private sealed class RecordingEnumerator : bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
        {
            private readonly bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> _inner;
            private readonly List<ChatResponseUpdate> _collected = new List<ChatResponseUpdate>();
            private readonly string _key;
            private readonly RimLLMResponseCacheChatClient _owner;
            private bool _completed;

            public RecordingEnumerator(
                bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> inner,
                string key,
                RimLLMResponseCacheChatClient owner)
            {
                _inner = inner;
                _key = key;
                _owner = owner;
            }

            public ChatResponseUpdate Current => _inner.Current;

            public async ste::System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
            {
                bool moved = await _inner.MoveNextAsync().ConfigureAwait(false);
                if (moved)
                {
                    _collected.Add(_inner.Current);
                    return true;
                }

                // 只有完整列舉結束才寫入：中途拋出例外或提早停止列舉的內容不完整，
                // 存進去會讓後續的相同請求拿到被截斷的回應。
                if (!_completed)
                {
                    _completed = true;
                    _owner.Store(_key, _collected.ToChatResponse());
                }
                return false;
            }

            public ste::System.Threading.Tasks.ValueTask DisposeAsync()
            {
                return _inner.DisposeAsync();
            }
        }
    }
#pragma warning restore S101
}
