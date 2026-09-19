using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.ClientModel;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
    /// <summary>以逐候選的參數（模型、schema 方言、逾時）包裝一次 MEAI IChatClient 呼叫。</summary>
    internal static class RimLLMChatClientExecutor
    {
        /// <summary>
        /// 非串流請求：以 <paramref name="timeoutSeconds"/> 建立整體逾時，並與呼叫端的取消 Token 連動。
        /// 官方 SDK 的 client 本身沒有套用使用者設定的 ApiTimeout，因此在此統一補上，
        /// 使 SDK 路徑與 raw HTTP 路徑的逾時語意一致。
        /// <paramref name="customizeOptions"/> 為供應商專屬的 options 客製化（如 reasoning、Patch 逃生門）。
        /// </summary>
#pragma warning disable S107 // reason: 內部轉接器協調 IChatClient、逾時與逐候選參數，參數物件無重用價值且降低可讀性，維持窄範圍抑制
        public static async Task<ChatResponse> GenerateAsync(
            IChatClient client,
            IEnumerable<ChatMessage> messages,
            ChatOptions options,
            string model,
            bool useNativeSchema,
            string providerId,
            float timeoutSeconds,
            CancellationToken cancellationToken,
            Action<ChatOptions> customizeOptions = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            IList<ChatMessage> builtMessages = BuildMessages(messages, options);

            using (var timeoutCts = new CancellationTokenSource(ResolveTimeout(timeoutSeconds)))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
            {
                ChatResponse response;
                try
                {
                    response = await client.GetResponseAsync(
                        builtMessages,
                        BuildOptions(options, model, useNativeSchema, customizeOptions),
                        linkedCts.Token).ConfigureAwait(false);
                }
                catch (ClientResultException ex)
                {
                    throw MapChatClientException(ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new RimLLMException(LLMError.NetworkError, $"{providerId} 網路連線錯誤: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", ex);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RimLLMException(LLMError.Timeout, $"{providerId} 請求逾時（{timeoutSeconds} 秒）。");
                }

                // 回應原樣交還。ResponseId、CreatedAt、ConversationId、RawRepresentation 這些
                // 只有 provider 知道的欄位，一旦在這裡拆成中介型別再重組就補不回來了。
                // 推理內容維持 MEAI 原生的 TextReasoningContent，不再合成 <think> 文字——
                // 那是呈現層的事，由 RimLLMThinkTagFormatter 在需要時組裝。
                //
                // 空回應判定仍要逐則彙整 Contents：response.Text 只涵蓋 TextContent，
                // 內層 client 回傳多則訊息時（巢狀 decorator）工具呼叫與推理內容不在其中。
                IList<AIContent> contents = CollectContents(response);
                bool hasToolCalls = contents != null && System.Linq.Enumerable.Any(contents, c => c is FunctionCallContent);
                bool hasReasoning = contents != null && System.Linq.Enumerable.Any(contents, c => c is TextReasoningContent);

                if (string.IsNullOrWhiteSpace(response?.Text) && !hasToolCalls && !hasReasoning)
                {
                    throw new RimLLMException(LLMError.InvalidResponse, $"{providerId} 回傳空白內容。");
                }

                RecordUsage(providerId, model, builtMessages, response.Text, response.Usage);
                return response;
            }
        }
#pragma warning restore S107

        /// <summary>
        /// 串流請求：採「閒置逾時」語意 —— 每收到一個 chunk 就重設計時器。
        /// 對長回應而言整體逾時並不合理，因此 ApiTimeout 在此代表「多久沒有新內容就視為斷線」。
        #pragma warning disable S107, S3776 // reason: S107 內部轉接器協調 IChatClient、逾時與回呼，參數物件會使呼叫端可讀性下降且無重用，維持窄範圍抑制；S3776 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
        /// </summary>
        public static async Task StreamAsync(
            IChatClient client,
            IEnumerable<ChatMessage> messages,
            ChatOptions options,
            string model,
            bool useNativeSchema,
            string providerId,
            Action<ChatResponseUpdate> onUpdateReceived,
            float timeoutSeconds,
            CancellationToken cancellationToken,
            Action<ChatOptions> customizeOptions = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            IList<ChatMessage> builtMessages = BuildMessages(messages, options);
            TimeSpan idleTimeout = ResolveTimeout(timeoutSeconds);
            // 只在 provider 沒回報用量時當作字元估算的來源，不再是回傳值。
            var textBuilder = new StringBuilder();
            bool anyOutput = false;
            UsageDetails lastUsage = null;

            using (var timeoutCts = new CancellationTokenSource(idleTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
            {
                try
                {
                    await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                        builtMessages,
                        BuildOptions(options, model, useNativeSchema, customizeOptions),
                        linkedCts.Token))
                    {
                        // 收到任何更新即重設閒置計時器，避免長回應被整體逾時誤殺。
                        timeoutCts.CancelAfter(idleTimeout);

                        if (update?.Contents == null)
                        {
                            continue;
                        }

                        // 只做記帳，不改寫內容：判斷這次串流是否真的產出過東西，
                        // 並收集用量。推理內容維持 MEAI 原生的 TextReasoningContent，
                        // 不再被合成成 <think> 文字——那是呈現層的事。
                        foreach (AIContent part in update.Contents)
                        {
                            if (part is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                            {
                                anyOutput = true;
                                textBuilder.Append(textContent.Text);
                            }
                            else if (part is TextReasoningContent reasoningContent && !string.IsNullOrEmpty(reasoningContent.Text))
                            {
                                anyOutput = true;
                            }
                            else if (part is FunctionCallContent)
                            {
                                // 工具呼叫沒有可顯示的文字，但仍然算是產出。
                                anyOutput = true;
                            }
                            else if (part is UsageContent usageContent && usageContent.Details != null)
                            {
                                lastUsage = usageContent.Details;
                            }
                        }

                        // 原樣轉發：呼叫端收到的就是 provider 產生的那個 update，
                        // ResponseId、FinishReason、UsageContent 一律不經改寫。
                        onUpdateReceived?.Invoke(update);
                    }
                }
                catch (ClientResultException ex)
                {
                    throw MapChatClientException(ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new RimLLMException(LLMError.NetworkError, $"{providerId} 串流網路連線錯誤: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", ex);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RimLLMException(LLMError.Timeout, $"{providerId} 串流閒置逾時（{timeoutSeconds} 秒未收到新內容）。");
                }
            }

            if (!anyOutput)
            {
                // 空串流幾乎都是連線被中斷，屬可重試錯誤；用 InvalidResponse 會讓 fallback 失效。
                throw new RimLLMException(LLMError.NetworkError, $"{providerId} 回傳空白串流內容。");
            }

            RecordUsage(providerId, model, builtMessages, textBuilder.ToString(), lastUsage);
        }
        #pragma warning restore S107, S3776

        /// <summary>彙整 <see cref="ChatResponse"/> 內所有訊息的 content，與 <c>ChatResponse.Text</c> 的涵蓋範圍一致。</summary>
        private static IList<AIContent> CollectContents(ChatResponse response)
        {
            var merged = new List<AIContent>();
            if (response?.Messages == null || response.Messages.Count == 0) return merged;
            if (response.Messages.Count == 1) return response.Messages[0]?.Contents ?? merged;

            foreach (ChatMessage message in response.Messages)
            {
                if (message?.Contents == null) continue;
                merged.AddRange(message.Contents);
            }
            return merged;
        }

        private static TimeSpan ResolveTimeout(float timeoutSeconds)
        {
            return TimeSpan.FromSeconds(timeoutSeconds > 0f ? timeoutSeconds : 30f);
        }

        /// <summary>
        /// 把可重複使用的長上下文（CachedContext）併進系統訊息。
        /// </summary>
        /// <remarks>
        /// 併進去而不是另開一則訊息，是因為 provider 端的上下文快取都以 system instruction
        /// 為單位；拆成兩則會讓相同的前綴算不到同一份快取。
        /// </remarks>
        internal static IList<ChatMessage> BuildMessages(IEnumerable<ChatMessage> messages, ChatOptions options)
        {
            var list = messages != null ? new List<ChatMessage>(messages) : new List<ChatMessage>();

            string cachedContext = RimLLMChatOptions.GetCachedContext(options);
            if (!string.IsNullOrEmpty(cachedContext))
            {
                int firstSystemIndex = list.FindIndex(m => m != null && m.Role == ChatRole.System);
                if (firstSystemIndex < 0)
                {
                    list.Insert(0, new ChatMessage(ChatRole.System, cachedContext));
                }
                else
                {
                    string existing = list[firstSystemIndex].Text;
                    if (string.IsNullOrEmpty(existing))
                    {
                        list[firstSystemIndex] = new ChatMessage(ChatRole.System, cachedContext);
                    }
                    else if (!existing.Contains(cachedContext))
                    {
                        // 附加在既有系統提示詞之後。先前這裡是「前置一份已經含有既有內容的
                        // 字串」，結果同一段系統提示詞會在訊息裡出現兩次。
                        list[firstSystemIndex] = new ChatMessage(ChatRole.System, existing + "\n\n" + cachedContext);
                    }
                }
            }

            if (list.Count == 0)
            {
                list.Add(new ChatMessage(ChatRole.User, string.Empty));
            }
            return list;
        }

        /// <summary>
        /// 以呼叫端的原始 options 為基底複製，組出送往 provider 的選項。
        /// </summary>
        internal static ChatOptions BuildOptions(
            ChatOptions source,
            string model,
            bool useNativeSchema,
            Action<ChatOptions> customizeOptions)
        {
            // 以呼叫端的原始 options 為基底複製，因此 TopP、TopK、FrequencyPenalty、
            // PresencePenalty、Seed、StopSequences、Tools、Reasoning、AdditionalProperties、
            // RawRepresentationFactory 一律原樣帶到 provider。工具能力過濾與思考強度正規化
            // 都在上游對 ChatOptions 完成，這裡不需要再重新決定一次。
            ChatOptions options = source?.Clone() ?? new ChatOptions();
            options.ModelId = model;
            options.Temperature = source?.Temperature ?? 0.7f;
            options.MaxOutputTokens = source?.MaxOutputTokens ?? 1024;

            // ResponseFormat 由框架獨佔，不可沿用呼叫端的複本：
            // 原生 schema 被拒時會以 useNativeSchema:false 重試，若呼叫端設定的 response_format
            // 在此存活，重試就會重送剛剛被拒的那一份而永久失敗。
            // 代價是純 MEAI 呼叫端自行設定的 ResponseFormat 目前仍不會生效——
            // 這留待結構化輸出改用 MEAI 原生 GetResponseAsync<T> 的階段一併處理。
            if (!useNativeSchema)
            {
                options.ResponseFormat = null;
            }

            // 框架私有欄位以 AdditionalProperties 傳遞給 provider hook（adapter 不透傳，僅框架內部讀取）
            if (options.AdditionalProperties == null)
            {
                options.AdditionalProperties = new AdditionalPropertiesDictionary();
            }
            options.AdditionalProperties[RimLLMChatOptions.DisableReasoningKey] =
                RimLLMChatOptions.GetDisableReasoning(source);
            options.AdditionalProperties[RimLLMChatOptions.ExecutorManagedKey] = true;

            Type responseType = RimLLMChatOptions.GetResponseType(source);

            // 與 raw 路徑一致：含 Dictionary 的開放式 map 型別仍送出 response_format，
            // 但 strict 改為 false，否則服務端會拒絕（AdditionalProperties["strict"] 控制
            // response_format.json_schema.strict，OpenAIClientExtensions.HasStrict）。
            if (useNativeSchema && responseType != null)
            {
                // schema 與 strict 取自同一次產生結果，避免兩者各算一次而分歧。
                RimLLMSchemaResult schema = RimLLMSchemaBuilder.Build(responseType);
#pragma warning disable S3267 // reason: 迴圈遍歷在串流與用量統計具更高可讀性與效能，刻意保留 foreach
                // Element 由 schema 結果快取，不再每請求 JsonDocument.Parse 一次。
                options.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    schema.Element,
                    "custom_type",
                    "RimLLM structured response");
                options.AdditionalProperties["strict"] = schema.StrictCompatible;
            }

            // 供應商專屬客製化最後套用，可覆寫上述基礎選項（如 reasoning 模型的 temperature/reasoning）。
            customizeOptions?.Invoke(options);
            return options;
        }

        /// <summary>
        /// 將官方 SDK 拋出的 ClientResultException 對照為 LLMError 語意
        /// （與 LLMErrorMapper 的狀態碼對照一致）。
        /// </summary>
        private static RimLLMException MapChatClientException(ClientResultException ex)
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
                // 交給 LLMErrorMapper 統一解析：秒數與 HTTP 日期兩種格式都吃。
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
            IList<ChatMessage> messages,
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
                // messages 已經是 BuildMessages 的產物，系統提示詞與 CachedContext 都在裡面。
                int promptChars = 0;
                foreach (ChatMessage m in messages)
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
#pragma warning restore S101, S2342
#pragma warning restore S3267
}