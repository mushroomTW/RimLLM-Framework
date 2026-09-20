using System;
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
            // MEAI 的 OpenAI client 會把 Instructions 當成 system message 送出，屬於會改變輸出的欄位。
            AppendField(canonical, options?.Instructions);
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
        /// 複雜值（工具參數、工具結果、自訂物件）以 <see cref="AppendValue"/> 遞迴序列化，
        /// 而不是 <c>ToString()</c>：後者對不同內容的 Dictionary 或自訂物件只會輸出相同的
        /// 型別名稱，讓不同請求命中同一筆快取。
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
                            // 參數字典列舉順序不穩定：排序鍵後再附加，同內容不同插入順序得到相同鍵值。
                            var sortedArguments = new List<KeyValuePair<string, object>>(call.Arguments);
                            sortedArguments.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                            foreach (KeyValuePair<string, object> argument in sortedArguments)
                            {
                                AppendField(builder, argument.Key);
                                AppendValue(builder, argument.Value);
                            }
                        }
                        break;
                    case FunctionResultContent result:
                        AppendField(builder, result.CallId);
                        AppendValue(builder, result.Result);
                        break;
                    case DataContent data:
                        AppendField(builder, data.Uri);
                        break;
                    default:
                        AppendValue(builder, content);
                        break;
                }
            }
        }

        /// <summary>
        /// 以穩定、遞迴、具型別標記的方式附加一個任意值。
        /// 目標是「內容相同則附加結果相同、內容不同則附加結果不同」：
        /// <list type="bullet">
        /// <item><c>null</c> 以標記表示，不會與字串 "null" 混淆；</item>
        /// <item>字串原樣附加（長度前綴）；</item>
        /// <item>可格式化的純量以 InvariantCulture 格式化；</item>
        /// <item>Dictionary 排序鍵後依序附加，列舉順序不影響鍵值；</item>
        /// <item>其他集合依序遞迴；</item>
        /// <item>其餘物件以 System.Text.Json 序列化，取得型別標記且內容相關的穩定表示。</item>
        /// </list>
        /// </summary>
        private static void AppendValue(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("N;");
                return;
            }
            if (value is string str)
            {
                builder.Append("S;");
                AppendField(builder, str);
                return;
            }
            // bool 必須在 IFormattable 之前：bool 實作 IFormattable，若順序顛倒此分支永遠到不了。
            if (value is bool flag)
            {
                builder.Append("B;");
                AppendField(builder, flag ? "1" : "0");
                return;
            }
            if (value is IFormattable formattable)
            {
                builder.Append("F;");
                AppendField(builder, formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            }
            if (value is System.Collections.IDictionary dict)
            {
                // 字典列舉順序不保證穩定：排序鍵後再附加，同內容不同順序得到相同鍵值。
                var pairs = new List<System.Collections.DictionaryEntry>();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    pairs.Add(entry);
                }
                pairs.Sort((a, b) => string.CompareOrdinal(
                    Convert.ToString(a.Key, CultureInfo.InvariantCulture),
                    Convert.ToString(b.Key, CultureInfo.InvariantCulture)));
                builder.Append("D;").Append(pairs.Count).Append(';');
                foreach (System.Collections.DictionaryEntry pair in pairs)
                {
                    AppendField(builder, Convert.ToString(pair.Key, CultureInfo.InvariantCulture));
                    AppendValue(builder, pair.Value);
                }
                return;
            }
            if (value is System.Collections.IEnumerable enumerable)
            {
                var items = new List<object>();
                foreach (object item in enumerable)
                {
                    items.Add(item);
                }
                builder.Append("E;").Append(items.Count).Append(';');
                foreach (object item in items)
                {
                    AppendValue(builder, item);
                }
                return;
            }

            // 其餘物件：以執行期型別做 JSON 序列化，得到與內容相關的穩定表示。
            // 序列化失敗（如循環引用）時退回型別名稱加 ToString()，仍比單獨型別名稱多一層區分。
            builder.Append("O;").Append(value.GetType().FullName).Append(';');
            try
            {
                AppendField(builder, RimLLMJson.Serialize(value));
            }
            catch
            {
                AppendField(builder, value.ToString());
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
