using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Manager.Services;
using RimLLM_Framework.Mod;
using TypeSafeAI;

namespace RimLLM_Framework.Tests;

/// <summary>
/// OpenRouter 作為決策與 Embedding 供應商：路徑改寫、模型清單與應用歸屬標頭。
/// 路徑與格式依 https://openrouter.ai/docs/guides/features/decisions 與 /docs/api_reference/embeddings。
/// </summary>
[TestFixture]
[NonParallelizable]
public class OpenRouterDecisionAndEmbeddingTests
{
    private const string ModId = "test.openrouter";
    private static readonly RimLLMAppAttribution Attribution = new("RimAgent", "https://example.com/rimagent");
    private static readonly string[] ExpectedDecisionModels = ["typesafe/jev-1.14", "liquid/d1.2"];
    private static readonly string[] ExpectedEmbeddingModels = ["openai/text-embedding-3-small"];

    private RecordingHttpHandler _handler;

    /// <summary>決策評估的回應內容；個別測試可覆寫。</summary>
    private string _decisionBody;
    private HttpClient _http;
    private Func<HttpMessageHandler> _originalMessageHandlerFactory;

    [SetUp]
    public void SetUp()
    {
        _decisionBody = DefaultDecisionBody;
        _handler = new RecordingHttpHandler(Respond);
        _http = new HttpClient(_handler);
        _originalMessageHandlerFactory = RimLLMDecisionService.MessageHandlerFactory;
        RimLLMDecisionService.MessageHandlerFactory = () => _handler;
        RimLLMEmbeddingService.TransportOverride = new System.ClientModel.Primitives.HttpClientPipelineTransport(_http);
    }

    [TearDown]
    public void TearDown()
    {
        RimLLMDecisionService.MessageHandlerFactory = _originalMessageHandlerFactory;
        RimLLMEmbeddingService.TransportOverride = null;
        _http.Dispose();
    }

    private static RimLLMManager NewManager(MockSettings settings)
    {
        return new RimLLMManager(settings) { AttributionResolver = _ => Attribution };
    }

