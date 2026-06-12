using NUnit.Framework;
using System;
using System.Reflection;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class FrameworkTests
    {
        [Test]
        public void TestEncryption()
        {
            string original = "sk-proj-1234567890abcdefghijklmnopqrstuvwxyz";
            string cipher = EncryptionUtility.Encrypt(original);
            Assert.IsNotEmpty(cipher);
            Assert.AreNotEqual(original, cipher);

            string decrypted = EncryptionUtility.Decrypt(cipher);
            Assert.AreEqual(original, decrypted);

            Assert.AreEqual("", EncryptionUtility.Encrypt(""));
            Assert.AreEqual("", EncryptionUtility.Decrypt(""));
        }

        [Test]
        public void TestClearLogs()
        {
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);
            
            var entry = new RimLLMManager.RequestLogEntry
            {
                Timestamp = DateTime.UtcNow,
                ModId = "test-mod",
                Provider = "OpenAI",
                Model = "gpt-4",
                Success = true,
                LatencyMs = 150
            };
            manager.RequestLogs.Enqueue(entry);
            Assert.AreEqual(1, manager.RequestLogs.Count);
            
            manager.ClearLogs();
            Assert.AreEqual(0, manager.RequestLogs.Count);
        }

        [Test]
        public void TestClientRegistry()
        {
            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            Assembly externalAssembly = typeof(string).Assembly;
            string modId = "test.unit.mod.id";

            // 1. 正常註冊與驗證
            ClientRegistry.RegisterClient(modId, thisAssembly);
            Assert.IsTrue(ClientRegistry.Verify(modId, thisAssembly));

            // 2. 阻擋來源不一致的呼叫
            Assert.IsFalse(ClientRegistry.Verify(modId, externalAssembly));

            // 3. 驗證無自動補註冊 (未註冊時 Verify 應回傳 false)
            string newModId = "auto.unit.mod.id";
            Assert.IsFalse(ClientRegistry.Verify(newModId, thisAssembly));
            
            // 註冊後驗證
            ClientRegistry.RegisterClient(newModId, thisAssembly);
            Assert.IsTrue(ClientRegistry.Verify(newModId, thisAssembly));
            Assert.IsFalse(ClientRegistry.Verify(newModId, externalAssembly));

            // 4. 驗證 RimLLM Framework 自身 Assembly 的內部放行機制 (避免內部調用崩潰)
            Assembly frameworkAssembly = typeof(ClientRegistry).Assembly;
            Assert.IsTrue(ClientRegistry.Verify(modId, frameworkAssembly));
            Assert.IsTrue(ClientRegistry.Verify(newModId, frameworkAssembly));
        }

        [Test]
        public void TestFallbackMechanism()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockFail:model-x", "MockSuccess:model-y" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockFail"] = true;
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockFail"] = "mock-key-1";
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-2";

            var manager = new RimLLMManager(mockSettings);

            int failCalls = 0;
            int successCalls = 0;

            var mockFail = new MockTestProvider
            {
                ProviderId = "MockFail",
                GenerateHandler = (req, model) =>
                {
                    failCalls++;
                    throw new Exception("Simulated connection failure");
                }
            };

            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (req, model) =>
                {
                    successCalls++;
                    return System.Threading.Tasks.Task.FromResult("success-data");
                }
            };

            manager.RegisterProvider(mockFail);
            manager.RegisterProvider(mockSuccess);

            var request = new LLMRequest 
            { 
                ModId = "test.fallback.unit.id",
                Prompt = "test" 
            };

            ClientRegistry.RegisterClient(request.ModId, Assembly.GetExecutingAssembly());
            string result = manager.GenerateAsync(request).GetAwaiter().GetResult();

            Assert.AreEqual("success-data", result);
            Assert.AreEqual(1, failCalls);
            Assert.AreEqual(1, successCalls);
        }

        [Test]
        public void TestSimpleRequestBuilderApi()
        {
            var chunks = new List<string>();

            var request = LLMRequest.Create("simple.builder.mod", "hello")
                .WithSystemPrompt("be concise")
                .WithSampling(maxTokens: 64, temperature: 0.2f)
                .WithReasoning(LLMReasoningEffort.Low)
                .WithPriority(3)
                .WithMinFallbackLevel("High")
                .WithCachedContext("stable context")
                .WithStreaming(chunk => chunks.Add(chunk));

            Assert.AreEqual("simple.builder.mod", request.ModId);
            Assert.AreEqual("hello", request.Prompt);
            Assert.AreEqual("be concise", request.SystemPrompt);
            Assert.AreEqual("stable context", request.CachedContext);
            Assert.AreEqual(64, request.MaxTokens);
            Assert.AreEqual(0.2f, request.Temperature);
            Assert.AreEqual(LLMReasoningEffort.Low, request.ReasoningEffort);
            Assert.AreEqual(3, request.Priority);
            Assert.AreEqual("High", request.MinFallbackLevel);
            Assert.IsTrue(request.EnableContextCaching);
            Assert.IsTrue(request.EnableStreaming);
            request.OnChunkReceived("chunk");
            Assert.AreEqual("chunk", chunks[0]);
        }

        [Test]
        public void TestSimpleGenerateAsyncOverload()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockSuccess:model-z" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);

            LLMRequest capturedRequest = null;
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (req, model) =>
                {
                    capturedRequest = req;
                    return System.Threading.Tasks.Task.FromResult("simple-response");
                }
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.simple.generate";
            ClientRegistry.RegisterClient(modId, Assembly.GetExecutingAssembly());

            string result = manager.GenerateAsync(
                modId,
                "hello",
                systemPrompt: "be concise",
                cachedContext: "stable context",
                maxTokens: 55,
                temperature: 0.3f,
                reasoningEffort: LLMReasoningEffort.Medium).GetAwaiter().GetResult();

            Assert.AreEqual("simple-response", result);
            Assert.IsNotNull(capturedRequest);
            Assert.AreEqual(modId, capturedRequest.ModId);
            Assert.AreEqual("hello", capturedRequest.Prompt);
            Assert.AreEqual("be concise", capturedRequest.SystemPrompt);
            Assert.AreEqual("stable context", capturedRequest.CachedContext);
            Assert.IsTrue(capturedRequest.EnableContextCaching);
            Assert.AreEqual(55, capturedRequest.MaxTokens);
            Assert.AreEqual(0.3f, capturedRequest.Temperature);
            Assert.AreEqual(LLMReasoningEffort.Medium, capturedRequest.ReasoningEffort);
        }

        [Test]
        public void TestGlobalDefaultReasoningEffortAppliedToAutoRequests()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockSuccess:model-z" },
                MaxRetries = 0,
                RetryDelay = 0f,
                DefaultReasoningEffort = LLMReasoningEffort.High
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);

            LLMRequest capturedRequest = null;
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (req, model) =>
                {
                    capturedRequest = req;
                    return System.Threading.Tasks.Task.FromResult("ok");
                }
            };
            manager.RegisterProvider(mockSuccess);

            var request = new LLMRequest
            {
                ModId = "test.global.reasoning.default",
                Prompt = "hello"
            };
            ClientRegistry.RegisterClient(request.ModId, Assembly.GetExecutingAssembly());

            string result = manager.GenerateAsync(request).GetAwaiter().GetResult();

            Assert.AreEqual("ok", result);
            Assert.IsNotNull(capturedRequest);
            Assert.AreEqual(LLMReasoningEffort.High, capturedRequest.ReasoningEffort);
            Assert.AreEqual(LLMReasoningEffort.Auto, request.ReasoningEffort, "Manager should not mutate caller-owned request instances.");
        }

        [Test]
        public void TestSimpleGenerateObjectAsyncOverload()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockSuccess:model-z" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);

            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (req, model) => System.Threading.Tasks.Task.FromResult("{\"Value\":7,\"Message\":\"ok\"}")
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.simple.object";
            ClientRegistry.RegisterClient(modId, Assembly.GetExecutingAssembly());

            var result = manager.GenerateObjectAsync<TestDataStructure>(
                modId,
                "make object",
                systemPrompt: "json only",
                cachedContext: "stable schema notes").GetAwaiter().GetResult();

            Assert.IsNotNull(result);
            Assert.AreEqual(7, result.Value);
            Assert.AreEqual("ok", result.Message);
        }

        [Test]
        public void TestSimpleGenerateStreamingAsyncOverload()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockStream:model-z" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockStream"] = true;
            mockSettings.ApiKeys["MockStream"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);

            var mockStream = new MockStreamProvider
            {
                ProviderId = "MockStream",
                StreamHandler = (req, model, onChunk) =>
                {
                    onChunk("a");
                    onChunk("b");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            };
            manager.RegisterProvider(mockStream);

            const string modId = "test.simple.streaming";
            ClientRegistry.RegisterClient(modId, Assembly.GetExecutingAssembly());

            var chunks = new List<string>();
            string result = manager.GenerateStreamingAsync(modId, "stream please", chunk => chunks.Add(chunk)).GetAwaiter().GetResult();

            Assert.AreEqual("ab", result);
            Assert.AreEqual(2, chunks.Count);
            Assert.AreEqual("a", chunks[0]);
            Assert.AreEqual("b", chunks[1]);
        }

        [Test]
        public void TestGenerateObjectStructureAndCache()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockSuccess:model-z" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);

            string requestedPromptReceived = null;
            string requestedSystemPromptReceived = null;

            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (req, model) =>
                {
                    requestedPromptReceived = req.Prompt;
                    requestedSystemPromptReceived = req.SystemPrompt;
                    // 回傳合法的 JSON 字串，並刻意帶有 markdown 標記與尾隨逗號以測試 JSON 修復器
                    return System.Threading.Tasks.Task.FromResult("```json\n{\n  \"Value\": 42,\n  \"Message\": \"Hello Cache\",\n}\n```");
                }
            };
            manager.RegisterProvider(mockSuccess);

            // 1. 預熱測試
            manager.RegisterResponseType<TestDataStructure>();

            var request = new LLMRequest
            {
                ModId = "test.object.unit.id",
                Prompt = "Give me 42",
                SystemPrompt = "Base System Prompt"
            };

            ClientRegistry.RegisterClient(request.ModId, Assembly.GetExecutingAssembly());

            // 2. 測試物件生成與修復 (這也會測試 CallingAssembly 白名單放行是否正常，不拋出 Security Exception)
            var resultObject = manager.GenerateObjectAsync<TestDataStructure>(request).GetAwaiter().GetResult();

            Assert.IsNotNull(resultObject);
            Assert.AreEqual(42, resultObject.Value);
            Assert.AreEqual("Hello Cache", resultObject.Message);
            Assert.IsTrue(requestedSystemPromptReceived.Contains("Value"));
            Assert.IsTrue(requestedSystemPromptReceived.Contains("Base System Prompt"));
        }

        [Test]
        public void TestOpenRouterFallbackPayload()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["OpenRouter"] = "mock-key";
            
            var provider = new OpenRouterProvider(mockSettings);
            
            var method = provider.GetType().GetMethod("BuildPayload", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(method);
            
            var request = new LLMRequest { Prompt = "hello" };
            
            // 1. 測試單一模型
            var payloadSingle = method.Invoke(provider, new object[] { request, "model-a", false }) as Newtonsoft.Json.Linq.JObject;
            Assert.IsNotNull(payloadSingle);
            Assert.AreEqual("model-a", payloadSingle["model"]?.ToString());
            Assert.IsNull(payloadSingle["models"]);
            
            // 2. 測試多個模型 (逗號分隔)
            var payloadMultiple = method.Invoke(provider, new object[] { request, "model-a, model-b , model-c", false }) as Newtonsoft.Json.Linq.JObject;
            Assert.IsNotNull(payloadMultiple);
            Assert.IsNull(payloadMultiple["model"]);
            Assert.IsNotNull(payloadMultiple["models"]);
            
            var modelsArray = payloadMultiple["models"] as Newtonsoft.Json.Linq.JArray;
            Assert.IsNotNull(modelsArray);
            Assert.AreEqual(3, modelsArray.Count);
            Assert.AreEqual("model-a", modelsArray[0].ToString());
            Assert.AreEqual("model-b", modelsArray[1].ToString());
            Assert.AreEqual("model-c", modelsArray[2].ToString());
        }

        [Test]
        public void TestPureProviderFallbackResolution()
        {
            var mockSettings = new MockSettings();
            mockSettings.SetModelList("OpenRouter", new List<string> { "model-1", "model-2" });
            
            var manager = new RimLLMManager(mockSettings);

            // 1. 測試傳統 "Provider:Model" 格式
            bool res1 = manager.ResolveFallbackEntry("OpenAI:gpt-4o", out string providerId1, out string modelName1);
            Assert.IsTrue(res1);
            Assert.AreEqual("OpenAI", providerId1);
            Assert.AreEqual("gpt-4o", modelName1);

            // 2. 測試 OpenRouter 純供應商格式 (會自動解析為快取的第一個模型，此處為 model-1)
            bool res2 = manager.ResolveFallbackEntry("OpenRouter", out string providerId2, out string modelName2);
            Assert.IsTrue(res2);
            Assert.AreEqual("OpenRouter", providerId2);
            Assert.AreEqual("model-1", modelName2);

            // 3. 測試其他純供應商格式 (會自動回退至 defaultModel)
            bool res3 = manager.ResolveFallbackEntry("OpenAI", out string providerId3, out string modelName3);
            Assert.IsTrue(res3);
            Assert.AreEqual("OpenAI", providerId3);
            Assert.AreEqual("default", modelName3);
        }

        [Test]
        public void TestPriorityQueueAndCancellation()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockProv:model-a" },
                MaxConcurrentRequests = 1
            };
            mockSettings.EnabledProviders["MockProv"] = true;
            mockSettings.ApiKeys["MockProv"] = "mock-key";

            var manager = new RimLLMManager(mockSettings);
            
            var tcs1 = new System.Threading.Tasks.TaskCompletionSource<string>();
            var tcs2 = new System.Threading.Tasks.TaskCompletionSource<string>();

            int callCount = 0;
            var mockProv = new MockTestProvider
            {
                ProviderId = "MockProv",
                GenerateHandler = (req, model) =>
                {
                    callCount++;
                    if (callCount == 1) return tcs1.Task;
                    return tcs2.Task;
                }
            };
            manager.RegisterProvider(mockProv);

            // 1. 執行第 1 個
            var req1 = new LLMRequest { ModId = "mod1", Prompt = "p1" };
            ClientRegistry.RegisterClient("mod1", Assembly.GetExecutingAssembly());
            var task1 = manager.GenerateAsync(req1);

            // 2. 執行第 2 個，但先設定 CancellationToken
            var cts = new System.Threading.CancellationTokenSource();
            var req2 = new LLMRequest { ModId = "mod2", Prompt = "p2", CancellationToken = cts.Token };
            ClientRegistry.RegisterClient("mod2", Assembly.GetExecutingAssembly());
            var task2 = manager.GenerateAsync(req2);

            // 驗證只有 1 個請求實際被調用
            Assert.AreEqual(1, callCount);

            // 在 req1 還在執行時，取消 req2 
            cts.Cancel();

            // 驗證 task2 被標記為已取消
            Assert.Throws<AggregateException>(() => task2.Wait());
            Assert.IsTrue(task2.IsCanceled);

            // 釋放第 1 個
            tcs1.SetResult("r1");
            Assert.AreEqual("r1", task1.GetAwaiter().GetResult());

            // 驗證第 2 個請求因為在佇列中被取消，根本沒有被 provider 呼叫過
            Assert.AreEqual(1, callCount);
        }

        [Test]
        public void TestMinFallbackLevelFilter()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockProv:model-mini", "MockProv:model-pro" }
            };
            mockSettings.EnabledProviders["MockProv"] = true;
            mockSettings.ApiKeys["MockProv"] = "mock-key";

            var manager = new RimLLMManager(mockSettings);
            
            var calledModels = new List<string>();
            var mockProv = new MockTestProvider
            {
                ProviderId = "MockProv",
                GenerateHandler = (req, model) =>
                {
                    calledModels.Add(model);
                    return System.Threading.Tasks.Task.FromResult("success");
                }
            };
            manager.RegisterProvider(mockProv);

            // MinFallbackLevel = High (應跳過 model-mini(Medium)，只使用 model-pro(High))
            var req = new LLMRequest { ModId = "mod", Prompt = "p", MinFallbackLevel = "High" };
            ClientRegistry.RegisterClient(req.ModId, Assembly.GetExecutingAssembly());
            string res = manager.GenerateAsync(req).GetAwaiter().GetResult();

            Assert.AreEqual("success", res);
            Assert.AreEqual(1, calledModels.Count);
            Assert.AreEqual("model-pro", calledModels[0]);
        }

        [Test]
        public void TestCircuitBreaker()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockFail:model-a", "MockSuccess:model-b" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockFail"] = true;
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockFail"] = "key1";
            mockSettings.ApiKeys["MockSuccess"] = "key2";

            var manager = new RimLLMManager(mockSettings);
            
            int failCount = 0;
            var mockFail = new MockTestProvider
            {
                ProviderId = "MockFail",
                GenerateHandler = (req, model) =>
                {
                    failCount++;
                    throw new Exception("Temporary Error");
                }
            };
            int successCount = 0;
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (req, model) =>
                {
                    successCount++;
                    return System.Threading.Tasks.Task.FromResult("ok");
                }
            };
            manager.RegisterProvider(mockFail);
            manager.RegisterProvider(mockSuccess);

            var req = new LLMRequest { ModId = "mod", Prompt = "p", MaxTokens = 5 };
            ClientRegistry.RegisterClient(req.ModId, Assembly.GetExecutingAssembly());

            // 連續呼叫 3 次失敗以進入冷卻
            for (int i = 0; i < 3; i++)
            {
                try { manager.GenerateAsync(req).GetAwaiter().GetResult(); } catch {}
            }
            Assert.AreEqual(3, failCount);
            Assert.AreEqual(3, successCount);

            // 第 4 次呼叫，因進入冷卻，MockFail 應被跳過，只呼叫 MockSuccess
            string res = manager.GenerateAsync(req).GetAwaiter().GetResult();
            Assert.AreEqual("ok", res);
            Assert.AreEqual(3, failCount); // 還是 3，被跳過了
            Assert.AreEqual(4, successCount);
        }

        [Test]
        public void TestNonRetryableInvalidKeyDoesNotRetryOrTripCircuit()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockFail:model-a", "MockSuccess:model-b" },
                MaxRetries = 5,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockFail"] = true;
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockFail"] = "bad-key";
            mockSettings.ApiKeys["MockSuccess"] = "good-key";

            var manager = new RimLLMManager(mockSettings);

            int failCount = 0;
            var mockFail = new MockTestProvider
            {
                ProviderId = "MockFail",
                GenerateHandler = (req, model) =>
                {
                    failCount++;
                    throw new RimLLMException(LLMError.InvalidKey, "Invalid key");
                }
            };

            int successCount = 0;
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (req, model) =>
                {
                    successCount++;
                    return System.Threading.Tasks.Task.FromResult("ok");
                }
            };

            manager.RegisterProvider(mockFail);
            manager.RegisterProvider(mockSuccess);

            var req = new LLMRequest { ModId = "test.invalidkey.retry", Prompt = "p" };
            ClientRegistry.RegisterClient(req.ModId, Assembly.GetExecutingAssembly());

            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual("ok", manager.GenerateAsync(req).GetAwaiter().GetResult());
            }

            Assert.AreEqual(4, failCount, "InvalidKey should be tried once per request, not retried or cooled down.");
            Assert.AreEqual(4, successCount);
        }

        [Test]
        public void TestDoubleRepair()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockProv:model-z" }
            };
            mockSettings.EnabledProviders["MockProv"] = true;
            mockSettings.ApiKeys["MockProv"] = "key";

            var manager = new RimLLMManager(mockSettings);
            
            int callCount = 0;
            var mockProv = new MockTestProvider
            {
                ProviderId = "MockProv",
                GenerateHandler = (req, model) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        return System.Threading.Tasks.Task.FromResult("{{ Value: 100");
                    }
                    else
                    {
                        return System.Threading.Tasks.Task.FromResult("{\"Value\": 99, \"Message\": \"repaired\"}");
                    }
                }
            };
            manager.RegisterProvider(mockProv);

            var req = new LLMRequest { ModId = "mod", Prompt = "p" };
            ClientRegistry.RegisterClient(req.ModId, Assembly.GetExecutingAssembly());
            var res = manager.GenerateObjectAsync<TestDataStructure>(req).GetAwaiter().GetResult();

            Assert.IsNotNull(res);
            Assert.AreEqual(99, res.Value);
            Assert.AreEqual("repaired", res.Message);
            Assert.AreEqual(2, callCount); // 總共呼叫了 2 次 (首次失敗 + 二次修復)
        }

        [Test]
        public void TestDynamicHardwareSalt()
        {
            string original = "sensitive-api-key";
            
            // 1. 設定 Salt-A
            EncryptionUtility.CustomSalt = "SaltA";
            EncryptionUtility.InitializeKeyAndIv();
            string cipherA = EncryptionUtility.Encrypt(original);
            
            // 2. 設定 Salt-B
            EncryptionUtility.CustomSalt = "SaltB";
            EncryptionUtility.InitializeKeyAndIv();
            string cipherB = EncryptionUtility.Encrypt(original);

            Assert.AreNotEqual(cipherA, cipherB); // 不同 Salt 加密出的結果應該不同

            // 3. 驗證同 Salt 可以解密，異 Salt 會解密失敗或解出空字串
            EncryptionUtility.CustomSalt = "SaltA";
            EncryptionUtility.InitializeKeyAndIv();
            string decryptedA = EncryptionUtility.Decrypt(cipherA);
            Assert.AreEqual(original, decryptedA);

            string decryptedB = EncryptionUtility.Decrypt(cipherB);
            Assert.AreNotEqual(original, decryptedB); // 異 Salt 解密失敗
        }

        [Test]
        public void TestMultipleApiKeysRoundRobin()
        {
            var settings = new RimLLMFrameworkSettings();
            settings.SetApiKey("TestProvider", "key-a, key-b; key-c");
            
            // 驗證輪詢邏輯 (多個以逗號或分號分隔的 key 會循環回傳)
            Assert.AreEqual("key-a", settings.GetActiveApiKey("TestProvider"));
            Assert.AreEqual("key-b", settings.GetActiveApiKey("TestProvider"));
            Assert.AreEqual("key-c", settings.GetActiveApiKey("TestProvider"));
            Assert.AreEqual("key-a", settings.GetActiveApiKey("TestProvider")); // 繞回第一個金鑰
        }

        [Test]
        public void TestTokenUsageAndCostRecording()
        {
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);

            // 1. 初始狀態應該是 0
            Assert.AreEqual(0, mockSettings.TotalPromptTokens);
            Assert.AreEqual(0, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(0f, mockSettings.TotalEstimatedCost);

            // 2. 未知或未維護精確費率的模型只累計 tokens，不再用 provider 類別粗估金額。
            manager.RecordUsage("OpenAI", "gpt-4o", 100000, 50000);

            Assert.AreEqual(100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(50000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(0f, mockSettings.TotalEstimatedCost);

            // 3. 已知精確費率模型才累計估算金額。
            manager.RecordUsage("Anthropic", "claude-sonnet-4-6", 1000000, 1000000);

            Assert.AreEqual(1100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(1050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(18.00f, mockSettings.TotalEstimatedCost, 0.0001f);

            // 4. Gemini 模型若帶官方 models/ 前綴也能正規化。
            manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 1000000);

            Assert.AreEqual(2100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(2050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(20.80f, mockSettings.TotalEstimatedCost, 0.0001f);
        }

        [Test]
        public void TestAnthropicFetchAvailableModelsUsesApiResponse()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["Anthropic"] = "mock-key";
            mockSettings.Endpoints["Anthropic"] = "https://api.anthropic.com/v1/messages";

            var provider = new TestAnthropicModelListProvider(
                mockSettings,
                "{\"data\":[{\"id\":\"claude-sonnet-4-6\"},{\"id\":\"claude-opus-4-8\"},{\"id\":\"claude-sonnet-4-6\"}]}"
            );

            var models = provider.FetchAvailableModelsAsync().GetAwaiter().GetResult();

            Assert.AreEqual("https://api.anthropic.com/v1/models", provider.LastUrl);
            Assert.AreEqual(2, models.Count);
            Assert.AreEqual("claude-sonnet-4-6", models[0]);
            Assert.AreEqual("claude-opus-4-8", models[1]);
        }

        [Test]
        public void TestResetUsage()
        {
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);

            // 1. 設置一些初始使用量
            mockSettings.TotalPromptTokens = 5000;
            mockSettings.TotalCompletionTokens = 3000;
            mockSettings.TotalEstimatedCost = 0.05f;

            // 2. 執行重置
            manager.ResetUsage();

            // 3. 應該歸零
            Assert.AreEqual(0, mockSettings.TotalPromptTokens);
            Assert.AreEqual(0, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(0f, mockSettings.TotalEstimatedCost);
        }

        [Test]
        public void TestCachedContextRequestApi()
        {
            var request = new LLMRequest
            {
                Prompt = "dynamic question",
                SystemPrompt = "base behavior"
            }.WithCachedContext("stable colony state");

            Assert.IsTrue(request.EnableContextCaching);
            Assert.AreEqual("stable colony state", request.CachedContext);
            Assert.AreEqual("base behavior\n\nstable colony state", request.GetEffectiveSystemPrompt());

            var clone = request.Clone();
            Assert.AreEqual(request.CachedContext, clone.CachedContext);
            Assert.AreEqual(request.GetEffectiveSystemPrompt(), clone.GetEffectiveSystemPrompt());
        }

        [Test]
        public void TestAnthropicPromptCachingPayload()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["Anthropic"] = "mock-key";
            
            var provider = new TestAnthropicProvider(mockSettings);
            var request = new LLMRequest
            {
                ModId = "test",
                Prompt = "hello",
                SystemPrompt = "base-system-instructions",
                CachedContext = "stable-colony-context-for-caching",
                EnableContextCaching = true
            };

            string response = provider.GenerateAsync(request, "claude-3-5-sonnet").GetAwaiter().GetResult();
            Assert.AreEqual("mocked-response", response);
            Assert.IsNotNull(provider.InterceptedPayload);

            var payloadObj = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
            var systemArray = payloadObj["system"] as Newtonsoft.Json.Linq.JArray;
            Assert.IsNotNull(systemArray, "Anthropic system prompt payload must be an array when caching is enabled");
            Assert.AreEqual(1, systemArray.Count);
            Assert.AreEqual("text", systemArray[0]["type"]?.ToString());
            Assert.AreEqual("base-system-instructions\n\nstable-colony-context-for-caching", systemArray[0]["text"]?.ToString());
            Assert.AreEqual("ephemeral", systemArray[0]["cache_control"]?["type"]?.ToString());
        }

        [Test]
        public void TestGeminiContextCachingFlow()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["Gemini"] = "mock-key";

            var provider = new TestGeminiProvider(mockSettings);
            var request = new LLMRequest
            {
                ModId = "test",
                Prompt = "hello",
                SystemPrompt = "base-system-instructions",
                CachedContext = "stable-colony-context-for-gemini-caching",
                EnableContextCaching = true
            };

            // 1. 第一次呼叫：應觸發快取建立與快取引用
            string response1 = provider.GenerateAsync(request, "gemini-1.5-pro").GetAwaiter().GetResult();
            Assert.AreEqual("gemini-response", response1);
            Assert.AreEqual(2, provider.SendCalls.Count);

            // 驗證第一個呼叫是建立快取
            var firstCall = provider.SendCalls[0];
            Assert.IsTrue(firstCall.url.Contains("cachedContents"));
            var firstPayload = Newtonsoft.Json.Linq.JObject.Parse(firstCall.payload);
            Assert.AreEqual("models/gemini-1.5-pro", firstPayload["model"]?.ToString());
            Assert.AreEqual("base-system-instructions\n\nstable-colony-context-for-gemini-caching", firstPayload["systemInstruction"]?["parts"]?[0]?["text"]?.ToString());

            // 驗證第二個呼叫是生成內容，且使用了 cachedContent 屬性並不包含 systemInstruction
            var secondCall = provider.SendCalls[1];
            Assert.IsTrue(secondCall.url.Contains("generateContent"));
            var secondPayload = Newtonsoft.Json.Linq.JObject.Parse(secondCall.payload);
            Assert.AreEqual("cachedContents/mock-cache-id", secondPayload["cachedContent"]?.ToString());
            Assert.IsNull(secondPayload["systemInstruction"]);

            // 2. 第二次呼叫：快取已存在，應直接引用而不重複建立快取
            provider.SendCalls.Clear();
            string response2 = provider.GenerateAsync(request, "gemini-1.5-pro").GetAwaiter().GetResult();
            Assert.AreEqual("gemini-response", response2);
            Assert.AreEqual(1, provider.SendCalls.Count);

            var thirdCall = provider.SendCalls[0];
            Assert.IsTrue(thirdCall.url.Contains("generateContent"));
            var thirdPayload = Newtonsoft.Json.Linq.JObject.Parse(thirdCall.payload);
            Assert.AreEqual("cachedContents/mock-cache-id", thirdPayload["cachedContent"]?.ToString());
            Assert.IsNull(thirdPayload["systemInstruction"]);
        }

        [Test]
        public void TestReasoningEffortPayloads()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["OpenAI"] = "mock-key";
            mockSettings.ApiKeys["Anthropic"] = "mock-key";
            mockSettings.ApiKeys["Gemini"] = "mock-key";
            mockSettings.ApiKeys["OpenRouter"] = "mock-key";

            // 1. OpenAI: o1 Model with ReasoningEffort.Medium
            {
                var provider = new TestOpenAIProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Medium,
                    MaxTokens = 1500
                };
                string response = provider.GenerateAsync(request, "o1-mini").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.InterceptedPayload);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.AreEqual("medium", payload["reasoning_effort"]?.ToString());
                Assert.AreEqual(1500, (int)payload["max_completion_tokens"]);
                Assert.IsNull(payload["temperature"]);
                Assert.IsNull(payload["max_tokens"]);
            }

            // 2. OpenAI: gpt-4o Model with ReasoningEffort.Medium (should NOT include reasoning_effort)
            {
                var provider = new TestOpenAIProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Medium,
                    Temperature = 0.7f,
                    MaxTokens = 1000
                };
                string response = provider.GenerateAsync(request, "gpt-4o").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.InterceptedPayload);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNull(payload["reasoning_effort"]);
                Assert.AreEqual(0.7f, (float)payload["temperature"]);
                Assert.AreEqual(1000, (int)payload["max_tokens"]);
                Assert.IsNull(payload["max_completion_tokens"]);
            }

            // 3. Anthropic: Claude 3.7 with ReasoningEffort.High
            {
                var provider = new TestAnthropicProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.High,
                    Temperature = 0.5f,
                    MaxTokens = 1000
                };
                string response = provider.GenerateAsync(request, "claude-3-7-sonnet").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.InterceptedPayload);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNotNull(payload["thinking"]);
                Assert.AreEqual("enabled", payload["thinking"]["type"]?.ToString());
                Assert.AreEqual(4096, (int)payload["thinking"]["budget_tokens"]);
                Assert.AreEqual(1.0f, (float)payload["temperature"]);
                // max_tokens should be adjusted to be greater than budget_tokens
                Assert.IsTrue((int)payload["max_tokens"] > 4096);
            }
            // 3b. Anthropic: Claude 3.5 (non-thinking model) with ReasoningEffort.High (should NOT include thinking)
            {
                var provider = new TestAnthropicProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.High,
                    Temperature = 0.5f,
                    MaxTokens = 1000
                };
                string response = provider.GenerateAsync(request, "claude-3-5-sonnet").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.InterceptedPayload);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNull(payload["thinking"]);
                Assert.AreEqual(0.5f, (float)payload["temperature"]);
                Assert.AreEqual(1000, (int)payload["max_tokens"]);
            }

            // 3c. Anthropic: Claude 4 (adaptive-thinking model) with ReasoningEffort.Medium (should include adaptive thinking)
            {
                var provider = new TestAnthropicProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Medium,
                    Temperature = 0.5f,
                    MaxTokens = 1000
                };
                string response = provider.GenerateAsync(request, "claude-4-sonnet").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.InterceptedPayload);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNotNull(payload["thinking"]);
                Assert.AreEqual("adaptive", payload["thinking"]["type"]?.ToString());
                Assert.AreEqual("medium", payload["thinking"]["effort"]?.ToString());
                Assert.IsNull(payload["thinking"]["budget_tokens"]);
                Assert.AreEqual(1.0f, (float)payload["temperature"]);
            }

            // 4. Gemini: Gemini with ReasoningEffort.Low
            {
                var provider = new TestGeminiProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Low,
                    MaxTokens = 2000
                };
                string response = provider.GenerateAsync(request, "gemini-2.0-flash-thinking-exp").GetAwaiter().GetResult();
                Assert.AreEqual(1, provider.SendCalls.Count);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.SendCalls[0].payload);
                Assert.IsNotNull(payload["generationConfig"]["thinkingConfig"]);
                Assert.AreEqual(1024, (int)payload["generationConfig"]["thinkingConfig"]["thinkingBudget"]);
            }

            // 4b. Gemini: Gemini 1.5 Pro (non-thinking model) with ReasoningEffort.Low (should NOT include thinkingConfig)
            {
                var provider = new TestGeminiProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Low,
                    MaxTokens = 2000
                };
                string response = provider.GenerateAsync(request, "gemini-1.5-pro").GetAwaiter().GetResult();
                Assert.AreEqual(1, provider.SendCalls.Count);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.SendCalls[0].payload);
                Assert.IsNull(payload["generationConfig"]["thinkingConfig"]);
            }

            // 4c. Gemini: Gemma 4 (thinking-level model) with ReasoningEffort.Medium (should include thinkingLevel)
            {
                var provider = new TestGeminiProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Medium,
                    MaxTokens = 2000
                };
                string response = provider.GenerateAsync(request, "gemma-4-it-b-t").GetAwaiter().GetResult();
                Assert.AreEqual(1, provider.SendCalls.Count);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.SendCalls[0].payload);
                Assert.IsNotNull(payload["generationConfig"]["thinkingConfig"]);
                Assert.AreEqual("medium", payload["generationConfig"]["thinkingConfig"]["thinkingLevel"]?.ToString());
                Assert.IsNull(payload["generationConfig"]["thinkingConfig"]["thinkingBudget"]);
            }

            // 5. OpenRouter: DeepSeek R1 with ReasoningEffort.Medium
            {
                var provider = new TestOpenRouterProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Medium
                };
                string response = provider.GenerateAsync(request, "deepseek/deepseek-r1").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.InterceptedPayload);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.AreEqual(2048, (int)payload["max_thinking_tokens"]);
            }

            // 6. Test LLMReasoningEffort.Auto and LLMReasoningEffort.None payloads

            // 6a. OpenAI Auto
            {
                var provider = new TestOpenAIProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Auto
                };
                string response = provider.GenerateAsync(request, "o1-mini").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.InterceptedPayload);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNull(payload["reasoning_effort"]);
            }

            // 6b. Gemini 2.0 Auto -> thinkingBudget = -1
            {
                var provider = new TestGeminiProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Auto
                };
                string response = provider.GenerateAsync(request, "gemini-2.0-flash-thinking-exp").GetAwaiter().GetResult();
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.SendCalls[0].payload);
                Assert.IsNotNull(payload["generationConfig"]["thinkingConfig"]);
                Assert.AreEqual(-1, (int)payload["generationConfig"]["thinkingConfig"]["thinkingBudget"]);
            }

            // 6c. Gemini 2.0 None -> thinkingBudget = 0
            {
                var provider = new TestGeminiProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.None
                };
                string response = provider.GenerateAsync(request, "gemini-2.0-flash-thinking-exp").GetAwaiter().GetResult();
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.SendCalls[0].payload);
                Assert.IsNotNull(payload["generationConfig"]["thinkingConfig"]);
                Assert.AreEqual(0, (int)payload["generationConfig"]["thinkingConfig"]["thinkingBudget"]);
            }

            // 6d. Gemma 4 Auto -> Omit thinkingLevel
            {
                var provider = new TestGeminiProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Auto
                };
                string response = provider.GenerateAsync(request, "gemma-4-it-b-t").GetAwaiter().GetResult();
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.SendCalls[0].payload);
                Assert.IsNull(payload["generationConfig"]["thinkingConfig"]);
            }

            // 6e. Gemma 4 None -> thinkingLevel = "minimal"
            {
                var provider = new TestGeminiProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.None
                };
                string response = provider.GenerateAsync(request, "gemma-4-it-b-t").GetAwaiter().GetResult();
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.SendCalls[0].payload);
                Assert.IsNotNull(payload["generationConfig"]["thinkingConfig"]);
                Assert.AreEqual("minimal", payload["generationConfig"]["thinkingConfig"]["thinkingLevel"]?.ToString());
            }

            // 6f. Anthropic Claude 3.7 Auto -> enabled with 1024 budget
            {
                var provider = new TestAnthropicProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Auto
                };
                string response = provider.GenerateAsync(request, "claude-3-7-sonnet").GetAwaiter().GetResult();
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNotNull(payload["thinking"]);
                Assert.AreEqual("enabled", payload["thinking"]["type"]?.ToString());
                Assert.AreEqual(1024, (int)payload["thinking"]["budget_tokens"]);
            }

            // 6g. Anthropic Claude 4 Auto -> adaptive without effort
            {
                var provider = new TestAnthropicProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Auto
                };
                string response = provider.GenerateAsync(request, "claude-4-sonnet").GetAwaiter().GetResult();
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNotNull(payload["thinking"]);
                Assert.AreEqual("adaptive", payload["thinking"]["type"]?.ToString());
                Assert.IsNull(payload["thinking"]["effort"]);
            }

            // 6h. Anthropic Claude 3.7 None -> Omit thinking
            {
                var provider = new TestAnthropicProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.None
                };
                string response = provider.GenerateAsync(request, "claude-3-7-sonnet").GetAwaiter().GetResult();
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNull(payload["thinking"]);
            }

            // 6i. OpenRouter Auto -> Omit max_thinking_tokens
            {
                var provider = new TestOpenRouterProvider(mockSettings);
                var request = new LLMRequest
                {
                    Prompt = "hello",
                    ReasoningEffort = LLMReasoningEffort.Auto
                };
                string response = provider.GenerateAsync(request, "deepseek/deepseek-r1").GetAwaiter().GetResult();
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNull(payload["max_thinking_tokens"]);
            }
        }

        [Test]
        public void TestUnifiedStreamingInGenerateAsync()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockStream:model-s" }
            };
            mockSettings.EnabledProviders["MockStream"] = true;
            mockSettings.ApiKeys["MockStream"] = "mock-key";

            var manager = new RimLLMManager(mockSettings);

            var chunksReceived = new List<string>();
            var mockStream = new MockStreamProvider
            {
                ProviderId = "MockStream",
                StreamHandler = (req, model, onChunk) =>
                {
                    onChunk("Hello ");
                    onChunk("World");
                    onChunk("!");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            };
            manager.RegisterProvider(mockStream);

            var request = new LLMRequest
            {
                ModId = "test.stream.unified.id",
                Prompt = "test prompt",
                EnableStreaming = true,
                OnChunkReceived = chunk => chunksReceived.Add(chunk)
            };

            ClientRegistry.RegisterClient(request.ModId, Assembly.GetExecutingAssembly());

            string result = manager.GenerateAsync(request).GetAwaiter().GetResult();

            Assert.AreEqual("Hello World!", result);
            Assert.AreEqual(3, chunksReceived.Count);
            Assert.AreEqual("Hello ", chunksReceived[0]);
            Assert.AreEqual("World", chunksReceived[1]);
            Assert.AreEqual("!", chunksReceived[2]);
        }

        [Test]
        public void TestComplexTypeSchemaWarmupAndRecursion()
        {
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);
            
            // 預熱無空建構子、帶有循環引用的型別，驗證不會 StackOverflow 且產生合理 JSON
            manager.RegisterResponseType<ComplexTestDataStructure>();

            string json = manager.GetSampleJson(typeof(ComplexTestDataStructure));
            
            Assert.IsNotEmpty(json);
            Assert.AreNotEqual("{}", json);
            Assert.IsTrue(json.Contains("\"Name\":\"string\""), "應該遞迴產生 string 欄位的 dummy 資料");
            Assert.IsTrue(json.Contains("\"Age\":0"), "應該遞迴展開 int 欄位的 dummy 資料");
            Assert.IsTrue(json.Contains("\"IsActive\":false"), "應該遞迴展開 bool 欄位的 dummy 資料");
            Assert.IsTrue(json.Contains("\"Skills\":[\"string\"]"), "應該產生 List 的範例陣列元素");
            Assert.IsTrue(json.Contains("\"Mapping\":{\"string\":0}"), "應該產生 Dictionary 的範例鍵值對");
            Assert.IsTrue(json.Contains("\"Nested\":{"), "應該遞迴展開 Nested 屬性");
            Assert.IsTrue(json.Contains("\"SelfRef\":null"), "循環引用欄位在偵測到之後應截斷為 null，避免 StackOverflow");
        }

        [Test]
        public void TestReasoningThoughtsPackaging()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["OpenAI"] = "mock-key";
            mockSettings.ApiKeys["Gemini"] = "mock-key";
            mockSettings.ApiKeys["Anthropic"] = "mock-key";

            // 1. 測試 OpenAIProvider (DeepSeek-R1 格式 reasoning_content)
            {
                var provider = new TestOpenAIProviderWithReasoning(mockSettings);
                var request = new LLMRequest { Prompt = "hello" };
                string result = provider.GenerateAsync(request, "deepseek-reasoning").GetAwaiter().GetResult();
                Assert.IsTrue(result.Contains("<think>"));
                Assert.IsTrue(result.Contains("</think>"));
                Assert.IsTrue(result.Contains("Assessing the situation..."));
                Assert.IsTrue(result.Contains("Hello, user!"));
            }

            // 2. 測試 GeminiProvider (thought: true 欄位)
            {
                var provider = new TestGeminiProviderWithReasoning(mockSettings);
                var request = new LLMRequest { Prompt = "hello" };
                string result = provider.GenerateAsync(request, "gemini-thinking").GetAwaiter().GetResult();
                Assert.IsTrue(result.Contains("<think>"));
                Assert.IsTrue(result.Contains("</think>"));
                Assert.IsTrue(result.Contains("Thinking deeply..."));
                Assert.IsTrue(result.Contains("Response from Gemini"));
            }

            // 3. 測試 AnthropicProvider (thinking block)
            {
                var provider = new TestAnthropicProviderWithReasoning(mockSettings);
                var request = new LLMRequest { Prompt = "hello" };
                string result = provider.GenerateAsync(request, "claude-thinking").GetAwaiter().GetResult();
                Assert.IsTrue(result.Contains("<think>"));
                Assert.IsTrue(result.Contains("</think>"));
                Assert.IsTrue(result.Contains("Formulating the answer..."));
                Assert.IsTrue(result.Contains("Final output text"));
            }
        }

        [Test]
        public void TestExternalProviderRegistration()
        {
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);

            // 1. 註冊外部供應商成功，且出現在已註冊清單中
            var external = new MockTestProvider { ProviderId = "MyCustomProvider" };
            manager.RegisterProvider(external);
            Assert.IsTrue(manager.GetRegisteredProviderIds().Contains("MyCustomProvider"));

            // 2. 外部供應商視為註冊即啟用
            Assert.IsTrue(manager.IsProviderEnabled("MyCustomProvider"));

            // 3. 重複 ProviderId 應擲出，防止覆蓋既有供應商（含內建）
            Assert.Throws<InvalidOperationException>(() =>
                manager.RegisterProvider(new MockTestProvider { ProviderId = "MyCustomProvider" }));
            Assert.Throws<InvalidOperationException>(() =>
                manager.RegisterProvider(new MockTestProvider { ProviderId = "OpenAI" }));

            // 4. 內建供應商仍依設定啟用狀態
            mockSettings.EnabledProviders["OpenAI"] = false;
            Assert.IsFalse(manager.IsProviderEnabled("OpenAI"));
        }

        [Test]
        public void TestModelLevelOverrideTakesPriority()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockProv:model-mini", "MockProv:model-pro" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockProv"] = true;
            mockSettings.ApiKeys["MockProv"] = "mock-key";
            // 將原本關鍵字判定為 Medium 的 model-mini 覆寫為 High
            mockSettings.ModelLevelOverrides["model-mini"] = 3;

            var manager = new RimLLMManager(mockSettings);
            var calledModels = new List<string>();
            manager.RegisterProvider(new MockTestProvider
            {
                ProviderId = "MockProv",
                GenerateHandler = (req, model) =>
                {
                    calledModels.Add(model);
                    return System.Threading.Tasks.Task.FromResult("success");
                }
            });

            var req = new LLMRequest { ModId = "mod.level.override", Prompt = "p", MinFallbackLevel = "High" };
            ClientRegistry.RegisterClient(req.ModId, Assembly.GetExecutingAssembly());
            string res = manager.GenerateAsync(req).GetAwaiter().GetResult();

            // 覆寫生效：model-mini 被視為 High，不再被 MinFallbackLevel 過濾
            Assert.AreEqual("success", res);
            Assert.AreEqual(1, calledModels.Count);
            Assert.AreEqual("model-mini", calledModels[0]);
        }
    }

    public class TestDataStructure
    {
        public int Value { get; set; } = 100;
        public string Message { get; set; } = "default";
    }

    public class MockSettings : IRimLLMSettings
    {
        public List<string> FallbackChain { get; set; } = new List<string>();
        public float ApiTimeout { get; set; } = 30f;
        public bool DetailedLogging { get; set; } = true;
        public LLMReasoningEffort DefaultReasoningEffort { get; set; } = LLMReasoningEffort.Auto;
        public int MaxRetries { get; set; } = 3;
        public float RetryDelay { get; set; } = 3f;
        public int MaxConcurrentRequests { get; set; } = 2;
        public long TotalPromptTokens { get; set; } = 0;
        public long TotalCompletionTokens { get; set; } = 0;
        public float TotalEstimatedCost { get; set; } = 0f;

        public Dictionary<string, string> ApiKeys = new Dictionary<string, string>();
        public Dictionary<string, string> Endpoints = new Dictionary<string, string>();
        public Dictionary<string, bool> EnabledProviders = new Dictionary<string, bool>();
        public Dictionary<string, List<string>> ModelLists = new Dictionary<string, List<string>>();

        public string GetApiKey(string providerId) => ApiKeys.TryGetValue(providerId, out var val) ? val : "";
        public string GetActiveApiKey(string providerId) => GetApiKey(providerId);
        public string GetEndpoint(string providerId, string defaultVal) => Endpoints.TryGetValue(providerId, out var val) ? val : defaultVal;
        public bool IsProviderEnabled(string providerId) => EnabledProviders.TryGetValue(providerId, out var val) ? val : true;
        public List<string> GetModelList(string providerId) => ModelLists.TryGetValue(providerId, out var val) ? val : new List<string>();
        
        public string GetDefaultModel(string providerId, string defaultVal)
        {
            var list = GetModelList(providerId);
            return list.Count > 0 ? list[0] : defaultVal;
        }

        public void SetModelList(string providerId, List<string> models) => ModelLists[providerId] = models;

        public Dictionary<string, int> ModelLevelOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int GetModelLevelOverride(string modelName) =>
            !string.IsNullOrEmpty(modelName) && ModelLevelOverrides.TryGetValue(modelName, out var level) ? level : 0;

        public void Write() {}
    }

    public class MockTestProvider : ILLMProvider
    {
        public string ProviderId { get; set; }
        public bool RequiresApiKey { get; set; } = true;

        public Func<LLMRequest, string, System.Threading.Tasks.Task<string>> GenerateHandler { get; set; }

        public System.Threading.Tasks.Task<string> GenerateAsync(LLMRequest request, string model)
        {
            return GenerateHandler != null ? GenerateHandler(request, model) : System.Threading.Tasks.Task.FromResult("");
        }

        public System.Threading.Tasks.Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived)
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task<TestResult> TestConnectionAsync()
        {
            return System.Threading.Tasks.Task.FromResult(new TestResult { Success = true });
        }

        public System.Threading.Tasks.Task<List<string>> FetchAvailableModelsAsync()
        {
            return System.Threading.Tasks.Task.FromResult(new List<string> { "model-test" });
        }
    }

    public class TestAnthropicProvider : AnthropicProvider
    {
        public string InterceptedPayload { get; private set; }

        public TestAnthropicProvider(IRimLLMSettings settings) : base(settings) {}

        protected override System.Threading.Tasks.Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            InterceptedPayload = payload;
            return System.Threading.Tasks.Task.FromResult("{\"content\": [{\"type\": \"text\", \"text\": \"mocked-response\"}]}");
        }
    }

    public class TestAnthropicModelListProvider : AnthropicProvider
    {
        private readonly string _responseJson;
        public string LastUrl { get; private set; }

        public TestAnthropicModelListProvider(IRimLLMSettings settings, string responseJson) : base(settings)
        {
            _responseJson = responseJson;
        }

        protected override System.Threading.Tasks.Task<string> SendGetAsync(string url, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            LastUrl = url;
            return System.Threading.Tasks.Task.FromResult(_responseJson);
        }
    }

    public class TestGeminiProvider : GeminiProvider
    {
        public List<(string url, string payload)> SendCalls { get; } = new List<(string, string)>();

        public TestGeminiProvider(IRimLLMSettings settings) : base(settings) {}

        protected override System.Threading.Tasks.Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            SendCalls.Add((url, payload));
            if (url.Contains("cachedContents"))
            {
                string expireStr = DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                return System.Threading.Tasks.Task.FromResult("{\"name\": \"cachedContents/mock-cache-id\", \"expireTime\": \"" + expireStr + "\"}");
            }
            return System.Threading.Tasks.Task.FromResult("{\"candidates\": [{\"content\": {\"parts\": [{\"text\": \"gemini-response\"}]}}]}");
        }
    }

    public class TestOpenAIProvider : OpenAIProvider
    {
        public string InterceptedPayload { get; private set; }

        public TestOpenAIProvider(IRimLLMSettings settings) : base(settings) {}

        protected override System.Threading.Tasks.Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            InterceptedPayload = payload;
            return System.Threading.Tasks.Task.FromResult("{\"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"mocked-openai-response\"}}]}");
        }
    }

    public class TestOpenRouterProvider : OpenRouterProvider
    {
        public string InterceptedPayload { get; private set; }

        public TestOpenRouterProvider(IRimLLMSettings settings) : base(settings) {}

        protected override System.Threading.Tasks.Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            InterceptedPayload = payload;
            return System.Threading.Tasks.Task.FromResult("{\"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"mocked-openrouter-response\"}}]}");
        }
    }

    public class MockStreamProvider : ILLMProvider
    {
        public string ProviderId { get; set; }
        public bool RequiresApiKey { get; set; } = true;
        public Func<LLMRequest, string, Action<string>, System.Threading.Tasks.Task> StreamHandler { get; set; }

        public System.Threading.Tasks.Task<string> GenerateAsync(LLMRequest request, string model)
        {
            return System.Threading.Tasks.Task.FromResult("");
        }

        public System.Threading.Tasks.Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived)
        {
            return StreamHandler != null ? StreamHandler(request, model, onChunkReceived) : System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task<TestResult> TestConnectionAsync()
        {
            return System.Threading.Tasks.Task.FromResult(new TestResult { Success = true });
        }

        public System.Threading.Tasks.Task<List<string>> FetchAvailableModelsAsync()
        {
            return System.Threading.Tasks.Task.FromResult(new List<string> { "model-stream" });
        }
    }

    public class ComplexTestDataStructure
    {
        public string Name { get; set; }
        public int Age { get; set; }
        public bool IsActive { get; set; }
        public List<string> Skills { get; set; }
        public Dictionary<string, int> Mapping { get; set; }
        public NestedData Nested { get; set; }
        
        public ComplexTestDataStructure(string dummyParam)
        {
            // 此類別沒有無參數建構子！
        }
    }

    public class NestedData
    {
        public float Weight { get; set; }
        public ComplexTestDataStructure SelfRef { get; set; } // 循環引用測試
    }

    public class TestOpenAIProviderWithReasoning : OpenAIProvider
    {
        public TestOpenAIProviderWithReasoning(IRimLLMSettings settings) : base(settings) {}

        protected override System.Threading.Tasks.Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            return System.Threading.Tasks.Task.FromResult(
                "{\"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"Hello, user!\", \"reasoning_content\": \"Assessing the situation...\"}}]}"
            );
        }
    }

    public class TestGeminiProviderWithReasoning : GeminiProvider
    {
        public TestGeminiProviderWithReasoning(IRimLLMSettings settings) : base(settings) {}

        protected override System.Threading.Tasks.Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            return System.Threading.Tasks.Task.FromResult(
                "{\"candidates\": [{\"content\": {\"parts\": [{\"text\": \"Thinking deeply...\", \"thought\": true}, {\"text\": \"Response from Gemini\"}]}}]}"
            );
        }
    }

    public class TestAnthropicProviderWithReasoning : AnthropicProvider
    {
        public TestAnthropicProviderWithReasoning(IRimLLMSettings settings) : base(settings) {}

        protected override System.Threading.Tasks.Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            return System.Threading.Tasks.Task.FromResult(
                "{\"content\": [{\"type\": \"thinking\", \"thinking\": \"Formulating the answer...\"}, {\"type\": \"text\", \"text\": \"Final output text\"}]}"
            );
        }
    }
}

