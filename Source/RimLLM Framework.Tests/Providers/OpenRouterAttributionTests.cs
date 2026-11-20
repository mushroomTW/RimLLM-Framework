using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

/// <summary>
/// OpenRouter 應用歸屬：Mod 中繼資料解析、歸屬層的 AsyncLocal 範圍，以及實際送出的標頭。
/// </summary>
[TestFixture]
public class OpenRouterAttributionTests
{
    private const string RimAgentUrl = "https://example.com/rimagent";

    [Test]
    public void FromMetadata_WithHttpUrl_UsesModNameAndUrl()
    {
        var attribution = RimLLMAppAttribution.FromMetadata("RimAgent", "greenmushroom.rimagent", RimAgentUrl);

        ClassicAssert.AreEqual("RimAgent", attribution.Title);
        ClassicAssert.AreEqual(RimAgentUrl, attribution.Url);
    }

    /// <summary>About.xml 的值可能跨行；應先去頭尾空白再檢查，而不是因換行字元退回框架身分。</summary>
    [Test]
    public void FromMetadata_TrimsWhitespaceBeforeValidating()
    {
        var attribution = RimLLMAppAttribution.FromMetadata("\n  RimAgent\n", "greenmushroom.rimagent", "\n    " + RimAgentUrl + "\n  ");

        ClassicAssert.AreEqual("RimAgent", attribution.Title);
        ClassicAssert.AreEqual(RimAgentUrl, attribution.Url);
    }

    /// <summary>OpenRouter 以網址識別 App：沒網址的 Mod 若掛在框架網址下，會和框架與其他 Mod 混成同一個 App。</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("steam://url/CommunityFilePage/1")]
    [TestCase("not a url")]
    public void FromMetadata_WithoutHttpUrl_FallsBackToFramework(string url)
    {
        ClassicAssert.AreSame(
            RimLLMAppAttribution.Framework,
            RimLLMAppAttribution.FromMetadata("Some Mod", "author.somemod", url));
    }

    /// <summary>非 ASCII 標頭值會讓 SocketsHttpHandler 擲出例外，名稱必須改用 packageId。</summary>
    [Test]
    public void FromMetadata_NonAsciiName_UsesPackageId()
    {
        var attribution = RimLLMAppAttribution.FromMetadata("對話模組", "author.talk", "https://example.com");

        ClassicAssert.AreEqual("author.talk", attribution.Title);
    }

    [Test]
    public void FromMetadata_StripsRichTextTags()
    {
        var attribution = RimLLMAppAttribution.FromMetadata("<color=#ffcc00>Cool Mod</color>", "author.cool", "https://example.com");

        ClassicAssert.AreEqual("Cool Mod", attribution.Title);
    }

    [Test]
    public void CreateResolver_MatchesPackageIdIgnoringCase()
    {
        var resolve = RimLLMAppAttribution.CreateResolver([("GreenMushroom.RimAgent", "RimAgent", RimAgentUrl)]);

        ClassicAssert.AreEqual("RimAgent", resolve("greenmushroom.rimagent").Title);
        ClassicAssert.AreSame(RimLLMAppAttribution.Framework, resolve("unknown.mod"));
        ClassicAssert.AreSame(RimLLMAppAttribution.Framework, resolve(null));
    }

    /// <summary>共用網址的 Mod 在 OpenRouter 上是同一個 App，顯示名稱會互相覆寫，因此歸屬框架。</summary>
    [Test]
    public void CreateResolver_SharedOrFrameworkUrl_FallsBackToFramework()
    {
        var resolve = RimLLMAppAttribution.CreateResolver(
        [
            ("author.a", "Mod A", "https://github.com/author"),
            ("author.b", "Mod B", "https://github.com/author/"),
            ("other.c", "Mod C", RimLLMAppAttribution.Framework.Url),
            ("other.d", "Mod D", "https://example.com/d")
        ]);

        ClassicAssert.AreSame(RimLLMAppAttribution.Framework, resolve("author.a"));
        ClassicAssert.AreSame(RimLLMAppAttribution.Framework, resolve("author.b"));
        ClassicAssert.AreSame(RimLLMAppAttribution.Framework, resolve("other.c"));
        ClassicAssert.AreEqual("Mod D", resolve("other.d").Title);
    }

