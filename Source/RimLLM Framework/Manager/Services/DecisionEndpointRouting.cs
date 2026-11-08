using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RimLLM_Framework.Manager.Services;

/// <summary>
/// 決策端點的傳輸層路由。
///
/// Jev SDK 只接受 HTTPS origin（<c>ClientSettings.ValidateBaseAddress</c> 會拒絕其他 scheme），
/// 但本機架設的決策服務（Ollama、Unsloth 等）只提供明文 HTTP。這個類別把兩件事分開處理：
/// 給 SDK 一個通過驗證的 https origin，並在請求真正送上線前把 URI 改寫回玩家設定的實際端點。
///
/// 框架的對話與 embedding 供應商本來就允許明文本機端點（Ollama、LM Studio），
/// 決策這裡沿用同一套規則：玩家自己填的端點就是權威，框架只負責讓 SDK 能跟它說話。
/// </summary>
internal static class DecisionEndpointRouting
{
    /// <summary>
    /// 明文端點沒有金鑰時送出的佔位值。SDK 拒絕空金鑰（<c>TextRules.IsUsableApiKey</c>），
    /// 但本機服務多半不驗證金鑰，所以送一個固定的佔位字串過去。
    /// </summary>
    public const string PlaintextPlaceholderKey = "local";

    /// <summary>端點是否走明文 HTTP。明文端點視為玩家自架的服務，可以不填金鑰。</summary>
    public static bool IsPlaintext(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri)
               && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 給 SDK 的 base address。明文端點換成同 host／port 的 https origin，
    /// 這樣 SDK 的驗證會通過；實際連線仍由 <see cref="PlaintextEndpointRewriteHandler"/> 改寫回明文。
    /// </summary>
    public static Uri GetSdkBaseAddress(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri))
        {
            throw new UriFormatException($"The decision endpoint '{endpoint}' is not an absolute URI.");
        }

        if (!IsPlaintext(endpoint) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new UriFormatException(
                $"The decision endpoint '{endpoint}' must use http:// or https://.");
        }

        if (!IsPlaintext(endpoint))
        {
            return uri;
        }

        // SDK 要求 base address 是沒有路徑的 origin；路徑交給 SDK 自己去接 /v1/systemone。
        return new UriBuilder(Uri.UriSchemeHttps, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
    }

    /// <summary>實際要連的 origin（明文端點保留原本的 http scheme 與 host／port）。</summary>
    public static Uri GetTransportOrigin(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri))
        {
            throw new UriFormatException($"The decision endpoint '{endpoint}' is not an absolute URI.");
        }

        return new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
    }
}

/// <summary>
/// 把 SDK 產生的 https 請求改寫成玩家設定的明文端點。
///
/// SDK 只會用自己的 base address 組出 <c>/v1/systemone</c> 與 <c>/v1/models</c> 兩個路徑，
/// 因此改寫只需要換掉 scheme 與 host／port，路徑原封不動。
/// </summary>
internal sealed class PlaintextEndpointRewriteHandler : DelegatingHandler
{
    private readonly Uri _transportOrigin;

    public PlaintextEndpointRewriteHandler(Uri transportOrigin)
    {
        _transportOrigin = transportOrigin ?? throw new ArgumentNullException(nameof(transportOrigin));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request?.RequestUri != null)
        {
            request.RequestUri = new Uri(
                $"{_transportOrigin.Scheme}://{_transportOrigin.Authority}{request.RequestUri.PathAndQuery}",
                UriKind.Absolute);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// 把 OpenAI 格式的模型清單轉成 Jev SDK 能解析的格式。
///
/// Jev SDK 只接受 <c>{"models":[{"name","description","release_date"}]}</c>，
/// 但 Unsloth、Ollama 等本機服務的 <c>/v1/models</c> 回的是 OpenAI 的 <c>{"data":[{"id"}]}</c>，
/// SDK 會判定為「不是合法 JSON」。另外 Unsloth 只有帶上 <c>output_modalities=decisions</c>
/// 才會列出決策模型，所以遇到 OpenAI 格式時先帶參數重查一次，再轉換格式。
/// Jev 原生格式的回應原樣放行。
/// </summary>
internal sealed class OpenAiModelListAdapterHandler : DelegatingHandler
{
    private const string ModelsPath = "/v1/models";
    private const string DecisionModalityQuery = "output_modalities=decisions";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Method != HttpMethod.Get ||
            request.RequestUri == null ||
            !request.RequestUri.AbsolutePath.EndsWith(ModelsPath, StringComparison.OrdinalIgnoreCase) ||
            !response.IsSuccessStatusCode ||
            response.Content == null)
        {
            return response;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        List<string> ids = TryReadOpenAiModelIds(body);
        if (ids == null)
        {
            // 不是 OpenAI 格式（多半是 Jev 原生格式）：把讀掉的內容放回去，交給 SDK 自己解析。
            response.Content = CreateJsonContent(body);
            return response;
        }

        if (string.IsNullOrEmpty(request.RequestUri.Query))
        {
            using HttpRequestMessage decisionRequest = CloneWithQuery(request, DecisionModalityQuery);
            using HttpResponseMessage decisionResponse =
                await base.SendAsync(decisionRequest, cancellationToken).ConfigureAwait(false);
            if (decisionResponse.IsSuccessStatusCode && decisionResponse.Content != null)
            {
                List<string> decisionIds = TryReadOpenAiModelIds(
                    await decisionResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (decisionIds != null)
                {
                    ids = decisionIds;
                }
            }
        }

        response.Content = CreateJsonContent(ToJevModelList(ids));
        return response;
    }

    /// <summary>讀出 OpenAI 格式的 <c>data[].id</c>；不是這個格式時回傳 null。</summary>
    internal static List<string> TryReadOpenAiModelIds(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("models", out _) ||
                !root.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var ids = new List<string>();
            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("id", out JsonElement id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    ids.Add(id.GetString());
                }
            }
            return ids;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ToJevModelList(List<string> ids)
    {
        var models = new List<Dictionary<string, string>>(ids.Count);
        foreach (string id in ids)
        {
            models.Add(new Dictionary<string, string>
            {
                ["name"] = id,
                ["description"] = "",
                ["release_date"] = ""
            });
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["models"] = models });
    }

    private static HttpRequestMessage CloneWithQuery(HttpRequestMessage request, string query)
    {
        var uri = new UriBuilder(request.RequestUri) { Query = query }.Uri;
        var clone = new HttpRequestMessage(request.Method, uri) { Version = request.Version };
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private static StringContent CreateJsonContent(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }
}
