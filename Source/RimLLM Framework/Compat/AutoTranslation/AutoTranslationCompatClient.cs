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
    /// 以 RimLLM 的 <see cref="IChatClient"/> 承接 Auto Translation 的文本與批次翻譯流量。
    /// </summary>
    /// <remarks>
    /// 本類別刻意不引用任何 Auto Translation 專有型別，以求徹底解耦與高測試性；
    /// 它的公開介面只處理中性型別（string、JSON）。
    /// </remarks>
    internal sealed class AutoTranslationCompatClient
    {
        private IChatClient _chat;

        private IChatClient Chat => _chat ?? (_chat = RimLLMProvider.CreateChatClient(AutoTranslationCompatTarget.PackageId));

        /// <summary>正式路徑：延遲到第一次請求才向 RimLLM 取 client，避免在攔截掛載階段觸碰 manager。</summary>
        public AutoTranslationCompatClient()
        {
        }

        /// <summary>測試用：注入假的 <see cref="IChatClient"/>，驗證訊息建構、參數傳遞與 JSON 回傳格式。</summary>
        internal AutoTranslationCompatClient(IChatClient chat)
        {
            _chat = chat;
        }

        /// <summary>
        /// 同步執行翻譯請求並回傳 OpenAI 相容 JSON 回應。
        /// Auto Translation 的背景工作線程透過此方法取得翻譯結果。
        /// </summary>
        /// <param name="text">待翻譯文字或 XML 批次文件。</param>
        /// <param name="prompt">系統提示詞（包含翻譯規則與佔位符防護說明）。</param>
        /// <returns>包含 content、prompt_tokens 與 completion_tokens 的 JSON 字串。</returns>
        public string GetResponseUnsafe(string text, string prompt)
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, prompt ?? string.Empty),
                new ChatMessage(ChatRole.User, text ?? string.Empty)
            };

            ChatOptions options = BuildOptions();

            ChatResponse response;
            try
            {
                response = Task.Run(() => Chat.GetResponseAsync(messages, options, CancellationToken.None)).GetAwaiter().GetResult();
            }
            catch (AggregateException agg) when (agg.InnerExceptions.Count > 0)
            {
                throw agg.Flatten().InnerExceptions[0];
            }

            string responseText = response.Text ?? string.Empty;
            int inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
            int outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                content = responseText,
                prompt_tokens = inputTokens,
                completion_tokens = outputTokens
            }, SharedJsonOptions);
        }

        private static readonly System.Text.Json.JsonSerializerOptions SharedJsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 翻譯任務著重精準度與低延遲，預設關閉思考鏈（DisableReasoning）並使用溫和的溫度（0.3）。
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
