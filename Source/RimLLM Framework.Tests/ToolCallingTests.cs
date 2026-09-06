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
        public void TestGeminiProvider_BuildContents_TranslatesToolCallingAndResponses()
        {
            var gemini = new GeminiProvider(new MockSettings());
            var method = typeof(GeminiProvider).GetMethod("BuildContents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            ClassicAssert.IsNotNull(method, "BuildContents 應存在");

            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.User, "What is 1+1?"),
                new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call_1", "add", new Dictionary<string, object> { ["x"] = 1, ["y"] = 1 })
                }),
                new ChatMessage(ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent("call_1", 2)
                })
            };

            var contents = method.Invoke(null, new object[] { messages }) as List<Google.GenAI.Types.Content>;
            ClassicAssert.IsNotNull(contents);
            ClassicAssert.AreEqual(3, contents.Count);

            // 驗證 Model Assistant Part
            ClassicAssert.AreEqual("model", contents[1].Role);
            var part1 = contents[1].Parts[0];
            ClassicAssert.IsNotNull(part1.FunctionCall);
            ClassicAssert.AreEqual("add", part1.FunctionCall.Name);

            // 驗證 User / Tool Response Part
            ClassicAssert.AreEqual("user", contents[2].Role);
            var part2 = contents[2].Parts[0];
            ClassicAssert.IsNotNull(part2.FunctionResponse);
            ClassicAssert.AreEqual("add", part2.FunctionResponse.Name, "應能透過 CallId 還原原始函式名稱");
        }

        [Test]
        public void TestGeminiProvider_ReadGeminiChatResponse_ParsesFunctionCall()
        {
            var gemini = new GeminiProvider(new MockSettings());
            var method = typeof(GeminiProvider).GetMethod("ReadGeminiChatResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            ClassicAssert.IsNotNull(method, "ReadGeminiChatResponse 應存在");

            var geminiResponse = new Google.GenAI.Types.GenerateContentResponse
            {
                ResponseId = "resp_123",
                Candidates = new List<Google.GenAI.Types.Candidate>
                {
                    new Google.GenAI.Types.Candidate
                    {
                        Content = new Google.GenAI.Types.Content
                        {
                            Parts = new List<Google.GenAI.Types.Part>
                            {
                                new Google.GenAI.Types.Part
                                {
                                    FunctionCall = new Google.GenAI.Types.FunctionCall
                                    {
                                        Id = "call_gemini_1",
                                        Name = "GetStatus",
                                        Args = new Dictionary<string, object> { ["target"] = "Pawn" }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            var chatResponse = method.Invoke(gemini, new object[] { geminiResponse, "gemini-2.5-flash" }) as ChatResponse;
            ClassicAssert.IsNotNull(chatResponse);
            ClassicAssert.AreEqual(ChatFinishReason.ToolCalls, chatResponse.FinishReason);
            ClassicAssert.AreEqual(1, chatResponse.Messages.Count);

            var call = chatResponse.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault();
            ClassicAssert.IsNotNull(call);
            ClassicAssert.AreEqual("GetStatus", call.Name);
            ClassicAssert.AreEqual("call_gemini_1", call.CallId);
            ClassicAssert.AreEqual("Pawn", call.Arguments["target"]);
        }
    }
}
