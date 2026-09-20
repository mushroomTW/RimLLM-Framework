using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class CoverageEnhancementTests
    {
        private string _encryptionKeyDirectory;

        [SetUp]
        public void SetUpEncryptionKeyStore()
        {
            _encryptionKeyDirectory = Path.Combine(
                Path.GetTempPath(),
                "RimLLMCoverageEncryptionTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_encryptionKeyDirectory);
            EncryptionUtility.ResetSecureKeyForTests();
            EncryptionUtility.SecureKeyPathResolver = () =>
                Path.Combine(_encryptionKeyDirectory, "RimLLM_EncryptionKey.dat");
            EncryptionUtility.CustomSalt = null;
            EncryptionUtility.InitializeKeyAndIv();
        }

        [TearDown]
        public void TearDownEncryptionKeyStore()
        {
            EncryptionUtility.CustomSalt = null;
            EncryptionUtility.InitializeKeyAndIv();
            EncryptionUtility.ResetSecureKeyForTests();
            try
            {
                Directory.Delete(_encryptionKeyDirectory, true);
            }
            catch
            {
            }
        }

        [Test]
        public void TestHealthLedger()
        {
            var ledger = new RimLLMHealthLedger();
            
            // Null / empty 安全性
            ledger.RecordSuccess(null);
            ledger.RecordFailure(null);
            ClassicAssert.IsFalse(ledger.IsInCooldown(null));
            ClassicAssert.AreEqual(0f, ledger.GetAverageLatency(null));

            // 1~2 次失敗（可重試）給予短暫冷卻以利備援切換，並累積失敗計數
            ledger.RecordFailure("p1", isRetryable: true);
            ClassicAssert.IsTrue(ledger.IsInCooldown("p1", out DateTime cdTime, out int failures));
            ClassicAssert.AreEqual(1, failures);
            ClassicAssert.IsTrue(cdTime > DateTime.UtcNow);

            ledger.RecordFailure("p1", isRetryable: true);
            ClassicAssert.IsTrue(ledger.IsInCooldown("p1", out cdTime, out failures));
            ClassicAssert.AreEqual(2, failures);

            // 非可重試失敗不進入冷卻或連續失敗計數
            var ledger2 = new RimLLMHealthLedger();
            ledger2.RecordFailure("p_nonretry", isRetryable: false);
            ClassicAssert.IsFalse(ledger2.IsInCooldown("p_nonretry"));

            // 第 3 次失敗觸發指數熔斷冷卻
            ledger.RecordFailure("p1", isRetryable: true);
            ClassicAssert.IsTrue(ledger.IsInCooldown("p1", out cdTime, out failures));
            ClassicAssert.AreEqual(3, failures);
            ClassicAssert.IsTrue(cdTime > DateTime.UtcNow);

            // 成功呼叫清除連續失敗與冷卻，並記錄延遲
            ledger.RecordSuccess("p1", 120);
            ledger.RecordSuccess("p1", 180);
            ClassicAssert.IsFalse(ledger.IsInCooldown("p1"));
            ClassicAssert.AreEqual(150f, ledger.GetAverageLatency("p1"));

            // 驗證超過 5 筆延遲的滾動平均
            for (int i = 0; i < 5; i++)
            {
                ledger.RecordSuccess("p_lat", 100);
            }
            ledger.RecordSuccess("p_lat", 200); // 應踢除第一筆 100
            ClassicAssert.AreEqual(120f, ledger.GetAverageLatency("p_lat"));

            // Clear 測試
            ledger.Clear();
            ClassicAssert.IsFalse(ledger.IsInCooldown("p1"));
            ClassicAssert.IsFalse(ledger.IsInCooldown("p2"));
            ClassicAssert.AreEqual(0f, ledger.GetAverageLatency("p1"));
        }

        [Test]
        public void TestSchemaBuilderCacheKeysAndEdgeCases()
        {
            // Null type
            Assert.Throws<ArgumentNullException>(() => RimLLMSchemaBuilder.Build(null));
            ClassicAssert.IsFalse(RimLLMSchemaBuilder.ContainsOpenEndedMap(null));

            // ForceLegacy toggle
            bool originalLegacy = RimLLMSchemaBuilder.ForceLegacy;
            try
            {
                RimLLMSchemaBuilder.ForceLegacy = true;
                ClassicAssert.IsTrue(RimLLMSchemaBuilder.ForceLegacy);

                var legacyResult = RimLLMSchemaBuilder.Build(typeof(SimpleTestDataStructure));
                ClassicAssert.IsNotNull(legacyResult);
                ClassicAssert.IsTrue(legacyResult.UsedLegacyFallback);

                RimLLMSchemaBuilder.ForceLegacy = false;
                var normalResult = RimLLMSchemaBuilder.Build(typeof(SimpleTestDataStructure));
                ClassicAssert.IsNotNull(normalResult);

                // Cache hit check
                var cachedResult = RimLLMSchemaBuilder.Build(typeof(SimpleTestDataStructure));
                ClassicAssert.AreSame(normalResult, cachedResult);
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
            ClassicAssert.AreEqual(0, emptyBatch.Count);
        }

        [Test]
        public void TestEncryptionUtilityEdgeCasesAndLegacyV1()
        {
            // Empty / null string
            ClassicAssert.AreEqual(string.Empty, EncryptionUtility.Encrypt(null));
            ClassicAssert.AreEqual(string.Empty, EncryptionUtility.Encrypt(""));
            ClassicAssert.AreEqual(string.Empty, EncryptionUtility.Decrypt(null));
            ClassicAssert.AreEqual(string.Empty, EncryptionUtility.Decrypt(""));

            // Custom Salt get/set
            string oldSalt = EncryptionUtility.CustomSalt;
            try
            {
                EncryptionUtility.CustomSalt = "UnitTestSalt2026";
                ClassicAssert.AreEqual("UnitTestSalt2026", EncryptionUtility.CustomSalt);
                EncryptionUtility.InitializeKeyAndIv();

                string plain = "secret-api-key-value-12345";
                string encrypted = EncryptionUtility.Encrypt(plain);
                ClassicAssert.IsTrue(encrypted.StartsWith("v3:"));
                string decrypted = EncryptionUtility.Decrypt(encrypted);
                ClassicAssert.AreEqual(plain, decrypted);

                // Corrupted ciphertext
                ClassicAssert.IsNull(EncryptionUtility.Decrypt("v2:corrupted-base64!"));
                ClassicAssert.IsNull(EncryptionUtility.Decrypt("invalid-base64-random-string"));

                // Test legacy V1 decryption fallback (AES-256 without MAC or v2 prefix)
                string legacyV1 = EncryptLegacyV1(plain);
                string decryptedV1 = EncryptionUtility.Decrypt(legacyV1);
                ClassicAssert.AreEqual(plain, decryptedV1);

                // 舊版 V2（隨機 IV + HMAC）也只能走 legacy key migration 路徑。
                string legacyV2 = EncryptLegacyV2(plain);
                string decryptedV2 = EncryptionUtility.Decrypt(legacyV2);
                ClassicAssert.AreEqual(plain, decryptedV2);
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

        private static string EncryptLegacyV2(string plainText)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] key = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    "RimLLMSecretKeySeed2026UnitTestSalt2026"));
                byte[] macKey = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    "RimLLMSecretKeySeed2026UnitTestSalt2026:mac"));

                using (Aes aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.GenerateIV();

                    using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                    using (var ms = new System.IO.MemoryStream())
                    {
                        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        using (var sw = new System.IO.StreamWriter(cs))
                        {
                            sw.Write(plainText);
                        }

                        byte[] cipherBytes = ms.ToArray();
                        byte[] payload = new byte[aes.IV.Length + cipherBytes.Length];
                        Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
                        Buffer.BlockCopy(cipherBytes, 0, payload, aes.IV.Length, cipherBytes.Length);

                        using (var hmac = new HMACSHA256(macKey))
                        {
                            byte[] mac = hmac.ComputeHash(payload);
                            byte[] allBytes = new byte[payload.Length + mac.Length];
                            Buffer.BlockCopy(payload, 0, allBytes, 0, payload.Length);
                            Buffer.BlockCopy(mac, 0, allBytes, payload.Length, mac.Length);
                            return "v2:" + Convert.ToBase64String(allBytes);
                        }
                    }
                }
            }
        }

        [Test]
        public void TestRequestQueueRejectsAlreadyCanceledToken()
        {
            var settings = new MockSettings { MaxConcurrentRequests = 2 };
            var queue = new RimLLMRequestQueue(settings);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    await queue.AcquireSlotAsync(5, cts.Token);
                });
            }
        }

        [Test]
        public void TestRequestQueueKeepsFifoWithinSamePriority()
        {
            // 同優先級、同一個時鐘刻度內入列的請求先前以 DateTime 判先後，鍵值相等時
            // BinarySearch 會把後到的插到前面，變成後進先出。改用入列序號後必須嚴格 FIFO。
            var settings = new MockSettings { MaxConcurrentRequests = 1 };
            var queue = new RimLLMRequestQueue(settings);

            IDisposable holder = queue.AcquireSlotAsync(0, CancellationToken.None).GetAwaiter().GetResult();
            var waiters = new List<Task<IDisposable>>();
            for (int i = 0; i < 5; i++)
            {
                waiters.Add(queue.AcquireSlotAsync(0, CancellationToken.None));
            }
            // 高優先級最後入列，仍要排在所有同級項目之前。
            Task<IDisposable> urgent = queue.AcquireSlotAsync(10, CancellationToken.None);

            holder.Dispose();
            ClassicAssert.IsTrue(urgent.Wait(2000), "高優先級應最先取得名額");
            ClassicAssert.IsFalse(waiters.Exists(w => w.IsCompleted), "名額只有一個，同級項目此時都還在等");
            urgent.Result.Dispose();
            for (int i = 0; i < 5; i++)
            {
                int granted = Task.WaitAny(waiters.ToArray(), 2000);
                ClassicAssert.AreEqual(i, granted, "同優先級必須依入列順序取得名額");
                waiters[granted].Result.Dispose();
                waiters[granted] = new TaskCompletionSource<IDisposable>().Task; // 已處理者換成永不完成的佔位
            }
        }

        [Test]
        [NonParallelizable]
        public void TestDispatcherQueueBudgetAndErrorHandling()
        {
            RimLLMDispatcher.ResetQueueForTests();

            // TryEnqueueBounded with null
            ClassicAssert.IsFalse(RimLLMDispatcher.TryEnqueueBounded(null));

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
            ClassicAssert.AreEqual(2, processed);
            ClassicAssert.AreEqual(2, executionCount);

            RimLLMDispatcher.ResetQueueForTests();
            ClassicAssert.AreEqual(0, RimLLMDispatcher.QueuedCount);
        }

        [Test]
        public void TestChatClientAndEmbeddingClientFacades()
        {
            var manager = new RimLLMManager(new MockSettings());
            IChatClient chatClient = manager.CreateChatClient("test.mod");
            var metadata = (ChatClientMetadata)chatClient.GetService(typeof(ChatClientMetadata));
            ClassicAssert.IsNotNull(metadata);
            ClassicAssert.AreEqual("RimLLM", metadata.ProviderName);
            ClassicAssert.IsNull(chatClient.GetService(typeof(string)));
            chatClient.Dispose();

            var embeddingClient = new RimLLMEmbeddingClient(manager, "test.mod");
            ClassicAssert.IsNull(embeddingClient.GetService(typeof(string)));
            Assert.ThrowsAsync<ArgumentNullException>(async () => await embeddingClient.GenerateAsync(null));
            embeddingClient.Dispose();
        }

        [Test]
        public void TestChatClientExecutorNullMessagesHandling()
        {
            var cachedContextOptions = new RimLLMChatOptions { CachedContext = "System instruction" };
            var messagesWithSys = RimLLMChatClientExecutor.BuildMessages(null, cachedContextOptions);

            ClassicAssert.IsNotNull(messagesWithSys);
            ClassicAssert.AreEqual(1, messagesWithSys.Count);
            ClassicAssert.AreEqual(ChatRole.System, messagesWithSys[0].Role);
            ClassicAssert.AreEqual("System instruction", messagesWithSys[0].Text);

            var messagesEmpty = RimLLMChatClientExecutor.BuildMessages(null, null);

            ClassicAssert.IsNotNull(messagesEmpty);
            ClassicAssert.AreEqual(1, messagesEmpty.Count);
            ClassicAssert.AreEqual(ChatRole.User, messagesEmpty[0].Role);
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
            ClassicAssert.AreEqual("generated result", res);

            var chunks = new List<string>();
            await mock.StreamAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }, null, "m1", c => chunks.Add(c));
            ClassicAssert.IsTrue(chunks.Count > 0);
        }

        [Test]
        public async Task TestGeminiChatClientCoverage()
        {
            var settings = new MockSettings();
            settings.ApiKeys["Gemini"] = "test-key";
            var provider = new TestGeminiProvider(settings);
            using (IChatClient client = provider.CreateChatClient("gemini-1.5-flash"))
            {
                ClassicAssert.IsNotNull(client.GetService(typeof(ChatClientMetadata)));
                ClassicAssert.IsNull(client.GetService(typeof(string)));

                var resp = await client.GetResponseAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") });
                ClassicAssert.IsNotNull(resp);

                // 串流需以 SSE 格式回應，這是 OpenAI wire 協定的既有測試寫法。
                provider.WireHandler.ResponseContentType = "text/event-stream";
                provider.WireHandler.ResponseBody = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n";
                var streamed = new List<string>();
                await foreach (var update in client.GetStreamingResponseAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") }))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        streamed.Add(update.Text);
                    }
                }
                ClassicAssert.IsTrue(streamed.Count > 0);

                // 測試重複釋放串流列舉器時安全忽略 ObjectDisposedException
                var streamEnumerator = client.GetStreamingResponseAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") }).GetAsyncEnumerator();
                await streamEnumerator.MoveNextAsync();
                await streamEnumerator.DisposeAsync();
                await streamEnumerator.DisposeAsync();
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
            Assert.Throws<RimLLMException>(() => pipeline.ResolveCandidates(null, null));

            // 2. 無符合資格 Provider 例外
            settings.FallbackChain = new List<string> { "NonExistent:m1" };
            Assert.Throws<RimLLMException>(() => pipeline.ResolveCandidates(null, null));

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

            // 設定 P1 延遲 300ms, P2 延遲 50ms。
            // 健康帳本以「供應商:模型」為鍵，因此這裡要對應 fallback chain 的完整條目。
            ledger.RecordSuccess("P1:m1", 300);
            ledger.RecordSuccess("P2:m2", 50);

            ClassicAssert.AreEqual("P2", pipeline.ResolveCandidates(null, null)[0].ProviderId); // P2 延遲較低應排在前面

            // 4. PreferredModelId 優先插入
            settings.RoutingStrategy = 0; // Priority
            var preferred = pipeline.ResolveCandidates("P1:preferred-m", null)[0];
            ClassicAssert.AreEqual("P1:preferred-m", $"{preferred.ProviderId}:{preferred.ModelName}");

            // 5. MinFallbackLevel 分級過濾與 API 價格分級
            ClassicAssert.AreEqual(3, tracker.GetModelLevel("openai", "gpt-4o")); // Completion $10.00 >= $3.00 -> High (3)
            ClassicAssert.AreEqual(2, tracker.GetModelLevel("openai", "gpt-4o-mini")); // Completion $0.60 >= $0.50 -> Medium (2)
            ClassicAssert.AreEqual(1, tracker.GetModelLevel("gemini", "gemini-2.0-flash-lite")); // Completion $0.30 < $0.50 -> Low (1)
            ClassicAssert.AreEqual(1, tracker.GetModelLevel("deepseek", "deepseek-chat")); // Completion $0.28 < $0.50 -> Low (1)
            ClassicAssert.AreEqual(1, tracker.GetModelLevel("openai-compatible", "local-llama")); // 本地免費 -> Low (1)

            settings.ModelLevelOverrides["P2:pro-model"] = 3;
            settings.FallbackChain = new List<string> { "P1:mini-model", "P2:pro-model" }; // P1 預設為 tier 2, P2 覆寫為 tier 3
            var tiered = pipeline.ResolveCandidates(null, "high"); // 等級 3
            ClassicAssert.AreEqual(1, tiered.Count); // P1 (tier 2) 被過濾掉
            ClassicAssert.AreEqual("P2", tiered[0].ProviderId);

            // 6. RoundRobin 路由策略 (Strategy = 2)
            settings.RoutingStrategy = 2;
            ClassicAssert.AreEqual(2, pipeline.ResolveCandidates(null, "low").Count);

            // 7. 非可重試例外直接跳往備援
            settings.RoutingStrategy = 0;
            settings.FallbackChain = new List<string> { "P1:m1", "P2:m2" };
            settings.MaxRetries = 2;
            int p1Attempts = 0;
            int p2Attempts = 0;
            p1.GenerateHandler = (msgs, opts, m) =>
            {
                p1Attempts++;
                throw new ArgumentException("Invalid arguments - non-retryable");
            };
            p2.GenerateHandler = (msgs, opts, m) =>
            {
                p2Attempts++;
                return Task.FromResult("fallback-after-non-retry");
            };
            var router = new RimLLMFailoverChatClient(settings, ledger, tracker, pipeline, "test.pipe");
            await router.GetResponseAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") });
            ClassicAssert.AreEqual(1, p1Attempts); // 非可重試不重試，直接中斷給下一個
            ClassicAssert.AreEqual(1, p2Attempts);

            // 8. ClearCooldowns 會清空健康帳本
            ledger.RecordSuccess("P1:m1", 99);
            ledger.RecordFailure("P1:m1", isRetryable: true);
            ClassicAssert.IsTrue(ledger.IsInCooldown("P1:m1"));

            pipeline.ClearCooldowns();
            ClassicAssert.IsFalse(ledger.IsInCooldown("P1:m1"));
            ClassicAssert.AreEqual(0f, ledger.GetAverageLatency("P1:m1"));
        }

        [Test]
        public async Task TestChatExecutionPipelineCoverage()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);

            // 2. Silent mocking when daily budget is exceeded (BudgetPolicy = 1)
            settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");
            settings.DailyBudgetLimit = 1.0f;
            settings.DailyAccumulatedCost = 2.0f;
            settings.BudgetPolicy = 1; // SilentMocking

            // 預算檢查與靜默模擬已上移為中介層，不再由 pipeline 負責。
            var budgetClient = new RimLLMBudgetChatClient(
                new MockCustomChatClient(), new RimLLMUsageTracker(settings));
            var mockMessages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };

            ChatResponse mockRes = await budgetClient.GetResponseAsync(mockMessages);
            ClassicAssert.IsNotNull(mockRes?.Text);
            ClassicAssert.AreEqual("rimllm:budget-mock", mockRes.ModelId, "模擬回應要有可辨識的來源");

            string streamedText = "";
            var streamEnumerator = budgetClient.GetStreamingResponseAsync(mockMessages).GetAsyncEnumerator();
            try
            {
                while (streamEnumerator.MoveNextAsync().GetAwaiter().GetResult())
                {
                    streamedText += streamEnumerator.Current.Text;
                }
            }
            finally
            {
                streamEnumerator.DisposeAsync().GetAwaiter().GetResult();
            }
            ClassicAssert.AreEqual(mockRes.Text, streamedText, "串流與非串流的模擬內容必須一致");

            // 3. ResponseType with SilentMocking returns "{}"
            settings.DailyAccumulatedCost = 2.0f;
            var structuredOptions = new RimLLMChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [RimLLMChatOptions.ResponseTypeKey] = typeof(NullableTestDataStructure)
                }
            };
            ChatResponse objRes = await budgetClient.GetResponseAsync(mockMessages, structuredOptions);
            ClassicAssert.AreEqual("{}", objRes.Text);

            // 4. JSON Repair disabled throws RimLLMException
            settings.EnableJsonRepair = false;
            Assert.Throws<RimLLMException>(() => RimLLMJsonHelper.DeserializeStructured<NullableTestDataStructure>("invalid json", settings));

            // 5. Static JSON repair fallback
            settings.EnableJsonRepair = true;
            string markdownJson = "```json\n{\"Name\":\"repaired-str\",\"OptionalCount\":42}\n```";
            var parsed = RimLLMJsonHelper.DeserializeStructured<NullableTestDataStructure>(markdownJson, settings);
            ClassicAssert.AreEqual("repaired-str", parsed.Name);
            ClassicAssert.AreEqual(42, parsed.OptionalCount);

            // 6. Anti-abuse rate limiting trigger & cooldown
            // 節流狀態已從 pipeline 抽到共用的 RimLLMThrottleStore（由防濫用中介層使用）。
            settings.EnableAntiAbuse = true;
            settings.MaxRequestsPerWindow = 3;
            settings.ThrottlingWindowSeconds = 60;
            settings.CoolDownDurationSeconds = 60;
            var throttleStore = new RimLLMThrottleStore(settings);
            throttleStore.CheckAntiAbuse("test-abuse-mod");
            throttleStore.CheckAntiAbuse("test-abuse-mod");
            throttleStore.CheckAntiAbuse("test-abuse-mod");
            // 第 4 次觸發限流
            Assert.Throws<RimLLMException>(() => throttleStore.CheckAntiAbuse("test-abuse-mod"));
            // 冷卻中再次呼叫也拋出限流
            Assert.Throws<RimLLMException>(() => throttleStore.CheckAntiAbuse("test-abuse-mod"));
            throttleStore.ClearCooldowns();

            // 即使呼叫端輪換 modId，也必須受到共享安全上限保護。
            settings.MaxRequestsPerWindow = 1;
            var rotatingIdStore = new RimLLMThrottleStore(settings);
            for (int i = 0; i < 10; i++)
            {
                rotatingIdStore.CheckAntiAbuse("rotating-mod-" + i);
            }
            Assert.Throws<RimLLMException>(
                () => rotatingIdStore.CheckAntiAbuse("rotating-mod-overflow"));
            rotatingIdStore.ClearCooldowns();

            // 工具續輪不增加 per-Mod 視窗，但每個實際呼叫仍須消耗共享安全上限。
            var toolLoopStore = new RimLLMThrottleStore(settings);
            for (int i = 0; i < 10; i++)
            {
                toolLoopStore.CheckAntiAbuse("tool-loop-mod", countTowardWindow: false);
            }
            Assert.Throws<RimLLMException>(
                () => toolLoopStore.CheckAntiAbuse("tool-loop-mod", countTowardWindow: false));
            toolLoopStore.ClearCooldowns();

            // 7. HardBlock 預算政策 (BudgetPolicy = 0)：改由預算中介層負責攔阻。
            settings.BudgetPolicy = 0;
            settings.DailyBudgetLimit = 1.0f;
            settings.DailyAccumulatedCost = 2.0f;
            var hardBlockClient = new RimLLMBudgetChatClient(
                new MockCustomChatClient(), new RimLLMUsageTracker(settings));
            Assert.ThrowsAsync<RimLLMException>(
                async () => await hardBlockClient.GetResponseAsync(mockMessages));

            // 8. 驗證空物件與結構化必填驗證
            Assert.Throws<InvalidOperationException>(() => RimLLMManager.DeserializeAndValidate<NullableTestDataStructure>("null"));
        }

        [Test]
        public async Task TestNativeSchemaRejectionAndDoubleRepair()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);

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

            IChatClient schemaClient = manager.CreateChatClient("test.mod");
            var structuredOptions = new RimLLMChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [RimLLMChatOptions.ResponseTypeKey] = typeof(NullableTestDataStructure)
                }
            };
            ChatResponse res = await schemaClient.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }, structuredOptions);
            ClassicAssert.IsNotNull(res);
            ClassicAssert.IsTrue(res.Text.Contains("fallback-success"));

            // 2. 結構化修復失敗時拋出 RimLLMException
            Assert.Throws<RimLLMException>(() => RimLLMJsonHelper.DeserializeStructured<NullableTestDataStructure>("totally broken no json anywhere", settings));

            // 4. 結構化欄位驗證 (Field 必填為 null 拋出 InvalidOperationException)
            Assert.Throws<InvalidOperationException>(() => RimLLMManager.DeserializeAndValidate<TestStructureWithRequiredField>("{\"RequiredField\":null}"));
            Assert.Throws<InvalidOperationException>(() => RimLLMManager.DeserializeAndValidate<List<TestStructureWithRequiredField>>("[{\"RequiredField\":null}]"));

        }

        /// <summary>
        /// 服務端持續拒絕原生 schema 時，降級重打只能發生一次且必須改走提示式路徑。
        /// 先前的 catch 遞迴呼叫 GenerateAsync 本身，第二次仍送原生 schema，只要服務端一直拒絕就會無限重打；
        /// 上面的 TestNativeSchemaRejectionAndDoubleRepair 因 mock 第二次無條件成功而看不出來。
        /// </summary>
        [Test]
        public async Task TestPersistentNativeSchemaRejection_FallsBackToPromptPathExactlyOnce()
        {
            var settings = new MockSettings { EnableNativeSchema = true };
            var manager = new RimLLMManager(settings);

            int nativeCalls = 0;
            int promptCalls = 0;
            var alwaysRejecting = new MockTestProvider
            {
                ProviderId = "SchemaAlwaysReject",
                GenerateHandler = (msgs, opts, m) =>
                {
                    // executor 只有在走原生 schema 時才會設定 ResponseFormat。
                    if (opts?.ResponseFormat != null)
                    {
                        nativeCalls++;
                        throw new RimLLMException(LLMError.InvalidResponse, "400 response_format json_schema is not supported")
                        {
                            IsSchemaRejection = true
                        };
                    }
                    promptCalls++;
                    return Task.FromResult("{\"Name\":\"prompt-path\"}");
                }
            };
            alwaysRejecting.Capabilities.SupportsNativeStructuredOutput = true;
            settings.EnabledProviders["SchemaAlwaysReject"] = true;
            settings.ApiKeys["SchemaAlwaysReject"] = "test-key";
            settings.FallbackChain = new List<string> { "SchemaAlwaysReject:m1" };
            manager.RegisterProvider(alwaysRejecting);

            IChatClient client = manager.CreateChatClient("test.mod");
            var options = new RimLLMChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [RimLLMChatOptions.ResponseTypeKey] = typeof(NullableTestDataStructure)
                }
            };
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                ChatResponse res = await client.GetResponseAsync(
                    new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }, options, cts.Token);
                StringAssert.Contains("prompt-path", res.Text);
            }

            ClassicAssert.AreEqual(1, nativeCalls, "原生 schema 只能嘗試一次");
            ClassicAssert.AreEqual(1, promptCalls, "被拒後只能以提示式路徑重打一次");
        }

        /// <summary>
        /// 前綴比對必須停在版本邊界：先前 "gpt-4"（$30/$60）會吃到 "gpt-4.1-mini"（$0.40/$1.60），
        /// 成本高估 75 倍；日期後綴等以 "-" 接續的變體仍要能命中。
        /// </summary>
        [Test]
        public void TestModelRatePrefixMatchStopsAtVersionBoundary()
        {
            var tracker = new RimLLMUsageTracker(new MockSettings());

            float mini = tracker.EstimateCost("openai", "gpt-4.1-mini", 1000000, 1000000, 0);
            ClassicAssert.AreEqual(2.00f, mini, 0.01f, "gpt-4.1-mini 必須以自己的費率計算，不能落到 gpt-4");

            float datedMini = tracker.EstimateCost("openai", "gpt-4.1-mini-2025-04-14", 1000000, 1000000, 0);
            ClassicAssert.AreEqual(mini, datedMini, 0.001f, "日期後綴以 - 接續，仍屬同一費率");

            float gpt4 = tracker.EstimateCost("openai", "gpt-4-0613", 1000000, 1000000, 0);
            ClassicAssert.AreEqual(90.00f, gpt4, 0.01f, "gpt-4 自己的版本後綴照常命中");

            ClassicAssert.AreEqual(0f, tracker.EstimateCost("openai", "gpt-40", 1000000, 1000000, 0),
                "前綴後接數字不是版本邊界，不得命中 gpt-4");
        }

        [Test]
        public async Task TestBudgetAndTelemetryAccountingDeepening()
        {
            var settings = new MockSettings();
            var tracker = new RimLLMUsageTracker(settings);

            // 1. 定價精準匹配與前綴模糊匹配
            float exactCost = tracker.EstimateCost("openai", "gpt-4o", 1000000, 1000000, 0);
            ClassicAssert.AreEqual(12.50f, exactCost, 0.01f);

            float prefixCost = tracker.EstimateCost("openai", "gpt-4o-2024-11-20", 1000000, 1000000, 0);
            ClassicAssert.AreEqual(12.50f, prefixCost, 0.01f);

            float geminiPrefixCost = tracker.EstimateCost("gemini", "gemini-2.0-flash-001", 1000000, 1000000, 0);
            ClassicAssert.AreEqual(0.50f, geminiPrefixCost, 0.01f);

            // 2. 快取折扣計算
            float fullPromptCost = tracker.EstimateCost("gemini", "gemini-1.5-flash", 1000000, 0, 0);
            float cachedPromptCost = tracker.EstimateCost("gemini", "gemini-1.5-flash", 1000000, 0, 1000000);
            ClassicAssert.IsTrue(cachedPromptCost < fullPromptCost);
            ClassicAssert.AreEqual(fullPromptCost * 0.25f, cachedPromptCost, 0.001f);

            // 3. 未知模型回傳 0f
            float unknownCost = tracker.EstimateCost("unknown-prov", "unknown-model", 1000, 1000, 0);
            ClassicAssert.AreEqual(0f, unknownCost);

            // 4. 預算政策審查
            settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");
            settings.DailyBudgetLimit = 1.0f;
            settings.DailyAccumulatedCost = 2.0f;

            // Policy 0 = HardBlock
            settings.BudgetPolicy = 0;
            bool ok = tracker.CheckBudgetLimit();
            ClassicAssert.IsFalse(ok);

            // Policy 1 = SilentMocking
            settings.BudgetPolicy = 1;
            ok = tracker.CheckBudgetLimit();
            ClassicAssert.IsTrue(ok);
            ClassicAssert.IsTrue(tracker.IsBudgetMocked(false, out string mockStr));
            ClassicAssert.IsNotNull(mockStr);

            // Policy 2 = FallbackToFree
            settings.BudgetPolicy = 2;
            ok = tracker.CheckBudgetLimit();
            ClassicAssert.IsTrue(ok);

            // 5. 跨天重置
            settings.DailyBudgetResetDate = "2000-01-01";
            tracker.CheckDailyReset();
            ClassicAssert.AreEqual(0f, settings.DailyAccumulatedCost);
            ClassicAssert.AreEqual(DateTime.Today.ToString("yyyy-MM-dd"), settings.DailyBudgetResetDate);
        }

        [Test]
        public void TestAntiAbuseChatClientIsToolLoopContinuationDetailed()
        {
            // Null / empty cases
            ClassicAssert.IsFalse(RimLLMAntiAbuseChatClient.IsToolLoopContinuation(null));
            ClassicAssert.IsFalse(RimLLMAntiAbuseChatClient.IsToolLoopContinuation(new List<ChatMessage>()));

            // Message with null contents
            var msgNullContents = new ChatMessage(ChatRole.User, (IList<AIContent>)null);
            ClassicAssert.IsFalse(RimLLMAntiAbuseChatClient.IsToolLoopContinuation(new List<ChatMessage> { msgNullContents }));

            // Message with empty contents
            var msgEmptyContents = new ChatMessage(ChatRole.User, new List<AIContent>());
            ClassicAssert.IsFalse(RimLLMAntiAbuseChatClient.IsToolLoopContinuation(new List<ChatMessage> { msgEmptyContents }));

            // Message with non-tool contents (TextContent)
            var msgTextOnly = new ChatMessage(ChatRole.User, new List<AIContent> { new TextContent("Hello") });
            ClassicAssert.IsFalse(RimLLMAntiAbuseChatClient.IsToolLoopContinuation(new List<ChatMessage> { msgTextOnly }));

            // Message with FunctionResultContent
            var msgToolResult = new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call1", "result") });
            ClassicAssert.IsTrue(RimLLMAntiAbuseChatClient.IsToolLoopContinuation(new List<ChatMessage> { msgTextOnly, msgToolResult }));

            // Last message is not tool result even if previous was
            ClassicAssert.IsFalse(RimLLMAntiAbuseChatClient.IsToolLoopContinuation(new List<ChatMessage> { msgToolResult, msgTextOnly }));
        }

        [Test]
        public void TestChatEntryMetaDeserializeEdgeCases()
        {
            // Null or empty
            ClassicAssert.IsNull(ChatTestDrawer.ChatEntryMeta.Deserialize(null));
            ClassicAssert.IsNull(ChatTestDrawer.ChatEntryMeta.Deserialize(""));

            // Malformed entry with no equals
            var metaNoEquals = ChatTestDrawer.ChatEntryMeta.Deserialize("plain_text|no_equals");
            ClassicAssert.IsNotNull(metaNoEquals);
            ClassicAssert.AreEqual("", metaNoEquals.ModelId);

            // Empty key or value
            var metaEmptyKeyVal = ChatTestDrawer.ChatEntryMeta.Deserialize("=val|key=");
            ClassicAssert.IsNotNull(metaEmptyKeyVal);

            // Fully populated with unknown keys and invalid numbers
            string raw = "unknown=ignored|model=test-model|ms=invalid|tok=50|p=20|c=30|est=1";
            var meta = ChatTestDrawer.ChatEntryMeta.Deserialize(raw);
            ClassicAssert.IsNotNull(meta);
            ClassicAssert.AreEqual("test-model", meta.ModelId);
            ClassicAssert.AreEqual(0, meta.ElapsedMs); // "invalid" defaults to 0
            ClassicAssert.AreEqual(50, meta.TotalTokens);
            ClassicAssert.AreEqual(20, meta.PromptTokens);
            ClassicAssert.AreEqual(30, meta.CompletionTokens);
            ClassicAssert.IsTrue(meta.IsEstimatedTokens);

            // Round-trip serialize
            string serialized = meta.Serialize();
            ClassicAssert.IsTrue(serialized.Contains("model=test-model"));
            ClassicAssert.IsTrue(serialized.Contains("est=1"));
        }

        [Test]
        public void TestFallbackPipelinePrependPreferredModelEdgeCases()
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

            var p1 = new MockTestProvider { ProviderId = "P1" };
            providers["P1"] = p1;
            settings.EnabledProviders["P1"] = true;
            settings.ApiKeys["P1"] = "k1";
            settings.FallbackChain = new List<string> { "P1:default-model" };

            // Null or empty preferredModelId
            var res = pipeline.ResolveCandidates(null, null);
            ClassicAssert.AreEqual("default-model", res[0].ModelName);

            res = pipeline.ResolveCandidates("", null);
            ClassicAssert.AreEqual("default-model", res[0].ModelName);

            // Preferred model without colon
            res = pipeline.ResolveCandidates("invalidFormat", null);
            ClassicAssert.AreEqual("default-model", res[0].ModelName);

            // Preferred model already in chain at index 0
            res = pipeline.ResolveCandidates("P1:default-model", null);
            ClassicAssert.AreEqual(1, res.Count);
            ClassicAssert.AreEqual("default-model", res[0].ModelName);

            // Preferred model with disabled provider
            settings.EnabledProviders["P2"] = false;
            res = pipeline.ResolveCandidates("P2:some-model", null);
            ClassicAssert.AreEqual("default-model", res[0].ModelName);
        }

#pragma warning disable CS0649
        private class TestStructureWithRequiredField
        {
            public string RequiredField;
        }
#pragma warning restore CS0649
    }
}
