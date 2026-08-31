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
        public void TestHealthLedger()
        {
            var ledger = new RimLLMHealthLedger();
            
            // Null / empty 安全性
            ledger.RecordSuccess(null);
            ledger.RecordFailure(null);
            Assert.IsFalse(ledger.IsInCooldown(null));
            Assert.AreEqual(0f, ledger.GetAverageLatency(null));

            // 1~2 次失敗（可重試）給予短暫冷卻以利備援切換，並累積失敗計數
            ledger.RecordFailure("p1", isRetryable: true);
            Assert.IsTrue(ledger.IsInCooldown("p1", out DateTime cdTime, out int failures));
            Assert.AreEqual(1, failures);
            Assert.IsTrue(cdTime > DateTime.UtcNow);

            ledger.RecordFailure("p1", isRetryable: true);
            Assert.IsTrue(ledger.IsInCooldown("p1", out cdTime, out failures));
            Assert.AreEqual(2, failures);

            // 非可重試失敗不進入冷卻或連續失敗計數
            var ledger2 = new RimLLMHealthLedger();
            ledger2.RecordFailure("p_nonretry", isRetryable: false);
            Assert.IsFalse(ledger2.IsInCooldown("p_nonretry"));

            // 第 3 次失敗觸發指數熔斷冷卻
            ledger.RecordFailure("p1", isRetryable: true);
            Assert.IsTrue(ledger.IsInCooldown("p1", out cdTime, out failures));
            Assert.AreEqual(3, failures);
            Assert.IsTrue(cdTime > DateTime.UtcNow);

            // 成功呼叫清除連續失敗與冷卻，並記錄延遲
            ledger.RecordSuccess("p1", 120);
            ledger.RecordSuccess("p1", 180);
            Assert.IsFalse(ledger.IsInCooldown("p1"));
            Assert.AreEqual(150f, ledger.GetAverageLatency("p1"));

            // 驗證超過 5 筆延遲的滾動平均
            for (int i = 0; i < 5; i++)
            {
                ledger.RecordSuccess("p_lat", 100);
            }
            ledger.RecordSuccess("p_lat", 200); // 應踢除第一筆 100
            Assert.AreEqual(120f, ledger.GetAverageLatency("p_lat"));

            // AreAllInCooldown 測試
            var chain = new List<string> { "p1", "p2" };
            Assert.IsFalse(ledger.AreAllInCooldown(chain, id => id)); // p1, p2 不在冷卻中

            ledger.RecordFailure("p1", true);
            ledger.RecordFailure("p1", true);
            ledger.RecordFailure("p1", true);
            Assert.IsFalse(ledger.AreAllInCooldown(chain, id => id)); // 只有 p1 冷卻

            ledger.RecordFailure("p2", true);
            ledger.RecordFailure("p2", true);
            ledger.RecordFailure("p2", true);
            Assert.IsTrue(ledger.AreAllInCooldown(chain, id => id)); // p1, p2 皆冷卻

            Assert.IsFalse(ledger.AreAllInCooldown((List<string>)null, id => id));

            // Clear 測試
            ledger.Clear();
            Assert.IsFalse(ledger.IsInCooldown("p1"));
            Assert.IsFalse(ledger.IsInCooldown("p2"));
            Assert.AreEqual(0f, ledger.GetAverageLatency("p1"));
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
        [NonParallelizable]
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

        [Test]
        public async Task TestLLMProviderExtensions()
        {
            Assert.ThrowsAsync<ArgumentNullException>(async () => await LLMProviderExtensions.GenerateAsync(null, new List<ChatMessage>(), null, "m"));
            Assert.ThrowsAsync<ArgumentNullException>(async () => await LLMProviderExtensions.StreamAsync(null, new List<ChatMessage>(), null, "m", _ => { }));

            var mock = new MockTestProvider
            {
                ProviderId = "ExtMock",
                GenerateHandler = (msgs, opts, m) => Task.FromResult("generated result")
            };

            string res = await mock.GenerateAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }, null, "m1");
            Assert.AreEqual("generated result", res);

            var chunks = new List<string>();
            await mock.StreamAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }, null, "m1", c => chunks.Add(c));
            Assert.IsTrue(chunks.Count > 0);
        }

        [Test]
        public async Task TestGeminiChatClientAdapterCoverage()
        {
            var settings = new MockSettings();
            settings.ApiKeys["Gemini"] = "test-key";
            var provider = new TestGeminiProvider(settings);
            using (IChatClient client = provider.CreateChatClient("gemini-1.5-flash"))
            {
                Assert.IsNotNull(client.GetService(typeof(ChatClientMetadata)));
                Assert.IsNull(client.GetService(typeof(string)));

                var resp = await client.GetResponseAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") });
                Assert.IsNotNull(resp);

                var streamed = new List<string>();
                await foreach (var update in client.GetStreamingResponseAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") }))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        streamed.Add(update.Text);
                    }
                }
                Assert.IsTrue(streamed.Count > 0);
            }
        }

        [Test]
        public async Task TestFallbackPipelineFullCoverage()
        {
            var settings = new MockSettings();
            var ledger = new RimLLMHealthLedger();
            var tracker = new RimLLMUsageTracker(settings);

            var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
            var pipeline = new RimLLMFallbackPipeline(
                settings,
                ledger,
                tracker,
                id => providers.TryGetValue(id, out var p) ? p : null,
                id => settings.EnabledProviders.TryGetValue(id, out bool enabled) && enabled);

            // 1. 空鏈例外
            settings.FallbackChain = new List<string>();
            var req = new RimLLMRequest { ModId = "test.pipe" };
            Assert.ThrowsAsync<RimLLMException>(async () => await pipeline.ExecuteWithFallbackAsync(req, (p, m) => Task.FromResult(new RimLLMGenerationResult { Text = "ok" }), LLMError.ProviderOffline, "exhausted"));

            // 2. 無符合資格 Provider 例外
            settings.FallbackChain = new List<string> { "NonExistent:m1" };
            Assert.ThrowsAsync<RimLLMException>(async () => await pipeline.ExecuteWithFallbackAsync(req, (p, m) => Task.FromResult(new RimLLMGenerationResult { Text = "ok" }), LLMError.ProviderOffline, "exhausted"));

            // 3. MinLatency 路由策略 (Strategy = 1)
            var p1 = new MockTestProvider { ProviderId = "P1" };
            var p2 = new MockTestProvider { ProviderId = "P2" };
            providers["P1"] = p1;
            providers["P2"] = p2;
            settings.EnabledProviders["P1"] = true;
            settings.EnabledProviders["P2"] = true;
            settings.ApiKeys["P1"] = "k1";
            settings.ApiKeys["P2"] = "k2";
            settings.FallbackChain = new List<string> { "P1:m1", "P2:m2" };
            settings.RoutingStrategy = 1; // MinLatency

            // 設定 P1 延遲 300ms, P2 延遲 50ms
            ledger.RecordSuccess("P1", 300);
            ledger.RecordSuccess("P2", 50);

            string firstAttempted = null;
            await pipeline.ExecuteWithFallbackAsync(req, (p, m) =>
            {
                if (firstAttempted == null) firstAttempted = p.ProviderId;
                return Task.FromResult(new RimLLMGenerationResult { Text = "latency-ok" });
            }, LLMError.Unknown, "err");

            Assert.AreEqual("P2", firstAttempted); // P2 延遲較低應先被調用

            // 4. PreferredModelId 優先插入
            req.PreferredModelId = "P1:preferred-m";
            settings.RoutingStrategy = 0; // Priority
            firstAttempted = null;
            await pipeline.ExecuteWithFallbackAsync(req, (p, m) =>
            {
                if (firstAttempted == null) firstAttempted = $"{p.ProviderId}:{m}";
                return Task.FromResult(new RimLLMGenerationResult { Text = "pref-ok" });
            }, LLMError.Unknown, "err");
            Assert.AreEqual("P1:preferred-m", firstAttempted);

            // 5. MinFallbackLevel 分級過濾與 API 價格分級
            Assert.AreEqual(3, tracker.GetModelLevel("openai", "gpt-4o")); // Completion $10.00 >= $3.00 -> High (3)
            Assert.AreEqual(2, tracker.GetModelLevel("openai", "gpt-4o-mini")); // Completion $0.60 >= $0.50 -> Medium (2)
            Assert.AreEqual(1, tracker.GetModelLevel("gemini", "gemini-2.0-flash-lite")); // Completion $0.30 < $0.50 -> Low (1)
            Assert.AreEqual(1, tracker.GetModelLevel("deepseek", "deepseek-chat")); // Completion $0.28 < $0.50 -> Low (1)
            Assert.AreEqual(1, tracker.GetModelLevel("openai-compatible", "local-llama")); // 本地免費 -> Low (1)

            req.PreferredModelId = null;
            req.MinFallbackLevel = "high"; // 等級 3
            settings.ModelLevelOverrides["P2:pro-model"] = 3;
            settings.FallbackChain = new List<string> { "P1:mini-model", "P2:pro-model" }; // P1 預設為 tier 2, P2 覆寫為 tier 3
            firstAttempted = null;
            await pipeline.ExecuteWithFallbackAsync(req, (p, m) =>
            {
                if (firstAttempted == null) firstAttempted = p.ProviderId;
                return Task.FromResult(new RimLLMGenerationResult { Text = "tier-ok" });
            }, LLMError.Unknown, "err");
            Assert.AreEqual("P2", firstAttempted); // P1 (tier 2) 被跳過，只執行 P2 (tier 3)

            // 6. RoundRobin 路由策略 (Strategy = 2)
            settings.RoutingStrategy = 2;
            req.MinFallbackLevel = "low";
            var rrResult = await pipeline.ExecuteWithFallbackAsync(req, (p, m) => Task.FromResult(new RimLLMGenerationResult { Text = "rr-ok" }), LLMError.Unknown, "err");
            Assert.AreEqual("rr-ok", rrResult.Text);

            // 7. 非可重試例外直接跳往備援
            settings.RoutingStrategy = 0;
            settings.FallbackChain = new List<string> { "P1:m1", "P2:m2" };
            settings.MaxRetries = 2;
            int p1Attempts = 0;
            int p2Attempts = 0;
            await pipeline.ExecuteWithFallbackAsync(req, (p, m) =>
            {
                if (p.ProviderId == "P1")
                {
                    p1Attempts++;
                    throw new ArgumentException("Invalid arguments - non-retryable");
                }
                p2Attempts++;
                return Task.FromResult(new RimLLMGenerationResult { Text = "fallback-after-non-retry" });
            }, LLMError.Unknown, "err");
            Assert.AreEqual(1, p1Attempts); // 非可重試不重試，直接中斷給下一個
            Assert.AreEqual(1, p2Attempts);

            // 8. 管道輔助方法驗證
            pipeline.RecordLatency("P1", 99);
            Assert.IsTrue(pipeline.GetAverageLatency("P1") > 0);
            pipeline.ClearCooldowns();
            Assert.IsFalse(pipeline.IsInCooldown("P1"));
        }

        [Test]
        public async Task TestChatExecutionPipelineCoverage()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);
            var pipeline = manager.ChatPipeline;
            Assert.IsNotNull(pipeline);

            // 1. null request throws ArgumentNullException
            Assert.ThrowsAsync<ArgumentNullException>(async () => await pipeline.GenerateAsync(null));
            Assert.ThrowsAsync<ArgumentNullException>(async () => await pipeline.StreamAsync(null, _ => { }));

            // 2. Silent mocking when daily budget is exceeded (BudgetPolicy = 1)
            settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");
            settings.DailyBudgetLimit = 1.0f;
            settings.DailyAccumulatedCost = 2.0f;
            settings.BudgetPolicy = 1; // SilentMocking

            var mockReq = new RimLLMRequest { ModId = "test.mod" };
            var mockRes = await pipeline.GenerateAsync(mockReq);
            Assert.IsNotNull(mockRes?.Text);

            string streamedText = "";
            var streamRes = await pipeline.StreamAsync(mockReq, chunk => streamedText += chunk);
            Assert.AreEqual(mockRes.Text, streamRes.Text);
            Assert.AreEqual(mockRes.Text, streamedText);

            // 3. ResponseType with SilentMocking returns "{}"
            settings.DailyAccumulatedCost = 2.0f;
            var objReq = new RimLLMRequest { ModId = "test.mod", ResponseType = typeof(NullableTestDataStructure) };
            var objRes = await pipeline.GenerateAsync(objReq);
            Assert.AreEqual("{}", objRes.Text);

            // 4. JSON Repair disabled throws RimLLMException
            settings.EnableJsonRepair = false;
            Assert.Throws<RimLLMException>(() => pipeline.DeserializeStructured<NullableTestDataStructure>("invalid json", objReq));

            // 5. Static JSON repair fallback
            settings.EnableJsonRepair = true;
            string markdownJson = "```json\n{\"Name\":\"repaired-str\",\"OptionalCount\":42}\n```";
            var parsed = pipeline.DeserializeStructured<NullableTestDataStructure>(markdownJson, objReq);
            Assert.AreEqual("repaired-str", parsed.Name);
            Assert.AreEqual(42, parsed.OptionalCount);

            // 6. Anti-abuse rate limiting trigger & cooldown
            settings.EnableAntiAbuse = true;
            settings.MaxRequestsPerWindow = 3;
            settings.ThrottlingWindowSeconds = 60;
            settings.CoolDownDurationSeconds = 60;
            pipeline.CheckAntiAbuse("test-abuse-mod");
            pipeline.CheckAntiAbuse("test-abuse-mod");
            pipeline.CheckAntiAbuse("test-abuse-mod");
            // 第 4 次觸發限流
            Assert.Throws<RimLLMException>(() => pipeline.CheckAntiAbuse("test-abuse-mod"));
            // 冷卻中再次呼叫也拋出限流
            Assert.Throws<RimLLMException>(() => pipeline.CheckAntiAbuse("test-abuse-mod"));
            pipeline.ClearCooldowns();

            // 7. HardBlock 預算政策 (BudgetPolicy = 0)
            settings.BudgetPolicy = 0;
            settings.DailyBudgetLimit = 1.0f;
            settings.DailyAccumulatedCost = 2.0f;
            Assert.ThrowsAsync<RimLLMException>(async () => await pipeline.GenerateAsync(mockReq));

            // 8. 驗證空物件與結構化必填驗證
            Assert.Throws<InvalidOperationException>(() => RimLLMChatExecutionPipeline.DeserializeAndValidate<NullableTestDataStructure>("null"));
        }

        [Test]
        public async Task TestNativeSchemaRejectionAndDoubleRepair()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);
            var pipeline = manager.ChatPipeline;

            // 1. 模擬原生 Schema 被 400 拒絕時自動降級重試
            int callCount = 0;
            var rejectingProvider = new MockTestProvider
            {
                ProviderId = "SchemaRejectMock",
                GenerateHandler = (msgs, opts, m) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        throw new RimLLMException(LLMError.InvalidResponse, "400 Unrecognized parameter: response_format / schema is not supported");
                    }
                    return Task.FromResult("{\"Name\":\"fallback-success\"}");
                }
            };
            rejectingProvider.Capabilities.SupportsNativeStructuredOutput = true;
            settings.EnableNativeSchema = true;
            settings.EnabledProviders["SchemaRejectMock"] = true;
            settings.ApiKeys["SchemaRejectMock"] = "test-key";
            settings.FallbackChain = new List<string> { "SchemaRejectMock:m1" };
            manager.RegisterProvider(rejectingProvider);

            var req = new RimLLMRequest
            {
                ModId = "test.mod",
                ResponseType = typeof(NullableTestDataStructure)
            };

            var res = await pipeline.GenerateAsync(req);
            Assert.IsNotNull(res);
            Assert.IsTrue(res.Text.Contains("fallback-success"));

            // 2. 結構化修復失敗時拋出 RimLLMException
            Assert.Throws<RimLLMException>(() => pipeline.DeserializeStructured<NullableTestDataStructure>("totally broken no json anywhere", req));

            // 4. 結構化欄位驗證 (Field 必填為 null 拋出 InvalidOperationException)
            Assert.Throws<InvalidOperationException>(() => RimLLMChatExecutionPipeline.DeserializeAndValidate<TestStructureWithRequiredField>("{\"RequiredField\":null}"));
            Assert.Throws<InvalidOperationException>(() => RimLLMChatExecutionPipeline.DeserializeAndValidate<List<TestStructureWithRequiredField>>("[{\"RequiredField\":null}]"));

            // 5. 串流重試通知 (StreamAttemptSink 重設時派發 OnStreamRestart)
            bool restarted = false;
            var streamRestartReq = new RimLLMRequest
            {
                ModId = "test.mod",
                OnStreamRestart = () => restarted = true
            };
            int streamAttempts = 0;
            var streamFailProvider = new MockTestProvider
            {
                ProviderId = "StreamFailMock",
                StreamHandler = (msgs, opts, m, callback) =>
                {
                    streamAttempts++;
                    if (streamAttempts == 1)
                    {
                        callback("partial text before failure");
                        throw new RimLLMException(LLMError.NetworkError, "stream dropped");
                    }
                    callback("fresh text after retry");
                    return Task.CompletedTask;
                }
            };
            settings.EnabledProviders["StreamFailMock"] = true;
            settings.ApiKeys["StreamFailMock"] = "k";
            settings.FallbackChain = new List<string> { "StreamFailMock:m1", "StreamFailMock:m2" };
            manager.RegisterProvider(streamFailProvider);

            string finalStreamed = "";
            var streamGenResult = await pipeline.StreamAsync(streamRestartReq, chunk => finalStreamed += chunk);
            Assert.AreEqual("fresh text after retry", streamGenResult.Text);
            Assert.IsTrue(restarted);
        }

        [Test]
        public async Task TestBudgetAndTelemetryAccountingDeepening()
        {
            var settings = new MockSettings();
            var tracker = new RimLLMUsageTracker(settings);

            // 1. 定價精準匹配與前綴模糊匹配
            float exactCost = tracker.EstimateCost("openai", "gpt-4o", 1000000, 1000000, 0);
            Assert.AreEqual(12.50f, exactCost, 0.01f);

            float prefixCost = tracker.EstimateCost("openai", "gpt-4o-2024-11-20", 1000000, 1000000, 0);
            Assert.AreEqual(12.50f, prefixCost, 0.01f);

            float geminiPrefixCost = tracker.EstimateCost("gemini", "gemini-2.0-flash-001", 1000000, 1000000, 0);
            Assert.AreEqual(0.50f, geminiPrefixCost, 0.01f);

            // 2. 快取折扣計算
            float fullPromptCost = tracker.EstimateCost("gemini", "gemini-1.5-flash", 1000000, 0, 0);
            float cachedPromptCost = tracker.EstimateCost("gemini", "gemini-1.5-flash", 1000000, 0, 1000000);
            Assert.IsTrue(cachedPromptCost < fullPromptCost);
            Assert.AreEqual(fullPromptCost * 0.25f, cachedPromptCost, 0.001f);

            // 3. 未知模型回傳 0f
            float unknownCost = tracker.EstimateCost("unknown-prov", "unknown-model", 1000, 1000, 0);
            Assert.AreEqual(0f, unknownCost);

            // 4. 預算政策審查
            settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");
            settings.DailyBudgetLimit = 1.0f;
            settings.DailyAccumulatedCost = 2.0f;

            // Policy 0 = HardBlock
            settings.BudgetPolicy = 0;
            bool ok = await tracker.CheckBudgetLimitAsync(new RimLLMRequest { ModId = "t" });
            Assert.IsFalse(ok);

            // Policy 1 = SilentMocking
            settings.BudgetPolicy = 1;
            ok = await tracker.CheckBudgetLimitAsync(new RimLLMRequest { ModId = "t" });
            Assert.IsTrue(ok);
            Assert.IsTrue(tracker.IsBudgetMocked(new RimLLMRequest { ModId = "t" }, out string mockStr));
            Assert.IsNotNull(mockStr);

            // Policy 2 = FallbackToFree
            settings.BudgetPolicy = 2;
            ok = await tracker.CheckBudgetLimitAsync(new RimLLMRequest { ModId = "t" });
            Assert.IsTrue(ok);

            // 5. 跨天重置
            settings.DailyBudgetResetDate = "2000-01-01";
            tracker.CheckDailyReset();
            Assert.AreEqual(0f, settings.DailyAccumulatedCost);
            Assert.AreEqual(DateTime.Today.ToString("yyyy-MM-dd"), settings.DailyBudgetResetDate);
        }

#pragma warning disable CS0649
        private class TestStructureWithRequiredField
        {
            public string RequiredField;
        }
#pragma warning restore CS0649
    }
}
