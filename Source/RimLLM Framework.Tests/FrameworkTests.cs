extern alias bclasync;
using NUnit.Framework;
using System;
using System.Reflection;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Google.GenAI;
using Google.GenAI.Types;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using OpenAI.Chat;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;
using RimLLM_Framework.Mod;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

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
                RetryDelay = 0f,
                RoutingStrategy = 0
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
                GenerateHandler = (msgs, opts, model) =>
                {
                    failCalls++;
                    throw new Exception("Simulated connection failure");
                }
            };

            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) =>
                {
                    successCalls++;
                    return System.Threading.Tasks.Task.FromResult("success-data");
                }
            };

            manager.RegisterProvider(mockFail);
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.fallback.unit.id";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "test") };
            string result = client.GetResponseAsync(messages).GetAwaiter().GetResult().Text;

            Assert.AreEqual("success-data", result);
            Assert.AreEqual(1, failCalls);
            Assert.AreEqual(1, successCalls);
        }

        [Test]
        public void TestSimpleRequestBuilderApi()
        {
            var options = new RimLLMChatOptions
            {
                Temperature = 0.2f,
                MaxOutputTokens = 64,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
                Priority = 3,
                MinFallbackLevel = "High",
                CachedContext = "stable context",
                EnableContextCaching = true
            };

            Assert.AreEqual("stable context", options.CachedContext);
            Assert.AreEqual(64, options.MaxOutputTokens);
            Assert.AreEqual(0.2f, options.Temperature);
            Assert.AreEqual(ReasoningEffort.Low, options.Reasoning.Effort);
            Assert.AreEqual(3, options.Priority);
            Assert.AreEqual("High", options.MinFallbackLevel);
            Assert.IsTrue(options.EnableContextCaching);
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

            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) =>
                {
                    return System.Threading.Tasks.Task.FromResult("simple-response");
                }
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.simple.generate";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> 
            { 
                new ChatMessage(ChatRole.System, "be concise"),
                new ChatMessage(ChatRole.User, "hello") 
            };
            var options = new RimLLMChatOptions
            {
                CachedContext = "stable context",
                MaxOutputTokens = 55,
                Temperature = 0.3f,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium }
            };

            string result = client.GetResponseAsync(messages, options).GetAwaiter().GetResult().Text;

            Assert.AreEqual("simple-response", result);
        }

        [Test]
        public void TestGlobalDefaultReasoningEffortAppliedToAutoRequests()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockSuccess:model-z" },
                MaxRetries = 0,
                RetryDelay = 0f,
                DefaultReasoningEffort = ReasoningEffort.High
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);

            ChatOptions capturedOptions = null;
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) =>
                {
                    capturedOptions = opts;
                    return System.Threading.Tasks.Task.FromResult("ok");
                }
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.global.reasoning.default";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") };
            string result = client.GetResponseAsync(messages).GetAwaiter().GetResult().Text;

            Assert.AreEqual("ok", result);
            Assert.IsNotNull(capturedOptions);
            Assert.AreEqual(ReasoningEffort.High, capturedOptions.Reasoning?.Effort);
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
                GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("{\"Value\":7,\"Message\":\"ok\"}")
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.simple.object";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> 
            { 
                new ChatMessage(ChatRole.System, "json only"),
                new ChatMessage(ChatRole.User, "make object") 
            };
            var options = new RimLLMChatOptions { CachedContext = "stable schema notes" };
            var result = client.GetResponseObjectAsync<TestDataStructure>(messages, options).GetAwaiter().GetResult();

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
                StreamHandler = (msgs, opts, model, onChunk) =>
                {
                    onChunk("a");
                    onChunk("b");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            };
            manager.RegisterProvider(mockStream);

            const string modId = "test.simple.streaming";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var chunks = new List<string>();
            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "stream please") };
            var enumerator = client.GetStreamingResponseAsync(messages).GetAsyncEnumerator();
            string result = "";
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    ChatResponseUpdate update = enumerator.Current;
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        result += update.Text;
                        chunks.Add(update.Text);
                    }
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

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
                GenerateHandler = (msgs, opts, model) =>
                {
                    requestedPromptReceived = System.Linq.Enumerable.FirstOrDefault(msgs, m => m.Role == ChatRole.User)?.Text;
                    requestedSystemPromptReceived = string.Join("\n", System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(msgs, m => m.Role == ChatRole.System), m => m.Text)));
                    // 回傳合法的 JSON 字串，並刻意帶有 markdown 標記與尾隨逗號以測試 JSON 修復器
                    return System.Threading.Tasks.Task.FromResult("```json\n{\n  \"Value\": 42,\n  \"Message\": \"Hello Cache\",\n}\n```");
                }
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.object.unit.id";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, "Base System Prompt"),
                new ChatMessage(ChatRole.User, "Give me 42")
            };

            var resultObject = client.GetResponseObjectAsync<TestDataStructure>(messages).GetAwaiter().GetResult();

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

            var provider = new TestOpenRouterProvider(mockSettings);

            const string modId = "test.openrouter.fallback";
            ClientRegistry.RegisterClient(modId, Assembly.GetExecutingAssembly());

            // 1. 測試單一模型
            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") };
            provider.GenerateAsync(messages, null, "model-a").GetAwaiter().GetResult();
            var payloadSingle = JObject.Parse(provider.InterceptedPayload);
            Assert.AreEqual("model-a", payloadSingle["model"]?.ToString());
            Assert.IsNull(payloadSingle["models"]);

            // 2. 測試多個模型 (逗號分隔)
            provider.GenerateAsync(messages, null, "model-a, model-b , model-c").GetAwaiter().GetResult();
            var payloadMultiple = JObject.Parse(provider.InterceptedPayload);
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
                GenerateHandler = (msgs, opts, model) =>
                {
                    callCount++;
                    if (callCount == 1) return tcs1.Task;
                    return tcs2.Task;
                }
            };
            manager.RegisterProvider(mockProv);

            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient("mod1");
            RimLLMProvider.RegisterClient("mod2");
            IChatClient client1 = RimLLMProvider.CreateChatClient("mod1");
            IChatClient client2 = RimLLMProvider.CreateChatClient("mod2");

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "p1") };
            var task1 = client1.GetResponseAsync(messages);

            // 2. 執行第 2 個，但先設定 CancellationToken
            var cts = new System.Threading.CancellationTokenSource();
            var task2 = client2.GetResponseAsync(messages, cancellationToken: cts.Token);

            // 驗證只有 1 個請求實際被調用
            Assert.AreEqual(1, callCount);

            // 在 req1 還在執行時，取消 req2 
            cts.Cancel();

            // 驗證 task2 被標記為已取消
            Assert.Throws<AggregateException>(() => task2.Wait());
            Assert.IsTrue(task2.IsCanceled);

            // 釋放第 1 個
            tcs1.SetResult("r1");
            Assert.AreEqual("r1", task1.GetAwaiter().GetResult().Text);

            // 驗證第 2 個請求因為在佇列中被取消，根本沒有被 provider 呼叫過
            Assert.AreEqual(1, callCount);
        }

        [Test]
        public void TestMinFallbackLevelFilter()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockProv:model-mini", "MockProv:model-pro" },
                RoutingStrategy = 0
            };
            mockSettings.ModelLevelOverrides["MockProv:model-mini"] = 2; // Medium
            mockSettings.ModelLevelOverrides["MockProv:model-pro"] = 3;  // High
            mockSettings.EnabledProviders["MockProv"] = true;
            mockSettings.ApiKeys["MockProv"] = "mock-key";

            var manager = new RimLLMManager(mockSettings);
            
            var calledModels = new List<string>();
            var mockProv = new MockTestProvider
            {
                ProviderId = "MockProv",
                GenerateHandler = (msgs, opts, model) =>
                {
                    calledModels.Add(model);
                    return System.Threading.Tasks.Task.FromResult("success");
                }
            };
            manager.RegisterProvider(mockProv);

            const string modId = "mod.minfallback";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "p") };
            var options = new RimLLMChatOptions { MinFallbackLevel = "High" };
            string res = client.GetResponseAsync(messages, options).GetAwaiter().GetResult().Text;

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
                RetryDelay = 0f,
                RoutingStrategy = 0
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
                GenerateHandler = (msgs, opts, model) =>
                {
                    failCount++;
                    throw new Exception("Temporary Error");
                }
            };
            int successCount = 0;
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) =>
                {
                    successCount++;
                    return System.Threading.Tasks.Task.FromResult("ok");
                }
            };
            manager.RegisterProvider(mockFail);
            manager.RegisterProvider(mockSuccess);

            const string modId = "mod.circuitbreaker";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "p") };
            var options = new ChatOptions { MaxOutputTokens = 5 };

            // 連續呼叫 3 次失敗以進入冷卻
            for (int i = 0; i < 3; i++)
            {
                try { client.GetResponseAsync(messages, options).GetAwaiter().GetResult(); } catch {}
                manager.ClearCooldowns();
            }
            Assert.AreEqual(3, failCount);
            Assert.AreEqual(3, successCount);

            // 第 4 次呼叫，因進入冷卻，MockFail 應被跳過，只呼叫 MockSuccess
            string res = client.GetResponseAsync(messages, options).GetAwaiter().GetResult().Text;
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
                GenerateHandler = (msgs, opts, model) =>
                {
                    failCount++;
                    throw new RimLLMException(LLMError.InvalidKey, "Invalid key");
                }
            };

            int successCount = 0;
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) =>
                {
                    successCount++;
                    return System.Threading.Tasks.Task.FromResult("ok");
                }
            };

            manager.RegisterProvider(mockFail);
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.invalidkey.retry";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "p") };

            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);
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
                GenerateHandler = (msgs, opts, model) =>
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

            const string modId = "mod.doublerepair";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "p") };
            var res = client.GetResponseObjectAsync<TestDataStructure>(messages).GetAwaiter().GetResult();

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
        public void TestSettingsDefaultRoutingStrategy()
        {
            var settings = new RimLLMFrameworkSettings();
            Assert.AreEqual(2, settings.RoutingStrategy);
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
            manager.RecordUsage("Gemini", "gemini-2.5-flash", 1000000, 1000000);

            Assert.AreEqual(1100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(1050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(2.80f, mockSettings.TotalEstimatedCost, 0.0001f);

            // 4. Gemini 模型若帶官方 models/ 前綴也能正規化。
            manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 1000000);

            Assert.AreEqual(2100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(2050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(5.60f, mockSettings.TotalEstimatedCost, 0.0001f);

            manager.RecordUsage("Gemini", "gemini-3.5-flash", 1000000, 1000000);

            Assert.AreEqual(3100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(3050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(16.10f, mockSettings.TotalEstimatedCost, 0.0001f);

            manager.RecordUsage("DeepSeek", "deepseek-v4-flash", 1000000, 1000000);
            Assert.AreEqual(4100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(4050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(16.52f, mockSettings.TotalEstimatedCost, 0.0001f);

            manager.RecordUsage("Groq", "llama-3.3-70b-versatile", 1000000, 1000000);
            Assert.AreEqual(5100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(5050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(17.90f, mockSettings.TotalEstimatedCost, 0.0001f);

            manager.RecordUsage("MiniMax", "MiniMax-M3", 1000000, 1000000);
            Assert.AreEqual(6100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(6050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(19.40f, mockSettings.TotalEstimatedCost, 0.0001f);
        }

        [Test]
        public void TestCachedTokenDiscountReducesCost()
        {
            // 快取命中的輸入 Token 應以折扣費率計價，藉此反映 Context Caching 的節省。
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);

            // Gemini：輸入價 $0.3/M，cache read 折扣 0.25x。
            // 1,000,000 輸入中有 800,000 為快取命中 → 200,000*0.3 + 800,000*0.3*0.25 = $0.06 + $0.06 = $0.12
            manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 0, 800000);
            Assert.AreEqual(1000000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(0.12f, mockSettings.TotalEstimatedCost, 0.0001f);

            // 對照組：相同輸入但完全無快取應為 $0.30。
            mockSettings.TotalEstimatedCost = 0f;
            manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 0, 0);
            Assert.AreEqual(0.30f, mockSettings.TotalEstimatedCost, 0.0001f);

            // 防呆：cachedPromptTokens 超過 promptTokens 時應被夾到上限，不會出現負值或溢出。
            mockSettings.TotalEstimatedCost = 0f;
            manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 0, 5000000);
            // 全部視為快取命中：1,000,000 * 0.30/M * 0.25 = $0.075
            Assert.AreEqual(0.075f, mockSettings.TotalEstimatedCost, 0.0001f);

            mockSettings.TotalEstimatedCost = 0f;
            manager.RecordUsage("DeepSeek", "deepseek-v4-flash", 1000000, 0, 1000000);
            Assert.AreEqual(0.0028f, mockSettings.TotalEstimatedCost, 0.0001f);
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
        public void TestOpenAIProviderCacheUsage()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["OpenAI"] = "mock-key";
            var manager = new RimLLMManager(mockSettings);
            
            // 初始化 SDK 入口，讓 OpenAIProvider 能 RecordUsage
            RimLLMProvider.Initialize(manager);

            var provider = new TestOpenAIProviderWithUsage(mockSettings);
            
            // 測試 1：標準 OpenAI 的 prompt_tokens_details.cached_tokens
            provider.MockResponse = "{" +
                "\"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"hello\"}}]," +
                "\"usage\": {" +
                "  \"prompt_tokens\": 1000," +
                "  \"completion_tokens\": 200," +
                "  \"prompt_tokens_details\": {" +
                "    \"cached_tokens\": 600" +
                "  }" +
                "}" +
                "}";

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "ping") };
            string res = provider.GenerateAsync(messages, null, "gpt-4o").GetAwaiter().GetResult();
            
            Assert.AreEqual("hello", res);
            Assert.AreEqual(1000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(200, mockSettings.TotalCompletionTokens);
            
            // 驗證 UsageTracker 內部的統計數據
            var stats = manager.UsageTracker.ProviderStatistics["OpenAI"];
            Assert.AreEqual(1000, stats.TotalPromptTokens);
            Assert.AreEqual(600, stats.CachedPromptTokens);
            Assert.AreEqual(0.6f, stats.ContextCacheHitRate, 0.0001f);

            // 測試 2：OpenAI 標準格式的 cached_tokens 位於 prompt_tokens_details
            mockSettings.TotalPromptTokens = 0;
            mockSettings.TotalCompletionTokens = 0;
            stats.TotalPromptTokens = 0;
            stats.CachedPromptTokens = 0;

            provider.MockResponse = "{" +
                "\"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"hello 2\"}}]," +
                "\"usage\": {" +
                "  \"prompt_tokens\": 2000," +
                "  \"completion_tokens\": 300," +
                "  \"prompt_tokens_details\": {" +
                "    \"cached_tokens\": 800" +
                "  }" +
                "}" +
                "}";

            string res2 = provider.GenerateAsync(messages, null, "gpt-4o").GetAwaiter().GetResult();
            Assert.AreEqual("hello 2", res2);
            Assert.AreEqual(2000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(300, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(2000, stats.TotalPromptTokens);
            Assert.AreEqual(800, stats.CachedPromptTokens);
            Assert.AreEqual(0.4f, stats.ContextCacheHitRate, 0.0001f);
        }


        [Test]
        public void TestCachedContextRequestApi()
        {
            var options = new RimLLMChatOptions
            {
                CachedContext = "stable colony state"
            };

            Assert.IsTrue(options.EnableContextCaching);
            Assert.AreEqual("stable colony state", options.CachedContext);
        }

        [Test]
        public void TestGeminiContextCachingFlow()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["Gemini"] = "mock-key";

            var provider = new TestGeminiProvider(mockSettings);
            // 內容須超過顯式快取門檻（pro 模型約 2048 token，以字元數為保守下界），否則會正確地略過快取改走 systemInstruction
            const string systemPrompt = "base-system-instructions";
            string cachedContext = "stable-colony-context-for-gemini-caching " + new string('x', 2100);
            string expectedSystemText = systemPrompt + "\n\n" + cachedContext;

            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, "hello")
            };
            var options = new RimLLMChatOptions
            {
                CachedContext = cachedContext
            };

            // 1. 第一次呼叫：應觸發快取建立與快取引用
            string response1 = provider.GenerateAsync(messages, options, "gemini-1.5-pro").GetAwaiter().GetResult();
            Assert.AreEqual("gemini-response", response1);
            Assert.AreEqual(1, provider.SendCalls.Count);

            // 驗證唯一的 raw 呼叫是建立快取
            var firstCall = provider.SendCalls[0];
            Assert.IsTrue(firstCall.url.Contains("cachedContents"));
            var firstPayload = Newtonsoft.Json.Linq.JObject.Parse(firstCall.payload);
            Assert.AreEqual("models/gemini-1.5-pro", firstPayload["model"]?.ToString());
            Assert.AreEqual(expectedSystemText, firstPayload["systemInstruction"]?["parts"]?[0]?["text"]?.ToString());

            // 驗證 SDK seam 收到 cachedContent 且未附帶 systemInstruction
            Assert.AreEqual("cachedContents/mock-cache-id", provider.LastConfig.CachedContent);
            Assert.IsNull(provider.LastConfig.SystemInstruction);

            // 2. 第二次呼叫：快取已存在，應直接引用而不重複建立快取
            provider.SendCalls.Clear();
            string response2 = provider.GenerateAsync(messages, options, "gemini-1.5-pro").GetAwaiter().GetResult();
            Assert.AreEqual("gemini-response", response2);
            Assert.AreEqual(0, provider.SendCalls.Count);
            Assert.AreEqual("cachedContents/mock-cache-id", provider.LastConfig.CachedContent);
            Assert.IsNull(provider.LastConfig.SystemInstruction);
        }

        [Test]
        public void TestGeminiSkipsExplicitCacheWhenContextTooSmall()
        {
            // 內容過小時建立顯式快取不划算（建立費 + 儲存費 > 節省），應略過快取改走一般 systemInstruction。
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["Gemini"] = "mock-key";

            var provider = new TestGeminiProvider(mockSettings);
            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, "small-system"),
                new ChatMessage(ChatRole.User, "hello")
            };
            var options = new RimLLMChatOptions
            {
                CachedContext = "tiny-context"
            };

            string response = provider.GenerateAsync(messages, options, "gemini-2.5-flash").GetAwaiter().GetResult();
            Assert.AreEqual("gemini-response", response);

            // 不應有任何建立快取的呼叫
            Assert.AreEqual(0, provider.SendCalls.Count);

            // SDK seam 未附 cachedContent，改以 systemInstruction 承載
            Assert.IsNull(provider.LastConfig.CachedContent);
            Assert.IsNotNull(provider.LastConfig.SystemInstruction);
            Assert.AreEqual(
                "small-system\n\ntiny-context",
                provider.LastConfig.SystemInstruction.Parts?[0]?.Text);
        }

        [Test]
        public void TestGeminiConnectionTestUsesGemini35Flash()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["Gemini"] = "mock-key";

            var provider = new TestGeminiProvider(mockSettings);
            TestResult result = provider.TestConnectionAsync().GetAwaiter().GetResult();

            Assert.IsTrue(result.Success);
            Assert.AreEqual("gemini-3.5-flash", result.Model);
            Assert.AreEqual("gemini-3.5-flash", provider.LastModel);
            Assert.AreEqual(0, provider.SendCalls.Count);
        }

        [Test]
        public void TestOpenRouterConnectionTestUsesFreeRouter()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["OpenRouter"] = "mock-key";

            var provider = new TestOpenRouterProvider(mockSettings);
            TestResult result = provider.TestConnectionAsync().GetAwaiter().GetResult();

            Assert.IsTrue(result.Success);
            Assert.AreEqual("openrouter/free", result.Model);
            Assert.IsNotNull(provider.InterceptedPayload);
            var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
            Assert.AreEqual("openrouter/free", payload["model"]?.ToString());
        }

        [Test]
        public void TestZaiConnectionTestUsesFreeFlashModel()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys[ProviderIds.Zai] = "mock-key";

            var provider = new TestZaiProvider(mockSettings);
            TestResult result = provider.TestConnectionAsync().GetAwaiter().GetResult();

            Assert.IsTrue(result.Success);
            Assert.AreEqual("glm-4.5-flash", result.Model);
            Assert.AreEqual("https://api.z.ai/api/paas/v4/chat/completions", provider.InterceptedUrl);
            Assert.IsNotNull(provider.InterceptedPayload);
            var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
            Assert.AreEqual("glm-4.5-flash", payload["model"]?.ToString());
        }

        [Test]
        public void TestManagerRegistersZaiProvider()
        {
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);

            Assert.Contains(ProviderIds.Zai, manager.GetRegisteredProviderIds());
        }

        [Test]
        public void TestReasoningEffortPayloads()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["OpenAI"] = "mock-key";
            mockSettings.ApiKeys["Gemini"] = "mock-key";
            mockSettings.ApiKeys["OpenRouter"] = "mock-key";

            var userMsgs = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") };

            // 1. OpenAI: o1 Model with ReasoningEffort.Medium
            {
                var provider = new TestOpenAIProvider(mockSettings);
                var options = new ChatOptions
                {
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium },
                    MaxOutputTokens = 1500
                };
                string response = provider.GenerateAsync(userMsgs, options, "o1-mini").GetAwaiter().GetResult();
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
                var options = new ChatOptions
                {
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium },
                    Temperature = 0.7f,
                    MaxOutputTokens = 1000
                };
                string response = provider.GenerateAsync(userMsgs, options, "gpt-4o").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.InterceptedPayload);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNull(payload["reasoning_effort"]);
                Assert.AreEqual(0.7f, (float)payload["temperature"]);
                Assert.AreEqual(1000, (int)payload["max_tokens"]);
                Assert.IsNull(payload["max_completion_tokens"]);
            }

            // 4. Gemini: Gemini with ReasoningEffort.Low
            {
                var provider = new TestGeminiProvider(mockSettings);
                var options = new ChatOptions
                {
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
                    MaxOutputTokens = 2000
                };
                string response = provider.GenerateAsync(userMsgs, options, "gemini-2.0-flash-thinking-exp").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.LastConfig.ThinkingConfig);
                Assert.AreEqual(1024, provider.LastConfig.ThinkingConfig.ThinkingBudget);
            }

            // 4b. Gemini: Gemini 1.5 Pro (non-thinking model) with ReasoningEffort.Low (should NOT include thinkingConfig)
            {
                var provider = new TestGeminiProvider(mockSettings);
                var options = new ChatOptions
                {
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
                    MaxOutputTokens = 2000
                };
                string response = provider.GenerateAsync(userMsgs, options, "gemini-1.5-pro").GetAwaiter().GetResult();
                Assert.IsNull(provider.LastConfig.ThinkingConfig);
            }

            // 4c. Gemini: Gemma 4 (thinking-level model) with ReasoningEffort.Medium (should include thinkingLevel)
            {
                var provider = new TestGeminiProvider(mockSettings);
                var options = new ChatOptions
                {
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium },
                    MaxOutputTokens = 2000
                };
                string response = provider.GenerateAsync(userMsgs, options, "gemma-4-it-b-t").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.LastConfig.ThinkingConfig);
                Assert.AreEqual(Google.GenAI.Types.ThinkingLevel.Medium, provider.LastConfig.ThinkingConfig.ThinkingLevel);
                Assert.IsNull(provider.LastConfig.ThinkingConfig.ThinkingBudget);
            }

            // 5. OpenRouter: DeepSeek R1 with ReasoningEffort.Medium
            {
                var provider = new TestOpenRouterProvider(mockSettings);
                var options = new ChatOptions
                {
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium }
                };
                string response = provider.GenerateAsync(userMsgs, options, "deepseek/deepseek-r1").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.InterceptedPayload);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.AreEqual(2048, (int)payload["max_thinking_tokens"]);
            }

            // 6. Test ReasoningEffort? = null (Auto) and DisableReasoning = true (None) payloads

            // 6a. OpenAI Auto
            {
                var provider = new TestOpenAIProvider(mockSettings);
                var options = new ChatOptions(); // Reasoning null
                string response = provider.GenerateAsync(userMsgs, options, "o1-mini").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.InterceptedPayload);
                var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
                Assert.IsNull(payload["reasoning_effort"]);
            }

            // 6b. Gemini 2.0 Auto -> thinkingBudget = -1
            {
                var provider = new TestGeminiProvider(mockSettings);
                var options = new ChatOptions(); // Reasoning null
                string response = provider.GenerateAsync(userMsgs, options, "gemini-2.0-flash-thinking-exp").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.LastConfig.ThinkingConfig);
                Assert.AreEqual(-1, provider.LastConfig.ThinkingConfig.ThinkingBudget);
            }

            // 6c. Gemini 2.0 None -> thinkingBudget = 0
            {
                var provider = new TestGeminiProvider(mockSettings);
                var options = new RimLLMChatOptions { DisableReasoning = true };
                string response = provider.GenerateAsync(userMsgs, options, "gemini-2.0-flash-thinking-exp").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.LastConfig.ThinkingConfig);
                Assert.AreEqual(0, provider.LastConfig.ThinkingConfig.ThinkingBudget);
            }

            // 6d. Gemma 4 Auto -> Omit thinkingLevel
            {
                var provider = new TestGeminiProvider(mockSettings);
                var options = new ChatOptions();
                string response = provider.GenerateAsync(userMsgs, options, "gemma-4-it-b-t").GetAwaiter().GetResult();
                Assert.IsNull(provider.LastConfig.ThinkingConfig);
            }

            // 6e. Gemma 4 None -> thinkingLevel = "minimal"
            {
                var provider = new TestGeminiProvider(mockSettings);
                var options = new RimLLMChatOptions { DisableReasoning = true };
                string response = provider.GenerateAsync(userMsgs, options, "gemma-4-it-b-t").GetAwaiter().GetResult();
                Assert.IsNotNull(provider.LastConfig.ThinkingConfig);
                Assert.AreEqual(Google.GenAI.Types.ThinkingLevel.Minimal, provider.LastConfig.ThinkingConfig.ThinkingLevel);
            }

            // 6i. OpenRouter Auto -> Omit max_thinking_tokens
            {
                var provider = new TestOpenRouterProvider(mockSettings);
                var options = new ChatOptions();
                string response = provider.GenerateAsync(userMsgs, options, "deepseek/deepseek-r1").GetAwaiter().GetResult();
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
                StreamHandler = (msgs, opts, model, onChunk) =>
                {
                    onChunk("Hello ");
                    onChunk("World");
                    onChunk("!");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            };
            manager.RegisterProvider(mockStream);

            const string modId = "test.stream.unified.id";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "test prompt") };
            var enumerator = client.GetStreamingResponseAsync(messages).GetAsyncEnumerator();
            string result = "";
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    if (!string.IsNullOrEmpty(enumerator.Current.Text))
                    {
                        result += enumerator.Current.Text;
                        chunksReceived.Add(enumerator.Current.Text);
                    }
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

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

            var userMsgs = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") };

            // 1. 測試 OpenAIProvider (DeepSeek-R1 格式 reasoning_content)
            {
                var provider = new TestOpenAIProviderWithReasoning(mockSettings);
                provider.WireHandler.ResponseBody = "{" +
                    "\"choices\": [{\"message\": {\"role\": \"assistant\", \"content\": \"Hello, user!\", " +
                    "\"reasoning_content\": \"Assessing the situation...\"}}]}";
                string result = provider.GenerateAsync(userMsgs, null, "deepseek-reasoning").GetAwaiter().GetResult();
                Assert.IsTrue(result.Contains("<think>"));
                Assert.IsTrue(result.Contains("</think>"));
                Assert.IsTrue(result.Contains("Assessing the situation..."));
                Assert.IsTrue(result.Contains("Hello, user!"));
            }

            // 2. 測試 GeminiProvider (thought: true 欄位)
            {
                var provider = new TestGeminiProviderWithReasoning(mockSettings);
                string result = provider.GenerateAsync(userMsgs, null, "gemini-thinking").GetAwaiter().GetResult();
                Assert.IsTrue(result.Contains("<think>"));
                Assert.IsTrue(result.Contains("</think>"));
                Assert.IsTrue(result.Contains("Thinking deeply..."));
                Assert.IsTrue(result.Contains("Response from Gemini"));
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
                GenerateHandler = (msgs, opts, model) =>
                {
                    calledModels.Add(model);
                    return System.Threading.Tasks.Task.FromResult("success");
                }
            });

            const string modId = "mod.level.override";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var userMsgs = new List<ChatMessage> { new ChatMessage(ChatRole.User, "p") };
            var options = new RimLLMChatOptions { MinFallbackLevel = "High" };
            string res = client.GetResponseAsync(userMsgs, options).GetAwaiter().GetResult().Text;

            // 覆寫生效：model-mini 被視為 High，不再被 MinFallbackLevel 過濾
            Assert.AreEqual("success", res);
            Assert.AreEqual(1, calledModels.Count);
            Assert.AreEqual("model-mini", calledModels[0]);
        }

        [Test]
        public void TestBudgetReset()
        {
            var mockSettings = new MockSettings();
            var tracker = new RimLLMUsageTracker(mockSettings);

            // 設置非今日重置日期
            mockSettings.DailyBudgetResetDate = "2026-01-01";
            mockSettings.DailyAccumulatedCost = 5.5f;

            // 觸發重置
            tracker.CheckDailyReset();

            string todayStr = DateTime.Today.ToString("yyyy-MM-dd");
            Assert.AreEqual(todayStr, mockSettings.DailyBudgetResetDate);
            Assert.AreEqual(0f, mockSettings.DailyAccumulatedCost);
        }

        [Test]
        public void TestThrottlingAntiAbuse()
        {
            var mockSettings = new MockSettings
            {
                EnableAntiAbuse = true,
                MaxRequestsPerWindow = 3,
                ThrottlingWindowSeconds = 5,
                CoolDownDurationSeconds = 10,
                FallbackChain = new List<string> { "MockSuccess:model-z" }
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("ok")
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.abuse.mod";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") };

            // 呼叫 3 次應該都成功
            Assert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);
            Assert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);
            Assert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);

            // 第 4 次呼叫超出頻率限制，預期觸發 RateLimit 錯誤
            var ex = Assert.Throws<RimLLMException>(() =>
            {
                client.GetResponseAsync(messages).GetAwaiter().GetResult();
            });
            Assert.AreEqual(LLMError.RateLimit, ex.Error);
        }

        [Test]
        public void TestBudgetPolicyHardBlock()
        {
            var mockSettings = new MockSettings
            {
                DailyBudgetLimit = 1.0f,
                DailyAccumulatedCost = 1.2f,
                DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd"),
                BudgetPolicy = 0, // HardBlock
                FallbackChain = new List<string> { "MockSuccess:model-z" }
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("ok")
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.budget.block.mod";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") };

            var ex = Assert.Throws<RimLLMException>(() =>
            {
                client.GetResponseAsync(messages).GetAwaiter().GetResult();
            });
            Assert.AreEqual(LLMError.QuotaExceeded, ex.Error);
        }

        [Test]
        public void TestBudgetPolicySilentMocking()
        {
            var mockSettings = new MockSettings
            {
                DailyBudgetLimit = 1.0f,
                DailyAccumulatedCost = 1.2f,
                DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd"),
                BudgetPolicy = 1, // SilentMocking
                FallbackChain = new List<string> { "MockSuccess:model-z" }
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("ok")
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.budget.mock.mod";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            // 1. 一般文字請求
            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") };
            string resText = client.GetResponseAsync(messages).GetAwaiter().GetResult().Text;
            Assert.IsTrue(resText.Contains("沉思") || resText.Contains("resting") || resText.Contains("thinking") || resText.Contains("REST"));

            // 2. 結構化輸出請求，預期回傳空 JSON "{}"
            var resObj = client.GetResponseObjectAsync<TestDataStructure>(messages).GetAwaiter().GetResult();
            Assert.IsNotNull(resObj);
            Assert.AreEqual(100, resObj.Value);
            Assert.AreEqual("default", resObj.Message);
        }

        [Test]
        public void TestJsonSchemaGenerator()
        {
            // test lowercase (OpenAI style)
            var openaiSchema = RimLLMJsonHelper.GenerateJsonSchema(typeof(TestDataStructure), uppercaseTypes: false);
            Assert.AreEqual("object", openaiSchema["type"]?.ToString());
            Assert.IsNotNull(openaiSchema["properties"]);
            Assert.AreEqual("integer", openaiSchema["properties"]?["Value"]?["type"]?.ToString());
            Assert.AreEqual("string", openaiSchema["properties"]?["Message"]?["type"]?.ToString());
            Assert.IsFalse((bool)openaiSchema["additionalProperties"]);

            // test uppercase (Gemini style)
            var geminiSchema = RimLLMJsonHelper.GenerateJsonSchema(typeof(TestDataStructure), uppercaseTypes: true);
            Assert.AreEqual("OBJECT", geminiSchema["type"]?.ToString());
            Assert.AreEqual("INTEGER", geminiSchema["properties"]?["Value"]?["type"]?.ToString());
            Assert.AreEqual("STRING", geminiSchema["properties"]?["Message"]?["type"]?.ToString());
        }

        [Test]
        public void TestJsonSchemaRecursiveTypeDoesNotStackOverflow()
        {
            // NestedData.SelfRef 指回 ComplexTestDataStructure，形成循環。
            var schema = RimLLMJsonHelper.GenerateJsonSchema(typeof(ComplexTestDataStructure));

            Assert.IsNotNull(schema, "循環型別仍應產生可用的 schema，不得遞迴爆棧");

            var nested = schema["properties"]?["Nested"];
            Assert.IsNotNull(nested, "非循環的巢狀成員應正常展開");
            Assert.AreEqual("number", nested["properties"]?["Weight"]?["type"]?.ToString());

            // 循環成員應被略過，且不得列入 required。
            Assert.IsNull(nested["properties"]?["SelfRef"], "循環引用成員應於偵測後截斷，不得展開");
            var nestedRequired = (JArray)nested["required"];
            foreach (var item in nestedRequired)
            {
                Assert.AreNotEqual("SelfRef", item.ToString(), "被截斷的循環成員不得列入 required");
            }
        }

        [Test]
        public void TestJsonSchemaDictionaryBecomesOpenMap()
        {
            var schema = RimLLMJsonHelper.GenerateJsonSchema(typeof(ComplexTestDataStructure));
            var mapping = schema["properties"]?["Mapping"];

            Assert.IsNotNull(mapping, "Dictionary 成員應出現在 schema 中");
            Assert.AreEqual("object", mapping["type"]?.ToString());
            Assert.AreEqual("integer", mapping["additionalProperties"]?["type"]?.ToString(),
                "Dictionary 應產生開放式 map schema 而非空物件");
            Assert.IsNull(mapping["properties"], "開放式 map 不應帶有固定的 properties 清單");

            Assert.IsTrue(RimLLMJsonHelper.ContainsOpenEndedMap(typeof(ComplexTestDataStructure)),
                "含 Dictionary 的型別必須被偵測為開放式 map，以便關閉 strict 模式");
            Assert.IsFalse(RimLLMJsonHelper.ContainsOpenEndedMap(typeof(TestDataStructure)),
                "不含 Dictionary 的型別不應被誤判為開放式 map");
        }

        [Test]
        public void TestJsonSchemaNullableUnwrapsUnderlyingType()
        {
            var schema = RimLLMJsonHelper.GenerateJsonSchema(typeof(NullableTestDataStructure));

            Assert.AreEqual("integer", schema["properties"]?["OptionalCount"]?["type"]?.ToString(),
                "Nullable<int> 應解包為底層型別 integer");

            var required = (JArray)schema["required"];
            var requiredNames = new List<string>();
            foreach (var item in required) requiredNames.Add(item.ToString());

            Assert.IsTrue(requiredNames.Contains("Name"), "非 Nullable 成員應列入 required");
            Assert.IsFalse(requiredNames.Contains("OptionalCount"), "Nullable 成員屬選填，不應列入 required");
        }

        [Test]
        public void TestJsonSchemaGeneratorCacheReturnsIndependentInstances()
        {
            var first = RimLLMJsonHelper.GenerateJsonSchema(typeof(TestDataStructure));
            first["type"] = "polluted";
            ((JObject)first["properties"]).Remove("Value");

            var second = RimLLMJsonHelper.GenerateJsonSchema(typeof(TestDataStructure));

            Assert.AreEqual("object", second["type"]?.ToString(),
                "schema 快取必須回傳深拷貝，避免呼叫端汙染");
            Assert.IsNotNull(second["properties"]?["Value"], "快取內容不得被前一次呼叫端的修改影響");
        }

        [Test]
        public void TestRepairJsonClosesInterleavedBracketsInOrder()
        {
            // 陣列在物件內：必須先補 ] 再補 }
            string repairedArrayInObject = RimLLMJsonHelper.RepairJson("{\"items\":[1,2");
            Assert.AreEqual("{\"items\":[1,2]}", repairedArrayInObject, "巢狀括號必須依 LIFO 順序閉合");
            Assert.DoesNotThrow(() => JObject.Parse(repairedArrayInObject));

            // 物件在陣列內：必須先補 } 再補 ]
            string repairedObjectInArray = RimLLMJsonHelper.RepairJson("[{\"a\":1");
            Assert.AreEqual("[{\"a\":1}]", repairedObjectInArray, "巢狀括號必須依 LIFO 順序閉合");
            Assert.DoesNotThrow(() => JArray.Parse(repairedObjectInArray));

            // 多層交錯
            string repairedMixed = RimLLMJsonHelper.RepairJson("{\"a\":[{\"b\":[1");
            Assert.DoesNotThrow(() => JObject.Parse(repairedMixed), "多層交錯巢狀修復後必須可解析");
        }

        [Test]
        public void TestRepairJsonClosesDanglingString()
        {
            string repaired = RimLLMJsonHelper.RepairJson("{\"message\":\"unterminated");
            Assert.DoesNotThrow(() => JObject.Parse(repaired), "未閉合的字串必須先補上引號，補的括號才不會落在字串內部");
            Assert.AreEqual("unterminated", JObject.Parse(repaired)["message"]?.ToString());
        }

        [Test]
        public void TestRepairJsonTrimsDanglingTokens()
        {
            // 截斷在鍵之後
            string afterColon = RimLLMJsonHelper.RepairJson("{\"a\":1,\"b\":");
            Assert.DoesNotThrow(() => JObject.Parse(afterColon), "截斷於冒號後應補 null 使其可解析");

            // 截斷在逗號之後
            string afterComma = RimLLMJsonHelper.RepairJson("{\"a\":1,");
            Assert.DoesNotThrow(() => JObject.Parse(afterComma), "截斷於逗號後應移除懸空逗號");
        }

        [Test]
        public void TestRepairJsonBailsOutOnMismatchedBrackets()
        {
            // 閉合符號與開啟順序不符，代表結構已損毀，不應嘗試補齊而讓結果更糟。
            const string broken = "{\"a\":]";
            string repaired = RimLLMJsonHelper.RepairJson(broken);
            Assert.AreEqual(broken, repaired, "括號順序不符時應放棄補齊，交由後續 fallback 處理");
        }

        [Test]
        public void TestSmartRoutingMinLatency()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockSlow:model-s", "MockFast:model-f" },
                RoutingStrategy = 1, // MinLatency
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockSlow"] = true;
            mockSettings.EnabledProviders["MockFast"] = true;
            mockSettings.ApiKeys["MockSlow"] = "key-s";
            mockSettings.ApiKeys["MockFast"] = "key-f";

            var manager = new RimLLMManager(mockSettings);

            var mockSlow = new MockTestProvider
            {
                ProviderId = "MockSlow",
                GenerateHandler = async (msgs, opts, model) =>
                {
                    await System.Threading.Tasks.Task.Delay(100);
                    return "slow-ok";
                }
            };
            var mockFast = new MockTestProvider
            {
                ProviderId = "MockFast",
                GenerateHandler = async (msgs, opts, model) =>
                {
                    await System.Threading.Tasks.Task.Delay(5);
                    return "fast-ok";
                }
            };

            manager.RegisterProvider(mockSlow);
            manager.RegisterProvider(mockFast);

            const string modId = "test.routing.latency";
            ClientRegistry.RegisterClient(modId, Assembly.GetExecutingAssembly());

            var request = new LLMRequest { ModId = modId, Prompt = "hello" };

            // 第一次呼叫：兩個都沒有延遲歷史，依據 FallbackChain 順序（先 MockSlow）
            string res1 = manager.GenerateAsync(request).GetAwaiter().GetResult();
            Assert.AreEqual("slow-ok", res1);

            // 第二次呼叫：因為 MockSlow 已有延遲（100ms），MockFast 尚未有歷史（視為 0 延遲），優先呼叫 MockFast
            string res2 = manager.GenerateAsync(request).GetAwaiter().GetResult();
            Assert.AreEqual("fast-ok", res2);

            // 第三次呼叫：此時 MockSlow 平均 100ms，MockFast 平均 5ms，智慧路由應該優先選擇 MockFast
            string res3 = manager.GenerateAsync(request).GetAwaiter().GetResult();
            Assert.AreEqual("fast-ok", res3);
        }

        [Test]
        public void TestSmartRoutingPriorityFailover()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockFail:model-x", "MockSuccess:model-y" },
                RoutingStrategy = 0, // PriorityFailover
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockFail"] = true;
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockFail"] = "key-x";
            mockSettings.ApiKeys["MockSuccess"] = "key-y";

            var manager = new RimLLMManager(mockSettings);

            int failCalls = 0;
            int successCalls = 0;

            var mockFail = new MockTestProvider
            {
                ProviderId = "MockFail",
                GenerateHandler = (msgs, opts, model) =>
                {
                    failCalls++;
                    throw new Exception("Simulated fail");
                }
            };
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) =>
                {
                    successCalls++;
                    return System.Threading.Tasks.Task.FromResult("success-ok");
                }
            };

            manager.RegisterProvider(mockFail);
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.routing.failover";
            ClientRegistry.RegisterClient(modId, Assembly.GetExecutingAssembly());

            var request = new LLMRequest { ModId = modId, Prompt = "hello" };

            // 第一次呼叫：MockFail 失敗，然後 Fallback 到 MockSuccess 成功
            string res1 = manager.GenerateAsync(request).GetAwaiter().GetResult();
            Assert.AreEqual("success-ok", res1);
            Assert.AreEqual(1, failCalls);
            Assert.AreEqual(1, successCalls);

            // 第二次呼叫：MockFail 此時正處於 60 秒的故障冷卻期，智慧路由應直接跳過它，不進行呼叫，直接執行 MockSuccess
            string res2 = manager.GenerateAsync(request).GetAwaiter().GetResult();
            Assert.AreEqual("success-ok", res2);
            Assert.AreEqual(1, failCalls); // 呼叫次數仍為 1，說明已被跳過！
            Assert.AreEqual(2, successCalls);
        }

        [Test]
        public void TestJsonRepairSettings()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockJSON:model-j" },
                EnableJsonRepair = false, // 禁用 JSON 修復
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockJSON"] = true;
            mockSettings.ApiKeys["MockJSON"] = "key-j";

            var manager = new RimLLMManager(mockSettings);
            var mockJSON = new MockTestProvider
            {
                ProviderId = "MockJSON",
                GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("```json\n{ \"Value\": 42, \"Message\": \"ok\", }\n```") // 帶有 markdown 與尾隨逗號的不合法 JSON
            };
            manager.RegisterProvider(mockJSON);

            const string modId = "test.json.repair.settings";
            ClientRegistry.RegisterClient(modId, Assembly.GetExecutingAssembly());

            var request = new LLMRequest { ModId = modId, Prompt = "hello" };

            // 1. 當 EnableJsonRepair 為 false 時，預期拋出例外
            Assert.Throws<RimLLMException>(() =>
            {
                manager.GenerateObjectAsync<TestDataStructure>(request).GetAwaiter().GetResult();
            });

            // 2. 當 EnableJsonRepair 為 true 時，預期成功修復並解析
            mockSettings.EnableJsonRepair = true;
            var res = manager.GenerateObjectAsync<TestDataStructure>(request).GetAwaiter().GetResult();
            Assert.IsNotNull(res);
            Assert.AreEqual(42, res.Value);
            Assert.AreEqual(okStr(res.Message), "ok");
        }

        [Test]
        public void TestTrigramSimilarityOnEmbeddingService()
        {
            // 1. 相同字串相似度為 1.0
            float simSelf = RimLLMEmbeddingService.CalculateTrigramSimilarity("Colony status is good", "Colony status is good");
            Assert.AreEqual(1.0f, simSelf, 0.001f, "完全相同的字串相似度必須為 1.0");

            // 2. 完全無關字串相似度接近 0.0
            float simDiff = RimLLMEmbeddingService.CalculateTrigramSimilarity("Colony status is good", "Starve event happened");
            Assert.IsTrue(simDiff < 0.2f, "無關字串的相似度應該偏低");

            // 3. 相似字串相似度較高
            float simClose = RimLLMEmbeddingService.CalculateTrigramSimilarity("We have 10 colonists", "We have 11 colonists");
            Assert.IsTrue(simClose > 0.75f, "僅有些微差異的字串相似度應該偏高");
        }

        [Test]
        public void TestCosineSimilarityHandlesMismatchedLength()
        {
            float[] a = new float[] { 1f, 0f, 0f };
            float[] b = new float[] { 1f, 0f };

            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateCosineSimilarity(a, b), 0.0001f, "長度不一致的向量應回傳 0 而非拋出例外");
            Assert.AreEqual(0f, RimLLMEmbeddingService.CalculateCosineSimilarity(a, null), 0.0001f, "任一向量為 null 時應回傳 0");
            Assert.AreEqual(1f, RimLLMEmbeddingService.CalculateCosineSimilarity(a, a), 0.0001f, "相同向量的餘弦相似度必須為 1");
        }

        [Test]
        public void TestEmbeddingServiceRejectsOfflineProvider()
        {
            var mockSettings = new MockSettings { EmbeddingProvider = "Offline_Trigram" };
            var service = new RimLLMEmbeddingService(mockSettings);

            var ex = Assert.Throws<RimLLMException>(() =>
                service.ComputeEmbeddingAsync("hello").GetAwaiter().GetResult());
            Assert.IsTrue(ex.Message.Contains("Trigram"), "離線模式不支援向量運算，應明確拋出錯誤");
        }

        [Test]
        public void TestEmbeddingRequestVerifiesCaller()
        {
            var mockSettings = new MockSettings { EmbeddingProvider = "Google" };
            var manager = new RimLLMManager(mockSettings);
            RimLLMProvider.Initialize(manager);

            // 未透過 ClientRegistry 註冊的 ModId 不得取用計費的 Embedding API。
            Assert.Throws<RimLLMException>(() =>
                RimLLMProvider.CreateEmbeddingGenerator("unregistered.mod.id"),
                "Embedding 為計費 API，必須通過呼叫端身分校驗");
        }

        [Test]
        public void TestHttpStatusCodeMapping()
        {
            var probe = new HttpErrorProbeProvider(new MockSettings());

            var notFound = Assert.Throws<RimLLMException>(() =>
                probe.Probe(System.Net.HttpStatusCode.NotFound, "{\"error\":{\"message\":\"model does not exist\"}}"));
            Assert.AreEqual(LLMError.ModelNotFound, notFound.Error, "404 應對應 ModelNotFound 而非可重試的 Unknown");

            var badRequest = Assert.Throws<RimLLMException>(() =>
                probe.Probe(System.Net.HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"bad input\"}}"));
            Assert.AreEqual(LLMError.InvalidResponse, badRequest.Error, "400 屬於請求本身的問題，應對應 InvalidResponse");
            Assert.IsFalse(badRequest.IsSchemaRejection, "與 schema 無關的 400 不應標記為 schema 拒絕");

            var timeout = Assert.Throws<RimLLMException>(() =>
                probe.Probe(System.Net.HttpStatusCode.RequestTimeout, "timeout"));
            Assert.AreEqual(LLMError.Timeout, timeout.Error, "408 應對應 Timeout");

            var paymentRequired = Assert.Throws<RimLLMException>(() =>
                probe.Probe((System.Net.HttpStatusCode)402, "payment required"));
            Assert.AreEqual(LLMError.QuotaExceeded, paymentRequired.Error, "402 應對應 QuotaExceeded");
        }

        [Test]
        public void TestSchemaRejectionIsMarkedOnRelevant400()
        {
            var probe = new HttpErrorProbeProvider(new MockSettings());

            var ex = Assert.Throws<RimLLMException>(() =>
                probe.Probe(System.Net.HttpStatusCode.BadRequest,
                    "{\"error\":{\"message\":\"response_format json_schema is not supported\"}}"));

            Assert.AreEqual(LLMError.InvalidResponse, ex.Error);
            Assert.IsTrue(ex.IsSchemaRejection, "提及 response_format／json_schema 的 400 應標記為 schema 拒絕");
        }

        [Test]
        public void TestQuotaDetectionIsCaseInsensitive()
        {
            var probe = new HttpErrorProbeProvider(new MockSettings());

            var ex = Assert.Throws<RimLLMException>(() =>
                probe.Probe((System.Net.HttpStatusCode)429, "Insufficient_Quota for this account"));

            Assert.AreEqual(LLMError.QuotaExceeded, ex.Error, "配額關鍵字比對必須大小寫不敏感");
        }

        [Test]
        public void TestModelNotFoundDoesNotConsumeRetries()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockNotFound:model-a" },
                MaxRetries = 3,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockNotFound"] = true;
            mockSettings.ApiKeys["MockNotFound"] = "key";

            var manager = new RimLLMManager(mockSettings);
            int calls = 0;
            manager.RegisterProvider(new MockTestProvider
            {
                ProviderId = "MockNotFound",
                GenerateHandler = (msgs, opts, model) =>
                {
                    calls++;
                    throw new RimLLMException(LLMError.ModelNotFound, "Model or endpoint not found");
                }
            });

            const string modId = "test.modelnotfound";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };
            Assert.Throws<RimLLMException>(() =>
                client.GetResponseAsync(messages).GetAwaiter().GetResult());
            Assert.AreEqual(1, calls, "404 屬於不可重試錯誤，不應消耗重試次數");
        }

        [Test]
        public void TestEmptyStreamErrorIsRetryable()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockEmptyStream:model-a", "MockGoodStream:model-b" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockEmptyStream"] = true;
            mockSettings.EnabledProviders["MockGoodStream"] = true;
            mockSettings.ApiKeys["MockEmptyStream"] = "key";
            mockSettings.ApiKeys["MockGoodStream"] = "key";

            var manager = new RimLLMManager(mockSettings);

            // 模擬 provider 偵測到零輸出後擲出的錯誤（與 OpenAI／Gemini 串流實作一致，
            // 刻意使用可重試的 NetworkError，讓 fallback 能接手）。
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "MockEmptyStream",
                StreamHandler = (msgs, opts, model, onChunk) =>
                    throw new RimLLMException(LLMError.NetworkError, "串流未回傳任何內容。")
            });
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "MockGoodStream",
                StreamHandler = (msgs, opts, model, onChunk) =>
                {
                    onChunk("recovered");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            });

            const string modId = "test.emptystream.fallback";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var received = new List<string>();
            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };
            var enumerator = client.GetStreamingResponseAsync(messages).GetAsyncEnumerator();
            string result = "";
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    if (!string.IsNullOrEmpty(enumerator.Current.Text))
                    {
                        result += enumerator.Current.Text;
                        received.Add(enumerator.Current.Text);
                    }
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            Assert.AreEqual("recovered", result, "零輸出串流應視為可重試失敗並由下一個供應商接手");
            CollectionAssert.Contains(received, "recovered");
        }

        [Test]
        public void TestDerivedProviderDoesNotSendNativeSchemaPayload()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "Kimi:moonshot-v1-8k" },
                EnableNativeSchema = true
            };
            mockSettings.EnabledProviders["Kimi"] = true;
            mockSettings.ApiKeys["Kimi"] = "key";

            var manager = new RimLLMManager(mockSettings);
            var provider = new TestKimiPayloadProvider(mockSettings);

            const string modId = "test.kimi.payload";
            ClientRegistry.RegisterClient(modId, Assembly.GetExecutingAssembly());

            provider.GenerateAsync(
                new LLMRequest { ModId = modId, Prompt = "hi", ResponseType = typeof(TestDataStructure) },
                "moonshot-v1-8k").GetAwaiter().GetResult();

            var payload = JObject.Parse(provider.CapturedPayload);
            Assert.IsNull(payload["response_format"],
                "未宣告支援原生 schema 的衍生供應商不應收到 response_format");
        }

        [Test]
        public void TestWhitelistedDerivedProviderSendsNativeSchemaPayload()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "DeepSeek:deepseek-chat" },
                EnableNativeSchema = true
            };
            mockSettings.EnabledProviders["DeepSeek"] = true;
            mockSettings.ApiKeys["DeepSeek"] = "key";

            var provider = new TestDeepSeekPayloadProvider(mockSettings);

            const string modId = "test.deepseek.payload";
            ClientRegistry.RegisterClient(modId, Assembly.GetExecutingAssembly());

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };
            var options = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary() };
            options.AdditionalProperties["rimllm_response_schema"] = RimLLMJsonHelper.GenerateJsonSchema(typeof(TestDataStructure), uppercaseTypes: false).ToString();
            options.AdditionalProperties["strict"] = !RimLLMJsonHelper.ContainsOpenEndedMap(typeof(TestDataStructure));
            provider.GenerateStructuredAsync(messages, options, "deepseek-chat").GetAwaiter().GetResult();

            var payload = JObject.Parse(provider.CapturedPayload);
            Assert.IsNotNull(payload["response_format"], "已驗證支援的衍生供應商應收到 response_format");
            Assert.AreEqual("custom_type", payload["response_format"]?["json_schema"]?["name"]?.ToString());
            Assert.AreEqual(true, payload["response_format"]?["json_schema"]?["strict"]?.Value<bool>(),
                "不含 Dictionary 的型別應維持 strict 模式");
        }

        [Test]
        public void TestDictionaryTypeDisablesStrictMode()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "DeepSeek:deepseek-chat" },
                EnableNativeSchema = true
            };
            mockSettings.EnabledProviders["DeepSeek"] = true;
            mockSettings.ApiKeys["DeepSeek"] = "key";

            var provider = new TestDeepSeekPayloadProvider(mockSettings);

            const string modId = "test.deepseek.strict";
            ClientRegistry.RegisterClient(modId, Assembly.GetExecutingAssembly());

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };
            var options = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary() };
            options.AdditionalProperties["rimllm_response_schema"] = RimLLMJsonHelper.GenerateJsonSchema(typeof(ComplexTestDataStructure), uppercaseTypes: false).ToString();
            options.AdditionalProperties["strict"] = !RimLLMJsonHelper.ContainsOpenEndedMap(typeof(ComplexTestDataStructure));
            provider.GenerateStructuredAsync(messages, options, "deepseek-chat").GetAwaiter().GetResult();

            var payload = JObject.Parse(provider.CapturedPayload);
            Assert.AreEqual(false, payload["response_format"]?["json_schema"]?["strict"]?.Value<bool>(),
                "含 Dictionary 的型別必須關閉 strict，否則服務端會拒絕開放式 map");
        }

        [Test]
        public void TestNativeSchemaRejectionRequiresExplicitMarker()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockInvalid:model-a" },
                MaxRetries = 0,
                RetryDelay = 0f,
                EnableNativeSchema = true,
                EnableJsonRepair = false
            };
            mockSettings.EnabledProviders["MockInvalid"] = true;
            mockSettings.ApiKeys["MockInvalid"] = "key";

            var manager = new RimLLMManager(mockSettings);
            int calls = 0;
            manager.RegisterProvider(new MockTestProvider
            {
                ProviderId = "MockInvalid",
                GenerateHandler = (msgs, opts, model) =>
                {
                    calls++;
                    // 未標記 IsSchemaRejection 的一般 InvalidResponse
                    throw new RimLLMException(LLMError.InvalidResponse, "provider returned garbage");
                }
            });

            const string modId = "test.schema.marker";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };
            Assert.Throws<RimLLMException>(() =>
                client.GetResponseObjectAsync<TestDataStructure>(messages).GetAwaiter().GetResult());

            Assert.AreEqual(1, calls, "僅標記為 schema 拒絕的錯誤才可觸發降級重打");
        }

        [Test]
        public void TestBudgetPromptWaiterRespectsCancellation()
        {
            var neverCompletes = new System.Threading.Tasks.TaskCompletionSource<bool>();
            using (var cts = new System.Threading.CancellationTokenSource())
            {
                cts.Cancel();

                Assert.Throws<OperationCanceledException>(() =>
                    RimLLMManager.AwaitBudgetApprovalAsync(
                        neverCompletes.Task, cts.Token, TimeSpan.FromSeconds(30))
                        .GetAwaiter().GetResult(),
                    "預算詢問等待必須響應請求的取消 Token");
            }
        }

        [Test]
        public void TestBudgetPromptWaiterTimesOutAsDecline()
        {
            var neverCompletes = new System.Threading.Tasks.TaskCompletionSource<bool>();

            bool approved = RimLLMManager.AwaitBudgetApprovalAsync(
                neverCompletes.Task,
                System.Threading.CancellationToken.None,
                TimeSpan.FromMilliseconds(50)).GetAwaiter().GetResult();

            Assert.IsFalse(approved, "逾時應視為拒絕而非無限期等待或拋出例外");
        }

        [Test]
        public void TestBudgetPromptWaiterCancellationDoesNotAffectOtherWaiters()
        {
            var shared = new System.Threading.Tasks.TaskCompletionSource<bool>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            using (var cancelledCts = new System.Threading.CancellationTokenSource())
            {
                // 等待者 A 會被取消
                var waiterA = RimLLMManager.AwaitBudgetApprovalAsync(
                    shared.Task, cancelledCts.Token, TimeSpan.FromSeconds(30));

                // 等待者 B 正常等待
                var waiterB = RimLLMManager.AwaitBudgetApprovalAsync(
                    shared.Task, System.Threading.CancellationToken.None, TimeSpan.FromSeconds(30));

                cancelledCts.Cancel();
                Assert.Throws<OperationCanceledException>(() => waiterA.GetAwaiter().GetResult());

                // 使用者稍後按下同意，B 仍應取得結果。
                shared.TrySetResult(true);
                Assert.IsTrue(waiterB.GetAwaiter().GetResult(),
                    "單一等待者取消不得影響其他共用同一對話框的請求");
            }
        }

        [Test]
        public void TestStreamRequestChargesAntiAbuseOnce()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockStreamOnce:model-a" },
                EnableAntiAbuse = true,
                MaxRequestsPerWindow = 1,
                ThrottlingWindowSeconds = 60
            };
            mockSettings.EnabledProviders["MockStreamOnce"] = true;
            mockSettings.ApiKeys["MockStreamOnce"] = "key";

            var manager = new RimLLMManager(mockSettings);
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "MockStreamOnce",
                StreamHandler = (msgs, opts, model, onChunk) =>
                {
                    onChunk("ok");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            });

            const string modId = "test.stream.antiabuse";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };
            var enumerator = client.GetStreamingResponseAsync(messages).GetAsyncEnumerator();
            string result = "";
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    result += enumerator.Current.Text;
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            Assert.AreEqual("ok", result, "單一請求不得重複計入防濫用額度");
        }

        [Test]
        public void TestStreamingDiscardsPartialChunksOnProviderFailure()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockPartial:model-a", "MockGood:model-b" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockPartial"] = true;
            mockSettings.EnabledProviders["MockGood"] = true;
            mockSettings.ApiKeys["MockPartial"] = "key";
            mockSettings.ApiKeys["MockGood"] = "key";

            var manager = new RimLLMManager(mockSettings);
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "MockPartial",
                StreamHandler = (msgs, opts, model, onChunk) =>
                {
                    // 先吐出部分內容再失敗
                    onChunk("AB");
                    throw new RimLLMException(LLMError.ProviderOffline, "dropped mid-stream");
                }
            });
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "MockGood",
                StreamHandler = (msgs, opts, model, onChunk) =>
                {
                    onChunk("XY");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            });

            const string modId = "test.stream.partial";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };
            string result = "";
            var options = new RimLLMChatOptions
            {
                OnStreamRestart = () => result = ""
            };
            var enumerator = client.GetStreamingResponseAsync(messages, options).GetAsyncEnumerator();
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    if (enumerator.Current.AdditionalProperties != null &&
                        enumerator.Current.AdditionalProperties.ContainsKey("rimllm_stream_restart"))
                    {
                        result = "";
                        continue;
                    }
                    if (!string.IsNullOrEmpty(enumerator.Current.Text))
                    {
                        result += enumerator.Current.Text;
                    }
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            Assert.AreEqual("XY", result, "失敗 attempt 的部分串流內容不得混入最終結果");
        }

        [Test]
        public void TestStreamingRestartCallbackFiresOnceOnFallback()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockPartial2:model-a", "MockGood2:model-b" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockPartial2"] = true;
            mockSettings.EnabledProviders["MockGood2"] = true;
            mockSettings.ApiKeys["MockPartial2"] = "key";
            mockSettings.ApiKeys["MockGood2"] = "key";

            var manager = new RimLLMManager(mockSettings);
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "MockPartial2",
                StreamHandler = (msgs, opts, model, onChunk) =>
                {
                    onChunk("partial");
                    throw new RimLLMException(LLMError.ProviderOffline, "dropped mid-stream");
                }
            });
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "MockGood2",
                StreamHandler = (msgs, opts, model, onChunk) =>
                {
                    onChunk("final");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            });

            const string modId = "test.stream.restart";
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient(modId);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            int restartCount = 0;
            var displayed = new System.Text.StringBuilder();

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };
            string result = "";
            var options = new RimLLMChatOptions
            {
                OnStreamRestart = () => { restartCount++; displayed.Length = 0; result = ""; }
            };

            var enumerator = client.GetStreamingResponseAsync(messages, options).GetAsyncEnumerator();
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    if (enumerator.Current.AdditionalProperties != null &&
                        enumerator.Current.AdditionalProperties.ContainsKey("rimllm_stream_restart"))
                    {
                        result = "";
                        displayed.Length = 0;
                        continue;
                    }
                    if (!string.IsNullOrEmpty(enumerator.Current.Text))
                    {
                        result += enumerator.Current.Text;
                        displayed.Append(enumerator.Current.Text);
                    }
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            Assert.AreEqual(1, restartCount, "供應商中途失敗後應恰好通知呼叫端重設一次");
            Assert.AreEqual("final", result);
            Assert.AreEqual("final", displayed.ToString(), "呼叫端在收到重設通知後顯示內容不應殘留前一段");
        }

        [Test]
        public void TestSanitizeForLogRedactsProviderSpecificKeys()
        {
            var secrets = new Dictionary<string, string>
            {
                { "Google", "AIzaSyA1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7" },
                { "Groq", "gsk_abcdefghijklmnopqrstuvwxyz0123456789" },
                { "Grok", "xai-abcdefghijklmnopqrstuvwxyz0123456789" },
                { "Nvidia", "nvapi-abcdefghijklmnopqrstuvwxyz0123456789" },
                { "OpenAI", "sk-abcdefghijklmnopqrstuvwxyz0123456789" },
                { "Anthropic", "sk-ant-abcdefghijklmnopqrstuvwxyz0123" }
            };

            foreach (var kvp in secrets)
            {
                string sanitized = RimLLMLog.SanitizeForLog($"request failed with key {kvp.Value}");
                Assert.IsFalse(sanitized.Contains(kvp.Value),
                    $"日誌遮罩必須涵蓋全部供應商的金鑰格式（{kvp.Key} 未被遮罩）");
            }

            string bearer = RimLLMLog.SanitizeForLog("Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345");
            Assert.IsFalse(bearer.Contains("abcdefghijklmnopqrstuvwxyz012345"), "Bearer token 必須被遮罩");
        }

        [Test]
        public void TestSanitizeForLogTruncatesAndEscapesNewlines()
        {
            string sanitized = RimLLMLog.SanitizeForLog("line1\r\nline2", 500);
            Assert.IsFalse(sanitized.Contains("\n"), "換行必須被跳脫以防日誌注入");
            Assert.IsTrue(sanitized.Contains("\\r\\n"));

            string longText = new string('x', 600);
            Assert.IsTrue(RimLLMLog.SanitizeForLog(longText, 100).Length <= 103, "超長內容必須被截斷");
        }

        [Test]
        public void TestChatInputWhitespaceOnlyIsRejected()
        {
            Assert.IsFalse(ChatTestDrawer.ShouldSendChatInput(null));
            Assert.IsFalse(ChatTestDrawer.ShouldSendChatInput(""));
            Assert.IsFalse(ChatTestDrawer.ShouldSendChatInput("   \t \n "),
                "僅含空白的聊天輸入不應送出請求");
            Assert.IsTrue(ChatTestDrawer.ShouldSendChatInput(" hi "));
        }

        [Test]
        public void TestDispatcherQueueIsBounded()
        {
            RimLLMDispatcher.ResetQueueForTests();
            try
            {
                for (int i = 0; i < 5000; i++)
                {
                    RimLLMDispatcher.TryEnqueueBounded(() => { });
                }

                Assert.LessOrEqual(RimLLMDispatcher.QueuedCount, 4096,
                    "派遣器佇列必須有上限，避免無限成長");
            }
            finally
            {
                RimLLMDispatcher.ResetQueueForTests();
            }
        }

        [Test]
        public void TestDispatcherDrainRespectsPerFrameBudget()
        {
            RimLLMDispatcher.ResetQueueForTests();
            try
            {
                int executed = 0;
                for (int i = 0; i < 500; i++)
                {
                    RimLLMDispatcher.TryEnqueueBounded(() => executed++);
                }

                int processed = RimLLMDispatcher.DrainWithBudget(128, long.MaxValue);

                Assert.AreEqual(128, processed, "單次清空不得超過每幀項目上限");
                Assert.AreEqual(128, executed);
                Assert.Greater(RimLLMDispatcher.QueuedCount, 0, "剩餘項目應留待下一幀處理");
            }
            finally
            {
                RimLLMDispatcher.ResetQueueForTests();
            }
        }

        [Test]
        public void TestTelemetrySaveIsAtomicAndEncryptsChatHistory()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "telemetry.json");

            var previousResolver = RimLLMTelemetryStore.FilePathResolver;
            RimLLMTelemetryStore.FilePathResolver = () => path;
            try
            {
                var store = new RimLLMTelemetryStore();
                store.ChatHistory.Add("SENTINEL-CONVERSATION-CONTENT");
                store.Save();

                Assert.IsFalse(System.IO.File.Exists(path + ".tmp"),
                    "遙測寫入必須先寫暫存檔再原子替換，不得留下 .tmp");

                string raw = System.IO.File.ReadAllText(path);
                Assert.IsFalse(raw.Contains("SENTINEL-CONVERSATION-CONTENT"),
                    "對話歷史不得以明文寫入遙測檔");

                var reloaded = new RimLLMTelemetryStore();
                reloaded.Load();
                CollectionAssert.Contains(reloaded.ChatHistory, "SENTINEL-CONVERSATION-CONTENT");
            }
            finally
            {
                RimLLMTelemetryStore.FilePathResolver = previousResolver;
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void TestTelemetryLoadRecoversFromCorruptFileUsingBackup()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "telemetry.json");

            var previousResolver = RimLLMTelemetryStore.FilePathResolver;
            RimLLMTelemetryStore.FilePathResolver = () => path;
            try
            {
                // 先寫一份有效檔，再寫第二次讓第一份成為 .bak，最後把主檔弄壞。
                var store = new RimLLMTelemetryStore { TotalPromptTokens = 1234 };
                store.Save();
                store.TotalPromptTokens = 5678;
                store.Save();
                System.IO.File.WriteAllText(path, "{ this is not valid json");

                var reloaded = new RimLLMTelemetryStore();
                reloaded.Load();

                Assert.AreEqual(1234, reloaded.TotalPromptTokens,
                    "主檔損毀時應改由備份檔還原");
            }
            finally
            {
                RimLLMTelemetryStore.FilePathResolver = previousResolver;
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void TestLegacyPlaintextChatHistoryIsMigratedOnSave()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "telemetry.json");

            var previousResolver = RimLLMTelemetryStore.FilePathResolver;
            RimLLMTelemetryStore.FilePathResolver = () => path;
            try
            {
                // 模擬舊版明文格式
                System.IO.File.WriteAllText(path,
                    "{\"ChatHistory\":[\"LEGACY-PLAINTEXT-LINE\"],\"TotalPromptTokens\":7}");

                var store = new RimLLMTelemetryStore();
                store.Load();
                CollectionAssert.Contains(store.ChatHistory, "LEGACY-PLAINTEXT-LINE");
                Assert.IsTrue(store.IsDirty, "讀到舊版明文歷史後應標記為待重寫以完成加密遷移");

                store.Save();
                string raw = System.IO.File.ReadAllText(path);
                Assert.IsFalse(raw.Contains("LEGACY-PLAINTEXT-LINE"),
                    "遷移後舊版明文歷史必須從磁碟消失");
            }
            finally
            {
                RimLLMTelemetryStore.FilePathResolver = previousResolver;
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void TestDecryptFailureReturnsNullInsteadOfEmpty()
        {
            // 無法解密的內容（既非 v2 格式也不是合法 Base64 密文）
            string result = EncryptionUtility.Decrypt("v2:bm90LWEtdmFsaWQtcGF5bG9hZA==");

            Assert.IsNull(result,
                "解密失敗必須回傳 null 而非空字串，呼叫端才能保留原始密文而不覆寫為空");
            Assert.AreEqual(string.Empty, EncryptionUtility.Decrypt(""),
                "空輸入仍應回傳空字串，與解密失敗區分");
        }

        [Test]
        public void TestLanguageKeysAreConsistentAcrossLocales()
        {
            string repoRoot = FindRepositoryRoot();
            if (repoRoot == null)
            {
                Assert.Ignore("找不到 repository 根目錄，略過語言檔一致性檢查。");
            }

            var localeKeys = new Dictionary<string, List<string>>();
            foreach (string locale in new[] { "English", "ChineseSimplified", "ChineseTraditional" })
            {
                string path = System.IO.Path.Combine(repoRoot, "Languages", locale, "Keyed", "Keys.xml");
                Assert.IsTrue(System.IO.File.Exists(path), $"缺少語言檔: {path}");

                var doc = System.Xml.Linq.XDocument.Load(path);
                var keys = new List<string>();
                foreach (var element in doc.Root.Elements())
                {
                    keys.Add(element.Name.LocalName);
                }

                var duplicates = new List<string>();
                var seen = new HashSet<string>();
                foreach (string key in keys)
                {
                    if (!seen.Add(key)) duplicates.Add(key);
                }
                Assert.IsEmpty(duplicates, $"{locale} 語言檔含重複鍵: {string.Join(", ", duplicates.ToArray())}");

                keys.Sort(StringComparer.Ordinal);
                localeKeys[locale] = keys;
            }

            CollectionAssert.AreEqual(localeKeys["English"], localeKeys["ChineseTraditional"],
                "三語系語言檔的鍵集合必須完全一致");
            CollectionAssert.AreEqual(localeKeys["English"], localeKeys["ChineseSimplified"],
                "三語系語言檔的鍵集合必須完全一致");
        }

        private static string FindRepositoryRoot()
        {
            string envRoot = System.Environment.GetEnvironmentVariable("RIMLLM_REPO_ROOT");
            if (!string.IsNullOrEmpty(envRoot) && System.IO.Directory.Exists(envRoot))
            {
                return envRoot;
            }

            var dir = new System.IO.DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "About", "About.xml")))
                {
                    return dir.FullName;
                }
            }
            return null;
        }

        [Test]
        public void TestProviderStatisticsTracking()
        {
            var mockSettings = new MockSettings();
            var tracker = new RimLLMUsageTracker(mockSettings);

            // 1. 記錄 2 次成功與 1 次失敗
            tracker.RecordLog(DateTime.UtcNow, "mod", "Gemini", "gemini-model", true, "", 100);
            tracker.RecordLog(DateTime.UtcNow, "mod", "Gemini", "gemini-model", true, "", 100);
            tracker.RecordLog(DateTime.UtcNow, "mod", "Gemini", "gemini-model", false, "Error", 100);

            Assert.IsTrue(tracker.ProviderStatistics.TryGetValue("Gemini", out var stats));
            Assert.AreEqual(3, stats.TotalCount);
            Assert.AreEqual(2, stats.SuccessCount);
            Assert.AreEqual(1, stats.FailureCount);
            Assert.AreEqual(2.0f / 3.0f, stats.SuccessRate, 0.001f);

            // 2. 清空日誌後應清空統計
            tracker.ClearLogs();
            Assert.AreEqual(0, tracker.ProviderStatistics.Count);
        }

        private string okStr(string s) => s;
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
        public ReasoningEffort? DefaultReasoningEffort { get; set; } = null;
        public int MaxRetries { get; set; } = 3;
        public float RetryDelay { get; set; } = 3f;
        public int MaxConcurrentRequests { get; set; } = 2;
        public long TotalPromptTokens { get; set; } = 0;
        public long TotalCompletionTokens { get; set; } = 0;
        public float TotalEstimatedCost { get; set; } = 0f;
        public float DailyBudgetLimit { get; set; } = 0f;
        public int BudgetPolicy { get; set; } = 0;
        public bool EnableAntiAbuse { get; set; } = true;
        public int MaxRequestsPerWindow { get; set; } = 10;
        public int ThrottlingWindowSeconds { get; set; } = 10;
        public int CoolDownDurationSeconds { get; set; } = 60;
        public float DailyAccumulatedCost { get; set; } = 0f;
        public string DailyBudgetResetDate { get; set; } = "";
        public int RoutingStrategy { get; set; } = 0;
        public bool EnableNativeSchema { get; set; } = true;
        public bool EnableJsonRepair { get; set; } = true;

        public string EmbeddingProvider { get; set; } = "Offline_Trigram";
        public string EmbeddingModel { get; set; } = "text-embedding-004";
        public string EmbeddingEndpoint { get; set; } = "";
        public string EmbeddingApiKey { get; set; } = "";

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

        public Func<IEnumerable<ChatMessage>, ChatOptions, string, System.Threading.Tasks.Task<string>> GenerateHandler { get; set; }

        public System.Threading.Tasks.Task<string> GenerateAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model)
        {
            return GenerateHandler != null ? GenerateHandler(messages, options, model) : System.Threading.Tasks.Task.FromResult("");
        }

        public System.Threading.Tasks.Task StreamAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, Action<string> onChunkReceived)
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

    public class TestGeminiProvider : GeminiProvider
    {
        public List<(string url, string payload)> SendCalls { get; } = new List<(string, string)>();

        /// <summary>非串流 seam 最後收到的組態，供斷言 cachedContent / systemInstruction。</summary>
        public GenerateContentConfig LastConfig { get; private set; }

        public string LastModel { get; private set; }

        /// <summary>可注入的模擬回應；預設回傳含 text 的 response。</summary>
        public GenerateContentResponse MockResponse { get; set; }

        public TestGeminiProvider(IRimLLMSettings settings) : base(settings)
        {
            // 刻意不帶 UsageMetadata：usage 記錄依賴 RimLLMProvider.Instance（需先初始化），
            // 且 usage 數值已由任務 7 的 wire 整合測試驗證。
            MockResponse = new GenerateContentResponse
            {
                Candidates = new List<Candidate>
                {
                    new Candidate
                    {
                        Content = new Content
                        {
                            Parts = new List<Part> { new Part { Text = "gemini-response" } }
                        }
                    }
                }
            };
        }

        // 對話一律走 SDK seam；以 null 用戶端即可，using 不會對 null 呼叫 Dispose。
        protected override Client CreateGenAiClient(string apiKey)
        {
            return null;
        }

        protected override System.Threading.Tasks.Task<GenerateContentResponse> GenerateContentNativeAsync(
            Client client,
            string model,
            List<Content> contents,
            GenerateContentConfig config,
            System.Threading.CancellationToken ct)
        {
            LastConfig = config;
            LastModel = model;
            return System.Threading.Tasks.Task.FromResult(MockResponse);
        }

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

    public class TestGeminiProviderWithReasoning : TestGeminiProvider
    {
        public TestGeminiProviderWithReasoning(IRimLLMSettings settings) : base(settings)
        {
            MockResponse = new GenerateContentResponse
            {
                Candidates = new List<Candidate>
                {
                    new Candidate
                    {
                        Content = new Content
                        {
                            Parts = new List<Part>
                            {
                                new Part { Text = "Thinking deeply...", Thought = true },
                                new Part { Text = "Response from Gemini" }
                            }
                        }
                    }
                }
            };
        }
    }

    /// <summary>
    /// 攔截 OpenAI SDK 實際送出的 wire payload（HttpRequestMessage），
    /// 供任務 7 的 wire 整合測試驗證 BuildChatOptions / RawRepresentationFactory 效果。
    /// </summary>
    public class CapturingHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        public System.Collections.Generic.List<string> RequestBodies { get; } = new System.Collections.Generic.List<string>();

        public System.Collections.Generic.List<string> RequestUrls { get; } = new System.Collections.Generic.List<string>();

        public string ResponseBody { get; set; } = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}";

        public string ResponseContentType { get; set; } = "application/json";

        public string LastRequestBody => RequestBodies.Count == 0 ? null : RequestBodies[RequestBodies.Count - 1];

        public string FirstRequestBody => RequestBodies.Count == 0 ? null : RequestBodies[0];

        public string LastRequestUrl => RequestUrls.Count == 0 ? null : RequestUrls[RequestUrls.Count - 1];

        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            string body = request.Content != null
                ? request.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : null;
            RequestBodies.Add(body ?? string.Empty);
            RequestUrls.Add(request.RequestUri != null ? request.RequestUri.ToString() : null);
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Content = new System.Net.Http.StringContent(
                ResponseBody ?? string.Empty,
                System.Text.Encoding.UTF8,
                ResponseContentType);
            return System.Threading.Tasks.Task.FromResult(response);
        }
    }

    /// <summary>以 capturing transport 建立 OpenAI ChatClient 的共用測試縫。</summary>
    public static class WireChatClientFactory
    {
        public static IChatClient Create(
            IRimLLMSettings settings,
            string providerId,
            string endpoint,
            string model,
            CapturingHttpMessageHandler handler)
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrEmpty(endpoint))
            {
                options.Endpoint = new Uri(endpoint, UriKind.Absolute);
            }
            options.Transport = new HttpClientPipelineTransport(new System.Net.Http.HttpClient(handler));
            var client = new ChatClient(model, new ApiKeyCredential(settings.GetActiveApiKey(providerId)), options);
            return client.AsIChatClient();
        }
    }

    public class TestOpenAIProvider : OpenAIProvider
    {
        public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

        public string InterceptedPayload => WireHandler.LastRequestBody;

        public TestOpenAIProvider(IRimLLMSettings settings) : base(settings) {}

        public override IChatClient CreateChatClient(string model)
        {
            return WireChatClientFactory.Create(
                Settings,
                ProviderId,
                Settings.GetEndpoint(ProviderId, DefaultEndpoint),
                model,
                WireHandler);
        }
    }

    public class TestOpenRouterProvider : OpenRouterProvider
    {
        public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

        public string InterceptedPayload => WireHandler.LastRequestBody;

        public TestOpenRouterProvider(IRimLLMSettings settings) : base(settings) {}

        public override IChatClient CreateChatClient(string model)
        {
            return WireChatClientFactory.Create(
                Settings,
                ProviderId,
                Settings.GetEndpoint(ProviderId, DefaultEndpoint),
                model,
                WireHandler);
        }
    }

    public class TestZaiProvider : ZaiProvider
    {
        public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

        public string InterceptedPayload => WireHandler.LastRequestBody;

        public string InterceptedUrl => WireHandler.LastRequestUrl;

        public TestZaiProvider(IRimLLMSettings settings) : base(settings) {}

        public override IChatClient CreateChatClient(string model)
        {
            return WireChatClientFactory.Create(
                Settings,
                ProviderId,
                Settings.GetEndpoint(ProviderId, DefaultEndpoint),
                model,
                WireHandler);
        }
    }

    public class MockStreamProvider : ILLMProvider
    {
        public string ProviderId { get; set; }
        public bool RequiresApiKey { get; set; } = true;
        public Func<IEnumerable<ChatMessage>, ChatOptions, string, Action<string>, System.Threading.Tasks.Task> StreamHandler { get; set; }

        public System.Threading.Tasks.Task<string> GenerateAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model)
        {
            return System.Threading.Tasks.Task.FromResult("");
        }

        public System.Threading.Tasks.Task StreamAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, Action<string> onChunkReceived)
        {
            return StreamHandler != null ? StreamHandler(messages, options, model, onChunkReceived) : System.Threading.Tasks.Task.CompletedTask;
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

    public class NullableTestDataStructure
    {
        public string Name { get; set; }
        public int? OptionalCount { get; set; } // Nullable 解包測試
    }

    public class TestOpenAIProviderWithReasoning : OpenAIProvider
    {
        public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

        public TestOpenAIProviderWithReasoning(IRimLLMSettings settings) : base(settings) {}

        public override IChatClient CreateChatClient(string model)
        {
            return WireChatClientFactory.Create(
                Settings,
                ProviderId,
                Settings.GetEndpoint(ProviderId, DefaultEndpoint),
                model,
                WireHandler);
        }
    }

    public class TestOpenAIProviderWithUsage : OpenAIProvider
    {
        public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

        public string MockResponse
        {
            get { return WireHandler.ResponseBody; }
            set { WireHandler.ResponseBody = value; }
        }

        public TestOpenAIProviderWithUsage(IRimLLMSettings settings) : base(settings) {}

        public override IChatClient CreateChatClient(string model)
        {
            return WireChatClientFactory.Create(
                Settings,
                ProviderId,
                Settings.GetEndpoint(ProviderId, DefaultEndpoint),
                model,
                WireHandler);
        }
    }

    /// <summary>
    /// 對外開放 protected 的 ThrowHttpError，供 HTTP 狀態碼對照測試直接驗證。
    /// </summary>
    public class HttpErrorProbeProvider : OpenAIProvider
    {
        public HttpErrorProbeProvider(IRimLLMSettings settings) : base(settings) {}

        public void Probe(System.Net.HttpStatusCode statusCode, string responseBody)
        {
            using (var response = new System.Net.Http.HttpResponseMessage(statusCode))
            {
                ThrowHttpError(response, responseBody);
            }
        }
    }

    /// <summary>
    /// 未宣告支援原生 JSON Schema 的衍生供應商（沿用預設 false）。
    /// </summary>
    public class TestKimiPayloadProvider : KimiProvider
    {
        public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

        public string CapturedPayload => WireHandler.FirstRequestBody;

        public TestKimiPayloadProvider(IRimLLMSettings settings) : base(settings) {}

        public override IChatClient CreateChatClient(string model)
        {
            return WireChatClientFactory.Create(
                Settings,
                ProviderId,
                Settings.GetEndpoint(ProviderId, DefaultEndpoint),
                model,
                WireHandler);
        }
    }

    /// <summary>
    /// 已驗證支援原生 JSON Schema 的衍生供應商。
    /// </summary>
    public class TestDeepSeekPayloadProvider : DeepSeekProvider
    {
        public CapturingHttpMessageHandler WireHandler { get; } = new CapturingHttpMessageHandler();

        public string CapturedPayload => WireHandler.FirstRequestBody;

        public TestDeepSeekPayloadProvider(IRimLLMSettings settings) : base(settings) {}

        public override IChatClient CreateChatClient(string model)
        {
            return WireChatClientFactory.Create(
                Settings,
                ProviderId,
                Settings.GetEndpoint(ProviderId, DefaultEndpoint),
                model,
                WireHandler);
        }
    }
}
