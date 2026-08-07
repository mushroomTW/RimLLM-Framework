using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class RimLLMChatClientTests
    {
        private RimLLMManager CreateManager()
        {
            var settings = new MockSettings { FallbackChain = new List<string> { "TestMock:mock-model" } };
            settings.EnabledProviders["TestMock"] = true;
            settings.ApiKeys["TestMock"] = "mock-key";
            var manager = new RimLLMManager(settings);
            manager.RegisterProvider(new MockTestProvider
            {
                ProviderId = "TestMock",
                GenerateHandler = (messages, options, model) =>
                    System.Threading.Tasks.Task.FromResult(
                        (options != null && (options.ResponseFormat != null || (options.AdditionalProperties != null && options.AdditionalProperties.ContainsKey("rimllm_response_schema"))))
                            ? "{\"Value\":42,\"Message\":\"mock\"}"
                            : "mock-reply for " + model)
            });
            return manager;
        }

        private RimLLMChatClient CreateClient(RimLLMManager manager, string modId)
        {
            RimLLMProvider.RegisterClient(modId);
            return manager.CreateChatClient(modId, Assembly.GetCallingAssembly());
        }

        [Test]
        public void TestGetResponseAsync_ReturnsTextAndModel()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.facade.mod");
            var response = client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new RimLLMChatOptions()).GetAwaiter().GetResult();
            Assert.IsNotNull(response);
            Assert.IsNotEmpty(response.Text);
            Assert.AreEqual("TestMock", response.ModelId.Split(new[] { ':' }, StringSplitOptions.None)[0]);
        }

        [Test]
        public void TestGetResponseAsync_ModelIdSpecifiedPreferred()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.modelid.mod");
            var response = client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new ChatOptions { ModelId = "TestMock:preferred-model" }).GetAwaiter().GetResult();
            Assert.AreEqual("TestMock", response.ModelId.Split(new[] { ':' }, StringSplitOptions.None)[0]);
            Assert.AreEqual("preferred-model", response.ModelId.Split(new[] { ':' }, StringSplitOptions.None)[1]);
            Assert.AreEqual("mock-reply for preferred-model", response.Text);
        }

        [Test]
        public void TestGetResponseAsync_UsageMappedFromResult()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.usage.mod");
            var response = client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new RimLLMChatOptions()).GetAwaiter().GetResult();
            Assert.IsNotNull(response.Usage);
            Assert.AreEqual(0, response.Usage.InputTokenCount ?? 0);
            Assert.AreEqual(0, response.Usage.OutputTokenCount ?? 0);
        }

        [Test]
        public void TestGetResponseAsync_RimLLMChatOptionsPriorityPassed()
        {
            // 透過 manager 佇列行為間接驗證：Priority 越高越先執行（此處僅驗證不會例外、回傳正常）
            var manager = CreateManager();
            var client = CreateClient(manager, "test.priority.mod");
            var response = client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new RimLLMChatOptions { Priority = 5 }).GetAwaiter().GetResult();
            Assert.IsNotEmpty(response.Text);
        }

        [Test]
        public void TestGetResponseAsync_StandardChatOptionsDefaults()
        {
            // 純標準 ChatOptions：框架預設值路徑（無 RimLLMChatOptions 延伸欄位）
            var manager = CreateManager();
            var client = CreateClient(manager, "test.defaults.mod");
            var response = client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new ChatOptions()).GetAwaiter().GetResult();
            Assert.IsNotNull(response);
            Assert.IsNotEmpty(response.Text);
        }

        [Test]
        public void TestTranslate_SystemPromptExtractedFromFirstMessage()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.translate.mod");
            var request = client.Translate(
                new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, "You are a helpful assistant."),
                    new ChatMessage(ChatRole.User, "hi")
                },
                new RimLLMChatOptions { DisableReasoning = true, Priority = 7 },
                CancellationToken.None);
            Assert.AreEqual("You are a helpful assistant.", request.SystemPrompt);
            Assert.IsTrue(request.DisableReasoning);
            Assert.AreEqual(7, request.Priority);
            Assert.AreEqual(2, request.Messages.Count);
            Assert.AreEqual("test.translate.mod", request.ModId);
        }

        [Test]
        public void TestGetResponseAsync_UnregisteredModThrows()
        {
            var manager = CreateManager();
            Assert.Throws<RimLLMException>(() =>
                manager.CreateChatClient("test.notregistered.mod", Assembly.GetCallingAssembly()));
        }

        [Test]
        public void TestGetStreamingResponseAsync_YieldsChunks()
        {
            var mockSettings = new MockSettings { FallbackChain = new List<string> { "TestMockStream:model-s" } };
            mockSettings.EnabledProviders["TestMockStream"] = true;
            mockSettings.ApiKeys["TestMockStream"] = "key";
            var manager = new RimLLMManager(mockSettings);
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "TestMockStream",
                StreamHandler = (messages, options, model, onChunk) =>
                {
                    onChunk("mock-");
                    onChunk("stream");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            });

            var client = CreateClient(manager, "test.stream.mod");
            var chunks = new List<string>();
            var enumerator = client.GetStreamingResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new RimLLMChatOptions()).GetAsyncEnumerator();
            try
            {
                while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
                {
                    var update = enumerator.Current;
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        chunks.Add(update.Text);
                    }
                }
            }
            finally
            {
                enumerator.DisposeAsync().GetAwaiter().GetResult();
            }
            Assert.AreEqual("mock-stream", string.Concat(chunks));
        }

        [Test]
        public void TestGetStreamingResponseAsync_RestartMarkerPushed()
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
                StreamHandler = (messages, options, model, onChunk) =>
                {
                    onChunk("partial");
                    throw new RimLLMException(LLMError.ProviderOffline, "dropped mid-stream");
                }
            });
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "MockGood",
                StreamHandler = (messages, options, model, onChunk) =>
                {
                    onChunk("final");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            });

            int restartCount = 0;
            var updates = new List<ChatResponseUpdate>();
            var client = CreateClient(manager, "test.stream.restart.mod");
            var enumerator = client.GetStreamingResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new RimLLMChatOptions { OnStreamRestart = () => restartCount++ }).GetAsyncEnumerator();
            try
            {
                while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
                {
                    updates.Add(enumerator.Current);
                }
            }
            finally
            {
                enumerator.DisposeAsync().GetAwaiter().GetResult();
            }
            Assert.AreEqual(1, restartCount, "供應商中途失敗後應恰好通知呼叫端重設一次");
            Assert.IsTrue(updates.Exists(u => u.AdditionalProperties != null
                && u.AdditionalProperties.ContainsKey("rimllm_stream_restart")),
                "應推送 rimllm_stream_restart marker update");
        }

        [Test]
        public void TestMetadata_ProviderNameIsRimLLM()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.metadata.mod");
            Assert.AreEqual("RimLLM", client.Metadata.ProviderName);
        }

        [Test]
        public void TestGetResponseObjectAsync_DeserializesViaFacade()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.obj.mod");
            var result = client.GetResponseObjectAsync<TestDataStructure>(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "give data") },
                new RimLLMChatOptions
                {
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(
                        System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
                        "custom_type",
                        "RimLLM structured response")
                }).GetAwaiter().GetResult();
            Assert.IsNotNull(result);
            Assert.AreEqual(42, result.Value);
            Assert.AreEqual("mock", result.Message);
        }

        [Test]
        public void TestGetResponseObjectAsync_NonFacadeUsesSimplifiedPath()
        {
            var plainClient = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"Value\":5,\"Message\":\"ok\"}"))
            };
            var result = plainClient.GetResponseObjectAsync<TestDataStructure>(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }).GetAwaiter().GetResult();
            Assert.AreEqual(5, result.Value);
            Assert.AreEqual("ok", result.Message);
        }
    }
}
