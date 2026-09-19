using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimLLM_Framework.Compat;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責第三方整合（Compat）分頁的 UI 渲染：每個可接管的 Mod 一列緊湊卡片，
    /// 狀態以色點＋短標籤呈現，細節與警語收進滑鼠懸停的提示。
    ///
    /// 先前每個 Mod 各佔一大塊（名稱、狀態、開關、兩段警語全部重複），
    /// 目標一多整頁就得不停捲動；共用的警語改為只在頁首顯示一次。
    /// </summary>
    public static class CompatSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        private const float RowHeight = 34f;
        private const float RowGap = 4f;
        private const float RowPadding = 10f;
        private const float DotSize = 8f;
        private const float BadgeWidth = 120f;
        private const float CheckboxSize = 24f;

        private static readonly Color StatusActive = new Color32(0x4a, 0xde, 0x80, 0xff);
        private static readonly Color StatusInstalled = new Color32(0x60, 0xa5, 0xfa, 0xff);
        private static readonly Color StatusPatchFailed = new Color32(0xef, 0x44, 0x44, 0xff);
        private static readonly Color StatusNotInstalled = new Color32(0x94, 0xa3, 0xb8, 0xff);

        /// <summary>未安裝的列整體淡化，讓可操作的列在清單中一眼可辨。</summary>
        private static readonly Color DimmedTint = new Color(1f, 1f, 1f, 0.55f);

        /// <summary>
        /// 一列的靜態資料。安裝與掛載狀態在啟動後就固定（Mod 清單不會在執行期變動、
        /// Harmony 攔截只在啟動時套用一次），因此只算一次；接管開關每幀從設定讀。
        /// </summary>
        private sealed class Row
        {
            public RimLLMCompatTarget Target;
            public bool Installed;
            public bool Patched;
            public string Title;
        }

        private static List<Row> _rows;

        /// <summary>
        /// 排序：可用（已安裝且掛載成功）→ 掛載失敗 → 未安裝。
        /// 玩家真正能操作的列排最前，清單變長時不必在一堆未安裝項目裡找。
        /// </summary>
        private static List<Row> Rows
        {
            get
            {
                if (_rows != null) return _rows;

                List<Row> rows = new List<Row>(RimLLMCompatBootstrap.Targets.Count);
                foreach (RimLLMCompatTarget target in RimLLMCompatBootstrap.Targets)
                {
                    rows.Add(new Row
                    {
                        Target = target,
                        Installed = target.IsInstalled,
                        Patched = target.IsPatched,
                        Title = $"<b>{target.DisplayName}</b>  <color=#94a3b8>{target.ModId}</color>"
                    });
                }
                rows.Sort((a, b) => SortRank(a).CompareTo(SortRank(b)));
                _rows = rows;
                return _rows;
            }
        }

        private static int SortRank(Row row)
        {
            if (!row.Installed) return 2;
            return row.Patched ? 0 : 1;
        }

        public static float GetHeight(float width)
        {
            // 說明段落 + 共用警語 + 每個目標一列
            return 150f + RimLLMCompatBootstrap.Targets.Count * (RowHeight + RowGap);
        }

        public static void DrawCompatSettings(Listing_Standard listing)
        {
            using (RimLLMUIStyle.With(font: GameFont.Small))
            {
                GUI.color = RimLLMUIStyle.Muted;
                listing.Label("RimLLM_CompatIntro".Translate());
                GUI.color = Color.white;
            }
            listing.Gap(6f);
            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                GUI.color = RimLLMUIStyle.Warning;
                listing.Label("RimLLM_CompatTakeoverWarningShared".Translate());
                GUI.color = RimLLMUIStyle.Muted;
                listing.Label("RimLLM_CompatOfflineFallbackNote".Translate());
                GUI.color = Color.white;
            }
            listing.Gap(12f);

            foreach (Row row in Rows)
            {
                DrawRow(listing.GetRect(RowHeight), row);
                listing.Gap(RowGap);
            }
        }

        private static void DrawRow(Rect rowRect, Row row)
        {
            bool enabled = Settings.IsCompatTakeoverEnabled(row.Target.ModId);
            Color statusColor = StatusColor(row, enabled);

            Widgets.DrawBoxSolid(rowRect, RimLLMUIStyle.ChipFill);
            if (Mouse.IsOver(rowRect))
            {
                Widgets.DrawHighlight(rowRect);
                TooltipHandler.TipRegion(rowRect, BuildTooltip(row, enabled));
            }

            Rect checkboxRect = new Rect(
                rowRect.xMax - RowPadding - CheckboxSize,
                rowRect.y + (rowRect.height - CheckboxSize) / 2f,
                CheckboxSize, CheckboxSize);
            Rect badgeRect = new Rect(checkboxRect.x - 8f - BadgeWidth, rowRect.y, BadgeWidth, rowRect.height);
            Rect dotRect = new Rect(
                rowRect.x + RowPadding,
                rowRect.y + (rowRect.height - DotSize) / 2f,
                DotSize, DotSize);
            Rect titleRect = new Rect(dotRect.xMax + 8f, rowRect.y, badgeRect.x - dotRect.xMax - 16f, rowRect.height);

            if (!row.Installed) GUI.color = DimmedTint;

            Widgets.DrawBoxSolid(dotRect, statusColor * GUI.color);
            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Small, wordWrap: false))
            {
                Widgets.Label(titleRect, row.Title);
            }
            using (RimLLMUIStyle.With(TextAnchor.MiddleRight, GameFont.Tiny, wordWrap: false))
            {
                Color previousColor = GUI.color;
                GUI.color = statusColor * previousColor;
                Widgets.Label(badgeRect, StatusBadge(row, enabled));
                GUI.color = previousColor;
            }

            bool toggled = enabled;
            Widgets.Checkbox(checkboxRect.position, ref toggled, CheckboxSize);
            GUI.color = Color.white;

            if (toggled != enabled)
            {
                Settings.SetCompatTakeoverEnabled(row.Target.ModId, toggled);
                Settings.Write();
                row.Target.OnTakeoverToggled(toggled);
            }
        }

        /// <summary>
        /// 狀態優先序：未安裝 → 掛載失敗 → 接管生效中 → 已安裝（開關未開）。
        /// 「接管生效中」代表開關開啟且 Harmony 攔截已就緒；RimLLM 當下有沒有可用供應商是每次請求才判定，這裡不重複查。
        /// </summary>
        private static Color StatusColor(Row row, bool enabled)
        {
            if (!row.Installed) return StatusNotInstalled;
            if (!row.Patched) return StatusPatchFailed;
            return enabled ? StatusActive : StatusInstalled;
        }

        private static string StatusBadge(Row row, bool enabled)
        {
            if (!row.Installed) return "RimLLM_CompatBadgeNotInstalled".Translate();
            if (!row.Patched) return "RimLLM_CompatBadgePatchFailed".Translate();
            return (enabled ? "RimLLM_CompatBadgeActive" : "RimLLM_CompatBadgeInstalled").Translate();
        }

        private static string StatusDescription(Row row, bool enabled)
        {
            if (!row.Installed) return "RimLLM_CompatStatusNotInstalled".Translate();
            if (!row.Patched) return "RimLLM_CompatStatusPatchFailed".Translate(row.Target.PatchError ?? string.Empty);
            return (enabled ? "RimLLM_CompatStatusActive" : "RimLLM_CompatStatusInstalled").Translate();
        }

        /// <summary>只在滑鼠停在該列時才組字串，避免每幀對每列做 Translate 與串接。</summary>
        private static string BuildTooltip(Row row, bool enabled)
        {
            string name = row.Target.DisplayName;
            return $"<b>{name}</b>  <color=#94a3b8>{row.Target.ModId}</color>\n"
                + $"{StatusDescription(row, enabled)}\n\n"
                + $"{"RimLLM_CompatTakeoverLabel".Translate(name)}\n"
                + $"{"RimLLM_CompatTakeoverWarning".Translate(name)}";
        }
    }
}
