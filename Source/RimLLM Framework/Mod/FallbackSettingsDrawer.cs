using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責 Fallback 鏈配置面板的 UI 渲染與互動狀態管理。
    /// </summary>
    public static class FallbackSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        // Fallback 分頁專屬的 UI 暫存狀態
        private static string addProviderId = "Gemini";
        private static string addModelName = "";

        /// <summary>
        /// 獲取 Fallback 設定詳細內容的滾動高度。
        /// </summary>
        public static float GetHeight(float width)
        {
            int chainCount = Settings.FallbackChain.Count;
            return 150f + (chainCount * 36f) + 180f;
        }

        /// <summary>
        /// 繪製 Fallback 鏈設定。
        /// </summary>
        public static void DrawFallbackSettings(Listing_Standard listing)
        {
            listing.Label("RimLLM_FallbackExplanation".Translate());
            listing.Gap(8f);
            var chain = Settings.FallbackChain;
            int originalCount = chain.Count;
            chain.RemoveAll(entry => string.IsNullOrEmpty(entry));
            if (chain.Count != originalCount)
            {
                Settings.FallbackChain = chain;
                Settings.Write();
            }

            // 確保 addProviderId 是已啟用的供應商（如果有啟用的話）
            if (!Settings.IsProviderEnabled(addProviderId))
            {
                string firstEnabled = null;
                foreach (string prov in new string[] { "Gemini", "OpenAI", "DeepSeek", "Groq", "Anthropic", "OpenRouter", "Kimi", "MiniMax", "Qwen", "Nvidia", "OpenAICompatible" })
                {
                    if (Settings.IsProviderEnabled(prov))
                    {
                        firstEnabled = prov;
                        break;
                    }
                }
                if (firstEnabled != null)
                {
                    addProviderId = firstEnabled;
                    addModelName = ""; // 重設模型名稱以重新加載預設值
                }
            }

            // 確保 addModelName 已經初始化
            if (string.IsNullOrEmpty(addModelName))
            {
                SetDefaultAddModelName(addProviderId);
            }

            // 1. 繪製 Fallback 鏈列表
            if (chain.Count == 0)
            {
                listing.Label("RimLLM_FallbackEmptyWarning".Translate());
            }
            else
            {
                for (int i = 0; i < chain.Count; i++)
                {
                    string entry = chain[i];
                    Rect itemRect = listing.GetRect(30f);

                    // 左右劃分
                    Rect labelRect = new Rect(itemRect.x, itemRect.y, itemRect.width - 120f, itemRect.height);
                    Rect upRect = new Rect(itemRect.x + itemRect.width - 110f, itemRect.y, 30f, itemRect.height);
                    Rect downRect = new Rect(itemRect.x + itemRect.width - 75f, itemRect.y, 30f, itemRect.height);
                    Rect deleteRect = new Rect(itemRect.x + itemRect.width - 40f, itemRect.y, 30f, itemRect.height);

                    // 繪製順序標記與名稱
                    Widgets.Label(labelRect, $" {i + 1}. <color=cyan>{entry}</color>");

                    // 上移按鈕
                    if (i > 0)
                    {
                        if (Widgets.ButtonText(upRect, "▲"))
                        {
                            string temp = chain[i];
                            chain[i] = chain[i - 1];
                            chain[i - 1] = temp;
                            Settings.FallbackChain = chain;
                            Settings.Write();
                            break;
                        }
                    }
                    else
                    {
                        GUI.color = Color.gray;
                        Widgets.ButtonText(upRect, "▲");
                        GUI.color = Color.white;
                    }

                    // 下移按鈕
                    if (i < chain.Count - 1)
                    {
                        if (Widgets.ButtonText(downRect, "▼"))
                        {
                            string temp = chain[i];
                            chain[i] = chain[i + 1];
                            chain[i + 1] = temp;
                            Settings.FallbackChain = chain;
                            Settings.Write();
                            break;
                        }
                    }
                    else
                    {
                        GUI.color = Color.gray;
                        Widgets.ButtonText(downRect, "▼");
                        GUI.color = Color.white;
                    }

                    // 刪除按鈕
                    if (Widgets.ButtonText(deleteRect, "X"))
                    {
                        chain.RemoveAt(i);
                        Settings.FallbackChain = chain;
                        Settings.Write();
                        break;
                    }
                }
            }
            listing.GapLine(10f);

            // 2. 新增項目區域
            listing.Label("RimLLM_AddToFallbackTitle".Translate());

            // 2.1 選擇供應商
            Rect addRect = listing.GetRect(30f);
            Rect addProvBtn = new Rect(addRect.x, addRect.y, 150f, addRect.height);
            Rect addModBtn = new Rect(addRect.x + 160f, addRect.y, 250f, addRect.height);
            Rect addSubmitBtn = new Rect(addRect.x + 420f, addRect.y, 100f, addRect.height);
            if (Widgets.ButtonText(addProvBtn, "RimLLM_SelectProviderBtn".Translate(addProviderId)))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                if (Settings.IsProviderEnabled("Gemini")) options.Add(new FloatMenuOption("Gemini", () => SetDefaultAddModelName("Gemini")));
                if (Settings.IsProviderEnabled("OpenAI")) options.Add(new FloatMenuOption("OpenAI", () => SetDefaultAddModelName("OpenAI")));
                if (Settings.IsProviderEnabled("DeepSeek")) options.Add(new FloatMenuOption("DeepSeek", () => SetDefaultAddModelName("DeepSeek")));
                if (Settings.IsProviderEnabled("Groq")) options.Add(new FloatMenuOption("Groq", () => SetDefaultAddModelName("Groq")));
                if (Settings.IsProviderEnabled("Anthropic")) options.Add(new FloatMenuOption("Anthropic", () => SetDefaultAddModelName("Anthropic")));
                if (Settings.IsProviderEnabled("OpenRouter")) options.Add(new FloatMenuOption("OpenRouter", () => SetDefaultAddModelName("OpenRouter")));
                if (Settings.IsProviderEnabled("Kimi")) options.Add(new FloatMenuOption("Kimi", () => SetDefaultAddModelName("Kimi")));
                if (Settings.IsProviderEnabled("MiniMax")) options.Add(new FloatMenuOption("MiniMax", () => SetDefaultAddModelName("MiniMax")));
                if (Settings.IsProviderEnabled("Qwen")) options.Add(new FloatMenuOption("Qwen", () => SetDefaultAddModelName("Qwen")));
                if (Settings.IsProviderEnabled("Nvidia")) options.Add(new FloatMenuOption("Nvidia", () => SetDefaultAddModelName("Nvidia")));
                if (Settings.IsProviderEnabled("OpenAICompatible")) options.Add(new FloatMenuOption("OpenAICompatible", () => SetDefaultAddModelName("OpenAICompatible")));

                if (options.Count == 0)
                {
                    options.Add(new FloatMenuOption("RimLLM_NoEnabledProviders".Translate(), null));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // 2.2 選擇該供應商底下的快取模型
            var models = Settings.GetModelList(addProviderId);
            string modelBtnLabel = string.IsNullOrEmpty(addModelName) ? "default" : addModelName;

            if (Widgets.ButtonText(addModBtn, "RimLLM_SelectModelBtn".Translate(modelBtnLabel)))
            {
                Find.WindowStack.Add(new Dialog_SelectModel(models, (selectedM) => addModelName = selectedM));
            }

            // 2.3 點擊新增
            if (Widgets.ButtonText(addSubmitBtn, "RimLLM_AddBtn".Translate()))
            {
                string entry = $"{addProviderId}:{addModelName}";
                if (chain.Contains(entry))
                {
                    Messages.Message("RimLLM_MsgModelExists".Translate(), MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    chain.Add(entry);
                    Settings.FallbackChain = chain;
                    Settings.Write();
                    Messages.Message("RimLLM_MsgModelAdded".Translate(entry), MessageTypeDefOf.PositiveEvent, false);
                }
            }
            listing.GapLine(10f);
        }

        private static void SetDefaultAddModelName(string providerId)
        {
            addProviderId = providerId;
            var models = Settings.GetModelList(providerId);
            if (models != null && models.Count > 0)
            {
                addModelName = models[0];
            }
            else
            {
                addModelName = Settings.GetDefaultModel(providerId, "default");
            }
        }
    }
}
