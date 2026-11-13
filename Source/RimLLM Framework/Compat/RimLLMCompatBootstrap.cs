using System;
using System.Collections.Generic;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 第三方相容層登錄表與啟動掛載點。
/// </summary>
/// <remarks>
/// 以 <see cref="StaticConstructorOnStartup"/> 觸發而非在 Mod 建構子裡做，因為 Mod 建構子執行時
/// 其他 Mod 的組件不一定已載入；StaticConstructorOnStartup 保證所有 Mod 組件都已就緒，
/// 載入順序因此不影響攔截是否成功。
///
/// 新增一個目標 Mod 只需：在 Targets 清單加入一個 RimLLMCompatTarget，指定 PackageId、顯示名稱與 Apply 委派。
/// </remarks>
[StaticConstructorOnStartup]
internal static class RimLLMCompatBootstrap
{
    /// <summary>所有已知的接管目標（依設定頁顯示順序）。</summary>
    public static readonly IReadOnlyList<RimLLMCompatTarget> Targets = new RimLLMCompatTarget[]
    {
        new RimLLMCompatTarget(
            RimTalkCompatPatch.PackageId,
            "RimTalk",
            RimTalkCompatPatch.Apply,
            RimTalkCompatPatch.OnTakeoverToggled),
        new RimLLMCompatTarget(
            AutoTranslationCompatPatch.PackageId,
            "Auto Translation",
            AutoTranslationCompatPatch.Apply,
            AutoTranslationCompatPatch.OnTakeoverToggled),
        new RimLLMCompatTarget(
            ScAutoCompatPatch.PackageId,
            "One 簡中自動化 (Simplified Chinese Automation: One)",
            ScAutoCompatPatch.Apply,
            ScAutoCompatPatch.OnTakeoverToggled),
        new RimLLMCompatTarget(
            ModCompatCheckerCompatPatch.PackageId,
            "Mod 兼容性檢查器 (Mod Compatibility Checker)",
            ModCompatCheckerCompatPatch.Apply,
            ModCompatCheckerCompatPatch.OnTakeoverToggled)
    };

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
            // 啟動期意外：相容層整體停用，主功能不受影響。
            Log.Warning($"[RimLLM] Compat layer failed to initialize; third-party takeover is disabled: {ex.Message}");
        }
    }
}
