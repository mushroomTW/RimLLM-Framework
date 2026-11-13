using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// Embedding 與決策設定頁共用的備援鏈控制項：目前選取的供應商可勾選「加入備援鏈」，
/// 並用上下箭頭調整嘗試順序；上方一行摘要顯示整條鏈。
/// 兩個頁面的供應商清單來源不同，但備援鏈都只是「供應商識別碼的有序清單」，所以共用同一份繪製。
/// </summary>
internal static class ProviderFallbackChainControls
{
    /// <summary>控制項固定佔用的高度，供頁面的 GetHeight 預留。</summary>
    internal const float Height = 78f;

    /// <summary>
    /// 繪製備援鏈控制項。
    /// </summary>
    /// <param name="listing">目前的版面。</param>
    /// <param name="selectedProviderId">中欄目前選取的供應商。</param>
    /// <param name="activeProviderId">啟用中的供應商；停用時為 <paramref name="disabledId"/>。</param>
    /// <param name="disabledId">代表停用的識別碼。</param>
    /// <param name="chain">目前的備援鏈（複本）。</param>
    /// <param name="save">備援鏈變更後的儲存回呼，接收整份新清單。</param>
    /// <param name="displayName">供應商識別碼轉顯示名稱。</param>
    internal static void Draw(
        Listing_Standard listing,
        string selectedProviderId,
        string activeProviderId,
        string disabledId,
        List<string> chain,
        Action<List<string>> save,
        Func<string, string> displayName)
    {
        bool hasActive = !string.IsNullOrEmpty(activeProviderId) &&
                         !string.Equals(activeProviderId, disabledId, StringComparison.Ordinal);

        // 向導等路徑直接改啟用中的供應商，不會把它從備援鏈拿掉。啟用中的那一家不算備援，
        // 先剔除再算順位與調換，畫面上的順位才和實際嘗試的順序一致。
        if (hasActive)
        {
            chain = chain.Where(id => !string.Equals(id, activeProviderId, StringComparison.Ordinal)).ToList();
        }

        // 摘要列：啟用中的供應商 → 備援 1 → 備援 2…
        Rect summaryRect = listing.GetRect(34f);
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Color oldColor = GUI.color;
            GUI.color = RimLLMUIStyle.Muted;
            Widgets.Label(summaryRect, BuildSummary(hasActive, activeProviderId, chain, displayName));
            GUI.color = oldColor;
        }

        // 啟用中的供應商本身就是鏈的第一環，不能再把自己加成備援。
        if (!hasActive || string.Equals(selectedProviderId, activeProviderId, StringComparison.Ordinal))
        {
            listing.Gap(28f);
            return;
        }

        int index = chain.IndexOf(selectedProviderId);
        bool inChain = index >= 0;
        bool wantChain = inChain;
        Rect rowRect = listing.GetRect(28f);
        Rect checkRect = new Rect(rowRect.x, rowRect.y, rowRect.width - 130f, rowRect.height);
        Widgets.CheckboxLabeled(checkRect, "RimLLM_FallbackChainJoin".Translate(), ref wantChain);

        if (wantChain != inChain)
        {
            var updated = new List<string>(chain);
            if (wantChain) updated.Add(selectedProviderId);
            else updated.Remove(selectedProviderId);
            save(updated);
            return;
        }

        if (!inChain) return;

        // 順位與調整按鈕只在已加入時出現。
        Rect upRect = new Rect(rowRect.xMax - 64f, rowRect.y, 30f, rowRect.height);
        Rect downRect = new Rect(rowRect.xMax - 30f, rowRect.y, 30f, rowRect.height);
        Widgets.Label(
            new Rect(rowRect.xMax - 130f, rowRect.y + 4f, 62f, rowRect.height),
            "RimLLM_FallbackChainPosition".Translate(index + 1));
        if (index > 0 && Widgets.ButtonText(upRect, "▲"))
        {
            save(Swap(chain, index, index - 1));
        }
        else if (index < chain.Count - 1 && Widgets.ButtonText(downRect, "▼"))
        {
            save(Swap(chain, index, index + 1));
        }
    }

    private static List<string> Swap(List<string> chain, int a, int b)
    {
        var updated = new List<string>(chain);
        (updated[a], updated[b]) = (updated[b], updated[a]);
        return updated;
    }

    private static string BuildSummary(
        bool hasActive, string activeProviderId, List<string> chain, Func<string, string> displayName)
    {
        if (!hasActive)
        {
            return "RimLLM_FallbackChainNeedsActive".Translate();
        }

        List<string> backups = chain.Where(id => !string.Equals(id, activeProviderId, StringComparison.Ordinal)).ToList();
        if (backups.Count == 0)
        {
            return "RimLLM_FallbackChainEmpty".Translate(displayName(activeProviderId));
        }

        string path = string.Join(" → ", new[] { activeProviderId }.Concat(backups).Select(displayName));
        return "RimLLM_FallbackChainSummary".Translate(path);
    }
}
