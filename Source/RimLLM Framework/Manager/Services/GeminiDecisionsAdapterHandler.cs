using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RimLLM_Framework.Manager.Services;

/// <summary>
/// 把 Jev SDK 的 System One 請求轉成 Google Gemini Decisions API（<c>/v1beta/decisions</c>），
/// 回應再轉回 Jev 格式，讓 Gemini Decision 走同一個 JevClient 與型別化結果。
///
/// 兩者的問題型別（noul／choice／score）與答案形狀相同，差異只在：
/// 路徑、金鑰標頭（<c>x-goog-api-key</c>）、<c>state</c> 改名 <c>input</c>、
/// noul criteria 的欄位名稱、choice 的空描述，以及 usage 的欄位名稱。
/// 只有端點主機是 Gemini API 時才會掛上這個處理器。
/// </summary>
internal sealed class GeminiDecisionsAdapterHandler : DelegatingHandler
{
    /// <summary>Gemini Developer API 的主機名稱。</summary>
    internal const string GeminiHost = "generativelanguage.googleapis.com";

    private const string JevEvaluatePath = "/v1/systemone";
    private const string JevModelsPath = "/v1/models";
    private const string GeminiEvaluatePath = "/v1beta/decisions";
    private const string GeminiModelsPath = "/v1beta/models";

    /// <summary>端點是否指向 Gemini API，需要套用這個轉接。</summary>
    public static bool Targets(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) &&
               string.Equals(uri.Host, GeminiHost, StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? "";
        bool isEvaluate = request.Method == HttpMethod.Post &&
                          path.EndsWith(JevEvaluatePath, StringComparison.OrdinalIgnoreCase);
        bool isModelList = request.Method == HttpMethod.Get &&
                           path.EndsWith(JevModelsPath, StringComparison.OrdinalIgnoreCase);
        if (!isEvaluate && !isModelList)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Gemini 以 x-goog-api-key 驗證，不接受 Bearer。
        string apiKey = request.Headers.Authorization?.Parameter;
        request.Headers.Authorization = null;
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        }

        string model = "";
        if (isEvaluate && request.Content != null)
        {
            string body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            request.Content = JevWireFormat.JsonContent(ToGeminiRequest(body, out model));
        }

