using System.Collections.Generic;
using RimLLM_Framework.Mod;
using RimTestRedux;
using Verse;

namespace RimLLM_Framework.InGameTests
{
    internal static class TestHelpers
    {
        private const string LogPrefix = "[RimLLM.InGameTests]";

        /// <summary>收集完所有失敗再一次拋出，一次執行就能看到全部問題。</summary>
        public static void AssertNone(List<string> failures, string message)
        {
            if (failures.Count > 0)
            {
                throw new AssertionException($"{message} ({failures.Count}):\n{string.Join("\n", failures)}");
            }
        }

        /// <summary>
        /// RimTest Redux 沒有執行期 skip API，前置條件不成立時 return 會被記為通過；
        /// 統一由這行日誌計算實際跳過數。
        /// </summary>
        public static void LogSkipped(string test, string reason)
        {
            Log.Message($"{LogPrefix} skipped: {test}: {reason}");
        }

        /// <summary>RimLLM Framework 自己的 ModContentPack。</summary>
        public static ModContentPack FrameworkContent =>
            LoadedModManager.GetMod<RimLLMFrameworkMod>()?.Content
            ?? throw new AssertionException("RimLLM Framework mod not loaded (no RimLLMFrameworkMod instance)");
    }
}
