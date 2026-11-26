using System;
using System.Collections.Generic;
using System.ClientModel.Primitives;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

/// <summary>Responses API 優先、退回 Chat Completions，以及工具迴圈的推理續傳。</summary>
[TestFixture]
public class ResponsesApiTests
{
    private const string ResponsesToolCall =
        "{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"m\",\"store\":false,\"output\":[" +
        "{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[],\"encrypted_content\":\"ENC\"}," +
        "{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"c1\",\"name\":\"f\",\"arguments\":\"{}\"}]," +
        "\"usage\":{\"input_tokens\":10,\"input_tokens_details\":{\"cached_tokens\":4,\"cache_write_tokens\":3}," +
        "\"output_tokens\":5,\"output_tokens_details\":{\"reasoning_tokens\":2},\"total_tokens\":15}}";

    private const string ResponsesText =
        "{\"id\":\"resp_2\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"m\",\"store\":false,\"output\":[" +
        "{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\",\"annotations\":[]}]}]}";

    private const string ChatText =
        "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}";

    private static string ChatToolCall(string extraMessage, string extraCall = "") =>
        "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"message\":{" +
        "\"role\":\"assistant\",\"content\":null," + extraMessage +
        "\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\"," + extraCall + "\"function\":{\"name\":\"f\",\"arguments\":\"{}\"}}]}}]}";

    private CapturingHttpMessageHandler _wire;
    private HttpClient _http;
    private MockSettings _settings;

    [SetUp]
    public void SetUp()
    {
        _wire = new CapturingHttpMessageHandler { ResponseBody = ResponsesText };
        _http = new HttpClient(_wire);
        OpenAIProvider.TransportOverride = new HttpClientPipelineTransport(_http);
        _settings = new MockSettings();
        foreach (string id in new[] { ProviderIds.OpenAI, ProviderIds.Gemini, ProviderIds.DeepSeek, ProviderIds.Grok, ProviderIds.OpenRouter, ProviderIds.Groq })
        {
            _settings.ApiKeys[id] = "sk-test";
        }
    }

    [TearDown]
    public void TearDown()
    {
        OpenAIProvider.TransportOverride = null;
        _http.Dispose();
    }

