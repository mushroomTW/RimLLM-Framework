using HarmonyLib;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// Auto Translation（seohyeon.autotranslation）接管目標。本類別本身不得參考任何 Auto Translation 型別——
    /// 那些只能出現在 <see cref="AutoTranslationCompatPatch"/> 與 <see cref="AutoTranslationCompatClient"/>，
    /// 否則登錄表在 Auto Translation 缺席時光是建構就會觸發型別載入失敗。
    /// </summary>
    internal sealed class AutoTranslationCompatTarget : RimLLMCompatTarget
    {
        public const string PackageId = "seohyeon.autotranslation";

        public override string ModId => PackageId;

        public override string DisplayName => "Auto Translation";

        protected override void ApplyPatch(Harmony harmony)
        {
            AutoTranslationCompatPatch.Apply(harmony);
        }

        public override void OnTakeoverToggled(bool enabled)
        {
            if (IsInstalled && IsPatched)
            {
                AutoTranslationCompatPatch.OnTakeoverToggled(enabled);
            }
        }
    }
}
