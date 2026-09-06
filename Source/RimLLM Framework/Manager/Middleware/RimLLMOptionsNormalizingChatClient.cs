extern alias bclasync;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 把玩家在設定中指定的預設思考強度套進未指定的請求。
    /// </summary>
    /// <remarks>
    /// 這一層必須是整條堆疊的最外層，且務必排在回應快取之前：
    /// 快取鍵是由「所有會影響輸出的欄位」算出來的，若正規化排在後面，
    /// 算鍵時看到的思考強度會和實際送出的不一致，導致兩個實際等價的請求各打一次 API。
    /// </remarks>
    internal sealed class RimLLMOptionsNormalizingChatClient : DelegatingChatClient
    {
        private readonly IRimLLMSettings _settings;

        public RimLLMOptionsNormalizingChatClient(IChatClient innerClient, IRimLLMSettings settings)
            : base(innerClient)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            return base.GetResponseAsync(messages, Normalize(options), cancellationToken);
        }

        public override bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            return base.GetStreamingResponseAsync(messages, Normalize(options), cancellationToken);
        }

        /// <summary>
        /// 呼叫端已指定思考強度、或玩家未設定預設值時原樣放行。
        /// 需要改寫時一律複製，不可就地修改呼叫端傳進來的物件。
        /// </summary>
        private ChatOptions Normalize(ChatOptions options)
        {
            if (_settings.DefaultReasoningEffort == null) return options;
            if (options?.Reasoning?.Effort != null) return options;

            ChatOptions clone = options?.Clone() ?? new ChatOptions();
            clone.Reasoning = new ReasoningOptions { Effort = _settings.DefaultReasoningEffort };
            return clone;
        }
    }
#pragma warning restore S101
}
