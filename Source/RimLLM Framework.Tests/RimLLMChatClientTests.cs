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

            // 框架欄位存放於 AdditionalProperties，因此 MEAI 的 base.Clone() 就足以保留它們，
            // 不再需要覆寫 Clone、也不再要求 clone 結果維持衍生型別：前方的 middleware
            // （ConfigureOptions、FunctionInvocation 等）即使 clone 成純 ChatOptions 也不會切掉設定。
            ClassicAssert.AreEqual(0.5f, cloned.Temperature);
            ClassicAssert.AreEqual(9, RimLLMChatOptions.GetPriority(cloned));
            ClassicAssert.AreEqual("Medium", RimLLMChatOptions.GetMinFallbackLevel(cloned));
            ClassicAssert.AreEqual("world rules", RimLLMChatOptions.GetCachedContext(cloned));
            ClassicAssert.IsTrue(RimLLMChatOptions.GetDisableReasoning(cloned));
            ClassicAssert.IsTrue(RimLLMChatOptions.GetEnableContextCaching(cloned), "CachedContext 不為空時應沿用計算預設值");
            ClassicAssert.IsNotNull(RimLLMChatOptions.GetOnStreamRestart(cloned));

            // 明確設定 false 時不可被 CachedContext 的計算預設值蓋掉。
            original.EnableContextCaching = false;
            ClassicAssert.IsFalse(RimLLMChatOptions.GetEnableContextCaching(original.Clone()));
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
        public void TestGetStreamingResponseAsync_PreservesProviderAndModelMetadata()
        {
            // 回歸測試：串流路徑先前只回傳 Text 與 Contents，把成功那次嘗試的
            // ProviderId / ModelName / token 計數整組丟掉，使呼叫端只拿到空的 ModelId。
            var mockSettings = new MockSettings { FallbackChain = new List<string> { "TestMockStream:model-s" } };
            mockSettings.EnabledProviders["TestMockStream"] = true;
            mockSettings.ApiKeys["TestMockStream"] = "key";
            var manager = new RimLLMManager(mockSettings);
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "TestMockStream",
                StreamHandler = (messages, options, model, onChunk) =>
                {
                    onChunk("chunk");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            });

            var client = CreateClient(manager, "test.stream.meta.mod");
            string modelId = null;
            var enumerator = client.GetStreamingResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                new RimLLMChatOptions()).GetAsyncEnumerator();
            try
            {
                while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
                {
                    modelId = modelId ?? enumerator.Current.ModelId;
                }
            }
            finally
            {
                enumerator.DisposeAsync().GetAwaiter().GetResult();
            }

            ClassicAssert.AreEqual("TestMockStream:model-s", modelId);
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

        [Test]
        public void TestGetStreamingResponseAsync_ResponseCacheHit_EmitsCachedChunks()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "TestMockStream:model-s" },
                EnableResponseCache = true,
                ResponseCacheTtlMinutes = 10f
            };
            mockSettings.EnabledProviders["TestMockStream"] = true;
            mockSettings.ApiKeys["TestMockStream"] = "key";
            var manager = new RimLLMManager(mockSettings);
            int callCount = 0;
            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "TestMockStream",
                StreamHandler = (messages, options, model, onChunk) =>
                {
                    callCount++;
                    onChunk("cached-");
                    onChunk("content");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            });

            var client = CreateClient(manager, "test.cachestream.mod");
            var inputMessages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "query-for-cache") };

            // 第一次呼叫：填充快取
            var chunks1 = new List<string>();
            var e1 = client.GetStreamingResponseAsync(inputMessages).GetAsyncEnumerator();
            try
            {
                while (e1.MoveNextAsync().GetAwaiter().GetResult())
                {
                    if (!string.IsNullOrEmpty(e1.Current.Text)) chunks1.Add(e1.Current.Text);
                }
            }
            finally
            {
                e1.DisposeAsync().GetAwaiter().GetResult();
            }
            ClassicAssert.AreEqual("cached-content", string.Concat(chunks1));
            ClassicAssert.AreEqual(1, callCount);

            // 第二次呼叫：命中快取，必須完整回傳快取文字且不應漏封包
            var chunks2 = new List<string>();
            var e2 = client.GetStreamingResponseAsync(inputMessages).GetAsyncEnumerator();
            try
            {
                while (e2.MoveNextAsync().GetAwaiter().GetResult())
                {
                    if (!string.IsNullOrEmpty(e2.Current.Text)) chunks2.Add(e2.Current.Text);
                }
            }
            finally
            {
                e2.DisposeAsync().GetAwaiter().GetResult();
            }
            ClassicAssert.AreEqual("cached-content", string.Concat(chunks2));
            ClassicAssert.AreEqual(1, callCount, "第二次串流呼叫應直接命中快取，不應再次觸發 StreamHandler");
        }

        [Test]
        public void TestOpenAICompatibleProvider_CreateChatClient_WithoutApiKey_Succeeds()
        {
            var settings = new MockSettings();
            var provider = new Providers.OpenAICompatibleProvider(settings);
            // 本地相容 Provider 在金鑰為空時應自動回退 PlaceholderApiKey 而非拋出 ArgumentException
            using (var client = provider.CreateChatClient("llama3"))
            {
                ClassicAssert.IsNotNull(client);
            }
        }

        [Test]
        public void TestBuildMessages_MergesSystemPromptWhenSystemMessageAlreadyExists()
        {
            var request = new RimLLMRequest
            {
                SystemPrompt = "Framework System Instruction",
                CachedContext = "Cached Lore",
                Messages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, "Caller System Message"),
                    new ChatMessage(ChatRole.User, "Hello")
                }
            };

            var built = RimLLMChatClientExecutor.BuildMessages(request);
            ClassicAssert.AreEqual(2, built.Count);
            ClassicAssert.AreEqual(ChatRole.System, built[0].Role);
            StringAssert.Contains("Framework System Instruction", built[0].Text);
            StringAssert.Contains("Cached Lore", built[0].Text);
            StringAssert.Contains("Caller System Message", built[0].Text);
        }

        [Test]
        public void TestBuildOptions_IncludesExecutorManagedFlag()
        {
            var request = new RimLLMRequest
            {
                ModId = "test.mod",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }
            };

            var options = RimLLMChatClientExecutor.BuildOptions(request, "gpt-4o", false, null, RimLLMSchemaProfile.OpenAI);
            ClassicAssert.IsNotNull(options.AdditionalProperties);
            ClassicAssert.IsTrue(options.AdditionalProperties.ContainsKey(RimLLMChatOptions.ExecutorManagedKey));
            ClassicAssert.IsTrue((bool)options.AdditionalProperties[RimLLMChatOptions.ExecutorManagedKey]);
        }

        [Test]
        public void TestGetResponseAsync_RecordUsage_NotDoubleCounted()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "TestMock:m1" }
            };
            mockSettings.EnabledProviders["TestMock"] = true;
            mockSettings.ApiKeys["TestMock"] = "key";
            var manager = new RimLLMManager(mockSettings);
            RimLLMProvider.Initialize(manager);

            manager.RegisterProvider(new MockTestProvider
            {
                ProviderId = "TestMock",
                GenerateHandler = (messages, options, model) => System.Threading.Tasks.Task.FromResult("mock output text")
            });

            var client = CreateClient(manager, "test.usage.mod");
            var response = client.GetResponseAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello world") }).GetAwaiter().GetResult();
            ClassicAssert.IsNotNull(response);

            var stats = manager.UsageTracker.ProviderStatistics["TestMock"];
            ClassicAssert.AreEqual(1, stats.TotalCount, "經由框架管線執行時用量應僅記錄一次，不得重複記帳");
            ClassicAssert.AreEqual(1, stats.SuccessCount);
        }

        [Test]
        public void TestStreamEarlyDispose_CancelsBackgroundStream()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "StreamCancelMock:m1" }
            };
            mockSettings.EnabledProviders["StreamCancelMock"] = true;
            mockSettings.ApiKeys["StreamCancelMock"] = "key";
            var manager = new RimLLMManager(mockSettings);

            manager.RegisterProvider(new MockStreamProvider
            {
                ProviderId = "StreamCancelMock",
                StreamHandler = async (messages, options, model, onChunk) =>
                {
                    onChunk("chunk-1");
                    for (int i = 0; i < 20; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(50);
                    }
                }
            });

            var client = CreateClient(manager, "test.cancel.mod");
            var enumerator = client.GetStreamingResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }).GetAsyncEnumerator();

            bool moved = enumerator.MoveNextAsync().GetAwaiter().GetResult();
            ClassicAssert.IsTrue(moved);
            ClassicAssert.AreEqual("chunk-1", enumerator.Current.Text);
            enumerator.DisposeAsync().GetAwaiter().GetResult();
        }

        [Test]
        public void TestGetStreamingResponseAsync_DoubleDisposeAsync_SwallowsObjectDisposedException()
        {
            var mockSettings = new MockSettings
            {
                FallbackChain = new List<string> { "MockDoubleDispose:default" }
            };
            mockSettings.EnabledProviders["MockDoubleDispose"] = true;
            mockSettings.ApiKeys["MockDoubleDispose"] = "key";
            var manager = new RimLLMManager(mockSettings);
            var provider = new MockStreamProvider
            {
                ProviderId = "MockDoubleDispose",
                StreamHandler = (msgs, opts, model, onChunk) =>
                {
                    onChunk("test");
                    return Task.CompletedTask;
                }
            };
            manager.RegisterProvider(provider);
            var client = CreateClient(manager, "test.double.dispose");
            var enumerator = client.GetStreamingResponseAsync(
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }).GetAsyncEnumerator();

            bool moved = enumerator.MoveNextAsync().GetAwaiter().GetResult();
            ClassicAssert.IsTrue(moved);
            enumerator.DisposeAsync().GetAwaiter().GetResult();
            // 第二次呼叫 DisposeAsync，CTS 已處於 Disposed 狀態，驗證安全忽略 ObjectDisposedException
            Assert.DoesNotThrow(() => enumerator.DisposeAsync().GetAwaiter().GetResult());
        }

        [Test]
        public void TestOpenAiStream_SelfHealsInPlaceOn400()
        {
            RimLLM_Framework.Providers.RimLLMReasoningSupport.Reset();
            try
            {
                var mockSettings = new MockSettings();
                mockSettings.ApiKeys["OpenAI"] = "test-key";
                var provider = new TestOpenAIProvider(mockSettings);

                provider.WireHandler.ScriptResponse(
                    System.Net.HttpStatusCode.BadRequest,
                    "{\"error\":{\"message\":\"unsupported parameter: reasoning_effort\",\"type\":\"invalid_request_error\"}}");
                provider.WireHandler.ResponseContentType = "text/event-stream";
                provider.WireHandler.ResponseBody = "data: {\"choices\":[{\"delta\":{\"content\":\"healed\"}}]}\n\ndata: [DONE]\n\n";

                using (var client = provider.CreateChatClient("gpt-4o"))
                {
                    var options = new ChatOptions
                    {
                        Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium }
                    };

                    var chunks = new List<string>();
                    var enumerator = client.GetStreamingResponseAsync(
                        new List<ChatMessage> { new ChatMessage(ChatRole.User, "stream test") },
                        options).GetAsyncEnumerator();

                    try
                    {
                        while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
                        {
                            if (!string.IsNullOrEmpty(enumerator.Current.Text))
                            {
                                chunks.Add(enumerator.Current.Text);
                            }
                        }
                    }
                    finally
                    {
                        enumerator.DisposeAsync().GetAwaiter().GetResult();
                    }

                    ClassicAssert.AreEqual("healed", string.Concat(chunks));
                    ClassicAssert.IsTrue(RimLLM_Framework.Providers.RimLLMReasoningSupport.IsReasoningUnsupported("OpenAI", "gpt-4o"));
                }
            }
            finally
            {
                RimLLM_Framework.Providers.RimLLMReasoningSupport.Reset();
            }
        }
    }
}
