using System;
using System.Collections.Generic;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Manager.Services;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

/// <summary>
/// Embedding 與決策服務的備援鏈：主供應商失敗時依序換下一家，
/// 以及哪些失敗不該換家。網路層以假的 message handler 依主機名稱決定成功或失敗。
/// </summary>
[TestFixture]
public class ServiceFailoverTests
{
    private const string EmbeddingResponse =
        "{\"object\":\"list\",\"model\":\"m\",\"data\":[" +
        "{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.1,0.2]}]," +
        "\"usage\":{\"prompt_tokens\":10,\"total_tokens\":10}}";

    // 重複出現於多個測試的常數輸入與主機清單，抽為靜態唯讀欄位避免重複建立陣列。
    private static readonly string[] HelloInput = new[] { "hello" };
    private static readonly string[] OpenAiHost = new[] { "api.openai.com" };
    private static readonly string[] OpenAiAndGeminiHosts = new[] { "api.openai.com", "generativelanguage.googleapis.com" };
    private static readonly string[] GoogleProviders = new[] { "Google", "Google" };
    private static readonly string[] GoogleAndOpenAIProviders = new[] { "Google", "OpenAI" };
    private static readonly string[] DecisionFallbackChainLiquid = new[] { RimLLMDecisionProviderCatalog.Liquid };
    private static readonly string[] FallbackChainNames = new[] { "EmbeddingFallbackChain", "DecisionFallbackChain" };

    // ---------- 共用規則 ----------

    [Test]
    public void BuildCandidates_PutsActiveFirstAndDropsEmptyDuplicateAndActiveEntries()
    {
        List<string> candidates = ServiceProviderFailover.BuildCandidates(
            "A", chain, "Disabled");

        CollectionAssert.AreEqual(expected, candidates);
    }

    [Test]
    public void BuildCandidates_WhenActiveIsDisabled_ReturnsNothing()
    {
        ClassicAssert.IsEmpty(ServiceProviderFailover.BuildCandidates("Disabled", chainArray, "Disabled"));
        ClassicAssert.IsEmpty(ServiceProviderFailover.BuildCandidates("", chainArray, "Disabled"));
    }

    [TestCase(LLMError.Timeout, true)]
    [TestCase(LLMError.RateLimit, true)]
    [TestCase(LLMError.InvalidKey, true)]
    [TestCase(LLMError.QuotaExceeded, true)]
    [TestCase(LLMError.ModelNotFound, true)]
    [TestCase(LLMError.NetworkError, true)]
    [TestCase(LLMError.Unknown, false)]
    [TestCase(LLMError.ContentFilter, false)]
    [TestCase(LLMError.ContextWindowExceeded, false)]
    [TestCase(LLMError.Cancelled, false)]
    public void ShouldFailOver_ByErrorKind(LLMError error, bool expected)
    {
        ClassicAssert.AreEqual(expected, ServiceProviderFailover.ShouldFailOver(new RimLLMException(error, "x")));
    }

    [Test]
    public void ShouldFailOver_NotWhenTheProviderRejectedTheRequestItself()
    {
        var rejected = new RimLLMException(LLMError.InvalidResponse, "bad request") { HttpStatusCode = 400 };
        ClassicAssert.IsFalse(ServiceProviderFailover.ShouldFailOver(rejected));
    }

    [Test]
    public void ShouldFailOver_NeverForCallerCancellationOrProgrammingErrors()
    {
        ClassicAssert.IsFalse(ServiceProviderFailover.ShouldFailOver(new OperationCanceledException()));
        ClassicAssert.IsFalse(ServiceProviderFailover.ShouldFailOver(new ArgumentNullException("x")));
    }

    // ---------- Embedding ----------

    [Test]
    public async Task Embedding_WhenPrimaryFails_UsesTheFallbackProviderWithItsOwnModel()
    {
        MockSettings settings = EmbeddingSettings();
        var hosts = new List<string>();
        var handler = RoutingHandler(hosts, failingHost: "api.openai.com");

        await WithTransportAsync(handler, async () =>
        {
            IReadOnlyList<RimLLMEmbeddingResult> results =
                await new RimLLMEmbeddingService(settings).ComputeEmbeddingsAsync(HelloInput);

            ClassicAssert.AreEqual(1, results.Count);
            ClassicAssert.AreEqual("Google", results[0].ProviderId, "向量必須標上實際算出它的供應商。");
            ClassicAssert.AreEqual("backup-model", results[0].ModelId, "備援供應商用自己的模型，不沿用主供應商的。");
        });

        CollectionAssert.AreEqual(
            OpenAiAndGeminiHosts, hosts,
            "主供應商先試，失敗後才換備援。");
    }