    [Test]
    public void Current_Unset_IsFramework()
    {
        ClassicAssert.AreSame(RimLLMAppAttribution.Framework, RimLLMAppAttribution.Current);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AttributionClient_ScopesAttributionToTheRequest(bool streaming)
    {
        var inner = new AttributionRecordingChatClient();
        var attribution = new RimLLMAppAttribution("RimAgent", RimAgentUrl);
        using var client = new RimLLMAttributionChatClient(inner, attribution);
        var messages = new[] { new ChatMessage(ChatRole.User, "hi") };

        if (streaming)
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(messages))
            {
            }
        }
        else
        {
            await client.GetResponseAsync(messages);
        }

        ClassicAssert.AreSame(attribution, inner.Observed, "下游應看到此 Mod 的歸屬");
        ClassicAssert.AreSame(RimLLMAppAttribution.Framework, RimLLMAppAttribution.Current, "請求結束後不得殘留在呼叫端");
    }

    [Test]
    public async Task OpenRouterProvider_SendsAttributionHeaders()
    {
        var settings = new MockSettings();
        settings.ApiKeys[ProviderIds.OpenRouter] = "sk-or-test";
        var provider = new OpenRouterProvider(settings);

        Dictionary<string, string> headers = await SendAndCaptureHeadersAsync(
            provider,
            "openai/gpt-4o-mini",
            new RimLLMAppAttribution("RimAgent", RimAgentUrl));

        ClassicAssert.AreEqual(RimAgentUrl, headers["HTTP-Referer"]);
        ClassicAssert.AreEqual("RimAgent", headers["X-OpenRouter-Title"]);
    }

    [Test]
    public async Task OpenAIProvider_DoesNotSendAttributionHeaders()
    {
        var settings = new MockSettings();
        settings.ApiKeys[ProviderIds.OpenAI] = "sk-test";
        var provider = new OpenAIProvider(settings);

        Dictionary<string, string> headers = await SendAndCaptureHeadersAsync(
            provider,
            "gpt-4o-mini",
            new RimLLMAppAttribution("RimAgent", RimAgentUrl));

        ClassicAssert.IsFalse(headers.ContainsKey("HTTP-Referer"));
        ClassicAssert.IsFalse(headers.ContainsKey("X-OpenRouter-Title"));
    }

    /// <summary>OpenAI 相容供應商指向 openrouter.ai 時，對話也要帶歸屬標頭（依主機判斷，不看供應商類別）。</summary>
    [Test]
    public async Task OpenAICompatibleProvider_PointedAtOpenRouter_SendsAttributionHeaders()
    {
        var settings = new MockSettings();
        settings.ApiKeys[ProviderIds.OpenAICompatible] = "sk-or-test";
        settings.Endpoints[ProviderIds.OpenAICompatible] = "https://openrouter.ai/api/v1";
        var provider = new OpenAICompatibleProvider(settings);

        Dictionary<string, string> headers = await SendAndCaptureHeadersAsync(
            provider,
            "openai/gpt-4o-mini",
            new RimLLMAppAttribution("RimAgent", RimAgentUrl));

        ClassicAssert.AreEqual(RimAgentUrl, headers["HTTP-Referer"]);
    }

    /// <summary>經由歸屬層送出一次請求，回傳實際送出的 HTTP 標頭。</summary>
    private static async Task<Dictionary<string, string>> SendAndCaptureHeadersAsync(
        OpenAIProvider provider, string model, RimLLMAppAttribution attribution)
    {
        var handler = new RecordingHttpHandler(_ => ChatCompletionBody);
        using var http = new HttpClient(handler);
        OpenAIProvider.TransportOverride = new System.ClientModel.Primitives.HttpClientPipelineTransport(http);
        try
        {
            using var client = new RimLLMAttributionChatClient(provider.CreateChatClient(model), attribution);
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
            return handler.Last.Headers;
        }
        finally
        {
            OpenAIProvider.TransportOverride = null;
        }
    }

    private const string ChatCompletionBody =
        "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"m\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";

    private sealed class AttributionRecordingChatClient : IChatClient
    {
        public RimLLMAppAttribution Observed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
        {
            Observed = RimLLMAppAttribution.Current;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Observed = RimLLMAppAttribution.Current;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object GetService(System.Type serviceType, object serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
