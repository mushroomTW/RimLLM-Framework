using NUnit.Framework;
using System;
using RimLLM_Framework.Manager;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class RimLLMEmbeddingClientTests
    {
        [Test]
        public void TestGenerateAsync_ReturnsEmbedding()
        {
            // 離線 provider 會拋例外，僅驗證防濫用與例外對照
            var settings = new MockSettings { EmbeddingProvider = "Offline_Trigram" };
            var manager = new RimLLMManager(settings);
            RimLLMProvider.RegisterClient("test.embed.mod");
            var generator = manager.CreateEmbeddingGenerator("test.embed.mod", typeof(RimLLMEmbeddingClientTests).Assembly);
            Assert.ThrowsAsync<RimLLMException>(async () =>
            {
                await generator.GenerateAsync(new[] { "hello" }).ConfigureAwait(false);
            });
        }

        [Test]
        public void TestCreateEmbeddingGenerator_UnregisteredModThrows()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);
            Assert.Throws<RimLLMException>(() =>
                manager.CreateEmbeddingGenerator("unregistered.mod", typeof(RimLLMEmbeddingClientTests).Assembly));
        }

        [Test]
        public void TestEmbeddingClient_AntiAbuseThrottle()
        {
            var settings = new MockSettings
            {
                EmbeddingProvider = "Offline_Trigram",
                EnableAntiAbuse = true,
                MaxRequestsPerWindow = 1,
                ThrottlingWindowSeconds = 60
            };
            var manager = new RimLLMManager(settings);
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient("test.embed.antiabuse.mod");
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
