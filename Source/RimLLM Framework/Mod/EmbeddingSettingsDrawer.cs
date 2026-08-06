using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責「Embedding 向量」設定分頁的 UI 渲染。
    /// Embedding 供第三方 Mod 透過 RimLLMProvider.Instance.GetEmbeddingAsync 取用。
    /// </summary>
    public static class EmbeddingSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        public static float GetHeight(float width)
        {
            float baseHeight = 120f;
            if (Settings.EmbeddingProvider != "Offline_Trigram")
            {
                // 模型名稱與 API 金鑰輸入框
                baseHeight += 120f;

                // 自架服務額外顯示端點輸入框
                if (Settings.EmbeddingProvider == "LocalAPI_Ollama" || Settings.EmbeddingProvider == "LocalAPI_OpenAI")
                {
                    baseHeight += 60f;
                }
            }
            return baseHeight;
        }

        public static void DrawEmbeddingSettings(Listing_Standard listing)
        {
            string prevProvider = Settings.EmbeddingProvider;
            string prevModel = Settings.EmbeddingModel;
            string prevEndpoint = Settings.EmbeddingEndpoint;
            string prevApiKey = Settings.EmbeddingApiKey;

            listing.Label("RimLLM_EmbeddingExplanation".Translate());
            listing.Gap(6f);

            // 1. Embedding 供應商選擇
            Rect providerRect = listing.GetRect(30f);
            float providerLabelWidth = Text.CalcSize("RimLLM_EmbeddingProviderLabel".Translate()).x;
            Rect providerLabelRect = new Rect(providerRect.x, providerRect.y, providerLabelWidth + 5f, providerRect.height);
            Rect providerBtnRect = new Rect(providerRect.x + providerLabelWidth + 15f, providerRect.y, 250f, providerRect.height);

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(providerLabelRect, "RimLLM_EmbeddingProviderLabel".Translate());
            Text.Anchor = TextAnchor.UpperLeft;

            string currentProviderLabel = $"RimLLM_EmbeddingProvider_{Settings.EmbeddingProvider}".Translate();
            if (Widgets.ButtonText(providerBtnRect, currentProviderLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RimLLM_EmbeddingProvider_Offline_Trigram".Translate(), () => { Settings.EmbeddingProvider = "Offline_Trigram"; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_EmbeddingProvider_Google".Translate(), () => { Settings.EmbeddingProvider = "Google"; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_EmbeddingProvider_LocalAPI_Ollama".Translate(), () => { Settings.EmbeddingProvider = "LocalAPI_Ollama"; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_EmbeddingProvider_LocalAPI_OpenAI".Translate(), () => { Settings.EmbeddingProvider = "LocalAPI_OpenAI"; Settings.Write(); })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
            listing.Gap(6f);

            // 2. 根據供應商繪製細部屬性
            if (Settings.EmbeddingProvider != "Offline_Trigram")
            {
                listing.Label("RimLLM_EmbeddingModelLabel".Translate());
                Settings.EmbeddingModel = listing.TextEntry(Settings.EmbeddingModel);

                if (Settings.EmbeddingProvider == "LocalAPI_Ollama" || Settings.EmbeddingProvider == "LocalAPI_OpenAI")
                {
                    listing.Label("RimLLM_EmbeddingEndpointLabel".Translate());
                    Settings.EmbeddingEndpoint = listing.TextEntry(Settings.EmbeddingEndpoint);
                }

                listing.Label("RimLLM_EmbeddingApiKeyLabel".Translate());
                Settings.EmbeddingApiKey = listing.TextEntry(Settings.EmbeddingApiKey);
            }

            // 檢查是否有屬性異動以存檔
            if (prevProvider != Settings.EmbeddingProvider ||
                prevModel != Settings.EmbeddingModel ||
                prevEndpoint != Settings.EmbeddingEndpoint ||
                prevApiKey != Settings.EmbeddingApiKey)
            {
                Settings.Write();
            }
        }
    }
}
