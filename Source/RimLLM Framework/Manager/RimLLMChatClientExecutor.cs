using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.ClientModel;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
    /// <summary>將內部 RimLLMRequest 轉換為 MEAI IChatClient 呼叫。</summary>
    internal static class RimLLMChatClientExecutor
    {
        internal static RimLLMRequest CreateFromChatOptions(
            IEnumerable<ChatMessage> messages,
            ChatOptions options,
            string model,
            CancellationToken cancellationToken = default)
        {
            var list = new List<ChatMessage>(messages ?? new List<ChatMessage>());
            string systemPrompt = null;
            if (list.Count > 0 && list[0]?.Role == ChatRole.System)
            {
                systemPrompt = list[0].Text;
            }
            var request = new RimLLMRequest
            {
                Messages = list,
                SystemPrompt = systemPrompt,
                Temperature = options?.Temperature,
                MaxOutputTokens = options?.MaxOutputTokens,
                ReasoningEffort = options?.Reasoning?.Effort,
                CancellationToken = cancellationToken,
                PreferredModelId = model
            };
            if (options?.AdditionalProperties != null)
            {
                if (options.AdditionalProperties.TryGetValue("rimllm_cached_context", out object cc) && cc is string ccStr)
                    request.CachedContext = ccStr;
                if (options.AdditionalProperties.TryGetValue("rimllm_enable_context_caching", out object ec) && ec is bool ecBool)
                    request.EnableContextCaching = ecBool;
                if (options.AdditionalProperties.TryGetValue("rimllm_disable_reasoning", out object dr) && dr is bool drBool)
                    request.DisableReasoning = drBool;
            }
            return request;
        }
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

                ReadUsage(response?.Usage, out int promptTokens, out int completionTokens, out int cachedPromptTokens);
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

            ReadUsage(lastUsage, out int promptTokens, out int completionTokens, out int cachedPromptTokens);
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
            if (request.ReasoningEffort.HasValue)
            {
                options.Reasoning = new ReasoningOptions { Effort = request.ReasoningEffort.Value };
            }

            // 與 raw 路徑一致：含 Dictionary 的開放式 map 型別仍送出 response_format，
            // 但 strict 改為 false，否則服務端會拒絕（AdditionalProperties["strict"] 控制
            // response_format.json_schema.strict，OpenAIClientExtensions.HasStrict）。
            if (useNativeSchema && request.ResponseType != null)
            {
                string schemaJson = RimLLMJsonHelper.GenerateJsonSchemaString(request.ResponseType);
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
            string rawBody = SafeGetRawBody(ex);
            string message = !string.IsNullOrEmpty(rawBody) ? rawBody : ex.Message;

            // 顯示用訊息經過淨化與截斷；關鍵字偵測則沿用未截斷的原始內容，避免關鍵字被切掉。
            return LLMErrorMapper.CreateException(
                ex.Status,
                RimLLMLog.SanitizeForLog(message, 300),
                ParseRetryAfter(ex),
                ex,
                detectionText: message);
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
                // 交給 LLMErrorMapper 統一解析：秒數與 HTTP 日期兩種格式都吃，
                // 與 raw HTTP 路徑（BaseHttpProvider）行為一致。
                return ex.GetRawResponse()?.Headers.TryGetValue("Retry-After", out string value) == true
                    ? LLMErrorMapper.ParseRetryAfter(value)
                    : null;
            }
            catch
            {
                // 無法解析 Retry-After 時忽略，重試仍會採用使用者設定的延遲。
                return null;
            }
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
                ReadUsage(usage, out promptTokens, out completionTokens, out cachedPromptTokens);
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

        /// <summary>
        /// 由 SDK 的 <see cref="UsageDetails"/> 取出三項 token 計數；usage 為 null 時一律回傳 0。
        /// </summary>
        private static void ReadUsage(UsageDetails usage, out int promptTokens, out int completionTokens, out int cachedPromptTokens)
        {
            promptTokens = ToInt32(usage?.InputTokenCount);
            completionTokens = ToInt32(usage?.OutputTokenCount);
            cachedPromptTokens = ToInt32(usage?.CachedInputTokenCount);
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
