using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>Embedding 與決策設定頁共用的備援鏈控制項；備援鏈只是「供應商識別碼的有序清單」，兩頁共用同一份繪製。</summary>
internal static class ProviderFallbackChainControls
{
    internal const float Height = 78f;

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
            chain = [.. chain.Where(id => !string.Equals(id, activeProviderId, StringComparison.Ordinal))];
        }

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
        Rect checkRect = new(rowRect.x, rowRect.y, rowRect.width - 130f, rowRect.height);
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

        Rect upRect = new(rowRect.xMax - 64f, rowRect.y, 30f, rowRect.height);
        Rect downRect = new(rowRect.xMax - 30f, rowRect.y, 30f, rowRect.height);
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

        List<string> backups = [.. chain.Where(id => !string.Equals(id, activeProviderId, StringComparison.Ordinal))];
        if (backups.Count == 0)
        {
            return "RimLLM_FallbackChainEmpty".Translate(displayName(activeProviderId));
        }

        string path = string.Join(" → ", new[] { activeProviderId }.Concat(backups).Select(displayName));
        return "RimLLM_FallbackChainSummary".Translate(path);
    }

/// <summary>「啟用此供應商」核取方塊與徽章；啟用時一併移出備援清單。標籤鍵為 RimLLM_Enable{kind}Provider、RimLLM_{kind}ActiveBadge。</summary>
    internal static void DrawActiveToggle(
        Listing_Standard listing,
        string providerId,
        bool isActive,
        string kind,
        Action<bool> setActive,
        Func<List<string>> getChain,
        Action<List<string>> setChain)
    {
        bool oldActive = isActive;
        listing.CheckboxLabeled(("RimLLM_Enable" + kind + "Provider").Translate(), ref isActive);
        if (isActive != oldActive)
        {
            setActive(isActive);
            if (isActive)
            {
                List<string> chain = getChain();
                if (chain.Remove(providerId)) setChain(chain);
            }
        }

        if (isActive)
        {
            listing.Label(("RimLLM_" + kind + "ActiveBadge").Translate());
        }
        listing.Gap(4f);
    }
}
