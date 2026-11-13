using System.Collections.Generic;
using System.Linq;
using DevTools.Testing;
using RimLLM_Framework.Compat;

namespace RimLLM_Framework.InGameTests;

/// <summary>
/// 相容層的各目標是否在玩家實際安裝的 Mod 上初始化成功（登錄端點處理器、寫入設定）。
/// 目標 Mod 未啟用時記錄 skipped；要涵蓋全部目標，需以 cj.rimtalk、seohyeon.autotranslation、
/// modcompatchecker.main、ow1r.scauto 為伴隨 Mod 再跑一次。
/// </summary>
[TestFixture(TestType.MainMenu)]
internal sealed class CompatPatchTests
{
    [Test]
    public void InstalledTargetsReportPatched()
    {
        if (!RimLLMCompatBootstrap.Targets.Any(t => t.IsInstalled))
        {
            Test.Skip("No compatibility target mods are active.");
            return;
        }
        var failures = new List<string>();
        foreach (RimLLMCompatTarget target in RimLLMCompatBootstrap.Targets)
        {
            if (!target.IsInstalled)
            {
                TestHelpers.LogSkipped(nameof(InstalledTargetsReportPatched), $"{target.ModId} not active");
                continue;
            }
            if (!target.IsPatched || target.PatchError != null)
            {
                failures.Add($"{target.ModId}: IsPatched={target.IsPatched}, PatchError={target.PatchError}");
            }
        }
        TestHelpers.AssertNone(failures, "compat targets failed to hook");
    }
}
