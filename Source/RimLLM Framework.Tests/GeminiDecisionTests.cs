using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kkdev92.Jev;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager.Services;

namespace RimLLM_Framework.Tests;

/// <summary>
/// Gemini Decision 的轉接測試：JevClient 送出的 System One 請求必須被換成
/// Gemini Decisions API 的路徑、標頭與欄位，Gemini 的回應也要能被 SDK 解析成型別化結果。
/// 請求與回應格式依 Google 官方 Decisions API 文件。
/// </summary>
[TestFixture]
[NonParallelizable]
public class GeminiDecisionTests
{
    private const string GeminiEndpoint = "https://generativelanguage.googleapis.com";

    private FakeGeminiHandler _handler;
    private Func<HttpMessageHandler> _originalMessageHandlerFactory;

    [SetUp]
    public void SetUp()
    {
        _handler = new FakeGeminiHandler();
        _originalMessageHandlerFactory = RimLLMDecisionService.MessageHandlerFactory;
        RimLLMDecisionService.MessageHandlerFactory = () => _handler;
    }

    [TearDown]
    public void TearDown()
    {
        RimLLMDecisionService.MessageHandlerFactory = _originalMessageHandlerFactory;
        _handler = null;
    }

    private static RimLLMDecisionService NewGeminiService()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.Gemini;
        settings.DecisionModels[RimLLMDecisionProviderCatalog.Gemini] = "gemini-decision-1";
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Gemini] = "gemini-test-key";
        return new RimLLMDecisionService(settings);
    }

    /// <summary>
    /// 只檢查送出的請求：假端點的固定回答不一定對得上這份計畫，SDK 因此拒收回應是預期的，不影響請求斷言。
    /// </summary>
    private static async Task SendIgnoringAnswersAsync(
        string state, JevDecisionPlan plan, RimLLMDecisionService service = null)
    {
        try
        {
            await (service ?? NewGeminiService()).EvaluateAsync(state, plan);
        }
        catch (RimLLMException ex) when (ex.Error == LLMError.InvalidResponse)
        {
        }
    }

    [Test]
    public void Catalog_GeminiDefaultsToTheOfficialEndpoint()
    {
        ClassicAssert.AreEqual(GeminiEndpoint,
            RimLLMDecisionProviderCatalog.GetDefaultEndpoint(RimLLMDecisionProviderCatalog.Gemini));
    }

    [Test]
    public void Targets_OnlyMatchesTheGeminiHost()
    {
        ClassicAssert.IsTrue(GeminiDecisionsAdapterHandler.Targets(GeminiEndpoint));
        ClassicAssert.IsFalse(GeminiDecisionsAdapterHandler.Targets("https://decisions-api.dev"));
        ClassicAssert.IsFalse(GeminiDecisionsAdapterHandler.Targets("not a uri"));
    }

    [Test]
    public void NormalizeBaseEndpoint_StripsPastedGeminiPaths()
    {
        ClassicAssert.AreEqual(GeminiEndpoint, RimLLMDecisionService.NormalizeBaseEndpoint(
            GeminiEndpoint + "/v1beta/decisions", RimLLMDecisionProviderCatalog.Gemini));
        ClassicAssert.AreEqual(GeminiEndpoint, RimLLMDecisionService.NormalizeBaseEndpoint(
            GeminiEndpoint + "/v1beta/", RimLLMDecisionProviderCatalog.Gemini));
    }

    [Test]
    public async Task Evaluate_SendsTheGeminiWireFormat()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("is_damaged", "Did the item arrive damaged?",
            new JevNoulCriteria("The product is broken.", "Preference complaint."));
        builder.AddChoice<string>("queue", "Which queue?", new[]
        {
            new JevChoiceOption<string>("billing", "billing", "Payment questions."),
            new JevChoiceOption<string>("general", "general")
        });
        builder.AddScore("severity", "How severe?", new JevContent[] { "Cosmetic", "Blocked" });

        await SendIgnoringAnswersAsync("The mug arrived cracked.", builder.Build());

        ClassicAssert.AreEqual(1, _handler.Requests.Count);
        CapturedRequest sent = _handler.Requests[0];
        ClassicAssert.AreEqual("https://generativelanguage.googleapis.com/v1beta/decisions", sent.Uri.ToString());
        ClassicAssert.AreEqual("gemini-test-key", sent.GoogApiKey);
        ClassicAssert.IsNull(sent.Authorization, "Gemini 不接受 Bearer，金鑰不得留在 Authorization。");

        using JsonDocument body = JsonDocument.Parse(sent.Body);
        JsonElement root = body.RootElement;
        ClassicAssert.AreEqual("gemini-decision-1", root.GetProperty("model").GetString());
        ClassicAssert.AreEqual("The mug arrived cracked.", root.GetProperty("input").GetString());
        ClassicAssert.IsFalse(root.TryGetProperty("state", out _));

        JsonElement questions = root.GetProperty("questions");
        JsonElement noul = questions.GetProperty("is_damaged");
        ClassicAssert.AreEqual("noul", noul.GetProperty("type").GetString());
        ClassicAssert.AreEqual("The product is broken.",
            noul.GetProperty("criteria").GetProperty("true_description").GetString());
        ClassicAssert.AreEqual("Preference complaint.",
            noul.GetProperty("criteria").GetProperty("false_description").GetString());

        JsonElement choice = questions.GetProperty("queue").GetProperty("criteria");
        ClassicAssert.AreEqual("Payment questions.", choice.GetProperty("billing").GetString());
        // Jev 的 null 描述必須換成 Gemini 規定的空字串。
        ClassicAssert.AreEqual(JsonValueKind.String, choice.GetProperty("general").ValueKind);
        ClassicAssert.AreEqual("", choice.GetProperty("general").GetString());

        JsonElement score = questions.GetProperty("severity").GetProperty("criteria");
        ClassicAssert.AreEqual(2, score.GetArrayLength());
        ClassicAssert.AreEqual("Cosmetic", score[0].GetString());
    }

    [Test]
    public void ObjectState_IsSentAsJsonText()
    {
        // Jev 對物件型 state 送出的是 JSON 物件；Gemini 的 input 只收文字，必須序列化成字串。
        string jevBody = "{\"model\":\"gemini-decision-1\",\"state\":{\"order\":1}," +
                         "\"questions\":{\"q\":{\"type\":\"noul\",\"instructions\":\"Is it?\"}}}";

        using JsonDocument body = JsonDocument.Parse(GeminiDecisionsAdapterHandler.ToGeminiRequest(jevBody, out _));
        JsonElement input = body.RootElement.GetProperty("input");
        ClassicAssert.AreEqual(JsonValueKind.String, input.ValueKind);
        ClassicAssert.AreEqual("{\"order\":1}", input.GetString());
    }

    [Test]
    public void ResponseWithoutUsage_GetsZeroUsage()
    {
        using JsonDocument body = JsonDocument.Parse(GeminiDecisionsAdapterHandler.ToJevResult(
            "{\"answers\":{\"q\":{\"type\":\"noul\",\"noul\":0.5}}}", "gemini-decision-1"));
        JsonElement usage = body.RootElement.GetProperty("usage");
        ClassicAssert.AreEqual(0, usage.GetProperty("input_tokens").GetInt32());
        ClassicAssert.AreEqual(0, usage.GetProperty("output_tokens").GetInt32());
    }

    [Test]
    public async Task MalformedSuccessBody_MapsToInvalidResponse()
    {
        _handler.EvaluateBody = "<html>captive portal</html>";
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("q", "Is it?", default);

        var ex = await ClassicAssert.ThrowsAsync<RimLLMException>(() =>
            NewGeminiService().EvaluateAsync("state", builder.Build()));
        ClassicAssert.AreEqual(LLMError.InvalidResponse, ex!.Error);
    }

    [Test]
    public void NormalizeBaseEndpoint_KeepsV1betaForOtherGateways()
    {
        ClassicAssert.AreEqual("https://gw.example/v1beta", RimLLMDecisionService.NormalizeBaseEndpoint(
            "https://gw.example/v1beta", RimLLMDecisionProviderCatalog.Local));
    }

    [Test]
    public async Task Evaluate_ParsesGeminiAnswersAndUsage()
    {
        var builder = new JevDecisionPlanBuilder();
        JevNoulHandle damaged = builder.AddNoul("is_damaged", "Did the item arrive damaged?", default);
        JevChoiceHandle<string> queue = builder.AddChoice<string>("queue", "Which queue?", new[]
        {
            new JevChoiceOption<string>("billing", "billing"),
            new JevChoiceOption<string>("replacements", "replacements")
        });
        JevScoreHandle severity = builder.AddScore("severity", "How severe?", new JevContent[] { "Low", "High" });

        JevResult result = await NewGeminiService().EvaluateAsync("The mug arrived cracked.", builder.Build());

        ClassicAssert.AreEqual(0.99, result.Get(damaged).Probability, 1e-9);
        ClassicAssert.AreEqual("replacements", result.Get(queue).Value);
        ClassicAssert.AreEqual(0.3, result.Get(severity).Value, 1e-9);
        ClassicAssert.AreEqual(100, result.Usage.InputTokens);
        ClassicAssert.AreEqual(0, result.Usage.OutputTokens);
    }

    [Test]
    public async Task FetchModels_UsesGeminiModelListAndKeepsOnlyDecisionModels()
    {
        IReadOnlyList<string> models =
            await NewGeminiService().FetchDecisionModelsAsync(RimLLMDecisionProviderCatalog.Gemini);

        CapturedRequest sent = _handler.Requests[0];
        ClassicAssert.AreEqual("/v1beta/models", sent.Uri.AbsolutePath);
        ClassicAssert.AreEqual("gemini-test-key", sent.GoogApiKey);
        CollectionAssert.AreEqual(new[] { "gemini-decision-1" }, models);
    }

    [Test]
    public async Task Evaluate_GeminiErrorStatus_MapsToRimLLMException()
    {
        _handler.StatusCode = HttpStatusCode.Unauthorized;
        _handler.EvaluateBody = "{\"error\":{\"code\":401,\"message\":\"API key not valid.\"}}";
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("q", "Is it?", default);

        var ex = await ClassicAssert.ThrowsAsync<RimLLMException>(() =>
            NewGeminiService().EvaluateAsync("state", builder.Build()));
        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
    }

    // ---------- 沿用對話的 Gemini 金鑰（一把金鑰設好對話、Embedding、決策）----------

    private static MockSettings NewSettingsWithOnlyChatKey()
    {
        MockSettings settings = DecisionTestDoubles.NewSettings();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.Gemini;
        settings.DecisionModels[RimLLMDecisionProviderCatalog.Gemini] = "gemini-decision-1";
        settings.ApiKeys[ProviderIds.Gemini] = "chat-gemini-key";
        return settings;
    }

    [Test]
    public async Task EmptyDecisionKey_ReusesTheChatGeminiKey()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("q", "Is it?", default);

        await SendIgnoringAnswersAsync("state", builder.Build(), new RimLLMDecisionService(NewSettingsWithOnlyChatKey()));

        ClassicAssert.AreEqual("chat-gemini-key", _handler.Requests[0].GoogApiKey);
    }

    [Test]
    public void EmptyDecisionKey_WithChatGeminiKey_ReportsAKey()
    {
        MockSettings settings = NewSettingsWithOnlyChatKey();
        var service = new RimLLMDecisionService(settings);

        ClassicAssert.IsTrue(service.GetAvailability().HasKey);
        ClassicAssert.AreEqual("chat-gemini-key",
            service.GetPreviewArguments(RimLLMDecisionProviderCatalog.Gemini).ApiKey);
    }

    [Test]
    public void ExplicitDecisionKey_WinsOverTheChatKey()
    {
        MockSettings settings = NewSettingsWithOnlyChatKey();
        settings.DecisionApiKeys[RimLLMDecisionProviderCatalog.Gemini] = "decision-only-key";

        ClassicAssert.AreEqual("decision-only-key",
            RimLLMDecisionService.GetEffectiveApiKey(settings, RimLLMDecisionProviderCatalog.Gemini));
    }

    [Test]
    public void OtherDecisionProviders_DoNotInheritTheChatGeminiKey()
    {
        MockSettings settings = NewSettingsWithOnlyChatKey();
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.TypeSafe;

        ClassicAssert.AreEqual("",
            RimLLMDecisionService.GetEffectiveApiKey(settings, RimLLMDecisionProviderCatalog.TypeSafe));
        ClassicAssert.IsFalse(new RimLLMDecisionService(settings).GetAvailability().HasKey);
    }

    private sealed record CapturedRequest(Uri Uri, string Authorization, string GoogApiKey, string Body);

    /// <summary>扮演 Gemini Decisions API 的假端點，回應取自官方文件的範例形狀。</summary>
    private sealed class FakeGeminiHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public string EvaluateBody { get; set; } =
            "{\"answers\":{" +
            "\"is_damaged\":{\"type\":\"noul\",\"noul\":0.99,\"confidence\":0.99}," +
            "\"queue\":{\"type\":\"choice\",\"choice\":\"replacements\"," +
            "\"probabilities\":{\"billing\":0.1,\"replacements\":0.9},\"confidence\":0.9}," +
            "\"severity\":{\"type\":\"score\",\"score\":0.3,\"legend\":{\"0\":\"Low\",\"1\":\"High\"}," +
            "\"probabilities\":{\"0\":0.7,\"1\":0.3},\"confidence\":0.7}}," +
            "\"usage\":{\"total_input_tokens\":100," +
            "\"input_tokens_by_modality\":[{\"modality\":\"text\",\"tokens\":100}]," +
            "\"total_output_tokens\":0,\"total_tokens\":100}}";

        public string ModelsBody { get; set; } =
            "{\"models\":[{\"name\":\"models/gemini-4-argon\",\"description\":\"chat\"}," +
            "{\"name\":\"models/gemini-decision-1\",\"description\":\"Gemini Decision\"}]}";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string googKey = request.Headers.TryGetValues("x-goog-api-key", out IEnumerable<string> values)
                ? string.Join(",", values)
                : null;
            Requests.Add(new CapturedRequest(
                request.RequestUri, request.Headers.Authorization?.ToString(), googKey, body));

            bool isModels = request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal);
            return new HttpResponseMessage(isModels ? HttpStatusCode.OK : StatusCode)
            {
                Content = new StringContent(isModels ? ModelsBody : EvaluateBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
