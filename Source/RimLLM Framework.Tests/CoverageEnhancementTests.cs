using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class CoverageEnhancementTests
    {
        [Test]
        public void TestCircuitBreakerClearAndAllEligibleLogic()
        {
            var breaker = new RimLLMCircuitBreaker();
            breaker.RecordFailure("p1");
            breaker.RecordFailure("p1");
            breaker.RecordFailure("p1");
            Assert.IsTrue(breaker.IsCooldown("p1", out _, out _));

            breaker.Clear();
            Assert.IsFalse(breaker.IsCooldown("p1", out _, out _));

            // AreAllEligibleProvidersInCooldown
            var chain = new List<string> { "p1:model1", "p2:model2" };
            breaker.RecordFailure("p1");
            breaker.RecordFailure("p1");
            breaker.RecordFailure("p1");

            bool allInCooldown = breaker.AreAllEligibleProvidersInCooldown(chain, id => true);
            Assert.IsFalse(allInCooldown); // p2 not in cooldown

            breaker.RecordFailure("p2");
            breaker.RecordFailure("p2");
            breaker.RecordFailure("p2");
            allInCooldown = breaker.AreAllEligibleProvidersInCooldown(chain, id => true);
            Assert.IsTrue(allInCooldown); // both p1 and p2 in cooldown

            // Null/ineligible provider
            bool ineligibleResult = breaker.AreAllEligibleProvidersInCooldown(
                new List<string> { "unknown", "p1:model1" },
                id => id == "p1");
            Assert.IsTrue(ineligibleResult);
        }

        [Test]
        public void TestSchemaBuilderCacheKeysAndEdgeCases()
        {
            // Null type
            Assert.Throws<ArgumentNullException>(() => RimLLMSchemaBuilder.Build(null, RimLLMSchemaProfile.OpenAI));
            Assert.IsFalse(RimLLMSchemaBuilder.ContainsOpenEndedMap(null));

            // Profile resolve
            Assert.AreEqual(RimLLMSchemaProfile.Gemini, RimLLMSchemaBuilder.ResolveProfile(ProviderIds.Gemini));
            Assert.AreEqual(RimLLMSchemaProfile.OpenAI, RimLLMSchemaBuilder.ResolveProfile(ProviderIds.OpenAI));
            Assert.AreEqual(RimLLMSchemaProfile.OpenAI, RimLLMSchemaBuilder.ResolveProfile("custom-provider"));

            // ForceLegacy toggle
            bool originalLegacy = RimLLMSchemaBuilder.ForceLegacy;
            try
            {
                RimLLMSchemaBuilder.ForceLegacy = true;
                Assert.IsTrue(RimLLMSchemaBuilder.ForceLegacy);

                var legacyResult = RimLLMSchemaBuilder.Build(typeof(SimpleTestDataStructure), RimLLMSchemaProfile.OpenAI);
                Assert.IsNotNull(legacyResult);
                Assert.IsTrue(legacyResult.UsedLegacyFallback);

                RimLLMSchemaBuilder.ForceLegacy = false;
                var normalResult = RimLLMSchemaBuilder.Build(typeof(SimpleTestDataStructure), RimLLMSchemaProfile.OpenAI);
                Assert.IsNotNull(normalResult);

                // Cache hit check
                var cachedResult = RimLLMSchemaBuilder.Build(typeof(SimpleTestDataStructure), RimLLMSchemaProfile.OpenAI);
                Assert.AreSame(normalResult, cachedResult);
            }
            finally
            {
                RimLLMSchemaBuilder.ForceLegacy = originalLegacy;
            }
        }

        private class SimpleTestDataStructure
        {
            public string Name { get; set; }
            public int Value { get; set; }
        }

        [Test]
        public void TestEmbeddingServiceSimilarityCalculations()
        {
            // CalculateCosineSimilarity
            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateCosineSimilarity(null, null));
            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateCosineSimilarity(new float[] { 1f }, null));
            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateCosineSimilarity(null, new float[] { 1f }));
            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateCosineSimilarity(new float[] { 1f }, new float[] { 1f, 2f }));
            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateCosineSimilarity(new float[] { 0f, 0f }, new float[] { 0f, 0f }));

            float[] v1 = new float[] { 1f, 2f, 3f };
            float[] v2 = new float[] { 1f, 2f, 3f };
            Assert.AreEqual(1f, RimLLMEmbeddingService.CalculateCosineSimilarity(v1, v2), 0.0001f);

            float[] v3 = new float[] { 1f, 0f };
            float[] v4 = new float[] { 0f, 1f };
            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateCosineSimilarity(v3, v4), 0.0001f);

            // CalculateTrigramSimilarity
            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateTrigramSimilarity(null, "test"));
            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateTrigramSimilarity("test", null));
            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateTrigramSimilarity("", "test"));
            Assert.AreEqual(1f, RimLLMEmbeddingService.CalculateTrigramSimilarity("identical", "identical"));
            Assert.AreEqual(1f, RimLLMEmbeddingService.CalculateTrigramSimilarity("a", "a"));
            Assert.AreEqual(1f, RimLLMEmbeddingService.CalculateTrigramSimilarity("abc", "abc"));

            float similarity = RimLLMEmbeddingService.CalculateTrigramSimilarity("colonist mood bad", "colonist mood is very bad");
            Assert.Greater(similarity, 0.4f);

            float disjoint = RimLLMEmbeddingService.CalculateTrigramSimilarity("abcdef", "uvwxyz");
            Assert.AreEqual(0f, disjoint, 0.0001f);
        }

        [Test]
        public void TestEmbeddingServiceParameterValidation()
        {
            var mockSettings = new MockSettings
            {
                EmbeddingProvider = RimLLMEmbeddingService.DisabledProviderId
            };
            var service = new RimLLMEmbeddingService(mockSettings);

            // Empty text validation
            Assert.ThrowsAsync<ArgumentException>(async () => await service.ComputeEmbeddingAsync(""));
            Assert.ThrowsAsync<ArgumentException>(async () => await service.ComputeEmbeddingAsync(null));

            // Disabled provider validation
            Assert.ThrowsAsync<RimLLMException>(async () => await service.ComputeEmbeddingAsync("test text"));
            Assert.ThrowsAsync<RimLLMException>(async () => await service.FetchAvailableModelsAsync());

            // Unknown provider validation
            mockSettings.EmbeddingProvider = "InvalidProviderName";
            Assert.ThrowsAsync<RimLLMException>(async () => await service.ComputeEmbeddingAsync("test text"));
            Assert.ThrowsAsync<RimLLMException>(async () => await service.FetchAvailableModelsAsync());

            // Batch embedding validation
            Assert.ThrowsAsync<ArgumentNullException>(async () => await service.ComputeEmbeddingsAsync(null));
            var emptyBatch = service.ComputeEmbeddingsAsync(new List<string>()).GetAwaiter().GetResult();
            Assert.AreEqual(0, emptyBatch.Count);
        }

        [Test]
        public void TestEncryptionUtilityEdgeCasesAndLegacyV1()
        {
            // Empty / null string
            Assert.AreEqual(string.Empty, EncryptionUtility.Encrypt(null));
            Assert.AreEqual(string.Empty, EncryptionUtility.Encrypt(""));
            Assert.AreEqual(string.Empty, EncryptionUtility.Decrypt(null));
            Assert.AreEqual(string.Empty, EncryptionUtility.Decrypt(""));

            // Custom Salt get/set
            string oldSalt = EncryptionUtility.CustomSalt;
            try
            {
                EncryptionUtility.CustomSalt = "UnitTestSalt2026";
                Assert.AreEqual("UnitTestSalt2026", EncryptionUtility.CustomSalt);
                EncryptionUtility.InitializeKeyAndIv();

                string plain = "secret-api-key-value-12345";
                string encrypted = EncryptionUtility.Encrypt(plain);
                Assert.IsTrue(encrypted.StartsWith("v2:"));
                string decrypted = EncryptionUtility.Decrypt(encrypted);
                Assert.AreEqual(plain, decrypted);

                // Corrupted ciphertext
                Assert.IsNull(EncryptionUtility.Decrypt("v2:corrupted-base64!"));
                Assert.IsNull(EncryptionUtility.Decrypt("invalid-base64-random-string"));

                // Test legacy V1 decryption fallback (AES-256 without MAC or v2 prefix)
                string legacyV1 = EncryptLegacyV1(plain);
                string decryptedV1 = EncryptionUtility.Decrypt(legacyV1);
                Assert.AreEqual(plain, decryptedV1);
            }
            finally
            {
                EncryptionUtility.CustomSalt = oldSalt;
                EncryptionUtility.InitializeKeyAndIv();
            }
        }

        private static string EncryptLegacyV1(string plainText)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (MD5 md5 = MD5.Create())
            {
                byte[] key = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes("RimLLMSecretKeySeed2026UnitTestSalt2026"));
                byte[] iv = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("RimLLMSecretIvSeed2026UnitTestSalt2026"));

                using (Aes aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                    using (var ms = new System.IO.MemoryStream())
                    {
                        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        using (var sw = new System.IO.StreamWriter(cs))
                        {
                            sw.Write(plainText);
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
        }

        [Test]
        public void TestRequestQueueCancellationAndErrorBranches()
        {
            var settings = new MockSettings { MaxConcurrentRequests = 2 };
            var queue = new RimLLMRequestQueue(settings);

            // 1. Pre-canceled token
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var req = new RimLLMRequest
                {
                    ModId = "test.cancel",
                    Priority = 5,
                    CancellationToken = cts.Token
                };

                Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    await queue.EnqueueRequestAsync(req, () => Task.FromResult(new RimLLMGenerationResult()));
                });
            }

            // 2. Action throws OperationCanceledException
            var reqCancel = new RimLLMRequest { ModId = "test.action.cancel", Priority = 1 };
            Assert.CatchAsync<OperationCanceledException>(async () =>
            {
                await queue.EnqueueRequestAsync(reqCancel, () =>
                {
                    throw new OperationCanceledException();
                });
            });

            // 3. Action throws general Exception
            var reqError = new RimLLMRequest { ModId = "test.action.error", Priority = 1 };
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await queue.EnqueueRequestAsync(reqError, () =>
                {
                    throw new InvalidOperationException("Simulated queue task failure");
                });
            });
        }

        [Test]
        public void TestDispatcherQueueBudgetAndErrorHandling()
        {
            RimLLMDispatcher.ResetQueueForTests();

            // TryEnqueueBounded with null
            Assert.IsFalse(RimLLMDispatcher.TryEnqueueBounded(null));

            // DrainWithBudget error recovery
            int executionCount = 0;
            RimLLMDispatcher.TryEnqueueBounded(() =>
            {
                executionCount++;
                throw new Exception("Simulated main thread dispatch exception");
            });

            RimLLMDispatcher.TryEnqueueBounded(() =>
            {
                executionCount++;
            });

            int processed = RimLLMDispatcher.DrainWithBudget(10, 100);
            Assert.AreEqual(2, processed);
            Assert.AreEqual(2, executionCount);

            RimLLMDispatcher.ResetQueueForTests();
            Assert.AreEqual(0, RimLLMDispatcher.QueuedCount);
        }

        [Test]
        public void TestChatClientAndEmbeddingClientFacades()
        {
            var manager = new RimLLMManager(new MockSettings());
            var chatClient = new RimLLMChatClient(manager, "test.mod");
            Assert.IsNotNull(chatClient.Metadata);
            Assert.AreSame(chatClient.Metadata, chatClient.GetService(typeof(ChatClientMetadata)));
            Assert.IsNull(chatClient.GetService(typeof(string)));
            chatClient.Dispose();

            var embeddingClient = new RimLLMEmbeddingClient(manager, "test.mod");
            Assert.IsNull(embeddingClient.GetService(typeof(string)));
            Assert.ThrowsAsync<ArgumentNullException>(async () => await embeddingClient.GenerateAsync(null));
            embeddingClient.Dispose();
        }

        [Test]
        public void TestChatClientExecutorNullMessagesHandling()
        {
            var req = RimLLMChatClientExecutor.CreateFromChatOptions(
                null,
                new ChatOptions { Temperature = 0.5f, MaxOutputTokens = 100 },
                "test-model",
                CancellationToken.None);

            Assert.IsNotNull(req.Messages);
            Assert.AreEqual(0, req.Messages.Count);

            var messagesWithSys = RimLLMChatClientExecutor.BuildMessages(new RimLLMRequest
            {
                Messages = null,
                SystemPrompt = "System instruction"
            });

            Assert.IsNotNull(messagesWithSys);
            Assert.AreEqual(1, messagesWithSys.Count);
            Assert.AreEqual(ChatRole.System, messagesWithSys[0].Role);
            Assert.AreEqual("System instruction", messagesWithSys[0].Text);

            var messagesEmpty = RimLLMChatClientExecutor.BuildMessages(new RimLLMRequest
            {
                Messages = null,
                SystemPrompt = null
            });

            Assert.IsNotNull(messagesEmpty);
            Assert.AreEqual(1, messagesEmpty.Count);
            Assert.AreEqual(ChatRole.User, messagesEmpty[0].Role);
        }
    }
}
