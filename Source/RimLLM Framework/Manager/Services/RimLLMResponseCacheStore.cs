using System;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;

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
    /// 而每個被丟棄的 client 還會留下一個沒人釋放的 MemoryCache。
    /// 與 <see cref="RimLLMThrottleStore"/> 同樣的理由、同樣的做法。
    /// </remarks>
    internal sealed class RimLLMResponseCacheStore : IDisposable
    {
        /// <summary>
        /// 快取項目上限。每筆項目的 Size 都是 1，因此 SizeLimit 等同筆數上限。
        /// 這個數字不開放給玩家調整：調小省不了多少記憶體，調大也不會提高命中率——
        /// 會重複的請求本來就集中在少數幾種。
        /// </summary>
        private const int MaxEntries = 256;

        private readonly MemoryCache _cache =
            new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxEntries });

        public bool TryGet(string key, out ChatResponse response)
        {
            return _cache.TryGetValue(key, out response);
        }

        /// <summary>
        /// 存活時間在寫入當下就固定下來，之後玩家調整 TTL 設定只會影響新寫入的項目。
        /// </summary>
        public void Store(string key, ChatResponse response, float ttlMinutes)
        {
            _cache.Set(key, response, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(ttlMinutes),
                Size = 1
            });
        }

        public void Dispose()
        {
            _cache.Dispose();
        }
    }
#pragma warning restore S101
}
