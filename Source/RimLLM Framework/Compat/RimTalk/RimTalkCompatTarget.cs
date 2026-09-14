using HarmonyLib;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// RimTalk（cj.rimtalk）接管目標。本類別本身不得參考任何 RimTalk 型別——
    /// 那些只能出現在 <see cref="RimTalkCompatPatch"/> 與 <see cref="RimTalkCompatClient"/>，
    /// 否則登錄表在 RimTalk 缺席時光是建構就會觸發型別載入失敗。
    /// </summary>
    internal sealed class RimTalkCompatTarget : RimLLMCompatTarget
    {
        public const string PackageId = "cj.rimtalk";

        public override string ModId => PackageId;

        public override string DisplayName => "RimTalk";

        protected override void ApplyPatch(Harmony harmony)
        {
            RimTalkCompatPatch.Apply(harmony);
        }
    }
}
