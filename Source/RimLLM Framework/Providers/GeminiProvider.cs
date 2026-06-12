using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
        private static readonly Regex GeminiTextRegex = new Regex(@"\""text\""\s*:\s*\""([^\""\\]*(?:\\.[^\""\\]*)*)\""", RegexOptions.Compiled);

        public override string ProviderId => "Gemini";

        private class GeminiCacheEntry
        {
            public string CacheId { get; set; }
            public DateTime ExpireTime { get; set; }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GeminiCacheEntry> _contextCaches = 
            new System.Collections.Concurrent.ConcurrentDictionary<string, GeminiCacheEntry>();

        public GeminiProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        public override async Task<string> GenerateAsync(LLMRequest request, string model)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string baseEndpoint = Settings.GetEndpoint(ProviderId, "https://generativelanguage.googleapis.com/v1beta");
            string url = $"{baseEndpoint}/models/{model}:generateContent?key={apiKey}";

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

            string responseJson = await SendPostAsync(url, payload.ToString(), apiKey, "Gemini", cancellationToken: request.CancellationToken).ConfigureAwait(false);

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
                    if (RimLLMProvider.Instance is RimLLMManager manager)
                    {
                        manager.RecordUsage(ProviderId, model, prompt, completion);
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
            string url = $"{baseEndpoint}/models/{model}:streamGenerateContent?key={apiKey}";

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

            float timeoutSeconds = Settings?.ApiTimeout ?? 30f;
            float streamTimeout = Math.Max(timeoutSeconds * 2f, 120f); // 串流給予寬鬆的超時保護

            using (var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(streamTimeout)))
            using (var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, request.CancellationToken))
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
            {
                httpRequest.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");

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
                            manager.RecordUsage(ProviderId, model, finalPromptTokens, finalCompletionTokens);
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

        protected override string DefaultTestModel => "gemini-2.5-flash";

        public override async Task<List<string>> FetchAvailableModelsAsync()
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string baseEndpoint = Settings.GetEndpoint(ProviderId, "https://generativelanguage.googleapis.com/v1beta");
            string url = $"{baseEndpoint.TrimEnd(new char[] { '/' })}/models?key={apiKey}";

            string responseJson = await SendGetAsync(url, apiKey, "Gemini").ConfigureAwait(false);
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
            string cacheKey = $"{model}\n{cacheableContext}";

            // 清理已過期的快取 entry，避免內存洩漏
            foreach (var kvp in _contextCaches)
            {
                if (kvp.Value.ExpireTime <= DateTime.UtcNow)
                {
                    _contextCaches.TryRemove(kvp.Key, out _);
                }
            }

            if (_contextCaches.TryGetValue(cacheKey, out var entry))
            {
                // 快取未過期，且加上 10 秒安全緩衝，避免邊界失效
                if (entry.ExpireTime > DateTime.UtcNow.AddSeconds(10))
                {
                    return entry.CacheId;
                }
            }

            // 建立新的 Cached Content 資源
            // API url 格式: POST https://generativelanguage.googleapis.com/v1beta/cachedContents?key=YOUR_API_KEY
            string cacheUrl = $"{baseEndpoint.TrimEnd(new char[] { '/' })}/cachedContents?key={apiKey}";

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
                string cacheResponseJson = await SendPostAsync(cacheUrl, cachePayload.ToString(), apiKey, "Gemini", cancellationToken).ConfigureAwait(false);
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
