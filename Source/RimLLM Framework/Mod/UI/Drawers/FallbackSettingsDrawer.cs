using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 負責 Fallback 鏈配置面板的 UI 渲染與互動狀態管理。
/// </summary>
public static class FallbackSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    /// <summary>
    /// 獲取 Fallback 設定詳細內容的滾動高度。本頁只剩路由策略與鏈條順序，新增模型已搬至模型設置頁。
    /// </summary>
    public static float GetHeight(float width)
    {
        int chainCount = Settings.FallbackChain.Count;
        return 150f + (chainCount * 36f);
    }

    /// <summary>
    /// 繪製 Fallback 鏈設定。
    /// </summary>
    public static void DrawFallbackSettings(Listing_Standard listing)
    {
        // 3. 智慧路由設定
        DrawRoutingStrategyButton(listing);
        listing.GapLine(10f);

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

        // 1. 繪製 Fallback 鏈列表（只管順序與增刪；新增模型改在模型設置頁）
        if (chain.Count == 0)
        {
            listing.Label("RimLLM_FallbackEmptyWarning".Translate());
        }
        else
        {
            for (int i = 0; i < chain.Count; i++)
            {
                if (DrawFallbackEntry(listing, chain, i)) break;
            }
        }
        listing.GapLine(10f);
    }

    /// <summary>
    /// 繪製單一 Fallback 鏈項目（順序標記、上移/下移、刪除）。
    /// 回傳 true 表示已進行順序變更或刪除，呼叫端應中止迴圈避免索引失效。
    /// </summary>
    private static bool DrawFallbackEntry(Listing_Standard listing, List<string> chain, int i)
    {
        string entry = chain[i];
        Rect itemRect = listing.GetRect(30f);

        // 左右劃分：本頁只管順序與增刪，模型級數值（分級、上下文上限）改在模型設置頁調整。
        Rect labelRect = new(itemRect.x, itemRect.y, itemRect.width - 120f, itemRect.height);
        Rect upRect = new(itemRect.x + itemRect.width - 110f, itemRect.y, 30f, itemRect.height);
        Rect downRect = new(itemRect.x + itemRect.width - 75f, itemRect.y, 30f, itemRect.height);
        Rect deleteRect = new(itemRect.x + itemRect.width - 40f, itemRect.y, 30f, itemRect.height);

        // 繪製順序標記與名稱
        Widgets.Label(labelRect, string.Create(CultureInfo.CurrentCulture, $" {i + 1}. <color=cyan>{entry}</color>"));

        // 上移、下移按鈕
        if (DrawMoveButton(upRect, "▲", chain, i, i - 1)) return true;
        if (DrawMoveButton(downRect, "▼", chain, i, i + 1)) return true;

        // 刪除按鈕
        if (Widgets.ButtonText(deleteRect, "X"))
        {
            Settings.RemoveFallbackEntry(chain[i]);
            Settings.Write();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 繪製把項目與 <paramref name="target"/> 位置互換的按鈕；目標超出範圍時畫成停用的灰色按鈕。
    /// 回傳 true 表示已互換並寫入設定。
    /// </summary>
    private static bool DrawMoveButton(Rect rect, string glyph, List<string> chain, int index, int target)
    {
        if (target < 0 || target >= chain.Count)
        {
            GUI.color = Color.gray;
            Widgets.ButtonText(rect, glyph);
            GUI.color = Color.white;
            return false;
        }

        if (!Widgets.ButtonText(rect, glyph)) return false;
        (chain[index], chain[target]) = (chain[target], chain[index]);
        Settings.FallbackChain = chain;
        Settings.Write();
        return true;
    }

    /// <summary>
    /// 繪製路由策略下拉按鈕。
    /// </summary>
    private static void DrawRoutingStrategyButton(Listing_Standard listing)
    {
        Rect routingRect = listing.GetRect(30f);
        float routingLabelWidth = Text.CalcSize("RimLLM_RoutingStrategyLabel".Translate()).x;
        Rect strategyLabelRect = new(routingRect.x, routingRect.y, routingLabelWidth + 5f, routingRect.height);
        Rect strategyBtnRect = new(routingRect.x + routingLabelWidth + 15f, routingRect.y, 220f, routingRect.height);

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(strategyLabelRect, "RimLLM_RoutingStrategyLabel".Translate());
        }

        if (Widgets.ButtonText(strategyBtnRect, StrategyLabelKey(Settings.RoutingStrategy).Translate()))
        {
            var options = new List<FloatMenuOption>();
            for (int strategy = 0; strategy < StrategyNames.Length; strategy++)
            {
                int captured = strategy;
                options.Add(new FloatMenuOption(
                    StrategyLabelKey(captured).Translate(),
                    () => { Settings.RoutingStrategy = captured; Settings.Write(); }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    /// <summary>
    /// 路由策略名稱，索引即為 <see cref="RimLLMFrameworkSettings.RoutingStrategy"/> 的值。
    /// 目前選項的顯示與下拉選單共用這份清單，不需要另外維護一份 switch 對照。
    /// </summary>
    private static readonly string[] StrategyNames = ["PriorityFailover", "MinLatency", "RoundRobin", "LowestCost"];

    private static string StrategyLabelKey(int strategy)
    {
        string name = strategy >= 0 && strategy < StrategyNames.Length ? StrategyNames[strategy] : StrategyNames[0];
        return "RimLLM_RoutingStrategy_" + name;
    }
}
