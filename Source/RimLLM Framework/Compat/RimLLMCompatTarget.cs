using System;
using HarmonyLib;
using RimLLM_Framework.Core;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 一個可被 RimLLM 強制接管 LLM 流量的第三方 Mod。
    /// </summary>
    /// <remarks>
    /// 生命週期：<see cref="RimLLMCompatBootstrap"/> 於所有 Mod 組件載入完成後，對每個目標
    /// 先以 <see cref="IsInstalled"/> 偵測，只有已啟用的目標才呼叫 <see cref="TryApply"/> 套用 Harmony 攔截。
    /// 攔截本身不看設定開關——開關在 Prefix 內每次請求時讀取，因此玩家在設定頁切換不需重啟遊戲。
    ///
    /// 型別載入防護：<see cref="ApplyPatch"/> 與 Harmony patch 類別是唯一允許直接參考目標 Mod
    /// 型別的地方，且只能在 <see cref="IsInstalled"/> 為 true 時被 JIT；目標 Mod 缺席時這些方法
    /// 從未被呼叫，就不會觸發 FileNotFoundException / TypeLoadException。
    /// </remarks>
    internal abstract class RimLLMCompatTarget
    {
        /// <summary>目標 Mod 的 packageId（不含 _steam 等後綴），同時作為設定登錄表的鍵與遙測歸屬。</summary>
        public abstract string ModId { get; }

        /// <summary>設定頁顯示用名稱。</summary>
        public abstract string DisplayName { get; }

        /// <summary>目標 Mod 是否在目前的 Mod 清單中啟用。</summary>
        public bool IsInstalled => ModLister.GetActiveModWithIdentifier(ModId, ignorePostfix: true) != null;

        /// <summary>玩家是否在設定頁開啟接管。</summary>
        public bool IsEnabled => RimLLMFrameworkMod.Settings != null && RimLLMFrameworkMod.Settings.IsCompatTakeoverEnabled(ModId);

        /// <summary>Harmony 攔截是否已成功套用。為 false 時開關即使打開也不會生效。</summary>
        public bool IsPatched { get; private set; }

        /// <summary>攔截套用失敗的原因（通常是目標 Mod 版本漂移導致找不到方法）。</summary>
        public string PatchError { get; private set; }

        /// <summary>套用 Harmony 攔截。只會在 <see cref="IsInstalled"/> 為 true 時被呼叫。</summary>
        protected abstract void ApplyPatch(Harmony harmony);

        /// <summary>
        /// 套用攔截，失敗時 fail-soft：記警告、把原因存到 <see cref="PatchError"/>，不讓例外炸到遊戲啟動流程。
        /// </summary>
        public void TryApply(Harmony harmony)
        {
            try
            {
                ApplyPatch(harmony);
                IsPatched = true;
                PatchError = null;
                RimLLMLog.Message($"[RimLLM] 相容層：已掛載 {DisplayName} ({ModId}) 攔截。");
            }
            catch (Exception ex)
            {
                IsPatched = false;
                PatchError = ex.Message;
                Log.Warning($"[RimLLM] 相容層：掛載 {DisplayName} ({ModId}) 攔截失敗，該 Mod 將維持原生路徑。原因：{ex.Message}");
            }
        }
    }
#pragma warning restore S101
}
