using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using RimLLM_Framework.Mod;
using RimTalk;
using RimTalk.Client;
using Verse;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// 攔截 RimTalk 的唯一傳輸 choke point：<c>AIClientFactory.GetAIClientAsync</c>。
    /// RimTalk 的串流對話（ChatStreaming）與結構化查詢（Query&lt;T&gt;）都經由
    /// <c>AIService.ExecuteWithRetry</c> 呼叫它取得 <see cref="IAIClient"/>，
    /// 因此只要在這裡換成 <see cref="RimTalkCompatClient"/>，RimTalk 全部 LLM 流量
    /// （含 OpenAI／Gemini／Custom／Local／Player2）都會改走 RimLLM 的 fallback 鏈。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 目標方法是 <c>async</c>：Harmony 攔的是產生狀態機的 stub，所以 Prefix 攔下時
    /// 必須把 <c>__result</c> 設成已完成的 <see cref="Task{TResult}"/>。
    /// Prefix 完全繞過 RimTalk 自己的 client 快取，因此轉接器單例由這裡自行持有。
    /// </para>
    /// <para>
    /// 第二個攔截點 <c>RimTalkSettings.GetActiveConfig</c>：RimTalk 在 <c>TalkService</c> 與
    /// <c>TickManagerPatch</c> 裡以「回傳 null」判定未設定 API，會直接不發起對話並提示缺金鑰——
    /// 這道門檻在 <c>GetAIClientAsync</c> 之前。接管生效時若玩家從未設定 RimTalk，就補一個合成的
    /// 設定讓門檻通過；玩家自己有設定則原樣保留（模型名稱只用於 RimTalk 的提示訊息）。
    /// </para>
    /// </remarks>
    internal static class RimTalkCompatPatch
    {
        /// <summary>合成設定用的端點與模型名稱；只會出現在 RimTalk 自己的提示訊息與設定雜湊裡。</summary>
        private const string SyntheticEndpoint = "rimllm://" + RimTalkCompatTarget.PackageId;
        private const string SyntheticModel = "RimLLM";

        private static RimTalkCompatClient _client;
        private static DateTime _lastOfflineWarning = DateTime.MinValue;
        private static readonly TimeSpan OfflineWarningInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// 判定結果的短暫快取。RimTalk 的 TickManagerPatch 每個 tick 都會呼叫 GetActiveConfig，
        /// 沒有這層的話 Postfix 每 tick 都要跑一次候選解析（含 lock 與配置），備援鏈為空時還每 tick 擲一次例外。
        /// 一秒的延遲對「開關即時生效」在體感上沒有差別。
        /// </summary>
        private static bool _cachedTakeOver;
        private static DateTime _cachedAt = DateTime.MinValue;
        private static readonly TimeSpan TakeOverCacheTtl = TimeSpan.FromSeconds(1);

        public static void Apply(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(AIClientFactory), nameof(AIClientFactory.GetAIClientAsync));
            if (target == null)
            {
                throw new MissingMethodException("RimTalk.Client.AIClientFactory.GetAIClientAsync 不存在（RimTalk 版本可能已變動）。");
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(RimTalkCompatPatch), nameof(GetAIClientAsyncPrefix)));

            MethodInfo activeConfig = AccessTools.Method(typeof(RimTalkSettings), nameof(RimTalkSettings.GetActiveConfig));
            if (activeConfig == null)
            {
                throw new MissingMethodException("RimTalk.RimTalkSettings.GetActiveConfig 不存在（RimTalk 版本可能已變動）。");
            }
            harmony.Patch(activeConfig, postfix: new HarmonyMethod(typeof(RimTalkCompatPatch), nameof(GetActiveConfigPostfix)));
        }

        /// <summary>
        /// 玩家沒有任何有效的 RimTalk API 設定時，接管生效期間補一個合成設定，讓 RimTalk 願意發起對話。
        /// 只在原生結果為 null 時介入；Provider 設為 Local 是因為它是唯一不要求金鑰的類型，
        /// 而 BaseUrl 永遠不會被真的連線——請求在 <see cref="GetAIClientAsyncPrefix"/> 就被接走了。
        /// </summary>
        public static void GetActiveConfigPostfix(ref ApiConfig __result)
        {
            if (__result != null || !ShouldTakeOver()) return;

            __result = new ApiConfig
            {
                IsEnabled = true,
                Provider = AIProvider.Local,
                BaseUrl = SyntheticEndpoint,
                SelectedModel = SyntheticModel
            };
        }

        /// <summary>回傳 false 代表攔下並以 RimLLM 轉接器取代；true 放行 RimTalk 原生路徑。</summary>
        public static bool GetAIClientAsyncPrefix(ref Task<IAIClient> __result)
        {
            // TalkService 是用 GetActiveConfig()!=null（可能是 Postfix 補的合成設定）放行對話，
            // 走到這裡時若供應商剛好全部不可用而判定翻轉，原生路徑會因設定為 null 而回傳 null client，
            // RimTalk 接著對 null 呼叫方法。開關仍開著時這種情況交給轉接器，讓它以清楚的 ProviderOffline 錯誤結束；
            // 開關已被關掉則一律放行，尊重玩家的選擇。
            if (!ShouldTakeOver() && (!IsToggleEnabled() || Settings.Get()?.GetActiveConfig() != null)) return true;

            __result = Task.FromResult<IAIClient>(_client ?? (_client = new RimTalkCompatClient()));
            return false;
        }

        /// <summary>
        /// 每次請求時決定是否接管：設定開關必須開啟，且 RimLLM 當下要有至少一個可用候選。
        /// 後者是 Player2／離線使用者誤開開關的降級保護——fallback 鏈沒設定或全部不合格時
        /// 放行原生路徑，而不是讓 RimTalk 收到一連串 ProviderOffline 錯誤。
        /// </summary>
        internal static bool ShouldTakeOver()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _cachedAt < TakeOverCacheTtl) return _cachedTakeOver;

            _cachedTakeOver = EvaluateTakeOver();
            _cachedAt = now;
            return _cachedTakeOver;
        }

        private static bool IsToggleEnabled()
        {
            RimLLMFrameworkSettings settings = RimLLMFrameworkMod.Settings;
            return settings != null && settings.IsCompatTakeoverEnabled(RimTalkCompatTarget.PackageId);
        }

        private static bool EvaluateTakeOver()
        {
            if (!IsToggleEnabled())
            {
                return false;
            }

            try
            {
                RimLLMProvider.GetEffectiveCapabilities();
                return true;
            }
            catch (RimLLMException ex)
            {
                // RimTalk 對話頻率高，警告限一分鐘一次，避免洗版。
                // 刻意不走 RimLLMLog（受 DetailedLogging 開關遮蔽）：這是玩家唯一能得知接管被降級的線索。
                DateTime now = DateTime.UtcNow;
                if (now - _lastOfflineWarning >= OfflineWarningInterval)
                {
                    _lastOfflineWarning = now;
                    Log.Warning($"[RimLLM] 相容層：RimTalk 接管已開啟，但 RimLLM 目前沒有可用的供應商，本次退回 RimTalk 原生路徑。原因：{ex.Message}");
                }
                return false;
            }
        }
    }
}
