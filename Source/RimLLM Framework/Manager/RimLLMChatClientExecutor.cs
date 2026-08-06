using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Manager
{
    /// <summary>將共用 LLMRequest 轉換為 MEAI IChatClient 呼叫。</summary>
    internal static class RimLLMChatClientExecutor
    {
        /// <summary>
        /// 非串流請求：以 <paramref name="timeoutSeconds"/> 建立整體逾時，並與呼叫端的取消 Token 連動。
        /// 官方 SDK 的 client 本身沒有套用使用者設定的 ApiTimeout，因此在此統一補上，
        /// 使 SDK 路徑與 raw HTTP 路徑的逾時語意一致。
        /// </summary>
        public static async Task<string> GenerateAsync(
            IChatClient client,
            LLMRequest request,
            string model,
            bool useNativeSchema,
            string providerId,
            float timeoutSeconds)
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
                        BuildOptions(request, model, useNativeSchema),
                        linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!request.CancellationToken.IsCancellationRequested)
                {
                    throw new RimLLMException(LLMError.Timeout, $"{providerId} 請求逾時（{timeoutSeconds} 秒）。");
                }

                string text = response?.Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new RimLLMException(LLMError.InvalidResponse, $"{providerId} 回傳空白內容。");
                }

                RecordUsage(providerId, model, request, text, response?.Usage);
                return text;
            }
        }

        /// <summary>
        /// 串流請求：採「閒置逾時」語意 —— 每收到一個 chunk 就重設計時器。
        /// 對長回應而言整體逾時並不合理，因此 ApiTimeout 在此代表「多久沒有新內容就視為斷線」。
        /// </summary>
        public static async Task StreamAsync(
            IChatClient client,
            LLMRequest request,
            string model,
            bool useNativeSchema,
            string providerId,
            Action<string> onChunkReceived,
            float timeoutSeconds)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (request == null) throw new ArgumentNullException(nameof(request));

            TimeSpan idleTimeout = ResolveTimeout(timeoutSeconds);
            var responseBuilder = new StringBuilder();

            using (var timeoutCts = new CancellationTokenSource(idleTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, request.CancellationToken))
            {
                try
                {
                    await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                        BuildMessages(request),
                        BuildOptions(request, model, useNativeSchema),
                        linkedCts.Token))
                    {
                        // 收到任何更新即重設閒置計時器，避免長回應被整體逾時誤殺。
                        timeoutCts.CancelAfter(idleTimeout);

                        string text = update?.Text;
                        if (string.IsNullOrEmpty(text)) continue;
                        responseBuilder.Append(text);
                        onChunkReceived?.Invoke(text);
                    }
                }
                catch (OperationCanceledException) when (!request.CancellationToken.IsCancellationRequested)
                {
                    throw new RimLLMException(LLMError.Timeout, $"{providerId} 串流閒置逾時（{timeoutSeconds} 秒未收到新內容）。");
                }
            }

            if (responseBuilder.Length == 0)
            {
                // 空串流幾乎都是連線被中斷，屬可重試錯誤；用 InvalidResponse 會讓 fallback 失效。
                throw new RimLLMException(LLMError.NetworkError, $"{providerId} 回傳空白串流內容。");
            }

            RecordUsage(providerId, model, request, responseBuilder.ToString(), null);
        }

        private static TimeSpan ResolveTimeout(float timeoutSeconds)
        {
            return TimeSpan.FromSeconds(timeoutSeconds > 0f ? timeoutSeconds : 30f);
        }

        private static IList<ChatMessage> BuildMessages(LLMRequest request)
        {
            var messages = new List<ChatMessage>();
            string systemPrompt = request.GetEffectiveSystemPrompt();
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
            }
            messages.Add(new ChatMessage(ChatRole.User, request.Prompt ?? string.Empty));
            return messages;
        }

        private static ChatOptions BuildOptions(LLMRequest request, string model, bool useNativeSchema)
        {
            var options = new ChatOptions
            {
                ModelId = model,
                Temperature = request.Temperature,
                MaxOutputTokens = request.MaxTokens
            };

            // 含 Dictionary 的型別會產生開放式 map，而 IChatClient 的 JSON Schema response format
            // 走的是 strict 模式，服務端會拒絕。這類型別改走提示式 JSON + RepairJson fallback，
            // CreateDummyInstance 已能正確產生 Dictionary 的範例 JSON。
            if (useNativeSchema && request.ResponseType != null &&
                !RimLLMJsonHelper.ContainsOpenEndedMap(request.ResponseType))
            {
                string schemaJson = RimLLMJsonHelper.GenerateJsonSchema(
                    request.ResponseType,
                    uppercaseTypes: false).ToString();
                using (JsonDocument document = JsonDocument.Parse(schemaJson))
                {
                    options.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                        document.RootElement.Clone(),
                        "rimllm_response",
                        "RimLLM structured response");
                }
            }
            return options;
        }

        private static void RecordUsage(
            string providerId,
            string model,
            LLMRequest request,
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
                promptTokens = EstimateTokens((systemPrompt?.Length ?? 0) + (request.Prompt?.Length ?? 0));
                completionTokens = EstimateTokens(responseText?.Length ?? 0);
            }

            try
            {
                if (RimLLMProvider.Instance is RimLLMManager manager)
                {
                    manager.RecordUsage(
                        providerId,
                        model,
                        Math.Max(1, promptTokens),
                        Math.Max(1, completionTokens),
                        Math.Max(0, cachedPromptTokens));
                }
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
