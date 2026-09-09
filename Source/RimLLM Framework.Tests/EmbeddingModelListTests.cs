using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Collections.Generic;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// Embedding 模型清單的排序。
    ///
    /// 各供應商皆走 OpenAI 相容的 /v1/models，該端點沒有能力資訊，
    /// 因此只能排序不能過濾 —— 本地伺服器的模型名由使用者自訂，
    /// 過濾會把合法選項藏起來。
    /// </summary>
    [TestFixture]
    public class EmbeddingModelListTests
    {
        [Test]
        public void EmbeddingLookingNamesAreRecognised()
        {
            ClassicAssert.IsTrue(RimLLMEmbeddingService.LooksLikeEmbeddingModel("text-embedding-004"));
            ClassicAssert.IsTrue(RimLLMEmbeddingService.LooksLikeEmbeddingModel("nomic-embed-text"));
            ClassicAssert.IsTrue(RimLLMEmbeddingService.LooksLikeEmbeddingModel("bge-m3"));
            ClassicAssert.IsTrue(RimLLMEmbeddingService.LooksLikeEmbeddingModel("mxbai-embed-large"));
        }

        [Test]
        public void ChatModelNamesAreNotRecognised()
        {
            ClassicAssert.IsFalse(RimLLMEmbeddingService.LooksLikeEmbeddingModel("llama3.1:8b"));
            ClassicAssert.IsFalse(RimLLMEmbeddingService.LooksLikeEmbeddingModel("qwen2.5-coder"));
        }

        [Test]
        public void CandidatesAreOrderedButNeverDropped()
        {
            var input = new List<string> { "llama3.1:8b", "nomic-embed-text", "qwen2.5-coder", "bge-m3" };

            List<string> ordered = RimLLMEmbeddingService.OrderEmbeddingCandidatesFirst(input);

            ClassicAssert.AreEqual(4, ordered.Count, "本地模型名由使用者自訂，過濾會把合法選項藏起來。");
            ClassicAssert.AreEqual("nomic-embed-text", ordered[0]);
            ClassicAssert.AreEqual("bge-m3", ordered[1]);
            ClassicAssert.AreEqual("llama3.1:8b", ordered[2], "同一組內的原始順序要保留。");
            ClassicAssert.AreEqual("qwen2.5-coder", ordered[3]);
        }

        [Test]
        public void EmptyEntriesAreSkipped()
        {
            var ordered = RimLLMEmbeddingService.OrderEmbeddingCandidatesFirst(new List<string> { null, "", "bge-m3" });

            ClassicAssert.AreEqual(1, ordered.Count);
            ClassicAssert.AreEqual("bge-m3", ordered[0]);
        }

        [Test]
        public void NullInputYieldsEmptyList()
        {
            ClassicAssert.AreEqual(0, RimLLMEmbeddingService.OrderEmbeddingCandidatesFirst(null).Count);
        }

        [Test]
        public void ModelListKeyIsNamespacedAwayFromChatProviders()
        {
            // 與對話供應商共用同一份持久化字典，鍵必須不會與 providerId 相撞。
            string key = RimLLMEmbeddingService.GetModelListKey("Google");

            ClassicAssert.AreEqual("Embedding:Google", key);
            ClassicAssert.AreNotEqual(ProviderIds.Gemini, key);
            ClassicAssert.AreNotEqual("Google", key);
        }
    }
}
