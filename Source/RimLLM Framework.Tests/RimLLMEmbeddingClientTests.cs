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
    }
}
