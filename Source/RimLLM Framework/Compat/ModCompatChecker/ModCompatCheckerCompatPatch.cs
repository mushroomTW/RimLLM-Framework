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
/// <see cref="LocalOpenAIEndpoint"/> 後，所有 AI 功能自然走框架。設定欄位都是 public，
/// 但為了在它缺席或改版時框架 DLL 仍能安全載入，一律以名稱反射存取，不在編譯期引用它。
/// </remarks>
internal static class ModCompatCheckerCompatPatch
{
    private const string DisplayName = "Mod Compatibility Checker";

    /// <summary>啟動時呼叫：登錄端點處理器，接管已開啟的話一併寫入設定。不需要 Harmony。</summary>
    public static void Apply(Harmony harmony)
    {
        _ = harmony;
        CompatSeedUtil.RegisterEndpoint(ModCompatCheckerCompatTarget.PackageId, 1500, 0.3f);
        OnTakeoverToggled(RimLLMFrameworkMod.Settings?.IsCompatTakeoverEnabled(ModCompatCheckerCompatTarget.PackageId) == true);
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

    /// <summary>它的預設端點（與其設定類別的欄位預設值一致）。</summary>
    private const string DefaultEndpoint = "https://api.deepseek.com/v1/chat/completions";

    /// <summary>
    /// 接管關閉時，若端點是框架寫的，把端點、佔位金鑰與模型選擇改回它的預設值。
    /// 框架只在玩家沒填金鑰時才寫入，所以還原不會丟掉玩家自己的設定。
    /// </summary>
    internal static void RevertNativeConfig()
    {
        try
        {
            object mod = CompatSeedUtil.FindModInstance("ModCompatChecker.ModCompatMod");
            object settings = mod == null ? null : CompatSeedUtil.GetMember(mod, "Settings");
            if (settings == null
                || !LocalOpenAIEndpoint.IsOwnBaseUrl(CompatSeedUtil.GetMember(settings, "APIEndpoint") as string, ModCompatCheckerCompatTarget.PackageId))
            {
                return;
            }

            CompatSeedUtil.SetMember(settings, "APIEndpoint", DefaultEndpoint);
            if (CompatSeedUtil.GetMember(settings, "APIKey") as string == CompatSeedUtil.PlaceholderApiKey)
            {
                CompatSeedUtil.SetMember(settings, "APIKey", string.Empty);
            }
            CompatSeedUtil.SetMember(settings, "CustomModelId", string.Empty);
            CompatSeedUtil.SetMember(settings, "SelectedModelIndex", 0);
            ((Verse.Mod)mod).WriteSettings();
            CompatSeedUtil.LogReverted(DisplayName);
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnRevertFailed(DisplayName, ex);
        }
    }

    /// <summary>
    /// 把它的設定改成指向本機端點。只改「從未設定（金鑰為空）」或「框架自己先前寫的」位址；
    /// 玩家自己填的真實端點與金鑰一律不動。
    /// </summary>
    internal static void SeedNativeConfig()
    {
        try
        {
            string baseUrl = LocalOpenAIEndpoint.GetBaseUrl(ModCompatCheckerCompatTarget.PackageId);
            if (baseUrl == null)
            {
                Log.Warning($"[RimLLM] Compat: the local endpoint is not available, so {DisplayName} keeps its own settings.");
                return;
            }

            object mod = CompatSeedUtil.FindModInstance("ModCompatChecker.ModCompatMod");
            object settings = mod == null ? null : CompatSeedUtil.GetMember(mod, "Settings");
            if (settings == null)
            {
                return;
            }

            string endpoint = CompatSeedUtil.GetMember(settings, "APIEndpoint") as string;
            string key = CompatSeedUtil.GetMember(settings, "APIKey") as string;
            bool ours = LocalOpenAIEndpoint.IsOwnBaseUrl(endpoint, ModCompatCheckerCompatTarget.PackageId);
            if (!string.IsNullOrEmpty(key) && !ours)
            {
                CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
                return;
            }

            CompatSeedUtil.SetMember(settings, "APIEndpoint", baseUrl + "/chat/completions");
            // 請求的模型欄位不影響路由，每次都寫入目前的專用模型名，設定頁顯示才會跟著玩家的選擇。
            CompatSeedUtil.SetMember(settings, "CustomModelId", CompatSeedUtil.ModelLabel(ModCompatCheckerCompatTarget.PackageId));
            if (!ours)
            {
                // 首次寫入才改金鑰與模型選擇；之後只更新埠號與模型名。
                // 「自訂模型」的索引等於內建模型清單的長度（IsCustomModel 以此判定）。
                Type modelConfig = AccessTools.TypeByName("ModCompatChecker.AI.ModelConfig");
                object models = modelConfig == null ? null : AccessTools.Property(modelConfig, "Models")?.GetValue(null);
                if (models is not ICollection collection)
                {
                    throw new MissingMemberException("ModCompatChecker.AI.ModelConfig", "Models");
                }

                CompatSeedUtil.SetMember(settings, "APIKey", CompatSeedUtil.PlaceholderApiKey);
                CompatSeedUtil.SetMember(settings, "SelectedModelIndex", collection.Count);
            }

            ((Verse.Mod)mod).WriteSettings();
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnFailed(DisplayName, ex);
        }
    }
}
