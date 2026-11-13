using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Api;
using RimLLM_Framework.Compat;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 2026-11-06 全面審查發現的回歸測試。錯誤路徑的問題用假 client 測不出來，
/// 因此 SDK 重試與錯誤碼的部分走真實的 OpenAI SDK 與本機假伺服器。
/// </summary>
[TestFixture]
public sealed class ReviewFindingsRegressionTests
{
    private static readonly JsonElement SampleSchema =
        JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}}}").RootElement;

    [SetUp]
    [TearDown]
    public void ResetLearnedSupport()
    {
        RimLLMReasoningSupport.Reset();
    }

    #region 真實 SDK 傳輸

    [TestCase(429)]
    [TestCase(500)]
    public async Task RealTransport_SdkDoesNotRetryOnItsOwn(int status)
    {
        using var server = new FakeHttpServer(status, retryAfter: "1");
        var settings = new MockSettings { RoutingStrategy = 0, MaxRetries = 0, RetryDelay = 0f, ApiTimeout = 30f };
        settings.FallbackChain = [ProviderIds.OpenAICompatible + ":local-model"];
        settings.EnabledProviders[ProviderIds.OpenAICompatible] = true;
        settings.Endpoints[ProviderIds.OpenAICompatible] = server.Endpoint;
        var manager = new RimLLMManager(settings);

        RimLLMException ex = await Assert.CatchAsync<RimLLMException>(async () =>
            await manager.CreateChatClient("test.transport")
                .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        ClassicAssert.AreEqual(1, server.RequestCount, "重試必須只由框架負責，SDK 不得自行放大請求");
        ClassicAssert.AreEqual(status, ex!.HttpStatusCode);
        ClassicAssert.AreEqual(status == 429 ? LLMError.RateLimit : LLMError.ProviderOffline, ex.Error);
        if (status == 429)
        {
            ClassicAssert.AreEqual(TimeSpan.FromSeconds(1), ex.RetryAfter);
        }
    }

    [Test]
    public async Task RealTransport_LongRetryAfterSurfacesImmediatelyAsRateLimit()
    {
        using var server = new FakeHttpServer(429, retryAfter: "120");
        var settings = new MockSettings { RoutingStrategy = 0, MaxRetries = 3, RetryDelay = 0f, ApiTimeout = 30f };
        settings.FallbackChain = [ProviderIds.OpenAICompatible + ":local-model"];
        settings.EnabledProviders[ProviderIds.OpenAICompatible] = true;
        settings.Endpoints[ProviderIds.OpenAICompatible] = server.Endpoint;
        var manager = new RimLLMManager(settings);

        DateTime started = DateTime.UtcNow;
        RimLLMException ex = await Assert.CatchAsync<RimLLMException>(async () =>
            await manager.CreateChatClient("test.transport")
                .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        ClassicAssert.AreEqual(LLMError.RateLimit, ex!.Error, "伺服器的限流答覆不得被等待逾時蓋掉");
        ClassicAssert.AreEqual(TimeSpan.FromSeconds(120), ex.RetryAfter);
        ClassicAssert.AreEqual(1, server.RequestCount);
        ClassicAssert.Less((DateTime.UtcNow - started).TotalSeconds, 10);
    }

    [Test]
    public void SdkStillHasPatchFieldsUsedByReflection()
    {
        ClassicAssert.IsTrue(OpenAIPatchExtensions.CanDisablePropagators,
            "升級 OpenAI SDK 後私有欄位若改名，這段反射會失效，必須重新檢查");
    }

    #endregion

    #region 備援與錯誤碼

    [TestCase(false)]
    [TestCase(true)]
    public async Task Exhausted_KeepsLastErrorCodeAndDetails(bool streaming)
    {
        var settings = new MockSettings { RoutingStrategy = 0, MaxRetries = 0, RetryDelay = 0f };
        var provider = new ScriptedProvider("P1")
        {
            Responder = _ => throw LLMErrorMapper.CreateException(429, "insufficient_quota", TimeSpan.FromSeconds(7))
        };
        IChatClient client = BuildManager(settings, provider).CreateChatClient("test.exhausted");

        RimLLMException ex = await Assert.CatchAsync<RimLLMException>(async () =>
        {
            if (streaming)
            {
                await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
                {
                }
            }
            else
            {
                await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
            }
        });

        ClassicAssert.AreEqual(LLMError.QuotaExceeded, ex!.Error);
        ClassicAssert.AreEqual(429, ex.HttpStatusCode);
        ClassicAssert.AreEqual(TimeSpan.FromSeconds(7), ex.RetryAfter);
        StringAssert.Contains("All fallback attempts failed", ex.Message);
    }

    [Test]
    public async Task RetryAfterBeyondLimit_SwitchesToNextCandidateWithoutRetrying()
    {
        var settings = new MockSettings { RoutingStrategy = 0, MaxRetries = 3, RetryDelay = 0f };
        var limited = new ScriptedProvider("P1")
        {
            Responder = _ => throw LLMErrorMapper.CreateException(429, "limited", TimeSpan.FromMinutes(5))
        };
        var healthy = new ScriptedProvider("P2");

        ChatResponse response = await BuildManager(settings, limited, healthy).CreateChatClient("test.limit")
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        ClassicAssert.AreEqual("P2", response.Text);
        ClassicAssert.AreEqual(1, limited.RequestCount);
    }

    [Test]
    public async Task ProviderCasingInPinnedModelIsIgnored()
    {
        var settings = new MockSettings { RoutingStrategy = 0, MaxRetries = 0, RetryDelay = 0f };
        var first = new ScriptedProvider("P1");
        var pinned = new ScriptedProvider("P2");

        ChatResponse response = await BuildManager(settings, first, pinned).CreateChatClient("test.casing")
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "p2:model" });

        ClassicAssert.AreEqual("P2", response.Text);
    }

    [TestCase("insufficient_quota", false)]
    [TestCase("You exceeded your current quota for requests per minute", true)]
    public void InsufficientQuotaIsNotRetriedButPerMinuteQuotaIs(string body, bool retryable)
    {
        RimLLMException ex = LLMErrorMapper.CreateException(429, body);

        ClassicAssert.AreEqual(LLMError.QuotaExceeded, ex.Error);
        ClassicAssert.AreEqual(retryable, RimLLMFallbackPipeline.IsRetryableException(ex));
    }

    #endregion

    #region 供應商統計

    [Test]
    public async Task FailedCandidateIsCountedUnderItsOwnProvider()
    {
        var settings = new MockSettings { RoutingStrategy = 0, MaxRetries = 1, RetryDelay = 0f };
        var failing = new ScriptedProvider("P1")
        {
            Responder = _ => throw new RimLLMException(LLMError.ProviderOffline, "down")
        };
        var healthy = new ScriptedProvider("P2");
        RimLLMManager manager = BuildManager(settings, failing, healthy);

        await manager.CreateChatClient("test.stats").GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        ClassicAssert.AreEqual(2, failing.RequestCount);
        ClassicAssert.AreEqual(1, manager.UsageTracker.ProviderStatistics["P1"].FailureCount, "同一候選的重試合計一筆");
        ClassicAssert.AreEqual(1, manager.UsageTracker.ProviderStatistics["P2"].SuccessCount);
    }

    [Test]
    public async Task MidStreamFailureIsCountedUnderItsProvider()
    {
        var settings = new MockSettings { RoutingStrategy = 0, MaxRetries = 0, RetryDelay = 0f };
        var provider = new ScriptedProvider("P1")
        {
            StreamResponder = FailAfterFirstChunk
        };
        RimLLMManager manager = BuildManager(settings, provider);

        await Assert.CatchAsync<RimLLMException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in manager.CreateChatClient("test.stats")
                .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
            }
        });

        ClassicAssert.AreEqual(1, manager.UsageTracker.ProviderStatistics["P1"].FailureCount);
    }

    [Test]
    public void CorruptTelemetryEntriesDoNotBreakTrackerConstruction()
    {
        var settings = new MockSettings
        {
            RequestLogs = [null, new RimLLMManager.RequestLogEntry { Provider = null, Success = false }]
        };

        Assert.DoesNotThrow(() => _ = new RimLLMUsageTracker(settings));
    }

    #endregion

    #region 只有推理內容的回應

    [Test]
    public async Task ReasoningOnlyResponse_FailsOverToNextCandidate()
    {
        var settings = new MockSettings { RoutingStrategy = 0, MaxRetries = 0, RetryDelay = 0f };
        var thinker = new ScriptedProvider("P1")
        {
            Responder = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("thinking...")]))
        };
        var healthy = new ScriptedProvider("P2");
        RimLLMManager manager = BuildManager(settings, thinker, healthy);

        ChatResponse response = await manager.CreateChatClient("test.reasoning")
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        ClassicAssert.AreEqual("P2", response.Text);
        ClassicAssert.AreEqual(1, manager.UsageTracker.ProviderStatistics["P1"].FailureCount);
    }

    [Test]
    public async Task ReasoningOnlyStream_ReportsMissingAnswer()
    {
        var client = new ScriptedClient(new ScriptedProvider("P1") { StreamResponder = ReasoningOnlyStream });

        RimLLMException ex = await Assert.CatchAsync<RimLLMException>(() => RimLLMChatClientExecutor.StreamAsync(
            client, [new ChatMessage(ChatRole.User, "hi")], null, "model", false, "P1", null, 30f, CancellationToken.None));

        ClassicAssert.AreEqual(LLMError.InvalidResponse, ex!.Error);
        StringAssert.Contains("MaxOutputTokens", ex.Message);
    }

    #endregion

    #region 串流閒置逾時

    [Test]
    public async Task StreamIdleTimeout_DoesNotCountTimeWaitingForConsumer()
    {
        var client = new ScriptedClient(new ScriptedProvider("P1") { StreamResponder = TwoChunks });
        int received = 0;

        await RimLLMChatClientExecutor.StreamAsync(
            client,
            [new ChatMessage(ChatRole.User, "hi")],
            null,
            "model",
            false,
            "P1",
            async _ =>
            {
                received++;
                await Task.Delay(400);
            },
            0.1f,
            CancellationToken.None);

        ClassicAssert.AreEqual(2, received);
    }

    [Test]
    public async Task StreamIdleTimeout_StillFiresWhenProviderStalls()
    {
        var client = new ScriptedClient(new ScriptedProvider("P1") { StreamResponder = StallAfterFirstChunk });

        RimLLMException ex = await Assert.CatchAsync<RimLLMException>(() => RimLLMChatClientExecutor.StreamAsync(
            client, [new ChatMessage(ChatRole.User, "hi")], null, "model", false, "P1", null, 0.1f, CancellationToken.None));

        ClassicAssert.AreEqual(LLMError.Timeout, ex!.Error);
    }

    #endregion

    #region 呼叫端的 ResponseFormat

    [Test]
    public async Task CallerSchema_IsForwardedToNativeProvider()
    {
        var provider = new ScriptedProvider("P1", nativeSchema: true);
        var client = new RimLLMProviderChatClient(provider, "model", new MockSettings { EnableNativeSchema = true });

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(SampleSchema, "answer") });

        var format = provider.LastOptions.ResponseFormat as ChatResponseFormatJson;
        ClassicAssert.IsNotNull(format?.Schema, "原生供應商必須收到呼叫端的 schema");
    }

    [Test]
    public async Task CallerSchema_IsDowngradedForProviderWithoutNativeSchema()
    {
        var provider = new ScriptedProvider("P1", nativeSchema: false);
        var client = new RimLLMProviderChatClient(provider, "model", new MockSettings { EnableNativeSchema = true });

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(SampleSchema, "answer") });

        var format = provider.LastOptions.ResponseFormat as ChatResponseFormatJson;
        ClassicAssert.IsNotNull(format, "仍要維持 JSON 模式");
        ClassicAssert.IsNull(format!.Schema, "不支援的供應商不得收到 json_schema");
        StringAssert.Contains("\"answer\"", provider.LastMessages.First(m => m.Role == ChatRole.System).Text);
    }

    [Test]
    public async Task CallerSchema_RejectedByProvider_RetriesWithPromptInstructions()
    {
        int calls = 0;
        var provider = new ScriptedProvider("P1", nativeSchema: true);
        provider.Responder = _ =>
        {
            if (++calls == 1)
            {
                throw LLMErrorMapper.CreateException(400, "response_format json_schema is unsupported");
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"));
        };
        var client = new RimLLMProviderChatClient(provider, "model", new MockSettings { EnableNativeSchema = true });

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(SampleSchema, "answer") });

        ClassicAssert.AreEqual("{}", response.Text);
        ClassicAssert.AreEqual(2, calls);
        ClassicAssert.IsNull((provider.LastOptions.ResponseFormat as ChatResponseFormatJson)?.Schema);
    }

    [Test]
    public void ScAutoPayload_ChatResponseFormatIsForwarded()
    {
        const string json = "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]," +
                            "\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"n\",\"strict\":true,\"schema\":{\"type\":\"object\"}}}}";

        OpenAICompatClient.RequestSpec spec = OpenAICompatClient.ParsePayload(json, chatProtocol: true);

        var format = spec.ResponseFormat as ChatResponseFormatJson;
        ClassicAssert.AreEqual("object", format?.Schema?.GetProperty("type").GetString());
        ClassicAssert.IsTrue(spec.StrictSchema);
    }

    [Test]
    public void ScAutoPayload_ResponsesTextFormatIsForwarded()
    {
        const string schemaJson = "{\"input\":\"hi\",\"text\":{\"format\":{\"type\":\"json_schema\",\"name\":\"n\",\"schema\":{\"type\":\"object\"}}}}";
        const string objectJson = "{\"input\":\"hi\",\"text\":{\"format\":{\"type\":\"json_object\"}}}";

        var schemaFormat = OpenAICompatClient.ParsePayload(schemaJson, chatProtocol: false).ResponseFormat as ChatResponseFormatJson;
        var objectFormat = OpenAICompatClient.ParsePayload(objectJson, chatProtocol: false).ResponseFormat as ChatResponseFormatJson;

        ClassicAssert.IsNotNull(schemaFormat?.Schema);
        ClassicAssert.IsNotNull(objectFormat);
        ClassicAssert.IsNull(objectFormat!.Schema);
    }

    #endregion

    #region 請求參數

    [TestCase("Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.", true)]
    [TestCase("max_tokens is too large: 999999", false)]
    public void MaxTokensRejectionIsOnlyDetectedForTheFieldItself(string body, bool expected)
    {
        ClassicAssert.AreEqual(expected, LLMErrorMapper.CreateException(400, body).IsMaxTokensRejection);
    }

    [Test]
    public void LearnedMaxTokensRejection_StopsRewritingTheField()
    {
        // 假傳輸只保留第一個請求，所以每次送出都用新的供應商實例。
        var options = new ChatOptions { MaxOutputTokens = 50, AdditionalProperties = [] };

        TestDeepSeekPayloadProvider before = BuildPayloadProvider();
        before.GenerateAsync([new ChatMessage(ChatRole.User, "hi")], options, "future-model").GetAwaiter().GetResult();
        ClassicAssert.IsNotNull(JsonNode.Parse(before.CapturedPayload)!["max_tokens"]);

        TestDeepSeekPayloadProvider after = BuildPayloadProvider();
        RimLLMReasoningSupport.MarkMaxTokensUnsupported(after.ProviderId, "future-model");
        after.GenerateAsync([new ChatMessage(ChatRole.User, "hi")], options, "future-model").GetAwaiter().GetResult();
        JsonNode payload = JsonNode.Parse(after.CapturedPayload)!;
        ClassicAssert.IsNull(payload["max_tokens"]);
        ClassicAssert.AreEqual(50, payload["max_completion_tokens"]?.GetValue<int>());
    }

    [Test]
    public void ReasoningEffortExtremes_AreNotSilentlyDropped()
    {
        TestDeepSeekPayloadProvider extraHigh = BuildPayloadProvider();
        extraHigh.GenerateAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } },
            "deepseek-chat").GetAwaiter().GetResult();
        ClassicAssert.AreEqual("xhigh", JsonNode.Parse(extraHigh.CapturedPayload)!["reasoning_effort"]?.ToString());

        TestDeepSeekPayloadProvider none = BuildPayloadProvider();
        none.GenerateAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } },
            "deepseek-chat").GetAwaiter().GetResult();
        ClassicAssert.AreEqual("disabled", JsonNode.Parse(none.CapturedPayload)!["thinking"]?["type"]?.ToString());
    }

    #endregion

    #region 預算

    [Test]
    public void BudgetReservationContention_IsTransientAndDoesNotUseUpTheDailyWarning()
    {
        var settings = new MockSettings
        {
            DailyTokenBudgetLimit = 100,
            DailyAccumulatedTokens = 0,
            DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd"),
            BudgetPolicy = 0
        };
        var tracker = new RimLLMUsageTracker(settings);

        using RimLLMUsageTracker.BudgetReservation holder = tracker.ReserveBudgetOrThrow(100);
        RimLLMException ex = Assert.Throws<RimLLMException>(() => tracker.ReserveBudgetOrThrow(1));

        ClassicAssert.AreEqual(LLMError.RateLimit, ex!.Error, "在途預留不是超限");
        ClassicAssert.IsNotNull(ex.RetryAfter);
        ClassicAssert.IsTrue(tracker.ShouldWarnBudgetExceeded(), "當天第一次真正超限時仍必須警告");
    }

    [Test]
    public void TrulyExceededBudget_IsQuotaExceeded()
    {
        var settings = new MockSettings
        {
            DailyTokenBudgetLimit = 100,
            DailyAccumulatedTokens = 100,
            DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd"),
            BudgetPolicy = 0
        };
        var tracker = new RimLLMUsageTracker(settings);

        RimLLMException ex = Assert.Throws<RimLLMException>(() => tracker.ReserveBudgetOrThrow(1));

        ClassicAssert.AreEqual(LLMError.QuotaExceeded, ex!.Error);
    }

    #endregion

    #region 其他

    [Test]
    public void StartupNotices_AreHeldUntilGameDataIsReadyAndDeduplicated()
    {
        RimLLMStartupNotices.ResetForTests();
        try
        {
            RimLLMStartupNotices.Post("RimLLM_MsgApiKeyDecryptFailed", "OpenAI");
            RimLLMStartupNotices.Post("RimLLM_MsgApiKeyDecryptFailed", "OpenAI");
            RimLLMStartupNotices.Post("RimLLM_MsgApiKeyDecryptFailed", "Gemini");
            ClassicAssert.AreEqual(2, RimLLMStartupNotices.PendingCountForTests);

            RimLLMStartupNotices.Flush();
            ClassicAssert.AreEqual(0, RimLLMStartupNotices.PendingCountForTests);
        }
        finally
        {
            RimLLMStartupNotices.ResetForTests();
        }
    }

    [Test]
    public void ThrottleStore_DropsExpiredModIds()
    {
        var settings = new MockSettings { MaxRequestsPerWindow = 100000, ThrottlingWindowSeconds = 0, CoolDownDurationSeconds = 1 };
        var store = new RimLLMThrottleStore(settings);

        for (int i = 0; i < RimLLMThrottleStore.MaxTrackedModIds; i++)
        {
            store.CheckAntiAbuse("rotated." + i);
        }
        Thread.Sleep(20);
        store.CheckAntiAbuse("rotated.next");

        ClassicAssert.Less(store.TrackedModCount, RimLLMThrottleStore.MaxTrackedModIds);
    }

    #endregion

    private static TestDeepSeekPayloadProvider BuildPayloadProvider()
    {
        var settings = new MockSettings { EnableNativeSchema = true };
        settings.EnabledProviders["DeepSeek"] = true;
        settings.ApiKeys["DeepSeek"] = "key";
        return new TestDeepSeekPayloadProvider(settings);
    }

    private static RimLLMManager BuildManager(MockSettings settings, params ScriptedProvider[] providers)
    {
        settings.FallbackChain = providers.Select(p => p.ProviderId + ":model").ToList();
        var manager = new RimLLMManager(settings);
        foreach (ScriptedProvider provider in providers)
        {
            settings.EnabledProviders[provider.ProviderId] = true;
            settings.ApiKeys[provider.ProviderId] = "test-key";
            manager.RegisterProvider(provider);
        }
        return manager;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FailAfterFirstChunk(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
        await Task.Yield();
        throw new RimLLMException(LLMError.ProviderOffline, "connection lost");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ReasoningOnlyStream(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thinking...")]);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TwoChunks(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "one");
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "two");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StallAfterFirstChunk(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "one");
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    /// <summary>可逐案例指定回應的假供應商，並記下最後一次收到的請求。</summary>
    private sealed class ScriptedProvider : ILLMProvider
    {
        public ScriptedProvider(string providerId, bool nativeSchema = false)
        {
            ProviderId = providerId;
            Capabilities = new LLMProviderCapabilities
            {
                SupportsStreaming = true,
                SupportsFunctionCalling = true,
                SupportsUsageMetadata = true,
                SupportsNativeStructuredOutput = nativeSchema
            };
        }

        public string ProviderId { get; }
        public bool RequiresApiKey => true;
        public LLMProviderCapabilities Capabilities { get; }
        public Func<ChatOptions, ChatResponse> Responder { get; set; }
        public Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> StreamResponder { get; set; }
        public int RequestCount;
        public ChatOptions LastOptions;
        public List<ChatMessage> LastMessages;

        public IChatClient CreateChatClient(string model) => new ScriptedClient(this);
        public Task<TestResult> TestConnectionAsync() => Task.FromResult(new TestResult { Success = true });
        public Task<List<string>> FetchAvailableModelsAsync() => Task.FromResult(new List<string> { "model" });
    }

    private sealed class ScriptedClient : IChatClient
    {
        private readonly ScriptedProvider _provider;

        public ScriptedClient(ScriptedProvider provider) => _provider = provider;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _provider.RequestCount);
            _provider.LastOptions = options;
            _provider.LastMessages = messages.ToList();
            return Task.FromResult(_provider.Responder != null
                ? _provider.Responder(options)
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, _provider.ProviderId)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _provider.RequestCount);
            if (_provider.StreamResponder != null)
            {
                return _provider.StreamResponder(cancellationToken);
            }
            if (_provider.Responder != null)
            {
                _provider.Responder(options);
            }
            throw new NotSupportedException();
        }

        public object GetService(Type serviceType, object serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>對所有請求回同一個狀態碼的本機假伺服器，並計算收到幾個請求。</summary>
    private sealed class FakeHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly int _status;
        private readonly string _retryAfter;
        private int _requestCount;

        public FakeHttpServer(int status, string retryAfter)
        {
            _status = status;
            _retryAfter = retryAfter;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Endpoint = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Endpoint { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task ServeAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    Interlocked.Increment(ref _requestCount);
                    context.Response.StatusCode = _status;
                    if (_retryAfter != null)
                    {
                        context.Response.Headers["Retry-After"] = _retryAfter;
                    }
                    byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"fake\"}}");
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException)
            {
                // 測試結束關閉監聽時的正常結果。
            }
        }

        public void Dispose()
        {
            _listener.Close();
        }
    }
}
