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
        public void TestClientRegistry()
        {
            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            Assembly externalAssembly = typeof(string).Assembly;
            string modId = "test.unit.mod.id";

            // 1. 正常註冊與驗證
            ClientRegistry.RegisterClient(modId, thisAssembly);
            Assert.IsTrue(ClientRegistry.Verify(modId, thisAssembly));

            // 2. 阻擋假冒安全驗證
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
            var registerMethod = manager.GetType().GetMethod("RegisterProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(registerMethod, "找不到 RegisterProvider 反射方法");

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

            registerMethod.Invoke(manager, new object[] { mockFail });
            registerMethod.Invoke(manager, new object[] { mockSuccess });

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
            var registerMethod = manager.GetType().GetMethod("RegisterProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(registerMethod);

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
            registerMethod.Invoke(manager, new object[] { mockSuccess });

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
            var method = manager.GetType().GetMethod("ResolveFallbackEntry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(method);
            
            // 1. 測試傳統 "Provider:Model" 格式
            object[] args1 = new object[] { "OpenAI:gpt-4o", null, null };
            bool res1 = (bool)method.Invoke(manager, args1);
            Assert.IsTrue(res1);
            Assert.AreEqual("OpenAI", args1[1]?.ToString());
            Assert.AreEqual("gpt-4o", args1[2]?.ToString());
            
            // 2. 測試 OpenRouter 純供應商格式 (會自動解析為 openrouter/auto)
            object[] args2 = new object[] { "OpenRouter", null, null };
            bool res2 = (bool)method.Invoke(manager, args2);
            Assert.IsTrue(res2);
            Assert.AreEqual("OpenRouter", args2[1]?.ToString());
            Assert.AreEqual("openrouter/auto", args2[2]?.ToString());
            
            // 3. 測試其他純供應商格式 (會自動回退至 defaultModel)
            object[] args3 = new object[] { "OpenAI", null, null };
            bool res3 = (bool)method.Invoke(manager, args3);
            Assert.IsTrue(res3);
            Assert.AreEqual("OpenAI", args3[1]?.ToString());
            Assert.AreEqual("default", args3[2]?.ToString());
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
            var registerMethod = manager.GetType().GetMethod("RegisterProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            
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
            registerMethod.Invoke(manager, new object[] { mockProv });

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
            var registerMethod = manager.GetType().GetMethod("RegisterProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            
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
            registerMethod.Invoke(manager, new object[] { mockProv });

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
            var registerMethod = manager.GetType().GetMethod("RegisterProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            
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
            registerMethod.Invoke(manager, new object[] { mockFail });
            registerMethod.Invoke(manager, new object[] { mockSuccess });

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
        public void TestDoubleRepair()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockProv:model-z" }
            };
            mockSettings.EnabledProviders["MockProv"] = true;
            mockSettings.ApiKeys["MockProv"] = "key";

            var manager = new RimLLMManager(mockSettings);
            var registerMethod = manager.GetType().GetMethod("RegisterProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            
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
            registerMethod.Invoke(manager, new object[] { mockProv });

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
        public void TestAnthropicPromptCachingPayload()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["Anthropic"] = "mock-key";
            
            var provider = new TestAnthropicProvider(mockSettings);
            var request = new LLMRequest
            {
                ModId = "test",
                Prompt = "hello",
                SystemPrompt = "long-system-instructions-for-caching",
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
            Assert.AreEqual("long-system-instructions-for-caching", systemArray[0]["text"]?.ToString());
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
                SystemPrompt = "long-system-instructions-for-gemini-caching",
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
            Assert.AreEqual("long-system-instructions-for-gemini-caching", firstPayload["systemInstruction"]?["parts"]?[0]?["text"]?.ToString());

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
            var registerMethod = manager.GetType().GetMethod("RegisterProvider", BindingFlags.NonPublic | BindingFlags.Instance);

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
            registerMethod.Invoke(manager, new object[] { mockStream });

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

            var method = manager.GetType().GetMethod("GetSampleJson", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(Type) }, null);
            Assert.IsNotNull(method);

            string json = method.Invoke(manager, new object[] { typeof(ComplexTestDataStructure) }) as string;
            
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
        public int MaxRetries { get; set; } = 3;
        public float RetryDelay { get; set; } = 3f;
        public int MaxConcurrentRequests { get; set; } = 2;

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
        public void Write() {}
    }

    public class MockTestProvider : ILLMProvider
    {
        public string ProviderId { get; set; }
        
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

