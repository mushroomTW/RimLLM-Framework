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
/// 請求、不需要金鑰」，正好是本機端點的形狀。
/// </remarks>
internal static class AutoTranslationCompatPatch
{
    public const string PackageId = "seohyeon.autotranslation";

    private const string DisplayName = "Auto Translation";

    /// <summary>該翻譯器在 Auto Translation 內的顯示名，同時是它的設定字典的鍵。</summary>
    private const string TranslatorName = "OpenAI Compatible (Local/Other)";

    private const string DefaultTranslatorName = "Google";

    private const string UserCustomBaseUrlMember = "UserCustomBaseURL";

    public static void Apply()
    {
        CompatSeedUtil.RegisterEndpoint(AutoTranslationCompatPatch.PackageId, 2048, 0.3f);
        OnTakeoverToggled(RimLLMFrameworkMod.Settings?.IsCompatTakeoverEnabled(AutoTranslationCompatPatch.PackageId) == true);
    }

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

    internal static void RevertNativeConfig()
    {
        try
        {
            Type settingsType = AccessTools.TypeByName("AutoTranslation.Settings");
            Type managerType = AccessTools.TypeByName("AutoTranslation.Services.TranslatorManager");
            FieldInfo? translatorNameField = settingsType == null ? null : AccessTools.Field(settingsType, "TranslatorName");
            FieldInfo? translatorSettingsField = settingsType == null ? null : AccessTools.Field(settingsType, "TranslatorSettings");
            MethodInfo? getTranslator = managerType == null ? null : AccessTools.Method(managerType, "GetTranslator", [typeof(string)]);
            if (translatorNameField == null || translatorSettingsField == null || getTranslator == null)
            {
                return;
            }

            object? translator = getTranslator.Invoke(null, [TranslatorName]);
            object? config = (translator == null ? null : CompatSeedUtil.GetMember(translator, "Settings"))
                ?? ((IDictionary?)translatorSettingsField.GetValue(null))?[TranslatorName];
            if (config == null
                || !LocalOpenAIEndpoint.IsOwnBaseUrl(CompatSeedUtil.GetMember(config, UserCustomBaseUrlMember) as string, AutoTranslationCompatPatch.PackageId))
            {
                return;
            }

            CompatSeedUtil.SetMember(config, UserCustomBaseUrlMember, string.Empty);
            if (string.Equals(translatorNameField.GetValue(null) as string, TranslatorName, StringComparison.Ordinal))
            {
                translatorNameField.SetValue(null, DefaultTranslatorName);
                object? fallback = getTranslator.Invoke(null, [DefaultTranslatorName]);
                if (fallback != null)
                {
                    AccessTools.Field(managerType, "CurrentTranslator")?.SetValue(null, fallback);
                }
            }

            ((Verse.Mod?)CompatSeedUtil.FindModInstance("AutoTranslation.AutoTranslation"))?.WriteSettings();
            CompatSeedUtil.LogReverted(DisplayName);
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnRevertFailed(DisplayName, ex);
        }
    }

    internal static void SeedNativeConfig()
    {
        try
        {
            string? baseUrl = LocalOpenAIEndpoint.GetBaseUrl(AutoTranslationCompatPatch.PackageId);
            if (baseUrl == null)
            {
                Log.Warning($"[RimLLM] Compat: the local endpoint is not available, so {DisplayName} keeps its own settings.");
                return;
            }

            if (!TryResolveMembers(out SettingsMembers members))
            {
                Log.Warning($"[RimLLM] Compat: {DisplayName} settings were not found (the mod may have changed); its settings were not pre-filled.");
                return;
            }

            object? translator = members.GetTranslator.Invoke(null, [TranslatorName]);
            if (translator == null)
            {
                Log.Warning($"[RimLLM] Compat: the '{TranslatorName}' translator of {DisplayName} was not found; its settings were not pre-filled.");
                return;
            }

            SeedTranslator(baseUrl, translator, members);
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnFailed(DisplayName, ex);
        }
    }

