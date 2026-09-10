using System;
using System.IO;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace RimLLM_Framework.Mod
{
#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
    /// <summary>
    /// 基於 Markdig AST 解析引擎，把 Markdown 轉成 Unity 舊版 IMGUI 能顯示的 rich text。
    ///
    /// 舊版 IMGUI rich text 只認得 b、i、size、color、material、quad 六個標籤。
    /// 本實作透過 Markdig 構建 CommonMark 語法樹，精準將各語法節點映射至支援的標籤與縮排符號。
    /// </summary>
    public static class RimLLMMarkdown
    {
        private const string CodeColor = "#4ec9b0";
        private const string LinkColor = "#6cb6ff";
        private const string MutedColor = "#9aa0a6";

        private static readonly int[] HeadingSizes = { 20, 17, 15 };

        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

        private static readonly System.Text.RegularExpressions.Regex BoldPattern =
            new System.Text.RegularExpressions.Regex(@"\*\*\s*([^\*\n]+?)\s*\*\*", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// 粗體預轉換不得碰的區域：``` 或 ~~~ 圍籬（含串流中尚未閉合的尾巴）、行內碼、四空格／tab 縮排的程式碼行。
        /// 這些地方的 ** 是程式碼（Python 的 **kwargs、a ** b），不是 Markdown 標記。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex ProtectedRegion =
            new System.Text.RegularExpressions.Regex(
                @"(?:^|(?<=\n))(`{3,}|~{3,})[\s\S]*?(?:\n\1[ \t]*(?=\r?\n|\z)|\z)" +
                @"|`+[^`\n]*`+" +
                @"|(?:^|(?<=\n))(?:[ ]{4}|\t)[^\n]*",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex UnityTagPattern =
            new System.Text.RegularExpressions.Regex(@"<(/?(?:b|i|size|color|material|quad)(?:=[^>]*)?)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// 預先處理 Markdown 中的粗體標記。
        /// 繁簡中文環境下，全形括號或標點與粗體星號相鄰（例如 "**快速排序法（Quick Sort）**的"）時，
        /// 會觸發 CommonMark 規範中關於標點符號側翼（flanking delimiter）的判定缺陷而拒絕閉合。
        /// 本方法在排除 <see cref="ProtectedRegion"/>（圍籬、行內碼、縮排程式碼）後，
        /// 將一般文本中的 **內容** 預先轉換為 Unity 原生支援的 &lt;b&gt;內容&lt;/b&gt;。
        /// </summary>
        private static string PreprocessMarkdown(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return markdown;

            var sb = new StringBuilder(markdown.Length + 32);
            int last = 0;
            foreach (System.Text.RegularExpressions.Match region in ProtectedRegion.Matches(markdown))
            {
                sb.Append(BoldPattern.Replace(markdown.Substring(last, region.Index - last), "<b>$1</b>"));
                sb.Append(region.Value);
                last = region.Index + region.Length;
            }
            sb.Append(BoldPattern.Replace(markdown.Substring(last), "<b>$1</b>"));
            return sb.ToString();
        }

        /// <summary>
        /// 把程式碼裡剛好長得像 Unity rich text 標籤的片段（&lt;b&gt;、&lt;/i&gt;、&lt;color=red&gt;…）改成全形角括號。
        /// Unity 舊版 IMGUI 只認得 b/i/size/color/material/quad 六種標籤，其餘如 &lt;iostream&gt;、vector&lt;int&gt;、a &lt; b
        /// 都會原樣顯示（實機驗證過），不需要也不該動它們；但程式碼裡的 HTML 標籤若不跳脫，會被當成格式套用，
        /// 甚至提早關閉包住整段程式碼的 &lt;color&gt;。剪貼簿複製時會把全形角括號還原成 ASCII。
        /// </summary>
        public static string EscapeCode(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
            return UnityTagPattern.Replace(text, "＜$1＞");
        }

        /// <summary>
        /// 將 Markdown 文字轉換為 Unity IMGUI 支援的 rich text。
        /// </summary>
        public static string ToRichText(string markdown)
        {
            if (markdown == null) return null;
            if (markdown.Length == 0) return string.Empty;

            string preprocessed = PreprocessMarkdown(markdown);
            MarkdownDocument document = Markdown.Parse(preprocessed, Pipeline);
            var sb = new StringBuilder(preprocessed.Length + 64);
            var renderer = new UnityRichTextRenderer(sb);
            renderer.Render(document);
            return sb.ToString().TrimEnd(new char[] { '\r', '\n' });
        }

        private sealed class UnityRichTextRenderer
        {
            private readonly StringBuilder _sb;
            private int _listDepth;

            public UnityRichTextRenderer(StringBuilder sb)
            {
                _sb = sb;
            }

            public void Render(MarkdownDocument document)
            {
                bool first = true;
                foreach (Block block in document)
                {
                    // 頂層區塊（段落、清單、標題、程式碼）之間留一行空白，與一般 Markdown 呈現一致；
                    // 清單項目、引用行等區塊內部的換行由各自的 Render 方法處理。
                    if (!first)
                    {
                        _sb.Append("\n\n");
                    }
                    RenderBlock(block);
                    first = false;
                }
            }

            private void RenderBlock(Block block)
            {
                switch (block)
                {
                    case HeadingBlock heading:
                        RenderHeading(heading);
                        break;
                    case ParagraphBlock paragraph:
                        RenderParagraph(paragraph);
                        break;
                    case QuoteBlock quote:
                        RenderQuote(quote);
                        break;
                    case ListBlock list:
                        RenderList(list);
                        break;
                    case ListItemBlock listItem:
                        RenderListItem(listItem);
                        break;
                    case FencedCodeBlock fenced:
                        RenderFencedCode(fenced);
                        break;
                    case CodeBlock code:
                        RenderCodeBlock(code);
                        break;
                    case ThematicBreakBlock _:
                        _sb.Append("<color=").Append(MutedColor).Append(">------------------------------------------------</color>");
                        break;
                    case HtmlBlock html:
                        RenderHtmlBlock(html);
                        break;
                    default:
                        if (block is ContainerBlock container)
                        {
                            foreach (Block child in container)
                            {
                                RenderBlock(child);
                            }
                        }
                        break;
                }
            }

            private void RenderHeading(HeadingBlock heading)
            {
                int level = heading.Level;
                bool hasSize = level >= 1 && level <= HeadingSizes.Length;

                if (hasSize)
                {
                    _sb.Append("<size=").Append(HeadingSizes[level - 1]).Append("><b>");
                }
                else
                {
                    _sb.Append("<b>");
                }

                if (heading.Inline != null)
                {
                    RenderInlines(heading.Inline);
                }

                if (hasSize)
                {
                    _sb.Append("</b></size>");
                }
                else
                {
                    _sb.Append("</b>");
                }
            }

            private void RenderParagraph(ParagraphBlock paragraph)
            {
                if (paragraph.Inline != null)
                {
                    RenderInlines(paragraph.Inline);
                }
            }

            private void RenderQuote(QuoteBlock quote)
            {
                var subSb = new StringBuilder();
                var subRenderer = new UnityRichTextRenderer(subSb);
                bool firstChild = true;
                foreach (Block child in quote)
                {
                    if (!firstChild) subSb.Append('\n');
                    subRenderer.RenderBlock(child);
                    firstChild = false;
                }

                string content = subSb.ToString().TrimEnd(new char[] { '\r', '\n' });
                string[] lines = content.Replace("\r\n", "\n").Split(new char[] { '\n' });
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) _sb.Append('\n');
                    _sb.Append("<color=").Append(MutedColor).Append(">| ").Append(lines[i]).Append("</color>");
                }
            }

            private void RenderList(ListBlock list)
            {
                _listDepth++;
                bool isOrdered = list.IsOrdered;
                int itemIndex = 1;

                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) _sb.Append('\n');
                    if (list[i] is ListItemBlock listItem)
                    {
                        RenderListItemInternal(listItem, isOrdered, itemIndex++);
                    }
                }
                _listDepth--;
            }

            private void RenderListItem(ListItemBlock listItem)
            {
                RenderListItemInternal(listItem, false, 1);
            }

            private void RenderListItemInternal(ListItemBlock listItem, bool isOrdered, int index)
            {
                string indent = new string(' ', _listDepth * 2);
                _sb.Append(indent);
                if (isOrdered)
                {
                    _sb.Append(index).Append(". ");
                }
                else
                {
                    _sb.Append("• ");
                }

                bool firstChild = true;
                foreach (Block child in listItem)
                {
                    if (!firstChild) _sb.Append('\n');
                    if (child is ParagraphBlock p)
                    {
                        if (p.Inline != null) RenderInlines(p.Inline);
                    }
                    else
                    {
                        RenderBlock(child);
                    }
                    firstChild = false;
                }
            }

            private void RenderFencedCode(FencedCodeBlock fenced)
            {
                _sb.Append("<color=").Append(CodeColor).Append(">");
                for (int i = 0; i < fenced.Lines.Count; i++)
                {
                    if (i > 0) _sb.Append('\n');
                    _sb.Append("  ").Append(EscapeCode(fenced.Lines.Lines[i].Slice.ToString()));
                }
                _sb.Append("</color>");
            }

            private void RenderCodeBlock(CodeBlock code)
            {
                _sb.Append("<color=").Append(CodeColor).Append(">");
                for (int i = 0; i < code.Lines.Count; i++)
                {
                    if (i > 0) _sb.Append('\n');
                    _sb.Append("  ").Append(EscapeCode(code.Lines.Lines[i].Slice.ToString()));
                }
                _sb.Append("</color>");
            }

            private void RenderHtmlBlock(HtmlBlock html)
            {
                for (int i = 0; i < html.Lines.Count; i++)
                {
                    if (i > 0) _sb.Append('\n');
                    _sb.Append(html.Lines.Lines[i].Slice.ToString());
                }
            }

            private void RenderInlines(ContainerInline container)
            {
                foreach (Inline inline in container)
                {
                    RenderInline(inline);
                }
            }

            private void RenderInline(Inline inline)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        _sb.Append(literal.Content.ToString());
                        break;
                    case EmphasisInline emphasis:
                        RenderEmphasis(emphasis);
                        break;
                    case CodeInline code:
                        _sb.Append("<color=").Append(CodeColor).Append(">")
                           .Append(EscapeCode(code.Content))
                           .Append("</color>");
                        break;
                    case LinkInline link:
                        RenderLink(link);
                        break;
                    case LineBreakInline _:
                        _sb.Append('\n');
                        break;
                    case HtmlInline html:
                        _sb.Append(html.Tag);
                        break;
                    default:
                        if (inline is ContainerInline subContainer)
                        {
                            RenderInlines(subContainer);
                        }
                        break;
                }
            }

            private void RenderEmphasis(EmphasisInline emphasis)
            {
                if (emphasis.DelimiterChar == '_')
                {
                    // 為了防止 snake_case（如 some_field_name）誤判，若是底線則原樣輸出其子內容
                    foreach (Inline child in emphasis)
                    {
                        RenderInline(child);
                    }
                    return;
                }

                bool isBold = emphasis.DelimiterCount >= 2;
                if (isBold)
                {
                    _sb.Append("<b>");
                }
                else
                {
                    _sb.Append("<i>");
                }

                foreach (Inline child in emphasis)
                {
                    RenderInline(child);
                }

                if (isBold)
                {
                    _sb.Append("</b>");
                }
                else
                {
                    _sb.Append("</i>");
                }
            }

            private void RenderLink(LinkInline link)
            {
                _sb.Append("<color=").Append(LinkColor).Append(">");
                if (link.FirstChild != null)
                {
                    foreach (Inline child in link)
                    {
                        RenderInline(child);
                    }
                }
                else
                {
                    _sb.Append(link.Url);
                }
                _sb.Append("</color>");
            }
        }
    }
#pragma warning restore S101, S2342
}