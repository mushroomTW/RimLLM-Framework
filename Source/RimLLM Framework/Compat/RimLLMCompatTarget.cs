using System;
using RimLLM_Framework.Core;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 一個可被 RimLLM 接管 LLM 流量的第三方 Mod。
/// </summary>
/// <remarks>
/// 型別載入防護：各目標已全面改由 <see cref="LocalOpenAIEndpoint"/> 與設定反射串接，不再於方法簽章或欄位引用目標 Mod 型別，
/// 但仍保持只透過名稱反射存取目標 Mod，避免目標 Mod 缺席時觸發 FileNotFoundException / TypeLoadException。
/// </remarks>
internal sealed class RimLLMCompatTarget
{
    private readonly Action _apply;
    private readonly Action<bool> _onTakeoverToggled;

    public RimLLMCompatTarget(string modId, string displayName, Action apply, Action<bool> onTakeoverToggled)
    {
        ModId = modId;
        DisplayName = displayName;
        _apply = apply;
        _onTakeoverToggled = onTakeoverToggled;
    }

    public string ModId { get; }

    public string DisplayName { get; }

    public bool IsInstalled => ModLister.GetActiveModWithIdentifier(ModId, ignorePostfix: true) != null;

    public bool IsDownloaded => ModLister.GetModWithIdentifier(ModId, ignorePostfix: true) != null;

    public bool IsEnabled => RimLLMFrameworkMod.Settings != null && RimLLMFrameworkMod.Settings.IsCompatTakeoverEnabled(ModId);

    public bool IsPatched { get; private set; }

    public string? PatchError { get; private set; }

    public void TryApply()
    {
        try
        {
            _apply();
            IsPatched = true;
            PatchError = null;
            RimLLMLog.Message($"[RimLLM] Compat: hooked {DisplayName} ({ModId}).");
        }
        catch (Exception ex)
        {
            IsPatched = false;
            PatchError = RimLLMLog.SanitizeForLog(ex.Message);
            RimLLMLog.Warning($"[RimLLM] Compat: failed to patch {DisplayName} ({ModId}); that mod stays on its native path. Reason: {PatchError}");
        }
    }

    public void OnTakeoverToggled(bool enabled)
    {
        // 委派會觸及引用目標 Mod 型別的方法，只有目標已安裝且攔截已掛上時才能呼叫（型別載入防線）。
        if (!IsInstalled || !IsPatched) return;
        _onTakeoverToggled(enabled);
    }
}
