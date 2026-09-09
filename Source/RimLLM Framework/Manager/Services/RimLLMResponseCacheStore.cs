using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 回應快取的儲存後端。
    /// </summary>
    /// <remarks>
    /// 由 manager 持有單一實例，注入每個 per-mod 的快取中介層。
    /// 這個共用是必要的：CreateChatClient 每次呼叫都會組出一疊全新的中介層，
    /// 若快取跟著中介層走，同一個 Mod 只要重新取得一次 client 就再也命中不了，
    /// 而每個被丟棄的 client 還會留下一個沒人釋放的快取實例。
    /// 與 <see cref="RimLLMThrottleStore"/> 同樣的理由、同樣的做法。
    /// 內部使用輕量化 <see cref="ConcurrentDictionary{TKey, TValue}"/> 實作 TTL 與容量淘汰，
    /// 不再依賴外部快取套件，降低 RimWorld AppDomain 組件衝突風險。
    /// </remarks>
    internal sealed class RimLLMResponseCacheStore : IDisposable
    {
        /// <summary>
        /// 快取項目上限。這個數字不開放給玩家調整：調小省不了多少記憶體，調大也不會提高命中率——
        /// 會重複的請求本來就集中在少數幾種。
        /// </summary>
        private const int MaxEntries = 256;

        private sealed class CacheEntry
        {
            public ChatResponse Response { get; }
            public DateTime ExpirationUtc { get; }
            public long Sequence { get; }

            public CacheEntry(ChatResponse response, DateTime expirationUtc, long sequence)
            {
                Response = response;
                ExpirationUtc = expirationUtc;
                Sequence = sequence;
            }
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new ConcurrentDictionary<string, CacheEntry>();
        private long _sequence;

        public bool TryGet(string key, out ChatResponse response)
        {
            if (key != null && _cache.TryGetValue(key, out CacheEntry entry))
            {
                if (DateTime.UtcNow < entry.ExpirationUtc)
                {
                    response = entry.Response;
                    return true;
                }
                _cache.TryRemove(key, out _);
            }

            response = null;
            return false;
        }

        /// <summary>
        /// 存活時間在寫入當下就固定下來，之後玩家調整 TTL 設定只會影響新寫入的項目。
        /// </summary>
        public void Store(string key, ChatResponse response, float ttlMinutes)
        {
            if (key == null || response == null || ttlMinutes <= 0) return;

            if (_cache.Count >= MaxEntries)
            {
                Prune();
            }

            DateTime expiration = DateTime.UtcNow.AddMinutes(ttlMinutes);
            long seq = System.Threading.Interlocked.Increment(ref _sequence);
            _cache[key] = new CacheEntry(response, expiration, seq);
        }

        private void Prune()
        {
            DateTime now = DateTime.UtcNow;
            foreach (var kvp in _cache)
            {
                if (now >= kvp.Value.ExpirationUtc)
                {
                    _cache.TryRemove(kvp.Key, out _);
                }
            }

            if (_cache.Count >= MaxEntries)
            {
                var toRemove = _cache.OrderBy(kvp => kvp.Value.ExpirationUtc)
                    .ThenBy(kvp => kvp.Value.Sequence)
                    .Take(_cache.Count - MaxEntries + 1)
                    .ToList();
                foreach (var item in toRemove)
                {
                    _cache.TryRemove(item.Key, out _);
                }
            }
        }

        public void Dispose()
        {
            _cache.Clear();
        }
    }
#pragma warning restore S101
}
