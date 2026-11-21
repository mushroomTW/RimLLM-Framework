using System;
using System.Collections.Generic;
using System.Globalization;
using RimLLM_Framework.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>預算與防護（Budget &amp; Safety）分頁的繪製。</summary>
public static class BudgetSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    public static float GetHeight(float width)
    {
        float height = 280f;
        if (Settings.EnableAntiAbuse)
        {
            height += 150f;
        }
        else
        {
            height += 40f;
        }
        return height;
    }

    private const float MaxDailyTokenBudget = 10000000f;

    public static void DrawBudgetSettings(Listing_Standard listing)
    {
        long prevDailyLimit = Settings.DailyTokenBudgetLimit;
        int prevPolicy = Settings.BudgetPolicy;
        bool prevEnableAntiAbuse = Settings.EnableAntiAbuse;
        int prevMaxRequests = Settings.MaxRequestsPerWindow;
        int prevWindow = Settings.ThrottlingWindowSeconds;
        int prevCooldown = Settings.CoolDownDurationSeconds;

        listing.Label("RimLLM_DailyBudgetLimitLabel".Translate(Settings.DailyTokenBudgetLimit.ToString("N0", CultureInfo.InvariantCulture)));
        Settings.DailyTokenBudgetLimit = (long)Math.Round(listing.Slider((float)Settings.DailyTokenBudgetLimit, 0f, MaxDailyTokenBudget), MidpointRounding.ToEven);
        RimLLMUIStyle.DrawHint(listing, "RimLLM_DailyBudgetLimitHint".Translate());

        DrawBudgetPolicy(listing);

        bool enableAntiAbuse = Settings.EnableAntiAbuse;
        listing.CheckboxLabeled("RimLLM_EnableAntiAbuseLabel".Translate(), ref enableAntiAbuse);
        Settings.EnableAntiAbuse = enableAntiAbuse;
        RimLLMUIStyle.DrawHint(listing, "RimLLM_EnableAntiAbuseHint".Translate());
        listing.Gap(6f);

        DrawAntiAbuseSliders(listing);

        DrawDailyUsage(listing);

        // 僅在釋放滑鼠或非 GUI 呼叫時寫入，避免拖曳滑桿時每幀同步磁碟 I/O。
        bool changed = prevDailyLimit != Settings.DailyTokenBudgetLimit ||
            prevPolicy != Settings.BudgetPolicy ||
            prevEnableAntiAbuse != Settings.EnableAntiAbuse ||
            prevMaxRequests != Settings.MaxRequestsPerWindow ||
            prevWindow != Settings.ThrottlingWindowSeconds ||
            prevCooldown != Settings.CoolDownDurationSeconds;

        if (RimLLMUIStyle.ShouldSaveSettings(changed))
        {
            Settings.Write();
        }
    }

    private static void DrawBudgetPolicy(Listing_Standard listing)
    {
        Rect policyRect = listing.GetRect(30f);
        Rect policyLabelRect = new(policyRect.x, policyRect.y, 250f, policyRect.height);
        Rect policyBtnRect = new(policyRect.x + 260f, policyRect.y, 280f, policyRect.height);

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(policyLabelRect, "RimLLM_BudgetPolicyLabel".Translate());
        }

        string policyLabelKey = PolicyLabelKey(Settings.BudgetPolicy);
        if (Widgets.ButtonText(policyBtnRect, policyLabelKey.Translate()))
        {
            var options = new List<FloatMenuOption>();
            for (int policy = 0; policy < PolicyNames.Length; policy++)
            {
                int captured = policy;
                options.Add(new FloatMenuOption(
                    PolicyLabelKey(captured).Translate(),
                    () => { Settings.BudgetPolicy = captured; Settings.Write(); }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        RimLLMUIStyle.DrawHint(listing, "RimLLM_BudgetPolicyHint".Translate());
        listing.Gap(8f);
    }

    private static void DrawAntiAbuseSliders(Listing_Standard listing)
    {
        if (Settings.EnableAntiAbuse)
        {
            listing.Label("RimLLM_MaxRequestsPerWindowLabel".Translate(Settings.MaxRequestsPerWindow));
            float maxRequestsVal = listing.Slider((float)Settings.MaxRequestsPerWindow, 2f, 50f);
            Settings.MaxRequestsPerWindow = Mathf.RoundToInt(maxRequestsVal);

            listing.Label("RimLLM_ThrottlingWindowSecondsLabel".Translate(Settings.ThrottlingWindowSeconds));
            float windowVal = listing.Slider((float)Settings.ThrottlingWindowSeconds, 2f, 60f);
            Settings.ThrottlingWindowSeconds = Mathf.RoundToInt(windowVal);

            listing.Label("RimLLM_CoolDownDurationSecondsLabel".Translate(Settings.CoolDownDurationSeconds));
            float cooldownVal = listing.Slider((float)Settings.CoolDownDurationSeconds, 5f, 300f);
            Settings.CoolDownDurationSeconds = Mathf.RoundToInt(cooldownVal);
            listing.Gap(12f);
        }
    }

    private static void DrawDailyUsage(Listing_Standard listing)
    {
        listing.GapLine(12f);
        listing.Label("RimLLM_DailyAccumulatedCostLabel".Translate(
            Settings.DailyAccumulatedTokens.ToString("N0", CultureInfo.InvariantCulture),
            string.IsNullOrEmpty(Settings.DailyBudgetResetDate) ? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Settings.DailyBudgetResetDate
        ));
        listing.Gap(6f);

        Rect resetBtnRect = listing.GetRect(32f);
        resetBtnRect.width = 180f;
        if (Widgets.ButtonText(resetBtnRect, "RimLLM_ResetDailyCostBtn".Translate()))
        {
            Settings.DailyAccumulatedTokens = 0;
            Settings.DailyAccumulatedCost = 0f;
            Settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Settings.SaveTelemetry();
            Messages.Message("RimLLM_MsgDailyCostReset".Translate(), MessageTypeDefOf.TaskCompletion, historical: false);
        }
    }

    /// <summary>預算政策的名稱，索引即為 <see cref="RimLLMFrameworkSettings.BudgetPolicy"/> 的值。</summary>
    private static readonly string[] PolicyNames = ["HardBlock", "WarnOnly"];

    private static string PolicyLabelKey(int policy)
    {
        string name = policy >= 0 && policy < PolicyNames.Length ? PolicyNames[policy] : PolicyNames[0];
        return "RimLLM_BudgetPolicy_" + name;
    }
}
