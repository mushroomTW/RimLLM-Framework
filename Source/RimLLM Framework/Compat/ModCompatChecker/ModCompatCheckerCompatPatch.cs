using System;
using System.Collections;
using HarmonyLib;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// Mod 兼容性檢查器的接管設定：不攔截它的任何方法，只把它的「自訂模型」API 設定指向框架的本機端點。
/// </summary>
/// <remarks>
/// 它的 AI 診斷是對自訂端點發 OpenAI Chat Completions 請求，所以端點指向
/// <see cref="LocalOpenAIEndpoint"/> 後，所有 AI 功能自然走框架。設定欄位都是 public。
/// </remarks>
internal static class ModCompatCheckerCompatPatch
{
    public const string PackageId = "modcompatchecker.main";

    private const string DisplayName = "Mod Compatibility Checker";

    private const string ApiEndpointMember = "APIEndpoint";
    private const string ApiKeyMember = "APIKey";

    public static void Apply()
    {
        CompatSeedUtil.RegisterEndpoint(ModCompatCheckerCompatPatch.PackageId, 1500, 0.3f);
        if (RimLLMFrameworkMod.Settings?.IsCompatTakeoverEnabled(ModCompatCheckerCompatPatch.PackageId) == true)
        {
            SeedNativeConfigCore();
        }
        else
        {
            RevertNativeConfig();
        }
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

    private const string DefaultEndpoint = "https://api.deepseek.com/v1/chat/completions";

    internal static void RevertNativeConfig()
    {
        try
        {
            object? mod = CompatSeedUtil.FindModInstance("ModCompatChecker.ModCompatMod");
            object? settings = mod == null ? null : CompatSeedUtil.GetMember(mod, "Settings");
            if (settings == null
                || !LocalOpenAIEndpoint.IsOwnBaseUrl(CompatSeedUtil.GetMember(settings, ApiEndpointMember) as string, ModCompatCheckerCompatPatch.PackageId))
            {
                return;
            }

            CompatSeedUtil.SetMember(settings, ApiEndpointMember, DefaultEndpoint);
            if (CompatSeedUtil.GetMember(settings, ApiKeyMember) as string == CompatSeedUtil.PlaceholderApiKey)
            {
                CompatSeedUtil.SetMember(settings, ApiKeyMember, string.Empty);
            }
            CompatSeedUtil.SetMember(settings, "CustomModelId", string.Empty);
            CompatSeedUtil.SetMember(settings, "SelectedModelIndex", 0);
            ((Verse.Mod?)mod)?.WriteSettings();
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
            SeedNativeConfigCore();
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnFailed(DisplayName, ex);
        }
    }

    private static void SeedNativeConfigCore()
    {
        string baseUrl = LocalOpenAIEndpoint.GetBaseUrl(ModCompatCheckerCompatPatch.PackageId)
            ?? throw new InvalidOperationException($"The local endpoint is not available for {DisplayName}.");

        object mod = CompatSeedUtil.FindModInstance("ModCompatChecker.ModCompatMod")
            ?? throw new InvalidOperationException($"{DisplayName} mod instance was not found.");
        object settings = CompatSeedUtil.GetMember(mod, "Settings")
            ?? throw new InvalidOperationException($"{DisplayName} Settings was not found.");

        string? endpoint = CompatSeedUtil.GetMember(settings, "APIEndpoint") as string;
        string? key = CompatSeedUtil.GetMember(settings, "APIKey") as string;
        bool ours = LocalOpenAIEndpoint.IsOwnBaseUrl(endpoint, ModCompatCheckerCompatPatch.PackageId);
        if (!string.IsNullOrEmpty(key) && !ours)
        {
            CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
            return;
        }

        CompatSeedUtil.SetMember(settings, "APIEndpoint", baseUrl + "/chat/completions");
        // 請求的模型欄位不影響路由，每次都寫入目前的專用模型名，設定頁顯示才會跟著玩家的選擇。
        CompatSeedUtil.SetMember(settings, "CustomModelId", CompatSeedUtil.ModelLabel(ModCompatCheckerCompatPatch.PackageId));
        if (!ours)
        {
            // 首次寫入才改金鑰與模型選擇；之後只更新埠號與模型名。
            // 「自訂模型」的索引等於內建模型清單的長度（IsCustomModel 以此判定）。
            Type modelConfig = AccessTools.TypeByName("ModCompatChecker.AI.ModelConfig");
            object? models = modelConfig == null ? null : AccessTools.Property(modelConfig, "Models")?.GetValue(null);
            if (models is not ICollection collection)
            {
                throw new MissingMemberException("ModCompatChecker.AI.ModelConfig", "Models");
            }

            CompatSeedUtil.SetMember(settings, "APIKey", CompatSeedUtil.PlaceholderApiKey);
            CompatSeedUtil.SetMember(settings, "SelectedModelIndex", collection.Count);
        }

        ((Verse.Mod)mod).WriteSettings();
    }
}
