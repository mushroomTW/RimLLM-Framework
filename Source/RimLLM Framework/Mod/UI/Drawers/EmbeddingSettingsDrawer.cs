using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責「Embedding 向量」設定分頁的 UI 渲染。
    /// Embedding 供第三方 Mod 透過 RimLLMProvider.CreateEmbeddingGenerator(modId) 取用。
    /// </summary>
    public static class EmbeddingSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        /// <summary>模型清單抓取的忙碌旗標與狀態訊息。與供應商分頁各自獨立。</summary>
        private static bool isFetchingModels;
        private static string fetchStatus = "";

        public static float GetHeight(float width)
        {
            float baseHeight = 120f;
            if (Settings.EmbeddingProvider != "Disabled")
            {
                // 模型名稱與 API 金鑰輸入框
                baseHeight += 120f;

                // 抓取按鈕列與從清單挑選的按鈕列
                baseHeight += 72f;

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
                    new FloatMenuOption("RimLLM_EmbeddingProvider_Disabled".Translate(), () => { Settings.EmbeddingProvider = "Disabled"; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_EmbeddingProvider_Google".Translate(), () => { Settings.EmbeddingProvider = "Google"; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_EmbeddingProvider_LocalAPI_Ollama".Translate(), () => { Settings.EmbeddingProvider = "LocalAPI_Ollama"; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_EmbeddingProvider_LocalAPI_OpenAI".Translate(), () => { Settings.EmbeddingProvider = "LocalAPI_OpenAI"; Settings.Write(); })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
            listing.Gap(6f);

            // 2. 根據供應商繪製細部屬性
            if (Settings.EmbeddingProvider != "Disabled")
            {
                listing.Label("RimLLM_EmbeddingModelLabel".Translate());
                Settings.EmbeddingModel = listing.TextEntry(Settings.EmbeddingModel);

                DrawModelListControls(listing);

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

        /// <summary>
        /// 繪製「抓取模型清單」按鈕與「從清單挑選」下拉。
        /// 模型名稱仍保留手動輸入 —— 本地伺服器可能沒有 /v1/models，或使用者要用清單外的別名。
        /// </summary>
        private static void DrawModelListControls(Listing_Standard listing)
        {
            Rect rowRect = listing.GetRect(30f);
            Rect fetchBtnRect = new Rect(rowRect.x, rowRect.y, 200f, rowRect.height);
            Rect statusRect = new Rect(rowRect.x + 210f, rowRect.y, rowRect.width - 210f, rowRect.height);

            if (isFetchingModels)
            {
                Widgets.Label(fetchBtnRect, "RimLLM_Fetching".Translate());
            }
            else if (Widgets.ButtonText(fetchBtnRect, "RimLLM_FetchModelsBtn".Translate()))
            {
                StartFetchEmbeddingModels();
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(statusRect, fetchStatus);
            Text.Anchor = TextAnchor.UpperLeft;

            listing.Gap(6f);

            List<string> cached = Settings.GetModelList(RimLLMEmbeddingService.GetModelListKey(Settings.EmbeddingProvider));
            Rect pickRect = listing.GetRect(30f);
            Rect pickBtnRect = new Rect(pickRect.x, pickRect.y, 200f, pickRect.height);

            if (cached.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(pickRect, "RimLLM_EmbeddingNoModelList".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            if (Widgets.ButtonText(pickBtnRect, "RimLLM_EmbeddingSelectModelBtn".Translate(cached.Count)))
            {
                var options = new List<FloatMenuOption>();
                foreach (string model in cached)
                {
                    string captured = model;
                    options.Add(new FloatMenuOption(captured, () =>
                    {
                        Settings.EmbeddingModel = captured;
                        Settings.Write();
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        /// <summary>
        /// 於背景抓取模型清單，並把寫入設定的收尾動作交回主線程執行。
        /// </summary>
        private static void StartFetchEmbeddingModels()
        {
            string provider = Settings.EmbeddingProvider;
            isFetchingModels = true;
            fetchStatus = "RimLLM_Fetching".Translate();

            Task.Run(async () =>
            {
                Func<string> applyResult;
                try
                {
                    List<string> models = await new RimLLMEmbeddingService(Settings)
                        .FetchAvailableModelsAsync()
                        .ConfigureAwait(false);

                    applyResult = () =>
                    {
                        if (models == null || models.Count == 0)
                        {
                            return "RimLLM_FetchSuccessEmpty".Translate();
                        }
                        Settings.SetModelList(RimLLMEmbeddingService.GetModelListKey(provider), models);
                        Settings.Write();
                        return "RimLLM_FetchSuccessCount".Translate(models.Count);
                    };
                }
                catch (Exception ex)
                {
                    string message = "RimLLM_FetchFailed".Translate() +
                        " (" + RimLLMLog.SanitizeForLog(ex.Message, 220) + ")";
                    applyResult = () => message;
                }

                RimLLMDispatcher.EnqueueOnMainThread(() =>
                {
                    isFetchingModels = false;
                    fetchStatus = applyResult();
                });
            });
        }
    }
}
