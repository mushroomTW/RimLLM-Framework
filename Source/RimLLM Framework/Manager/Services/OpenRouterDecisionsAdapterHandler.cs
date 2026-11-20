using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Manager.Services;

/// <summary>
/// 把 TypeSafeAI SDK 送往 OpenRouter 的 System One 請求改到 OpenRouter 的路徑，並加上應用歸屬標頭。
/// </summary>
/// <remarks>
/// OpenRouter Decisions API 的請求與回應和 System One 相同（model／state／questions，
/// answers 與 usage.input_tokens／output_tokens），差別只在：
/// 評估路徑是 <c>/api/alpha/decisions</c>，模型清單是 <c>/api/v1/models?output_modalities=decisions</c>
/// （OpenAI 格式，交給外層的 <see cref="OpenAiModelListAdapterHandler"/> 轉成 Jev 格式），
/// 以及單題可能回 <c>{"type":"refusal"}</c>。
/// SDK 只接受沒有路徑的 BaseUrl，所以只能在傳輸層改寫。見 https://openrouter.ai/docs/guides/features/decisions。
/// </remarks>
internal sealed class OpenRouterDecisionsAdapterHandler : DelegatingHandler
{
    private const string JevEvaluatePath = "/v1/systemone";
    private const string JevModelsPath = "/v1/models";
    private const string OpenRouterEvaluatePath = "/api/alpha/decisions";
    private const string OpenRouterModelsPath = "/api/v1/models";
    private const string DecisionModalityQuery = "output_modalities=decisions";
    private const string RefusalType = "refusal";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!OpenRouterAttributionPolicy.Targets(request.RequestUri))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // 只改寫 SDK 原始路徑；已是 OpenRouter 路徑的請求（例如外層模型清單轉接重送的請求）原樣放行。
        ArgumentNullException.ThrowIfNull(request.RequestUri);
        string path = request.RequestUri.AbsolutePath;
        bool isEvaluate = request.Method == HttpMethod.Post &&
                          string.Equals(path, JevEvaluatePath, StringComparison.OrdinalIgnoreCase);
        if (isEvaluate)
        {
            request.RequestUri = new UriBuilder(request.RequestUri) { Path = OpenRouterEvaluatePath }.Uri;
        }
        else if (request.Method == HttpMethod.Get && string.Equals(path, JevModelsPath, StringComparison.OrdinalIgnoreCase))
        {
            request.RequestUri = new UriBuilder(request.RequestUri)
            {
                Path = OpenRouterModelsPath,
                Query = DecisionModalityQuery
            }.Uri;
        }

        RimLLMAppAttribution attribution = RimLLMAppAttribution.Current ?? RimLLMAppAttribution.Framework;
        request.Headers.Remove(OpenRouterAttributionPolicy.RefererHeader);
        request.Headers.Remove(OpenRouterAttributionPolicy.TitleHeader);
        request.Headers.TryAddWithoutValidation(OpenRouterAttributionPolicy.RefererHeader, attribution.Url);
        request.Headers.TryAddWithoutValidation(OpenRouterAttributionPolicy.TitleHeader, attribution.Title);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!isEvaluate || !response.IsSuccessStatusCode || response.Content == null)
        {
            return response;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.Content = JevWireFormat.JsonContent(RemoveRefusals(body));
        return response;
    }

    /// <summary>
    /// 移除拒答的題目。TypeSafeAI SDK 不認得 <c>refusal</c> 型別，留著會讓整份回應解析失敗；
    /// 拿掉後只有那一題缺值（<c>TryGet</c> 回傳 false），其他題照常取用。
    /// 不是預期的 JSON 物件時原樣交回，由 SDK 擲出協議錯誤。
    /// </summary>
    internal static string RemoveRefusals(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root || root["answers"] is not JsonObject answers)
            {
                return body;
            }

            string[] refused = answers
                .Where(a => a.Value is JsonObject answer &&
                            answer["type"] is JsonValue type &&
                            type.TryGetValue(out string? name) &&
                            name == RefusalType)
                .Select(a => a.Key)
                .ToArray();
            if (refused.Length == 0)
            {
                return body;
            }
            foreach (string id in refused)
            {
                answers.Remove(id);
            }
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
