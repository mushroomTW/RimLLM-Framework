using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責 API 供應商（Providers）分頁的頂層 SubTab 路由分發與選單繪製。
    /// </summary>
    public static class ProviderSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        // 供應商分頁專屬的 UI 暫存狀態
        public static string ActiveProviderSubTab { get; set; } = "Gemini";
        private static Vector2 _midScrollPosition = Vector2.zero;
        private static Vector2 _detailScrollPosition = Vector2.zero;

        /// <summary>
        /// 獲取供應商設定詳細內容的滾動高度。
        /// </summary>
        public static float GetHeight(float width)
        {
            bool enabled = Settings.IsProviderEnabled(ActiveProviderSubTab);
            if (!enabled) return 120f;

            int modelCount = Settings.GetModelList(ActiveProviderSubTab).Count;
            float modelSectionHeight = modelCount > 0 ? 280f : 60f;

            // 動態計算 API 金鑰列表的高度
            float keysHeight = 0f;
            if (ActiveProviderSubTab != "OpenAICompatible")
            {
                string rawApiKey = Settings.GetApiKey(ActiveProviderSubTab);
                var keys = rawApiKey.Split(new char[] { ',' }, System.StringSplitOptions.None);
                int keyCount = Mathf.Max(1, keys.Length);
                keysHeight = 30f + (keyCount * 32f) + 36f;
            }

            float extraHeight = 0f;
            if (ActiveProviderSubTab == "Kimi" || ActiveProviderSubTab == "MiniMax" || ActiveProviderSubTab == "Qwen")
            {
                extraHeight = 30f;
            }
            else if (ActiveProviderSubTab == "OpenAICompatible")
            {
                extraHeight = 60f;
            }

            float statsHeight = 120f;
            if (SDK.RimLLMProvider.Manager is Manager.RimLLMManager mgr)
            {
                if (mgr.UsageTracker.ProviderStatistics.TryGetValue(ActiveProviderSubTab, out var stats) && stats.TotalPromptTokens > 0)
                {
                    statsHeight += 50f;
                }
            }
            return 250f + keysHeight + modelSectionHeight + 100f + statsHeight + extraHeight;
        }

        /// <summary>
        /// 繪製中欄的供應商選單。
        /// </summary>
        public static void DrawMiddleProviderMenu(Rect rect)
        {
            Rect contentRect = rect.ContractedBy(6f);

            Rect titleRect = new Rect(contentRect.x, contentRect.y, contentRect.width, 20f);
            Text.Font = GameFont.Small;
            Widgets.Label(titleRect, "RimLLM_ApiProviders".Translate());

            Rect listRect = new Rect(contentRect.x, titleRect.yMax + 4f, contentRect.width, contentRect.height - 24f);
            // 12 個供應商，每個按鈕高度 46f + 4f gap = 50f
            Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, 12 * 50f + 10f);
            Widgets.BeginScrollView(listRect, ref _midScrollPosition, viewRect);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            DrawProviderSubButton(listing, "Google Gemini", "Gemini");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "OpenAI", "OpenAI");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "DeepSeek", "DeepSeek");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "Groq", "Groq");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "Grok", "Grok");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "Z.ai", "Z.ai");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "OpenRouter", "OpenRouter");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "Kimi", "Kimi");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "MiniMax", "MiniMax");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "Qwen", "Qwen");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "NVIDIA", "Nvidia");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "OpenAI Compatible", "OpenAICompatible");
            listing.End();

            Widgets.EndScrollView();
        }

        private static void DrawProviderSubButton(Listing_Standard listing, string label, string providerId)
        {
            Rect btnRect = listing.GetRect(46f);

            if (ActiveProviderSubTab == providerId)
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
                ActiveProviderSubTab = providerId;
                _detailScrollPosition = Vector2.zero;
            }

            Rect nameRect = new Rect(btnRect.x + 8f, btnRect.y + 3f, btnRect.width - 16f, 22f);
            Rect statusRect = new Rect(btnRect.x + 8f, btnRect.y + 25f, btnRect.width - 16f, 18f);
            Text.Font = GameFont.Small;
            string nameText = ActiveProviderSubTab == providerId ? $"<color=white><b>{label}</b></color>" : $"<color=silver>{label}</color>";
            Widgets.Label(nameRect, nameText);

            Text.Font = GameFont.Tiny;
            bool enabled = Settings.IsProviderEnabled(providerId);
            Color oldColor = GUI.color;
            GUI.color = enabled ? new Color(0.13f, 0.77f, 0.37f) : new Color(0.53f, 0.53f, 0.53f);
            string statusText = enabled ? "RimLLM_StatusEnabled".Translate() : "RimLLM_StatusDisabled".Translate();
            if (enabled)
            {
                int modelCount = Settings.GetModelList(providerId).Count;
                statusText += " | " + "RimLLM_ModelsCount".Translate(modelCount);
            }
            Widgets.Label(statusRect, statusText);
            GUI.color = oldColor;

            Text.Font = GameFont.Small;
        }

        /// <summary>
        /// 根據目前的供應商，調度右側的詳細配置渲染。
        /// </summary>
        public static void DrawRightDetailContent(Listing_Standard listing)
        {
            if (ActiveProviderSubTab == "Gemini")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "Gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash");
            else if (ActiveProviderSubTab == "OpenAI")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-4o-mini");
            else if (ActiveProviderSubTab == "DeepSeek")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "DeepSeek", "https://api.deepseek.com", "deepseek-chat");
            else if (ActiveProviderSubTab == "Groq")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile");
            else if (ActiveProviderSubTab == "Grok")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "Grok", "https://api.x.ai/v1", "grok-2-1212");
            else if (ActiveProviderSubTab == "Z.ai")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "Z.ai", "https://api.z.ai/api/paas/v4", "glm-4.5-flash");
            else if (ActiveProviderSubTab == "OpenRouter")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "OpenRouter", "https://openrouter.ai/api/v1", "google/gemini-2.5-flash");
            else if (ActiveProviderSubTab == "Kimi")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "Kimi", "https://api.moonshot.ai/v1", "moonshot-v1-8k");
            else if (ActiveProviderSubTab == "MiniMax")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "MiniMax", "https://api.minimax.io/v1", "abab6.5g-chat");
            else if (ActiveProviderSubTab == "Qwen")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "Qwen", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-plus");
            else if (ActiveProviderSubTab == "Nvidia")
                GenericProviderSubTabDrawer.DrawGenericProviderSettings(listing, "Nvidia", "https://integrate.api.nvidia.com/v1", "meta/llama-3.1-8b-instruct");
            else if (ActiveProviderSubTab == "OpenAICompatible")
                OpenAICompatibleSubTabDrawer.DrawOpenAICompatibleSettings(listing, "OpenAICompatible", "http://localhost:1234/v1", "default");
        }
    }
}
