using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責逐模型設置頁的 UI 渲染：備援鏈上每個模型的分級覆寫與上下文上限手寫值。
    /// 備援鏈頁（<see cref="FallbackSettingsDrawer"/>）只保留順序與增刪，模型級的數值調整一律在這裡。
    /// 存檔結構不變（沿用既有的分級覆寫與上下文上限覆寫字典），因此舊存檔無需遷移。
    /// </summary>
    public static class ModelSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        /// <summary>
        /// 獲取模型設置詳細內容的滾動高度。
        /// </summary>
        public static int RowCount()
        {
            return CollectEntries().Count;
        }

        public static float GetHeight(float width)
        {
            return 120f + (RowCount() * 36f);
        }

        /// <summary>
        /// 備援鏈去空去重後的有序模型條目，供本頁逐列顯示。鏈內保證唯一，
        /// 但舊存檔可能殘留重複，去重後顯示才不會出現調了兩次的同一列。
        /// </summary>
        private static List<string> CollectEntries()
        {
            var entries = new List<string>();
            foreach (string entry in Settings.FallbackChain)
            {
                if (!string.IsNullOrEmpty(entry) && !entries.Contains(entry))
                {
                    entries.Add(entry);
                }
            }
            return entries;
        }

        /// <summary>
        /// 繪製模型設置。
        /// </summary>
        public static void DrawModelSettings(Listing_Standard listing)
        {
            listing.Label("RimLLM_ModelsExplanation".Translate());
            listing.Gap(8f);

            List<string> entries = CollectEntries();
            if (entries.Count == 0)
            {
                listing.Label("RimLLM_ModelsEmptyWarning".Translate());
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                string entry = entries[i];
                Rect itemRect = listing.GetRect(30f);

                int colonIndex = entry.IndexOf(':');
                bool hasModelName = colonIndex >= 0;
                float labelWidth = hasModelName ? (itemRect.width - 170f) : itemRect.width;
                Rect labelRect = new Rect(itemRect.x, itemRect.y, labelWidth, itemRect.height);
                Widgets.Label(labelRect, $" <color=cyan>{entry}</color>");

                // 純供應商條目沒有可調的模型級數值，只顯示名稱。
                if (!hasModelName) continue;

                string modelName = entry.Substring(colonIndex + 1);
                DrawLevelButton(
                    new Rect(itemRect.x + itemRect.width - 80f, itemRect.y, 70f, itemRect.height),
                    modelName);
                DrawContextWindowButton(
                    new Rect(itemRect.x + itemRect.width - 160f, itemRect.y, 70f, itemRect.height),
                    entry, entry.Substring(0, colonIndex), modelName);
            }
        }

        /// <summary>分級標籤鍵，索引即為覆寫值（0＝自動）。</summary>
        private static readonly string[] LevelLabelKeys =
        {
            "RimLLM_FallbackLevelAuto",
            "RimLLM_FallbackLevelLow",
            "RimLLM_FallbackLevelMedium",
            "RimLLM_FallbackLevelHigh"
        };

        private static void DrawLevelButton(Rect rect, string modelName)
        {
            // 分級只有自動／低／中／高四檔，以陣列對照取代 switch，新增等級時只改一處。
            int currentLevel = Settings.GetModelLevelOverride(modelName);
            int index = currentLevel >= 1 && currentLevel <= 3 ? currentLevel : 0;
            string levelLabel = LevelLabelKeys[index].Translate();

            if (Widgets.ButtonText(rect, levelLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RimLLM_FallbackLevelAuto".Translate(), () => { Settings.SetModelLevelOverride(modelName, 0); Settings.Write(); }),
                    new FloatMenuOption("RimLLM_FallbackLevelLow".Translate(), () => { Settings.SetModelLevelOverride(modelName, 1); Settings.Write(); }),
                    new FloatMenuOption("RimLLM_FallbackLevelMedium".Translate(), () => { Settings.SetModelLevelOverride(modelName, 2); Settings.Write(); }),
                    new FloatMenuOption("RimLLM_FallbackLevelHigh".Translate(), () => { Settings.SetModelLevelOverride(modelName, 3); Settings.Write(); })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        /// <summary>常用的上下文上限，讓玩家一鍵選取；其他數值走自訂視窗。</summary>
        private static readonly int[] ContextWindowPresets = { 8192, 32768, 131072, 200000, 1048576 };

        /// <summary>
        /// 繪製上下文上限按鈕：顯示目前生效的值（手動值優先於 API 回報值），點擊可手動設定或改回自動。
        /// </summary>
        private static void DrawContextWindowButton(Rect rect, string entry, string providerId, string modelName)
        {
            int manual = Settings.GetContextWindowOverride(entry);
            int? fetched = Settings.GetFetchedContextWindow(providerId, modelName);
            int? effective = manual > 0 ? manual : fetched;

            string label = effective.HasValue ? FormatTokens(effective.Value) : "?";
            if (manual > 0) label += "*";

            // 提示文字只在滑鼠停留時才需要，以延遲委派產生，避免每幀每列都翻譯與格式化
            TooltipHandler.TipRegion(rect, () =>
            {
                string source = manual > 0
                    ? "RimLLM_ContextWindowSourceManual".Translate()
                    : fetched.HasValue
                        ? "RimLLM_ContextWindowSourceApi".Translate()
                        : "RimLLM_ContextWindowSourceNone".Translate();
                return "RimLLM_ContextWindowTooltip".Translate(
                    effective.HasValue ? effective.Value.ToString("N0") : "?", source);
            }, entry.GetHashCode());

            if (Widgets.ButtonText(rect, label))
            {
                var options = new List<FloatMenuOption>
                {
                    new FloatMenuOption(
                        "RimLLM_ContextWindowAuto".Translate(fetched.HasValue ? fetched.Value.ToString("N0") : "?"),
                        () => { Settings.SetContextWindowOverride(entry, 0); Settings.Write(); })
                };
                foreach (int preset in ContextWindowPresets)
                {
                    int captured = preset;
                    options.Add(new FloatMenuOption(
                        FormatTokens(captured) + " (" + captured.ToString("N0") + ")",
                        () => { Settings.SetContextWindowOverride(entry, captured); Settings.Write(); }));
                }
                options.Add(new FloatMenuOption("RimLLM_ContextWindowCustom".Translate(), () =>
                    Find.WindowStack.Add(new Dialog_SetContextWindow(entry, manual, tokens =>
                    {
                        Settings.SetContextWindowOverride(entry, tokens);
                        Settings.Write();
                    }))));
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }

        /// <summary>以 K / M 縮寫 token 數，例如 131072 → 131K、1048576 → 1M。</summary>
        private static string FormatTokens(int tokens)
        {
            return tokens >= 1000000
                ? (tokens / 1000000.0).ToString("0.#") + "M"
                : tokens >= 1000
                    ? (tokens / 1000.0).ToString("0") + "K"
                    : tokens.ToString();
        }
    }
}
