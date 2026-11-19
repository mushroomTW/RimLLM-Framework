using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 釘住 <see cref="LLMErrorMapper.FromClientResultException"/> 的行為：
/// 對話與 Embedding 路徑共用這一份對照，先前 Embedding 端只看 SDK 的 Message，
/// 服務端錯誤內文與 Retry-After 都被丟掉。
/// </summary>
[TestFixture]
public sealed class ClientResultExceptionMappingTests
{
    [Test]
    public void RateLimitKeepsRetryAfterAndResponseBody()
    {
        var response = new FakePipelineResponse(429, "{\"error\":\"slow down please\"}", ("Retry-After", "7"));
        var exception = new ClientResultException(response);

        RimLLMException mapped = LLMErrorMapper.FromClientResultException(exception, "Embedding API: ");

        ClassicAssert.AreEqual(LLMError.RateLimit, mapped.Error);
        ClassicAssert.AreEqual(TimeSpan.FromSeconds(7), mapped.RetryAfter);
        StringAssert.Contains("Embedding API: ", mapped.Message);
        StringAssert.Contains("slow down please", mapped.Message);
    }

    [Test]
    public void QuotaKeywordInBodyIsDetected()
    {
        // SDK 的 Message 不含 insufficient_quota，只有讀出內文才分得出是額度用完。
        var response = new FakePipelineResponse(429, "{\"error\":{\"code\":\"insufficient_quota\"}}");
        var exception = new ClientResultException(response);

        RimLLMException mapped = LLMErrorMapper.FromClientResultException(exception);

        ClassicAssert.AreEqual(LLMError.QuotaExceeded, mapped.Error);
        ClassicAssert.IsTrue(mapped.IsBalanceExhausted);
    }

    /// <summary>
    /// 連線被拒時，真實的 SDK 擲出的是 Status 為 0 的 <see cref="ClientResultException"/>（內層才是
    /// HttpRequestException），不是裸的 HttpRequestException。這裡走真實管線而不是手造例外，
    /// 才能在 SDK 改變行為時察覺。127.0.0.1:1 在本機沒有服務監聽，連線會立即被拒。
    /// </summary>
    [Test]
    public async Task ConnectionRefusedFromRealSdkMapsToNetworkError()
    {
        Microsoft.Extensions.AI.IChatClient client =
            RimLLM_Framework.Providers.OpenAIProvider.CreateOpenAiChatClient("test-key", "test-model", "http://127.0.0.1:1/v1");

        ClientResultException captured = null;
        try
        {
            await client.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "ping")]);
        }
        catch (ClientResultException ex)
        {
            captured = ex;
        }

        ClassicAssert.IsNotNull(captured, "The SDK no longer surfaces connection failures as ClientResultException.");
        ClassicAssert.AreEqual(0, captured.Status);

        RimLLMException mapped = LLMErrorMapper.FromClientResultException(captured, "Embedding API: ");
        ClassicAssert.AreEqual(LLMError.NetworkError, mapped.Error);
        // 主供應商連不上正是備援鏈存在的理由，必須換下一家。
        ClassicAssert.IsTrue(RimLLM_Framework.Manager.ServiceProviderFailover.ShouldFailOver(mapped));
    }

    /// <summary>只提供 <see cref="ClientResultException"/> 讀取所需成員的假回應。</summary>
    private sealed class FakePipelineResponse : PipelineResponse
    {
        private readonly int _status;
        private readonly FakeHeaders _headers;
        private BinaryData _content;

        public FakePipelineResponse(int status, string body, params (string Name, string Value)[] headers)
        {
            _status = status;
            _content = BinaryData.FromString(body);
            _headers = new FakeHeaders(headers);
        }

        public override int Status => _status;
        public override string ReasonPhrase => string.Empty;
        public override Stream ContentStream { get; set; }
        public override BinaryData Content => _content;
        protected override PipelineResponseHeaders HeadersCore => _headers;
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => _content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(_content);
        public override void Dispose() { _content = null; }
    }

    private sealed class FakeHeaders : PipelineResponseHeaders
    {
        private readonly (string Name, string Value)[] _headers;

        public FakeHeaders((string Name, string Value)[] headers) { _headers = headers; }

        public override bool TryGetValue(string name, out string value)
        {
            value = _headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
            return value != null;
        }

        public override bool TryGetValues(string name, out IEnumerable<string> values)
        {
            bool found = TryGetValue(name, out string value);
            values = found ? new[] { value } : null;
            return found;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            => _headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).GetEnumerator();
    }
}
