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
    internal const string DefaultModelLabel = "RimLLM";

    /// <summary>寫進第三方「API Key」欄位的佔位字串：本機端點不驗證金鑰，只是讓對方的「已設定」檢查通過。</summary>
    internal const string PlaceholderApiKey = "rimllm-local";

    internal static string ModelLabel(string modId)
    {
        string? pin = Mod.RimLLMFrameworkMod.Settings?.GetCompatModelPin(modId);
        return pin != null && ProviderIds.TrySplitModelId(pin, out _, out string? model) ? model : DefaultModelLabel;
    }

    internal static void RegisterEndpoint(string modId, int defaultMaxOutputTokens, float? defaultTemperature = null)
    {
        LocalOpenAIEndpoint.Register(
            modId,
            (payload, chat, ct) => new OpenAICompatClient(modId, defaultMaxOutputTokens, defaultTemperature).CompleteAsync(payload, chat, ct));
    }

    internal static void LogReverted(string displayName)
    {
        Log.Message($"[RimLLM] Compat: takeover is off, so the local endpoint address was removed from {displayName}'s settings. Set up that mod's own API to use it without RimLLM.");
    }

    internal static void WarnRevertFailed(string displayName, Exception ex)
    {
        Log.Warning($"[RimLLM] Compat: could not remove the local endpoint address from {displayName}'s settings. Reason: {ex.GetBaseException().Message}");
    }

    internal static void WarnKeptNativeConfig(string displayName)
    {
        Log.Warning($"[RimLLM] Compat: takeover is on, but {displayName} already has its own API settings, so they were kept and its requests still use them. Clear that mod's API address and key to let RimLLM handle its requests.");
    }

    internal static void WarnFailed(string displayName, Exception ex)
    {
        Log.Warning($"[RimLLM] Compat: could not pre-fill the native settings of {displayName}. Reason: {ex.GetBaseException().Message}");
    }

    internal static object? FindModInstance(string typeName)
    {
        Type type = AccessTools.TypeByName(typeName);
        return type == null
            ? null
            : System.Linq.Enumerable.FirstOrDefault(LoadedModManager.ModHandles, handle => handle.GetType() == type);
    }

    internal static object? GetMember(object target, string name)
    {
        return AccessTools.Field(target.GetType(), name)?.GetValue(target)
            ?? AccessTools.Property(target.GetType(), name)?.GetValue(target);
    }

    internal static void SetMember(object target, string name, object? value)
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