    private static MockSettings NewDecisionSettings()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.OpenRouter;
        settings.DecisionModels[RimLLMDecisionProviderCatalog.OpenRouter] = "typesafe/jev-1.14";
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.OpenRouter] = "sk-or-test";
        return settings;
    }

    [Test]
    public async Task Decision_PostsToAlphaDecisionsWithAttribution()
    {
        (QuestionSet plan, NoulHandle hostile) = DecisionTestDoubles.OneQuestion();
        RimLLMDecisionClient client = NewManager(NewDecisionSettings()).CreateDecisionService(ModId);

        QuestionSetResult result = await client.EvaluateAsync("A raider shouts threats.", plan);

        RecordingHttpHandler.Request sent = _handler.Requests[0];
        ClassicAssert.AreEqual("https://openrouter.ai/api/alpha/decisions", sent.Uri.GetLeftPart(UriPartial.Path));
        ClassicAssert.AreEqual("Bearer sk-or-test", sent.Headers["Authorization"]);
        ClassicAssert.AreEqual(Attribution.Url, sent.Headers["HTTP-Referer"]);
        ClassicAssert.AreEqual(Attribution.Title, sent.Headers["X-OpenRouter-Title"]);
        ClassicAssert.AreEqual(0.75, result.Get(hostile).Probability, 1e-9);
        ClassicAssert.AreSame(RimLLMAppAttribution.Framework, RimLLMAppAttribution.Current, "請求結束後不得殘留在呼叫端");
    }

    /// <summary>
    /// OpenRouter 可對單題回 <c>{"type":"refusal"}</c>（System One 沒有這個型別）。
    /// 拒答只能影響那一題：整次評估仍要成功，其他題照常取值。
    /// </summary>
    [Test]
    public async Task Decision_RefusedQuestionDoesNotFailTheOthers()
    {
        var plan = new QuestionSet();
        NoulHandle hostile = plan.Noul("Is the speaker hostile?", id: "hostile");
        NoulHandle lying = plan.Noul("Is the speaker lying?", id: "lying");
        _decisionBody =
            "{\"model\":\"typesafe/jev-1.14\",\"answers\":{" +
            "\"hostile\":{\"type\":\"noul\",\"noul\":0.75,\"confidence\":0.75}," +
            "\"lying\":{\"type\":\"refusal\"}}," +
            "\"usage\":{\"input_tokens\":11,\"output_tokens\":0}}";

        QuestionSetResult result = await NewManager(NewDecisionSettings()).CreateDecisionService(ModId)
            .EvaluateAsync("state", plan);

        ClassicAssert.AreEqual(0.75, result.Get(hostile).Probability, 1e-9);
        ClassicAssert.IsFalse(result.TryGet(lying, out _), "拒答的題目以 TryGet 回報缺值");
    }

    [Test]
    public async Task Decision_ModelListQueriesDecisionModalityOnly()
    {
        IReadOnlyList<string> models = await NewManager(NewDecisionSettings()).DecisionService
            .FetchDecisionModelsAsync(RimLLMDecisionProviderCatalog.OpenRouter);

        ClassicAssert.AreEqual(1, _handler.Requests.Count, "已帶 output_modalities 的請求不應再被外層重送");
        ClassicAssert.AreEqual(
            "https://openrouter.ai/api/v1/models?output_modalities=decisions",
            _handler.Requests[0].Uri.ToString());
        CollectionAssert.AreEqual(ExpectedDecisionModels, models);
    }

    [Test]
    public void Decision_OpenRouterInheritsTheChatKey()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.ApiKeys[ProviderIds.OpenRouter] = "sk-or-chat";

        ClassicAssert.AreEqual("sk-or-chat",
            RimLLMDecisionService.GetEffectiveApiKey(settings, RimLLMDecisionProviderCatalog.OpenRouter));
        ClassicAssert.AreEqual(RimLLMDecisionProviderCatalog.OpenRouter,
            SetupStatus.OneClickDecisionProviderFor(ProviderIds.OpenRouter));
    }

    /// <summary>其他主機的 System One 請求不得被改寫。</summary>
    [Test]
    public async Task Decision_OtherHostsAreNotRewritten()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;
        settings.DecisionModels[RimLLMDecisionProviderCatalog.TypeSafe] = "jev-latest";
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.TypeSafe] = "ts-key";

        await NewManager(settings).CreateDecisionService(ModId)
            .EvaluateAsync("state", DecisionTestDoubles.OneQuestionPlan());

        RecordingHttpHandler.Request sent = _handler.Requests[0];
        ClassicAssert.AreEqual("/v1/systemone", sent.Uri.AbsolutePath);
        ClassicAssert.IsFalse(sent.Headers.ContainsKey("HTTP-Referer"));
    }

    [Test]
    public async Task Embedding_PostsToOpenRouterWithAttribution()
    {
        var settings = new MockSettings
        {
            EmbeddingProvider = RimLLMEmbeddingService.OpenRouterProvider,
            EmbeddingModel = "openai/text-embedding-3-small",
            EmbeddingApiKey = "sk-or-test"
        };

        var generator = NewManager(settings).CreateEmbeddingGenerator(ModId);
        GeneratedEmbeddings<Embedding<float>> embeddings = await generator.GenerateAsync(["hello"]);

        RecordingHttpHandler.Request sent = _handler.Requests[0];
        ClassicAssert.AreEqual("https://openrouter.ai/api/v1/embeddings", sent.Uri.ToString());
        ClassicAssert.AreEqual(Attribution.Url, sent.Headers["HTTP-Referer"]);
        ClassicAssert.AreEqual(Attribution.Title, sent.Headers["X-OpenRouter-Title"]);
        ClassicAssert.AreEqual(1, embeddings.Count);
    }

    [Test]
    public async Task Embedding_ModelListUsesEmbeddingsModelsEndpoint()
    {
        var settings = new MockSettings();
        RimLLMEmbeddingModelList list = await NewManager(settings).EmbeddingService
            .FetchAvailableModelsAsync(RimLLMEmbeddingService.OpenRouterProvider, null, "sk-or-test");

        ClassicAssert.AreEqual("https://openrouter.ai/api/v1/embeddings/models", _handler.Requests[0].Uri.ToString());
        ClassicAssert.IsTrue(list.Filtered);
        CollectionAssert.AreEqual(ExpectedEmbeddingModels, list.Models);
    }

    [Test]
    public void Embedding_OpenRouterInheritsTheChatKey()
    {
        ClassicAssert.AreEqual(ProviderIds.OpenRouter,
            RimLLMEmbeddingService.GetMainProviderIdForEmbedding(RimLLMEmbeddingService.OpenRouterProvider));
        ClassicAssert.AreEqual(RimLLMEmbeddingService.OpenRouterProvider,
            SetupStatus.OneClickEmbeddingProviderFor(ProviderIds.OpenRouter));
    }

    private const string DefaultDecisionBody =
        "{\"id\":\"gen-dec-1\",\"model\":\"typesafe/jev-1.14\",\"provider\":\"TypeSafe\"," +
        "\"answers\":{\"hostile\":{\"type\":\"noul\",\"noul\":0.75,\"confidence\":0.75}}," +
        "\"usage\":{\"input_tokens\":11,\"output_tokens\":0,\"cost\":0.00001}}";

    /// <summary>依路徑回應 OpenRouter 格式的內容。</summary>
    private string Respond(Uri uri)
    {
        return uri.AbsolutePath switch
        {
            "/api/alpha/decisions" => _decisionBody,
            "/api/v1/models" =>
                "{\"data\":[{\"id\":\"typesafe/jev-1.14\"},{\"id\":\"liquid/d1.2\"}]}",
            "/api/v1/embeddings/models" =>
                "{\"object\":\"list\",\"data\":[{\"id\":\"openai/text-embedding-3-small\",\"object\":\"model\",\"created\":0,\"owned_by\":\"openai\"}]}",
            "/api/v1/embeddings" =>
                "{\"object\":\"list\",\"model\":\"openai/text-embedding-3-small\"," +
                "\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.1,0.2]}]," +
                "\"usage\":{\"prompt_tokens\":2,\"total_tokens\":2}}",
            // 未改寫的 System One 路徑（其他主機）：回一題 Noul。
            _ =>
                "{\"model\":\"jev-latest\",\"answers\":{\"hostile\":{\"type\":\"noul\",\"noul\":0.75}}," +
                "\"usage\":{\"input_tokens\":11,\"output_tokens\":7}}"
        };
    }
}
