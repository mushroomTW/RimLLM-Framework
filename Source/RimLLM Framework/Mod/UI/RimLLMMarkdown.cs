using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 把模型輸出的 Markdown 轉成 Unity 舊版 IMGUI 能顯示的 rich text。
    ///
    /// 為什麼不用現成套件：Unity 舊版 rich text（RimWorld 的 <c>GUIStyle.richText</c> 走的就是這條）
    /// 只認得 b、i、size、color、material、quad 六個標籤，沒有縮排、清單、表格、等寬字。
    /// 現有的 Unity Markdown 套件（FancyTextRendering、UMarkdown、UAddMd）產出的都是
    /// TextMeshPro 或 UI Toolkit 的標籤，那些標籤在這裡會被原樣印出來，反而更難讀。
    /// 因此只能自己把 Markdown 映射到那六個標籤，用縮排與符號模擬結構。
    ///
    /// 刻意不支援的語法：
    /// - 底線斜體（<c>_text_</c>）：與 snake_case 識別字衝突太嚴重，誤判成本高於收益。
    /// - 表格：舊版 rich text 沒有等寬字也無法對齊欄位，原樣保留比硬轉好。
    /// - 刪除線：舊版 rich text 沒有對應標籤，只去掉標記保留文字。
    /// </summary>
    public static class RimLLMMarkdown
    {
        /// <summary>行內程式碼與程式碼區塊的文字顏色。</summary>
        private const string CodeColor = "#ce9178";

        /// <summary>連結文字的顏色。</summary>
        private const string LinkColor = "#6cb6ff";

        /// <summary>引用區塊與水平分隔線的顏色。</summary>
        private const string MutedColor = "#9aa0a6";

        /// <summary>各級標題的字級（像素）。四級以下只加粗不放大。</summary>
        private static readonly int[] HeadingSizes = { 20, 17, 15 };

        // 佔位符使用控制字元，避免與模型輸出的內容碰撞。
        private const string PlaceholderOpen = "";
        private const string PlaceholderClose = "";

        private static readonly Regex FenceRegex = new Regex(@"^\s*(`{3,}|~{3,})");
        private static readonly Regex HorizontalRuleRegex = new Regex(@"^\s*([-*_])\s*(\1\s*){2,}$");
        private static readonly Regex HeadingRegex = new Regex(@"^\s*(#{1,6})\s+(.*)$");
        private static readonly Regex BlockQuoteRegex = new Regex(@"^\s*>\s?(.*)$");
        private static readonly Regex UnorderedItemRegex = new Regex(@"^(\s*)[-*+]\s+(.*)$");
        private static readonly Regex OrderedItemRegex = new Regex(@"^(\s*)(\d{1,3})[.)]\s+(.*)$");
        private static readonly Regex TableSeparatorRegex = new Regex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?\s*$");

        private static readonly Regex InlineCodeRegex = new Regex(@"`([^`\n]+)`");
        private static readonly Regex LinkRegex = new Regex(@"\[([^\]\n]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)");
        private static readonly Regex BoldRegex = new Regex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*", RegexOptions.Singleline);
        private static readonly Regex ItalicRegex = new Regex(@"(?<!\*)\*(?=\S)([^*\n]+?)(?<=\S)\*(?!\*)");
        private static readonly Regex StrikeRegex = new Regex(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Singleline);
        private static readonly Regex PlaceholderRegex = new Regex("(\\d+)");

        /// <summary>
        /// 轉換整段文字。已經存在的 rich text 標籤（例如思考過程的灰色包裝）會原樣通過。
        /// </summary>
        public static string ToRichText(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return markdown;

            // 必須用 char[] 多載：Split(char) 是 .NET Core 才有的簽章，
            // 在 RimWorld 的 Mono 執行環境會直接丟 MissingMethodException。
            string[] lines = markdown.Replace("\r\n", "\n").Split(new char[] { '\n' });
            var output = new StringBuilder(markdown.Length + 64);
            string fence = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Match fenceMatch = FenceRegex.Match(line);

                if (fence != null)
                {
                    // 區塊內：只有同種類的圍籬能收尾，其餘一律當程式碼。
                    if (fenceMatch.Success && fenceMatch.Groups[1].Value[0] == fence[0])
                    {
                        fence = null;
                    }
                    else
                    {
                        AppendLine(output, WrapCode("  " + line));
                    }
                    continue;
                }

                if (fenceMatch.Success)
                {
                    // 串流途中圍籬還沒閉合是常態，後續內容照樣以程式碼呈現。
                    fence = fenceMatch.Groups[1].Value;
                    continue;
                }

                AppendLine(output, ConvertBlockLine(line));
            }

            return output.ToString();
        }

        /// <summary>
        /// 轉換單一非程式碼行：先判斷區塊型語法，再對剩下的文字套用行內語法。
        /// </summary>
        private static string ConvertBlockLine(string line)
        {
            if (line.Trim().Length == 0) return line;

            if (HorizontalRuleRegex.IsMatch(line))
            {
                return "<color=" + MutedColor + ">" + new string('-', 48) + "</color>";
            }

            // 表格分隔列在沒有等寬字的環境只是雜訊，整列去掉；資料列原樣保留。
            if (TableSeparatorRegex.IsMatch(line)) return string.Empty;

            Match heading = HeadingRegex.Match(line);
            if (heading.Success)
            {
                int level = heading.Groups[1].Value.Length;
                // TrimEnd(char) 同樣是 .NET Core 才有的多載，在 Mono 上會找不到方法。
                string text = ConvertInline(heading.Groups[2].Value.TrimEnd(new char[] { '#' }).Trim());
                return level <= HeadingSizes.Length
                    ? $"<size={HeadingSizes[level - 1]}><b>{text}</b></size>"
                    : $"<b>{text}</b>";
            }

            Match quote = BlockQuoteRegex.Match(line);
            if (quote.Success)
            {
                return $"<color={MutedColor}>| {ConvertInline(quote.Groups[1].Value)}</color>";
            }

            Match ordered = OrderedItemRegex.Match(line);
            if (ordered.Success)
            {
                return $"{Indent(ordered.Groups[1].Value)}{ordered.Groups[2].Value}. {ConvertInline(ordered.Groups[3].Value)}";
            }

            Match unordered = UnorderedItemRegex.Match(line);
            if (unordered.Success)
            {
                return $"{Indent(unordered.Groups[1].Value)}• {ConvertInline(unordered.Groups[2].Value)}";
            }

            return ConvertInline(line);
        }

        /// <summary>
        /// 行內語法轉換。先把行內程式碼換成佔位符，避免它的內容被其他規則再處理一次。
        /// </summary>
        private static string ConvertInline(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var codeSpans = new List<string>();
            string result = InlineCodeRegex.Replace(text, m =>
            {
                codeSpans.Add(m.Groups[1].Value);
                return PlaceholderOpen + (codeSpans.Count - 1).ToString() + PlaceholderClose;
            });

            result = LinkRegex.Replace(result, m =>
            {
                string label = m.Groups[1].Value;
                string url = m.Groups[2].Value;
                return string.IsNullOrEmpty(label)
                    ? $"<color={LinkColor}>{url}</color>"
                    : $"<color={LinkColor}>{label}</color>";
            });
            result = BoldRegex.Replace(result, "<b>$1</b>");
            result = ItalicRegex.Replace(result, "<i>$1</i>");
            result = StrikeRegex.Replace(result, "$1");

            return PlaceholderRegex.Replace(result, m =>
            {
                int index = int.Parse(m.Groups[1].Value);
                return index >= 0 && index < codeSpans.Count ? WrapCode(codeSpans[index]) : m.Value;
            });
        }

        private static string WrapCode(string text)
        {
            return "<color=" + CodeColor + ">" + text + "</color>";
        }

        /// <summary>
        /// 巢狀清單的縮排：每兩個空白（或一個 tab）算一層，每層兩個空白。
        /// </summary>
        private static string Indent(string leading)
        {
            int width = 0;
            for (int i = 0; i < leading.Length; i++)
            {
                width += leading[i] == '\t' ? 2 : 1;
            }
            return new string(' ', 2 + (width / 2) * 2);
        }

        private static void AppendLine(StringBuilder builder, string line)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line);
        }
    }
}
