using NUnit.Framework;
using System;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class RimLLMEmbeddingClientTests
    {
        [Test]
        public void TestGenerateAsync_ReturnsEmbedding()
        {
            // 停用狀態會拋例外，僅驗證防濫用與例外對照
            var settings = new MockSettings { EmbeddingProvider = "Disabled" };
            var manager = new RimLLMManager(settings);
            var generator = manager.CreateEmbeddingGenerator("test.embed.mod");
            Assert.ThrowsAsync<RimLLMException>(async () =>
            {
                await generator.GenerateAsync(new[] { "hello" }).ConfigureAwait(false);
            });
        }

                [Test]
        public void TestEmbeddingClient_AntiAbuseThrottle()
        {
            var settings = new MockSettings
            {
                EmbeddingProvider = "Disabled",
                EnableAntiAbuse = true,
                MaxRequestsPerWindow = 1,
                ThrottlingWindowSeconds = 60
            };
            var manager = new RimLLMManager(settings);
            RimLLMProvider.Initialize(manager);
            var generator = RimLLMProvider.CreateEmbeddingGenerator("test.embed.antiabuse.mod");

            // 第一次呼叫：因為離線 Provider 拋出 Trigram 相關例外（說明已通過防濫用檢查）
            var ex1 = Assert.ThrowsAsync<RimLLMException>(async () =>
            {
                await generator.GenerateAsync(new[] { "hello" }).ConfigureAwait(false);
            });
            Assert.IsTrue(ex1.Message.Contains("Trigram"));

            // 第二次呼叫：超出 MaxRequestsPerWindow (1)，預期直接拋出 RateLimit 防濫用例外
            var ex2 = Assert.ThrowsAsync<RimLLMException>(async () =>
            {
                await generator.GenerateAsync(new[] { "hello 2" }).ConfigureAwait(false);
            });
            Assert.AreEqual(LLMError.RateLimit, ex2.Error);
        }
    }
}
