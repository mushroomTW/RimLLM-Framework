using NUnit.Framework;
using System.Collections.Generic;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// 設定介面的純函式：金鑰遮罩、模型過濾、chip 版面計算。
    /// IMGUI 的繪製本身無法單元測試，因此把可判定的邏輯抽成純函式並在此釘住。
    /// </summary>
    [TestFixture]
    public class UIStyleTests
    {
        // ---------- 金鑰遮罩 ----------

        [Test]
        public void LongKeyKeepsHeadAndTailOnly()
        {
            string masked = RimLLMUIStyle.MaskApiKey("sk-or-v1-TESTKEY-TESTKEY-TESTKEY-TESTKEY-TESTKEY-TESTKEY-2e8a");

            Assert.AreEqual(13, masked.Length, "頭 8 + 省略號 1 + 尾 4。");
            StringAssert.StartsWith("sk-or-v1", masked);
            StringAssert.EndsWith("2e8a", masked);
            Assert.IsFalse(masked.Contains("TESTKEY"), "中段必須完全隱藏。");
        }

        [Test]
        public void ShortKeyIsFullyHidden()
        {
            // 短金鑰若也保留頭尾，等於露出大半內容。
            string masked = RimLLMUIStyle.MaskApiKey("abcd1234");

            Assert.AreEqual("••••••••", masked);
        }

        [Test]
        public void BoundaryLengthKeyIsFullyHidden()
        {
            // 剛好 12 字（8 + 4）時保留頭尾就等於全部露出，因此仍應全遮。
            string masked = RimLLMUIStyle.MaskApiKey("123456789012");

            Assert.AreEqual(12, masked.Length);
            Assert.AreEqual("••••••••••••", masked);
        }

        [Test]
        public void EmptyKeyStaysEmpty()
        {
            // 空金鑰若顯示成圓點，畫面上會看起來像已經設定過。
            Assert.AreEqual("", RimLLMUIStyle.MaskApiKey(""));
            Assert.AreEqual("", RimLLMUIStyle.MaskApiKey(null));
        }

        // ---------- 模型過濾 ----------

        [Test]
        public void EmptyFilterReturnsEverything()
        {
            var models = new List<string> { "gpt-4o", "o5-preview" };

            Assert.AreEqual(2, RimLLMUIStyle.FilterModels(models, "").Count);
            Assert.AreEqual(2, RimLLMUIStyle.FilterModels(models, null).Count);
            Assert.AreEqual(2, RimLLMUIStyle.FilterModels(models, "   ").Count);
        }

        [Test]
        public void FilterIsCaseInsensitiveAndMatchesSubstrings()
        {
            var models = new List<string> { "google/gemini-3.5-flash-lite", "anthropic/claude-opus-5", "qwen/qwen3.8-max" };

            var result = RimLLMUIStyle.FilterModels(models, "GEMINI");

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("google/gemini-3.5-flash-lite", result[0]);
        }

        [Test]
        public void NoMatchReturnsEmptyList()
        {
            var models = new List<string> { "gpt-4o" };

            Assert.AreEqual(0, RimLLMUIStyle.FilterModels(models, "llama").Count);
        }

        [Test]
        public void NullAndEmptyEntriesAreSkipped()
        {
            var models = new List<string> { null, "", "bge-m3" };

            var result = RimLLMUIStyle.FilterModels(models, "");

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("bge-m3", result[0]);
        }

        [Test]
        public void NullSourceIsSafe()
        {
            Assert.AreEqual(0, RimLLMUIStyle.FilterModels(null, "anything").Count);
        }

        // ---------- chip 版面 ----------

        [Test]
        public void ChipsFillTheAvailableWidth()
        {
            // 先前使用固定寬度，右緣總是留下一條用不到的空隙。
            RimLLMUIStyle.ComputeChipLayout(700f, 8f, 220f, out int cols, out float chipWidth);

            float consumed = cols * chipWidth + (cols + 1) * 8f;
            Assert.AreEqual(700f, consumed, 0.01f);
        }

        [Test]
        public void ColumnCountFollowsPreferredWidth()
        {
            RimLLMUIStyle.ComputeChipLayout(700f, 8f, 220f, out int cols, out float chipWidth);

            Assert.AreEqual(3, cols);
            Assert.GreaterOrEqual(chipWidth, 220f, "反推後的寬度不該小於偏好寬度，否則欄數就算多了。");
        }

        [Test]
        public void VeryNarrowPanelStillGetsOneColumn()
        {
            // 欄數為 0 會導致取餘數時除以零。
            RimLLMUIStyle.ComputeChipLayout(60f, 8f, 220f, out int cols, out float chipWidth);

            Assert.AreEqual(1, cols);
            Assert.Greater(chipWidth, 0f);
        }

        [Test]
        public void DegenerateWidthDoesNotProduceNegativeChip()
        {
            RimLLMUIStyle.ComputeChipLayout(4f, 8f, 220f, out int cols, out float chipWidth);

            Assert.AreEqual(1, cols);
            Assert.Greater(chipWidth, 0f);
        }
    }
}
