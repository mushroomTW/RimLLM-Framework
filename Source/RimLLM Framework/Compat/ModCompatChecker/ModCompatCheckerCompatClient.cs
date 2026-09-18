using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Api;
using RimLLM_Framework.Manager;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// 以 RimLLM 的 <see cref="IChatClient"/> 承接 Mod 兼容性檢查器的 AI 診斷分析流量。
    /// </summary>
    /// <remarks>
    /// 本類別刻意不引用任何 ModCompatChecker 專有型別，以求徹底解耦與高測試性；
    /// 它的公開介面只處理中性型別（string）。
    /// 所有呼叫端（UnifiedWindow、ErrorAnalysisWindow）都在背景執行緒同步呼叫 AIService.CallAPIWithTimeout，
    /// 因此本 Client 也以同步阻塞方式執行。
    /// </remarks>
    internal sealed class ModCompatCheckerCompatClient
    {
        private IChatClient _chat;

        private IChatClient Chat => _chat ?? (_chat = RimLLMProvider.CreateChatClient(ModCompatCheckerCompatTarget.PackageId));

        /// <summary>正式路徑：延遲到第一次請求才向 RimLLM 取 client，避免在攔截掛載階段觸碰 manager。</summary>
        public ModCompatCheckerCompatClient()
        {
        }

        /// <summary>測試用：注入假的 <see cref="IChatClient"/>，驗證訊息建構與參數傳遞。</summary>
        internal ModCompatCheckerCompatClient(IChatClient chat)
        {
            _chat = chat;
        }

        /// <summary>
        /// 同步執行 AI 診斷分析並回傳模型回覆的純文字。
        /// </summary>
        /// <param name="userMessage">使用者問題或診斷提示詞（已由 PromptBuilder 建構）。</param>
        /// <param name="timeoutSeconds">逾時秒數。</param>
        /// <param name="cancelFlag">來自原 Mod 的取消旗標參照。</param>
        /// <returns>模型回覆的純文字內容。</returns>
        public string CallAPI(string userMessage, int timeoutSeconds, ref bool cancelFlag)
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.User, userMessage ?? string.Empty)
            };

            ChatOptions options = BuildOptions();

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                ChatResponse response;
                try
                {
                    response = Task.Run(() => Chat.GetResponseAsync(messages, options, cts.Token)).GetAwaiter().GetResult();
                }
                catch (AggregateException agg) when (agg.InnerExceptions.Count > 0)
                {
                    throw agg.Flatten().InnerExceptions[0];
                }

                return response.Text ?? string.Empty;
            }
        }

        /// <summary>
        /// 診斷分析著重精準度，預設關閉思考鏈（DisableReasoning）並使用溫和的溫度（0.3）。
        /// </summary>
        private static ChatOptions BuildOptions()
        {
            return new RimLLMChatOptions
            {
                DisableReasoning = true,
                Temperature = 0.3f
            };
        }
    }
}
