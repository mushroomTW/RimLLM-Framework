using System;
using System.Reflection;
using HarmonyLib;
using ModCompatChecker.AI;
using ModCompatChecker.Core;
using RimLLM_Framework.Api;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// 純 Harmony 接管 Mod 兼容性檢查器的 AI 診斷流量，框架本身不實作任何 ModCompatChecker 介面。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 核心設計：
    /// <list type="number">
    /// <item>攔截 <c>AIService.CallAPIWithTimeout</c>（唯一的傳輸出口）：接管開啟且 RimLLM 有可用供應商時，
    /// 改由 <see cref="ModCompatCheckerCompatClient"/> 呼叫 RimLLM 並將結果填入 <c>__result</c>。</item>
    /// <item>攔截 <c>ModCompatSettings.IsAIConfigured</c>：接管開啟時強制回傳 <c>true</c>，
    /// 讓 UI 上的 AI 分析按鈕即使未填原生金鑰也能啟用。</item>
    /// <item>攔截 <c>ApiBalanceChecker.CheckBalance</c>：接管開啟時短路跳過，
    /// 避免未填原生金鑰時發出 401 錯誤。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 型別載入防線：ModCompatChecker 型別不得出現在類別繼承、介面或欄位型別（欄位刻意使用 <see cref="object"/>），
    /// 僅允許於方法簽章與本體中出現，保證在未安裝 ModCompatChecker 時框架 DLL 仍能安全載入。
    /// </para>
    /// </remarks>
    internal static class ModCompatCheckerCompatPatch
    {
        private static ModCompatCheckerCompatClient _client;

        private static bool _cachedTakeOver;
        private static DateTime _cachedAt = DateTime.MinValue;
        private static readonly TimeSpan TakeOverCacheTtl = TimeSpan.FromSeconds(1);

        private static DateTime _lastOfflineWarning = DateTime.MinValue;
        private static readonly TimeSpan OfflineWarningInterval = TimeSpan.FromMinutes(1);

        internal static ModCompatCheckerCompatClient Client
        {
            get => _client ?? (_client = new ModCompatCheckerCompatClient());
            set => _client = value;
        }

        public static void Apply(Harmony harmony)
        {
            // 1. 攔截 AIService.CallAPIWithTimeout——所有 AI 請求的唯一出口
            MethodInfo callAPI = AccessTools.Method(
                typeof(AIService),
                nameof(AIService.CallAPIWithTimeout));
            if (callAPI == null)
            {
                throw new MissingMethodException("ModCompatChecker.AI.AIService.CallAPIWithTimeout 不存在（ModCompatChecker 版本可能已變動）。");
            }

            // 2. 攔截 ModCompatSettings.IsAIConfigured——讓接管開啟時無需填原生金鑰
            Type settingsType = AccessTools.TypeByName("ModCompatChecker.ModCompatSettings");
            MethodInfo isConfigured = settingsType != null
                ? AccessTools.Method(settingsType, "IsAIConfigured")
                : null;

            // 3. 攔截 ApiBalanceChecker.CheckBalance——接管開啟時短路跳過
            MethodInfo checkBalance = AccessTools.Method(
                typeof(ApiBalanceChecker),
                nameof(ApiBalanceChecker.CheckBalance));

            harmony.Patch(callAPI,
                prefix: new HarmonyMethod(typeof(ModCompatCheckerCompatPatch), nameof(CallAPIWithTimeoutPrefix)));

            if (isConfigured != null)
            {
                harmony.Patch(isConfigured,
                    postfix: new HarmonyMethod(typeof(ModCompatCheckerCompatPatch), nameof(IsAIConfiguredPostfix)));
            }

            if (checkBalance != null)
            {
                harmony.Patch(checkBalance,
                    prefix: new HarmonyMethod(typeof(ModCompatCheckerCompatPatch), nameof(CheckBalancePrefix)));
            }
        }

        /// <summary>
        /// 攔截 AIService.CallAPIWithTimeout：接管開啟時由 RimLLM 處理並填入結果。
        /// </summary>
        public static bool CallAPIWithTimeoutPrefix(
            string endpoint,
            string apiKey,
            string modelId,
            string userMessage,
            ModelConfig.ApiProvider provider,
            int timeoutSeconds,
            ref bool cancelFlag,
            ref string __result)
        {
            if (!ShouldTakeOver())
            {
                return true; // 放行原生
            }

            try
            {
                __result = Client.CallAPI(userMessage, timeoutSeconds, ref cancelFlag);
                return false; // 跳過原生
            }
            catch (Exception ex)
            {
                // 呼叫失敗時退回原生路徑
                DateTime now = DateTime.UtcNow;
                if (now - _lastOfflineWarning >= OfflineWarningInterval)
                {
                    _lastOfflineWarning = now;
                    Log.Warning($"[RimLLM] 相容層：ModCompatChecker 接管請求失敗，退回原生路徑。原因：{ex.Message}");
                }
                return true;
            }
        }

        /// <summary>
        /// 接管開啟且 RimLLM 有可用供應商時，強制 IsAIConfigured 回傳 true。
        /// </summary>
        public static void IsAIConfiguredPostfix(ref bool __result)
        {
            if (!__result && ShouldTakeOver())
            {
                __result = true;
            }
        }

        /// <summary>
        /// 接管開啟時短路 ApiBalanceChecker.CheckBalance，避免無原生金鑰時 401 錯誤。
        /// </summary>
        public static bool CheckBalancePrefix()
        {
            return !ShouldTakeOver();
        }

        /// <summary>
        /// 每次請求時判定是否接管：設定開關開啟且 RimLLM 當下有可用供應商。
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
            return settings != null && settings.IsCompatTakeoverEnabled(ModCompatCheckerCompatTarget.PackageId);
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
                DateTime now = DateTime.UtcNow;
                if (now - _lastOfflineWarning >= OfflineWarningInterval)
                {
                    _lastOfflineWarning = now;
                    Log.Warning($"[RimLLM] 相容層：ModCompatChecker 接管已開啟，但 RimLLM 目前沒有可用的供應商，本次退回 ModCompatChecker 原生路徑。原因：{ex.Message}");
                }
                return false;
            }
        }
    }
}
