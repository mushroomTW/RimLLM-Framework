using NUnit.Framework;
using NUnit.Framework.Legacy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Api;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class ToolCallingTests
    {
        private sealed class MockToolCallingProvider : ILLMProvider
        {
            public string ProviderId { get; set; } = "ToolMock";
            public bool RequiresApiKey { get; set; } = true;
            public LLMProviderCapabilities Capabilities { get; set; } = new LLMProviderCapabilities
            {
                SupportsStreaming = true,
                SupportsFunctionCalling = true,
                SupportsUsageMetadata = true
            };

            public Func<IEnumerable<ChatMessage>, ChatOptions, Task<ChatResponse>> GetResponseHandler { get; set; }

            public IChatClient CreateChatClient(string model)
            {
                return new MockCustomChatClient
                {
                    GetResponseHandler = GetResponseHandler
                };
            }

            public Task<TestResult> TestConnectionAsync() => Task.FromResult(new TestResult { Success = true });
            public Task<List<string>> FetchAvailableModelsAsync() => Task.FromResult(new List<string> { "mock-model" });
        }

        [Test]
        public async Task TestRawToolCalling_ProviderReturnsFunctionCall_PreservedInResponse()
        {
            var settings = new MockSettings { FallbackChain = new List<string> { "ToolMock:mock-model" } };
            settings.EnabledProviders["ToolMock"] = true;
            settings.ApiKeys["ToolMock"] = "mock-key";
            var manager = new RimLLMManager(settings);

            var toolProvider = new MockToolCallingProvider
            {
                GetResponseHandler = (messages, options) =>
                {
                    var funcCall = new FunctionCallContent("call_123", "GetWeather", new Dictionary<string, object>
                    {
                        ["location"] = "RimWorld Colony"
                    });
                    var respMessage = new ChatMessage(ChatRole.Assistant, new List<AIContent> { funcCall });
                    return Task.FromResult(new ChatResponse(respMessage)
                    {
                        FinishReason = ChatFinishReason.ToolCalls,
                        ModelId = "ToolMock:mock-model"
                    });
                }
            };
            manager.RegisterProvider(toolProvider);

            var client = manager.CreateChatClient("test.toolcalling.mod");
            var weatherFunc = AIFunctionFactory.Create((string location) => $"Sunny at {location}", "GetWeather", "Gets weather");

            var chatOptions = new ChatOptions
            {
                Tools = new List<AITool> { weatherFunc }
            };

            var response = await client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "What is the weather?") },
                chatOptions);

            ClassicAssert.IsNotNull(response);
            var assistantMsg = response.Messages.FirstOrDefault();
            ClassicAssert.IsNotNull(assistantMsg);
            ClassicAssert.AreEqual(1, assistantMsg.Contents.Count);

            var call = assistantMsg.Contents[0] as FunctionCallContent;
            ClassicAssert.IsNotNull(call);
            ClassicAssert.AreEqual("GetWeather", call.Name);
            ClassicAssert.AreEqual("call_123", call.CallId);
            ClassicAssert.AreEqual("RimWorld Colony", call.Arguments["location"]);
        }

        [Test]
        public async Task TestAutoInvokingToolCalling_ExecutesMainThreadAndCompletesLoop()
        {
            var settings = new MockSettings { FallbackChain = new List<string> { "ToolMock:mock-model" } };
            settings.EnabledProviders["ToolMock"] = true;
            settings.ApiKeys["ToolMock"] = "mock-key";
            var manager = new RimLLMManager(settings);

            int round = 0;
            var toolProvider = new MockToolCallingProvider
            {
                GetResponseHandler = (messages, options) =>
                {
                    round++;
                    if (round == 1)
                    {
                        // 第一輪回傳 FunctionCall
                        var funcCall = new FunctionCallContent("call_abc", "AddNumbers", new Dictionary<string, object>
                        {
                            ["a"] = 10,
                            ["b"] = 32
                        });
                        var callMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent> { funcCall });
                        return Task.FromResult(new ChatResponse(callMsg)
                        {
                            FinishReason = ChatFinishReason.ToolCalls,
                            ModelId = "ToolMock:mock-model"
                        });
                    }
                    else
                    {
                        // 第二輪檢查前一輪的 FunctionResult
                        var lastMsg = messages.LastOrDefault();
                        string resultVal = "unknown";
                        if (lastMsg?.Contents != null)
                        {
                            foreach (var c in lastMsg.Contents)
                            {
                                if (c is FunctionResultContent frc)
                                {
                                    resultVal = frc.Result?.ToString();
                                }
                            }
                        }
                        var finalMsg = new ChatMessage(ChatRole.Assistant, $"The sum is {resultVal}");
                        return Task.FromResult(new ChatResponse(finalMsg)
                        {
                            FinishReason = ChatFinishReason.Stop,
                            ModelId = "ToolMock:mock-model"
                        });
                    }
                }
            };
            manager.RegisterProvider(toolProvider);

            var baseClient = manager.CreateChatClient("test.toolcalling.mod");
            var invokingClient = baseClient.AsMainThreadFunctionInvokingClient(maxIterations: 5);

            bool functionExecuted = false;
            var addFunc = AIFunctionFactory.Create((int a, int b) =>
            {
                functionExecuted = true;
                return a + b;
            }, "AddNumbers", "Adds two numbers");

            var options = new ChatOptions
            {
                Tools = new List<AITool> { addFunc }
            };

            var finalResponse = await invokingClient.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "Calculate 10 + 32") },
                options);

            ClassicAssert.IsNotNull(finalResponse);
            ClassicAssert.IsTrue(functionExecuted, "AIFunction 應該被執行");
            ClassicAssert.AreEqual("The sum is 42", finalResponse.Text);
            ClassicAssert.AreEqual(2, round, "應該執行 2 輪對話");
        }

        [Test]
        public void GeminiSendsToolDefinitionsThroughOpenAiCompatibleEndpoint()
        {
            // Gemini 改走 OpenAI 相容端點後，工具定義沿用 OpenAI 家族的 wire 格式；
            // 翻譯正確性由 OpenAI 路徑的既有測試覆蓋，此處只釘住 Gemini 發出的請求形狀。
            var mockSettings = new MockSettings();
            mockSettings.ApiKeys[ProviderIds.Gemini] = "mock-key";
            var provider = new TestGeminiProvider(mockSettings);

            var addFunc = AIFunctionFactory.Create((int a, int b) => a + b, "AddNumbers", "Adds two numbers");
            var options = new ChatOptions
            {
                Tools = new List<AITool> { addFunc }
            };

            string result = provider.GenerateAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "Calculate 10 + 32") },
                options,
                "gemini-2.5-flash").GetAwaiter().GetResult();

            ClassicAssert.AreEqual("ok", result);
            var payload = Newtonsoft.Json.Linq.JObject.Parse(provider.InterceptedPayload);
            ClassicAssert.IsNotNull(payload["tools"], "工具定義應隨請求送出。");
            ClassicAssert.AreEqual("gemini-2.5-flash", payload["model"]?.ToString());
        }
    }
}
