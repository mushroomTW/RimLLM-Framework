using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Anthropic Claude API 供應商，採用特有 Messages 格式與 SSE 串流解析。
    /// </summary>
    public class AnthropicProvider : BaseHttpProvider
    {
        public override string ProviderId => "Anthropic";

        public AnthropicProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        public override async Task<string> GenerateAsync(LLMRequest request, string model)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string baseEndpoint = Settings.GetEndpoint(ProviderId, "https://api.anthropic.com/v1/messages");

            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = request.Prompt
                }
            };

            int maxTokens = request.MaxTokens;
            float temperature = request.Temperature;
            JObject thinkingConfig = null;

            thinkingConfig = BuildAnthropicThinkingConfig(model, request.ReasoningEffort, ref maxTokens, ref temperature);

            var payload = new JObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature
            };

            if (thinkingConfig != null)
            {
                payload["thinking"] = thinkingConfig;
            }

            // Anthropic 的 System Prompt 必須放在頂層欄位，並支援快取控制
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                if (request.EnableContextCaching)
                {
                    payload["system"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = request.SystemPrompt,
                            ["cache_control"] = new JObject
                            {
                                ["type"] = "ephemeral"
                            }
                        }
                    };
                }
                else
                {
                    payload["system"] = request.SystemPrompt;
                }
            }

            string responseJson = await SendPostAsync(baseEndpoint, payload.ToString(), apiKey, "Anthropic", cancellationToken: request.CancellationToken).ConfigureAwait(false);

            try
            {
                var responseObj = JObject.Parse(responseJson);
                var contentArray = responseObj["content"] as JArray;
                
                var sb = new StringBuilder();
                bool hasThoughts = false;
                if (contentArray != null)
                {
                    foreach (var item in contentArray)
                    {
                        string type = item["type"]?.ToString();
                        if (type == "thinking")
                        {
                            string thinkingText = item["thinking"]?.ToString();
                            if (!string.IsNullOrEmpty(thinkingText))
                            {
                                if (!hasThoughts)
                                {
                                    sb.Append("<think>\n");
                                    hasThoughts = true;
                                }
                                sb.Append(thinkingText);
                            }
                        }
                        else if (type == "text")
                        {
                            string textVal = item["text"]?.ToString();
                            if (!string.IsNullOrEmpty(textVal))
                            {
                                if (hasThoughts)
                                {
                                    sb.Append("\n</think>\n");
                                    hasThoughts = false;
                                }
                                sb.Append(textVal);
                            }
                        }
                    }
                }
                if (hasThoughts)
                {
                    sb.Append("\n</think>");
                }

                string text = sb.ToString();
                if (string.IsNullOrEmpty(text))
                {
                    throw new RimLLMException(LLMError.InvalidResponse, "Anthropic response JSON is missing text and thinking fields in content blocks");
                }

                // 記錄 Token 使用量
                var usage = responseObj["usage"];
                if (usage != null)
                {
                    int prompt = usage["input_tokens"]?.Value<int>() ?? 0;
                    int completion = usage["output_tokens"]?.Value<int>() ?? 0;
                    if (RimLLMProvider.Instance is RimLLMManager manager)
                    {
                        manager.RecordUsage(ProviderId, model, prompt, completion);
                    }
                }

                return text;
            }
            catch (Exception ex) when (!(ex is RimLLMException))
            {
                throw new RimLLMException(LLMError.InvalidResponse, $"Failed to parse Anthropic response: {ex.Message}", ex);
            }
        }

        public override async Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string baseEndpoint = Settings.GetEndpoint(ProviderId, "https://api.anthropic.com/v1/messages");

            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = request.Prompt
                }
            };

            int maxTokens = request.MaxTokens;
            float temperature = request.Temperature;
            JObject thinkingConfig = null;

            thinkingConfig = BuildAnthropicThinkingConfig(model, request.ReasoningEffort, ref maxTokens, ref temperature);

            var payload = new JObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature,
                ["stream"] = true
            };

            if (thinkingConfig != null)
            {
                payload["thinking"] = thinkingConfig;
            }

            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                if (request.EnableContextCaching)
                {
                    payload["system"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = request.SystemPrompt,
                            ["cache_control"] = new JObject
                            {
                                ["type"] = "ephemeral"
                            }
                        }
                    };
                }
                else
                {
                    payload["system"] = request.SystemPrompt;
                }
            }

            float timeoutSeconds = Settings?.ApiTimeout ?? 30f;
            float streamTimeout = Math.Max(timeoutSeconds * 2f, 120f); // 串流給予寬鬆的超時保護

            using (var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(streamTimeout)))
            using (var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, request.CancellationToken))
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, baseEndpoint))
            {
                httpRequest.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                httpRequest.Headers.Add("x-api-key", apiKey);
                httpRequest.Headers.Add("anthropic-version", "2023-06-01");
                httpRequest.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31,thinking-2025-02-15");

                HttpResponseMessage response = null;
                try
                {
                    response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex)
                {
                    response?.Dispose();
                    throw new RimLLMException(LLMError.NetworkError, $"Anthropic stream request failed: {ex.Message}", ex);
                }

                using (response)
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream))
                {
                    bool inReasoning = false;
                    int totalCompletionChars = 0;
                    int finalPromptTokens = 0;
                    int finalCompletionTokens = 0;
                    bool hasUsage = false;

                    while (!reader.EndOfStream)
                    {
                        if (cts.Token.IsCancellationRequested)
                        {
                            throw new RimLLMException(LLMError.Timeout, "Stream request timed out");
                        }

                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) continue;
                        line = line.Trim();

                        if (line.StartsWith("data: "))
                        {
                            string json = line.Substring(6);
                            try
                            {
                                var token = JObject.Parse(json);
                                string eventType = token["type"]?.ToString();
                                if (eventType == "message_start")
                                {
                                    var usage = token["message"]?["usage"];
                                    if (usage != null)
                                    {
                                        finalPromptTokens = usage["input_tokens"]?.Value<int>() ?? 0;
                                        finalCompletionTokens = usage["output_tokens"]?.Value<int>() ?? 0;
                                        hasUsage = true;
                                    }
                                }
                                else if (eventType == "message_delta")
                                {
                                    var usage = token["usage"];
                                    if (usage != null)
                                    {
                                        finalCompletionTokens = usage["output_tokens"]?.Value<int>() ?? finalCompletionTokens;
                                        hasUsage = true;
                                    }
                                }

                                // Anthropic 串流字元片段在 content_block_delta 事件中
                                if (eventType == "content_block_delta")
                                {
                                    var delta = token["delta"];
                                    if (delta != null)
                                    {
                                        string type = delta["type"]?.ToString();
                                        if (type == "thinking_delta" || delta["thinking"] != null)
                                        {
                                            string thinking = delta["thinking"]?.ToString();
                                            if (!string.IsNullOrEmpty(thinking))
                                            {
                                                totalCompletionChars += thinking.Length;
                                                if (!inReasoning)
                                                {
                                                    inReasoning = true;
                                                    onChunkReceived?.Invoke("<think>");
                                                }
                                                onChunkReceived?.Invoke(thinking);
                                            }
                                        }
                                        else
                                        {
                                            string text = delta["text"]?.ToString();
                                            if (!string.IsNullOrEmpty(text))
                                            {
                                                totalCompletionChars += text.Length;
                                                if (inReasoning)
                                                {
                                                    inReasoning = false;
                                                    onChunkReceived?.Invoke("</think>");
                                                }
                                                onChunkReceived?.Invoke(text);
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // 忽略非 JSON 行或未完成片段
                            }
                        }
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
                            int systemLen = request.SystemPrompt?.Length ?? 0;
                            int promptLen = request.Prompt?.Length ?? 0;
                            int estPrompt = (int)((systemLen + promptLen) * 0.8f);
                            int estCompletion = (int)(totalCompletionChars * 0.8f);
                            manager.RecordUsage(ProviderId, model, Math.Max(1, estPrompt), Math.Max(1, estCompletion));
                        }
                    }
                }
            }
        }

        protected bool IsAnthropicThinkingModel(string modelName)
        {
            return modelName != null && (
                modelName.IndexOf("claude-3-7", StringComparison.OrdinalIgnoreCase) >= 0 || 
                modelName.IndexOf("claude-3.7", StringComparison.OrdinalIgnoreCase) >= 0 || 
                modelName.IndexOf("claude-4", StringComparison.OrdinalIgnoreCase) >= 0 || 
                modelName.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0
            );
        }

        protected JObject BuildAnthropicThinkingConfig(string model, LLMReasoningEffort effort, ref int maxTokens, ref float temperature)
        {
            if (effort == LLMReasoningEffort.None || !IsAnthropicThinkingModel(model))
            {
                return null;
            }

            bool isAdaptive = model != null && (
                              model.IndexOf("claude-4", StringComparison.OrdinalIgnoreCase) >= 0 || 
                              model.IndexOf("4.", StringComparison.OrdinalIgnoreCase) >= 0);

            JObject thinkingConfig = null;

            if (isAdaptive)
            {
                thinkingConfig = new JObject
                {
                    ["type"] = "adaptive"
                };
                if (effort != LLMReasoningEffort.Auto)
                {
                    thinkingConfig["effort"] = effort.ToString().ToLower();
                }
            }
            else
            {
                int budget = 1024; // Default for Auto
                if (effort == LLMReasoningEffort.Low) budget = 1024;
                else if (effort == LLMReasoningEffort.Medium) budget = 2048;
                else if (effort == LLMReasoningEffort.High) budget = 4096;

                thinkingConfig = new JObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = budget
                };
                maxTokens = Math.Max(maxTokens, budget + 1024); // 確保 max_tokens 大於 budget_tokens
            }

            temperature = 1.0f; // 啟用 thinking 時強制的溫度值
            return thinkingConfig;
        }

        protected override string DefaultTestModel => "claude-3-5-sonnet-20241022";

        public override Task<List<string>> FetchAvailableModelsAsync()
        {
            // Anthropic 官方的 models endpoint 目前常有限制或不穩定，
            // 故於此手動回傳 Claude 常用模型以利玩家最穩健地選用。
            var models = new List<string>
            {
                "claude-3-5-sonnet-20241022",
                "claude-3-5-haiku-20241022",
                "claude-3-opus-20240229"
            };
            return Task.FromResult(models);
        }
    }
}
