using System;
using System.Collections.Generic;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 第三方相容層登錄表與啟動掛載點。以 <see cref="StaticConstructorOnStartup"/> 觸發而非在 Mod 建構子裡做，
/// 因為 Mod 建構子執行時其他 Mod 的組件不一定已載入，載入順序因此不影響攔截是否成功。
/// </summary>
[StaticConstructorOnStartup]
internal static class RimLLMCompatBootstrap
{
    public static readonly IReadOnlyList<RimLLMCompatTarget> Targets =
    [
        new(
            RimTalkCompatPatch.PackageId,
            "RimTalk",
            RimTalkCompatPatch.Apply,
            RimTalkCompatPatch.OnTakeoverToggled),
        new(
            AutoTranslationCompatPatch.PackageId,
            "Auto Translation",
            AutoTranslationCompatPatch.Apply,
            AutoTranslationCompatPatch.OnTakeoverToggled),
        new(
            ScAutoCompatPatch.PackageId,
            "One 簡中自動化 (Simplified Chinese Automation: One)",
            ScAutoCompatPatch.Apply,
            ScAutoCompatPatch.OnTakeoverToggled),
        new(
            ModCompatCheckerCompatPatch.PackageId,
            "Mod 兼容性檢查器 (Mod Compatibility Checker)",
            ModCompatCheckerCompatPatch.Apply,
            ModCompatCheckerCompatPatch.OnTakeoverToggled)
    ];

    static RimLLMCompatBootstrap()
    {
        try
        {
            foreach (RimLLMCompatTarget target in Targets)
            {
                if (target.IsInstalled)
                {
                    target.TryApply();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[RimLLM] Compat layer failed to initialize; third-party takeover is disabled: {ex.Message}");
        }
    }
}
