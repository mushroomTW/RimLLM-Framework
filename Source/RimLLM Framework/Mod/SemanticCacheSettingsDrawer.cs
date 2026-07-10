using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責「語意快取」設定分頁的 UI 渲染。
    /// </summary>
    public static class SemanticCacheSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        public static float GetHeight(float width)
        {
            // 如果不是離線模式，需要額外顯示自訂模型與端點的輸入框，所以高度動態調整
            float baseHeight = 230f;
            if (Settings.EmbeddingProvider != "Offline_Trigram")
            {
                baseHeight += 120f;
            }
            baseHeight += 190f; // 統計資料高度與快取圖形化進度條
            return baseHeight;
        }

        public static void DrawSemanticCacheSettings(Listing_Standard listing)
        {
            bool prevEnable = Settings.EnableSemanticCache;
            float prevThreshold = Settings.SemanticCacheThreshold;
            int prevMaxCount = Settings.SemanticCacheMaxCount;
            string prevProvider = Settings.EmbeddingProvider;
            string prevModel = Settings.EmbeddingModel;
            string prevEndpoint = Settings.EmbeddingEndpoint;
            string prevApiKey = Settings.EmbeddingApiKey;

            // 1. 全域語意快取開關
            bool enableCache = Settings.EnableSemanticCache;
            listing.CheckboxLabeled("RimLLM_EnableSemanticCacheLabel".Translate(), ref enableCache);
            Settings.EnableSemanticCache = enableCache;
            listing.Gap(6f);

            // 2. 相似度匹配閾值
            listing.Label("RimLLM_SemanticCacheThresholdLabel".Translate(Settings.SemanticCacheThreshold.ToString("F2")));
            Settings.SemanticCacheThreshold = listing.Slider(Settings.SemanticCacheThreshold, 0.80f, 0.99f);

            // 3. 快取最大條數上限
            float estimatedMB = Settings.SemanticCacheMaxCount * 12f / 1024f; // 假設每條約 12KB
            listing.Label("RimLLM_SemanticCacheMaxCountLabel".Translate(Settings.SemanticCacheMaxCount, estimatedMB.ToString("F2")));
            float maxCountVal = listing.Slider((float)Settings.SemanticCacheMaxCount, 10f, 1000f);
            Settings.SemanticCacheMaxCount = Mathf.RoundToInt(maxCountVal);
            listing.GapLine(10f);

            // 4. Embedding 供應商設定
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

            // 5. 根據供應商繪製細部屬性
            if (Settings.EmbeddingProvider != "Offline_Trigram")
            {
                // 模型名稱輸入框
                listing.Label("RimLLM_EmbeddingModelLabel".Translate());
                Settings.EmbeddingModel = listing.TextEntry(Settings.EmbeddingModel);

                // 端點與 API Key 輸入框
                if (Settings.EmbeddingProvider == "LocalAPI_Ollama" || Settings.EmbeddingProvider == "LocalAPI_OpenAI")
                {
                    listing.Label("RimLLM_EmbeddingEndpointLabel".Translate());
                    Settings.EmbeddingEndpoint = listing.TextEntry(Settings.EmbeddingEndpoint);
                }

                listing.Label("RimLLM_EmbeddingApiKeyLabel".Translate());
                Settings.EmbeddingApiKey = listing.TextEntry(Settings.EmbeddingApiKey);
            }
            listing.GapLine(10f);

            // 6. 快取統計指標顯示
            listing.Label($"<b>{"RimLLM_CacheStatsLabel".Translate()}</b>");
            
            int count = 0;
            int hits = 0;
            int misses = 0;
            long saved = 0;

            if (RimLLMProvider.Instance is RimLLMManager manager)
            {
                count = manager.SemanticCache.CacheCount;
                hits = manager.SemanticCache.CacheHits;
                misses = manager.SemanticCache.CacheMisses;
                saved = manager.SemanticCache.EstTokensSaved;
            }

            float hitRate = 0f;
            int totalRequests = hits + misses;
            if (totalRequests > 0)
            {
                hitRate = (hits * 100f) / totalRequests;
            }

            listing.Label("RimLLM_CacheTotalCountLabel".Translate(count, Settings.SemanticCacheMaxCount));
            listing.Label("RimLLM_CacheHitsLabel".Translate(hits));
            listing.Label("RimLLM_CacheMissesLabel".Translate(misses));
            listing.Label("RimLLM_CacheHitRateLabel".Translate(hitRate.ToString("F1")));

            // 繪製可視化命中率條
            Rect barRect = listing.GetRect(20f);
            Widgets.DrawBoxSolid(barRect, new Color(0.2f, 0.2f, 0.2f, 0.6f));
            if (totalRequests > 0)
            {
                float fillPercent = (float)hits / totalRequests;
                if (fillPercent > 0f)
                {
                    Rect fillRect = new Rect(barRect.x, barRect.y, barRect.width * fillPercent, barRect.height);
                    Widgets.DrawBoxSolid(fillRect, new Color(0.18f, 0.48f, 0.18f, 0.8f));
                }
            }
            Widgets.DrawBox(barRect, 1);

            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = GameFont.Tiny;
            Widgets.Label(barRect, totalRequests > 0 ? $"{hitRate:F1}%" : "0.0%");
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            listing.Gap(4f);

            listing.Label("RimLLM_EstTokensSavedLabel".Translate(saved.ToString("N0")));
            listing.Gap(6f);

            if (listing.ButtonText("RimLLM_ClearCacheBtn".Translate()))
            {
                if (RimLLMProvider.Instance is RimLLMManager mgr)
                {
                    mgr.SemanticCache.ClearCache();
                    Messages.Message("RimLLM_MsgCacheCleared".Translate(), MessageTypeDefOf.PositiveEvent, false);
                }
            }

            // 檢查是否有屬性異動以存檔
            if (prevEnable != Settings.EnableSemanticCache ||
                Math.Abs(prevThreshold - Settings.SemanticCacheThreshold) > 0.001f ||
                prevMaxCount != Settings.SemanticCacheMaxCount ||
                prevProvider != Settings.EmbeddingProvider ||
                prevModel != Settings.EmbeddingModel ||
                prevEndpoint != Settings.EmbeddingEndpoint ||
                prevApiKey != Settings.EmbeddingApiKey)
            {
                Settings.Write();
            }
        }
    }
}