    [Test]
    public async Task Embedding_WithoutFallbackChain_PropagatesPrimaryFailure()
    {
        MockSettings settings = EmbeddingSettings();
        settings.EmbeddingFallbackChain = [];
        var hosts = new List<string>();
        var handler = RoutingHandler(hosts, failingHost: "api.openai.com");

        await WithTransportAsync(handler, async () =>
        {
            RimLLMException ex = await ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
                await new RimLLMEmbeddingService(settings).ComputeEmbeddingsAsync(HelloInput));
            ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        });

        CollectionAssert.AreEqual(OpenAiHost, hosts);
    }

    [Test]
    public async Task Embedding_FallbackProviderWithoutAModel_IsSkippedAndPrimaryErrorSurfaces()
    {
        MockSettings settings = EmbeddingSettings();
        settings.EmbeddingModels.Clear();
        var hosts = new List<string>();
        var handler = RoutingHandler(hosts, failingHost: "api.openai.com");

        await WithTransportAsync(handler, async () =>
        {
            RimLLMException ex = await ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
                await new RimLLMEmbeddingService(settings).ComputeEmbeddingsAsync(HelloInput));
            ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error, "沒設定的備援不該蓋掉主供應商真正的失敗原因。");
        });

        CollectionAssert.AreEqual(OpenAiHost, hosts, "沒選模型的備援供應商不該被打。");
    }

    [Test]
    public async Task Embedding_WhenAllProvidersFail_ReturnsTheLastProvidersError()
    {
        MockSettings settings = EmbeddingSettings();
        var hosts = new List<string>();
        var handler = RoutingHandler(hosts, failingHost: "api.openai.com", alsoFailingHost: "generativelanguage.googleapis.com");

        await WithTransportAsync(handler, async () =>
        {
            RimLLMException ex = await ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
                await new RimLLMEmbeddingService(settings).ComputeEmbeddingsAsync(HelloInput));
            ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        });

        ClassicAssert.AreEqual(2, hosts.Count);
    }

    [Test]
    public async Task Embedding_CallerCancellation_DoesNotFailOver()
    {
        MockSettings settings = EmbeddingSettings();
        var hosts = new List<string>();
        var handler = RoutingHandler(hosts, failingHost: "api.openai.com");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await WithTransportAsync(handler, async () =>
        {
            await ClassicAssert.CatchAsync<OperationCanceledException>(async () =>
                await new RimLLMEmbeddingService(settings).ComputeEmbeddingsAsync(HelloInput, cancellation.Token));
        });

        ClassicAssert.IsEmpty(hosts);
    }

    [Test]
    public async Task Embedding_KeylessCloudBackup_IsSkipped()
    {
        MockSettings settings = EmbeddingSettings();
        settings.EmbeddingApiKeys.Remove("Google");
        var hosts = new List<string>();
        var handler = RoutingHandler(hosts, failingHost: "api.openai.com");

        await WithTransportAsync(handler, async () =>
        {
            await ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
                await new RimLLMEmbeddingService(settings).ComputeEmbeddingsAsync(HelloInput));
        });

        CollectionAssert.AreEqual(OpenAiHost, hosts, "沒有金鑰的雲端備援不該被打。");
    }

    [Test]
    public async Task Embedding_FailedProvidersBatches_AreNotReportedAsUsage()
    {
        MockSettings settings = EmbeddingSettings();
        int openAiCalls = 0;
        var handler = new HttpMessageHandlerStub(request =>
        {
            if (request.RequestUri.Host == "api.openai.com" && ++openAiCalls > 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"bad key\"}}", Encoding.UTF8, "application/json")
                };
            }
            using var body = System.Text.Json.JsonDocument.Parse(request.Content.ReadAsStringAsync().Result);
            int count = body.RootElement.GetProperty("input").GetArrayLength();
            var data = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                data.Append(i > 0 ? "," : "").Append("{\"object\":\"embedding\",\"index\":").Append(i).Append(",\"embedding\":[0.1,0.2]}");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"object\":\"list\",\"model\":\"m\",\"data\":[" + data + "],\"usage\":{\"prompt_tokens\":10,\"total_tokens\":10}}",
                    Encoding.UTF8, "application/json")
            };
        });
        var reportedProviders = new List<string>();
        var texts = new List<string>();
        for (int i = 0; i < RimLLMEmbeddingService.MaxEmbeddingBatchSize + 1; i++) texts.Add("t" + i);

        await WithTransportAsync(handler, async () =>
        {
            await new RimLLMEmbeddingService(settings).ComputeEmbeddingsAsync(
                texts, null, batch => reportedProviders.Add(batch[0].ProviderId));
        });

        // 主供應商第一批成功、第二批失敗：它的批次不能先回報用量，否則整次重算後會被重複計入。
        CollectionAssert.AreEqual(GoogleProviders, reportedProviders);
    }

    private static MockSettings EmbeddingSettings()
    {
        return new MockSettings
        {
            ApiTimeout = 30f,
            EnableAntiAbuse = false,
            EmbeddingProvider = "OpenAI",
            EmbeddingModel = "primary-model",
            EmbeddingApiKey = "primary-key",
            EmbeddingFallbackChain = ["Google"],
            EmbeddingModels = { ["Google"] = "backup-model" },
            EmbeddingApiKeys = { ["Google"] = "backup-key" }
        };
    }

    private static HttpMessageHandlerStub RoutingHandler(
        List<string> hosts, string failingHost, string alsoFailingHost = null)
    {
        return new HttpMessageHandlerStub(request =>
        {
            string host = request.RequestUri.Host;
            hosts.Add(host);
            bool fail = host == failingHost || host == alsoFailingHost;
            return fail
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"bad key\"}}", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(EmbeddingResponse, Encoding.UTF8, "application/json")
                };
        });
    }

    private static async Task WithTransportAsync(HttpMessageHandlerStub handler, Func<Task> action)
    {
        PipelineTransport previous = RimLLMEmbeddingService.TransportOverride;
        using var httpClient = new HttpClient(handler);
        RimLLMEmbeddingService.TransportOverride = new HttpClientPipelineTransport(httpClient);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            RimLLMEmbeddingService.TransportOverride = previous;
        }
    }

    private sealed class HttpMessageHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public HttpMessageHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }
    }

    // ---------- 設定 ----------

    [Test]
    public void Settings_FallbackChains_DropEmptyAndDuplicateEntries()
    {
        var settings = new RimLLMFrameworkSettings
        {
            EmbeddingFallbackChain = ["Google", "", "Google", "OpenAI"],
            DecisionFallbackChain = [RimLLMDecisionProviderCatalog.Liquid, null, RimLLMDecisionProviderCatalog.Liquid]
        };

        CollectionAssert.AreEqual(GoogleAndOpenAIProviders, settings.EmbeddingFallbackChain);
        CollectionAssert.AreEqual(DecisionFallbackChainLiquid, settings.DecisionFallbackChain);
    }

    [Test]
    public void Settings_ResetAll_ClearsBothFallbackChains()
    {
        var settings = new RimLLMFrameworkSettings
        {
            EmbeddingFallbackChain = ["Google"],
            DecisionFallbackChain = [RimLLMDecisionProviderCatalog.Liquid]
        };

        settings.ResetAllSettingsToDefaults();

        ClassicAssert.IsEmpty(settings.EmbeddingFallbackChain);
        ClassicAssert.IsEmpty(settings.DecisionFallbackChain);
    }

    [Test]
    public void Settings_Dto_CarriesBothFallbackChains()
    {
        // 存檔格式的私有 DTO 少了任一欄位，玩家的備援鏈重啟後就會悄悄消失。
        System.Type dto = typeof(RimLLMFrameworkSettings).GetNestedType(
            "SettingsDto", System.Reflection.BindingFlags.NonPublic);
        ClassicAssert.IsNotNull(dto);
        foreach (string name in FallbackChainNames)
        {
            System.Reflection.FieldInfo field = dto.GetField(name);
            ClassicAssert.IsNotNull(field, name);
            ClassicAssert.AreEqual(typeof(List<string>), field.FieldType, name);
        }
    }

    // ---------- 決策 ----------

    private const string PrimaryHost = "primary.example.test";
    private const string BackupHost = "backup.example.test";

    private static readonly string[] PrimaryHostArray = new[] { PrimaryHost };
    private static readonly string[] PrimaryAndBackupHosts = new[] { PrimaryHost, BackupHost };

    private Func<HttpMessageHandler> _originalFactory;
    private List<string> _decisionHosts;
    private static readonly string[] chain = new[] { "B", "", "A", "C", "B", "Disabled" };
    private static readonly string[] expected = new[] { "A", "B", "C" };
    private static readonly string[] chainArray = new[] { "B" };

    [SetUp]
    public void SetUp()
    {
        _originalFactory = RimLLMDecisionService.MessageHandlerFactory;
        _decisionHosts = [];
        var evaluateBody = new DecisionTestDoubles.FakeJevHandler().EvaluateResponseBody;
        RimLLMDecisionService.MessageHandlerFactory = () => new HttpMessageHandlerStub(request =>
        {
            string host = request.RequestUri.Host;
            lock (_decisionHosts) { _decisionHosts.Add(host); }
            return host == PrimaryHost
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"unauthorized\"}", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(evaluateBody, Encoding.UTF8, "application/json")
                };
        });
    }

    [TearDown]
    public void TearDown()
    {
        RimLLMDecisionService.MessageHandlerFactory = _originalFactory;
    }

    [Test]
    public async Task Decision_WhenPrimaryFails_UsesTheFallbackProviderAndReportsIt()
    {
        MockSettings settings = DecisionSettings();

        DecisionEvaluation evaluation = await new RimLLMDecisionService(settings)
            .EvaluateDetailedAsync("state", DecisionTestDoubles.OneQuestionPlan(), "caller-model");

        ClassicAssert.AreEqual(RimLLMDecisionProviderCatalog.Liquid, evaluation.ProviderId, "用量要記到實際回應的供應商。");
        ClassicAssert.AreEqual("backup-model", evaluation.ModelName, "逐次覆寫的模型只屬於主供應商。");
        CollectionAssert.AreEqual(PrimaryAndBackupHosts, _decisionHosts);
    }

    [Test]
    public async Task Decision_FallbackProviderWithoutKeyOrModel_IsSkippedAndPrimaryErrorSurfaces()
    {
        MockSettings settings = DecisionSettings();
        settings.DecisionApiKeys.Remove(RimLLMDecisionProviderCatalog.Liquid);

        RimLLMException ex = await ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await new RimLLMDecisionService(settings)
                .EvaluateDetailedAsync("state", DecisionTestDoubles.OneQuestionPlan()));

        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        CollectionAssert.AreEqual(PrimaryHostArray, _decisionHosts, "沒有金鑰的備援不該被打。");
    }

    [Test]
    public async Task Decision_ExplicitProviderRequest_NeverFailsOver()
    {
        MockSettings settings = DecisionSettings();

        RimLLMException ex = await ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await new RimLLMDecisionService(settings).EvaluateWithProviderDetailedAsync(
                "state", DecisionTestDoubles.OneQuestionPlan(), RimLLMDecisionProviderCatalog.TypeSafe));

        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        CollectionAssert.AreEqual(PrimaryHostArray, _decisionHosts, "呼叫端明確指定的供應商是它自己的選擇。");
    }

    [Test]
    public async Task Decision_WithoutFallbackChain_PropagatesPrimaryFailure()
    {
        MockSettings settings = DecisionSettings();
        settings.DecisionFallbackChain = [];

        RimLLMException ex = await ClassicAssert.ThrowsAsync<RimLLMException>(async () =>
            await new RimLLMDecisionService(settings)
                .EvaluateDetailedAsync("state", DecisionTestDoubles.OneQuestionPlan()));

        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
        CollectionAssert.AreEqual(PrimaryHostArray, _decisionHosts);
    }

    private static MockSettings DecisionSettings()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionFallbackChain = [RimLLMDecisionProviderCatalog.Liquid];
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "primary-key";
        settings.DecisionModels[RimLLMDecisionProviderCatalog.TypeSafe] = "primary-model";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.TypeSafe] = "https://" + PrimaryHost;
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Liquid] = "backup-key";
        settings.DecisionModels[RimLLMDecisionProviderCatalog.Liquid] = "backup-model";
        settings.DecisionEndpoints[RimLLMDecisionProviderCatalog.Liquid] = "https://" + BackupHost;
        return settings;
    }
}
