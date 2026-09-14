using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using RimTalk.Client;
using RimTalk.Data;
using RimTalk.Error;
using RimTalk.Service;
using RimTalk.Util;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// 以 RimLLM 的 <see cref="IChatClient"/> 實作 RimTalk 的 <see cref="IAIClient"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 提示工程完全沿用 RimTalk：<c>prefixMessages</c> 裡已含「Output JSONL」的系統指示，原樣送出即可，
    /// 模型回來的就是 RimTalk 期望的格式。
    /// </para>
    /// <para>
    /// 串流粒度與原生一致：RimTalk 的回應是 JSONL（多行、一行一個 pawn 氣泡），這裡把 MEAI 串流的文字塊
    /// 逐塊餵進 RimTalk 自己的 <see cref="JsonStreamParser{T}"/>，每湊齊一個完整物件就回呼一次——
    /// 第一個氣泡不必等整段對話生成完。
    /// </para>
    /// <para>
    /// 錯誤一律包成 <see cref="AIRequestException"/> 並附上 <see cref="Payload"/>，RimTalk 的重試與
    /// API Log 才能照常運作；取消則透過 <see cref="AIService.IsCancellationRequested"/> 逐塊輪詢，
    /// 與原生 client 在下載 handler 內的做法相同。
    /// </para>
    /// </remarks>
    internal sealed class RimTalkCompatClient : IAIClient
    {
        /// <summary>寫進 RimTalk API Log 的端點識別。實際 URL 由 RimLLM fallback 鏈決定，這裡只標示流量歸屬。</summary>
        private const string Endpoint = "rimllm://" + RimTalkCompatTarget.PackageId;

        /// <summary>模型尚未由回應揭露時（請求準備階段、失敗時）寫進日誌的佔位名稱。</summary>
        private const string PendingModel = "RimLLM fallback chain";

        private IChatClient _chat;

        private IChatClient Chat => _chat ?? (_chat = RimLLMProvider.CreateChatClient(RimTalkCompatTarget.PackageId));

        /// <summary>正式路徑：延遲到第一次請求才向 RimLLM 取 client，避免在攔截掛載階段觸碰 manager。</summary>
        public RimTalkCompatClient()
        {
        }

        /// <summary>測試用：注入假的 <see cref="IChatClient"/>，驗證訊息轉換、JSONL 串流切分與錯誤包裝。</summary>
        internal RimTalkCompatClient(IChatClient chat)
        {
            _chat = chat;
        }

        public Task<Payload> GetChatCompletionAsync(
            List<(Role role, string message)> prefixMessages,
            List<(Role role, string message)> messages,
            Action<Payload> onRequestPrepared = null)
        {
            return GetChatCompletionAsync(prefixMessages, messages, null, onRequestPrepared);
        }

        public async Task<Payload> GetChatCompletionAsync(
            List<(Role role, string message)> prefixMessages,
            List<(Role role, string message)> messages,
            string imageBase64,
            Action<Payload> onRequestPrepared = null)
        {
            List<ChatMessage> chatMessages = RimTalkMessageConverter.Build(prefixMessages, messages, imageBase64);
            string requestJson = RimTalkMessageConverter.DescribeRequest(chatMessages, stream: false, PendingModel);
            onRequestPrepared?.Invoke(new Payload(Endpoint, PendingModel, requestJson, null, 0));

            try
            {
                ChatResponse response = await Chat.GetResponseAsync(chatMessages, BuildOptions(), CancellationToken.None);
                int tokens = (int)(response.Usage?.TotalTokenCount ?? 0);
                return new Payload(Endpoint, response.ModelId ?? PendingModel, requestJson, response.Text, tokens)
                {
                    StatusCode = 200
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Wrap(ex, requestJson);
            }
        }

        public Task<Payload> GetStreamingChatCompletionAsync<T>(
            List<(Role role, string message)> prefixMessages,
            List<(Role role, string message)> messages,
            Action<T> onResponseParsed,
            Action<Payload> onRequestPrepared = null) where T : class
        {
            return GetStreamingChatCompletionAsync(prefixMessages, messages, null, onResponseParsed, onRequestPrepared);
        }

        public async Task<Payload> GetStreamingChatCompletionAsync<T>(
            List<(Role role, string message)> prefixMessages,
            List<(Role role, string message)> messages,
            string imageBase64,
            Action<T> onResponseParsed,
            Action<Payload> onRequestPrepared = null) where T : class
        {
            List<ChatMessage> chatMessages = RimTalkMessageConverter.Build(prefixMessages, messages, imageBase64);
            string requestJson = RimTalkMessageConverter.DescribeRequest(chatMessages, stream: true, PendingModel);
            onRequestPrepared?.Invoke(new Payload(Endpoint, PendingModel, requestJson, null, 0));

            var parser = new JsonStreamParser<T>();
            var fullText = new StringBuilder();
            string modelId = null;
            int tokens = 0;

            try
            {
                using (var cts = new CancellationTokenSource())
                {
                    await foreach (ChatResponseUpdate update in Chat.GetStreamingResponseAsync(chatMessages, BuildOptions(), cts.Token))
                    {
                        if (AIService.IsCancellationRequested())
                        {
                            cts.Cancel();
                            throw new OperationCanceledException("Request canceled by RimTalk.");
                        }
                        if (update == null) continue;

                        if (modelId == null && !string.IsNullOrEmpty(update.ModelId))
                        {
                            modelId = update.ModelId;
                        }
                        foreach (AIContent part in update.Contents)
                        {
                            if (part is UsageContent usage && usage.Details?.TotalTokenCount > 0)
                            {
                                tokens = (int)usage.Details.TotalTokenCount.Value;
                            }
                        }

                        // update.Text 只串接 TextContent，推理內容（TextReasoningContent）不會混進來污染 JSONL 解析。
                        string text = update.Text;
                        if (string.IsNullOrEmpty(text)) continue;

                        fullText.Append(text);
                        foreach (T item in parser.Parse(text))
                        {
                            onResponseParsed?.Invoke(item);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Wrap(ex, requestJson);
            }

            return new Payload(Endpoint, modelId ?? PendingModel, requestJson, fullText.ToString(), tokens)
            {
                StatusCode = 200
            };
        }

        /// <summary>
        /// RimTalk 原生 client 的 thinking ladder 第一階就是「關閉思考」（對話講求速度與成本），
        /// 這裡直接以框架的 DisableReasoning 表達同一意圖；其餘參數（溫度、上限）沿用 RimLLM 全域設定。
        /// </summary>
        private static ChatOptions BuildOptions()
        {
            return new RimLLMChatOptions { DisableReasoning = true };
        }

        private static AIRequestException Wrap(Exception ex, string requestJson)
        {
            string message = ex is RimLLMException llmEx ? $"[RimLLM:{llmEx.Error}] {ex.Message}" : ex.Message;
            return new AIRequestException(message, new Payload(Endpoint, PendingModel, requestJson, null, 0, message));
        }
    }
}
