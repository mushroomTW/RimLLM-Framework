extern alias bclasync;
extern alias ste;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 備援路由：依設定的 fallback chain 逐一嘗試「供應商 + 模型」候選，
    /// 並把成敗回報給健康帳本與用量日誌。
    /// </summary>
    /// <remarks>
    /// 建構在 MEAI 的 <see cref="FailoverChatClient"/> 之上：基底類別負責呼叫、串流
    /// 承諾狀態與嘗試上限，本類別只負責「下一個試誰」與「試完之後記什麼」。
    ///
    /// 與先前自製迴圈的一個行為差異：MEAI 規定「串流一旦把內容吐給呼叫端，就不再
    /// 切換供應商」，因此串流中途換供應商的能力不復存在，錯誤會直接上拋。非串流路徑
    /// 的備援行為不變。
    /// </remarks>
    internal sealed class RimLLMFailoverChatClient : FailoverChatClient
    {
        private readonly IRimLLMSettings _settings;
        private readonly RimLLMHealthLedger _healthLedger;
        private readonly RimLLMUsageTracker _usageTracker;
        private readonly RimLLMFallbackPipeline _policy;
        private readonly string _modId;

        /// <summary>
        /// 標記本次請求是串流。RoutingContext 不帶這個資訊，而候選用盡時要擲出的錯誤碼
        /// 取決於串流與否，因此以框架私有鍵夾在 ChatOptions 上傳遞。
        /// </summary>
        private const string StreamingKey = "rimllm_streaming";

        /// <summary>
        /// 每個請求的選擇進度。RoutingContext 由基底類別逐請求建立且不帶使用者狀態，
        /// 因此以它作為鍵掛在弱參考表上，請求結束後交給 GC 回收。
        /// </summary>
        private readonly ConditionalWeakTable<RoutingContext, RequestState> _states =
            new ConditionalWeakTable<RoutingContext, RequestState>();

        private sealed class RequestState
        {
            public List<RimLLMFallbackPipeline.ResolvedCandidate> Candidates;
            public int Index = -1;
            public int AttemptOnCurrent;
            public bool SawRetryableFailureOnCurrent;
            public Exception LastException;
            public DateTime StartTime = DateTime.Now;
            public Stopwatch Total = Stopwatch.StartNew();

            public RimLLMFallbackPipeline.ResolvedCandidate Current => Candidates[Index];
        }

        public RimLLMFailoverChatClient(
            IRimLLMSettings settings,
            RimLLMHealthLedger healthLedger,
            RimLLMUsageTracker usageTracker,
            RimLLMFallbackPipeline policy,
            string modId)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _healthLedger = healthLedger ?? throw new ArgumentNullException(nameof(healthLedger));
            _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _modId = modId;

            // 終止由候選是否用盡決定，不另設嘗試上限。
            MaximumAttemptsPerRequest = null;
        }

        /// <summary>
        /// 組出備援路由。外面固定再包一層 <see cref="StreamingMarkerChatClient"/>：
        /// 候選用盡時要擲出的錯誤碼取決於串流與否，而 RoutingContext 不帶這個資訊、
        /// FailoverChatClient 又把兩個入口都 sealed 了，只能在進入路由之前先標記。
        /// </summary>
        public static IChatClient Create(
            IRimLLMSettings settings,
            RimLLMHealthLedger healthLedger,
            RimLLMUsageTracker usageTracker,
            RimLLMFallbackPipeline policy,
            string modId)
        {
            return new StreamingMarkerChatClient(
                new RimLLMFailoverChatClient(settings, healthLedger, usageTracker, policy, modId));
        }

        /// <summary>
        /// 在請求進入路由之前標記「這是串流」。必須緊貼著 router：擺到回應快取之外的話，
        /// 這個鍵會進入快取鍵的計算，讓同一個請求的串流與非串流版本各存一份。
        /// </summary>
        private sealed class StreamingMarkerChatClient : DelegatingChatClient
        {
            public StreamingMarkerChatClient(IChatClient innerClient)
                : base(innerClient)
            {
            }

            public override bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                ChatOptions marked = options?.Clone() ?? new ChatOptions();
                if (marked.AdditionalProperties == null)
                {
                    marked.AdditionalProperties = new AdditionalPropertiesDictionary();
                }
                marked.AdditionalProperties[StreamingKey] = true;
                return base.GetStreamingResponseAsync(messages, marked, cancellationToken);
            }
        }

        protected override async ste::System.Threading.Tasks.ValueTask<IChatClient> SelectClientAsync(
            RoutingContext context,
            CancellationToken cancellationToken)
        {
            RequestState state = _states.GetValue(context, _ => new RequestState());

            if (state.Candidates == null)
            {
                state.Candidates = _policy.ResolveCandidates(
                    context.ChatOptions?.ModelId,
                    RimLLMChatOptions.GetMinFallbackLevel(context.ChatOptions));
                state.Index = 0;
            }
            else
            {
                // 只有在上一次嘗試失敗後基底類別才會再次選擇，因此走到這裡代表要決定
                // 「重試同一個候選」還是「換下一個候選」。
                await AdvanceAsync(state, cancellationToken).ConfigureAwait(false);
            }

            if (state.Index >= state.Candidates.Count)
            {
                Exhausted(state, context.ChatOptions);
            }

            var candidate = state.Current;
            // 呼叫端包裝：關閉詳細日誌時連內插字串都不配置，輸出與原本（不輸出）完全一致。
            if (RimLLMLog.Enabled)
            {
                RimLLMLog.Message(state.AttemptOnCurrent > 0
                    ? $"[RimLLM] Attempting to call provider: {candidate.ProviderId} (Model: {candidate.ModelName}), retrying attempt {state.AttemptOnCurrent + 1}..."
                    : $"[RimLLM] Attempting to call provider: {candidate.ProviderId} (Model: {candidate.ModelName})");
            }

            return new RimLLMProviderChatClient(candidate.Provider, candidate.ModelName, _settings);
        }

        /// <summary>
        /// 決定重試或換手，並在重試前套用退避延遲。
        /// </summary>
        private async Task AdvanceAsync(RequestState state, CancellationToken cancellationToken)
        {
            var candidate = state.Current;
            bool retryable = RimLLMFallbackPipeline.IsRetryableException(state.LastException);
            int maxRetries = _settings.MaxRetries;

            if (retryable && state.AttemptOnCurrent < maxRetries)
            {
                float delay = RimLLMFallbackPipeline.ResolveRetryDelay(
                    _settings.RetryDelay, state.AttemptOnCurrent, state.LastException);

                if (RimLLMLog.Enabled)
                {
                    RimLLMLog.Warning($"[RimLLM] Provider {candidate.ProviderId} (Model: {candidate.ModelName}) call failed: {RimLLMLog.SanitizeForLog(state.LastException?.Message, 300)}. Retrying in {delay:F1} seconds...");
                }
                if (delay > 0f)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
                }

                state.AttemptOnCurrent++;
                return;
            }

            if (!retryable)
            {
                if (RimLLMLog.Enabled)
                {
                    RimLLMLog.Warning($"[RimLLM] Provider {candidate.ProviderId} (Model: {candidate.ModelName}) returned a non-retryable error: {RimLLMLog.SanitizeForLog(state.LastException?.Message, 300)}. Fallbacking to the next entry.");
                    // 非重試類錯誤多半是請求組裝或 SDK 層的問題，只有訊息無從定位；
                    // 詳細日誌開啟時一併輸出完整例外鏈與堆疊。
                    RimLLMLog.Message($"[RimLLM] Non-retryable error detail:\n{RimLLMLog.SanitizeForLog(state.LastException?.ToString(), 4000)}");
                }
            }
            else
            {
                if (RimLLMLog.Enabled)
                {
                    RimLLMLog.Warning($"[RimLLM] Provider {candidate.ProviderId} (Model: {candidate.ModelName}) reached maximum retries ({maxRetries}). Fallbacking to the next entry.");
                }
            }

            // 換手之前結算這個候選的健康度。一次請求對同一個候選的所有重試合計只記一次
            // 失敗：逐次記錄會讓單一次網路抖動就把連續失敗數推過熔斷門檻
            //（預設重試 3 次即記 4 次失敗），把健康的目標冤枉冷卻數分鐘。
            if (state.SawRetryableFailureOnCurrent)
            {
                _healthLedger.RecordFailure(RimLLMFallbackPipeline.HealthKey(candidate), isRetryable: true);
            }

            state.Index++;
            state.AttemptOnCurrent = 0;
            state.SawRetryableFailureOnCurrent = false;
        }

        /// <summary>
        /// 候選用盡。選擇失敗不會觸發 OnRoutingUpdateAsync，因此收尾日誌必須在這裡寫。
        /// </summary>
        private void Exhausted(RequestState state, ChatOptions options)
        {
            // 最後一個候選的失敗已由 AdvanceAsync 在換手前結算，這裡只需寫收尾日誌。
            state.Total.Stop();
            _usageTracker.RecordLog(
                state.StartTime, _modId, "FallbackChain", "None", false,
                state.LastException?.Message ?? "All fallbacks failed", state.Total.ElapsedMilliseconds);

            // 串流沿用舊管線的 ProviderOffline：下游是以 LLMError 分支顯示離線提示的。
            bool streaming = RimLLMChatOptions.ReadAdditional(options, StreamingKey, false);
            throw new RimLLMException(
                streaming ? LLMError.ProviderOffline : LLMError.Unknown,
                streaming
                    ? $"All fallback attempts failed, unable to establish stream connection. Last error: {state.LastException?.Message}"
                    : $"All fallback attempts failed. Last error: {state.LastException?.Message}",
                state.LastException);
        }

        protected override ste::System.Threading.Tasks.ValueTask OnRoutingUpdateAsync(
            RoutingContext context,
            FailoverChatClientAttempt attempt,
            bool isTerminal,
            CancellationToken cancellationToken)
        {
            if (!_states.TryGetValue(context, out RequestState state) || state.Candidates == null)
            {
                return default(ste::System.Threading.Tasks.ValueTask);
            }

            // 這個回呼擲出的例外會取代請求本身已經產生的回應或錯誤，
            // 因此帳本與日誌的失敗絕不能往外冒。
            try
            {
                var candidate = state.Current;
                string healthKey = RimLLMFallbackPipeline.HealthKey(candidate);
                long elapsedMs = (long)attempt.Duration.TotalMilliseconds;

                if (attempt.Exception == null)
                {
                    // Exception 為 null 不等於成功：呼叫端提早停止列舉串流時，Exception 與
                    // ResponseCompleted 都不會被設定。把那種情況記成成功，會讓一個正在熔斷
                    // 冷卻中的目標被 RecordSuccess 清掉連續失敗數而立刻復活。
                    if (attempt.ResponseCompleted)
                    {
                        _healthLedger.RecordSuccess(healthKey, elapsedMs);
                        _usageTracker.RecordLog(
                            state.StartTime, _modId, candidate.ProviderId, candidate.ModelName, true, null, elapsedMs);
                    }
                }
                else
                {
                    state.LastException = attempt.Exception;
                    state.SawRetryableFailureOnCurrent |=
                        RimLLMFallbackPipeline.IsRetryableException(attempt.Exception);

                    // 終結更新代表基底類別不會再選下一個候選（例如串流已吐出內容），
                    // 此時 SelectClientAsync 不會再被呼叫，結算只能在這裡做。
                    if (isTerminal && state.SawRetryableFailureOnCurrent)
                    {
                        _healthLedger.RecordFailure(healthKey, isRetryable: true);
                        state.SawRetryableFailureOnCurrent = false;
                    }
                }
            }
            catch (Exception ex)
            {
                if (RimLLMLog.Enabled)
                {
                    RimLLMLog.Warning($"[RimLLM] Failed to record routing outcome: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
                }
            }

            return default(ste::System.Threading.Tasks.ValueTask);
        }

        public override object GetService(Type serviceType, object serviceKey = null)
        {
            if (serviceType == typeof(ChatClientMetadata))
            {
                return new ChatClientMetadata("RimLLM", null, null);
            }
            // 讓上層無論包了幾層 DelegatingChatClient，都還能認出這是框架的堆疊。
            if (serviceType == typeof(RimLLMFailoverChatClient))
            {
                return this;
            }
            return base.GetService(serviceType, serviceKey);
        }
    }
#pragma warning restore S101
}