        request.RequestUri = new UriBuilder(request.RequestUri)
        {
            Path = isEvaluate ? GeminiEvaluatePath : GeminiModelsPath,
            // 預設每頁只有 50 筆；一次取完，免得決策模型落在後面的頁面。
            Query = isEvaluate ? "" : "pageSize=1000"
        }.Uri;

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content == null)
        {
            // 錯誤回應原樣交給 SDK，狀態碼照常對應到 LLMError。
            return response;
        }

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string translated;
        try
        {
            translated = isEvaluate ? ToJevResult(responseBody, model) : ToJevModelList(responseBody);
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
        {
            // 不是預期的 JSON 物件（例如代理回的 HTML）：原樣交給 SDK，
            // 由它擲出協議錯誤，對應到 InvalidResponse 而不是 Unknown。
            translated = responseBody;
        }
        response.Content = JevWireFormat.JsonContent(translated);
        return response;
    }

    /// <summary>System One 請求 → Gemini Decisions 請求。</summary>
    internal static string ToGeminiRequest(string jevBody, out string model)
    {
        JsonObject source = JsonNode.Parse(jevBody)!.AsObject();
        model = source["model"] is JsonValue modelValue && modelValue.TryGetValue(out string m) ? m : "";

        var questions = new JsonObject();
        if (source["questions"] is JsonObject sourceQuestions)
        {
            foreach (var (id, node) in sourceQuestions)
            {
                if (node is not JsonObject question)
                {
                    continue;
                }

                string type = question["type"] is JsonValue t && t.TryGetValue(out string s) ? s : "";
                var converted = new JsonObject
                {
                    ["type"] = type,
                    // Gemini 的 instructions 只收文字；Jev 允許 JSON 內容，這裡序列化成文字。
                    ["instructions"] = AsText(question["instructions"])
                };

                JsonNode criteria = question["criteria"];
                if (criteria != null)
                {
                    converted["criteria"] = ConvertCriteria(type, criteria);
                }
                questions[id] = converted;
            }
        }

        var target = new JsonObject
        {
            ["model"] = model,
            // Gemini 文件要求結構化資料序列化成文字放進 input。
            ["input"] = AsText(source["state"]),
            ["questions"] = questions
        };
        return target.ToJsonString();
    }

    private static JsonNode ConvertCriteria(string type, JsonNode criteria)
    {
        switch (type)
        {
            case "noul" when criteria is JsonObject noul:
                var noulCriteria = new JsonObject();
                if (noul["true"] != null) noulCriteria["true_description"] = AsText(noul["true"]);
                if (noul["false"] != null) noulCriteria["false_description"] = AsText(noul["false"]);
                return noulCriteria;

            case "choice" when criteria is JsonObject choice:
                // Jev 用 null 表示沒有描述；Gemini 規定用空字串。
                var options = new JsonObject();
                foreach (var (label, description) in choice)
                {
                    options[label] = AsText(description);
                }
                return options;

            case "score" when criteria is JsonArray levels:
                var converted = new JsonArray();
                foreach (JsonNode level in levels)
                {
                    converted.Add(AsText(level));
                }
                return converted;

            default:
                return criteria.DeepClone();
        }
    }

    /// <summary>字串取原值，其他 JSON 內容序列化成文字，null 視為空字串。</summary>
    private static string AsText(JsonNode node)
    {
        if (node == null)
        {
            return "";
        }
        return node is JsonValue value && value.TryGetValue(out string text) ? text : node.ToJsonString();
    }

    /// <summary>
    /// Gemini Decisions 回應 → System One 回應。answers 形狀相同（多出的 confidence SDK 會略過），
    /// 只需補上 SDK 必填的 model，並把 usage 換成 input_tokens／output_tokens。
    /// </summary>
    internal static string ToJevResult(string geminiBody, string model)
    {
        JsonObject root = JsonNode.Parse(geminiBody)!.AsObject();
        if (root["model"] == null)
        {
            root["model"] = model;
        }

        // usage 缺席或不是 Jev 形狀時一律補成 input_tokens／output_tokens，缺的計數記 0。
        JsonObject usage = root["usage"] as JsonObject;
        if (usage == null || usage["input_tokens"] == null)
        {
            root["usage"] = new JsonObject
            {
                ["input_tokens"] = usage?["total_input_tokens"]?.DeepClone() ?? 0,
                ["output_tokens"] = usage?["total_output_tokens"]?.DeepClone() ?? 0
            };
        }
        return root.ToJsonString();
    }

    /// <summary>
    /// Gemini 的 <c>{"models":[{"name":"models/x"}]}</c> → Jev 的模型清單。
    /// Gemini 清單包含所有對話模型，只留下名稱含 decision 的決策模型。
    /// </summary>
    internal static string ToJevModelList(string geminiBody)
    {
        var models = new List<(string Name, string Description)>();
        using (JsonDocument doc = JsonDocument.Parse(geminiBody))
        {
            if (doc.RootElement.TryGetProperty("models", out JsonElement list) &&
                list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in list.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object ||
                        !entry.TryGetProperty("name", out JsonElement nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string name = nameElement.GetString() ?? "";
                    if (name.StartsWith("models/", StringComparison.Ordinal))
                    {
                        name = name.Substring("models/".Length);
                    }
                    if (name.IndexOf("decision", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    string description = entry.TryGetProperty("description", out JsonElement d) &&
                                         d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : "";
                    models.Add((name, description));
                }
            }
        }
        return JevWireFormat.ModelList(models);
    }
}
