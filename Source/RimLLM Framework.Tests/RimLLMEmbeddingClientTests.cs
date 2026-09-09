using NUnit.Framework;
using NUnit.Framework.Legacy;
using System;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class RimLLMEmbeddingClientTests
    {
        /// <summary>
        /// MEAI 的慣例是 GetService 至少要能回報自身與 metadata。先前這裡一律回傳 null，
        /// 任何依 EmbeddingGeneratorMetadata 判斷來源模型的中介層都會拿到空手。
        /// </summary>
        [Test]
        public void TestGetService_ReportsMetadataAndSelf()
        {
            var settings = new MockSettings
            {
                EmbeddingProvider = "Google",
                EmbeddingModel = "text-embedding-004"
            };
            var manager = new RimLLMManager(settings);
            var generator = manager.CreateEmbeddingGenerator("test.embed.metadata.mod");

            var metadata = generator.GetService(typeof(EmbeddingGeneratorMetadata), null) as EmbeddingGeneratorMetadata;
            ClassicAssert.IsNotNull(metadata);
            ClassicAssert.AreEqual("Google", metadata.ProviderName);
            ClassicAssert.AreEqual("text-embedding-004", metadata.DefaultModelId);

            ClassicAssert.AreSame(generator, generator.GetService(generator.GetType(), null));
            // 具名查詢不屬於這一層，維持 null。
            ClassicAssert.IsNull(generator.GetService(typeof(EmbeddingGeneratorMetadata), "some-key"));
        }

        /// <summary>
        /// 沒有任何一筆向量帶回 token 數時，Usage 必須維持 null。
        /// 填一個全零的 UsageDetails 會讓呼叫端把「供應商沒回報」誤讀成「這批不耗配額」。
        /// </summary>
        [Test]
        public void TestGenerateAsync_LeavesUsageNullWhenProviderReportsNothing()
        {
            var settings = new MockSettings { EmbeddingProvider = "Disabled" };
            var manager = new RimLLMManager(settings);
            var generator = manager.CreateEmbeddingGenerator("test.embed.usage.mod");

            // 空輸入不會碰到供應商，因此即使 Provider 停用也能走完整條組裝路徑。
            GeneratedEmbeddings<Embedding<float>> generated =
                generator.GenerateAsync(new string[0]).GetAwaiter().GetResult();

            ClassicAssert.AreEqual(0, generated.Count);
            ClassicAssert.IsNull(generated.Usage);
        }

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

            // 第一次呼叫：因為未設定 Provider 拋出例外（說明已通過防濫用檢查）
            var ex1 = Assert.ThrowsAsync<RimLLMException>(async () =>
            {
                await generator.GenerateAsync(new[] { "hello" }).ConfigureAwait(false);
            });
            ClassicAssert.IsTrue(ex1.Message.Contains("Embedding 尚未設定供應商"));

            // 第二次呼叫：超出 MaxRequestsPerWindow (1)，預期直接拋出 RateLimit 防濫用例外
            var ex2 = Assert.ThrowsAsync<RimLLMException>(async () =>
            {
                await generator.GenerateAsync(new[] { "hello 2" }).ConfigureAwait(false);
            });
            ClassicAssert.AreEqual(LLMError.RateLimit, ex2.Error);
        }
    }
}
