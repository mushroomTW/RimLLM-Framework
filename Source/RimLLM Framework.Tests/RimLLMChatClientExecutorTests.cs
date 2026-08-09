extern alias bclasync;
extern alias ste;

using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.ClientModel;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class RimLLMChatClientExecutorTests
    {
        [Test]
        public void TestGenerateAsync_SendsMessagesAndOptions()
        {
            var client = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello"))
            };

            var request = new RimLLMRequest
            {
                ModId = "test-mod",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") },
                SystemPrompt = "be brief",
                MaxOutputTokens = 256,
                Temperature = 0.2f
            };

            var result = RimLLMChatClientExecutor.GenerateAsync(
                client, request, "gpt-test", useNativeSchema: false, "OpenAI", 30f).GetAwaiter().GetResult();

            Assert.AreEqual("hello", result.Text);
            Assert.AreEqual(1, client.ReceivedOptions.Count);
            Assert.AreEqual("gpt-test", client.ReceivedOptions[0].ModelId);
            Assert.AreEqual(0.2f, client.ReceivedOptions[0].Temperature);
            Assert.AreEqual(256, client.ReceivedOptions[0].MaxOutputTokens);
            var messages = new List<ChatMessage>(client.ReceivedMessages[0]);
            Assert.AreEqual(2, messages.Count);
            Assert.AreEqual(ChatRole.System, messages[0].Role);
            Assert.AreEqual(ChatRole.User, messages[1].Role);
        }

        [Test]
        public void TestGenerateAsync_NativeSchemaSetsStrictFlag()
        {
            var client = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
            };

            var request = new RimLLMRequest
            {
                ModId = "test-mod",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "give data") }
            };
            request.ResponseType = typeof(TestDataStructure);

            RimLLMChatClientExecutor.GenerateAsync(
                client, request, "gpt-test", useNativeSchema: true, "OpenAI", 30f).GetAwaiter().GetResult();

            ChatOptions options = client.ReceivedOptions[0];
            Assert.IsNotNull(options.ResponseFormat);
            Assert.IsTrue((bool)options.AdditionalProperties["strict"]);
        }

        [Test]
        public void TestGenerateAsync_ContainsOpenEndedMapDisablesStrictMode()
        {
            var client = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
            };

            var request = new RimLLMRequest
            {
                ModId = "test-mod",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "give data") }
            };
            request.ResponseType = typeof(Dictionary<string, string>);

            RimLLMChatClientExecutor.GenerateAsync(
                client, request, "gpt-test", useNativeSchema: true, "OpenAI", 30f).GetAwaiter().GetResult();

            // 與 raw 路徑一致：含 Dictionary 的型別仍送出 response_format，
            // 但 strict 關閉，否則服務端會拒絕開放式 map。
            Assert.IsNotNull(client.ReceivedOptions[0].ResponseFormat);
            Assert.IsFalse((bool)client.ReceivedOptions[0].AdditionalProperties["strict"]);
        }

        [Test]
        public void TestGenerateAsync_WrapsReasoningInThink()
        {
            var client = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, new List<AIContent>
                    {
                        new TextReasoningContent("step one"),
                        new TextContent("final answer")
                    }))
            };

            var request = new RimLLMRequest
            {
                ModId = "test-mod",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "solve") }
            };

            var result = RimLLMChatClientExecutor.GenerateAsync(
                client, request, "gpt-test", useNativeSchema: false, "OpenAI", 30f).GetAwaiter().GetResult();

            Assert.AreEqual("<think>\nstep one\n</think>\n\nfinal answer", result.Text);
        }

        [Test]
        public void TestGenerateAsync_InvokesCustomizeOptions()
        {
            var client = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            };

            var request = new RimLLMRequest
            {
                ModId = "test-mod",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }
            };
            bool invoked = false;

            RimLLMChatClientExecutor.GenerateAsync(
                client, request, "gpt-test", useNativeSchema: false, "OpenAI", 30f,
                options =>
                {
                    invoked = true;
                    options.Temperature = 0.9f;
                }).GetAwaiter().GetResult();

            Assert.IsTrue(invoked);
            Assert.AreEqual(0.9f, client.ReceivedOptions[0].Temperature);
        }

        [Test]
        public void TestGenerateAsync_MapsClientResultException()
        {
            var client = new CapturingChatClient
            {
                ResponseException = new TestClientResultException("bad request", 400)
            };

            var request = new RimLLMRequest
            {
                ModId = "test-mod",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }
            };

            RimLLMException ex = Assert.Throws<RimLLMException>(() =>
                RimLLMChatClientExecutor.GenerateAsync(
                    client, request, "gpt-test", useNativeSchema: false, "OpenAI", 30f).GetAwaiter().GetResult());

            Assert.AreEqual(LLMError.InvalidResponse, ex.Error);
        }

        [Test]
        public void TestGenerateAsync_MapsRateLimit()
        {
            var client = new CapturingChatClient
            {
                ResponseException = new TestClientResultException("rate limited", 429)
            };

            var request = new RimLLMRequest
            {
                ModId = "test-mod",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hi") }
            };

            RimLLMException ex = Assert.Throws<RimLLMException>(() =>
                RimLLMChatClientExecutor.GenerateAsync(
                    client, request, "gpt-test", useNativeSchema: false, "OpenAI", 30f).GetAwaiter().GetResult());

            Assert.AreEqual(LLMError.RateLimit, ex.Error);
        }

        [Test]
        public void TestStreamAsync_HandlesReasoningAndText()
        {
            var client = new CapturingChatClient
            {
                StreamUpdates =
                {
                    new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                    {
                        new TextReasoningContent("think a")
                    }),
                    new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                    {
                        new TextReasoningContent("think b")
                    }),
                    new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                    {
                        new TextContent("answer")
                    })
                }
            };

            var request = new RimLLMRequest
            {
                ModId = "test-mod",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "solve") }
            };
            var chunks = new List<string>();

            RimLLMChatClientExecutor.StreamAsync(
                client, request, "gpt-test", useNativeSchema: false, "OpenAI",
                chunks.Add, 30f).GetAwaiter().GetResult();

            Assert.AreEqual("<think>", chunks[0]);
            Assert.AreEqual("think a", chunks[1]);
            Assert.AreEqual("think b", chunks[2]);
            Assert.AreEqual("</think>", chunks[3]);
            Assert.AreEqual("answer", chunks[4]);
        }

        [Test]
        public void TestStreamAsync_CapturesUsageContent()
        {
            var usage = new UsageContent(new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7 });
            var client = new CapturingChatClient
            {
                StreamUpdates =
                {
                    new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                    {
                        new TextContent("hi")
                    }),
                    new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                    {
                        usage
                    })
                }
            };

            var request = new RimLLMRequest
            {
                ModId = "test-mod",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") }
            };
            var chunks = new List<string>();

            RimLLMChatClientExecutor.StreamAsync(
                client, request, "gpt-test", useNativeSchema: false, "OpenAI",
                chunks.Add, 30f).GetAwaiter().GetResult();

            Assert.AreEqual("hi", string.Concat(chunks));
        }
    }

    public class TestClientResultException : ClientResultException
    {
        public TestClientResultException(string message, int status) : base(message)
        {
            Status = status;
        }
    }

    public class CapturingChatClient : IChatClient
    {
        public List<IEnumerable<ChatMessage>> ReceivedMessages { get; } = new List<IEnumerable<ChatMessage>>();
        public List<ChatOptions> ReceivedOptions { get; } = new List<ChatOptions>();
        public Func<ChatResponse> ResponseFactory { get; set; } = () => new ChatResponse();
        public List<ChatResponseUpdate> StreamUpdates { get; } = new List<ChatResponseUpdate>();
        public Exception ResponseException { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages);
            ReceivedOptions.Add(options);
            if (ResponseException != null)
            {
                var tcs = new TaskCompletionSource<ChatResponse>();
                tcs.SetException(ResponseException);
                return tcs.Task;
            }
            return Task.FromResult(ResponseFactory());
        }

        public bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages);
            ReceivedOptions.Add(options);
            return new TestStreamEnumerable(StreamUpdates);
        }

        private sealed class TestStreamEnumerable : bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
        {
            private readonly List<ChatResponseUpdate> _updates;

            public TestStreamEnumerable(List<ChatResponseUpdate> updates)
            {
                _updates = updates;
            }

            public bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                return new Enumerator(_updates);
            }
        }

        private sealed class Enumerator : bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
        {
            private readonly List<ChatResponseUpdate> _updates;
            private int _index;

            public Enumerator(List<ChatResponseUpdate> updates)
            {
                _updates = updates;
            }

            public ChatResponseUpdate Current { get; private set; }

            public ste::System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
            {
                if (_index < _updates.Count)
                {
                    Current = _updates[_index];
                    _index++;
                    return new ste::System.Threading.Tasks.ValueTask<bool>(true);
                }
                Current = null;
                return new ste::System.Threading.Tasks.ValueTask<bool>(false);
            }

            public ste::System.Threading.Tasks.ValueTask DisposeAsync()
            {
                return new ste::System.Threading.Tasks.ValueTask();
            }
        }

        public object GetService(Type serviceType, object serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [TestFixture]
    public class RimLLMRequestTests
    {
        [Test]
        public void TestEffectiveSystemPrompt_PrefersCombined()
        {
            var r = new RimLLMRequest { SystemPrompt = "sys", CachedContext = "cache" };
            Assert.AreEqual("sys\n\ncache", r.GetEffectiveSystemPrompt());
        }

        [Test]
        public void TestEffectiveSystemPrompt_FallsBackToCachedContext()
        {
            var r = new RimLLMRequest { CachedContext = "cache" };
            Assert.AreEqual("cache", r.GetEffectiveSystemPrompt());
        }

        [Test]
        public void TestClone_IsDeepIndependent()
        {
            var r = new RimLLMRequest { ModId = "m", Temperature = 0.3f, ReasoningEffort = ReasoningEffort.High };
            var c = r.Clone();
            c.Temperature = 0.9f;
            Assert.AreEqual(0.3f, r.Temperature);
            Assert.AreEqual(ReasoningEffort.High, r.ReasoningEffort);
            Assert.AreEqual("m", c.ModId);
        }
    }
}
