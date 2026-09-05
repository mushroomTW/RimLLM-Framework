using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 相同請求的回應快取。命中時直接回傳先前的結果，完全不發出 API 呼叫，
    /// 因此也不會產生任何 Token 用量或費用（用量記錄發生在實際呼叫路徑上）。
    /// </summary>
    /// <remarks>
    /// 這是「逐字相同的輸入」的精確比對，不做語意相似度比對。
    /// 代價是相同輸入必然得到相同輸出，對需要每次都不一樣的敘事文本並不合適，
    /// 因此預設關閉，由玩家在設定中自行開啟。
    /// 快取只存在於記憶體中，不寫入存檔，遊戲重啟即清空。
    /// 過期與容量淘汰交給 <see cref="MemoryCache"/>，本類別只負責「什麼算同一個請求」。
    /// </remarks>
    internal sealed class RimLLMResponseCache : IDisposable
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

        public RimLLMResponseCache(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        private bool IsEnabled => _settings.EnableResponseCache && _settings.ResponseCacheTtlMinutes > 0f;

        /// <summary>
        /// 嘗試取出未過期的快取回應。
        /// </summary>
        public bool TryGet(RimLLMRequest request, out RimLLMGenerationResult cached)
        {
            cached = null;
            // 帶有 Tools 的請求通常具有副作用或即時狀態查詢需求，一律繞過快取
            if (!IsEnabled || request == null || (request.Tools != null && request.Tools.Count > 0)) return false;

            if (!_cache.TryGetValue(BuildKey(request), out RimLLMGenerationResult stored))
            {
                return false;
            }

            cached = stored;
            RimLLMLog.Message("[RimLLM] Response cache hit; the API call was skipped.");
            return true;
        }


        /// <summary>
        /// 存入一筆回應。空字串不存，以免把失敗的空回應也快取起來。
        /// </summary>
        /// <remarks>
        /// 存活時間在寫入當下就固定下來，之後玩家調整 TTL 設定只會影響新寫入的項目。
        /// </remarks>
        public void Store(RimLLMRequest request, RimLLMGenerationResult result)
        {
            if (!IsEnabled || request == null || string.IsNullOrEmpty(result?.Text) ||
                (request.Tools != null && request.Tools.Count > 0)) return;

            _cache.Set(BuildKey(request), result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_settings.ResponseCacheTtlMinutes),
                Size = 1
            });
        }


        /// <summary>
        /// 釋放底層快取。實務上這個快取與遊戲行程同壽命，不會有人呼叫；
        /// 實作 <see cref="IDisposable"/> 是因為持有的 <see cref="MemoryCache"/> 本身可釋放，
        /// 不往上層傳遞則是刻意的——為一個行程等長的單例串接釋放鏈只是徒增樣板。
        /// </summary>
        public void Dispose()
        {
            _cache.Dispose();
        }

        /// <summary>
        /// 由「所有會影響模型輸出的欄位」組出快取鍵，再以 SHA-256 壓成定長字串
        /// （直接拿正規化字串當鍵會讓長提示詞把記憶體吃光）。
        /// </summary>
        /// <remarks>
        /// <c>ModId</c> 與 <c>Priority</c> 刻意不納入：它們只影響防濫用節流與排隊順序，
        /// 不影響模型輸出，納入只會讓不同 Mod 的相同請求各自打一次 API。
        /// <c>CancellationToken</c> 與 <c>OnStreamRestart</c> 同理。
        /// </remarks>
        internal static string BuildKey(RimLLMRequest request)
        {
            var canonical = new StringBuilder();
            AppendField(canonical, request.PreferredModelId);
            AppendField(canonical, request.MinFallbackLevel);
            AppendField(canonical, request.SystemPrompt);
            AppendField(canonical, request.CachedContext);
            AppendField(canonical, request.EnableContextCaching ? "1" : "0");
            AppendField(canonical, request.Temperature?.ToString("R", CultureInfo.InvariantCulture));
            AppendField(canonical, request.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture));
            AppendField(canonical, request.ReasoningEffort?.ToString());
            AppendField(canonical, request.DisableReasoning ? "1" : "0");
            AppendField(canonical, request.ResponseType?.FullName);

            // 呼叫端直接設定、框架不逐一轉譯但會原樣送達 provider 的取樣參數。
            // 這些欄位一旦真的送到 provider 就會改變輸出，不納入鍵的話，
            // 兩個只有 Seed（或 TopP、StopSequences…）不同的請求會共用同一筆快取。
            ChatOptions source = request.SourceOptions;
            AppendField(canonical, source?.TopP?.ToString("R", CultureInfo.InvariantCulture));
            AppendField(canonical, source?.TopK?.ToString(CultureInfo.InvariantCulture));
            AppendField(canonical, source?.FrequencyPenalty?.ToString("R", CultureInfo.InvariantCulture));
            AppendField(canonical, source?.PresencePenalty?.ToString("R", CultureInfo.InvariantCulture));
            AppendField(canonical, source?.Seed?.ToString(CultureInfo.InvariantCulture));
            if (source?.StopSequences != null)
            {
                foreach (string stopSequence in source.StopSequences)
                {
                    AppendField(canonical, stopSequence);
                }
            }

            if (request.Messages != null)
            {
                foreach (ChatMessage message in request.Messages)
                {
                    if (message == null) continue;
                    AppendField(canonical, message.Role.ToString());
                    AppendField(canonical, message.Text);
                }
            }

            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return hex.ToString();
            }
        }

        /// <summary>
        /// 以長度前綴附加一個欄位。
        /// 單純用分隔字元的話，只要該字元有機會出現在提示詞裡，兩個不同的請求就可能組出
        /// 相同的鍵而互相污染；長度前綴則不依賴任何「內容不會用到」的假設。
        /// </summary>
        private static void AppendField(StringBuilder builder, string value)
        {
            builder.Append(value?.Length ?? -1).Append(':').Append(value);
        }
    }
#pragma warning restore S101
}
