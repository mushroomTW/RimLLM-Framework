using System;
using System.Reflection;
using HarmonyLib;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// RimTalk 的接管設定：不攔截它的任何方法，只把它的「本機」供應商設定指向框架的本機端點。
/// </summary>
/// <remarks>
/// RimTalk 的 <c>Local</c> 供應商本來就是「對一個 Base URL 發 OpenAI 相容請求、不需要金鑰」，
/// 正好是本機端點的形狀。接管時把 <c>LocalConfig</c> 指向端點，並切到本機模式
/// （關閉簡易模式與雲端供應商清單）；玩家原本的雲端設定都還在，不會被改動或刪除。
/// </remarks>
internal static class RimTalkCompatPatch
{
    public const string PackageId = "cj.rimtalk";

    private const string DisplayName = "RimTalk";

    private const string BaseUrlMember = "BaseUrl";

    public static void Apply()
    {
        CompatSeedUtil.RegisterEndpoint(RimTalkCompatPatch.PackageId, 2048);
        if (RimLLMFrameworkMod.Settings?.IsCompatTakeoverEnabled(RimTalkCompatPatch.PackageId) == true)
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

    internal static void RevertNativeConfig()
    {
        try
        {
            Type settingsHolder = AccessTools.TypeByName("RimTalk.Settings");
            object? settings = settingsHolder == null ? null : AccessTools.Method(settingsHolder, "Get", Type.EmptyTypes)?.Invoke(null, parameters: null);
            object? local = settings == null ? null : CompatSeedUtil.GetMember(settings, "LocalConfig");
            if (settings == null || local == null
                || !LocalOpenAIEndpoint.IsOwnBaseUrl(CompatSeedUtil.GetMember(local, BaseUrlMember) as string, RimTalkCompatPatch.PackageId))
            {
                return;
            }

            CompatSeedUtil.SetMember(local, BaseUrlMember, string.Empty);
            CompatSeedUtil.SetMember(local, "CustomModelName", string.Empty);
            CompatSeedUtil.SetMember(settings, "UseSimpleConfig", value: true);
            CompatSeedUtil.SetMember(settings, "UseCloudProviders", value: true);
            AccessTools.Method(settings.GetType(), "Write", Type.EmptyTypes)?.Invoke(settings, parameters: null);
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
        string baseUrl = LocalOpenAIEndpoint.GetBaseUrl(RimTalkCompatPatch.PackageId)
            ?? throw new InvalidOperationException($"The local endpoint is not available for {DisplayName}.");

        Type settingsHolder = AccessTools.TypeByName("RimTalk.Settings")
            ?? throw new InvalidOperationException($"{DisplayName} settings type was not found.");
        object settings = AccessTools.Method(settingsHolder, "Get", Type.EmptyTypes)?.Invoke(null, parameters: null)
            ?? throw new InvalidOperationException($"{DisplayName} settings instance was not found.");
        object local = CompatSeedUtil.GetMember(settings, "LocalConfig")
            ?? throw new InvalidOperationException($"{DisplayName} LocalConfig was not found.");

        string? existingUrl = CompatSeedUtil.GetMember(local, "BaseUrl") as string;
        bool ours = LocalOpenAIEndpoint.IsOwnBaseUrl(existingUrl, RimTalkCompatPatch.PackageId);
        if (!string.IsNullOrWhiteSpace(existingUrl) && !ours)
        {
            CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
            return;
        }

        // 簡易模式或雲端清單已有可用設定（玩家自己的金鑰），不切走；GetActiveConfig 是它自己判斷「可用」的邏輯。
        object? active = AccessTools.Method(settings.GetType(), "GetActiveConfig", Type.EmptyTypes)?.Invoke(settings, parameters: null);
        if (active != null && !ReferenceEquals(active, local))
        {
            CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
            return;
        }

        // 它只在路徑是 "/" 或 "/v1" 時才自動補 chat/completions，其餘原樣使用，所以要給完整端點。
        CompatSeedUtil.SetMember(local, "BaseUrl", baseUrl + "/chat/completions");
        // 請求的模型欄位不影響路由，每次都寫入目前的專用模型名，設定頁顯示才會跟著玩家的選擇。
        CompatSeedUtil.SetMember(local, "CustomModelName", CompatSeedUtil.ModelLabel(RimTalkCompatPatch.PackageId));
        if (!ours)
        {
            FieldInfo provider = AccessTools.Field(local.GetType(), "Provider")
                ?? throw new MissingFieldException(local.GetType().FullName, "Provider");
            provider.SetValue(local, Enum.Parse(provider.FieldType, "Local"));
            CompatSeedUtil.SetMember(local, "IsEnabled", value: true);
            CompatSeedUtil.SetMember(local, "ApiKey", string.Empty);

            // 切到本機模式：它在簡易模式與雲端清單開啟時不會看 LocalConfig。
            CompatSeedUtil.SetMember(settings, "UseSimpleConfig", value: false);
            CompatSeedUtil.SetMember(settings, "UseCloudProviders", value: false);
        }

        AccessTools.Method(settings.GetType(), "Write", Type.EmptyTypes)?.Invoke(settings, parameters: null);
    }
}
