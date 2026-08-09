using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責 RimLLM Framework 的設定畫面進入點與框架布局。
    /// 將具體的分頁渲染與交互狀態委託給各個 Drawer 類別。
    /// </summary>
    public static class RimLLMSettingsUI
    {
        // 全域選單狀態
        private static string activeMainCategory = "Providers";
        private static Vector2 _detailScrollPosition = Vector2.zero;

        /// <summary>RimWorld 捲軸的固定寬度。</summary>
        private const float ScrollBarWidth = 16f;

        /// <summary>捲到底時在最後一個控制項下方保留的呼吸空間。</summary>
        private const float DetailBottomPadding = 12f;

        /// <summary>各分頁上一幀實際畫出的內容高度，見 <see cref="ResolveDetailViewHeight"/>。</summary>
        private static readonly Dictionary<string, float> _measuredHeights = new Dictionary<string, float>();

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
            DrawCategoryButton(listing, "RimLLM_TabEmbedding".Translate(), "Embedding");
            listing.Gap(4f);
            DrawCategoryButton(listing, "RimLLM_TabBudget".Translate(), "Budget");
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
                Widgets.DrawBoxSolid(btnRect, RimLLMUIStyle.SelectionFill);
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

            string text = activeMainCategory == categoryId ? $"<color=white><b>{label}</b></color>" : $"<color=silver>{label}</color>";
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
            {
                Widgets.Label(btnRect, text);
            }
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
            else if (activeMainCategory == "Embedding")
            {
                titleText = "RimLLM_TitleEmbedding".Translate();
            }
            else if (activeMainCategory == "Budget")
            {
                titleText = "RimLLM_TitleBudget".Translate();
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

            // 扣掉捲軸寬度，否則垂直捲軸出現時內容被擠出右緣，還會多冒出一條水平捲軸。
            float viewWidth = detailRect.width - ScrollBarWidth;
            Rect viewRect = new Rect(0f, 0f, viewWidth, ResolveDetailViewHeight(viewWidth, detailRect.height));
            Widgets.BeginScrollView(detailRect, ref _detailScrollPosition, viewRect);
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
            else if (activeMainCategory == "Embedding")
            {
                EmbeddingSettingsDrawer.DrawEmbeddingSettings(listing);
            }
            else if (activeMainCategory == "Budget")
            {
                BudgetSettingsDrawer.DrawBudgetSettings(listing);
            }
            else if (activeMainCategory == "ChatTest")
            {
                ChatTestDrawer.DrawChatTestSettings(listing);
            }
            else if (activeMainCategory == "Debug")
            {
                DebugSettingsDrawer.DrawDebugSettings(listing);
            }

            // CurHeight 必須在 End() 之前讀取；End() 之後該值不再代表本次繪製的內容高度。
            _measuredHeights[GetDetailPageKey()] = listing.CurHeight;

            listing.End();
            Widgets.EndScrollView();
        }

        /// <summary>
        /// 決定捲動內容的高度。優先採用上一幀實際畫出來的高度，只有該分頁第一次繪製時
        /// 才退回各 Drawer 的估計值。
        ///
        /// 先前一律使用估計值，而那些是手調的魔術數字，會與實際繪製內容漂移 ——
        /// 供應商分頁曾回報 848px 但實際只畫了約 662px，底部因此多出一大塊幽靈空白。
        /// 改為量測後，新增或調整任何控制項都不必再同步維護一份高度公式。
        /// </summary>
        private static float ResolveDetailViewHeight(float width, float visibleHeight)
        {
            float height = _measuredHeights.TryGetValue(GetDetailPageKey(), out float measured)
                ? measured + DetailBottomPadding
                : GetDetailViewHeight(width);

            // 內容比可視區短時仍讓 viewRect 至少等高，避免捲動範圍為負值。
            return Mathf.Max(height, visibleHeight);
        }

        /// <summary>
        /// 量測值的鍵。供應商分頁各家內容長度不同，因此要連子分頁一起入鍵。
        /// </summary>
        private static string GetDetailPageKey()
        {
            return activeMainCategory == "Providers"
                ? "Providers/" + ProviderSettingsDrawer.ActiveProviderSubTab
                : activeMainCategory;
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
            else if (activeMainCategory == "Embedding")
            {
                return EmbeddingSettingsDrawer.GetHeight(width);
            }
            else if (activeMainCategory == "Budget")
            {
                return BudgetSettingsDrawer.GetHeight(width);
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
