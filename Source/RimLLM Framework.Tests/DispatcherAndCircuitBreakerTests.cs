extern alias bclasync;
using NUnit.Framework;
using System;
using System.Reflection;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
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
    public class DispatcherAndCircuitBreakerTests
    {
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
        public void TestComplexTypeSchemaWarmupAndRecursion()
        {
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);
            
            // 預熱無空建構子、帶有循環引用的型別，驗證不會 StackOverflow 且產生合理 JSON
            RimLLMJsonHelper.GetSampleJson<ComplexTestDataStructure>();

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

            var request = new RimLLMRequest
            {
                ModId = modId,
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") }
            };

            // 第一次呼叫：兩個都沒有延遲歷史，依據 FallbackChain 順序（先 MockSlow）
            string res1 = manager.GenerateResultAsync(request).GetAwaiter().GetResult().Text;
            Assert.AreEqual("slow-ok", res1);

            // 第二次呼叫：因為 MockSlow 已有延遲（100ms），MockFast 尚未有歷史（視為 0 延遲），優先呼叫 MockFast
            string res2 = manager.GenerateResultAsync(request).GetAwaiter().GetResult().Text;
            Assert.AreEqual("fast-ok", res2);

            // 第三次呼叫：此時 MockSlow 平均 100ms，MockFast 平均 5ms，智慧路由應該優先選擇 MockFast
            string res3 = manager.GenerateResultAsync(request).GetAwaiter().GetResult().Text;
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

            var request = new RimLLMRequest
            {
                ModId = modId,
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") }
            };

            // 第一次呼叫：MockFail 失敗，然後 Fallback 到 MockSuccess 成功
            string res1 = manager.GenerateResultAsync(request).GetAwaiter().GetResult().Text;
            Assert.AreEqual("success-ok", res1);
            Assert.AreEqual(1, failCalls);
            Assert.AreEqual(1, successCalls);

            // 第二次呼叫：MockFail 此時正處於 60 秒的故障冷卻期，智慧路由應直接跳過它，不進行呼叫，直接執行 MockSuccess
            string res2 = manager.GenerateResultAsync(request).GetAwaiter().GetResult().Text;
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

            var request = new RimLLMRequest
            {
                ModId = modId,
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") },
                ResponseType = typeof(TestDataStructure)
            };

            // 1. 當 EnableJsonRepair 為 false 時，預期拋出例外
            Assert.Throws<RimLLMException>(() =>
            {
                string raw = manager.GenerateResultAsync(request).GetAwaiter().GetResult().Text;
                manager.DeserializeStructured<TestDataStructure>(raw, mockSettings, request);
            });

            // 2. 當 EnableJsonRepair 為 true 時，預期成功修復並解析
            mockSettings.EnableJsonRepair = true;
            string rawRepaired = manager.GenerateResultAsync(request).GetAwaiter().GetResult().Text;
            var res = manager.DeserializeStructured<TestDataStructure>(rawRepaired, mockSettings, request);
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
        public void TestEmbeddingEndpointNormalizesToServiceRoot()
        {
            // OpenAI SDK 需要的是服務根位址，使用者可能貼上完整的 embeddings 路徑。
            Assert.AreEqual("http://localhost:11434/v1",
                RimLLMEmbeddingService.NormalizeEmbeddingEndpoint("http://localhost:11434/v1/embeddings"));
            Assert.AreEqual("http://localhost:1234/v1",
                RimLLMEmbeddingService.NormalizeEmbeddingEndpoint(" http://localhost:1234/v1/ "));
            Assert.AreEqual("http://localhost:11434",
                RimLLMEmbeddingService.NormalizeEmbeddingEndpoint("http://localhost:11434/api/embed"));
            Assert.IsNull(RimLLMEmbeddingService.NormalizeEmbeddingEndpoint(""),
                "空字串應回傳 null，讓呼叫端改用預設端點");
            Assert.IsNull(RimLLMEmbeddingService.NormalizeEmbeddingEndpoint(null));
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
                var waiterA = RimLLMManager.AwaitBudgetApprovalAsync(
                    shared.Task, cancelledCts.Token, TimeSpan.FromSeconds(30));

                var waiterB = RimLLMManager.AwaitBudgetApprovalAsync(
                    shared.Task, System.Threading.CancellationToken.None, TimeSpan.FromSeconds(30));

                cancelledCts.Cancel();
                Assert.Throws<OperationCanceledException>(() => waiterA.GetAwaiter().GetResult());

                shared.TrySetResult(true);
                Assert.IsTrue(waiterB.GetAwaiter().GetResult(),
                    "單一等待者取消不得影響其他共用同一對話框的請求");
            }
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

        private string okStr(string s) => s;
    }
}
