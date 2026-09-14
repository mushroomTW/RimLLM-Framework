using UnityEngine;
using Verse;
using RimLLM_Framework.Compat;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責第三方整合（Compat）分頁的 UI 渲染：列出每個可接管的 Mod、偵測狀態與接管開關。
    /// </summary>
    public static class CompatSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        public static float GetHeight(float width)
        {
            // 說明段落 + 每個目標一個區塊（名稱列、狀態列、開關、警告文字）
            return 90f + RimLLMCompatBootstrap.Targets.Count * 150f;
        }

        public static void DrawCompatSettings(Listing_Standard listing)
        {
            using (RimLLMUIStyle.With(font: GameFont.Small))
            {
                GUI.color = RimLLMUIStyle.Muted;
                listing.Label("RimLLM_CompatIntro".Translate());
                GUI.color = Color.white;
            }
            listing.Gap(12f);

            foreach (RimLLMCompatTarget target in RimLLMCompatBootstrap.Targets)
            {
                DrawTarget(listing, target);
                listing.GapLine(12f);
            }
        }

        private static void DrawTarget(Listing_Standard listing, RimLLMCompatTarget target)
        {
            listing.Label($"<b>{target.DisplayName}</b>  <color=#94a3b8>({target.ModId})</color>");
            listing.Label(DescribeStatus(target));
            listing.Gap(4f);

            bool enabled = Settings.IsCompatTakeoverEnabled(target.ModId);
            bool previous = enabled;
            listing.CheckboxLabeled("RimLLM_CompatTakeoverLabel".Translate(target.DisplayName), ref enabled);
            if (enabled != previous)
            {
                Settings.SetCompatTakeoverEnabled(target.ModId, enabled);
                Settings.Write();
            }

            listing.Gap(4f);
            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                GUI.color = RimLLMUIStyle.Warning;
                listing.Label("RimLLM_CompatTakeoverWarning".Translate(target.DisplayName));
                GUI.color = RimLLMUIStyle.Muted;
                listing.Label("RimLLM_CompatOfflineFallbackNote".Translate());
                GUI.color = Color.white;
            }
        }

        /// <summary>
        /// 狀態優先序：未安裝 → 掛載失敗 → 接管生效中 → 已安裝（開關未開）。
        /// 「接管生效中」代表開關開啟且 Harmony 攔截已就緒；RimLLM 當下有沒有可用供應商是每次請求才判定，這裡不重複查。
        /// </summary>
        private static string DescribeStatus(RimLLMCompatTarget target)
        {
            if (!target.IsInstalled)
            {
                return $"<color=#94a3b8>{"RimLLM_CompatStatusNotInstalled".Translate()}</color>";
            }
            if (!target.IsPatched)
            {
                return $"<color=#ef4444>{"RimLLM_CompatStatusPatchFailed".Translate(target.PatchError ?? string.Empty)}</color>";
            }
            if (target.IsEnabled)
            {
                return $"<color=#4ade80>{"RimLLM_CompatStatusActive".Translate()}</color>";
            }
            return $"<color=#60a5fa>{"RimLLM_CompatStatusInstalled".Translate()}</color>";
        }
    }
}
