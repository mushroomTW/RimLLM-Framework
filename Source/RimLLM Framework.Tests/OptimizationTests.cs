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

        private static RimLLMFailoverChatClient BuildRouter(
            MockSettings settings,
            RimLLMHealthLedger ledger,
            RimLLMUsageTracker tracker,
            Dictionary<string, ILLMProvider> providers)
        {
            return new RimLLMFailoverChatClient(
                settings, ledger, tracker,
                BuildPipeline(settings, ledger, tracker, providers),
                "test.optimization");
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

            RegisterProvider(settings, providers, new MockTestProvider
            {
                ProviderId = "MultiModel",
                GenerateHandler = (msgs, opts, model) => model == "bad"
                    ? throw new RimLLMException(LLMError.ProviderOffline, "boom")
                    : Task.FromResult("ok")
            });
            settings.FallbackChain = new List<string> { "MultiModel:bad", "MultiModel:good" };

            var router = BuildRouter(settings, ledger, tracker, providers);
            ChatResponse response = await router.GetResponseAsync(NewMessages());

            ClassicAssert.AreEqual("ok", response.Text);

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

            RegisterProvider(settings, providers, new MockTestProvider
            {
                ProviderId = "Flaky",
                GenerateHandler = (msgs, opts, model) => throw new RimLLMException(LLMError.NetworkError, "offline")
            });
            settings.FallbackChain = new List<string> { "Flaky:m1" };

            var router = BuildRouter(settings, ledger, tracker, providers);

            Assert.ThrowsAsync<RimLLMException>(async () => await router.GetResponseAsync(NewMessages()));

            // 4 次嘗試（1 次 + 3 次重試）只能記成 1 次失敗，否則單一次網路抖動
            // 就會直接把目標推過熔斷門檻。
            ledger.IsInCooldown("Flaky:m1", out _, out int continuousFailures);
            ClassicAssert.AreEqual(1, continuousFailures);
        }

        // ---------- 成本優先路由 ----------

        [Test]
        public void LowestCostRoutingTriesTheCheaperModelFirst()
        {
            var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 3 };
            var ledger = new RimLLMHealthLedger();
            var tracker = new RimLLMUsageTracker(settings);
            var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

            RegisterProvider(settings, providers, new MockTestProvider { ProviderId = "openai" });
            // 鏈的順序刻意把貴的放前面，證明是策略而非順序決定的。
            settings.FallbackChain = new List<string> { "openai:gpt-4o", "openai:gpt-4o-mini" };

            var policy = BuildPipeline(settings, ledger, tracker, providers);

            var candidates = policy.ResolveCandidates(null, null);

            ClassicAssert.AreEqual("gpt-4o-mini", candidates[0].ModelName);
        }

        [Test]
        public void PriorityFailoverKeepsTheConfiguredChainOrder()
        {
            var settings = new MockSettings { MaxRetries = 0, RetryDelay = 0f, RoutingStrategy = 0 };
            var ledger = new RimLLMHealthLedger();
            var tracker = new RimLLMUsageTracker(settings);
            var providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);

            RegisterProvider(settings, providers, new MockTestProvider { ProviderId = "openai" });
            settings.FallbackChain = new List<string> { "openai:gpt-4o", "openai:gpt-4o-mini" };

            var policy = BuildPipeline(settings, ledger, tracker, providers);

            var candidates = policy.ResolveCandidates(null, null);

            ClassicAssert.AreEqual("gpt-4o", candidates[0].ModelName);
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

        private static List<ChatMessage> NewMessages(string prompt = "hello")
        {
            return new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, "sys"),
                new ChatMessage(ChatRole.User, prompt)
            };
        }

        private static RimLLMResponseCacheChatClient BuildCache(
            MockSettings settings,
            Func<IEnumerable<ChatMessage>, ChatOptions, Task<ChatResponse>> handler)
        {
            return new RimLLMResponseCacheChatClient(
                new MockCustomChatClient { GetResponseHandler = handler },
                settings,
                new RimLLMResponseCacheStore());
        }

        [Test]
        public async Task ResponseCacheReplaysIdenticalRequestsOnlyWhenEnabled()
        {
            var settings = new MockSettings { EnableResponseCache = false, ResponseCacheTtlMinutes = 30f };
            int calls = 0;
            var cache = BuildCache(settings, (msgs, opts) =>
            {
                calls++;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "cached-text")));
            });

            await cache.GetResponseAsync(NewMessages());
            await cache.GetResponseAsync(NewMessages());
            ClassicAssert.AreEqual(2, calls, "關閉時不應命中，兩次都要打到內層");

            settings.EnableResponseCache = true;
            await cache.GetResponseAsync(NewMessages());
            ClassicAssert.AreEqual(3, calls, "關閉期間不應存入，開啟後第一次仍要打到內層");

            ChatResponse replayed = await cache.GetResponseAsync(NewMessages());
            ClassicAssert.AreEqual(3, calls, "第二次相同請求應由快取回應，不再打 API");
            ClassicAssert.AreEqual("cached-text", replayed.Text);
        }

        [Test]
        public async Task ResponseCacheIsBypassedWhenToolsArePresent()
        {
            var settings = new MockSettings { EnableResponseCache = true, ResponseCacheTtlMinutes = 30f };
            int calls = 0;
            var cache = BuildCache(settings, (msgs, opts) =>
            {
                calls++;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "tool-result")));
            });

            var withTools = new ChatOptions
            {
                Tools = new List<AITool> { AIFunctionFactory.Create(() => "result", "Dummy") }
            };

            await cache.GetResponseAsync(NewMessages(), withTools);
            await cache.GetResponseAsync(NewMessages(), withTools);

            // 帶 Tools 的請求通常具有副作用或查詢即時狀態，絕不可重播。
            ClassicAssert.AreEqual(2, calls);
        }

        [Test]
        public async Task ResponseCacheDoesNotStoreEmptyResults()
        {
            var settings = new MockSettings { EnableResponseCache = true, ResponseCacheTtlMinutes = 30f };
            int calls = 0;
            var cache = BuildCache(settings, (msgs, opts) =>
            {
                calls++;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
            });

            await cache.GetResponseAsync(NewMessages());
            await cache.GetResponseAsync(NewMessages());

            ClassicAssert.AreEqual(2, calls, "空回應不可寫入快取，否則失敗的空結果會被重播");
        }

        [Test]
        public async Task ResponseCacheEntriesExpireAfterTheirTtl()
        {
            // 0.002 分鐘 = 120 毫秒
            var settings = new MockSettings { EnableResponseCache = true, ResponseCacheTtlMinutes = 0.002f };
            int calls = 0;
            var cache = BuildCache(settings, (msgs, opts) =>
            {
                calls++;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stale")));
            });

            await cache.GetResponseAsync(NewMessages());
            await cache.GetResponseAsync(NewMessages());
            ClassicAssert.AreEqual(1, calls);

            await Task.Delay(300);
            await cache.GetResponseAsync(NewMessages());
            ClassicAssert.AreEqual(2, calls, "過期後必須重新打 API");
        }

        [Test]
        public async Task CacheHitKeepsResponseMetadata()
        {
            // 回歸測試：快取先前只存回應文字，命中時 ModelId 與 Usage 全部遺失，
            // 呼叫端因此看到空的 ModelId 與全零的用量——與「真的打了 API 但供應商
            // 沒回傳模型名」無從分辨。
            var settings = new MockSettings { EnableResponseCache = true, ResponseCacheTtlMinutes = 30f };
            var cache = BuildCache(settings, (msgs, opts) => Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "generated"))
                {
                    ModelId = "Counting:m1",
                    Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 22 }
                }));

            ChatResponse first = await cache.GetResponseAsync(NewMessages());
            ChatResponse second = await cache.GetResponseAsync(NewMessages());

            ClassicAssert.AreEqual("Counting:m1", second.ModelId, "快取重播必須保留 ModelId");
            ClassicAssert.AreEqual(11, second.Usage.InputTokenCount);
            ClassicAssert.AreEqual(22, second.Usage.OutputTokenCount);
            ClassicAssert.AreEqual(first.ModelId, second.ModelId);
        }

        [Test]
        public void ResponseCacheReplaysStreamingResponses()
        {
            var settings = new MockSettings { EnableResponseCache = true, ResponseCacheTtlMinutes = 30f };
            int calls = 0;
            var cache = new RimLLMResponseCacheChatClient(
                new MockCustomChatClient
                {
                    StreamHandler = (msgs, opts, onChunk) =>
                    {
                        calls++;
                        onChunk("mock-");
                        onChunk("stream");
                        return Task.CompletedTask;
                    }
                },
                settings,
                new RimLLMResponseCacheStore());

            string first = CollectStream(cache);
            string second = CollectStream(cache);

            ClassicAssert.AreEqual("mock-stream", first);
            ClassicAssert.AreEqual("mock-stream", second, "重播的內容必須與原本的串流一致");
            ClassicAssert.AreEqual(1, calls, "第二次相同的串流請求應由快取重播，不再打 API");
        }

        private static string CollectStream(IChatClient client)
        {
            var builder = new System.Text.StringBuilder();
            var enumerator = client.GetStreamingResponseAsync(NewMessages()).GetAsyncEnumerator();
            try
            {
                while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
                {
                    builder.Append(enumerator.Current.Text);
                }
            }
            finally
            {
                enumerator.DisposeAsync().GetAwaiter().GetResult();
            }
            return builder.ToString();
        }

        [Test]
        public void StreamingHoldsItsQueueSlotForTheWholeEnumeration()
        {
            // 這是佇列中介層最容易踩的坑：GetStreamingResponseAsync 只是「建立」一個
            // 延遲的 IAsyncEnumerable，若名額在它回傳時就釋放，串流請求等於完全不受
            // 併發上限限制，而且不會有任何錯誤徵兆。
            var settings = new MockSettings { MaxConcurrentRequests = 1 };
            var queue = new RimLLMRequestQueue(settings);
            var client = new RimLLMRequestQueueChatClient(
                new MockCustomChatClient
                {
                    StreamHandler = (msgs, opts, onChunk) =>
                    {
                        onChunk("chunk");
                        return Task.CompletedTask;
                    }
                },
                queue);

            var enumerator = client.GetStreamingResponseAsync(NewMessages()).GetAsyncEnumerator();

            // 開始列舉即佔用唯一的名額。
            ClassicAssert.IsTrue(enumerator.MoveNextAsync().GetAwaiter().GetResult());

            Task<ChatResponse> blocked = client.GetResponseAsync(NewMessages());
            ClassicAssert.IsFalse(blocked.Wait(200), "串流仍在列舉中，第二個請求不該取得名額");

            // 名額直到列舉器被釋放才歸還——列舉結束本身不代表呼叫端已經用完。
            while (enumerator.MoveNextAsync().GetAwaiter().GetResult()) { }
            enumerator.DisposeAsync().GetAwaiter().GetResult();

            ClassicAssert.IsTrue(blocked.Wait(5000), "名額歸還後第二個請求應該完成");
        }

        [Test]
        public void AntiAbuseWindowIsSharedAcrossClientsOfTheSameMod()
        {
            // 節流狀態必須由所有 client 共用：若每個 client 各持一份，同一個 Mod
            // 只要多呼叫幾次 CreateChatClient 就能繞過節流，ClearCooldowns 也只會清掉其中一份。
            var settings = new MockSettings
            {
                EnableAntiAbuse = true,
                MaxRequestsPerWindow = 2,
                ThrottlingWindowSeconds = 60,
                CoolDownDurationSeconds = 60
            };
            var store = new RimLLMThrottleStore(settings);

            IChatClient first = new RimLLMAntiAbuseChatClient(
                new MockCustomChatClient(), settings, store, "same.mod");
            IChatClient second = new RimLLMAntiAbuseChatClient(
                new MockCustomChatClient(), settings, store, "same.mod");

            first.GetResponseAsync(NewMessages()).GetAwaiter().GetResult();
            second.GetResponseAsync(NewMessages()).GetAwaiter().GetResult();

            // 第 3 次跨越視窗上限——不論它是由哪一個 client 發出的。
            Assert.ThrowsAsync<RimLLMException>(async () => await second.GetResponseAsync(NewMessages()));
        }

        [Test]
        public void ResponseCacheKeyCoversEveryFieldThatChangesTheOutput()
        {
            string baseKey = RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions());

            ClassicAssert.AreEqual(
                baseKey,
                RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions()),
                "相同請求必須得到相同鍵");
            ClassicAssert.AreNotEqual(
                baseKey,
                RimLLMResponseCacheKey.Build(NewMessages("different prompt"), new ChatOptions()));
            ClassicAssert.AreNotEqual(
                baseKey,
                RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions { Temperature = 0.7f }));
            ClassicAssert.AreNotEqual(
                baseKey,
                RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions { ModelId = "openai:gpt-4o" }));

            var withResponseType = new RimLLMChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [RimLLMChatOptions.ResponseTypeKey] = typeof(string)
                }
            };
            ClassicAssert.AreNotEqual(baseKey, RimLLMResponseCacheKey.Build(NewMessages(), withResponseType));

            // ModId 根本不在 options 上，Priority 只影響排隊順序，兩者都不影響模型輸出，
            // 因此不可改變快取鍵——否則不同 Mod 的相同請求會各打一次 API。
            ClassicAssert.AreEqual(
                baseKey,
                RimLLMResponseCacheKey.Build(NewMessages(), new RimLLMChatOptions { Priority = 5 }),
                "Priority 不可改變快取鍵");
        }

        [Test]
        public void ResponseCacheKeyCoversPassThroughSamplingFields()
        {
            // 這些欄位以前被 BuildOptions 整組丟棄，所以不進鍵值也無妨；
            // 現在它們會原樣送達 provider 並改變輸出，不進鍵值就會造成快取毒化
            // ——兩個只有 Seed 不同的請求會拿到同一份回應。
            string baseKey = RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions());

            ClassicAssert.AreNotEqual(
                baseKey, RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions { TopP = 0.5f }));
            ClassicAssert.AreNotEqual(
                baseKey, RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions { TopK = 20 }));
            ClassicAssert.AreNotEqual(
                baseKey, RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions { FrequencyPenalty = 0.3f }));
            ClassicAssert.AreNotEqual(
                baseKey, RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions { PresencePenalty = 0.4f }));
            ClassicAssert.AreNotEqual(
                baseKey, RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions { Seed = 1234L }));
            ClassicAssert.AreNotEqual(
                RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions { Seed = 1234L }),
                RimLLMResponseCacheKey.Build(NewMessages(), new ChatOptions { Seed = 5678L }),
                "只有 Seed 不同的兩個請求不可共用快取");
            ClassicAssert.AreNotEqual(
                baseKey,
                RimLLMResponseCacheKey.Build(
                    NewMessages(), new ChatOptions { StopSequences = new List<string> { "STOP" } }));
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
