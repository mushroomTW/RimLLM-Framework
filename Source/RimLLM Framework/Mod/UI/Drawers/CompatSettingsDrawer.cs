using System.Collections.Generic;
using RimLLM_Framework.Compat;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>第三方整合（Compat）分頁：每個可接管的 Mod 一列緊湊卡片，共用警語只在頁首顯示一次，細節收進懸停提示。</summary>
public static class CompatSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private const float RowPadding = 10f;
    private const float CheckboxSize = 24f;

    private static readonly Color StatusActive = RimLLMUIStyle.StatusGreen;
    private static readonly Color StatusInstalled = RimLLMUIStyle.StatusBlue;
    private static readonly Color StatusPatchFailed = new Color32(0xef, 0x44, 0x44, 0xff);
    private static readonly Color StatusNotInstalled = new Color32(0x94, 0xa3, 0xb8, 0xff);

    private static readonly Color DimmedTint = new(1f, 1f, 1f, 0.55f);

    // 安裝與掛載狀態在啟動後即固定（Mod 清單不變、Harmony 攔截只在啟動時套用一次），故只算一次；接管開關每幀從設定讀。
    private sealed class Row
    {
        public required RimLLMCompatTarget Target;
        public CompatRowState State;
        public required string Title;
    }

    private static List<Row>? _rows;

    /// <summary>排序：可用（已啟用且掛載成功）→ 掛載失敗 → 已安裝未啟用 → 未安裝。</summary>
    private static List<Row> Rows
    {
        get
        {
            if (_rows != null) return _rows;

            List<Row> rows = new(RimLLMCompatBootstrap.Targets.Count);
            foreach (RimLLMCompatTarget target in RimLLMCompatBootstrap.Targets)
            {
                rows.Add(new Row
                {
                    Target = target,
                    State = Classify(target.IsInstalled, target.IsDownloaded, target.IsPatched),
                    Title = $"<b>{target.DisplayName}</b>  <color=#94a3b8>{target.ModId}</color>"
                });
            }
            rows.Sort((a, b) => SortRank(a.State).CompareTo(SortRank(b.State)));
            _rows = rows;
            return _rows;
        }
    }

    /// <summary>目標 Mod 在設定頁的狀態；未安裝與未啟用分開，掛載失敗只可能發生在已啟用時。</summary>
    internal enum CompatRowState
    {
        NotInstalled,
        Inactive,
        PatchFailed,
        Ready
    }

    internal static CompatRowState Classify(bool installed, bool downloaded, bool patched)
    {
        if (!installed) return downloaded ? CompatRowState.Inactive : CompatRowState.NotInstalled;
        return patched ? CompatRowState.Ready : CompatRowState.PatchFailed;
    }

    internal static int SortRank(CompatRowState state)
    {
        return state switch
        {
            CompatRowState.Ready => 0,
            CompatRowState.PatchFailed => 1,
            CompatRowState.Inactive => 2,
            _ => 3,
        };
    }

    public static float GetHeight(float width)
    {
        float height = 150f;
        foreach (Row row in Rows)
        {
            height += RimLLMUIStyle.StatusRowHeight + RimLLMUIStyle.StatusRowGap;
            if (ShowsModelPicker(row)) height += ModelRowHeight + RimLLMUIStyle.StatusRowGap;
        }
        return height;
    }

    private const float ModelRowHeight = 28f;

    private static bool ShowsModelPicker(Row row)
    {
        return row.State == CompatRowState.Ready && Settings.IsCompatTakeoverEnabled(row.Target.ModId);
    }

    private static void DrawModelPicker(Rect rect, Row row)
    {
        string modId = row.Target.ModId;
        string? pin = Settings.GetCompatModelPin(modId);
        string shown = string.IsNullOrEmpty(pin) ? (string)"RimLLM_CompatModelDefault".Translate() : pin;

        Rect labelRect = new(rect.x + RowPadding, rect.y, 120f, rect.height);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            GUI.color = RimLLMUIStyle.Muted;
            Widgets.Label(labelRect, "RimLLM_CompatModelLabel".Translate());
            GUI.color = Color.white;
        }

        Rect buttonRect = new(labelRect.xMax, rect.y + 2f, Mathf.Min(360f, rect.width - labelRect.width - RowPadding * 2f), rect.height - 4f);
        if (Widgets.ButtonText(buttonRect, shown))
        {
            var options = new List<FloatMenuOption>
            {
                new("RimLLM_CompatModelDefault".Translate(), () => SavePin(row.Target, entry: null))
            };
            foreach (string entry in Settings.FallbackChain)
            {
                string captured = entry;
                options.Add(new FloatMenuOption(entry, () => SavePin(row.Target, captured)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        TooltipHandler.TipRegion(buttonRect, "RimLLM_CompatModelTip".Translate());
    }

    private static void SavePin(RimLLMCompatTarget target, string? entry)
    {
        Settings.SetCompatModelPin(target.ModId, entry);
        Settings.Write();
        target.OnTakeoverToggled(enabled: true);
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
            DrawRow(listing.GetRect(RimLLMUIStyle.StatusRowHeight), row);
            listing.Gap(RimLLMUIStyle.StatusRowGap);
            if (ShowsModelPicker(row))
            {
                DrawModelPicker(listing.GetRect(ModelRowHeight), row);
                listing.Gap(RimLLMUIStyle.StatusRowGap);
            }
        }
    }

    private static void DrawRow(Rect rowRect, Row row)
    {
        bool enabled = Settings.IsCompatTakeoverEnabled(row.Target.ModId);
        Color statusColor = StatusColor(row, enabled);

        Color tint = row.State >= CompatRowState.PatchFailed ? Color.white : DimmedTint;
        if (RimLLMUIStyle.DrawStatusRow(rowRect, statusColor, row.Title, StatusBadge(row, enabled), tint, CheckboxSize + 8f))
        {
            TooltipHandler.TipRegion(rowRect, BuildTooltip(row, enabled));
        }

        Rect checkboxRect = new(
            rowRect.xMax - RowPadding - CheckboxSize,
            rowRect.y + (rowRect.height - CheckboxSize) / 2f,
            CheckboxSize, CheckboxSize);

        GUI.color = tint;
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

    /// <summary>狀態色；「接管生效中」代表開關開啟且 Harmony 攔截已就緒，是否有可用供應商於每次請求才判定，這裡不重複查。</summary>
    private static Color StatusColor(Row row, bool enabled)
    {
        return row.State switch
        {
            CompatRowState.NotInstalled or CompatRowState.Inactive => StatusNotInstalled,
            CompatRowState.PatchFailed => StatusPatchFailed,
            _ => enabled ? StatusActive : StatusInstalled,
        };
    }

    private static string StatusBadge(Row row, bool enabled)
    {
        return row.State switch
        {
            CompatRowState.NotInstalled => (string)"RimLLM_CompatBadgeNotInstalled".Translate(),
            CompatRowState.Inactive => (string)"RimLLM_CompatBadgeInactive".Translate(),
            CompatRowState.PatchFailed => (string)"RimLLM_CompatBadgePatchFailed".Translate(),
            _ => (string)(enabled ? "RimLLM_CompatBadgeActive" : "RimLLM_CompatBadgeInstalled").Translate(),
        };
    }

    private static string StatusDescription(Row row, bool enabled)
    {
        return row.State switch
        {
            CompatRowState.NotInstalled => (string)"RimLLM_CompatStatusNotInstalled".Translate(),
            CompatRowState.Inactive => (string)"RimLLM_CompatStatusInactive".Translate(),
            CompatRowState.PatchFailed => (string)"RimLLM_CompatStatusPatchFailed".Translate(row.Target.PatchError ?? string.Empty),
            _ => (string)(enabled ? "RimLLM_CompatStatusActive" : "RimLLM_CompatStatusInstalled").Translate(),
        };
    }

    private static string BuildTooltip(Row row, bool enabled)
    {
        string name = row.Target.DisplayName;
        return $"<b>{name}</b>  <color=#94a3b8>{row.Target.ModId}</color>\n"
            + $"{StatusDescription(row, enabled)}\n\n"
            + $"{"RimLLM_CompatTakeoverLabel".Translate(name)}\n"
            + $"{"RimLLM_CompatTakeoverWarning".Translate(name)}";
    }
}
