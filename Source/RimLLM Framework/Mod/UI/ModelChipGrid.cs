using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 決策與 Embedding 設定頁共用的「已抓取模型」區塊：搜尋列、模型晶片網格與點擊選單。
/// 兩頁只差在提示文字與「設為模型」寫入哪個欄位，其餘版面與行為必須一致。
/// </summary>
internal static class ModelChipGrid
{
    private const float ChipHeight = 28f;
    private const float ChipGap = 8f;
    private const float MinChipWidth = 220f;

    /// <summary>
    /// 繪製提示文字與搜尋列，輸入值寫回 <paramref name="filters"/>[<paramref name="providerId"/>]。
    /// </summary>
    internal static void DrawFilterBar(Listing_Standard listing, string hintKey, Dictionary<string, string> filters, string providerId)
    {
        Rect hintRect = listing.GetRect(18f);
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Color oldColor = GUI.color;
            GUI.color = RimLLMUIStyle.Success;
            Widgets.Label(hintRect, hintKey.Translate());
            GUI.color = oldColor;
        }
        listing.Gap(2f);

        string filter = filters.TryGetValue(providerId, out string current) ? current : "";
        Rect searchRowRect = listing.GetRect(28f);
        string searchLabel = "RimLLM_Search".Translate() + ": ";
        float searchLabelWidth = Text.CalcSize(searchLabel).x;
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(new Rect(searchRowRect.x, searchRowRect.y, searchLabelWidth, searchRowRect.height), searchLabel);
        }

        Rect searchFieldRect = new(
            searchRowRect.x + searchLabelWidth + 4f,
            searchRowRect.y,
            searchRowRect.width - searchLabelWidth - 4f,
            searchRowRect.height);
        filters[providerId] = Widgets.TextField(searchFieldRect, filter);
        listing.Gap(4f);
    }

    /// <summary>
    /// 在可捲動區域內以晶片網格列出模型，只畫可視範圍內的列。
    /// 捲動位置逐供應商保存在 <paramref name="scrollPositions"/>。
    /// </summary>
    internal static void DrawGrid(
        Rect scrollRect,
        string providerId,
        List<string> visibleModels,
        Dictionary<string, Vector2> scrollPositions,
        Action<string> onModelClicked)
    {
        float contentWidth = scrollRect.width - 16f;
        RimLLMUIStyle.ComputeChipLayout(contentWidth, ChipGap, MinChipWidth, out int cols, out float chipWidth);

        int rows = Mathf.CeilToInt((float)visibleModels.Count / cols);
        float viewHeight = Mathf.Max(200f, (rows * (ChipHeight + ChipGap)) + ChipGap);
        Rect viewRect = new(0f, 0f, contentWidth, viewHeight);

        Vector2 scrollPos = scrollPositions.TryGetValue(providerId, out Vector2 saved) ? saved : Vector2.zero;
        Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);
        scrollPositions[providerId] = scrollPos;

        // tooltip 後綴的翻譯提到迴圈外，不必每個晶片各查一次。
        RimLLMUIStyle.GetVisibleRowRange(scrollPos.y, scrollRect.height, ChipHeight + ChipGap, ChipGap, rows, out int firstRow, out int endRow);
        string copyHint = "\n\n" + "RimLLM_ClickToCopy".Translate();

        for (int i = firstRow * cols; i < visibleModels.Count && i < endRow * cols; i++)
        {
            string model = visibleModels[i];
            int col = i % cols;
            int row = i / cols;

            Rect chipRect = new(
                (col * (chipWidth + ChipGap)) + ChipGap,
                (row * (ChipHeight + ChipGap)) + ChipGap,
                chipWidth,
                ChipHeight);

            Widgets.DrawBoxSolid(chipRect, RimLLMUIStyle.ChipFill);
            Widgets.DrawBox(chipRect, 1);

            if (Mouse.IsOver(chipRect))
            {
                Widgets.DrawHighlight(chipRect);
                TooltipHandler.TipRegion(chipRect, model + copyHint);
            }

            if (Widgets.ButtonInvisible(chipRect))
            {
                onModelClicked(model);
            }

            Rect textRect = chipRect.ContractedBy(4f);
            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny, wordWrap: false))
            {
                Widgets.Label(textRect, "<color=silver>" + RimLLMUIStyle.TruncateCached(model, textRect.width) + "</color>");
            }
        }

        Widgets.EndScrollView();
    }

    /// <summary>
    /// 模型晶片的點擊選單：「設為模型」與「複製名稱」。
    /// </summary>
    /// <param name="model">被點擊的模型名稱。</param>
    /// <param name="setAsLabelKey">「設為模型」選項的翻譯鍵。</param>
    /// <param name="modelLabelKey">設定完成訊息的前綴翻譯鍵。</param>
    /// <param name="applyModel">把模型寫入設定（含存檔）的動作。</param>
    internal static void ShowModelMenu(string model, string setAsLabelKey, string modelLabelKey, Action<string> applyModel)
    {
        var options = new List<FloatMenuOption>
        {
            new(setAsLabelKey.Translate(), () =>
            {
                applyModel(model);
                Messages.Message(modelLabelKey.Translate() + model, MessageTypeDefOf.TaskCompletion, false);
            }),
            new("RimLLM_ClickToCopyMenu".Translate(), () =>
            {
                GUIUtility.systemCopyBuffer = model;
                Messages.Message("RimLLM_CopiedToClipboard".Translate(model), MessageTypeDefOf.TaskCompletion, false);
            })
        };
        Find.WindowStack.Add(new FloatMenu(options));
    }
}
