using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// Auto Translation 的接管設定：不攔截它的任何方法，只把它的「OpenAI 相容」翻譯器指向框架的本機端點，並選用該翻譯器。
/// </summary>
/// <remarks>
/// 它的 <c>OpenAI Compatible (Local/Other)</c> 翻譯器本來就是「對一個 Base URL 發 OpenAI Chat Completions
/// 請求、不需要金鑰」，正好是本機端點的形狀。全程以名稱反射存取，它缺席或改版時框架 DLL 仍能安全載入。
/// </remarks>
internal static class AutoTranslationCompatPatch
{
    /// <summary>目標 Mod 的 packageId（不含 _steam 等後綴）。</summary>
    public const string PackageId = "seohyeon.autotranslation";

    private const string DisplayName = "Auto Translation";

    /// <summary>該翻譯器在 Auto Translation 內的顯示名，同時是它的設定字典的鍵。</summary>
    private const string TranslatorName = "OpenAI Compatible (Local/Other)";

    /// <summary>Auto Translation 的預設翻譯器（免金鑰），接管關閉時改回它。</summary>
    private const string DefaultTranslatorName = "Google";

    /// <summary>啟動時呼叫：登錄端點處理器，接管已開啟的話一併寫入設定。不需要 Harmony。</summary>
    public static void Apply()
    {
        CompatSeedUtil.RegisterEndpoint(AutoTranslationCompatPatch.PackageId, 2048, 0.3f);
        OnTakeoverToggled(RimLLMFrameworkMod.Settings?.IsCompatTakeoverEnabled(AutoTranslationCompatPatch.PackageId) == true);
    }

    /// <summary>
    /// 玩家切換接管開關或專用模型時呼叫：開啟時把端點位址寫進它的設定，關閉時移除框架寫入的位址。
    /// </summary>
    public static void OnTakeoverToggled(bool enabled)
    {
        if (enabled)
        {
            SeedNativeConfig();
        }
        else
        {
            RevertNativeConfig();
        }
    }

    /// <summary>
    /// 接管關閉時，若翻譯器位址是框架寫的，清空位址並改回它的預設翻譯器（Google），
    /// 避免它繼續對已不服務的本機端點發請求。玩家自己的位址不動。
    /// </summary>
    internal static void RevertNativeConfig()
    {
        try
        {
            Type settingsType = AccessTools.TypeByName("AutoTranslation.Settings");
            Type managerType = AccessTools.TypeByName("AutoTranslation.Services.TranslatorManager");
            FieldInfo translatorNameField = settingsType == null ? null : AccessTools.Field(settingsType, "TranslatorName");
            FieldInfo translatorSettingsField = settingsType == null ? null : AccessTools.Field(settingsType, "TranslatorSettings");
            MethodInfo getTranslator = managerType == null ? null : AccessTools.Method(managerType, "GetTranslator", [typeof(string)]);
            if (translatorNameField == null || translatorSettingsField == null || getTranslator == null)
            {
                return;
            }

            object translator = getTranslator.Invoke(null, [TranslatorName]);
            object config = (translator == null ? null : CompatSeedUtil.GetMember(translator, "Settings"))
                ?? ((IDictionary)translatorSettingsField.GetValue(null))[TranslatorName];
            if (config == null
                || !LocalOpenAIEndpoint.IsOwnBaseUrl(CompatSeedUtil.GetMember(config, "UserCustomBaseURL") as string, AutoTranslationCompatPatch.PackageId))
            {
                return;
            }

            CompatSeedUtil.SetMember(config, "UserCustomBaseURL", string.Empty);
            if (string.Equals(translatorNameField.GetValue(null) as string, TranslatorName, StringComparison.Ordinal))
            {
                translatorNameField.SetValue(null, DefaultTranslatorName);
                object fallback = getTranslator.Invoke(null, [DefaultTranslatorName]);
                if (fallback != null)
                {
                    AccessTools.Field(managerType, "CurrentTranslator")?.SetValue(null, fallback);
                }
            }

            ((Verse.Mod)CompatSeedUtil.FindModInstance("AutoTranslation.AutoTranslation"))?.WriteSettings();
            CompatSeedUtil.LogReverted(DisplayName);
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnRevertFailed(DisplayName, ex);
        }
    }

