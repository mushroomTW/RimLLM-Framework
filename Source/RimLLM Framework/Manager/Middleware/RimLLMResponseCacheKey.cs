using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
        /// 執行緒專屬雜湊器：SHA256.Create 含密碼學提供者初始化，每請求建立一次昂貴；
        /// SHA256 實例非執行緒安全，故以 ThreadLocal 持有。輸出與每次新建完全一致。
        /// </summary>
        private static readonly ThreadLocal<SHA256> Hasher =
            new ThreadLocal<SHA256>(() => SHA256.Create());

        private static readonly char[] HexTable = "0123456789abcdef".ToCharArray();
        /// <summary>
        /// 由「所有會影響模型輸出的欄位」組出快取鍵，再以 SHA-256 壓成定長字串
        /// （直接拿正規化字串當鍵會讓長提示詞把記憶體吃光）。
        /// </summary>
        /// <remarks>
        /// ModId 與 Priority 刻意不納入：它們只影響防濫用節流與排隊順序，不影響模型輸出，
        /// 納入只會讓不同 Mod 的相同請求各自打一次 API。取消權杖同理。
        /// 反過來說，凡是會原樣送達 provider 的取樣參數與訊息內容都必須納入，否則兩個只有 Seed
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
                    AppendNonTextContents(canonical, message.Contents);
                }
            }

            // 與舊實作位元一致：UTF-8(SHA-256(正規化字串)) 的小寫十六進位。
            // 差異僅在效能：雜湊器執行緒複用、十六進位以查表直寫 char[]，
            // 省下每次的提供者初始化與每 byte 一次的 "x2" 格式化配置。
            byte[] hash = Hasher.Value.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
            char[] hexChars = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                hexChars[i * 2] = HexTable[hash[i] >> 4];
                hexChars[i * 2 + 1] = HexTable[hash[i] & 0xF];
            }
            return new string(hexChars);
        }

        /// <summary>
        /// 附加訊息中非文字的內容。ChatMessage.Text 只串接 TextContent，工具結果與二進位內容
        /// 完全不影響鍵值——兩段只有工具結果不同的對話會組出同一個鍵而互相污染。
        /// </summary>
        private static void AppendNonTextContents(StringBuilder builder, IList<AIContent> contents)
        {
            if (contents == null) return;

            foreach (AIContent content in contents)
            {
                if (content == null || content is TextContent) continue;

                AppendField(builder, content.GetType().FullName);
                switch (content)
                {
                    case FunctionCallContent call:
                        AppendField(builder, call.CallId);
                        AppendField(builder, call.Name);
                        if (call.Arguments != null)
                        {
                            foreach (KeyValuePair<string, object> argument in call.Arguments)
                            {
                                AppendField(builder, argument.Key);
                                AppendField(builder, argument.Value?.ToString());
                            }
                        }
                        break;
                    case FunctionResultContent result:
                        AppendField(builder, result.CallId);
                        AppendField(builder, result.Result?.ToString());
                        break;
                    case DataContent data:
                        AppendField(builder, data.Uri);
                        break;
                    default:
                        AppendField(builder, content.ToString());
                        break;
                }
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
