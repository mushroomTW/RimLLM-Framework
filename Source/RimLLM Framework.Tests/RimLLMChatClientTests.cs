using NUnit.Framework;
using NUnit.Framework.Legacy;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

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
                Capabilities = new LLMProviderCapabilities { SupportsStreaming = true, SupportsNativeStructuredOutput = true, SupportsUsageMetadata = true },
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
            return manager.CreateChatClient(modId);
        }

        [Test]
        public void TestGetResponseAsync_ReturnsTextAndModel()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.facade.mod");
            var response = client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new RimLLMChatOptions()).GetAwaiter().GetResult();
            ClassicAssert.IsNotNull(response);
            ClassicAssert.IsNotEmpty(response.Text);
            ClassicAssert.AreEqual("TestMock", response.ModelId.Split(new[] { ':' }, StringSplitOptions.None)[0]);
        }

        [Test]
        public void TestGetResponseAsync_ModelIdSpecifiedPreferred()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.modelid.mod");
            var response = client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new ChatOptions { ModelId = "TestMock:preferred-model" }).GetAwaiter().GetResult();
            ClassicAssert.AreEqual("TestMock", response.ModelId.Split(new[] { ':' }, StringSplitOptions.None)[0]);
            ClassicAssert.AreEqual("preferred-model", response.ModelId.Split(new[] { ':' }, StringSplitOptions.None)[1]);
            ClassicAssert.AreEqual("mock-reply for preferred-model", response.Text);
        }

        [Test]
        public void TestGetResponseAsync_UsageMappedFromResult()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.usage.mod");
            var response = client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new RimLLMChatOptions()).GetAwaiter().GetResult();
            ClassicAssert.IsNotNull(response.Usage);
            ClassicAssert.AreEqual(0, response.Usage.InputTokenCount ?? 0);
            ClassicAssert.AreEqual(0, response.Usage.OutputTokenCount ?? 0);
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
            ClassicAssert.IsNotEmpty(response.Text);
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
            ClassicAssert.IsNotNull(response);
            ClassicAssert.IsNotEmpty(response.Text);
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
            ClassicAssert.AreEqual("You are a helpful assistant.", request.SystemPrompt);
            ClassicAssert.IsTrue(request.DisableReasoning);
            ClassicAssert.AreEqual(7, request.Priority);
            ClassicAssert.AreEqual(2, request.Messages.Count);
            ClassicAssert.AreEqual("test.translate.mod", request.ModId);
        }

                [Test]
        public void TestRimLLMChatOptionsClonePreservesFrameworkFields()
        {
            var original = new RimLLMChatOptions
            {
                Temperature = 0.5f,
                Priority = 9,
                MinFallbackLevel = "Medium",
                CachedContext = "world rules",
                DisableReasoning = true,
                OnStreamRestart = () => { }
            };

            ChatOptions cloned = original.Clone();

            // base.Clone() 必須回傳衍生型別，否則框架欄位會在任何 middleware clone 時被切掉。
            ClassicAssert.IsInstanceOf<RimLLMChatOptions>(cloned, "Clone 必須保留 RimLLMChatOptions 型別");
            var typed = (RimLLMChatOptions)cloned;
            ClassicAssert.AreEqual(0.5f, typed.Temperature);
            ClassicAssert.AreEqual(9, typed.Priority);
            ClassicAssert.AreEqual("Medium", typed.MinFallbackLevel);
            ClassicAssert.AreEqual("world rules", typed.CachedContext);
            ClassicAssert.IsTrue(typed.DisableReasoning);
            ClassicAssert.IsTrue(typed.EnableContextCaching, "CachedContext 不為空時應沿用計算預設值");
            ClassicAssert.IsNotNull(typed.OnStreamRestart);

            // 明確設定 false 時不可被 CachedContext 的計算預設值蓋掉。
            original.EnableContextCaching = false;
            ClassicAssert.IsFalse(((RimLLMChatOptions)original.Clone()).EnableContextCaching);
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
            ClassicAssert.AreEqual("mock-stream", string.Concat(chunks));
        }

        [Test]
        public void TestGetStreamingResponseAsync_ProducerFailurePropagates()
        {
            // 所有供應商都失敗時，例外必須傳到列舉端，不能被靜默吞成「串流正常結束」。
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockDead:model-a" },
                MaxRetries = 0,
                RetryDelay = 0f
            };
            mockSettings.EnabledProviders["MockDead"] = true;
            mockSettings.ApiKeys["MockDead"] = "key";

            var manager = new RimLLMManager(mockSettings);
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "MockDead",
                StreamHandler = (messages, options, model, onChunk) =>
                    throw new RimLLMException(LLMError.ProviderOffline, "provider is down")
            });

            var client = CreateClient(manager, "test.stream.failure.mod");
            var enumerator = client.GetStreamingResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new RimLLMChatOptions()).GetAsyncEnumerator();

            Assert.Throws<RimLLMException>(() =>
            {
                try
                {
                    while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
                    {
                    }
                }
                finally
                {
                    enumerator.DisposeAsync().GetAwaiter().GetResult();
                }
            }, "產生端失敗時列舉必須擲出例外");
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
            ClassicAssert.AreEqual(1, restartCount, "供應商中途失敗後應恰好通知呼叫端重設一次");
            ClassicAssert.IsTrue(updates.Exists(u => u.AdditionalProperties != null
                && u.AdditionalProperties.ContainsKey("rimllm_stream_restart")),
                "應推送 rimllm_stream_restart marker update");
        }

        [Test]
        public void TestMetadata_ProviderNameIsRimLLM()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.metadata.mod");
            ClassicAssert.AreEqual("RimLLM", client.Metadata.ProviderName);
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
            ClassicAssert.IsNotNull(result);
            ClassicAssert.AreEqual(42, result.Value);
            ClassicAssert.AreEqual("mock", result.Message);
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
            ClassicAssert.AreEqual(5, result.Value);
            ClassicAssert.AreEqual("ok", result.Message);
        }

        [Test]
        public void TestGetResponseObjectAsync_FacadeFullPath_StaticRepair()
        {
            var settings = new MockSettings { FallbackChain = new List<string> { "StaticRepairMock:model-sr" } };
            settings.EnabledProviders["StaticRepairMock"] = true;
            settings.ApiKeys["StaticRepairMock"] = "key";

            var manager = new RimLLMManager(settings);
            int callCount = 0;
            manager.RegisterProvider(new MockTestProvider
            {
                ProviderId = "StaticRepairMock",
                GenerateHandler = (messages, options, model) =>
                {
                    callCount++;
                    // 模擬含 markdown 與尾隨逗號的 JSON
                    return System.Threading.Tasks.Task.FromResult("```json\n{\"Value\":99,\"Message\":\"repaired\",}\n```");
                }
            });

            var client = CreateClient(manager, "test.staticrepair.mod");
            var result = client.GetResponseObjectAsync<TestDataStructure>(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "get repaired data") }).GetAwaiter().GetResult();

            ClassicAssert.IsNotNull(result);
            ClassicAssert.AreEqual(99, result.Value);
            ClassicAssert.AreEqual("repaired", result.Message);
            ClassicAssert.AreEqual(1, callCount);
        }

        [Test]
        public void TestGetResponseObjectAsync_SchemaCached()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.schemacache.mod");
            
            var res1 = client.GetResponseObjectAsync<TestDataStructure>(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "req 1") }).GetAwaiter().GetResult();
            var res2 = client.GetResponseObjectAsync<TestDataStructure>(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "req 2") }).GetAwaiter().GetResult();

            ClassicAssert.IsNotNull(res1);
            ClassicAssert.IsNotNull(res2);
            ClassicAssert.AreEqual(42, res1.Value);
            ClassicAssert.AreEqual(42, res2.Value);
        }

        [Test]
        public void TestGetResponseAsync_PureChatOptionsDefaults()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.pureoptions.mod");
            var options = new ChatOptions(); // Pure MEAI ChatOptions without RimLLMChatOptions
            var response = client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") }, options).GetAwaiter().GetResult();

            ClassicAssert.IsNotNull(response);
            ClassicAssert.IsNotEmpty(response.Text);
        }

        [Test]
        public void TestGetResponseAsync_AdditionalPropertiesMerged()
        {
            var manager = CreateManager();
            var client = CreateClient(manager, "test.addprops.mod");
            var options = new RimLLMChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["custom_key"] = "custom_val" }
            };
            var response = client.GetResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }, options).GetAwaiter().GetResult();

            ClassicAssert.IsNotNull(response.AdditionalProperties);
            ClassicAssert.AreEqual("custom_val", response.AdditionalProperties["custom_key"]);
        }
    }
}
