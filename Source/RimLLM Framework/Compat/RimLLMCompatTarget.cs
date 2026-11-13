using System;
using RimLLM_Framework.Core;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 一個可被 RimLLM 強制接管 LLM 流量的第三方 Mod。
/// </summary>
/// <remarks>
/// 生命週期：<see cref="RimLLMCompatBootstrap"/> 於所有 Mod 組件載入完成後，對每個目標
/// 先以 <see cref="IsInstalled"/> 偵測，只有已啟用的目標才呼叫 <see cref="TryApply"/> 套用 Harmony 攔截。
/// 攔截本身不看設定開關——開關在 Prefix 內每次請求時讀取，因此玩家在設定頁切換不需重啟遊戲。
///
/// 型別載入防護：各目標的 <c>Apply</c> 方法是唯一允許直接參考目標 Mod
/// 型別的地方，且只能在 <see cref="IsInstalled"/> 為 true 時被 JIT；目標 Mod 缺席時這些方法
/// 從未被呼叫，就不會觸發 FileNotFoundException / TypeLoadException。
/// <b>但這只保護方法本體與實例方法簽章。</b>目標 Mod 的型別不得出現在：(1) 基底類別或介面——RimWorld 載入
/// DLL 時的 <c>Assembly.GetTypes()</c> 會解析，目標 Mod 缺席時整顆框架 DLL 被拒載、主功能一起失效；
/// (2) 任何欄位——DevMode 啟動時 <c>StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes</c>
/// 對每個型別 <c>GetFields()</c>，執行環境會解析全部欄位型別；(3) async 方法的參數／區域變數與 lambda 捕捉的變數
/// ——它們會被提升成狀態機／closure 的欄位，等同 (2)；(4) 靜態方法的簽章（參數與回傳型別）——Debug 動作選單
/// （<c>DebugTabMenu_Actions.InitActions</c>）對所有靜態方法做 <c>IsDefined</c>，執行環境會解析簽章，目標 Mod 缺席時開選單即拋
/// TypeLoadException；Harmony patch 方法請以 <c>object</c>（含 <c>ref object __result</c>）接參數並在本體轉型，
/// 轉換用的輔助方法則做成實例方法。需要「一個目標 Mod 介面的實作」時，改為攔截目標 Mod
/// 自己的實作類別（見 RimTalkCompatPatch 的哨兵做法）；需要 async 時用非 async 薄殼先換成中性型別
/// （見 RimTalkCompatClient）。<c>CompatRimTalkTests.FrameworkAssembly_HasNoRimTalkTypesInBaseInterfacesOrFields</c>
/// 與 <c>CompatAutoTranslationTests.FrameworkAssembly_HasNoCompatTargetTypesInMethodSignatures</c> 鎖住這四條。
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

    /// <summary>目標 Mod 的 packageId（不含 _steam 等後綴），同時作為設定登錄表的鍵與遙測歸屬。</summary>
    public string ModId { get; }

    /// <summary>設定頁顯示用名稱。</summary>
    public string DisplayName { get; }

    /// <summary>目標 Mod 是否在目前的 Mod 清單中啟用。</summary>
    public bool IsInstalled => ModLister.GetActiveModWithIdentifier(ModId, ignorePostfix: true) != null;

    /// <summary>目標 Mod 是否存在於本機（不論是否啟用），用來區分「未安裝」與「已安裝但未啟用」。</summary>
    public bool IsDownloaded => ModLister.GetModWithIdentifier(ModId, ignorePostfix: true) != null;

    /// <summary>玩家是否在設定頁開啟接管。</summary>
    public bool IsEnabled => RimLLMFrameworkMod.Settings != null && RimLLMFrameworkMod.Settings.IsCompatTakeoverEnabled(ModId);

    /// <summary>Harmony 攔截是否已成功套用。為 false 時開關即使打開也不會生效。</summary>
    public bool IsPatched { get; private set; }

    /// <summary>攔截套用失敗的原因（通常是目標 Mod 版本漂移導致找不到方法）。</summary>
    public string PatchError { get; private set; }

    /// <summary>
    /// 套用接管設定，失敗時 fail-soft：記警告、把原因存到 <see cref="PatchError"/>，不讓例外炸到遊戲啟動流程。
    /// 只會在 <see cref="IsInstalled"/> 為 true 時被呼叫。
    /// </summary>
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
            PatchError = ex.Message;
            Log.Warning($"[RimLLM] Compat: failed to patch {DisplayName} ({ModId}); that mod stays on its native path. Reason: {ex.Message}");
        }
    }

    /// <summary>玩家在設定頁切換接管開關時觸發，把狀態同步到目標 Mod 的設定。</summary>
    public void OnTakeoverToggled(bool enabled)
    {
        // 委派會觸及引用目標 Mod 型別的方法，只有目標已安裝且攔截已掛上時才能呼叫（型別載入防線）。
        if (!IsInstalled || !IsPatched) return;
        _onTakeoverToggled(enabled);
    }
}
