using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 首頁：用一張紅／綠大卡片告訴玩家「AI 現在能不能用」與原因，只給一顆下一步按鈕。
/// 新玩家第一次打開設定就落在這裡，不必先看懂其餘分頁。
/// </summary>
public static class HomeDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private const float CardHeight = 84f;
    private const float CardButtonWidth = 230f;

    public static float GetHeight(float width)
    {
        return 360f;
    }

    public static void DrawHome(Listing_Standard listing)
    {
        SetupState state = SetupStatus.Current(out string activeEntry);

        DrawStatusCard(listing, state);
        listing.Gap(14f);

        if (state == SetupState.Ready)
        {
            DrawReadyDetails(listing, activeEntry);
        }
        else
        {
            DrawTodoList(listing, state);
        }
    }

    private static void DrawStatusCard(Listing_Standard listing, SetupState state)
    {
        Rect card = listing.GetRect(CardHeight);
        Color tint = state switch
        {
            SetupState.Ready => RimLLMUIStyle.StatusGreen,
            SetupState.Broken => RimLLMUIStyle.Warning,
            _ => RimLLMUIStyle.Danger
        };

        Widgets.DrawBoxSolid(card, new Color(tint.r, tint.g, tint.b, 0.12f));
        Color oldColor = GUI.color;
        GUI.color = tint;
        Widgets.DrawBox(card, 2);
        GUI.color = oldColor;

        string titleKey;
        string reasonKey;
        string buttonKey;
        switch (state)
        {
            case SetupState.Ready:
                titleKey = "RimLLM_HomeReadyTitle";
                reasonKey = "RimLLM_HomeReadyReason";
                buttonKey = "RimLLM_HomeChatBtn";
                break;
            case SetupState.Broken:
                titleKey = "RimLLM_HomeNotReadyTitle";
                reasonKey = "RimLLM_HomeBrokenReason";
                buttonKey = "RimLLM_HomeFixBtn";
                break;
            default:
                titleKey = "RimLLM_HomeNotReadyTitle";
                reasonKey = "RimLLM_HomeNotConnectedReason";
                buttonKey = "RimLLM_HomeStartBtn";
                break;
        }

        Rect textRect = new Rect(card.x + 16f, card.y + 10f, card.width - CardButtonWidth - 40f, card.height - 20f);
        Rect titleRect = new Rect(textRect.x, textRect.y, textRect.width, 34f);
        Rect reasonRect = new Rect(textRect.x, titleRect.yMax + 2f, textRect.width, textRect.height - 36f);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Medium))
        {
            Widgets.Label(titleRect, $"<color=#{ColorUtility.ToHtmlStringRGB(tint)}><b>{titleKey.Translate()}</b></color>");
        }
        Widgets.Label(reasonRect, reasonKey.Translate());

        Rect buttonRect = new Rect(card.xMax - CardButtonWidth - 16f, card.y + (card.height - 44f) / 2f, CardButtonWidth, 44f);
        if (Widgets.ButtonText(buttonRect, buttonKey.Translate()))
        {
            if (state == SetupState.Ready)
            {
                RimLLMSettingsUI.OpenPage(RimLLMSettingsUI.ChatTestPageId);
            }
            else
            {
                SetupWizardDrawer.Restart();
                RimLLMSettingsUI.OpenPage(RimLLMSettingsUI.SetupPageId);
            }
        }
    }

    /// <summary>還不能用時，列出三步驟各自做完沒有，讓玩家知道卡在哪。</summary>
    private static void DrawTodoList(Listing_Standard listing, SetupState state)
    {
        bool anyEnabled = false;
        bool anyKeyed = false;
        foreach (string id in ProviderIds.BuiltIn)
        {
            if (!Settings.IsProviderEnabled(id)) continue;
            anyEnabled = true;
            if (SetupStatus.IsProviderUsable(id)) anyKeyed = true;
        }

        listing.Label("RimLLM_HomeTodoTitle".Translate());
        DrawTodoRow(listing, "RimLLM_HomeTodo1".Translate(), anyEnabled);
        DrawTodoRow(listing, "RimLLM_HomeTodo2".Translate(), anyKeyed);
        DrawTodoRow(listing, "RimLLM_HomeTodo3".Translate(), state == SetupState.Ready);
        listing.Gap(10f);
        RimLLMUIStyle.DrawHint(listing, "RimLLM_HomeTip".Translate());
    }

    private static void DrawTodoRow(Listing_Standard listing, string label, bool done)
    {
        Rect row = listing.GetRect(28f);
        Widgets.DrawLineHorizontal(row.x, row.yMax, row.width);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(new Rect(row.x + 4f, row.y, row.width - 140f, row.height), label);
            string status = done
                ? $"<color=#{ColorUtility.ToHtmlStringRGB(RimLLMUIStyle.StatusGreen)}>{"RimLLM_HomeTodoDone".Translate()}</color>"
                : $"<color=#{ColorUtility.ToHtmlStringRGB(RimLLMUIStyle.Danger)}>{"RimLLM_HomeTodoPending".Translate()}</color>";
            Widgets.Label(new Rect(row.xMax - 130f, row.y, 130f, row.height), status);
        }
    }

    private static void DrawReadyDetails(Listing_Standard listing, string activeEntry)
    {
        string providerId = ProviderIds.ParseProviderId(activeEntry);
        int colon = activeEntry.IndexOf(':');
        string model = colon > 0 ? activeEntry.Substring(colon + 1) : "RimLLM_HomeDefaultModel".Translate().ToString();

        listing.Label("RimLLM_HomeCurrentlyUsing".Translate(providerId, model));
        listing.Label("RimLLM_HomeTodayUsage".Translate(Settings.DailyAccumulatedTokens.ToString("N0")));
        listing.Gap(12f);

        Rect row = listing.GetRect(32f);
        if (Widgets.ButtonText(new Rect(row.x, row.y, 220f, row.height), "RimLLM_HomeAddAnotherBtn".Translate()))
        {
            SetupWizardDrawer.Restart();
            RimLLMSettingsUI.OpenPage(RimLLMSettingsUI.SetupPageId);
        }
        listing.Gap(8f);
        RimLLMUIStyle.DrawHint(listing, "RimLLM_HomeBackupTip".Translate());
    }
}
