using HarmonyLib;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// Mod 兼容性檢查器（modcompatchecker.main）接管目標。本類別本身不得參考任何 ModCompatChecker 型別——
    /// 那些只能出現在 <see cref="ModCompatCheckerCompatPatch"/> 與 <see cref="ModCompatCheckerCompatClient"/>，
    /// 否則登錄表在 ModCompatChecker 缺席時光是建構就會觸發型別載入失敗。
    /// </summary>
    internal sealed class ModCompatCheckerCompatTarget : RimLLMCompatTarget
    {
        public const string PackageId = "modcompatchecker.main";

        public override string ModId => PackageId;

        public override string DisplayName => "Mod 兼容性檢查器 (Mod Compatibility Checker)";

        protected override void ApplyPatch(Harmony harmony)
        {
            ModCompatCheckerCompatPatch.Apply(harmony);
        }
    }
}
