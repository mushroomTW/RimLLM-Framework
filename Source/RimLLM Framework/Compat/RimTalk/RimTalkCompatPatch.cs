using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using RimLLM_Framework.Mod;
using RimTalk;
using RimTalk.Client;
using RimTalk.Client.OpenAI;
using RimTalk.Data;
using Verse;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// 純 Harmony 接管 RimTalk 的 LLM 流量，框架本身不實作任何 RimTalk 介面。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三個攔截點：
    /// <list type="number">
    /// <item><c>AIClientFactory.GetAIClientAsync</c>（唯一的 client 取得點）：接管生效時回傳一個由本類別持有的
    /// RimTalk 原生 <see cref="OpenAIClient"/> 哨兵實例。RimTalk 的串流對話（ChatStreaming）與結構化查詢
    /// （Query&lt;T&gt;）都經由 <c>AIService.ExecuteWithRetry</c> 取得 client，因此 OpenAI／Gemini／Custom／
    /// Local／Player2 全部流量都會落到哨兵上。</item>
    /// <item><see cref="OpenAIClient"/> 的兩個非泛型漏斗 <c>GetChatCompletionAsync(prefix, messages, imageBase64, onRequestPrepared)</c>
    /// 與私有 <c>StreamAsync(…, onChunk, onRequestPrepared)</c>：<c>__instance</c> 是哨兵時改呼叫
    /// <see cref="RimTalkCompatClient"/>，不是哨兵（玩家自己的原生 client）則原樣放行。RimTalk 的泛型
    /// <c>GetStreamingChatCompletionAsync&lt;T&gt;</c> 與三參數 <c>GetChatCompletionAsync</c> 都是這兩個方法的包裝，
    /// 不必碰泛型方法的 Harmony 攔截。</item>
    /// </list>
    /// 為什麼不直接讓 <see cref="RimTalkCompatClient"/> 實作 <see cref="IAIClient"/>：那是型別定義層級的參考，
    /// RimWorld 載入 DLL 時的 <c>Assembly.GetTypes()</c> 會在 RimTalk 缺席時整顆拒載框架。哨兵是 RimTalk 自己的型別，
    /// 建構它只是方法本體裡的一次 <c>newobj</c>，只在 RimTalk 存在時才會被 JIT。
    /// </para>
    /// <para>
    /// 目標方法都是 <c>async</c>：Harmony 攔的是產生狀態機的 stub，所以 Prefix 攔下時
    /// 必須把 <c>__result</c> 設成一個 <see cref="Task{TResult}"/>。
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

        /// <summary>
        /// 交給 RimTalk 的哨兵 client（RimTalk 原生 <see cref="OpenAIClient"/>）。欄位型別刻意用 <see cref="object"/>，
        /// 讓這個類別的欄位描述完全不含 RimTalk 型別。只以參考相等判定，永遠不會真的對它發請求。
        /// </summary>
        private static object _sentinel;

        /// <summary>轉接器；測試可注入帶假 <c>IChatClient</c> 的實例。</summary>
        internal static RimTalkCompatClient Client
        {
            get => _client ?? (_client = new RimTalkCompatClient());
            set => _client = value;
        }
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
            // 先把全部目標方法與哨兵都準備好，確認無誤才開始 Patch：
            // 任何一步失敗都不能留下「GetAIClientAsync 已被攔、哨兵卻是 null」的半掛載狀態，否則 RimTalk 會拿到 null client。
            MethodInfo target = AccessTools.Method(typeof(AIClientFactory), nameof(AIClientFactory.GetAIClientAsync));
            if (target == null)
            {
                throw new MissingMethodException("RimTalk.Client.AIClientFactory.GetAIClientAsync 不存在（RimTalk 版本可能已變動）。");
            }

            MethodInfo activeConfig = AccessTools.Method(typeof(RimTalkSettings), nameof(RimTalkSettings.GetActiveConfig));
            if (activeConfig == null)
            {
                throw new MissingMethodException("RimTalk.RimTalkSettings.GetActiveConfig 不存在（RimTalk 版本可能已變動）。");
            }

            Type messages = typeof(List<(Role role, string message)>);
            MethodInfo completion = AccessTools.Method(typeof(OpenAIClient), nameof(OpenAIClient.GetChatCompletionAsync),
                new[] { messages, messages, typeof(string), typeof(Action<Payload>) });
            if (completion == null)
            {
                throw new MissingMethodException("RimTalk.Client.OpenAI.OpenAIClient.GetChatCompletionAsync(prefix, messages, imageBase64, onRequestPrepared) 不存在（RimTalk 版本可能已變動）。");
            }

            MethodInfo stream = AccessTools.Method(typeof(OpenAIClient), "StreamAsync",
                new[] { messages, messages, typeof(string), typeof(Action<string>), typeof(Action<Payload>) });
            if (stream == null)
            {
                throw new MissingMethodException("RimTalk.Client.OpenAI.OpenAIClient.StreamAsync 不存在（RimTalk 版本可能已變動）。");
            }

            // 哨兵在掛載階段就建好：RimTalk 建構子簽章若漂移，MissingMethodException 會落在 TryApply 裡、顯示在設定頁，
            // 而不是等到第一次對話才炸。baseUrl 給 null 讓 OpenAIClient 跳過 Uri 解析。
            object sentinel = new OpenAIClient(null, SyntheticModel, null, null, null, AIProvider.Local);

            // 漏斗先掛、入口最後掛：GetAIClientAsync 的 Prefix 一旦生效就會交出哨兵，此時兩個漏斗必須已經就位。
            harmony.Patch(completion, prefix: new HarmonyMethod(typeof(RimTalkCompatPatch), nameof(GetChatCompletionAsyncPrefix)));
            harmony.Patch(stream, prefix: new HarmonyMethod(typeof(RimTalkCompatPatch), nameof(StreamAsyncPrefix)));
            harmony.Patch(activeConfig, postfix: new HarmonyMethod(typeof(RimTalkCompatPatch), nameof(GetActiveConfigPostfix)));
            _sentinel = sentinel;
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(RimTalkCompatPatch), nameof(GetAIClientAsyncPrefix)));
        }

        /// <summary>目前的哨兵。setter 僅供測試不經 Harmony 掛載就驗證路由。</summary>
        internal static object Sentinel
        {
            get => _sentinel;
            set => _sentinel = value;
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
            // 沒有哨兵（掛載未完成）時寧可放行原生路徑，也不能交出 null client。
            if (_sentinel == null) return true;

            __result = Task.FromResult((IAIClient)_sentinel);
            return false;
        }

        /// <summary>非串流漏斗：哨兵實例改走 RimLLM，其他實例放行。</summary>
        public static bool GetChatCompletionAsyncPrefix(
            OpenAIClient __instance,
            List<(Role role, string message)> prefixMessages,
            List<(Role role, string message)> messages,
            string imageBase64,
            Action<Payload> onRequestPrepared,
            ref Task<Payload> __result)
        {
            if (!ReferenceEquals(__instance, _sentinel)) return true;

            __result = Client.GetChatCompletionAsync(prefixMessages, messages, imageBase64, onRequestPrepared);
            return false;
        }

        /// <summary>串流漏斗：哨兵實例改走 RimLLM，文字塊交回 RimTalk 自己的 JSONL parser。</summary>
        public static bool StreamAsyncPrefix(
            OpenAIClient __instance,
            List<(Role role, string message)> prefixMessages,
            List<(Role role, string message)> messages,
            string imageBase64,
            Action<string> onChunk,
            Action<Payload> onRequestPrepared,
            ref Task<Payload> __result)
        {
            if (!ReferenceEquals(__instance, _sentinel)) return true;

            __result = Client.StreamAsync(prefixMessages, messages, imageBase64, onChunk, onRequestPrepared);
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

            // 高頻輪詢走非拋版查詢：離線時不再每秒配置例外與堆疊，警告內容與節流維持不變。
            if (RimLLMProvider.TryGetEffectiveCapabilities(out _, out string failureReason))
            {
                return true;
            }

            // RimTalk 對話頻率高，警告限一分鐘一次，避免洗版。
            // 刻意不走 RimLLMLog（受 DetailedLogging 開關遮蔽）：這是玩家唯一能得知接管被降級的線索。
            DateTime now = DateTime.UtcNow;
            if (now - _lastOfflineWarning >= OfflineWarningInterval)
            {
                _lastOfflineWarning = now;
                Log.Warning($"[RimLLM] 相容層：RimTalk 接管已開啟，但 RimLLM 目前沒有可用的供應商，本次退回 RimTalk 原生路徑。原因：{failureReason}");
            }
            return false;
        }
    }
}
