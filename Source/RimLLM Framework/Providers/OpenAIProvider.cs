using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// OpenAI API 供應商，支援 Chat Completion 與 SSE 串流。
    /// </summary>
    public class OpenAIProvider : BaseHttpProvider
    {
        public override string ProviderId => "OpenAI";
        protected virtual string DefaultEndpoint => "https://api.openai.com/v1/chat/completions";

        public OpenAIProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        protected virtual JObject BuildPayload(LLMRequest request, string model, bool stream = false)
        {
            var messages = new JArray();
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                messages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = request.SystemPrompt
                });
            }
            messages.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = request.Prompt
            });

            var payload = new JObject
            {
                ["model"] = model,
                ["messages"] = messages
            };

            if (IsOpenAiReasoningModel(model))
            {
                payload["max_completion_tokens"] = request.MaxTokens;
                if (request.ReasoningEffort != LLMReasoningEffort.None)
                {
                    payload["reasoning_effort"] = request.ReasoningEffort.ToString().ToLower();
                }
            }
            else
            {
                payload["temperature"] = request.Temperature;
                payload["max_tokens"] = request.MaxTokens;
            }

            if (stream)
            {
                payload["stream"] = true;
            }

            return payload;
        }

        protected bool IsOpenAiReasoningModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            string name = modelName.Contains("/") ? modelName.Substring(modelName.LastIndexOf('/') + 1) : modelName;
            name = name.ToLowerInvariant();
            return name.StartsWith("o1") || name.StartsWith("o3");
        }

        public override async Task<string> GenerateAsync(LLMRequest request, string model)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);
            if (!endpoint.EndsWith("/chat/completions"))
            {
                endpoint = endpoint.TrimEnd(new char[] { '/' }) + "/chat/completions";
            }

            var payload = BuildPayload(request, model, false);
            string responseJson = await SendPostAsync(endpoint, payload.ToString(), apiKey, cancellationToken: request.CancellationToken).ConfigureAwait(false);

            try
            {
                var responseObj = JObject.Parse(responseJson);
                var message = responseObj["choices"]?[0]?["message"];
                if (message == null)
                {
                    throw new RimLLMException(LLMError.InvalidResponse, "OpenAI response JSON is missing message field");
                }
                var content = message["content"]?.ToString() ?? "";
                var reasoning = message["reasoning_content"]?.ToString();
                if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(reasoning))
                {
                    throw new RimLLMException(LLMError.InvalidResponse, "OpenAI response JSON is missing both content and reasoning_content fields");
                }
                if (!string.IsNullOrEmpty(reasoning))
                {
                    if (!string.IsNullOrEmpty(content))
                    {
                        return $"<think>\n{reasoning}\n</think>\n\n{content}";
                    }
                    return $"<think>\n{reasoning}\n</think>";
                }
                return content;
            }
            catch (Exception ex) when (!(ex is RimLLMException))
            {
                throw new RimLLMException(LLMError.InvalidResponse, $"Failed to parse OpenAI response: {ex.Message}", ex);
            }
        }

        public override async Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);
            if (!endpoint.EndsWith("/chat/completions"))
            {
                endpoint = endpoint.TrimEnd(new char[] { '/' }) + "/chat/completions";
            }

            var payload = BuildPayload(request, model, true);

            float timeoutSeconds = Settings?.ApiTimeout ?? 30f;
            float streamTimeout = Math.Max(timeoutSeconds * 2f, 120f); // 串流給予寬鬆的超時保護

            using (var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(streamTimeout)))
            using (var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, request.CancellationToken))
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                httpRequest.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                HttpResponseMessage response = null;
                try
                {
                    response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex)
                {
                    response?.Dispose();
                    throw new RimLLMException(LLMError.NetworkError, $"OpenAI stream request failed: {ex.Message}", ex);
                }

                using (response)
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream))
                {
                    bool inReasoning = false;
                    while (!reader.EndOfStream)
                    {
                        if (cts.Token.IsCancellationRequested)
                        {
                            throw new RimLLMException(LLMError.Timeout, "Stream request timed out");
                        }

                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) continue;
                        line = line.Trim();

                        if (line == "data: [DONE]")
                            break;

                        if (line.StartsWith("data: "))
                        {
                            string json = line.Substring(6);
                            string content = null;
                            string reasoning = null;
                            try
                            {
                                var token = JObject.Parse(json);
                                content = token["choices"]?[0]?["delta"]?["content"]?.ToString();
                                reasoning = token["choices"]?[0]?["delta"]?["reasoning_content"]?.ToString();
                            }
                            catch
                            {
                                // 忽略損毀或心跳包等非 JSON 片段
                            }

                            if (!string.IsNullOrEmpty(reasoning))
                            {
                                if (!inReasoning)
                                {
                                    inReasoning = true;
                                    onChunkReceived?.Invoke("<think>");
                                }
                                onChunkReceived?.Invoke(reasoning);
                            }

                            if (!string.IsNullOrEmpty(content))
                            {
                                if (inReasoning)
                                {
                                    inReasoning = false;
                                    onChunkReceived?.Invoke("</think>");
                                }
                                onChunkReceived?.Invoke(content);
                            }
                        }
                    }
                    if (inReasoning)
                    {
                        onChunkReceived?.Invoke("</think>");
                    }
                }
            }
        }

        protected override string DefaultTestModel => "gpt-4o-mini";

        public override async Task<List<string>> FetchAvailableModelsAsync()
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);

            string url = endpoint;
            if (url.EndsWith("/chat/completions"))
            {
                url = url.Replace("/chat/completions", "/models");
            }
            else if (!url.EndsWith("/models"))
            {
                url = url.TrimEnd(new char[] { '/' }) + "/models";
            }

            string responseJson = await SendGetAsync(url, apiKey).ConfigureAwait(false);
            var list = new List<string>();
            try
            {
                var obj = JObject.Parse(responseJson);
                var data = obj["data"] as JArray;
                if (data != null)
                {
                    foreach (var item in data)
                    {
                        string id = item["id"]?.ToString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            list.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new RimLLMException(LLMError.InvalidResponse, $"Failed to fetch {ProviderId} models list: {ex.Message}", ex);
            }
            return list;
        }
    }
}
