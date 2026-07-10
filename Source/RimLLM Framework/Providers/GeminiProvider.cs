using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Google Gemini API 供應商，支援 generateContent 與 streamGenerateContent。
    /// </summary>
    public class GeminiProvider : BaseHttpProvider
    {
        public override string ProviderId => ProviderIds.Gemini;

        private class GeminiCacheEntry
        {
            public string CacheId { get; set; }
            public DateTime ExpireTime { get; set; }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GeminiCacheEntry> _contextCaches =
            new System.Collections.Concurrent.ConcurrentDictionary<string, GeminiCacheEntry>();

        // 對同一 cacheKey 的快取建立流程加鎖，避免並發時重複建立資源（重複付建立費）。
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> _cacheCreationLocks =
            new System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim>();

        public GeminiProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        public override async Task<string> GenerateAsync(LLMRequest request, string model)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string baseEndpoint = Settings.GetEndpoint(ProviderId, "https://generativelanguage.googleapis.com/v1beta");
            // API Key 以 x-goog-api-key Header 傳遞（由 ApplyAuthHeaders 套用），避免金鑰出現在 URL / 日誌中
            string url = $"{baseEndpoint}/models/{model}:generateContent";

            JObject payload = await BuildRequestPayloadAsync(request, model, apiKey, baseEndpoint).ConfigureAwait(false);

            string responseJson = await SendPostAsync(url, payload.ToString(), apiKey, AuthSchemes.Gemini, cancellationToken: request.CancellationToken).ConfigureAwait(false);

            try
            {
                var responseObj = JObject.Parse(responseJson);
                var parts = responseObj["candidates"]?[0]?["content"]?["parts"] as JArray;
                if (parts == null || parts.Count == 0)
                {
                    throw new RimLLMException(LLMError.InvalidResponse, "Gemini response JSON is missing content parts");
                }

                var sb = new StringBuilder();
                bool hasThoughts = false;
                bool hasFinishedReasoning = false;
                foreach (var part in parts)
                {
                    string partText = part["text"]?.ToString();
                    if (string.IsNullOrEmpty(partText)) continue;

                    bool isThought = part["thought"]?.Type == JTokenType.Boolean && (bool)part["thought"];
                    if (isThought)
                    {
                        if (!hasFinishedReasoning)
                        {
                            if (!hasThoughts)
                            {
                                sb.Append("<think>\n");
                                hasThoughts = true;
                            }
                            sb.Append(partText);
                        }
                        else
                        {
                            sb.Append(partText);
                        }
                    }
                    else
                    {
                        if (hasThoughts)
                        {
                            sb.Append("\n</think>\n");
                            hasThoughts = false;
                            hasFinishedReasoning = true;
                        }
                        sb.Append(partText);
                    }
                }
                if (hasThoughts)
                {
                    sb.Append("\n</think>");
                }

                // 記錄 Token 使用量
                var metadata = responseObj["usageMetadata"];
                if (metadata != null)
                {
                    int prompt = metadata["promptTokenCount"]?.Value<int>() ?? 0;
                    int completion = metadata["candidatesTokenCount"]?.Value<int>() ?? 0;
                    // promptTokenCount 已包含快取命中部分，cachedContentTokenCount 為其中以折扣計價的子集
                    int cached = metadata["cachedContentTokenCount"]?.Value<int>() ?? 0;
                    if (RimLLMProvider.Instance is RimLLMManager manager)
                    {
                        manager.RecordUsage(ProviderId, model, prompt, completion, cached);
                    }
                }

                return sb.ToString();
            }
            catch (Exception ex) when (!(ex is RimLLMException))
            {
                throw new RimLLMException(LLMError.InvalidResponse, $"Failed to parse Gemini response: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", ex);
            }
        }

        public override async Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string baseEndpoint = Settings.GetEndpoint(ProviderId, "https://generativelanguage.googleapis.com/v1beta");
            string url = $"{baseEndpoint}/models/{model}:streamGenerateContent";

            JObject payload = await BuildRequestPayloadAsync(request, model, apiKey, baseEndpoint).ConfigureAwait(false);

            float timeoutSeconds = Settings?.ApiTimeout ?? 30f;
            float streamTimeout = Math.Max(timeoutSeconds * 2f, 120f); // 串流給予寬鬆的超時保護

            using (var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(streamTimeout)))
            using (var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, request.CancellationToken))
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
            {
                httpRequest.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                // 串流路徑自行組裝 HttpRequestMessage，需手動補上 Gemini 認證 Header
                if (!string.IsNullOrEmpty(apiKey))
                {
                    httpRequest.Headers.Add("x-goog-api-key", apiKey);
                }

                HttpResponseMessage response = null;
                try
                {
                    response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        ThrowHttpError(response, responseBody);
                    }
                }
                catch (RimLLMException)
                {
                    response?.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    response?.Dispose();
                    throw ConvertStreamTransportException("Gemini", ex, request.CancellationToken);
                }

                using (response)
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream))
                using (var jsonReader = new JsonTextReader(reader))
                {
                    jsonReader.SupportMultipleContent = true;
                    bool inReasoning = false;
                    bool hasFinishedReasoning = false;
                    int totalCompletionChars = 0;
                    int finalPromptTokens = 0;
                    int finalCompletionTokens = 0;
                    int finalCachedTokens = 0;
                    bool hasUsage = false;

                    try
                    {
                        while (await jsonReader.ReadAsync(cts.Token).ConfigureAwait(false))
                        {
                            if (jsonReader.TokenType == JsonToken.StartObject)
                            {
                                try
                                {
                                    JObject token = await JObject.LoadAsync(jsonReader, cts.Token).ConfigureAwait(false);
                                    var metadata = token["usageMetadata"];
                                    if (metadata != null)
                                    {
                                        finalPromptTokens = metadata["promptTokenCount"]?.Value<int>() ?? 0;
                                        finalCompletionTokens = metadata["candidatesTokenCount"]?.Value<int>() ?? 0;
                                        finalCachedTokens = metadata["cachedContentTokenCount"]?.Value<int>() ?? 0;
                                        hasUsage = true;
                                    }
                                    var parts = token["candidates"]?[0]?["content"]?["parts"] as JArray;
                                    if (parts != null)
                                    {
                                        foreach (var part in parts)
                                        {
                                            string partText = part["text"]?.ToString();
                                            if (string.IsNullOrEmpty(partText)) continue;
                                            totalCompletionChars += partText.Length;

                                            bool isThought = part["thought"]?.Type == JTokenType.Boolean && (bool)part["thought"];
                                            if (isThought)
                                            {
                                                if (!hasFinishedReasoning)
                                                {
                                                    if (!inReasoning)
                                                    {
                                                        inReasoning = true;
                                                        onChunkReceived?.Invoke("<think>");
                                                    }
                                                    onChunkReceived?.Invoke(partText);
                                                }
                                                else
                                                {
                                                    onChunkReceived?.Invoke(partText);
                                                }
                                            }
                                            else
                                            {
                                                if (inReasoning)
                                                {
                                                    inReasoning = false;
                                                    hasFinishedReasoning = true;
                                                    onChunkReceived?.Invoke("</think>");
                                                }
                                                onChunkReceived?.Invoke(partText);
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is HttpRequestException)
                                {
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    if (Settings.DetailedLogging)
                                    {
                                        RimLLMLog.Warning($"[RimLLM] Gemini stream JSON parse failed: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is HttpRequestException)
                    {
                        throw ConvertStreamTransportException("Gemini", ex, request.CancellationToken);
                    }

                    if (inReasoning)
                    {
                        onChunkReceived?.Invoke("</think>");
                    }

                    if (RimLLMProvider.Instance is RimLLMManager manager)
                    {
                        if (hasUsage)
                        {
                            manager.RecordUsage(ProviderId, model, finalPromptTokens, finalCompletionTokens, finalCachedTokens);
                        }
                        else
                        {
                            int systemLen = request.GetEffectiveSystemPrompt()?.Length ?? 0;
                            int promptLen = request.Prompt?.Length ?? 0;
                            int estPrompt = (int)((systemLen + promptLen) * 0.8f);
                            int estCompletion = (int)(totalCompletionChars * 0.8f);
                            manager.RecordUsage(ProviderId, model, Math.Max(1, estPrompt), Math.Max(1, estCompletion));
                        }
                    }
                }
            }
        }

        protected override string DefaultTestModel => "gemini-3.5-flash";

        public override async Task<List<string>> FetchAvailableModelsAsync()
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string baseEndpoint = Settings.GetEndpoint(ProviderId, "https://generativelanguage.googleapis.com/v1beta");
            string url = $"{baseEndpoint.TrimEnd(new char[] { '/' })}/models";

            string responseJson = await SendGetAsync(url, apiKey, AuthSchemes.Gemini).ConfigureAwait(false);
            var list = new List<string>();
            try
            {
                var obj = JObject.Parse(responseJson);
                var modelsArray = obj["models"] as JArray;
                if (modelsArray != null)
                {
                    foreach (var item in modelsArray)
                    {
                        string name = item["name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            // 剝離 models/ 前綴
                            string cleanName = name.Replace("models/", "");
                            list.Add(cleanName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new RimLLMException(LLMError.InvalidResponse, $"Failed to fetch Gemini models list: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", ex);
            }
            return list;
        }

        /// <summary>
        /// 組裝 generateContent / streamGenerateContent 共用的請求 payload，
        /// 包含 contents、generationConfig（含 thinking 設定）與 Context Cache / systemInstruction 的解析。
        /// </summary>
        private async Task<JObject> BuildRequestPayloadAsync(LLMRequest request, string model, string apiKey, string baseEndpoint)
        {
            var contents = new JArray
            {
                new JObject
                {
                    ["parts"] = new JArray
                    {
                        new JObject { ["text"] = request.Prompt }
                    }
                }
            };

            var generationConfig = new JObject
            {
                ["temperature"] = request.Temperature,
                ["maxOutputTokens"] = request.MaxTokens
            };

            ApplyGeminiThinkingConfig(generationConfig, model, request.ReasoningEffort);

            if (request.ResponseType != null && Settings.EnableNativeSchema)
            {
                generationConfig["responseMimeType"] = "application/json";
                generationConfig["responseSchema"] = RimLLMJsonHelper.GenerateJsonSchema(request.ResponseType, uppercaseTypes: true);
            }

            var payload = new JObject
            {
                ["contents"] = contents,
                ["generationConfig"] = generationConfig
            };

            string systemContext = request.GetEffectiveSystemPrompt();
            string cacheId = null;
            if (request.EnableContextCaching && !string.IsNullOrEmpty(systemContext))
            {
                cacheId = await GetOrCreateCachedContentAsync(apiKey, baseEndpoint, model, systemContext, request.CancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(cacheId))
            {
                payload["cachedContent"] = cacheId;
            }
            else if (!string.IsNullOrEmpty(systemContext))
            {
                payload["systemInstruction"] = new JObject
                {
                    ["parts"] = new JArray
                    {
                        new JObject { ["text"] = systemContext }
                    }
                };
            }

            return payload;
        }

        private void DetermineGeminiThinkingConfig(string model, out bool isThinkingBudgetModel, out bool isThinkingLevelModel)
        {
            isThinkingBudgetModel = false;
            isThinkingLevelModel = false;
            if (model == null) return;
 
            isThinkingBudgetModel = model.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                    model.IndexOf("gemini-2.5", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    model.IndexOf("gemini-2-5", StringComparison.OrdinalIgnoreCase) >= 0;
 
            isThinkingLevelModel = model.IndexOf("gemma-4", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                   model.IndexOf("gemini-3", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                   model.IndexOf("gemini-4", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ApplyGeminiThinkingConfig(JObject generationConfig, string model, LLMReasoningEffort effort)
        {
            if (model == null) return;
            DetermineGeminiThinkingConfig(model, out bool isThinkingBudgetModel, out bool isThinkingLevelModel);

            if (isThinkingBudgetModel)
            {
                int budget = -1; // Default for Auto
                if (effort == LLMReasoningEffort.Low) budget = 1024;
                else if (effort == LLMReasoningEffort.Medium) budget = 2048;
                else if (effort == LLMReasoningEffort.High) budget = 4096;
                else if (effort == LLMReasoningEffort.None) budget = 0;

                generationConfig["thinkingConfig"] = new JObject
                {
                    ["thinkingBudget"] = budget
                };
            }
            else if (isThinkingLevelModel)
            {
                if (effort == LLMReasoningEffort.None)
                {
                    generationConfig["thinkingConfig"] = new JObject
                    {
                        ["thinkingLevel"] = "minimal"
                    };
                }
                else if (effort != LLMReasoningEffort.Auto)
                {
                    generationConfig["thinkingConfig"] = new JObject
                    {
                        ["thinkingLevel"] = effort.ToString().ToLower()
                    };
                }
            }
        }

        private async Task<string> GetOrCreateCachedContentAsync(string apiKey, string baseEndpoint, string model, string cacheableContext, System.Threading.CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(cacheableContext)) return null;

            // 顯式快取需付「建立費 + 儲存費」，內容過小時這些成本會超過節省，因此低於最低門檻直接改走一般 systemInstruction。
            // Gemini 官方對 2.5 系列的最低可快取輸入量：Pro 約 2048 token、Flash / Flash-Lite 約 1024 token。
            // 以「字元數 < 最低 token 數」作為「必定不足」的保守下界（即使最密集的 CJK 也約為 1 token/字元），避免送出注定失敗的建立請求。
            int minCacheableTokens = (model != null && model.IndexOf("pro", StringComparison.OrdinalIgnoreCase) >= 0) ? 2048 : 1024;
            if (cacheableContext.Length < minCacheableTokens)
            {
                if (Settings.DetailedLogging)
                {
                    RimLLMLog.Message($"[RimLLM] Context too small for Gemini explicit cache ({cacheableContext.Length} chars < {minCacheableTokens}); using inline systemInstruction instead.");
                }
                return null;
            }

            string cacheKey = $"{model}\n{cacheableContext}";

            CleanupExpiredCaches();

            string existing = TryGetValidCachedId(cacheKey);
            if (existing != null) return existing;

            // 串行化同一 cacheKey 的建立流程，避免並發請求各自建立一份重複的快取資源
            var gate = _cacheCreationLocks.GetOrAdd(cacheKey, _ => new System.Threading.SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 雙重檢查：等待鎖期間可能已由其他請求建立完成
                existing = TryGetValidCachedId(cacheKey);
                if (existing != null) return existing;

                return await CreateCachedContentAsync(apiKey, baseEndpoint, model, cacheKey, cacheableContext, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 清理已過期的快取 entry 與其對應鎖，避免記憶體洩漏。
        /// </summary>
        private void CleanupExpiredCaches()
        {
            foreach (var kvp in _contextCaches)
            {
                if (kvp.Value.ExpireTime <= DateTime.UtcNow)
                {
                    _contextCaches.TryRemove(kvp.Key, out _);
                    _cacheCreationLocks.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// 取回未過期（含 10 秒安全緩衝）的快取 ID；查無或已逼近過期則回傳 null。
        /// </summary>
        private string TryGetValidCachedId(string cacheKey)
        {
            if (_contextCaches.TryGetValue(cacheKey, out var entry) &&
                entry.ExpireTime > DateTime.UtcNow.AddSeconds(10))
            {
                return entry.CacheId;
            }
            return null;
        }

        private async Task<string> CreateCachedContentAsync(string apiKey, string baseEndpoint, string model, string cacheKey, string cacheableContext, System.Threading.CancellationToken cancellationToken)
        {
            // 建立新的 Cached Content 資源
            // API url 格式: POST https://generativelanguage.googleapis.com/v1beta/cachedContents（金鑰走 x-goog-api-key Header）
            string cacheUrl = $"{baseEndpoint.TrimEnd(new char[] { '/' })}/cachedContents";

            // 剥離 model 中的 "models/" 前綴以對齊格式 (Gemini 官方要求建立快取時 model 必須包含 models/ 前綴)
            string modelWithPrefix = model.StartsWith("models/") ? model : $"models/{model}";

            var cachePayload = new JObject
            {
                ["model"] = modelWithPrefix,
                ["systemInstruction"] = new JObject
                {
                    ["parts"] = new JArray
                    {
                        new JObject { ["text"] = cacheableContext }
                    }
                },
                ["ttl"] = "300s" // 預設保留 5 分鐘
            };

            try
            {
                string cacheResponseJson = await SendPostAsync(cacheUrl, cachePayload.ToString(), apiKey, AuthSchemes.Gemini, cancellationToken).ConfigureAwait(false);
                var cacheObj = JObject.Parse(cacheResponseJson);
                string cacheId = cacheObj["name"]?.ToString();
                string expireTimeStr = cacheObj["expireTime"]?.ToString();

                if (!string.IsNullOrEmpty(cacheId))
                {
                    DateTime expireTime = DateTime.UtcNow.AddSeconds(300); // 預設 300 秒
                    if (DateTime.TryParse(expireTimeStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsedTime))
                    {
                        expireTime = parsedTime;
                    }

                    var newEntry = new GeminiCacheEntry
                    {
                        CacheId = cacheId,
                        ExpireTime = expireTime
                    };
                    _contextCaches[cacheKey] = newEntry;
                    return cacheId;
                }
            }
            catch (Exception ex)
            {
                // 記錄警告並 fallback。不拋出異常以防整體請求中斷。
                RimLLMLog.Warning($"[RimLLM] Failed to create Gemini Context Cache, fallback to normal call: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
            }

            return null;
        }
    }
}
