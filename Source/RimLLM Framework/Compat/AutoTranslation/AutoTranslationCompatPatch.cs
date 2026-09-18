using System;
using System.Reflection;
using AutoTranslation;
using AutoTranslation.Services;
using AutoTranslation.Translators;
using HarmonyLib;
using RimLLM_Framework.Api;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// 純 Harmony 接管 Auto Translation 的翻譯流量，框架本身不實作任何 Auto Translation 介面。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 核心設計：
    /// <list type="number">
    /// <item>以 Auto Translation 原生具備批次與佔位符防護的 <see cref="Translator_OpenAICompatible"/> 建構哨兵實例（<c>_sentinel</c>）。</item>
    /// <item>攔截 <c>Translator_OpenAICompatible.GetResponseUnsafe</c>：當 <c>__instance</c> 為哨兵時改由
    /// <see cref="AutoTranslationCompatClient"/> 呼叫 RimLLM，其餘原生實例一律放行。</item>
    /// <item>攔截 <c>TranslatorManager.Translate</c>：在任何文本入列前以 <see cref="SyncCurrentTranslator"/>
    /// 確保當前翻譯器與接管開關狀態完全同步；若接管開啟且線程尚未啟動，則自動喚醒。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 型別載入防線：Auto Translation 型別不得出現在類別繼承、介面或欄位型別（欄位刻意使用 <see cref="object"/>），
    /// 僅允許於方法簽章與本體中出現，保證在未安裝 Auto Translation 時框架 DLL 仍能安全載入。
    /// </para>
    /// </remarks>
    internal static class AutoTranslationCompatPatch
    {
        private static AutoTranslationCompatClient _client;

        /// <summary>哨兵 translator 實例，欄位型別刻意用 object 隔絕型別載入依賴。</summary>
        private static object _sentinel;

        /// <summary>在接管前或接管停用時的原生 translator 實例。</summary>
        private static object _nativeTranslator;

        private static bool _cachedTakeOver;
        private static DateTime _cachedAt = DateTime.MinValue;
        private static readonly TimeSpan TakeOverCacheTtl = TimeSpan.FromSeconds(1);

        private static DateTime _lastOfflineWarning = DateTime.MinValue;
        private static readonly TimeSpan OfflineWarningInterval = TimeSpan.FromMinutes(1);

        internal static AutoTranslationCompatClient Client
        {
            get => _client ?? (_client = new AutoTranslationCompatClient());
            set => _client = value;
        }

        internal static object Sentinel
        {
            get => _sentinel;
            set => _sentinel = value;
        }

        internal static object NativeTranslator
        {
            get => _nativeTranslator;
            set => _nativeTranslator = value;
        }

        public static void Apply(Harmony harmony)
        {
            MethodInfo getResponse = AccessTools.Method(
                typeof(Translator_OpenAICompatible),
                "GetResponseUnsafe",
                new[] { typeof(string), typeof(string) });
            if (getResponse == null)
            {
                throw new MissingMethodException("AutoTranslation.Translators.Translator_OpenAICompatible.GetResponseUnsafe 不存在（Auto Translation 版本可能已變動）。");
            }

            MethodInfo translate = AccessTools.Method(
                typeof(TranslatorManager),
                nameof(TranslatorManager.Translate),
                new[] { typeof(string), typeof(string), typeof(string), typeof(Action<string>) });
            if (translate == null)
            {
                throw new MissingMethodException("AutoTranslation.Services.TranslatorManager.Translate 不存在（Auto Translation 版本可能已變動）。");
            }

            // 建構哨兵實例：啟用批次翻譯與 RimLLM 模型標識
            var sentinel = new Translator_OpenAICompatible
            {
                Settings = new TranslatorSettings_AIModel
                {
                    UserSelectedModel = "RimLLM",
                    EnableBatchTranslation = true,
                    BatchSizeTokens = 2000,
                    RequestTimeoutSeconds = 60
                }
            };
            sentinel.Prepare();
            sentinel.Ready = true;

            _sentinel = sentinel;

            harmony.Patch(getResponse, prefix: new HarmonyMethod(typeof(AutoTranslationCompatPatch), nameof(GetResponseUnsafePrefix)));
            harmony.Patch(translate, prefix: new HarmonyMethod(typeof(AutoTranslationCompatPatch), nameof(TranslatePrefix)));

            // 啟動階段初始同步
            SyncCurrentTranslator();
        }

        /// <summary>
        /// 文本入列翻譯隊列時觸發，確保 CurrentTranslator 狀態最新。
        /// </summary>
        public static void TranslatePrefix()
        {
            SyncCurrentTranslator();
        }

        /// <summary>
        /// 攔截哨兵實例的底層連線請求，改走 RimLLM 管道。
        /// </summary>
        public static bool GetResponseUnsafePrefix(
            Translator_OpenAICompatible __instance,
            string text,
            string prompt,
            ref string __result)
        {
            if (!ReferenceEquals(__instance, _sentinel))
            {
                return true;
            }

            if (!ShouldTakeOver())
            {
                SyncCurrentTranslator();
                return true;
            }

            __result = Client.GetResponseUnsafe(text, prompt);
            return false;
        }

        /// <summary>
        /// 玩家在設定分頁切換接管開關時即時呼叫。
        /// </summary>
        public static void OnTakeoverToggled(bool enabled)
        {
            _cachedTakeOver = enabled;
            _cachedAt = DateTime.UtcNow;
            SyncCurrentTranslator();
        }

        /// <summary>
        /// 同步 Auto Translation 的 CurrentTranslator 實例與線程狀態。
        /// </summary>
        internal static void SyncCurrentTranslator()
        {
            if (_sentinel == null) return;

            if (ShouldTakeOver())
            {
                if (!ReferenceEquals(TranslatorManager.CurrentTranslator, _sentinel))
                {
                    _nativeTranslator = TranslatorManager.CurrentTranslator;
                    TranslatorManager.CurrentTranslator = (ITranslator)_sentinel;
                    TranslatorManager.Ready = true;

                    if (!TranslatorManager.Working)
                    {
                        TranslatorManager.StartThread();
                    }
                }
            }
            else
            {
                if (ReferenceEquals(TranslatorManager.CurrentTranslator, _sentinel))
                {
                    ITranslator native = _nativeTranslator as ITranslator
                                         ?? TranslatorManager.GetTranslator(global::AutoTranslation.Settings.TranslatorName);
                    TranslatorManager.CurrentTranslator = native;
                    TranslatorManager.Ready = native != null;
                }
            }
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
            return settings != null && settings.IsCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId);
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
                    Log.Warning($"[RimLLM] 相容層：Auto Translation 接管已開啟，但 RimLLM 目前沒有可用的供應商，本次退回 Auto Translation 原生路徑。原因：{ex.Message}");
                }
                return false;
            }
        }
    }
}