    private static bool TryResolveMembers(out SettingsMembers members)
    {
        Type settingsType = AccessTools.TypeByName("AutoTranslation.Settings");
        Type managerType = AccessTools.TypeByName("AutoTranslation.Services.TranslatorManager");
        Type aiSettingsType = AccessTools.TypeByName("AutoTranslation.Translators.TranslatorSettings_AIModel");
        FieldInfo? translatorNameField = settingsType == null ? null : AccessTools.Field(settingsType, "TranslatorName");
        FieldInfo? translatorSettingsField = settingsType == null ? null : AccessTools.Field(settingsType, "TranslatorSettings");
        MethodInfo? getTranslator = managerType == null ? null : AccessTools.Method(managerType, "GetTranslator", [typeof(string)]);
        if (managerType == null || translatorNameField == null || translatorSettingsField == null || getTranslator == null || aiSettingsType == null)
        {
            members = default;
            return false;
        }

        members = new SettingsMembers(managerType, translatorNameField, translatorSettingsField, getTranslator, aiSettingsType);
        return true;
    }

    private static void SeedTranslator(string baseUrl, object translator, SettingsMembers members)
    {
        if (members.TranslatorSettings.GetValue(null) is not IDictionary dictionary)
        {
            throw new InvalidOperationException("AutoTranslation 翻譯器設定字典不存在。");
        }

        object? config = CompatSeedUtil.GetMember(translator, "Settings") ?? dictionary[TranslatorName];
        if (config == null || !members.AiSettings.IsInstanceOfType(config))
        {
            config = Activator.CreateInstance(members.AiSettings) ?? throw new InvalidOperationException("無法建立 AI 模型翻譯器設定。");
        }

        string? existingUrl = CompatSeedUtil.GetMember(config, UserCustomBaseUrlMember) as string;
        bool ours = LocalOpenAIEndpoint.IsOwnBaseUrl(existingUrl, AutoTranslationCompatPatch.PackageId);
        if (!string.IsNullOrWhiteSpace(existingUrl) && !ours)
        {
            CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
            return;
        }

        // 玩家目前選用的是填了金鑰、已就緒的其他翻譯器（例如 DeepL），代表有自己的 API 設定，不切走。
        if (!ours && UsesOwnKeyedTranslator(members.TranslatorNameField.GetValue(null) as string, members.GetTranslator))
        {
            CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
            return;
        }

        CompatSeedUtil.SetMember(config, UserCustomBaseUrlMember, baseUrl);
        // 請求的模型欄位不影響路由，每次都寫入目前的專用模型名，設定頁顯示才會跟著玩家的選擇。
        CompatSeedUtil.SetMember(config, "UserSelectedModel", CompatSeedUtil.ModelLabel(AutoTranslationCompatPatch.PackageId));
        if (!ours)
        {
            members.TranslatorNameField.SetValue(null, TranslatorName);
        }
        dictionary[TranslatorName] = config;
        CompatSeedUtil.SetMember(translator, "Settings", config);

        if (!ours)
        {
            AccessTools.Method(translator.GetType(), "Prepare", Type.EmptyTypes)?.Invoke(translator, parameters: null);
            AccessTools.Field(members.ManagerType, "CurrentTranslator")?.SetValue(null, translator);
        }

        ((Verse.Mod?)CompatSeedUtil.FindModInstance("AutoTranslation.AutoTranslation"))?.WriteSettings();
    }

    private readonly struct SettingsMembers
    {
        public SettingsMembers(
            Type managerType,
            FieldInfo translatorName,
            FieldInfo translatorSettings,
            MethodInfo getTranslator,
            Type aiSettings)
        {
            ManagerType = managerType;
            TranslatorNameField = translatorName;
            TranslatorSettings = translatorSettings;
            GetTranslator = getTranslator;
            AiSettings = aiSettings;
        }

        public Type ManagerType { get; }

        public FieldInfo TranslatorNameField { get; }

        public FieldInfo TranslatorSettings { get; }

        public MethodInfo GetTranslator { get; }

        public Type AiSettings { get; }
    }

    private static bool UsesOwnKeyedTranslator(string? currentName, MethodInfo getTranslator)
    {
        if (string.IsNullOrEmpty(currentName) || string.Equals(currentName, TranslatorName, StringComparison.Ordinal))
        {
            return false;
        }

        object? current = getTranslator.Invoke(null, [currentName]);
        return current != null
            && CompatSeedUtil.GetMember(current, "RequiresKey") is true
            && CompatSeedUtil.GetMember(current, "Ready") is true;
    }
}
