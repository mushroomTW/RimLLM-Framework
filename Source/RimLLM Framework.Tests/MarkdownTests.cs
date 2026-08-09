using NUnit.Framework;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// Markdown 轉 Unity 舊版 rich text 的行為測試。
    /// 重點在於「只產生舊版 IMGUI 認得的六個標籤」，以及不要把模型輸出的內容誤判成語法。
    /// </summary>
    [TestFixture]
    public class MarkdownTests
    {
        [Test]
        public void BoldAndItalicBecomeRichTextTags()
        {
            Assert.AreEqual("<b>粗體</b>與<i>斜體</i>", RimLLMMarkdown.ToRichText("**粗體**與*斜體*"));
        }

        [Test]
        public void BoldIsNotBrokenIntoNestedItalic()
        {
            // 先處理 ** 再處理 *，否則 **text** 會被拆成 <i>*text</i>*
            Assert.AreEqual("<b>abc</b>", RimLLMMarkdown.ToRichText("**abc**"));
        }

        [Test]
        public void HeadingBecomesSizedBoldLine()
        {
            Assert.AreEqual("<size=20><b>標題</b></size>", RimLLMMarkdown.ToRichText("# 標題"));
            Assert.AreEqual("<size=15><b>第三層</b></size>", RimLLMMarkdown.ToRichText("### 第三層"));
        }

        [Test]
        public void DeepHeadingFallsBackToBoldOnly()
        {
            Assert.AreEqual("<b>第五層</b>", RimLLMMarkdown.ToRichText("##### 第五層"));
        }

        [Test]
        public void UnorderedListBecomesBulletWithIndent()
        {
            string result = RimLLMMarkdown.ToRichText("- 甲\n- 乙");
            Assert.AreEqual("  • 甲\n  • 乙", result);
        }

        [Test]
        public void NestedListGetsDeeperIndent()
        {
            string result = RimLLMMarkdown.ToRichText("- 外層\n  - 內層");
            Assert.AreEqual("  • 外層\n    • 內層", result);
        }

        [Test]
        public void OrderedListKeepsItsNumber()
        {
            Assert.AreEqual("  1. 第一項", RimLLMMarkdown.ToRichText("1. 第一項"));
        }

        [Test]
        public void InlineCodeContentIsNotReprocessed()
        {
            // 程式碼內的星號不是語法，必須原樣保留。
            string result = RimLLMMarkdown.ToRichText("看 `a * b * c` 這段");
            StringAssert.Contains("a * b * c", result);
            Assert.IsFalse(result.Contains("<i>"), "行內程式碼裡的星號不該被當成斜體。");
        }

        [Test]
        public void FencedCodeBlockDropsFencesAndKeepsContent()
        {
            string result = RimLLMMarkdown.ToRichText("```csharp\nint x = 1;\n```");
            StringAssert.Contains("int x = 1;", result);
            Assert.IsFalse(result.Contains("```"), "圍籬本身不該顯示出來。");
            Assert.IsFalse(result.Contains("csharp"), "語言標記不該顯示出來。");
        }

        [Test]
        public void UnclosedFenceStillRendersAsCode()
        {
            // 串流途中圍籬尚未閉合是常態，不能因此讓後續內容消失。
            string result = RimLLMMarkdown.ToRichText("```\nint x = 1;");
            StringAssert.Contains("int x = 1;", result);
        }

        [Test]
        public void MarkdownInsideFenceIsNotConverted()
        {
            string result = RimLLMMarkdown.ToRichText("```\n# 這不是標題\n```");
            StringAssert.Contains("# 這不是標題", result);
            Assert.IsFalse(result.Contains("<size="), "程式碼區塊內不該套用標題樣式。");
        }

        [Test]
        public void LinkKeepsLabelAndDropsUrlMarkup()
        {
            string result = RimLLMMarkdown.ToRichText("[說明](https://example.com)");
            StringAssert.Contains("說明", result);
            Assert.IsFalse(result.Contains("https://example.com"), "舊版 rich text 不能點擊，網址只是噪音。");
        }

        [Test]
        public void SnakeCaseIsNotTreatedAsItalic()
        {
            // 刻意不支援底線斜體，就是為了避免這種誤判。
            string result = RimLLMMarkdown.ToRichText("欄位 some_field_name 保持原樣");
            StringAssert.Contains("some_field_name", result);
            Assert.IsFalse(result.Contains("<i>"));
        }

        [Test]
        public void BlockQuoteBecomesMutedLine()
        {
            string result = RimLLMMarkdown.ToRichText("> 引用");
            StringAssert.Contains("引用", result);
            StringAssert.StartsWith("<color=", result);
        }

        [Test]
        public void ExistingRichTextTagsPassThrough()
        {
            // 思考過程的灰色包裝是在 Markdown 之前就加上去的，不能被破壞。
            const string input = "<color=silver>思考中</color>\n一般內容";
            string result = RimLLMMarkdown.ToRichText(input);
            StringAssert.Contains("<color=silver>思考中</color>", result);
        }

        [Test]
        public void OnlyLegacySupportedTagsAreEmitted()
        {
            // 舊版 IMGUI 只認得 b/i/size/color/material/quad，其餘標籤會被原樣印出來。
            string result = RimLLMMarkdown.ToRichText(
                "# 標題\n- 項目 **粗** *斜* `碼`\n> 引用\n\n---\n\n[連結](https://example.com)");
            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(result, @"</?([a-zA-Z]+)"))
            {
                string tag = match.Groups[1].Value.ToLowerInvariant();
                Assert.IsTrue(
                    tag == "b" || tag == "i" || tag == "size" || tag == "color" || tag == "material" || tag == "quad",
                    "產生了舊版 rich text 不支援的標籤: " + tag);
            }
        }

        [Test]
        public void EmptyInputIsReturnedUnchanged()
        {
            Assert.AreEqual("", RimLLMMarkdown.ToRichText(""));
            Assert.IsNull(RimLLMMarkdown.ToRichText(null));
        }

        [Test]
        public void PlainTextIsUnchanged()
        {
            const string input = "這只是一段普通文字，沒有任何標記。";
            Assert.AreEqual(input, RimLLMMarkdown.ToRichText(input));
        }
    }
}
