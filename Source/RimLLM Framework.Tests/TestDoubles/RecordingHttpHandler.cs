using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 記下每個請求的位址與標頭，並依位址回應 200 + JSON 的假端點。
/// 供需要檢查實際送出標頭（例如 OpenRouter 歸屬標頭）的測試共用。
/// </summary>
internal sealed class RecordingHttpHandler(Func<Uri, string> respond) : HttpMessageHandler
{
    internal sealed record Request(Uri Uri, Dictionary<string, string> Headers);

    public List<Request> Requests { get; } = [];

    /// <summary>最後一個請求；沒有請求時為 null。</summary>
    public Request Last => Requests.Count == 0 ? null : Requests[^1];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }
        lock (Requests)
        {
            Requests.Add(new Request(request.RequestUri, headers));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(respond(request.RequestUri), Encoding.UTF8, "application/json")
        });
    }
}
