using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimLLM_Framework.Core;
using RimWorld;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責預算與防護（Budget &amp; Safety）分頁的 UI 渲染。
    /// </summary>
    public static class BudgetSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        public static float GetHeight(float width)
        {
            float height = 220f; // 基礎預算設定 + 遙測顯示
            if (Settings.EnableAntiAbuse)
            {
                height += 150f; // 展開的防爆細部滑桿
            }
            else
            {
                height += 40f; // 僅防爆開關
            }
            return height;
        }

        public static void DrawBudgetSettings(Listing_Standard listing)
        {
            float prevDailyLimit = Settings.DailyBudgetLimit;
            int prevPolicy = Settings.BudgetPolicy;
            bool prevEnableAntiAbuse = Settings.EnableAntiAbuse;
            int prevMaxRequests = Settings.MaxRequestsPerWindow;
            int prevWindow = Settings.ThrottlingWindowSeconds;
            int prevCooldown = Settings.CoolDownDurationSeconds;

            // 1. 今日預算上限 (Daily Budget Limit)
            listing.Label("RimLLM_DailyBudgetLimitLabel".Translate(Settings.DailyBudgetLimit.ToString("F2")));
            // 提供 0.0 ~ 20.0 的滑桿，若為 0.0 代表無限制
            Settings.DailyBudgetLimit = listing.Slider(Settings.DailyBudgetLimit, 0f, 20f);
            
            // 2. 預算超限應對策略 (Budget Policy)
            Rect policyRect = listing.GetRect(30f);
            Rect policyLabelRect = new Rect(policyRect.x, policyRect.y, 250f, policyRect.height);
            Rect policyBtnRect = new Rect(policyRect.x + 260f, policyRect.y, 280f, policyRect.height);
            
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(policyLabelRect, "RimLLM_BudgetPolicyLabel".Translate());
            Text.Anchor = TextAnchor.UpperLeft;

            string policyLabelKey = $"RimLLM_BudgetPolicy_{GetPolicyEnumName(Settings.BudgetPolicy)}";
            if (Widgets.ButtonText(policyBtnRect, policyLabelKey.Translate()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RimLLM_BudgetPolicy_HardBlock".Translate(), () => { Settings.BudgetPolicy = 0; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_BudgetPolicy_SilentMocking".Translate(), () => { Settings.BudgetPolicy = 1; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_BudgetPolicy_FallbackToFree".Translate(), () => { Settings.BudgetPolicy = 2; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_BudgetPolicy_DialogPrompt".Translate(), () => { Settings.BudgetPolicy = 3; Settings.Write(); })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
            listing.Gap(12f);

            // 3. 頻率防護 (Anti-Abuse Throttling)
            bool enableAntiAbuse = Settings.EnableAntiAbuse;
            listing.CheckboxLabeled("RimLLM_EnableAntiAbuseLabel".Translate(), ref enableAntiAbuse);
            Settings.EnableAntiAbuse = enableAntiAbuse;
            listing.Gap(6f);

            if (Settings.EnableAntiAbuse)
            {
                // 窗口內最大請求次數
                listing.Label("RimLLM_MaxRequestsPerWindowLabel".Translate(Settings.MaxRequestsPerWindow));
                float maxRequestsVal = listing.Slider((float)Settings.MaxRequestsPerWindow, 2f, 50f);
                Settings.MaxRequestsPerWindow = Mathf.RoundToInt(maxRequestsVal);

                // 監測窗口秒數
                listing.Label("RimLLM_ThrottlingWindowSecondsLabel".Translate(Settings.ThrottlingWindowSeconds));
                float windowVal = listing.Slider((float)Settings.ThrottlingWindowSeconds, 2f, 60f);
                Settings.ThrottlingWindowSeconds = Mathf.RoundToInt(windowVal);

                // 冷卻秒數
                listing.Label("RimLLM_CoolDownDurationSecondsLabel".Translate(Settings.CoolDownDurationSeconds));
                float cooldownVal = listing.Slider((float)Settings.CoolDownDurationSeconds, 5f, 300f);
                Settings.CoolDownDurationSeconds = Mathf.RoundToInt(cooldownVal);
                listing.Gap(12f);
            }

            // 4. 遙測統計與重置
            listing.GapLine(12f);
            listing.Label("RimLLM_DailyAccumulatedCostLabel".Translate(
                Settings.DailyAccumulatedCost.ToString("F4"), 
                string.IsNullOrEmpty(Settings.DailyBudgetResetDate) ? DateTime.Today.ToString("yyyy-MM-dd") : Settings.DailyBudgetResetDate
            ));
            listing.Gap(6f);

            Rect resetBtnRect = listing.GetRect(32f);
            resetBtnRect.width = 180f;
            if (Widgets.ButtonText(resetBtnRect, "RimLLM_ResetDailyCostBtn".Translate()))
            {
                Settings.DailyAccumulatedCost = 0f;
                Settings.DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd");
                Settings.SaveTelemetry();
                Messages.Message("RimLLM_MsgDailyCostReset".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }

            // 檢查變更並寫入
            if (Math.Abs(prevDailyLimit - Settings.DailyBudgetLimit) > 0.001f ||
                prevPolicy != Settings.BudgetPolicy ||
                prevEnableAntiAbuse != Settings.EnableAntiAbuse ||
                prevMaxRequests != Settings.MaxRequestsPerWindow ||
                prevWindow != Settings.ThrottlingWindowSeconds ||
                prevCooldown != Settings.CoolDownDurationSeconds)
            {
                Settings.Write();
            }
        }

        private static string GetPolicyEnumName(int policy)
        {
            switch (policy)
            {
                case 0: return "HardBlock";
                case 1: return "SilentMocking";
                case 2: return "FallbackToFree";
                case 3: return "DialogPrompt";
                default: return "HardBlock";
            }
        }
    }
}
