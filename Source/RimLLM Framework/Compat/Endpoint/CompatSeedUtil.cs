using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 把第三方 Mod 的 API 設定指向本機端點時共用的小工具。
/// 這些 Mod 的設定類別多為 <c>internal</c> 或不能在編譯期引用，全部以名稱反射存取；
/// 任何一步找不到都只記警告、不擲例外，不能因為對方改版而影響遊戲啟動。
/// </summary>
internal static class CompatSeedUtil
{
    /// <summary>未指定專用模型時寫進第三方設定頁的模型名。</summary>
    internal const string DefaultModelLabel = "RimLLM";

    /// <summary>寫進第三方「API Key」欄位的佔位字串：本機端點不驗證金鑰，只是讓對方的「已設定」檢查通過。</summary>
    internal const string PlaceholderApiKey = "rimllm-local";

    /// <summary>該 Mod 的專用模型名（取自 "供應商:模型"）；未指定回傳預設標籤。</summary>
    internal static string ModelLabel(string modId)
    {
        string pin = Mod.RimLLMFrameworkMod.Settings?.GetCompatModelPin(modId);
        int colon = pin == null ? -1 : pin.IndexOf(':');
        return colon > 0 && colon < pin.Length - 1 ? pin.Substring(colon + 1) : DefaultModelLabel;
    }

    /// <summary>登錄端點處理器：把該 Mod 送來的 OpenAI 相容請求轉給 RimLLM。</summary>
    /// <param name="modId">該 Mod 的 packageId。</param>
    /// <param name="defaultMaxOutputTokens">請求未指定輸出上限時的預設值，對齊該 Mod 自家請求的用量。</param>
    /// <param name="defaultTemperature">請求未指定溫度時的預設值；null 代表交給供應商預設。</param>
    internal static void RegisterEndpoint(string modId, int defaultMaxOutputTokens, float? defaultTemperature = null)
    {
        LocalOpenAIEndpoint.Register(
            modId,
            (payload, chat, ct) => new OpenAICompatClient(modId, defaultMaxOutputTokens, defaultTemperature).CompleteAsync(payload, chat, ct));
    }

    /// <summary>接管關閉、已把框架寫入的位址還原成該 Mod 預設值時，告知玩家需要自行設定。</summary>
    internal static void LogReverted(string displayName)
    {
        Log.Message($"[RimLLM] Compat: takeover is off, so the local endpoint address was removed from {displayName}'s settings. Set up that mod's own API to use it without RimLLM.");
    }

    internal static void WarnRevertFailed(string displayName, Exception ex)
    {
        Log.Warning($"[RimLLM] Compat: could not remove the local endpoint address from {displayName}'s settings. Reason: {ex.GetBaseException().Message}");
    }

    /// <summary>已有玩家自己的設定時不覆蓋，但要讓玩家知道為什麼接管沒有生效。</summary>
    internal static void WarnKeptNativeConfig(string displayName)
    {
        Log.Warning($"[RimLLM] Compat: takeover is on, but {displayName} already has its own API settings, so they were kept and its requests still use them. Clear that mod's API address and key to let RimLLM handle its requests.");
    }

    internal static void WarnFailed(string displayName, Exception ex)
    {
        Log.Warning($"[RimLLM] Compat: could not pre-fill the native settings of {displayName}. Reason: {ex.GetBaseException().Message}");
    }

    /// <summary>取得目前啟用的 Mod 實例（Verse.Mod 子類）；找不到回傳 null。</summary>
    internal static object FindModInstance(string typeName)
    {
        Type type = AccessTools.TypeByName(typeName);
        if (type == null) return null;
        foreach (Verse.Mod handle in LoadedModManager.ModHandles)
        {
            if (handle.GetType() == type) return handle;
        }
        return null;
    }

    internal static object GetMember(object target, string name)
    {
        return AccessTools.Field(target.GetType(), name)?.GetValue(target)
            ?? AccessTools.Property(target.GetType(), name)?.GetValue(target);
    }

    internal static void SetMember(object target, string name, object value)
    {
        FieldInfo field = AccessTools.Field(target.GetType(), name);
        if (field != null)
        {
            field.SetValue(target, value);
            return;
        }
        PropertyInfo property = AccessTools.Property(target.GetType(), name)
            ?? throw new MissingMemberException(target.GetType().FullName, name);
        property.SetValue(target, value);
    }
}
