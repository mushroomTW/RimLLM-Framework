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
/// 全程以名稱反射存取，RimTalk 缺席或改版時框架 DLL 仍能安全載入。
/// </remarks>
internal static class RimTalkCompatPatch
{
    private const string DisplayName = "RimTalk";

    /// <summary>啟動時呼叫：登錄端點處理器，接管已開啟的話一併寫入設定。不需要 Harmony。</summary>
    public static void Apply(Harmony harmony)
    {
        _ = harmony;
        CompatSeedUtil.RegisterEndpoint(RimTalkCompatTarget.PackageId, 2048);
        OnTakeoverToggled(RimLLMFrameworkMod.Settings?.IsCompatTakeoverEnabled(RimTalkCompatTarget.PackageId) == true);
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
    /// 接管關閉時，若本機設定的位址是框架寫的，清空位址並把模式改回它的預設（簡易模式、雲端清單）。
    /// 框架只在玩家沒有可用設定時才寫入，所以還原不會丟掉玩家自己的設定。
    /// </summary>
    internal static void RevertNativeConfig()
    {
        try
        {
            Type settingsHolder = AccessTools.TypeByName("RimTalk.Settings");
            object settings = settingsHolder == null ? null : AccessTools.Method(settingsHolder, "Get", Type.EmptyTypes)?.Invoke(null, null);
            object local = settings == null ? null : CompatSeedUtil.GetMember(settings, "LocalConfig");
            if (local == null
                || !LocalOpenAIEndpoint.IsOwnBaseUrl(CompatSeedUtil.GetMember(local, "BaseUrl") as string, RimTalkCompatTarget.PackageId))
            {
                return;
            }

            CompatSeedUtil.SetMember(local, "BaseUrl", string.Empty);
            CompatSeedUtil.SetMember(local, "CustomModelName", string.Empty);
            CompatSeedUtil.SetMember(settings, "UseSimpleConfig", true);
            CompatSeedUtil.SetMember(settings, "UseCloudProviders", true);
            AccessTools.Method(settings.GetType(), "Write", Type.EmptyTypes)?.Invoke(settings, null);
            CompatSeedUtil.LogReverted(DisplayName);
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnRevertFailed(DisplayName, ex);
        }
    }

    /// <summary>
    /// 把它的本機設定改成指向本機端點。玩家自己填的本機位址（例如 Ollama）或可用的雲端設定不覆蓋；
    /// 框架先前寫的位址只更新埠號與模型名，不改它的模式。
    /// </summary>
    internal static void SeedNativeConfig()
    {
        try
        {
            string baseUrl = LocalOpenAIEndpoint.GetBaseUrl(RimTalkCompatTarget.PackageId);
            if (baseUrl == null)
            {
                Log.Warning($"[RimLLM] Compat: the local endpoint is not available, so {DisplayName} keeps its own settings.");
                return;
            }

            Type settingsHolder = AccessTools.TypeByName("RimTalk.Settings");
            object settings = settingsHolder == null ? null : AccessTools.Method(settingsHolder, "Get", Type.EmptyTypes)?.Invoke(null, null);
            object local = settings == null ? null : CompatSeedUtil.GetMember(settings, "LocalConfig");
            if (local == null)
            {
                return;
            }

            string existingUrl = CompatSeedUtil.GetMember(local, "BaseUrl") as string;
            bool ours = LocalOpenAIEndpoint.IsOwnBaseUrl(existingUrl, RimTalkCompatTarget.PackageId);
            if (!string.IsNullOrWhiteSpace(existingUrl) && !ours)
            {
                CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
                return;
            }

            // 簡易模式或雲端清單已有可用設定（玩家自己的金鑰），不切走；GetActiveConfig 是它自己判斷「可用」的邏輯。
            object active = AccessTools.Method(settings.GetType(), "GetActiveConfig", Type.EmptyTypes)?.Invoke(settings, null);
            if (active != null && !ReferenceEquals(active, local))
            {
                CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
                return;
            }

            // 它只在路徑是 "/" 或 "/v1" 時才自動補 chat/completions，其餘原樣使用，所以要給完整端點。
            CompatSeedUtil.SetMember(local, "BaseUrl", baseUrl + "/chat/completions");
            // 請求的模型欄位不影響路由，每次都寫入目前的專用模型名，設定頁顯示才會跟著玩家的選擇。
            CompatSeedUtil.SetMember(local, "CustomModelName", CompatSeedUtil.ModelLabel(RimTalkCompatTarget.PackageId));
            if (!ours)
            {
                FieldInfo provider = AccessTools.Field(local.GetType(), "Provider")
                    ?? throw new MissingFieldException(local.GetType().FullName, "Provider");
                provider.SetValue(local, Enum.Parse(provider.FieldType, "Local"));
                CompatSeedUtil.SetMember(local, "IsEnabled", true);
                CompatSeedUtil.SetMember(local, "ApiKey", string.Empty);

                // 切到本機模式：它在簡易模式與雲端清單開啟時不會看 LocalConfig。
                CompatSeedUtil.SetMember(settings, "UseSimpleConfig", false);
                CompatSeedUtil.SetMember(settings, "UseCloudProviders", false);
            }

            AccessTools.Method(settings.GetType(), "Write", Type.EmptyTypes)?.Invoke(settings, null);
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnFailed(DisplayName, ex);
        }
    }
}