    /// <summary>
    /// 把它的 OpenAI 相容翻譯器指向本機端點並選用。玩家自己填的位址（例如 Ollama）或已填金鑰的其他翻譯器不覆蓋；
    /// 框架先前寫的位址只更新埠號與模型名，不改它選用的翻譯器。
    /// </summary>
    internal static void SeedNativeConfig()
    {
        try
        {
            string baseUrl = LocalOpenAIEndpoint.GetBaseUrl(AutoTranslationCompatPatch.PackageId);
            if (baseUrl == null)
            {
                Log.Warning($"[RimLLM] Compat: the local endpoint is not available, so {DisplayName} keeps its own settings.");
                return;
            }

            Type settingsType = AccessTools.TypeByName("AutoTranslation.Settings");
            Type managerType = AccessTools.TypeByName("AutoTranslation.Services.TranslatorManager");
            Type aiSettingsType = AccessTools.TypeByName("AutoTranslation.Translators.TranslatorSettings_AIModel");
            FieldInfo translatorNameField = settingsType == null ? null : AccessTools.Field(settingsType, "TranslatorName");
            FieldInfo translatorSettingsField = settingsType == null ? null : AccessTools.Field(settingsType, "TranslatorSettings");
            MethodInfo getTranslator = managerType == null ? null : AccessTools.Method(managerType, "GetTranslator", [typeof(string)]);
            if (translatorNameField == null || translatorSettingsField == null || getTranslator == null || aiSettingsType == null)
            {
                Log.Warning($"[RimLLM] Compat: {DisplayName} settings were not found (the mod may have changed); its settings were not pre-filled.");
                return;
            }

            object translator = getTranslator.Invoke(null, [TranslatorName]);
            if (translator == null)
            {
                Log.Warning($"[RimLLM] Compat: the '{TranslatorName}' translator of {DisplayName} was not found; its settings were not pre-filled.");
                return;
            }

            // 該翻譯器的設定：優先用翻譯器已持有的，否則從設定字典取或新建，並把兩邊接起來。
            var dictionary = (IDictionary)translatorSettingsField.GetValue(null);
            object config = CompatSeedUtil.GetMember(translator, "Settings") ?? dictionary[TranslatorName];
            if (config == null || !aiSettingsType.IsInstanceOfType(config))
            {
                config = Activator.CreateInstance(aiSettingsType);
            }

            string existingUrl = CompatSeedUtil.GetMember(config, "UserCustomBaseURL") as string;
            bool ours = LocalOpenAIEndpoint.IsOwnBaseUrl(existingUrl, AutoTranslationCompatPatch.PackageId);
            if (!string.IsNullOrWhiteSpace(existingUrl) && !ours)
            {
                CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
                return;
            }

            // 玩家目前選用的是填了金鑰、已就緒的其他翻譯器（例如 DeepL），代表有自己的 API 設定，不切走。
            if (!ours && UsesOwnKeyedTranslator(translatorNameField.GetValue(null) as string, getTranslator))
            {
                CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
                return;
            }

            CompatSeedUtil.SetMember(config, "UserCustomBaseURL", baseUrl);
            // 請求的模型欄位不影響路由，每次都寫入目前的專用模型名，設定頁顯示才會跟著玩家的選擇。
            CompatSeedUtil.SetMember(config, "UserSelectedModel", CompatSeedUtil.ModelLabel(AutoTranslationCompatPatch.PackageId));
            if (!ours)
            {
                translatorNameField.SetValue(null, TranslatorName);
            }
            dictionary[TranslatorName] = config;
            CompatSeedUtil.SetMember(translator, "Settings", config);

            if (!ours)
            {
                // 讓翻譯器立即以新設定就緒並成為目前使用的翻譯器，不必重開遊戲。
                AccessTools.Method(translator.GetType(), "Prepare", Type.EmptyTypes)?.Invoke(translator, null);
                FieldInfo current = AccessTools.Field(managerType, "CurrentTranslator");
                current?.SetValue(null, translator);
            }

            ((Verse.Mod)CompatSeedUtil.FindModInstance("AutoTranslation.AutoTranslation"))?.WriteSettings();
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnFailed(DisplayName, ex);
        }
    }

    /// <summary>目前選用的翻譯器是否為需要金鑰且已就緒（玩家已填金鑰）的其他翻譯器。</summary>
    private static bool UsesOwnKeyedTranslator(string currentName, MethodInfo getTranslator)
    {
        if (string.IsNullOrEmpty(currentName) || string.Equals(currentName, TranslatorName, StringComparison.Ordinal))
        {
            return false;
        }

        object current = getTranslator.Invoke(null, [currentName]);
        return current != null
            && CompatSeedUtil.GetMember(current, "RequiresKey") is true
            && CompatSeedUtil.GetMember(current, "Ready") is true;
    }
}
