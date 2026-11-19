using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Compat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 簡中自動化 One 相容層：請求 JSON 解析、回應 JSON 合成、錯誤分類，
/// 以及以「結構同於該 Mod 真實簽章的假型別」驗證 Harmony 補丁綁定（該 Mod 的類別為 internal，無編譯期參考）。
/// 實際掛載到真 Mod 由遊戲內測試 <c>CompatPatchTests</c> 負責。
/// </summary>
[TestFixture]
public class CompatScAutoTests
{
    private const string SampleImageDataUrl = "data:image/png;base64,iVBORw0KGgo=";

    // ---------- 請求解析 ----------

    [Test]
    public void ParsePayload_Chat_ReadsMessagesAndSamplingParameters()
    {
        string json = "{\"model\":\"m\",\"messages\":[{\"role\":\"system\",\"content\":\"sys\"},{\"role\":\"user\",\"content\":\"usr\"}],"
            + "\"temperature\":0.3,\"top_p\":0.9,\"max_tokens\":4096}";

        OpenAICompatClient.RequestSpec spec = OpenAICompatClient.ParsePayload(json, chatProtocol: true);

        ClassicAssert.AreEqual(2, spec.Messages.Count);
        ClassicAssert.AreEqual(ChatRole.System, spec.Messages[0].Role);
        ClassicAssert.AreEqual("sys", spec.Messages[0].Text);
        ClassicAssert.AreEqual(ChatRole.User, spec.Messages[1].Role);
        ClassicAssert.AreEqual(4096, spec.MaxOutputTokens);
        ClassicAssert.AreEqual(0.3f, spec.Temperature.Value, 1e-6);
        ClassicAssert.AreEqual(0.9f, spec.TopP.Value, 1e-6);
    }

    [Test]
    public void ParsePayload_Chat_MaxCompletionTokensIsHonoured()
    {
        OpenAICompatClient.RequestSpec spec = OpenAICompatClient.ParsePayload(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"max_completion_tokens\":777}", chatProtocol: true);
        ClassicAssert.AreEqual(777, spec.MaxOutputTokens);
    }

    [Test]
    public void ParsePayload_Chat_ImagePartBecomesDataContent()
    {
        string json = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"look\"},"
            + "{\"type\":\"image_url\",\"image_url\":{\"url\":\"" + SampleImageDataUrl + "\"}}]}]}";

        OpenAICompatClient.RequestSpec spec = OpenAICompatClient.ParsePayload(json, chatProtocol: true);

        IList<AIContent> contents = spec.Messages.Single().Contents;
        ClassicAssert.AreEqual(2, contents.Count);
        ClassicAssert.AreEqual("look", ((TextContent)contents[0]).Text);
        var image = (DataContent)contents[1];
        ClassicAssert.AreEqual("image/png", image.MediaType);
        ClassicAssert.IsNotEmpty(image.Data.ToArray());
    }

    [Test]
    public void ParsePayload_Responses_StringInputWithInstructions()
    {
        OpenAICompatClient.RequestSpec spec = OpenAICompatClient.ParsePayload(
            "{\"instructions\":\"rules\",\"input\":\"translate me\",\"max_output_tokens\":1234}", chatProtocol: false);

        ClassicAssert.AreEqual(2, spec.Messages.Count);
        ClassicAssert.AreEqual(ChatRole.System, spec.Messages[0].Role);
        ClassicAssert.AreEqual("rules", spec.Messages[0].Text);
        ClassicAssert.AreEqual(ChatRole.User, spec.Messages[1].Role);
        ClassicAssert.AreEqual("translate me", spec.Messages[1].Text);
        ClassicAssert.AreEqual(1234, spec.MaxOutputTokens);
    }

    [Test]
    public void ParsePayload_Responses_ItemArrayWithInputTextAndDeveloperRole()
    {
        string json = "{\"input\":[{\"role\":\"developer\",\"content\":\"dev\"},"
            + "{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"a\"},"
            + "{\"type\":\"input_image\",\"image_url\":\"" + SampleImageDataUrl + "\"}]}]}";

        OpenAICompatClient.RequestSpec spec = OpenAICompatClient.ParsePayload(json, chatProtocol: false);

        ClassicAssert.AreEqual(2, spec.Messages.Count);
        ClassicAssert.AreEqual(ChatRole.System, spec.Messages[0].Role);
        ClassicAssert.AreEqual(2, spec.Messages[1].Contents.Count);
        ClassicAssert.IsInstanceOf<DataContent>(spec.Messages[1].Contents[1]);
    }

