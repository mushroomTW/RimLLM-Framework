using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// 針對回應快取、健康帳本粒度、重試退避、成本路由與新增錯誤碼的行為測試。
    /// </summary>
    [TestFixture]
    public class OptimizationTests
    {
        private static RimLLMRequest NewRequest(string prompt = "hello")
        {
            return new RimLLMRequest
            {
                ModId = "test.optimization",
                SystemPrompt = "sys",
                Messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, prompt) }
            };
        }

        private static RimLLMFallbackPipeline BuildPipeline(
            MockSettings settings,
            RimLLMHealthLedger ledger,
            RimLLMUsageTracker tracker,
            Dictionary<string, ILLMProvider> providers)
        {
            return new RimLLMFallbackPipeline(
                settings,
                ledger,
                tracker,
                id => providers.TryGetValue(id, out var provider) ? provider : null,
                id => settings.EnabledProviders.TryGetValue(id, out bool enabled) && enabled);
        }

        private static void RegisterProvider(
            MockSettings settings,
            Dictionary<string, ILLMProvider> providers,
            MockTestProvider provider)
        {
            providers[provider.ProviderId] = provider;
            settings.EnabledProviders[provider.ProviderId] = true;
            settings.ApiKeys[provider.ProviderId] = "key";
        }

        // ---------- 健康帳本粒度（供應商 + 模型） ----------

        [Test]
        public async Task CooldownIsolatesSiblingModelsOfTheSameProvider()
        {
            var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 0 };
            var ledger = new RimLLMHealthLedger();
            var tracker = new RimLLMUsageTracker(settings);
            var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

            RegisterProvider(settings, providers, new MockTestProvider { ProviderId = "MultiModel" });
            settings.FallbackChain = new List<string> { "MultiModel:bad", "MultiModel:good" };

            var pipeline = BuildPipeline(settings, ledger, tracker, providers);
            var result = await pipeline.ExecuteWithFallbackAsync(
                NewRequest(),
                (p, model) => model == "bad"
                    ? throw new RimLLMException(LLMError.ProviderOffline, "boom")
                    : Task.FromResult(new RimLLMGenerationResult { Text = "ok" }),
                LLMError.Unknown,
                "exhausted");

            ClassicAssert.AreEqual("ok", result.Text);

            // 壞掉的模型進入冷卻，但同一個供應商底下健康的模型不受牽連。
            ClassicAssert.IsTrue(ledger.IsInCooldown("MultiModel:bad"));
            ClassicAssert.IsFalse(ledger.IsInCooldown("MultiModel:good"));
        }

        [Test]
        public void RetriesOfOneRequestRecordAtMostOneFailure()
        {
            var settings = new MockSettings { MaxRetries = 3, RetryDelay = 0f, RoutingStrategy = 0 };
            var ledger = new RimLLMHealthLedger();
            var tracker = new RimLLMUsageTracker(settings);
            var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

            RegisterProvider(settings, providers, new MockTestProvider { ProviderId = "Flaky" });
            settings.FallbackChain = new List<string> { "Flaky:m1" };

            var pipeline = BuildPipeline(settings, ledger, tracker, providers);

            Assert.ThrowsAsync<RimLLMException>(async () => await pipeline.ExecuteWithFallbackAsync(
                NewRequest(),
                (p, model) => throw new RimLLMException(LLMError.NetworkError, "offline"),
                LLMError.Unknown,
                "exhausted"));

            // 4 次嘗試（1 次 + 3 次重試）只能記成 1 次失敗，否則單一次網路抖動
            // 就會直接把目標推過熔斷門檻。
            ledger.IsInCooldown("Flaky:m1", out _, out int continuousFailures);
            ClassicAssert.AreEqual(1, continuousFailures);
        }

        // ---------- 成本優先路由 ----------

        [Test]
        public async Task LowestCostRoutingTriesTheCheaperModelFirst()
        {
            var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 3 };
            var ledger = new RimLLMHealthLedger();
            var tracker = new RimLLMUsageTracker(settings);
            var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

            RegisterProvider(settings, providers, new MockTestProvider { ProviderId = "openai" });
            // 鏈的順序刻意把貴的放前面，證明是策略而非順序決定的。
            settings.FallbackChain = new List<string> { "openai:gpt-4o", "openai:gpt-4o-mini" };

            var pipeline = BuildPipeline(settings, ledger, tracker, providers);

            string firstModel = null;
            await pipeline.ExecuteWithFallbackAsync(
                NewRequest(),
                (p, model) =>
                {
                    firstModel = firstModel ?? model;
                    return Task.FromResult(new RimLLMGenerationResult { Text = "ok" });
                },
                LLMError.Unknown,
                "exhausted");

            ClassicAssert.AreEqual("gpt-4o-mini", firstModel);
        }

        [Test]
        public async Task PriorityFailoverKeepsTheConfiguredChainOrder()
        {
            var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 0 };
            var ledger = new RimLLMHealthLedger();
            var tracker = new RimLLMUsageTracker(settings);
            var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

            RegisterProvider(settings, providers, new MockTestProvider { ProviderId = "openai" });
            settings.FallbackChain = new List<string> { "openai:gpt-4o", "openai:gpt-4o-mini" };

            var pipeline = BuildPipeline(settings, ledger, tracker, providers);

            string firstModel = null;
            await pipeline.ExecuteWithFallbackAsync(
                NewRequest(),
                (p, model) =>
                {
                    firstModel = firstModel ?? model;
                    return Task.FromResult(new RimLLMGenerationResult { Text = "ok" });
                },
                LLMError.Unknown,
                "exhausted");

            ClassicAssert.AreEqual("gpt-4o", firstModel);
        }

        // ---------- 新增的錯誤碼辨識 ----------

        [Test]
        public void ContextWindowAndContentPolicyRejectionsGetTheirOwnErrorCodes()
        {
            ClassicAssert.AreEqual(
                LLMError.ContextWindowExceeded,
                LLMErrorMapper.CreateException(400, "This model's maximum context length is 8192 tokens").Error);

            // 413 依定義就是酬載過大，不必比對訊息。
            ClassicAssert.AreEqual(
                LLMError.ContextWindowExceeded,
                LLMErrorMapper.CreateException(413, "Payload Too Large").Error);

            ClassicAssert.AreEqual(
                LLMError.ContentFilter,
                LLMErrorMapper.CreateException(400, "content_policy_violation: the prompt was rejected").Error);

            ClassicAssert.AreEqual(
                LLMError.ContentFilter,
                LLMErrorMapper.CreateException(400, "blocked by the safety filter").Error);
        }

        [Test]
        public void ParameterRejectionsStillMapToInvalidResponseSoTheRetryPathIsUnchanged()
        {
            RimLLMException schemaRejected =
                LLMErrorMapper.CreateException(400, "Invalid schema for response_format 'json_schema'");
            ClassicAssert.AreEqual(LLMError.InvalidResponse, schemaRejected.Error);
            ClassicAssert.IsTrue(schemaRejected.IsSchemaRejection);

            RimLLMException reasoningRejected =
                LLMErrorMapper.CreateException(400, "Unrecognized request argument: reasoning_effort");
            ClassicAssert.AreEqual(LLMError.InvalidResponse, reasoningRejected.Error);
            ClassicAssert.IsTrue(reasoningRejected.IsReasoningRejection);

            RimLLMException temperatureRejected =
                LLMErrorMapper.CreateException(400, "Unsupported value: 'temperature' does not support 0.7");
            ClassicAssert.AreEqual(LLMError.InvalidResponse, temperatureRejected.Error);
            ClassicAssert.IsTrue(temperatureRejected.IsTemperatureRejection);

            // 一般的請求組裝錯誤仍是 InvalidResponse
            ClassicAssert.AreEqual(
                LLMError.InvalidResponse,
                LLMErrorMapper.CreateException(400, "missing required field 'messages'").Error);
        }

        // ---------- 回應快取 ----------

        [Test]
        public void ResponseCacheReplaysIdenticalRequestsOnlyWhenEnabled()
        {
            var settings = new MockSettings { EnableResponseCache = false, ResponseCacheTtlMinutes = 30f };
            var cache = new RimLLMResponseCache(settings);

            cache.Store(NewRequest(), "cached-text");
            ClassicAssert.IsFalse(cache.TryGet(NewRequest(), out _), "關閉時不應命中");

            settings.EnableResponseCache = true;
            ClassicAssert.IsFalse(cache.TryGet(NewRequest(), out _), "關閉期間也不應存入");

            cache.Store(NewRequest(), "cached-text");
            ClassicAssert.IsTrue(cache.TryGet(NewRequest(), out string hit));
            ClassicAssert.AreEqual("cached-text", hit);
        }

        [Test]
        public void ResponseCacheKeyCoversEveryFieldThatChangesTheOutput()
        {
            string baseKey = RimLLMResponseCache.BuildKey(NewRequest());

            ClassicAssert.AreEqual(baseKey, RimLLMResponseCache.BuildKey(NewRequest()), "相同請求必須得到相同鍵");
            ClassicAssert.AreNotEqual(baseKey, RimLLMResponseCache.BuildKey(NewRequest("different prompt")));

            var withTemperature = NewRequest();
            withTemperature.Temperature = 0.7f;
            ClassicAssert.AreNotEqual(baseKey, RimLLMResponseCache.BuildKey(withTemperature));

            var withSystemPrompt = NewRequest();
            withSystemPrompt.SystemPrompt = "other-sys";
            ClassicAssert.AreNotEqual(baseKey, RimLLMResponseCache.BuildKey(withSystemPrompt));

            var withModel = NewRequest();
            withModel.PreferredModelId = "openai:gpt-4o";
            ClassicAssert.AreNotEqual(baseKey, RimLLMResponseCache.BuildKey(withModel));

            var withResponseType = NewRequest();
            withResponseType.ResponseType = typeof(string);
            ClassicAssert.AreNotEqual(baseKey, RimLLMResponseCache.BuildKey(withResponseType));

            // ModId 與 Priority 只影響節流與排隊，不影響輸出，因此不進鍵值。
            var otherMod = NewRequest();
            otherMod.ModId = "someone.else";
            otherMod.Priority = 5;
            ClassicAssert.AreEqual(baseKey, RimLLMResponseCache.BuildKey(otherMod));
        }

        [Test]
        public void ResponseCacheDoesNotStoreEmptyResults()
        {
            var settings = new MockSettings { EnableResponseCache = true, ResponseCacheTtlMinutes = 30f };
            var cache = new RimLLMResponseCache(settings);

            cache.Store(NewRequest(), null);
            cache.Store(NewRequest(), "");
            ClassicAssert.IsFalse(cache.TryGet(NewRequest(), out _));
        }

        [Test]
        public async Task ResponseCacheEntriesExpireAfterTheirTtl()
        {
            // 0.002 分鐘 = 120 毫秒
            var settings = new MockSettings { EnableResponseCache = true, ResponseCacheTtlMinutes = 0.002f };
            var cache = new RimLLMResponseCache(settings);

            cache.Store(NewRequest(), "stale");
            ClassicAssert.IsTrue(cache.TryGet(NewRequest(), out _));

            await Task.Delay(300);
            ClassicAssert.IsFalse(cache.TryGet(NewRequest(), out _));
        }

        [Test]
        public async Task EnabledResponseCacheStopsTheSecondIdenticalCallFromReachingTheProvider()
        {
            var settings = new MockSettings
            {
                MaxRetries = 0,
                RetryDelay = 0f,
                RoutingStrategy = 0,
                EnableAntiAbuse = false,
                EnableResponseCache = true,
                ResponseCacheTtlMinutes = 30f
            };
            var ledger = new RimLLMHealthLedger();
            var tracker = new RimLLMUsageTracker(settings);
            var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

            int providerCalls = 0;
            RegisterProvider(settings, providers, new MockTestProvider
            {
                ProviderId = "Counting",
                GenerateHandler = (msgs, opts, model) =>
                {
                    providerCalls++;
                    return Task.FromResult("generated");
                }
            });
            settings.FallbackChain = new List<string> { "Counting:m1" };

            var pipeline = new RimLLMChatExecutionPipeline(
                settings,
                new RimLLMRequestQueue(settings),
                BuildPipeline(settings, ledger, tracker, providers),
                tracker,
                new RimLLMResponseCache(settings));

            var first = await pipeline.GenerateAsync(NewRequest());
            var second = await pipeline.GenerateAsync(NewRequest());

            ClassicAssert.AreEqual("generated", first.Text);
            ClassicAssert.AreEqual("generated", second.Text);
            ClassicAssert.AreEqual(1, providerCalls, "第二次相同請求應由快取回應，不再打 API");
        }

        // ---------- OpenAI Patch 傳播器停用與工廠輔助測試 ----------

        [Test]
        public void DisablePatchPropagatorsHandlesNullGracefully()
        {
            OpenAI.Chat.ChatCompletionOptions nullOptions = null;
            var result = nullOptions.DisablePatchPropagators();
            ClassicAssert.IsNull(result);
        }

        [Test]
        public void DisablePatchPropagatorsClearsPropagatorsAndAllowsPatching()
        {
            var options = new OpenAI.Chat.ChatCompletionOptions();
            var sanitized = options.DisablePatchPropagators();
            ClassicAssert.AreSame(options, sanitized);

            // 驗證清空後直接操作 Patch 不會引發 NullReferenceException
            sanitized.Patch.Set(System.Text.Encoding.UTF8.GetBytes("$.response_format"), "json_object");
            sanitized.Patch.Set(System.Text.Encoding.UTF8.GetBytes("$.max_tokens"), 100);
        }

        [Test]
        public void GetOrCreateSanitizedOptionsCreatesOrSanitizesOptions()
        {
            // Case 1: baseFactory 為 null
            var opts1 = OpenAIPatchExtensions.GetOrCreateSanitizedOptions(null, null);
            ClassicAssert.IsNotNull(opts1);

            // Case 2: baseFactory 回傳現有 options
            var existing = new OpenAI.Chat.ChatCompletionOptions();
            var opts2 = OpenAIPatchExtensions.GetOrCreateSanitizedOptions(client => existing, null);
            ClassicAssert.AreSame(existing, opts2);

            // Case 3: baseFactory 回傳非 ChatCompletionOptions 物件
            var opts3 = OpenAIPatchExtensions.GetOrCreateSanitizedOptions(client => "not-options", null);
            ClassicAssert.IsNotNull(opts3);
        }
    }
}
