extern alias bclasync;
extern alias ste;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// 審查發現問題的回歸測試。
    /// 對應六個修正：驗證路徑追蹤、快取鍵結構化序列化、快取深層快照、
    /// Embedding 實際 provider/model 標記、串流背壓與總量限制、Ollama 探測限流。
    /// </summary>
    [TestFixture]
    public class ReviewFixesTests
    {
        // ---------- [P1] 結構化驗證：重複型別的物件不再被跳過 ----------

        private class Item
        {
            public string Name;
            public string RequiredField;
        }

        private class ItemContainer
        {
            public List<Item> Items;
        }

        [Test]
        public void StructuredValidation_SecondSameTypeElement_StillValidates()
        {
            // 兩個同型別元素，第二個的 RequiredField 為 null：
            // 舊實作會因為 visitedTypes 永久記住 Item 而跳過第二個，這裡必須拋出。
            var container = new ItemContainer
            {
                Items = new List<Item>
                {
                    new Item { Name = "first", RequiredField = "ok" },
                    new Item { Name = "second", RequiredField = null }
                }
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                RimLLMJsonHelper.ValidateStructuredObject(container));
            ClassicAssert.IsNotNull(ex);
            ClassicAssert.IsTrue(ex.Message.Contains("RequiredField"), $"實際訊息：{ex.Message}");
        }

        [Test]
        public void StructuredValidation_AllRequiredFieldsPresent_Passes()
        {
            var container = new ItemContainer
            {
                Items = new List<Item>
                {
                    new Item { Name = "first", RequiredField = "ok" },
                    new Item { Name = "second", RequiredField = "ok" }
                }
            };

            RimLLMJsonHelper.ValidateStructuredObject(container); // 不拋出即通過
        }

        [Test]
        public void StructuredValidation_CyclicReference_DoesNotStackOverflow()
        {
            // 循環引用（Node.Next 指回自己）不該 StackOverflow，也不該誤報 null。
            var node = new CyclicNode();
            node.Next = node;

            RimLLMJsonHelper.ValidateStructuredObject(node);
        }

        private class CyclicNode
        {
            public CyclicNode Next;
        }

        // ---------- [P2] 快取鍵：結構化序列化區分內容 ----------

        [Test]
        public void CacheKey_DifferentDictionaryValues_ProduceDifferentKeys()
        {
            var messages1 = MessagesWithToolResult(new Dictionary<string, object> { { "city", "RimCity" } });
            var messages2 = MessagesWithToolResult(new Dictionary<string, object> { { "city", "OtherCity" } });

            string key1 = RimLLMResponseCacheKey.Build(messages1, new ChatOptions());
            string key2 = RimLLMResponseCacheKey.Build(messages2, new ChatOptions());

            ClassicAssert.AreNotEqual(key1, key2, "內容不同的工具結果必須產生不同的快取鍵");
        }

        [Test]
        public void CacheKey_SameDictionaryDifferentOrder_SameKey()
        {
            var messages1 = MessagesWithToolResult(new Dictionary<string, object> { { "a", 1 }, { "b", 2 } });
            var messages2 = MessagesWithToolResult(new Dictionary<string, object> { { "b", 2 }, { "a", 1 } });

            string key1 = RimLLMResponseCacheKey.Build(messages1, new ChatOptions());
            string key2 = RimLLMResponseCacheKey.Build(messages2, new ChatOptions());

            ClassicAssert.AreEqual(key1, key2, "字典列舉順序不可影響快取鍵");
        }

        [Test]
        public void CacheKey_FunctionResultComplexValue_DistinguishesContent()
        {
            var msgA = new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionResultContent("call-1", new Dictionary<string, object> { { "x", 1 } })
            });
            var msgB = new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionResultContent("call-1", new Dictionary<string, object> { { "x", 2 } })
            });

            string keyA = RimLLMResponseCacheKey.Build(new[] { msgA }, new ChatOptions());
            string keyB = RimLLMResponseCacheKey.Build(new[] { msgB }, new ChatOptions());

            ClassicAssert.AreNotEqual(keyA, keyB);
        }

        private static List<ChatMessage> MessagesWithToolResult(object result)
        {
            return new List<ChatMessage>
            {
                new ChatMessage(ChatRole.User, "check weather"),
                new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionResultContent("call-1", result)
                })
            };
        }

        // ---------- [P2] 快取深層快照：呼叫端修改不污染快取 ----------

        [Test]
        public async Task ResponseCache_CallerMutation_DoesNotPolluteCachedEntry()
        {
            var settings = new MockSettings { EnableResponseCache = true, ResponseCacheTtlMinutes = 30f };
            int calls = 0;
            var cache = new RimLLMResponseCacheChatClient(
                new MockCustomChatClient
                {
                    GetResponseHandler = (msgs, opts) =>
                    {
                        calls++;
                        var message = new ChatMessage(ChatRole.Assistant, "original");
                        return Task.FromResult(new ChatResponse(new List<ChatMessage> { message })
                        {
                            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
                            AdditionalProperties = new AdditionalPropertiesDictionary { ["k"] = "v" }
                        });
                    }
                },
                settings,
                new RimLLMResponseCacheStore());

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };

            // 第一次呼叫：取得原回應後刻意修改
            ChatResponse first = await cache.GetResponseAsync(messages);
            first.Messages.Add(new ChatMessage(ChatRole.User, "pollution"));
            first.Messages[0].Contents.Add(new TextContent("mutated"));
            first.Usage.InputTokenCount = 999;
            first.AdditionalProperties["k"] = "mutated";

            // 第二次呼叫：命中快取，但內容必須仍是原始值
            ChatResponse second = await cache.GetResponseAsync(messages);
            ClassicAssert.AreEqual(1, calls, "第二次應命中快取");
            ClassicAssert.AreEqual("original", second.Messages[0].Text);
            ClassicAssert.AreEqual(1, second.Messages.Count, "呼叫端的追加不得出現在快取裡");
            ClassicAssert.AreEqual(10, second.Usage.InputTokenCount, "呼叫端的 Usage 修改不得污染快取");
            ClassicAssert.AreEqual("v", second.AdditionalProperties["k"], "呼叫端的 AdditionalProperties 修改不得污染快取");
        }

        [Test]
        public async Task ResponseCache_HitResultMutation_DoesNotPolluteNextHit()
        {
            var settings = new MockSettings { EnableResponseCache = true, ResponseCacheTtlMinutes = 30f };
            int calls = 0;
            var cache = new RimLLMResponseCacheChatClient(
                new MockCustomChatClient
                {
                    GetResponseHandler = (msgs, opts) =>
                    {
                        calls++;
                        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stable")));
                    }
                },
                settings,
                new RimLLMResponseCacheStore());

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") };

            await cache.GetResponseAsync(messages); // 寫入快取
            ChatResponse hit1 = await cache.GetResponseAsync(messages);
            hit1.Messages.Clear(); // 清掉命中回傳的 Messages

            ChatResponse hit2 = await cache.GetResponseAsync(messages);
            ClassicAssert.AreEqual("stable", hit2.Messages[0].Text, "命中結果的修改不得影響下一次命中");
        }

        // ---------- [P2] Embedding：模型/供應商在請求開始時捕捉 ----------

        [Test]
        public void EmbeddingResult_CarriesProviderAndModel()
        {
            var settings = new MockSettings
            {
                EmbeddingProvider = "OpenAI",
                EmbeddingModel = "text-embedding-3-small",
                EmbeddingApiKey = "sk-embed-test"
            };
            var manager = new RimLLMManager(settings);
            RimLLMProvider.Initialize(manager);

            var handler = new CapturingHttpMessageHandler
            {
                ResponseBody =
                    "{\"object\":\"list\",\"model\":\"text-embedding-3-small\"," +
                    "\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.1,0.2]}]," +
                    "\"usage\":{\"prompt_tokens\":10,\"total_tokens\":10}}"
            };
            RimLLMEmbeddingService.TransportOverride =
                new System.ClientModel.Primitives.HttpClientPipelineTransport(new System.Net.Http.HttpClient(handler));
            try
            {
                var generator = manager.CreateEmbeddingGenerator("test.embed.capture.mod");
                GeneratedEmbeddings<Embedding<float>> generated =
                    generator.GenerateAsync(new[] { "hello" }).GetAwaiter().GetResult();

                ClassicAssert.AreEqual(1, generated.Count);
                ClassicAssert.AreEqual("text-embedding-3-small", generated[0].ModelId, "向量應標上實際使用的模型");

                var metadata = generator.GetService(typeof(EmbeddingGeneratorMetadata), null) as EmbeddingGeneratorMetadata;
                ClassicAssert.IsNotNull(metadata);
                ClassicAssert.AreEqual("OpenAI", metadata.ProviderName);
            }
            finally
            {
                RimLLMEmbeddingService.TransportOverride = null;
            }
        }

        // ---------- [P2] 串流：總量限制 ----------

        [Test]
        public void StreamAsync_CapsAccumulatedTextEstimate()
        {
            var client = new CapturingChatClient
            {
                StreamUpdates =
                {
                    new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                    {
                        new TextContent(new string('x', RimLLMChatClientExecutor.MaxAccumulatedCharsForEstimate + 1000))
                    })
                }
            };

            var received = new List<ChatResponseUpdate>();
            RimLLMChatClientExecutor.StreamAsync(
                client,
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") },
                null,
                "gpt-test",
                useNativeSchema: false,
                "OpenAI",
                async update => { received.Add(update); await Task.CompletedTask; },
                30f,
                CancellationToken.None).GetAwaiter().GetResult();

            // 不拋出、update 仍全部轉發；內部字元估算被截斷在上限內。
            ClassicAssert.AreEqual(1, received.Count);
        }

        [Test]
        public void StreamAsync_ForwardsAllUpdatesEvenWithManyChunks()
        {
            var client = new CapturingChatClient();
            for (int i = 0; i < 5000; i++)
            {
                client.StreamUpdates.Add(new ChatResponseUpdate(
                    ChatRole.Assistant, new List<AIContent> { new TextContent("chunk-" + i) }));
            }

            var received = new List<ChatResponseUpdate>();
            RimLLMChatClientExecutor.StreamAsync(
                client,
                new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") },
                null,
                "gpt-test",
                useNativeSchema: false,
                "OpenAI",
                async update => { received.Add(update); await Task.CompletedTask; },
                30f,
                CancellationToken.None).GetAwaiter().GetResult();

            ClassicAssert.AreEqual(5000, received.Count, "所有 chunk 都必須原樣轉發");
        }

        // ---------- [P2] Ollama 探測限流常數 ----------

        [Test]
        public void OllamaProbeLimits_AreSane()
        {
            ClassicAssert.Greater(RimLLMEmbeddingService.MaxOllamaModelsToProbe, 0);
            ClassicAssert.Greater(RimLLMEmbeddingService.MaxOllamaProbeConcurrency, 0);
            ClassicAssert.LessOrEqual(RimLLMEmbeddingService.MaxOllamaProbeConcurrency, RimLLMEmbeddingService.MaxOllamaModelsToProbe);
        }

        // ---------- [P3] 快取鍵追蹤修正：bool 標記與參數排序 ----------

        [Test]
        public void CacheKey_BoolTrueAndFalse_ProduceDifferentKeys()
        {
            var msgTrue = new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionResultContent("call-1", new Dictionary<string, object> { { "flag", true } })
            });
            var msgFalse = new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionResultContent("call-1", new Dictionary<string, object> { { "flag", false } })
            });

            string keyTrue = RimLLMResponseCacheKey.Build(new[] { msgTrue }, new ChatOptions());
            string keyFalse = RimLLMResponseCacheKey.Build(new[] { msgFalse }, new ChatOptions());

            ClassicAssert.AreNotEqual(keyTrue, keyFalse, "bool true/false 必須產生不同的快取鍵");
        }

        [Test]
        public void CacheKey_FunctionCallArgumentsDifferentOrder_SameKey()
        {
            var msgA = new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call-1", "GetWeather", new Dictionary<string, object> { { "a", 1 }, { "b", 2 } })
            });
            var msgB = new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call-1", "GetWeather", new Dictionary<string, object> { { "b", 2 }, { "a", 1 } })
            });

            string keyA = RimLLMResponseCacheKey.Build(new[] { msgA }, new ChatOptions());
            string keyB = RimLLMResponseCacheKey.Build(new[] { msgB }, new ChatOptions());

            ClassicAssert.AreEqual(keyA, keyB, "工具參數插入順序不可影響快取鍵");
        }

        // ---------- [P3] 深層複製追蹤修正：巢狀容器隔離 ----------

        [Test]
        public void DeepCopy_NestedAdditionalProperties_AreIsolated()
        {
            var source = new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi"))
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["nested"] = new Dictionary<string, object> { { "x", 1 } }
                }
            };

            ChatResponse copy = RimLLMResponseDeepCopy.Copy(source);
            ((Dictionary<string, object>)copy.AdditionalProperties["nested"])["x"] = 999;

            ClassicAssert.AreEqual(1, ((Dictionary<string, object>)source.AdditionalProperties["nested"])["x"],
                "複製品巢狀字典的修改不得回寫到來源");
        }

        [Test]
        public void DeepCopy_FunctionCallNestedArguments_AreIsolated()
        {
            var source = new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", "F", new Dictionary<string, object>
                {
                    { "opts", new Dictionary<string, object> { { "k", "v" } } }
                })
            });

            ChatMessage copy = (ChatMessage)RimLLMResponseDeepCopy.Copy(
                new ChatResponse(source)).Messages[0];
            var copiedCall = (FunctionCallContent)copy.Contents[0];
            ((Dictionary<string, object>)copiedCall.Arguments["opts"])["k"] = "mutated";

            var originalCall = (FunctionCallContent)source.Contents[0];
            ClassicAssert.AreEqual("v", ((Dictionary<string, object>)originalCall.Arguments["opts"])["k"],
                "複製品工具參數巢狀字典的修改不得回寫到來源");
        }

        // ---------- [P3] 結構化驗證追蹤修正：集合 null 元素 ----------

        private class NullableItemContainer
        {
            public List<int?> Values;
        }

        [Test]
        public void StructuredValidation_NullElement_Throws()
        {
            var container = new ItemContainer
            {
                Items = new List<Item> { null }
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                RimLLMJsonHelper.ValidateStructuredObject(container));
            ClassicAssert.IsTrue(ex.Message.Contains("null"), $"實際訊息：{ex.Message}");
        }

        [Test]
        public void StructuredValidation_NullableElement_AllowsNull()
        {
            var container = new NullableItemContainer
            {
                Values = new List<int?> { 1, null, 3 }
            };

            RimLLMJsonHelper.ValidateStructuredObject(container); // 不拋出即通過
        }
    }
}