    // 學習記憶是全域的：每個測試用自己的模型名稱，才不會互相影響。
    private static string UniqueModel(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");

    private static List<ChatMessage> User() => [new(ChatRole.User, "hi")];

    private static async Task<List<ChatMessage>> ToolRound(IChatClient client, ChatOptions options = null)
    {
        List<ChatMessage> messages = User();
        ChatResponse response = await client.GetResponseAsync(messages, options);
        messages.AddRange(response.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "done")]));
        return messages;
    }

    private JsonNode LastBody => JsonNode.Parse(_wire.LastRequestBody);

    [TestCase(ProviderIds.OpenAI, true)]
    [TestCase(ProviderIds.Grok, true)]
    [TestCase(ProviderIds.OpenRouter, true)]
    [TestCase(ProviderIds.DeepSeek, false)]
    [TestCase(ProviderIds.Gemini, false)]
    [TestCase(ProviderIds.Groq, false)]
    public async Task ChoosesEndpointPerProvider(string providerId, bool responses)
    {
        _wire.ResponseBody = responses ? ResponsesText : ChatText;
        OpenAIProvider provider = providerId switch
        {
            ProviderIds.Grok => new GrokProvider(_settings),
            ProviderIds.OpenRouter => new OpenRouterProvider(_settings),
            ProviderIds.DeepSeek => new DeepSeekProvider(_settings),
            ProviderIds.Gemini => new GeminiProvider(_settings),
            ProviderIds.Groq => new GroqProvider(_settings),
            _ => new OpenAIProvider(_settings)
        };
        using IChatClient client = provider.CreateChatClient(UniqueModel("m"));

        ChatResponse response = await client.GetResponseAsync(User());

        Assert.That(response.Text, Is.EqualTo("ok"));
        Assert.That(_wire.LastRequestUrl, Does.EndWith(responses ? "/responses" : "/chat/completions"));
    }

    [Test]
    public async Task ResponsesRequestIsStatelessAndCarriesEffortAndTools()
    {
        using IChatClient client = new OpenAIProvider(_settings).CreateChatClient(UniqueModel("gpt-5.4"));
        var options = new ChatOptions
        {
            MaxOutputTokens = 500,
            Temperature = 0.3f,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            Tools = [AIFunctionFactory.Create(() => 1, "f")]
        };
        options.AdditionalProperties = new() { [RimLLMChatOptions.PromptCacheKeyKey] = "key" };

        ChatResponse response = await client.GetResponseAsync(User(), options);

        JsonNode body = LastBody;
        Assert.Multiple(() =>
        {
            Assert.That((bool)body["store"], Is.False);
            Assert.That((string)body["reasoning"]["effort"], Is.EqualTo("high"));
            Assert.That((int)body["max_output_tokens"], Is.EqualTo(500));
            Assert.That(body["temperature"], Is.Null, "推理模型不接受 temperature");
            Assert.That(body["include"].AsArray().Select(n => (string)n), Does.Contain("reasoning.encrypted_content"));
            Assert.That((string)body["prompt_cache_key"], Is.EqualTo("key"));
            Assert.That(body["tools"].AsArray(), Has.Count.EqualTo(1));
            Assert.That(response.ConversationId, Is.Null, "有值會讓工具迴圈以為歷史存在伺服端而不再送出");
        });
    }

    [Test]
    public async Task EncryptedReasoningIsReplayedToTheSameModelEvenAfterSerialization()
    {
        _wire.ResponseBody = ResponsesToolCall;
        using IChatClient client = new OpenAIProvider(_settings).CreateChatClient(UniqueModel("gpt-5.4"));
        List<ChatMessage> messages = await ToolRound(client);
        messages = JsonSerializer.Deserialize<List<ChatMessage>>(
            JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions), AIJsonUtilities.DefaultOptions);

        await client.GetResponseAsync(messages);

        JsonNode reasoning = LastBody["input"].AsArray().Single(i => (string)i["type"] == "reasoning");
        Assert.That((string)reasoning["encrypted_content"], Is.EqualTo("ENC"));
        Assert.That((string)reasoning["id"], Is.EqualTo("rs_1"));
        Assert.That(LastBody["input"].AsArray().Any(i => (string)i["type"] == "function_call" && (string)i["call_id"] == "c1"));
    }

    [Test]
    public async Task EncryptedReasoningIsDroppedForAnotherModel()
    {
        _wire.ResponseBody = ResponsesToolCall;
        var provider = new OpenAIProvider(_settings);
        using IChatClient first = provider.CreateChatClient(UniqueModel("gpt-5.4"));
        using IChatClient second = provider.CreateChatClient(UniqueModel("gpt-5.5"));
        List<ChatMessage> messages = await ToolRound(first);

        await second.GetResponseAsync(messages);

        Assert.That(_wire.LastRequestBody, Does.Not.Contain("ENC"));
        Assert.That(_wire.LastRequestBody, Does.Not.Contain("fc_1"), "沒有對應推理項目的伺服端 id 會被拒絕");
        Assert.That(_wire.LastRequestBody, Does.Contain("function_call_output"));
    }

    [Test]
    public async Task StreamedEncryptedReasoningIsReplayed()
    {
        const string sse =
            "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[],\"encrypted_content\":\"ENC\"}}\n\n" +
            "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"sequence_number\":2,\"output_index\":1,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"c1\",\"name\":\"f\",\"arguments\":\"{}\",\"status\":\"completed\"}}\n\n" +
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":3,\"response\":" + ResponsesToolCall + "}\n\n";
        _wire.ResponseBody = sse;
        _wire.ResponseContentType = "text/event-stream";
        using IChatClient client = new OpenAIProvider(_settings).CreateChatClient(UniqueModel("gpt-5.4"));
        List<ChatMessage> messages = User();
        ChatResponse response = await client.GetStreamingResponseAsync(messages).ToChatResponseAsync();
        messages.AddRange(response.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "done")]));
        _wire.ResponseBody = ResponsesText;
        _wire.ResponseContentType = "application/json";

        await client.GetResponseAsync(messages);

        Assert.That(_wire.LastRequestBody, Does.Contain("\"encrypted_content\":\"ENC\""));
        Assert.That(response.Usage.AdditionalCounts[RimLLMChatClientExecutor.CacheWriteTokensKey], Is.EqualTo(3));
    }

    [TestCase(HttpStatusCode.NotImplemented)]
    [TestCase(HttpStatusCode.MethodNotAllowed)]
    public async Task FallsBackToChatCompletionsAndRemembers(HttpStatusCode status)
    {
        _wire.ResponseBody = ChatText;
        _wire.ScriptResponse(status, "{\"error\":{\"message\":\"no such route\"}}");
        using IChatClient client = new OpenAIProvider(_settings).CreateChatClient(UniqueModel("m"));

        await client.GetResponseAsync(User());
        await client.GetResponseAsync(User());

        string[] expected = ["responses", "completions", "completions"];
        Assert.That(_wire.RequestUrls.Select(u => u[(u.LastIndexOf('/') + 1)..]), Is.EqualTo(expected));
    }

    [Test]
    public async Task NotFoundOnTheOfficialEndpointOnlyFallsBackOnce()
    {
        _wire.ResponseBody = ChatText;
        _wire.ScriptResponse(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"No endpoints found\"}}");
        var provider = new OpenRouterProvider(_settings);
        string model = UniqueModel("m");
        using IChatClient client = provider.CreateChatClient(model);

        await client.GetResponseAsync(User());

        Assert.That(RimLLMReasoningSupport.IsResponsesUnsupported(provider.ResponsesKey, model), Is.False);
    }

    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.UnprocessableEntity)]
    public async Task CustomEndpointFallsBackAndRemembers(HttpStatusCode status)
    {
        _settings.Endpoints[ProviderIds.OpenAI] = "http://proxy.test/v1";
        _wire.ResponseBody = ChatText;
        _wire.ScriptResponse(status, "{\"error\":{\"message\":\"bad input\"}}");
        using IChatClient client = new OpenAIProvider(_settings).CreateChatClient(UniqueModel("m"));
        var options = new ChatOptions { AdditionalProperties = new() { [RimLLMChatOptions.PromptCacheKeyKey] = "k" } };

        await client.GetResponseAsync(User(), options);
        await client.GetResponseAsync(User(), options);

        string[] expected = ["responses", "completions", "completions"];
        Assert.That(_wire.RequestUrls.Select(u => u[(u.LastIndexOf('/') + 1)..]), Is.EqualTo(expected));
        Assert.That(string.Concat(_wire.RequestBodies), Does.Not.Contain("prompt_cache_key"));
    }

    [Test]
    public async Task OptionsMissingFromResponsesStayOnChatCompletions()
    {
        _wire.ResponseBody = ChatText;
        using IChatClient client = new OpenAIProvider(_settings).CreateChatClient(UniqueModel("m"));

        await client.GetResponseAsync(User(), new ChatOptions { StopSequences = ["END"] });

        Assert.That(_wire.RequestUrls.Single(), Does.EndWith("/chat/completions"));
        Assert.That(_wire.RequestBodies.Single(), Does.Contain("END"));
    }

    [Test]
    public async Task StreamingFallsBackToChatCompletions()
    {
        _wire.ResponseBody =
            "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        _wire.ResponseContentType = "text/event-stream";
        _wire.ScriptResponse(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"no such route\"}}");
        using IChatClient client = new OpenAIProvider(_settings).CreateChatClient(UniqueModel("m"));

        ChatResponse response = await client.GetStreamingResponseAsync(User()).ToChatResponseAsync();

        Assert.That(response.Text, Is.EqualTo("ok"));
    }

    [Test]
    public async Task MissingModelIsNotRememberedAsMissingEndpoint()
    {
        const string missing = "{\"error\":{\"message\":\"model does not exist\",\"code\":\"model_not_found\"}}";
        string model = UniqueModel("m");
        _wire.ScriptResponse(HttpStatusCode.NotFound, missing);
        _wire.ScriptResponse(HttpStatusCode.NotFound, missing);
        var provider = new OpenAIProvider(_settings);
        using IChatClient client = provider.CreateChatClient(model);

        RimLLMException error = await Assert.ThrowsAsync<RimLLMException>(async () => await client.GetResponseAsync(User()));

        Assert.That(error.Error, Is.EqualTo(LLMError.ModelNotFound));
        Assert.That(RimLLMReasoningSupport.IsResponsesUnsupported(provider.ResponsesKey, model), Is.False);
    }

    [Test]
    public async Task InvalidEncryptedContentIsRetriedWithoutIt()
    {
        _wire.ResponseBody = ResponsesToolCall;
        using IChatClient client = new OpenAIProvider(_settings).CreateChatClient(UniqueModel("gpt-5.4"));
        List<ChatMessage> messages = await ToolRound(client);
        _wire.ScriptResponse(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"The encrypted content could not be verified\",\"code\":\"invalid_encrypted_content\"}}");

        await client.GetResponseAsync(messages);

        Assert.That(_wire.LastRequestBody, Does.Not.Contain("ENC"));
    }

    [Test]
    public async Task FailedResponseIsAnErrorNotAnAnswer()
    {
        _wire.ResponseBody = "{\"id\":\"r\",\"object\":\"response\",\"created_at\":1,\"status\":\"failed\",\"model\":\"m\",\"output\":[],\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"slow down\"}}";
        using IChatClient client = new OpenAIProvider(_settings).CreateChatClient(UniqueModel("m"));

        RimLLMException error = await Assert.ThrowsAsync<RimLLMException>(async () => await client.GetResponseAsync(User()));

        Assert.That(error.Error, Is.EqualTo(LLMError.RateLimit));
    }

    [Test]
    public async Task FailedStreamIsAnErrorNotAnAnswer()
    {
        _wire.ResponseBody = "event: response.failed\ndata: {\"type\":\"response.failed\",\"sequence_number\":1,\"response\":{\"id\":\"r\",\"object\":\"response\",\"created_at\":1,\"status\":\"failed\",\"model\":\"m\",\"output\":[],\"error\":{\"code\":\"server_error\",\"message\":\"boom\"}}}\n\n";
        _wire.ResponseContentType = "text/event-stream";
        using IChatClient client = new OpenAIProvider(_settings).CreateChatClient(UniqueModel("m"));

        RimLLMException error = await Assert.ThrowsAsync<RimLLMException>(async () => await client.GetStreamingResponseAsync(User()).ToChatResponseAsync());

        Assert.That(error.Error, Is.EqualTo(LLMError.ProviderOffline));
    }

    [Test]
    public async Task DeepSeekReasoningContentIsReplayed()
    {
        _wire.ResponseBody = ChatToolCall("\"reasoning_content\":\"THINK\",");
        using IChatClient client = new DeepSeekProvider(_settings).CreateChatClient(UniqueModel("deepseek"));
        List<ChatMessage> messages = await ToolRound(client);

        await client.GetResponseAsync(messages);

        JsonNode assistant = LastBody["messages"].AsArray().Single(m => (string)m["role"] == "assistant");
        Assert.That((string)assistant["reasoning_content"], Is.EqualTo("THINK"));
        Assert.That((string)assistant["tool_calls"][0]["id"], Is.EqualTo("c1"));
    }

    [Test]
    public async Task DeepSeekStreamedReasoningContentIsReplayed()
    {
        _wire.ResponseBody =
            "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"THI\"}}]}\n\n" +
            "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"NK\"}}]}\n\n" +
            "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"f\",\"arguments\":\"{}\"}}]}}]}\n\n" +
            "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
        _wire.ResponseContentType = "text/event-stream";
        using IChatClient client = new DeepSeekProvider(_settings).CreateChatClient(UniqueModel("deepseek"));
        List<ChatMessage> messages = User();
        messages.AddRange((await client.GetStreamingResponseAsync(messages).ToChatResponseAsync()).Messages);
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "done")]));
        _wire.ResponseBody = ChatText;
        _wire.ResponseContentType = "application/json";

        await client.GetResponseAsync(messages);

        JsonNode assistant = LastBody["messages"].AsArray().Single(m => (string)m["role"] == "assistant");
        Assert.That((string)assistant["reasoning_content"], Is.EqualTo("THINK"));
    }

    [Test]
    public async Task ReasoningIsNotSentToProvidersThatDoNotAskForIt()
    {
        _wire.ResponseBody = ChatToolCall("\"reasoning_content\":\"THINK\",");
        using IChatClient client = new GroqProvider(_settings).CreateChatClient(UniqueModel("m"));
        List<ChatMessage> messages = await ToolRound(client);

        await client.GetResponseAsync(messages);

        Assert.That(_wire.LastRequestBody, Does.Not.Contain("THINK"));
    }

    [Test]
    public async Task OpenRouterChatFallbackReplaysReasoningDetails()
    {
        _wire.ResponseBody = ChatToolCall("\"reasoning\":\"THINK\",\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"data\":\"BLOB\"}],");
        using IChatClient client = new OpenRouterProvider(_settings).CreateChatClient("a/" + UniqueModel("m") + ",b/m");
        List<ChatMessage> messages = await ToolRound(client);

        await client.GetResponseAsync(messages);

        JsonNode assistant = LastBody["messages"].AsArray().Single(m => (string)m["role"] == "assistant");
        Assert.That((string)assistant["reasoning_details"][0]["data"], Is.EqualTo("BLOB"));
    }

    [Test]
    public async Task GeminiThoughtSignatureIsReplayed()
    {
        _wire.ResponseBody = ChatToolCall(string.Empty, "\"extra_content\":{\"google\":{\"thought_signature\":\"SIG\"}},");
        using IChatClient client = new GeminiProvider(_settings).CreateChatClient(UniqueModel("gemini-3-pro"));
        List<ChatMessage> messages = await ToolRound(client);

        await client.GetResponseAsync(messages);

        JsonNode call = LastBody["messages"].AsArray().Single(m => (string)m["role"] == "assistant")["tool_calls"][0];
        Assert.That((string)call["extra_content"]["google"]["thought_signature"], Is.EqualTo("SIG"));
    }

    [TestCase("gemini-3-pro", true)]
    [TestCase("gemini-2.5-flash", false)]
    public async Task GeminiGetsThePlaceholderSignatureForForeignCalls(string prefix, bool expected)
    {
        _wire.ResponseBody = ChatText;
        using IChatClient client = new GeminiProvider(_settings).CreateChatClient(UniqueModel(prefix));
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "hi"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "f", new Dictionary<string, object>())]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "done")])
        ];

        await client.GetResponseAsync(messages);

        Assert.That(_wire.LastRequestBody.Contains("skip_thought_signature_validator", StringComparison.Ordinal), Is.EqualTo(expected));
    }

    [Test]
    public async Task NonStreamingLearnsSeveralRejectedParametersInOneRequest()
    {
        _wire.ResponseBody = ChatText;
        _wire.ScriptResponse(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unsupported parameter: max_tokens. Use max_completion_tokens\"}}");
        _wire.ScriptResponse(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unsupported value: temperature\"}}");
        using IChatClient client = new GroqProvider(_settings).CreateChatClient(UniqueModel("m"));

        ChatResponse response = await client.GetResponseAsync(User(), new ChatOptions { MaxOutputTokens = 100, Temperature = 0.3f });

        Assert.That(response.Text, Is.EqualTo("ok"));
        Assert.That(_wire.RequestBodies, Has.Count.EqualTo(3));
    }

    [TestCase("The `reasoning_content` in the thinking mode must be passed back to the API.")]
    [TestCase("Function call is missing a thought_signature")]
    [TestCase("Function tools with reasoning_effort are not supported in /v1/chat/completions. Please use /v1/responses instead.")]
    public void ReplayErrorsAreNotMistakenForUnsupportedReasoning(string message)
    {
        Assert.That(LLMErrorMapper.LooksLikeReasoningRejection(message), Is.False);
    }

    [Test]
    public void PromptEstimateCountsToolsCallsAndResults()
    {
        string big = new('x', 4000);
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => 1, "f", big)] };
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "f", new Dictionary<string, object> { ["a"] = big })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", big)])
        ];

        Assert.That(RimLLMChatClientExecutor.EstimatePromptRaw(messages, options), Is.GreaterThan(3000));
        Assert.That(RimLLMChatClientExecutor.EstimatePromptRaw([new(ChatRole.Assistant, [new TextReasoningContent(big)])], options: null), Is.GreaterThan(900));
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public async Task TimeoutBeforeAnyOutputIsBilledOnlyForPaidEndpoints(bool streaming, bool bills)
    {
        long before = _settings.TotalPromptTokens;
        var p = new GenerationParams(new HangingClient(), User(), Options: null, "m", UseNativeSchema: false, ProviderIds.OpenAI, 0.1f, default)
        {
            UsageTracker = new RimLLMUsageTracker(_settings),
            BillsTimeouts = bills
        };

        await Assert.ThrowsAsync<RimLLMException>(() => streaming
            ? RimLLMChatClientExecutor.StreamAsync(p, _ => Task.CompletedTask)
            : RimLLMChatClientExecutor.GenerateAsync(p));

        Assert.That(_settings.TotalPromptTokens > before, Is.EqualTo(bills));
    }

    private sealed class HangingClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, System.Threading.CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return null;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            yield break;
        }

        public object GetService(Type serviceType, object serviceKey = null) => null;

        public void Dispose() => GC.SuppressFinalize(this);
    }

    [Test]
    public void CacheWritesAreBilledAtTheirOwnRate()
    {
        ModelPricing.SetCatalogRates(new() { [ProviderIds.OpenAI] = new() { ["cache-write-test"] = new ModelPricing.CostRate(1f, 1f, 0.1f, 1.25f) } });
        try
        {
            ModelPricing.TryEstimateCost(ProviderIds.OpenAI, "cache-write-test", 1000000, 0, 0, out float cost, 1000000);
            Assert.That(cost, Is.EqualTo(1.25f).Within(0.0001f));
        }
        finally
        {
            ModelPricing.SetCatalogRates([]);
        }
    }

    [Test]
    public async Task ToolLoopStaysOnTheCandidateThatAnswered()
    {
        _settings.RoutingStrategy = 2;
        string a = UniqueModel("a"), b = UniqueModel("b");
        _settings.FallbackChain = [ProviderIds.Groq + ":" + a, ProviderIds.Groq + ":" + b];
        _settings.EnabledProviders[ProviderIds.Groq] = true;
        var manager = new RimLLMManager(_settings);
        RimLLMProvider.Initialize(manager);
        IChatClient client = RimLLMProvider.CreateChatClient("sticky.test");
        _wire.ResponseBody = ChatToolCall(string.Empty);
        List<ChatMessage> messages = User();

        var models = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            ChatResponse response = await client.GetResponseAsync(messages);
            models.Add(response.ModelId);
            messages.AddRange(response.Messages);
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "done")]));
        }

        Assert.That(models.Distinct().Count(), Is.EqualTo(1));
    }
}