    [Test]
    public void ParsePayload_NoMessages_Throws()
    {
        Assert.Throws<FormatException>(() => OpenAICompatClient.ParsePayload("{\"model\":\"m\"}", chatProtocol: true));
        Assert.Throws<FormatException>(() => OpenAICompatClient.ParsePayload("[]", chatProtocol: true));
        Assert.Catch<JsonException>(() => OpenAICompatClient.ParsePayload("not json", chatProtocol: true));
    }

    // ---------- 回應合成 ----------

    [Test]
    public void BuildSuccessJson_Chat_HasChoicesMessageContent()
    {
        using JsonDocument doc = JsonDocument.Parse(OpenAICompatClient.BuildSuccessJson("譯文 \"q\"", chatProtocol: true));
        JsonElement choice = doc.RootElement.GetProperty("choices")[0];
        ClassicAssert.AreEqual("譯文 \"q\"", choice.GetProperty("message").GetProperty("content").GetString());
        ClassicAssert.AreEqual("stop", choice.GetProperty("finish_reason").GetString());
    }

    [Test]
    public void BuildSuccessJson_Responses_HasCompletedStatusAndOutputText()
    {
        using JsonDocument doc = JsonDocument.Parse(OpenAICompatClient.BuildSuccessJson("譯文", chatProtocol: false));
        ClassicAssert.AreEqual("completed", doc.RootElement.GetProperty("status").GetString());
        JsonElement part = doc.RootElement.GetProperty("output")[0].GetProperty("content")[0];
        ClassicAssert.AreEqual("output_text", part.GetProperty("type").GetString());
        ClassicAssert.AreEqual("譯文", part.GetProperty("text").GetString());
    }

    [Test]
    public void BuildErrorJson_Responses_AlsoMarksStatusFailed()
    {
        using JsonDocument chat = JsonDocument.Parse(OpenAICompatClient.BuildErrorJson("network", "boom", chatProtocol: true));
        using JsonDocument responses = JsonDocument.Parse(OpenAICompatClient.BuildErrorJson("network", "boom", chatProtocol: false));
        ClassicAssert.AreEqual("network", chat.RootElement.GetProperty("error").GetProperty("type").GetString());
        ClassicAssert.IsFalse(chat.RootElement.TryGetProperty("status", out _));
        ClassicAssert.AreEqual("failed", responses.RootElement.GetProperty("status").GetString());
        ClassicAssert.AreEqual("boom", responses.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [TestCase(LLMError.Timeout, "timeout")]
    [TestCase(LLMError.RateLimit, "rate_limit")]
    [TestCase(LLMError.NetworkError, "network")]
    [TestCase(LLMError.ProviderOffline, "network")]
    [TestCase(LLMError.ContextWindowExceeded, "context_exceeded")]
    [TestCase(LLMError.InvalidResponse, "parse")]
    public void MapErrorType_TransientAndKnownKinds(LLMError error, string expected)
    {
        ClassicAssert.AreEqual(expected, OpenAICompatClient.MapErrorType(error));
    }

    [TestCase(LLMError.InvalidKey)]
    [TestCase(LLMError.QuotaExceeded)]
    [TestCase(LLMError.ModelNotFound)]
    [TestCase(LLMError.ContentFilter)]
    [TestCase(LLMError.Cancelled)]
    [TestCase(LLMError.Unknown)]
    public void MapErrorType_PermanentKindsAvoidTransientKeywords(LLMError error)
    {
        // 該 Mod 以子字串判斷暫時性錯誤（network／timeout／TooManyRequests／rate_limit／server／service_unavailable），
        // 永久性錯誤的名稱不能含這些字樣，否則它會白白重試。
        string mapped = OpenAICompatClient.MapErrorType(error);
        foreach (string keyword in new[] { "network", "timeout", "toomanyrequests", "rate_limit", "server", "service_unavailable" })
        {
            StringAssert.DoesNotContain(keyword, mapped.ToLowerInvariant());
        }
    }

    // ---------- CompleteAsync ----------

    [Test]
    public async Task CompleteAsync_Chat_ForwardsMessagesAndReturnsChatJson()
    {
        var fake = new CapturingChatClient { ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "譯")) };
        var client = new OpenAICompatClient(fake);

        string json = await client.CompleteAsync(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"temperature\":0.5,\"max_tokens\":99}", true, CancellationToken.None);

        ClassicAssert.AreEqual("hi", fake.ReceivedMessages.Single().Single().Text);
        ClassicAssert.AreEqual(99, fake.ReceivedOptions.Single().MaxOutputTokens);
        ClassicAssert.AreEqual(0.5f, fake.ReceivedOptions.Single().Temperature.Value, 1e-6);
        using JsonDocument doc = JsonDocument.Parse(json);
        ClassicAssert.AreEqual("譯", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Test]
    public async Task CompleteAsync_NoMaxTokensInRequest_UsesDefaultOutputLimit()
    {
        var fake = new CapturingChatClient { ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "x")) };

