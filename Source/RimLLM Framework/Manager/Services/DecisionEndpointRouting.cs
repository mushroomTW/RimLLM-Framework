using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RimLLM_Framework.Manager.Services;

/// <summary>
/// 決策端點的規則。
///
/// 本機架設的決策服務（Ollama、Unsloth 等）只提供明文 HTTP；TypeSafeAI SDK 直接接受 http base URL，
/// 所以明文與 HTTPS 端點都原樣交給 SDK，不需要任何傳輸層改寫。
///
/// 框架的對話與 embedding 供應商本來就允許明文本機端點（Ollama、LM Studio），
/// 決策這裡沿用同一套規則：玩家自己填的端點就是權威。
/// </summary>
internal static class DecisionEndpointRouting
{
    /// <summary>
    /// 明文端點沒有金鑰時送出的佔位值。SDK 拒絕空金鑰（<c>TypeSafeClientOptions.ApiKey</c> 必填），
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
    /// 給 SDK 的 base URL：玩家設定的端點原樣使用，只檢查它是 http 或 https 的絕對 URI。
    /// SDK 會在既有路徑之後接上 <c>v1/systemone</c> 與 <c>v1/models</c>。
    /// </summary>
    public static Uri GetBaseUrl(string endpoint)
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

        return uri;
    }
}

/// <summary>
/// 把 OpenAI 格式的模型清單轉成 TypeSafeAI SDK 能解析的格式。
///
/// TypeSafeAI SDK 只接受 <c>{"models":[{"name","description","release_date"}]}</c>，
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
            response.Content = JevWireFormat.JsonContent(body);
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

        response.Content = JevWireFormat.JsonContent(ToJevModelList(ids));
        return response;
    }

    /// <summary>讀出 OpenAI 格式的 <c>data[].id</c>；不是這個格式時回傳 null。</summary>
    #pragma warning disable S1168 // API 合約規定格式不符時回傳 null，呼叫端已區分 null 與空集合。
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
    #pragma warning restore S1168

    private static string ToJevModelList(List<string> ids)
    {
        var models = new List<(string Name, string Description)>(ids.Count);
        foreach (string id in ids)
        {
            models.Add((id, ""));
        }
        return JevWireFormat.ModelList(models);
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
}

/// <summary>
/// 決策轉接處理器共用的 Jev wire format 小工具。模型清單的形狀只在這裡定義一次，
/// OpenAI 格式與 Gemini 格式的轉接都產生同一份 SDK 能解析的內容。
/// </summary>
internal static class JevWireFormat
{
    /// <summary>TypeSafeAI SDK 的模型清單：<c>{"models":[{"name","description","release_date"}]}</c>。</summary>
    public static string ModelList(IEnumerable<(string Name, string Description)> models)
    {
        var entries = new List<Dictionary<string, string>>();
        foreach ((string name, string description) in models)
        {
            entries.Add(new Dictionary<string, string>
            {
                ["name"] = name,
                ["description"] = description ?? "",
                ["release_date"] = ""
            });
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["models"] = entries });
    }

    public static StringContent JsonContent(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }
}
