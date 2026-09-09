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
    /// 每日預算保護。超出上限時依 BudgetPolicy 擋下請求或改回傳靜默模擬回應。
    /// </summary>
    /// <remarks>
    /// 排在防濫用之後、佇列之前：被預算擋下的請求不該佔用併發名額。
    /// </remarks>
    internal sealed class RimLLMBudgetChatClient : DelegatingChatClient
    {
        /// <summary>靜默模擬回應的供應商／模型識別，供呼叫端在 ChatResponse.ModelId 上辨識。</summary>
        internal const string MockProviderId = "rimllm";
        internal const string MockModelName = "budget-mock";

        /// <summary>模擬回應寫在 ChatResponse.ModelId 上的複合識別，供上層中介層辨識並繞過。</summary>
        internal const string MockModelId = MockProviderId + ":" + MockModelName;

        private readonly RimLLMUsageTracker _usageTracker;

        public RimLLMBudgetChatClient(IChatClient innerClient, RimLLMUsageTracker usageTracker)
            : base(innerClient)
        {
            _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            EnsureWithinBudget();

            if (TryBuildMock(options, out ChatResponse mock))
            {
                return mock;
            }

            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        public override bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            EnsureWithinBudget();

            if (TryBuildMock(options, out ChatResponse mock))
            {
                return RimLLMUpdateReplay.FromResponse(mock);
            }

            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        private void EnsureWithinBudget()
        {
            // CheckBudgetLimitAsync 沒有任何 await，同步取結果不會阻塞。
            if (!_usageTracker.CheckBudgetLimitAsync().GetAwaiter().GetResult())
            {
                throw new RimLLMException(LLMError.QuotaExceeded, "Daily budget limit exceeded.");
            }
        }

        /// <summary>
        /// 靜默模擬（BudgetPolicy = SilentMocking）的回應。給它明確的供應商／模型識別，
        /// 否則呼叫端只會拿到空的 ModelId，無從分辨「這是模擬回應」與「真的呼叫了
        /// 但供應商沒回傳模型名」。
        /// </summary>
        private bool TryBuildMock(ChatOptions options, out ChatResponse mock)
        {
            mock = null;
            bool hasResponseType = RimLLMChatOptions.GetResponseType(options) != null;
            if (!_usageTracker.IsBudgetMocked(hasResponseType, out string mockText))
            {
                return false;
            }

            mock = new ChatResponse(new ChatMessage(ChatRole.Assistant, mockText))
            {
                ModelId = MockModelId
            };
            return true;
        }
    }
#pragma warning restore S101
}
