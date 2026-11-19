using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager.Services;
using TypeSafeAI;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 決策服務的錯誤契約測試：所有從 TypeSafeAI SDK / HTTP 層冒出的例外都必須收斂成
/// RimLLMException（並帶 LLMError 代碼），呼叫端取消才維持 OperationCanceledException。
/// </summary>
[TestFixture]
public class DecisionServiceErrorTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoHeaders =
        new Dictionary<string, IReadOnlyList<string>>();

    private static Exception Translate(Exception ex)
    {
        return RimLLMDecisionService.TranslateDecisionError(ex);
    }

    private static TypeSafeApiException ApiError(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        return TypeSafeApiException.Create(status, null, NoHeaders, "req-1", retryAfter);
    }

    /// <summary>反覆建立含一題 Noul 的決策計畫；空計畫會被 SDK 拒收，不是要測的路徑。</summary>
    private static QuestionSet OneQuestionPlan()
    {
        var plan = new QuestionSet();
        plan.Noul("question?", id: "q");
        return plan;
    }

    [Test]
    public void ApiException_IsMappedByStatusCode_ThroughLlmErrorMapper()
    {
        // 401 → InvalidKey
        var unauthorized = (RimLLMException)Translate(ApiError(HttpStatusCode.Unauthorized));
        ClassicAssert.AreEqual(LLMError.InvalidKey, unauthorized.Error);
        ClassicAssert.AreEqual(401, unauthorized.HttpStatusCode);

        // 429 → RateLimit；Retry-After 必須保留，讓呼叫端的重試邏輯能運作
        var rateLimit = (RimLLMException)Translate(ApiError(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7)));
        ClassicAssert.AreEqual(LLMError.RateLimit, rateLimit.Error);
        ClassicAssert.AreEqual(429, rateLimit.HttpStatusCode);
        ClassicAssert.AreEqual(TimeSpan.FromSeconds(7), rateLimit.RetryAfter);
        ClassicAssert.IsNotNull(rateLimit.InnerException);

        // 404 → ModelNotFound
        var notFound = (RimLLMException)Translate(ApiError(HttpStatusCode.NotFound));
        ClassicAssert.AreEqual(LLMError.ModelNotFound, notFound.Error);

        // 500 → ProviderOffline
        var offline = (RimLLMException)Translate(ApiError(HttpStatusCode.InternalServerError));
        ClassicAssert.AreEqual(LLMError.ProviderOffline, offline.Error);
    }

    [Test]
    public void TimeoutException_MapsToTimeout()
    {
        // TypeSafeTimeoutException 繼承 TypeSafeConnectionException，不能被當成網路錯誤。
        var timeout = (RimLLMException)Translate(new TypeSafeTimeoutException("deadline", null));
        ClassicAssert.AreEqual(LLMError.Timeout, timeout.Error);
        ClassicAssert.IsNotNull(timeout.InnerException);
    }

    [Test]
    public void ConnectionException_MapsToNetworkError()
    {
        var transport = (RimLLMException)Translate(new TypeSafeConnectionException(
            "connection refused", new System.Net.Http.HttpRequestException("connection refused")));
        ClassicAssert.AreEqual(LLMError.NetworkError, transport.Error);
        ClassicAssert.IsNotNull(transport.InnerException);
    }

    [Test]
    public void ResponseValidationException_MapsToInvalidResponse()
    {
        var protocol = (RimLLMException)Translate(new TypeSafeResponseValidationException("malformed json"));
        ClassicAssert.AreEqual(LLMError.InvalidResponse, protocol.Error);
    }

    [Test]
    public void OtherSdkException_MapsToUnknown()
    {
        var other = (RimLLMException)Translate(new TypeSafeException("BaseUrl must be an absolute URI."));
        ClassicAssert.AreEqual(LLMError.Unknown, other.Error);
    }

    [Test]
    public void PlainTimeoutException_MapsToTimeout()
    {
        var timeout = (RimLLMException)Translate(new TimeoutException("deadline"));
        ClassicAssert.AreEqual(LLMError.Timeout, timeout.Error);
    }

    [Test]
    public void MissingKeyFromSdkOptions_MapsToInvalidKey_WithoutLeakingTheKey()
    {
        // TypeSafeClient 建立時發現沒有金鑰，會丟出這則訊息的 TypeSafeException（屬 SDK 的內部檢查）。
        var invalidKey = (RimLLMException)Translate(new TypeSafeException(
            "No API key configured. Set TypeSafeClientOptions.ApiKey."));
        ClassicAssert.AreEqual(LLMError.InvalidKey, invalidKey.Error);
        StringAssert.DoesNotContain("Bearer", invalidKey.Message, "錯誤訊息不得包含推測的金鑰內容");

        // 與 SDK 無關的 InvalidOperationException 退回 Unknown 即可。
        var unknown = (RimLLMException)Translate(new InvalidOperationException("unrelated"));
        ClassicAssert.AreEqual(LLMError.Unknown, unknown.Error);
    }

    [Test]
    public void GenericException_MapsToUnknown()
    {
        var unknown = (RimLLMException)Translate(new Exception("boom <script>"));
        ClassicAssert.AreEqual(LLMError.Unknown, unknown.Error);
    }

    [Test]
    public async Task DisabledProvider_ThrowsRimLLMExceptionNotInvalidOperationException()
    {
        var settings = new MockSettings();

        var service = new RimLLMDecisionService(settings);
        var ex = await ClassicAssert.ThrowsAsync<RimLLMException>(() =>
            service.EvaluateAsync("state", OneQuestionPlan()));
        ClassicAssert.AreEqual(LLMError.Unknown, ex!.Error);
        ClassicAssert.AreEqual("Disabled", settings.DecisionProvider);
    }

    [Test]
    public async Task EnabledProviderWithoutKey_ThrowsInvalidKey()
    {
        var settings = new MockSettings();
        settings.DecisionProvider = "TypeSafe";

        var service = new RimLLMDecisionService(settings);
        var ex = await ClassicAssert.ThrowsAsync<RimLLMException>(() =>
            service.EvaluateAsync("state", OneQuestionPlan()));
        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
    }

    [Test]
    public async Task TestConnectionWithoutKey_FailsBeforeAnyNetworkCall()
    {
        var service = new RimLLMDecisionService(new MockSettings());
        var ex = await ClassicAssert.ThrowsAsync<RimLLMException>(() =>
            service.TestConnectionAsync("https://api.typesafe.ai", "jev-latest", ""));
        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
    }

    [Test]
    public async Task FetchModelsWithoutKey_FailsBeforeAnyNetworkCall()
    {
        var service = new RimLLMDecisionService(new MockSettings());
        var ex = await ClassicAssert.ThrowsAsync<RimLLMException>(() =>
            service.FetchModelsAsync("https://api.typesafe.ai", "jev-latest", ""));
        ClassicAssert.AreEqual(LLMError.InvalidKey, ex!.Error);
    }
}
