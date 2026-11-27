#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Compat;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

[TestFixture]
public sealed class AuditFixesRegressionTests
{
    [Test]
    public void SanitizeForLog_RedactsBase64TokensAndMultiEqualsKeys()
    {
        string bearerResult = RimLLMLog.SanitizeForLog("Authorization: Bearer abcDEF1234567890+/==") ?? string.Empty;
        ClassicAssert.IsNotEmpty(bearerResult);
        ClassicAssert.IsFalse(bearerResult.Contains("abcDEF123", StringComparison.Ordinal));
        ClassicAssert.IsFalse(bearerResult.Contains("==", StringComparison.Ordinal));

        string bareBearer = RimLLMLog.SanitizeForLog("Bearer xyz9870123456789ABCDEF==") ?? string.Empty;
        ClassicAssert.IsNotEmpty(bareBearer);
        ClassicAssert.IsFalse(bareBearer.Contains("xyz987", StringComparison.Ordinal));

        string basicAuth = RimLLMLog.SanitizeForLog("Authorization: Basic dXNlcjpwYXNz==") ?? string.Empty;
        ClassicAssert.IsNotEmpty(basicAuth);
        ClassicAssert.IsFalse(basicAuth.Contains("dXNlcjpwYXNz", StringComparison.Ordinal));

        string headerKey = RimLLMLog.SanitizeForLog("x-api-key: secret=with=equals==") ?? string.Empty;
        ClassicAssert.IsNotEmpty(headerKey);
        ClassicAssert.IsFalse(headerKey.Contains("secret", StringComparison.Ordinal));
        ClassicAssert.IsFalse(headerKey.Contains("with=equals", StringComparison.Ordinal));
    }

    [Test]
    public void RimLLMLog_RedactsSecretsAtTheLoggerAndKeepsLongMultilineMessages()
    {
        TextWriter original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            string tail = new('x', 1000);
            RimLLMLog.Warning($"[RimLLM] failed: {new InvalidOperationException("Authorization: Bearer abcDEF1234567890ghijk")}\nline2 {tail}");
        }
        finally
        {
            Console.SetOut(original);
        }

