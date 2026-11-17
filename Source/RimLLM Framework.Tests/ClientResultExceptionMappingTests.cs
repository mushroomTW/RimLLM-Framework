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
