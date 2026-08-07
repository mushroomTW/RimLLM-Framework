using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.ClientModel;
using Microsoft.Extensions.AI;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
    /// <summary>將內部 RimLLMRequest 轉換為 MEAI IChatClient 呼叫。</summary>
    internal static class RimLLMChatClientExecutor
    {
        /// <summary>
        /// 非串流請求：以 <paramref name="timeoutSeconds"/> 建立整體逾時，並與呼叫端的取消 Token 連動。
        /// 官方 SDK 的 client 本身沒有套用使用者設定的 ApiTimeout，因此在此統一補上，
        /// 使 SDK 路徑與 raw HTTP 路徑的逾時語意一致。
        /// <paramref name="customizeOptions"/> 為供應商專屬的 options 客製化（如 reasoning、Patch 逃生門）。
        /// </summary>
        public static async Task<RimLLMGenerationResult> GenerateAsync(
            IChatClient client,
            RimLLMRequest request,
            string model,
            bool useNativeSchema,
            string providerId,
            float timeoutSeconds,
            Action<ChatOptions> customizeOptions = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (request == null) throw new ArgumentNullException(nameof(request));

            using (var timeoutCts = new CancellationTokenSource(ResolveTimeout(timeoutSeconds)))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, request.CancellationToken))
            {
                ChatResponse response;
                try
                {
                    response = await client.GetResponseAsync(
                        BuildMessages(request),
                        BuildOptions(request, model, useNativeSchema, customizeOptions),
                        linkedCts.Token).ConfigureAwait(false);
                }
                catch (ClientResultException ex)
                {
                    throw MapChatClientException(providerId, ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new RimLLMException(LLMError.NetworkError, $"{providerId} 網路連線錯誤: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", ex);
                }
                catch (OperationCanceledException) when (!request.CancellationToken.IsCancellationRequested)
                {
                    throw new RimLLMException(LLMError.Timeout, $"{providerId} 請求逾時（{timeoutSeconds} 秒）。");
                }

                // reasoning_content 由 SDK 對映為 TextReasoningContent，在此補上與 raw 路徑一致的 think 封裝。
                string text = response?.Text ?? string.Empty;
                string reasoning = ExtractReasoningText(response);
                string result;
                if (!string.IsNullOrEmpty(reasoning))
                {
                    result = string.IsNullOrEmpty(text)
                        ? $"<think>\n{reasoning}\n</think>"
                        : $"<think>\n{reasoning}\n</think>\n\n{text}";
                }
                else
                {
                    result = text;
                }

                if (string.IsNullOrWhiteSpace(result))
                {
                    throw new RimLLMException(LLMError.InvalidResponse, $"{providerId} 回傳空白內容。");
                }

                int promptTokens = 0, completionTokens = 0, cachedPromptTokens = 0;
                if (response?.Usage != null)
                {
                    promptTokens = ToInt32(response.Usage.InputTokenCount);
                    completionTokens = ToInt32(response.Usage.OutputTokenCount);
                    cachedPromptTokens = ToInt32(response.Usage.CachedInputTokenCount);
                }
                RecordUsage(providerId, model, request, result, response?.Usage);
                return new RimLLMGenerationResult
                {
                    Text = result,
                    ProviderId = providerId,
                    ModelName = model,
                    PromptTokens = Math.Max(1, promptTokens),
                    CompletionTokens = Math.Max(1, completionTokens),
                    CachedPromptTokens = Math.Max(0, cachedPromptTokens)
                };
            }
        }

        /// <summary>
        /// 串流請求：採「閒置逾時」語意 —— 每收到一個 chunk 就重設計時器。
        /// 對長回應而言整體逾時並不合理，因此 ApiTimeout 在此代表「多久沒有新內容就視為斷線」。
        /// </summary>
        public static async Task<RimLLMGenerationResult> StreamAsync(
            IChatClient client,
            RimLLMRequest request,
            string model,
            bool useNativeSchema,
            string providerId,
            Action<string> onChunkReceived,
            float timeoutSeconds,
            Action<ChatOptions> customizeOptions = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (request == null) throw new ArgumentNullException(nameof(request));

            TimeSpan idleTimeout = ResolveTimeout(timeoutSeconds);
            var responseBuilder = new StringBuilder();
            bool anyOutput = false;
            bool inReasoning = false;
            UsageDetails lastUsage = null;

            using (var timeoutCts = new CancellationTokenSource(idleTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, request.CancellationToken))
            {
                try
                {
                    await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                        BuildMessages(request),
                        BuildOptions(request, model, useNativeSchema, customizeOptions),
                        linkedCts.Token))
                    {
                        // 收到任何更新即重設閒置計時器，避免長回應被整體逾時誤殺。
                        timeoutCts.CancelAfter(idleTimeout);

                        if (update?.Contents == null)
                        {
                            continue;
                        }

                        // 依序巡覽 content：reasoning 先進場（<think>）、text 退場（</think>），
                        // 與 raw SSE 路徑的處理順序一致；UsageContent 則作為串流 usage 記錄來源。
                        foreach (AIContent part in update.Contents)
                        {
                            if (part is TextReasoningContent reasoningContent && !string.IsNullOrEmpty(reasoningContent.Text))
                            {
                                anyOutput = true;
                                if (!inReasoning)
                                {
                                    inReasoning = true;
                                    Emit("<think>");
                                }
                                Emit(reasoningContent.Text);
                            }
                            else if (part is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                            {
                                anyOutput = true;
                                if (inReasoning)
                                {
                                    inReasoning = false;
                                    Emit("</think>");
                                }
                                Emit(textContent.Text);
                            }
                            else if (part is UsageContent usageContent && usageContent.Details != null)
                            {
                                lastUsage = usageContent.Details;
                            }
                        }
                    }
                }
                catch (ClientResultException ex)
                {
                    throw MapChatClientException(providerId, ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new RimLLMException(LLMError.NetworkError, $"{providerId} 串流網路連線錯誤: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", ex);
                }
                catch (OperationCanceledException) when (!request.CancellationToken.IsCancellationRequested)
                {
                    throw new RimLLMException(LLMError.Timeout, $"{providerId} 串流閒置逾時（{timeoutSeconds} 秒未收到新內容）。");
                }
            }

            if (inReasoning)
            {
                Emit("</think>");
            }

            if (!anyOutput)
            {
                // 空串流幾乎都是連線被中斷，屬可重試錯誤；用 InvalidResponse 會讓 fallback 失效。
                throw new RimLLMException(LLMError.NetworkError, $"{providerId} 回傳空白串流內容。");
            }

            RecordUsage(providerId, model, request, responseBuilder.ToString(), lastUsage);

            int promptTokens = 0, completionTokens = 0, cachedPromptTokens = 0;
            if (lastUsage != null)
            {
                promptTokens = ToInt32(lastUsage.InputTokenCount);
                completionTokens = ToInt32(lastUsage.OutputTokenCount);
                cachedPromptTokens = ToInt32(lastUsage.CachedInputTokenCount);
            }
            return new RimLLMGenerationResult
            {
                Text = responseBuilder.ToString(),
                ProviderId = providerId,
                ModelName = model,
                PromptTokens = Math.Max(1, promptTokens),
                CompletionTokens = Math.Max(1, completionTokens),
                CachedPromptTokens = Math.Max(0, cachedPromptTokens)
            };

            void Emit(string chunk)
            {
                responseBuilder.Append(chunk);
                onChunkReceived?.Invoke(chunk);
            }
        }

        private static TimeSpan ResolveTimeout(float timeoutSeconds)
        {
            return TimeSpan.FromSeconds(timeoutSeconds > 0f ? timeoutSeconds : 30f);
        }

        internal static IList<ChatMessage> BuildMessages(RimLLMRequest request)
        {
            var messages = new List<ChatMessage>(request.Messages ?? new List<ChatMessage>());
            string systemPrompt = request.GetEffectiveSystemPrompt();
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                bool hasSystem = false;
                foreach (var m in messages)
                {
                    if (m != null && m.Role == ChatRole.System)
                    {
                        hasSystem = true;
                        break;
                    }
                }
                if (!hasSystem)
                {
                    messages.Insert(0, new ChatMessage(ChatRole.System, systemPrompt));
                }
            }
            if (messages.Count == 0)
            {
                messages.Add(new ChatMessage(ChatRole.User, string.Empty));
            }
            return messages;
        }

        internal static ChatOptions BuildOptions(
            RimLLMRequest request,
            string model,
            bool useNativeSchema,
            Action<ChatOptions> customizeOptions)
        {
            var options = new ChatOptions
            {
                ModelId = model,
                Temperature = request.Temperature ?? 0.7f,
                MaxOutputTokens = request.MaxOutputTokens ?? 1024
            };

            // 框架私有欄位以 AdditionalProperties 傳遞給 provider hook（adapter 不透傳，僅框架內部讀取）
            if (options.AdditionalProperties == null)
            {
                options.AdditionalProperties = new AdditionalPropertiesDictionary();
            }
            options.AdditionalProperties["rimllm_disable_reasoning"] = request.DisableReasoning;

            // 與 raw 路徑一致：含 Dictionary 的開放式 map 型別仍送出 response_format，
            // 但 strict 改為 false，否則服務端會拒絕（AdditionalProperties["strict"] 控制
            // response_format.json_schema.strict，OpenAIClientExtensions.HasStrict）。
            if (useNativeSchema && request.ResponseType != null)
            {
                string schemaJson = RimLLMJsonHelper.GenerateJsonSchema(
                    request.ResponseType,
                    uppercaseTypes: false).ToString();
                using (JsonDocument document = JsonDocument.Parse(schemaJson))
                {
                    options.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                        document.RootElement.Clone(),
                        "custom_type",
                        "RimLLM structured response");
                }
                options.AdditionalProperties["strict"] = !RimLLMJsonHelper.ContainsOpenEndedMap(request.ResponseType);
            }

            // 供應商專屬客製化最後套用，可覆寫上述基礎選項（如 reasoning 模型的 temperature/reasoning）。
            customizeOptions?.Invoke(options);
            return options;
        }

        private static string ExtractReasoningText(ChatResponse response)
        {
            if (response?.Messages == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (ChatMessage message in response.Messages)
            {
                if (message?.Contents == null)
                {
                    continue;
                }
                foreach (AIContent content in message.Contents)
                {
                    if (content is TextReasoningContent reasoningContent && !string.IsNullOrEmpty(reasoningContent.Text))
                    {
                        sb.Append(reasoningContent.Text);
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 將官方 SDK 拋出的 ClientResultException 對照為既有 raw HTTP 路徑的 LLMError 語意
        /// （與 BaseHttpProvider.ThrowHttpError 的狀態碼對照一致）。
        /// </summary>
        private static RimLLMException MapChatClientException(string providerId, ClientResultException ex)
        {
            int? status = ex.Status;
            string rawBody = SafeGetRawBody(ex);
            string message = !string.IsNullOrEmpty(rawBody) ? rawBody : ex.Message;
            string friendly = RimLLMLog.SanitizeForLog(message, 300);
            TimeSpan? retryAfter = ParseRetryAfter(ex);

            if (status == 401 || status == 403)
            {
                return new RimLLMException(LLMError.InvalidKey, $"Invalid API key or authorization failed: {friendly}");
            }
            if (status == 402)
            {
                return new RimLLMException(LLMError.QuotaExceeded, $"Payment required, please check your account balance: {friendly}");
            }
            if (status == 404)
            {
                // 模型或端點不存在屬於不可重試錯誤，重試只會空耗延遲。
                return new RimLLMException(LLMError.ModelNotFound, $"Model or endpoint not found: {friendly}");
            }
            if (status == 408)
            {
                return new RimLLMException(LLMError.Timeout, $"Request timed out on the server side: {friendly}")
                {
                    RetryAfter = retryAfter
                };
            }
            if (status == 400 || status == 413 || status == 422)
            {
                // 請求本身有問題，以同一份 payload 重試必然再次失敗。
                return new RimLLMException(LLMError.InvalidResponse, $"The request was rejected by the provider: {friendly}")
                {
                    IsSchemaRejection = LooksLikeSchemaRejection(message)
                };
            }
            if (status == 429)
            {
                if (ContainsIgnoreCase(message, "quota") || ContainsIgnoreCase(message, "insufficient"))
                {
                    return new RimLLMException(LLMError.QuotaExceeded, "API insufficient quota (insufficient_quota), please check your account balance.")
                    {
                        RetryAfter = retryAfter
                    };
                }
                return new RimLLMException(LLMError.RateLimit, $"Rate limit triggered: {friendly}")
                {
                    RetryAfter = retryAfter
                };
            }
            if (status.HasValue && status.Value >= 500)
            {
                return new RimLLMException(LLMError.ProviderOffline, $"Internal server error: {friendly}")
                {
                    RetryAfter = retryAfter
                };
            }
            return new RimLLMException(LLMError.Unknown, $"API request failed: {friendly}", ex);
        }

        private static string SafeGetRawBody(ClientResultException ex)
        {
            try
            {
                BinaryData content = ex.GetRawResponse()?.Content;
                if (content == null)
                {
                    return null;
                }
                string body = content.ToString();
                return string.IsNullOrWhiteSpace(body) ? null : body;
            }
            catch
            {
                return null;
            }
        }

        private static TimeSpan? ParseRetryAfter(ClientResultException ex)
        {
            try
            {
                if (ex.GetRawResponse()?.Headers.TryGetValue("Retry-After", out string value) == true &&
                    double.TryParse(value, out double seconds) && seconds > 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }
            catch
            {
                // 無法解析 Retry-After 時忽略，重試仍會採用使用者設定的延遲。
            }
            return null;
        }

        private static bool LooksLikeSchemaRejection(string message)
        {
            return message != null &&
                (ContainsIgnoreCase(message, "response_format") ||
                 ContainsIgnoreCase(message, "json_schema") ||
                 ContainsIgnoreCase(message, "schema"));
        }

        private static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void RecordUsage(
            string providerId,
            string model,
            RimLLMRequest request,
            string responseText,
            UsageDetails usage)
        {
            int promptTokens;
            int completionTokens;
            int cachedPromptTokens = 0;
            if (usage != null && (usage.InputTokenCount.HasValue || usage.OutputTokenCount.HasValue))
            {
                promptTokens = ToInt32(usage.InputTokenCount);
                completionTokens = ToInt32(usage.OutputTokenCount);
                cachedPromptTokens = ToInt32(usage.CachedInputTokenCount);
            }
            else
            {
                string systemPrompt = request.GetEffectiveSystemPrompt();
                int promptChars = systemPrompt?.Length ?? 0;
                foreach (var m in request.Messages)
                {
                    if (m != null && !string.IsNullOrEmpty(m.Text)) promptChars += m.Text.Length;
                }
                promptTokens = EstimateTokens(promptChars);
                completionTokens = EstimateTokens(responseText?.Length ?? 0);
            }

            try
            {
                RimLLMProvider.Manager.RecordUsage(
                    providerId,
                    model,
                    Math.Max(1, promptTokens),
                    Math.Max(1, completionTokens),
                    Math.Max(0, cachedPromptTokens));
            }
            catch (InvalidOperationException)
            {
                // 直接測試 provider 時可能尚未建立 manager；不影響實際回應。
            }
        }

        private static int EstimateTokens(int characterCount)
        {
            return Math.Max(1, (int)Math.Ceiling(characterCount * 0.8d));
        }

        private static int ToInt32(long? value)
        {
            if (!value.HasValue || value.Value <= 0) return 0;
            return value.Value > int.MaxValue ? int.MaxValue : (int)value.Value;
        }
    }
}
