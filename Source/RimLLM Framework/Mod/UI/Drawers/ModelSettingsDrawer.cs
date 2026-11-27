using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>逐模型設置頁：備援鏈上每個模型的分級覆寫與上下文上限手寫值，以及新增模型至備援鏈。</summary>
public static class ModelSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private static string addProviderId = ProviderIds.Gemini;
    private static string addModelName = "";

    /// <summary>備援鏈上的模型列數。</summary>
    public static int RowCount()
    {
        return CollectEntries().Count;
    }

    public static float GetHeight(float width)
    {
        return 280f + (RowCount() * 36f);
    }

    /// <summary>備援鏈去空去重後的有序模型條目；舊存檔可能殘留重複，去重後才不會出現調了兩次的同一列。</summary>
    internal static List<string> CollectEntries()
    {
        return [.. Settings.FallbackChain.Where(entry => !string.IsNullOrEmpty(entry)).Distinct(System.StringComparer.Ordinal)];
    }

    public static void DrawModelSettings(Listing_Standard listing)
    {
        listing.Label("RimLLM_ModelsExplanation".Translate());
        listing.Gap(8f);

        List<string> entries = CollectEntries();
        if (entries.Count == 0)
        {
            listing.Label("RimLLM_ModelsEmptyWarning".Translate());
        }
        else
        {
            string? entryToRemove = null;
            for (int i = 0; i < entries.Count; i++)
            {
                string entry = entries[i];
                Rect itemRect = listing.GetRect(30f);

                if (Widgets.ButtonText(new Rect(itemRect.x + itemRect.width - 40f, itemRect.y, 30f, itemRect.height), "X"))
                {
                    entryToRemove = entry;
                }

                bool hasModelName = ProviderIds.TrySplitModelId(entry, out string? providerId, out string? modelName);
                float labelWidth = hasModelName ? (itemRect.width - 200f) : (itemRect.width - 50f);
                Rect labelRect = new(itemRect.x, itemRect.y, labelWidth, itemRect.height);
                Widgets.Label(labelRect, $" <color=cyan>{entry}</color>");

                // 純供應商條目沒有可調的模型級數值，只顯示名稱。
                if (!hasModelName) continue;

                DrawLevelButton(
                    new Rect(itemRect.x + itemRect.width - 115f, itemRect.y, 70f, itemRect.height),
                    modelName ?? string.Empty);
                DrawContextWindowButton(
                    new Rect(itemRect.x + itemRect.width - 190f, itemRect.y, 70f, itemRect.height),
                    entry ?? string.Empty, providerId ?? string.Empty, modelName ?? string.Empty);
            }

            if (entryToRemove != null)
            {
                // 刪除會連同該模型調過的分級／上下文上限一起清掉，先確認避免誤觸
                string removed = entryToRemove;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "RimLLM_ConfirmRemoveModel".Translate(removed),
                    () =>
                    {
                        Settings.RemoveFallbackEntry(removed);
                        Settings.Write();
                    },
                    destructive: true));
            }
        }
        listing.GapLine(10f);

        DrawAddModelSection(listing);
    }

    private static List<string> GetRegisteredProviderIds()
    {
        return RimLLMProvider.TryGetManager(out var manager)
            ? manager.GetRegisteredProviderIds()
            : new List<string>(ProviderIds.BuiltIn);
    }

    /// <summary>供應商在 UI 中是否可選：內建依設定啟用狀態，外部供應商註冊即啟用。</summary>
    private static bool IsProviderSelectable(string providerId)
    {
        return RimLLMProvider.TryGetManager(out var manager)
            ? manager.IsProviderEnabled(providerId)
            : Settings.IsProviderEnabled(providerId);
    }

    private static void SelectAddProvider(string providerId)
    {
        addProviderId = providerId;
        // 沒有預設模型：換供應商就清空，由玩家從清單自己選。
        addModelName = "";
    }

    private static void EnsureActiveProvider()
    {
        if (!IsProviderSelectable(addProviderId))
        {
            string firstEnabled = GetRegisteredProviderIds().FirstOrDefault(IsProviderSelectable);
            if (firstEnabled != null)
            {
                SelectAddProvider(firstEnabled);
            }
        }
    }

    private static void ShowProviderSelectionMenu()
    {
        List<FloatMenuOption> options = [.. GetRegisteredProviderIds()
            .Where(IsProviderSelectable)
            .Select(prov => new FloatMenuOption(prov, () => SelectAddProvider(prov)))];

        if (options.Count == 0)
        {
            options.Add(new FloatMenuOption("RimLLM_NoEnabledProviders".Translate(), action: null));
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }

    private static void TryAddModelToChain()
    {
        if (string.IsNullOrEmpty(addModelName))
        {
            Messages.Message("RimLLM_MsgSelectModelFirst".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        string entry = $"{addProviderId}:{addModelName}";
        var chain = Settings.FallbackChain;
        if (chain.Contains(entry))
        {
            Messages.Message("RimLLM_MsgModelExists".Translate(), MessageTypeDefOf.RejectInput, historical: false);
        }
        else
        {
            chain.Add(entry);
            Settings.FallbackChain = chain;
            Settings.Write();
            Messages.Message("RimLLM_MsgModelAdded".Translate(entry), MessageTypeDefOf.PositiveEvent, historical: false);
        }
    }

    private static void DrawAddModelSection(Listing_Standard listing)
    {
        EnsureActiveProvider();

        listing.Label("RimLLM_AddToFallbackTitle".Translate());

        Rect addRect = listing.GetRect(30f);
        Rect addProvBtn = new(addRect.x, addRect.y, 150f, addRect.height);
        Rect addModBtn = new(addRect.x + 160f, addRect.y, 250f, addRect.height);
        Rect addSubmitBtn = new(addRect.x + 420f, addRect.y, 100f, addRect.height);
        if (Widgets.ButtonText(addProvBtn, "RimLLM_SelectProviderBtn".Translate(addProviderId)))
        {
            ShowProviderSelectionMenu();
        }

        string modelBtnLabel = string.IsNullOrEmpty(addModelName) ? "RimLLM_SelectModelPlaceholder".Translate().ToString() : addModelName;

        if (Widgets.ButtonText(addModBtn, "RimLLM_SelectModelBtn".Translate(modelBtnLabel)))
        {
            var models = Settings.GetModelList(addProviderId);
            Find.WindowStack.Add(new Dialog_SelectModel(models, (selectedM) => addModelName = selectedM, addProviderId));
        }

        if (Widgets.ButtonText(addSubmitBtn, "RimLLM_AddBtn".Translate()))
        {
            TryAddModelToChain();
        }
        listing.GapLine(10f);
    }

    /// <summary>分級標籤鍵，索引即為覆寫值（0＝自動）。</summary>
    private static readonly string[] LevelLabelKeys =
    [
        "RimLLM_FallbackLevelAuto",
        "RimLLM_FallbackLevelLow",
        "RimLLM_FallbackLevelMedium",
        "RimLLM_FallbackLevelHigh"
    ];

    private static void DrawLevelButton(Rect rect, string modelName)
    {
        int currentLevel = Settings.GetModelLevelOverride(modelName);
        int index = currentLevel >= 1 && currentLevel <= 3 ? currentLevel : 0;
        string levelLabel = LevelLabelKeys[index].Translate();
        string levelModel = modelName;

        if (Widgets.ButtonText(rect, levelLabel))
        {
            List<FloatMenuOption> options =
            [
                new("RimLLM_FallbackLevelAuto".Translate(), () => { Settings.SetModelLevelOverride(levelModel, 0); Settings.Write(); }),
                new("RimLLM_FallbackLevelLow".Translate(), () => { Settings.SetModelLevelOverride(levelModel, 1); Settings.Write(); }),
                new("RimLLM_FallbackLevelMedium".Translate(), () => { Settings.SetModelLevelOverride(levelModel, 2); Settings.Write(); }),
                new("RimLLM_FallbackLevelHigh".Translate(), () => { Settings.SetModelLevelOverride(levelModel, 3); Settings.Write(); })
            ];
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    private static readonly int[] ContextWindowPresets = [131072, 200000, 262144, 1048576];

    /// <summary>繪製上下文上限按鈕：顯示目前生效的值（手動值優先於 API 回報值）。</summary>
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
            string source;
            if (manual > 0)
            {
                source = "RimLLM_ContextWindowSourceManual".Translate();
            }
            else if (fetched.HasValue)
            {
                source = "RimLLM_ContextWindowSourceApi".Translate();
            }
            else
            {
                source = "RimLLM_ContextWindowSourceNone".Translate();
            }

            return "RimLLM_ContextWindowTooltip".Translate(
                effective.HasValue ? effective.Value.ToString("N0", CultureInfo.InvariantCulture) : "?", source);
        }, System.StringComparer.Ordinal.GetHashCode(entry));

        if (Widgets.ButtonText(rect, label))
        {
            var options = new List<FloatMenuOption>
            {
                new(
                    "RimLLM_ContextWindowAuto".Translate(fetched.HasValue ? fetched.Value.ToString("N0", CultureInfo.InvariantCulture) : "?"),
                    () => { Settings.SetContextWindowOverride(entry, 0); Settings.Write(); })
            };
            foreach (int preset in ContextWindowPresets)
            {
                int captured = preset;
                options.Add(new FloatMenuOption(
                    FormatTokens(captured) + " (" + captured.ToString("N0", CultureInfo.InvariantCulture) + ")",
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

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1000000)
        {
            return (tokens / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }
        if (tokens >= 1000)
        {
            return (tokens / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "K";
        }
        return tokens.ToString(CultureInfo.InvariantCulture);
    }
}
