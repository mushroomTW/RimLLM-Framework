using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 「什麼算同一個請求」的唯一定義。
    /// </summary>
    internal static class RimLLMResponseCacheKey
    {
        /// <summary>
        /// 由「所有會影響模型輸出的欄位」組出快取鍵，再以 SHA-256 壓成定長字串
        /// （直接拿正規化字串當鍵會讓長提示詞把記憶體吃光）。
        /// </summary>
        /// <remarks>
        /// ModId 與 Priority 刻意不納入：它們只影響防濫用節流與排隊順序，不影響模型輸出，
        /// 納入只會讓不同 Mod 的相同請求各自打一次 API。OnStreamRestart 與取消權杖同理。
        /// 反過來說，凡是會原樣送達 provider 的取樣參數都必須納入，否則兩個只有 Seed
        /// 不同的請求會共用同一筆快取。
        /// </remarks>
        internal static string Build(IEnumerable<ChatMessage> messages, ChatOptions options)
        {
            var canonical = new StringBuilder();

            AppendField(canonical, options?.ModelId);
            AppendField(canonical, RimLLMChatOptions.GetMinFallbackLevel(options));
            AppendField(canonical, RimLLMChatOptions.GetCachedContext(options));
            AppendField(canonical, RimLLMChatOptions.GetEnableContextCaching(options) ? "1" : "0");
            AppendField(canonical, options?.Temperature?.ToString("R", CultureInfo.InvariantCulture));
            AppendField(canonical, options?.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture));
            AppendField(canonical, options?.Reasoning?.Effort?.ToString());
            AppendField(canonical, RimLLMChatOptions.GetDisableReasoning(options) ? "1" : "0");
            AppendField(canonical, RimLLMChatOptions.GetResponseType(options)?.FullName);

            AppendField(canonical, options?.TopP?.ToString("R", CultureInfo.InvariantCulture));
            AppendField(canonical, options?.TopK?.ToString(CultureInfo.InvariantCulture));
            AppendField(canonical, options?.FrequencyPenalty?.ToString("R", CultureInfo.InvariantCulture));
            AppendField(canonical, options?.PresencePenalty?.ToString("R", CultureInfo.InvariantCulture));
            AppendField(canonical, options?.Seed?.ToString(CultureInfo.InvariantCulture));
            if (options?.StopSequences != null)
            {
                foreach (string stopSequence in options.StopSequences)
                {
                    AppendField(canonical, stopSequence);
                }
            }

            if (messages != null)
            {
                foreach (ChatMessage message in messages)
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
