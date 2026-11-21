using System;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>全域配置（GlobalConfig）分頁的繪製。</summary>
public static class GlobalConfigDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    public static float GetHeight(float width)
    {
        return 560f;
    }

    public static void DrawGlobalConfigSettings(Listing_Standard listing)
    {
        DrawResetRow(listing);

        float prevTimeout = Settings.ApiTimeout;
        int prevRetries = Settings.MaxRetries;
        float prevDelay = Settings.RetryDelay;
        int prevMaxConcurrent = Settings.MaxConcurrentRequests;
        bool prevNativeSchema = Settings.EnableNativeSchema;
        bool prevJsonRepair = Settings.EnableJsonRepair;

        listing.Label("RimLLM_ApiTimeoutLabel".Translate(Mathf.RoundToInt(Settings.ApiTimeout)));
        Settings.ApiTimeout = listing.Slider(Settings.ApiTimeout, 5f, 120f);
        RimLLMUIStyle.DrawHint(listing, "RimLLM_ApiTimeoutHint".Translate());

        listing.Label("RimLLM_MaxRetriesLabel".Translate(Settings.MaxRetries));
        float retriesVal = listing.Slider((float)Settings.MaxRetries, 0f, 10f);
        Settings.MaxRetries = Mathf.RoundToInt(retriesVal);
        RimLLMUIStyle.DrawHint(listing, "RimLLM_MaxRetriesHint".Translate());

        listing.Label("RimLLM_RetryDelayLabel".Translate(Mathf.RoundToInt(Settings.RetryDelay)));
        Settings.RetryDelay = listing.Slider(Settings.RetryDelay, 0f, 10f);
        RimLLMUIStyle.DrawHint(listing, "RimLLM_RetryDelayHint".Translate());

        listing.Label("RimLLM_MaxConcurrentRequestsLabel".Translate(Settings.MaxConcurrentRequests));
        float maxConcurrentVal = listing.Slider((float)Settings.MaxConcurrentRequests, 1f, 10f);
        Settings.MaxConcurrentRequests = Mathf.RoundToInt(maxConcurrentVal);
        RimLLMUIStyle.DrawHint(listing, "RimLLM_MaxConcurrentRequestsHint".Translate());
        listing.Gap(6f);

        bool enableNativeSchema = Settings.EnableNativeSchema;
        listing.CheckboxLabeled("RimLLM_EnableNativeSchemaLabel".Translate(), ref enableNativeSchema);
        Settings.EnableNativeSchema = enableNativeSchema;
        RimLLMUIStyle.DrawHint(listing, "RimLLM_EnableNativeSchemaHint".Translate());

        bool enableJsonRepair = Settings.EnableJsonRepair;
        listing.CheckboxLabeled("RimLLM_EnableJsonRepairLabel".Translate(), ref enableJsonRepair);
        Settings.EnableJsonRepair = enableJsonRepair;
        RimLLMUIStyle.DrawHint(listing, "RimLLM_EnableJsonRepairHint".Translate());
        listing.Gap(6f);

        bool changed = Math.Abs(prevTimeout - Settings.ApiTimeout) > 0.001f ||
            prevRetries != Settings.MaxRetries ||
            Math.Abs(prevDelay - Settings.RetryDelay) > 0.001f ||
            prevMaxConcurrent != Settings.MaxConcurrentRequests ||
            prevNativeSchema != Settings.EnableNativeSchema ||
            prevJsonRepair != Settings.EnableJsonRepair;

        if (RimLLMUIStyle.ShouldSaveSettings(changed))
        {
            Settings.Write();
        }

        DrawReasoningEffort(listing);
    }

    private static void DrawResetRow(Listing_Standard listing)
    {
        Rect resetRow = listing.GetRect(30f);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(new Rect(resetRow.x, resetRow.y, resetRow.width - 190f, resetRow.height), "RimLLM_AdvancedDontTouch".Translate());
        }
        if (Widgets.ButtonText(new Rect(resetRow.xMax - 180f, resetRow.y, 180f, resetRow.height), "RimLLM_ResetToDefaultsBtn".Translate()))
        {
            Settings.ResetRequestSettingsToDefaults();
            Settings.Write();
        }
        listing.GapLine(10f);
    }

    private static void DrawReasoningEffort(Listing_Standard listing)
    {
        Rect effortRect = listing.GetRect(30f);
        Rect labelRect = new(effortRect.x, effortRect.y, 250f, effortRect.height);
        Rect btnRect = new(effortRect.x + 260f, effortRect.y, 200f, effortRect.height);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(labelRect, "RimLLM_ReasoningEffortLabel".Translate());
        }

        string currentEffortLabel = Settings.DefaultReasoningEffort == null ? "RimLLM_ReasoningEffort_Auto".Translate() : $"RimLLM_ReasoningEffort_{Settings.DefaultReasoningEffort}".Translate();
        if (Widgets.ButtonText(btnRect, currentEffortLabel))
        {
            List<FloatMenuOption> options =
            [
                new("RimLLM_ReasoningEffort_Auto".Translate(), () => { Settings.DefaultReasoningEffort = null; Settings.Write(); }),
                new("RimLLM_ReasoningEffort_Low".Translate(), () => { Settings.DefaultReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.Low; Settings.Write(); }),
                new("RimLLM_ReasoningEffort_Medium".Translate(), () => { Settings.DefaultReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.Medium; Settings.Write(); }),
                new("RimLLM_ReasoningEffort_High".Translate(), () => { Settings.DefaultReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.High; Settings.Write(); })
            ];
            Find.WindowStack.Add(new FloatMenu(options));
        }
        RimLLMUIStyle.DrawHint(listing, "RimLLM_ReasoningEffortHint".Translate());
    }
}
