using NUnit.Framework;
using System.Collections.Generic;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// Embedding 模型清單的篩選與排序。
    ///
    /// 兩種資料來源的可信度不同，行為也刻意不同：
    /// Google 的 models.list 會宣告 supportedActions，可以精確篩選；
    /// OpenAI 相容端點的 /v1/models 沒有能力資訊，只能排序不能過濾 ——
    /// 本地伺服器的模型名由使用者自訂，過濾會把合法選項藏起來。
    /// </summary>
    [TestFixture]
    public class EmbeddingModelListTests
    {
        [Test]
        public void EmbedContentActionIsDetectedCaseInsensitively()
        {
            Assert.IsTrue(RimLLMEmbeddingService.DeclaresEmbedContent(new[] { "embedContent" }));
            Assert.IsTrue(RimLLMEmbeddingService.DeclaresEmbedContent(new[] { "generateContent", "EMBEDCONTENT" }));
        }

        [Test]
        public void ChatOnlyModelIsNotTreatedAsEmbedding()
        {
            Assert.IsFalse(RimLLMEmbeddingService.DeclaresEmbedContent(new[] { "generateContent", "countTokens" }));
        }

        [Test]
        public void MissingSupportedActionsIsNotTreatedAsEmbedding()
        {
            Assert.IsFalse(RimLLMEmbeddingService.DeclaresEmbedContent(null));
            Assert.IsFalse(RimLLMEmbeddingService.DeclaresEmbedContent(new string[0]));
        }

        [Test]
        public void EmbeddingLookingNamesAreRecognised()
        {
            Assert.IsTrue(RimLLMEmbeddingService.LooksLikeEmbeddingModel("text-embedding-004"));
            Assert.IsTrue(RimLLMEmbeddingService.LooksLikeEmbeddingModel("nomic-embed-text"));
            Assert.IsTrue(RimLLMEmbeddingService.LooksLikeEmbeddingModel("bge-m3"));
            Assert.IsTrue(RimLLMEmbeddingService.LooksLikeEmbeddingModel("mxbai-embed-large"));
        }

        [Test]
        public void ChatModelNamesAreNotRecognised()
        {
            Assert.IsFalse(RimLLMEmbeddingService.LooksLikeEmbeddingModel("llama3.1:8b"));
            Assert.IsFalse(RimLLMEmbeddingService.LooksLikeEmbeddingModel("qwen2.5-coder"));
        }

        [Test]
        public void CandidatesAreOrderedButNeverDropped()
        {
            var input = new List<string> { "llama3.1:8b", "nomic-embed-text", "qwen2.5-coder", "bge-m3" };

            List<string> ordered = RimLLMEmbeddingService.OrderEmbeddingCandidatesFirst(input);

            Assert.AreEqual(4, ordered.Count, "本地模型名由使用者自訂，過濾會把合法選項藏起來。");
            Assert.AreEqual("nomic-embed-text", ordered[0]);
            Assert.AreEqual("bge-m3", ordered[1]);
            Assert.AreEqual("llama3.1:8b", ordered[2], "同一組內的原始順序要保留。");
            Assert.AreEqual("qwen2.5-coder", ordered[3]);
        }

        [Test]
        public void EmptyEntriesAreSkipped()
        {
            var ordered = RimLLMEmbeddingService.OrderEmbeddingCandidatesFirst(new List<string> { null, "", "bge-m3" });

            Assert.AreEqual(1, ordered.Count);
            Assert.AreEqual("bge-m3", ordered[0]);
        }

        [Test]
        public void NullInputYieldsEmptyList()
        {
            Assert.AreEqual(0, RimLLMEmbeddingService.OrderEmbeddingCandidatesFirst(null).Count);
        }

        [Test]
        public void ModelListKeyIsNamespacedAwayFromChatProviders()
        {
            // 與對話供應商共用同一份持久化字典，鍵必須不會與 providerId 相撞。
            string key = RimLLMEmbeddingService.GetModelListKey("Google");

            Assert.AreEqual("Embedding:Google", key);
            Assert.AreNotEqual(ProviderIds.Gemini, key);
            Assert.AreNotEqual("Google", key);
        }
    }
}