        await new OpenAICompatClient(fake).CompleteAsync("{\"input\":\"hi\"}", false, CancellationToken.None);

        ClassicAssert.AreEqual(OpenAICompatClient.DefaultMaxOutputTokens, fake.ReceivedOptions.Single().MaxOutputTokens);
    }

    [Test]
    public async Task CompleteAsync_ProviderFailure_BecomesErrorJsonNotException()
    {
        var fake = new CapturingChatClient { ResponseException = new RimLLMException(LLMError.RateLimit, "slow down") };

        string json = await new OpenAICompatClient(fake).CompleteAsync(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", true, CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement error = doc.RootElement.GetProperty("error");
        ClassicAssert.AreEqual("rate_limit", error.GetProperty("type").GetString());
        StringAssert.Contains("slow down", error.GetProperty("message").GetString());
    }

    [Test]
    public async Task CompleteAsync_MalformedPayload_BecomesParseError()
    {
        var fake = new CapturingChatClient();

        string json = await new OpenAICompatClient(fake).CompleteAsync("garbage", true, CancellationToken.None);

        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            ClassicAssert.AreEqual("parse", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        }
        ClassicAssert.IsEmpty(fake.ReceivedMessages);
    }

    [Test]
    public async Task CompleteAsync_CallerCancellation_Propagates()
    {
        var fake = new CapturingChatClient { ResponseException = new OperationCanceledException() };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new OpenAICompatClient(fake).CompleteAsync(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", true, cts.Token));
    }

    // ---------- 本機端點的輔助判斷 ----------

    [TestCase("rate_limit", 429)]
    [TestCase("quota_exceeded", 429)]
    [TestCase("invalid_key", 401)]
    [TestCase("model_not_found", 404)]
    [TestCase("timeout", 504)]
    [TestCase("network", 502)]
    [TestCase("server", 502)]
    [TestCase("parse", 400)]
    public void Endpoint_ErrorJson_MapsToHttpStatus(string errorType, int expected)
    {
        string json = OpenAICompatClient.BuildErrorJson(errorType, "x", true);
        ClassicAssert.IsTrue(LocalOpenAIEndpoint.TryGetErrorStatus(json, out int status));
        ClassicAssert.AreEqual(expected, status);
    }

    [Test]
    public void Endpoint_SuccessJson_IsNotAnError()
    {
        ClassicAssert.IsFalse(LocalOpenAIEndpoint.TryGetErrorStatus(OpenAICompatClient.BuildSuccessJson("ok", true), out _));
    }

    [TestCase("http://127.0.0.1:5123/m/ow1r.scauto/v1", true)]
    [TestCase("http://127.0.0.1:1/v1", true)]
    [TestCase("https://api.openai.com/v1", false)]
    [TestCase("http://127.0.0.1:5123/m/other.mod/v1", false)]
    [TestCase("", false)]
    public void Endpoint_IsOwnBaseUrl_OnlyRecognisesFrameworkAddresses(string url, bool expected)
    {
        ClassicAssert.AreEqual(expected, LocalOpenAIEndpoint.IsOwnBaseUrl(url, "ow1r.scauto"));
    }
}
