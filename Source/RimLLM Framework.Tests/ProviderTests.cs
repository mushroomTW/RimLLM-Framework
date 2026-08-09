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
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;
using RimLLM_Framework.Mod;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class ProviderTests
    {
        [Test]
        public void TestOpenRouterFallbackPayload()
        {
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys["OpenRouter"] = "mock-key";

            var provider = new TestOpenRouterProvider(mockSettings);


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
            Assert.AreEqual(1, provider.CacheCreateCalls.Count);

            // 驗證快取建立參數（改走官方 SDK 後為型別化物件，不再解析 JSON 字串）
            var firstCall = provider.CacheCreateCalls[0];
            Assert.AreEqual("models/gemini-1.5-pro", firstCall.model);
            Assert.AreEqual(expectedSystemText, firstCall.config.SystemInstruction?.Parts?[0]?.Text);
            Assert.AreEqual("300s", firstCall.config.Ttl);

            // 驗證 SDK seam 收到 cachedContent 且未附帶 systemInstruction
            Assert.AreEqual("cachedContents/mock-cache-id", provider.LastConfig.CachedContent);
            Assert.IsNull(provider.LastConfig.SystemInstruction);

            // 2. 第二次呼叫：快取已存在，應直接引用而不重複建立快取
            provider.CacheCreateCalls.Clear();
            string response2 = provider.GenerateAsync(messages, options, "gemini-1.5-pro").GetAwaiter().GetResult();
            Assert.AreEqual("gemini-response", response2);
            Assert.AreEqual(0, provider.CacheCreateCalls.Count);
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
            Assert.AreEqual(0, provider.CacheCreateCalls.Count);

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
            Assert.AreEqual(0, provider.CacheCreateCalls.Count);
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


            provider.GenerateAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new RimLLMChatOptions(),
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
    }

    // Shared mock and helper classes used by test suites
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

        public string EmbeddingProvider { get; set; } = "Disabled";
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
        /// <summary>快取建立呼叫紀錄（走官方 SDK 的 Caches.CreateAsync seam）。</summary>
        public List<(string model, CreateCachedContentConfig config)> CacheCreateCalls { get; } = new List<(string, CreateCachedContentConfig)>();

        /// <summary>非串流 seam 最後收到的組態，供斷言 cachedContent / systemInstruction。</summary>
        public GenerateContentConfig LastConfig { get; private set; }

        public string LastModel { get; private set; }

        /// <summary>可注入的模擬回應；預設回傳含 text 的 response。</summary>
        public GenerateContentResponse MockResponse { get; set; }

        public TestGeminiProvider(IRimLLMSettings settings) : base(settings)
        {
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

        protected override System.Threading.Tasks.Task<CachedContent> CreateCachedContentNativeAsync(
            string apiKey,
            string modelWithPrefix,
            CreateCachedContentConfig config,
            System.Threading.CancellationToken cancellationToken)
        {
            CacheCreateCalls.Add((modelWithPrefix, config));
            return System.Threading.Tasks.Task.FromResult(new CachedContent
            {
                Name = "cachedContents/mock-cache-id",
                ExpireTime = DateTime.UtcNow.AddMinutes(5)
            });
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
        }
    }

    public class NestedData
    {
        public float Weight { get; set; }
        public ComplexTestDataStructure SelfRef { get; set; }
    }

    public class NullableTestDataStructure
    {
        public string Name { get; set; }
        public int? OptionalCount { get; set; }
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
    /// 探測 HTTP 狀態碼 → LLMError 的對照。框架已無 raw HTTP 路徑，
    /// 此處直接驗證所有路徑共用的 LLMErrorMapper。
    /// </summary>
    public class HttpErrorProbeProvider : OpenAIProvider
    {
        public HttpErrorProbeProvider(IRimLLMSettings settings) : base(settings) {}

        public void Probe(System.Net.HttpStatusCode statusCode, string responseBody)
        {
            using (var response = new System.Net.Http.HttpResponseMessage(statusCode))
            {
                throw LLMErrorMapper.CreateException(
                    (int)statusCode,
                    responseBody,
                    LLMErrorMapper.ParseRetryAfter(response));
            }
        }
    }

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
