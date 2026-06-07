using System;
using UnityEngine;
using Verse;
using RimWorld;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責 RimLLM Framework 的設定畫面進入點與框架布局。
    /// 將具體的分頁渲染與交互狀態委託給各個 Drawer 類別。
    /// </summary>
    public static class RimLLMSettingsUI
    {
        // 快捷訪問設定實例
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        // 全域選單狀態
        private static string activeMainCategory = "Providers";
        private static Vector2 _detailScrollPosition = Vector2.zero;

        /// <summary>
        /// 初始化 UI 狀態，例如還原對話歷史。
        /// </summary>
        public static void Initialize(RimLLMFrameworkSettings settings)
        {
            ChatTestDrawer.Initialize(settings);
        }

        /// <summary>
        /// 繪製 Mod 設定視窗的主入口。
        /// </summary>
        public static void DoSettingsWindowContents(Rect inRect)
        {
            // 動態尋找並調整 RimWorld 預設的 Mod 設定視窗大小
            var modSettingsWindow = Find.WindowStack.WindowOfType<Dialog_ModSettings>();
            if (modSettingsWindow != null)
            {
                float targetWidth = 1150f;
                float targetHeight = 780f;
                if (Math.Abs(modSettingsWindow.windowRect.width - targetWidth) > 1f || Math.Abs(modSettingsWindow.windowRect.height - targetHeight) > 1f)
                {
                    modSettingsWindow.windowRect.width = targetWidth;
                    modSettingsWindow.windowRect.height = targetHeight;
                    modSettingsWindow.windowRect.x = (UI.screenWidth - targetWidth) / 2f;
                    modSettingsWindow.windowRect.y = (UI.screenHeight - targetHeight) / 2f;
                }
            }

            // 直接在原地繪製完整設定內容
            DrawSettingsWindowBody(inRect);
        }

        private static void DrawSettingsWindowBody(Rect inRect)
        {
            float leftWidth = 135f;
            float midWidth = 190f;
            float gap = 8f;
            float height = inRect.height - 10f;

            // 1. 左側一級分類欄
            Rect leftColRect = new Rect(inRect.x, inRect.y, leftWidth, height);
            Widgets.DrawMenuSection(leftColRect);
            DrawLeftCategoryMenu(leftColRect);

            // 2. 根據選中項目決定中欄與右欄佈局
            if (activeMainCategory == "Providers")
            {
                Rect midColRect = new Rect(leftColRect.xMax + gap, inRect.y, midWidth, height);
                Widgets.DrawMenuSection(midColRect);
                ProviderSettingsDrawer.DrawMiddleProviderMenu(midColRect);

                Rect rightColRect = new Rect(midColRect.xMax + gap, inRect.y, inRect.width - leftWidth - midWidth - gap * 2f, height);
                Widgets.DrawMenuSection(rightColRect);
                DrawRightDetailContent(rightColRect);
            }
            else
            {
                Rect rightColRect = new Rect(leftColRect.xMax + gap, inRect.y, inRect.width - leftWidth - gap, height);
                Widgets.DrawMenuSection(rightColRect);
                DrawRightDetailContent(rightColRect);
            }
        }

        private static void DrawLeftCategoryMenu(Rect rect)
        {
            Rect contentRect = rect.ContractedBy(6f);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(contentRect);
            Text.Font = GameFont.Small;
            listing.Label("RimLLM_MainMenu".Translate());
            listing.Gap(6f);

            DrawCategoryButton(listing, "RimLLM_TabProviders".Translate(), "Providers");
            listing.Gap(4f);
            DrawCategoryButton(listing, "RimLLM_TabFallback".Translate(), "Fallback");
            listing.Gap(4f);
            DrawCategoryButton(listing, "RimLLM_TabGlobalConfig".Translate(), "GlobalConfig");
            listing.Gap(4f);
            DrawCategoryButton(listing, "RimLLM_TabChatTest".Translate(), "ChatTest");
            listing.Gap(4f);
            DrawCategoryButton(listing, "RimLLM_TabDebug".Translate(), "Debug");
            listing.End();
        }

        private static void DrawCategoryButton(Listing_Standard listing, string label, string categoryId)
        {
            Rect btnRect = listing.GetRect(32f);

            if (activeMainCategory == categoryId)
            {
                Widgets.DrawBoxSolid(btnRect, new Color(1f, 1f, 1f, 0.08f));
                Widgets.DrawBox(btnRect, 1);
            }
            else
            {
                if (Mouse.IsOver(btnRect))
                {
                    Widgets.DrawHighlight(btnRect);
                }
            }

            if (Widgets.ButtonInvisible(btnRect))
            {
                activeMainCategory = categoryId;
                _detailScrollPosition = Vector2.zero;
            }

            Text.Anchor = TextAnchor.MiddleCenter;
            string text = activeMainCategory == categoryId ? $"<color=white><b>{label}</b></color>" : $"<color=silver>{label}</color>";
            Widgets.Label(btnRect, text);
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static void DrawRightDetailContent(Rect rect)
        {
            Rect contentRect = rect.ContractedBy(8f);
            Rect titleRect = new Rect(contentRect.x, contentRect.y, contentRect.width, 24f);
            string titleText = "";

            if (activeMainCategory == "Providers")
            {
                string tabName = ProviderSettingsDrawer.ActiveProviderSubTab;
                if (ProviderSettingsDrawer.ActiveProviderSubTab == "OpenAICompatible")
                {
                    tabName += "RimLLM_LocalCompatibleMode".Translate();
                }
                titleText = "RimLLM_TitleProviderSettings".Translate(tabName);
            }
            else if (activeMainCategory == "Fallback")
            {
                titleText = "RimLLM_TitleFallback".Translate();
            }
            else if (activeMainCategory == "GlobalConfig")
            {
                titleText = "RimLLM_TitleGlobalConfig".Translate();
            }
            else if (activeMainCategory == "ChatTest")
            {
                titleText = "RimLLM_ChatTitle".Translate();
            }
            else if (activeMainCategory == "Debug")
            {
                titleText = "RimLLM_TitleDebug".Translate();
            }

            Widgets.Label(titleRect, $"<size=14><b>{titleText}</b></size>");
            Widgets.DrawLineHorizontal(contentRect.x, titleRect.yMax + 4f, contentRect.width);

            Rect detailRect = new Rect(contentRect.x, titleRect.yMax + 8f, contentRect.width, contentRect.height - 36f);
            Rect viewRect = new Rect(0f, 0f, detailRect.width, GetDetailViewHeight(detailRect.width));
            Widgets.BeginScrollView(detailRect, ref _detailScrollPosition, viewRect, false);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            if (activeMainCategory == "Providers")
            {
                ProviderSettingsDrawer.DrawRightDetailContent(listing);
            }
            else if (activeMainCategory == "Fallback")
            {
                FallbackSettingsDrawer.DrawFallbackSettings(listing);
            }
            else if (activeMainCategory == "GlobalConfig")
            {
                GlobalConfigDrawer.DrawGlobalConfigSettings(listing);
            }
            else if (activeMainCategory == "ChatTest")
            {
                ChatTestDrawer.DrawChatTestSettings(listing);
            }
            else if (activeMainCategory == "Debug")
            {
                DebugSettingsDrawer.DrawDebugSettings(listing);
            }

            listing.End();
            Widgets.EndScrollView();
        }

        private static float GetDetailViewHeight(float width)
        {
            if (activeMainCategory == "Providers")
            {
                return ProviderSettingsDrawer.GetHeight(width);
            }
            else if (activeMainCategory == "Fallback")
            {
                return FallbackSettingsDrawer.GetHeight(width);
            }
            else if (activeMainCategory == "GlobalConfig")
            {
                return GlobalConfigDrawer.GetHeight(width);
            }
            else if (activeMainCategory == "ChatTest")
            {
                return ChatTestDrawer.GetHeight(width);
            }
            else if (activeMainCategory == "Debug")
            {
                return DebugSettingsDrawer.GetHeight(width);
            }
            else
            {
                return 280f;
            }
        }
    }
}