        string output = captured.ToString();
        ClassicAssert.IsTrue(output.Contains("[RimLLM] failed:", StringComparison.Ordinal), "測試環境應走 Console 備援輸出。");
        ClassicAssert.IsFalse(output.Contains("abcDEF1234567890", StringComparison.Ordinal));
        ClassicAssert.IsTrue(output.Contains("\nline2", StringComparison.Ordinal), "日誌本身的換行應保留。");
        ClassicAssert.IsTrue(output.Contains(new string('x', 1000), StringComparison.Ordinal), "日誌本身不應被截斷。");
    }

    [Test]
    public void SaturatingMath_ClampsPositiveAndNegativeOverflow()
    {
        ClassicAssert.AreEqual(long.MaxValue, SaturatingMath.Add(long.MaxValue, 10));
        ClassicAssert.AreEqual(long.MinValue, SaturatingMath.Add(long.MinValue, -10));
        ClassicAssert.AreEqual(0L, SaturatingMath.Add(-5, 5));
    }

    [Test]
    public void LocalOpenAIEndpoint_RecognizesUrlsAndEnforcesTokenHostAndSlashNormalization()
    {
        string baseUrl = LocalOpenAIEndpoint.GetBaseUrl("audit.test") ?? string.Empty;
        ClassicAssert.IsNotEmpty(baseUrl);
        ClassicAssert.IsTrue(baseUrl.Contains("/t/", StringComparison.Ordinal));
        ClassicAssert.IsTrue(baseUrl.Contains("/m/audit.test/v1", StringComparison.Ordinal));

        ClassicAssert.IsTrue(LocalOpenAIEndpoint.IsOwnBaseUrl(baseUrl, "audit.test"));
        ClassicAssert.IsTrue(LocalOpenAIEndpoint.IsOwnBaseUrl("http://127.0.0.1:12345/m/audit.test/v1", "audit.test"));
        ClassicAssert.IsFalse(LocalOpenAIEndpoint.IsOwnBaseUrl("http://127.0.0.1:8000/v1", "audit.test"));
        ClassicAssert.IsFalse(LocalOpenAIEndpoint.IsOwnBaseUrl(baseUrl: null, "audit.test"));

        var baseUri = new Uri(baseUrl + "/");
        using var http = new HttpClient();

        using HttpResponseMessage wrongToken = http.GetAsync(
            $"http://{baseUri.Authority}/t/wrongtoken/m/audit.test/v1/models").GetAwaiter().GetResult();
        ClassicAssert.AreEqual(HttpStatusCode.Forbidden, wrongToken.StatusCode);

        using HttpResponseMessage legacyPath = http.GetAsync(
            $"http://{baseUri.Authority}/m/audit.test/v1/models").GetAwaiter().GetResult();
        ClassicAssert.AreEqual(HttpStatusCode.Forbidden, legacyPath.StatusCode);

        using var badHostRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        badHostRequest.Headers.Host = "evil.example:80";
        using HttpResponseMessage badHost = http.SendAsync(badHostRequest).GetAwaiter().GetResult();
        ClassicAssert.AreEqual(HttpStatusCode.Forbidden, badHost.StatusCode);

        // 重複斜線應正確正規化到端點判斷（未註冊的 mod 回 404 且訊息為無 handler，而非 Unknown path）。
        string unregisteredDoubleSlash = baseUrl.Replace("/m/audit.test/", "/m/unregistered.audit.test/", StringComparison.Ordinal) + "//models";
        using HttpResponseMessage doubleSlash = http.GetAsync(unregisteredDoubleSlash).GetAwaiter().GetResult();
        ClassicAssert.AreEqual(HttpStatusCode.NotFound, doubleSlash.StatusCode);
        string body = doubleSlash.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        ClassicAssert.IsTrue(body.Contains("No handler registered", StringComparison.Ordinal));
    }

    [TestCase(30f, 30f)]
    [TestCase(45f, 45f)]
    [TestCase(1f, 5f)]
    [TestCase(600f, 120f)]
    [TestCase(0f, 100f)]
    [TestCase(float.NaN, 100f)]
    public void SanitizeApiTimeout_KeepsUserValueWithinSliderRange(float stored, float expected)
    {
        ClassicAssert.AreEqual(expected, RimLLMFrameworkSettings.SanitizeApiTimeout(stored));
    }

    [Test]
    public async Task LocalOpenAIEndpoint_StopCancelsInFlightHandler()
    {
        const string modId = "audit.stop.test";
        System.Reflection.PropertyInfo settingsProperty = typeof(RimLLMFrameworkMod).GetProperty(
            "Settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        object? previousSettings = settingsProperty.GetValue(null);
        var settings = new RimLLMFrameworkSettings();
        settings.SetCompatTakeoverEnabled(modId, true);
        settingsProperty.SetValue(null, settings);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LocalOpenAIEndpoint.Register(modId, async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            return "{}";
        });

        try
        {
            string baseUrl = LocalOpenAIEndpoint.GetBaseUrl(modId) ?? string.Empty;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            Task<HttpResponseMessage> request = http.PostAsync($"{baseUrl}/chat/completions", new StringContent("{}"));

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            LocalOpenAIEndpoint.Stop();

            Task finished = await Task.WhenAny(cancelled.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            ClassicAssert.AreSame(cancelled.Task, finished, "Stop() 後進行中的 handler 應收到取消。");
            try
            {
                using HttpResponseMessage _ = await request.ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // 監聽器關閉時連線可能直接中斷。
            }
        }
        finally
        {
            settingsProperty.SetValue(null, previousSettings);
        }
    }

    [Test]
    public async Task LocalOpenAIEndpoint_StopClosesListenerEvenWhenCancellationCallbackThrows()
    {
        const string modId = "audit.stop.throw.test";
        System.Reflection.PropertyInfo settingsProperty = typeof(RimLLMFrameworkMod).GetProperty(
            "Settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        object? previousSettings = settingsProperty.GetValue(null);
        var settings = new RimLLMFrameworkSettings();
        settings.SetCompatTakeoverEnabled(modId, true);
        settingsProperty.SetValue(null, settings);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LocalOpenAIEndpoint.Register(modId, async (_, _, cancellationToken) =>
        {
            cancellationToken.Register(() => throw new InvalidOperationException("callback failure"));
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return "{}";
        });

        try
        {
            var baseUri = new Uri(LocalOpenAIEndpoint.GetBaseUrl(modId) ?? string.Empty);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            Task<HttpResponseMessage> request = http.PostAsync($"{baseUri}/chat/completions", new StringContent("{}"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            using (var before = new System.Net.Sockets.TcpClient())
            {
                await before.ConnectAsync(IPAddress.Loopback, baseUri.Port).ConfigureAwait(false);
            }

            LocalOpenAIEndpoint.Stop();

            using var probe = new System.Net.Sockets.TcpClient();
            await Assert.CatchAsync<System.Net.Sockets.SocketException>(
                async () => await probe.ConnectAsync(IPAddress.Loopback, baseUri.Port).ConfigureAwait(false),
                "取消回呼擲錯時，監聽器仍須關閉。");
            try
            {
                using HttpResponseMessage _ = await request.ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // 監聽器關閉時連線可能直接中斷。
            }
        }
        finally
        {
            settingsProperty.SetValue(null, previousSettings);
        }
    }

    [Test]
    public async Task GuardChatClient_ReEnumeratingStreamReRunsAntiAbuseCheck()
    {
        var settings = new MockSettings
        {
            EnableAntiAbuse = true,
            MaxRequestsPerWindow = 1,
            ThrottlingWindowSeconds = 60,
            CoolDownDurationSeconds = 60
        };
        var throttleStore = new RimLLMThrottleStore(settings);
        var usageTracker = new RimLLMUsageTracker(settings);
        using var inner = new MinimalStreamClient();
        using var guard = new RimLLMGuardChatClient(inner, settings, throttleStore, "guard.reenum.test", usageTracker);

        IAsyncEnumerable<ChatResponseUpdate> stream = guard.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var chunks = new List<string>();
        await foreach (ChatResponseUpdate update in stream)
        {
            if (!string.IsNullOrEmpty(update.Text)) chunks.Add(update.Text);
        }
        ClassicAssert.AreEqual("ok", string.Concat(chunks));

        RimLLMException? ex = await Assert.ThrowsAsync<RimLLMException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in stream)
            {
            }
        });
        if (ex is null) { Assert.Fail("Expected RimLLMException."); return; }
        ClassicAssert.AreEqual(LLMError.RateLimit, ex.Error);
    }

    [Test]
    public void TelemetryStore_DirtyStateAndNegativeCounterClamping()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RimLLM_AuditTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, "telemetry.json");
        Func<string> prev = RimLLMTelemetryStore.FilePathResolver;
        try
        {
            RimLLMTelemetryStore.FilePathResolver = () => path;

            var store = new RimLLMTelemetryStore();
            ClassicAssert.IsFalse(store.IsDirty);
            store.MarkDirty();
            ClassicAssert.IsTrue(store.IsDirty);
            store.Save();
            ClassicAssert.IsFalse(store.IsDirty);

            File.WriteAllText(path, """
            {
              "RequestLogs": [],
              "TotalPromptTokens": -100,
              "TotalCompletionTokens": -50,
              "TotalEstimatedCost": -2.5,
              "DailyAccumulatedCost": -1.25,
              "DailyAccumulatedTokens": -99
            }
            """);

            var loaded = new RimLLMTelemetryStore();
            loaded.Load();
            ClassicAssert.AreEqual(0L, loaded.TotalPromptTokens);
            ClassicAssert.AreEqual(0L, loaded.TotalCompletionTokens);
            ClassicAssert.AreEqual(0f, loaded.TotalEstimatedCost);
            ClassicAssert.AreEqual(0f, loaded.DailyAccumulatedCost);
            ClassicAssert.AreEqual(0L, loaded.DailyAccumulatedTokens);
        }
        finally
        {
            RimLLMTelemetryStore.FilePathResolver = prev;
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Test]
    public void OpenAIProvider_NormalizesEndpointsModelsAndInvalidUri()
    {
        ClassicAssert.AreEqual("https://api.openai.com/v1", OpenAIProvider.NormalizeEndpoint("https://api.openai.com/v1/responses/"));
        ClassicAssert.AreEqual("https://api.openai.com/v1", OpenAIProvider.NormalizeEndpoint("https://api.openai.com/v1/chat/completions"));
        ClassicAssert.IsTrue(ReasoningModelProbe.CheckReasoning("openrouter/openai/o3-mini, openai/gpt-4o"));

        RimLLMException? ex = Assert.Throws<RimLLMException>(() =>
            OpenAIProvider.CreateOpenAiChatClient("test-key", "gpt-4o", "not a valid uri"));
        if (ex is null) { Assert.Fail("Expected RimLLMException."); return; }
        ClassicAssert.AreEqual(LLMError.InvalidResponse, ex.Error);
    }

    [Test]
    public void FallbackPipeline_StrictPreferredResolvesPinnedModelWithEmptyFallbackChainAndDifferentCasing()
    {
        var settings = new MockSettings
        {
            FallbackChain = []
        };
        settings.EnabledProviders["OpenAI"] = true;
        settings.ApiKeys["OpenAI"] = "test-key";

        var ledger = new RimLLMHealthLedger();
        var tracker = new RimLLMUsageTracker(settings);
        var provider = new TestOpenAIProvider(settings);
        var pipeline = new RimLLMFallbackPipeline(
            settings,
            ledger,
            tracker,
            id => string.Equals(id, ProviderIds.OpenAI, StringComparison.OrdinalIgnoreCase) ? provider : null,
            id => settings.IsProviderEnabled(id));

        List<RimLLMFallbackPipeline.ResolvedCandidate> candidates = pipeline.ResolveCandidates(
            "openai:gpt-4o",
            minFallbackLevel: null,
            strictPreferred: true);

        ClassicAssert.AreEqual(1, candidates.Count);
        ClassicAssert.AreEqual(ProviderIds.OpenAI, candidates[0].ProviderId);
        ClassicAssert.AreEqual("gpt-4o", candidates[0].ModelName);
    }

    [Test]
    public void SchemaBuilderAndJsonHelper_HandleNullableStructProseExtractionAndNumericArrays()
    {
        RimLLMSchemaResult result = RimLLMSchemaBuilder.Build(typeof(NullableStructEnvelope));
        JsonNode? root = JsonNode.Parse(result.Json);
        JsonNode? pointNode = root?["properties"]?["Point"];
        if (pointNode is null) { Assert.Fail("Expected Point schema node."); return; }
        ClassicAssert.AreEqual("Optional point", pointNode["description"]?.GetValue<string>());

        string proseResponse = "Thinking... Here is result:\n```json\n{\"Name\":\"ok\",\"Count\":3}\n```";
        ClassicAssert.AreEqual("{\"Name\":\"ok\",\"Count\":3}", RimLLMJsonHelper.RepairJson(proseResponse));

        string multipleBlocks = "prefix {\"Name\":\"ok\",\"Nested\":{\"X\":1}} and {\"Second\":2}";
        ClassicAssert.AreEqual("{\"Name\":\"ok\",\"Nested\":{\"X\":1}}", RimLLMJsonHelper.ExtractJsonBlock(multipleBlocks));

        string sample = RimLLMJsonHelper.GetSampleJson<NumericSamples>();
        NumericSamples? parsed = RimLLMJsonHelper.Deserialize<NumericSamples>(sample);
        if (parsed is null) { Assert.Fail("Expected deserialized NumericSamples."); return; }
        ClassicAssert.IsNotEmpty(parsed.Floats);
        ClassicAssert.IsNotEmpty(parsed.FloatList);
        ClassicAssert.IsNotEmpty(parsed.Longs);
        ClassicAssert.IsNotEmpty(parsed.Decimals);
        ClassicAssert.AreEqual(0m, parsed.Amount);
    }

    [Test]
    public void LLMErrorMapper_ClassifiesContextWindowThinking429CapacityAndSafetyIdentifierCorrectly()
    {
        RimLLMException thinkingWindow = LLMErrorMapper.CreateException(
            400,
            "context_length_exceeded: too many thinking tokens");
        ClassicAssert.AreEqual(LLMError.ContextWindowExceeded, thinkingWindow.Error);

        RimLLMException payloadTooLarge = LLMErrorMapper.CreateException(413, "Payload too large");
        ClassicAssert.AreEqual(LLMError.ContextWindowExceeded, payloadTooLarge.Error);

        RimLLMException capacityLimit = LLMErrorMapper.CreateException(429, "insufficient capacity for request");
        ClassicAssert.AreEqual(LLMError.RateLimit, capacityLimit.Error);

        RimLLMException safetyFieldError = LLMErrorMapper.CreateException(400, "Unknown parameter: 'safety_identifier'");
        ClassicAssert.AreNotEqual(LLMError.ContentFilter, safetyFieldError.Error);
    }

    [Test]
    public void MarkdownTableAndDispatcher_HandleIndentedTableNarrowFitAndSyncContext()
    {
        string indented = "    | a | b |\n    |---|---|\n    | 1 | 2 |";
        List<RimLLMMarkdownSegment> segments = RimLLMMarkdownTable.Split(indented);
        ClassicAssert.AreEqual(1, segments.Count);
        ClassicAssert.IsFalse(segments[0].IsTable);
        ClassicAssert.AreEqual(indented, segments[0].Markdown);

        float[] narrow = RimLLMMarkdownTable.FitColumns([100f, 100f, 100f], 0f);
        ClassicAssert.AreEqual(3, narrow.Length);
        foreach (float width in narrow)
        {
            ClassicAssert.GreaterOrEqual(width, 1f);
        }

        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(syncContext: null);
        try
        {
            SynchronizationContext? observedInside = null;
            bool enqueued = RimLLMDispatcher.TryEnqueueBounded(() =>
            {
                observedInside = SynchronizationContext.Current;
            });
            ClassicAssert.IsTrue(enqueued);
            int drained = RimLLMDispatcher.DrainWithBudget(16, 1000);
            ClassicAssert.GreaterOrEqual(drained, 1);
            ClassicAssert.IsNotNull(observedInside);
            ClassicAssert.IsNull(SynchronizationContext.Current);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private struct SamplePoint
    {
        public int X { get; set; }
    }

    private sealed class NullableStructEnvelope
    {
        [System.ComponentModel.Description("Optional point")]
        public SamplePoint? Point { get; set; }
    }

    private sealed class NumericSamples
    {
        public float[] Floats { get; set; } = [];
        public List<float> FloatList { get; set; } = [];
        public long[] Longs { get; set; } = [];
        public decimal[] Decimals { get; set; } = [];
        public decimal Amount { get; set; }
    }

    private sealed class ReasoningModelProbe(IRimLLMSettings settings) : OpenAIProvider(settings)
    {
        public static bool CheckReasoning(string modelName) => IsOpenAiReasoningModel(modelName);
    }

    private sealed class MinimalStreamClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("minimal");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
