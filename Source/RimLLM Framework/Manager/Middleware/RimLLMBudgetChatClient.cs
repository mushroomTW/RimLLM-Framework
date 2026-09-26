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
    /// 每日預算保護。超出上限時依 BudgetPolicy 擋下請求或只留下警告後放行。
    /// </summary>
    /// <remarks>
    /// 排在防濫用之後、佇列之前：被預算擋下的請求不該佔用併發名額。
    /// </remarks>
    internal sealed class RimLLMBudgetChatClient : DelegatingChatClient
    {
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

            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        public override bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            EnsureWithinBudget();

            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        private void EnsureWithinBudget()
        {
            if (!_usageTracker.CheckBudgetLimit())
            {
                throw new RimLLMException(LLMError.QuotaExceeded, "Daily budget limit exceeded.");
            }
        }
    }
#pragma warning restore S101
}
