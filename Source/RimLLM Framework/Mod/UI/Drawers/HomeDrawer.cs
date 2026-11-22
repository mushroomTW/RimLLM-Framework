using System.Globalization;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 首頁：用一張紅／綠大卡片告訴玩家「AI 現在能不能用」與原因，只給一顆下一步按鈕。
/// </summary>
public static class HomeDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private const float CardHeight = 84f;
    private const float CardButtonWidth = 230f;

    public static float GetHeight(float width)
    {
        return 480f;
    }

    public static void DrawHome(Listing_Standard listing)
    {
        SetupState state = SetupStatus.Current(out string? activeEntry);

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

        DrawOptionalServices(listing);
        DrawFooterButtons(listing, state);
    }

    private static void DrawFooterButtons(Listing_Standard listing, SetupState state)
    {
        listing.Gap(20f);
        Rect row = listing.GetRect(30f);
        float x = row.x;
        if (state == SetupState.Ready)
        {
            if (Widgets.ButtonText(new Rect(x, row.y, 220f, row.height), "RimLLM_HomeAddAnotherBtn".Translate()))
            {
                SetupWizardDrawer.Restart();
                RimLLMSettingsUI.OpenPage(RimLLMSettingsUI.SetupPageId);
            }
            x += 230f;
        }

        Color oldColor = GUI.color;
        GUI.color = RimLLMUIStyle.Danger;
        bool reset = Widgets.ButtonText(new Rect(x, row.y, 160f, row.height), "RimLLM_HomeResetAllBtn".Translate());
        GUI.color = oldColor;
        if (reset)
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "RimLLM_HomeResetAllConfirm".Translate(),
                () =>
                {
                    Settings.ResetAllSettingsToDefaults();
                    Settings.Write();
                    SetupWizardDrawer.Restart();
                },
                destructive: true));
        }
    }

    private static void DrawOptionalServices(Listing_Standard listing)
    {
        listing.Gap(16f);
        listing.Label($"<b>{"RimLLM_SetupOptionalTitle".Translate()}</b>");
        RimLLMUIStyle.DrawHint(listing, "RimLLM_SetupOptionalHint".Translate());

        string embedding = Settings.EmbeddingProvider;
        SetupWizardDrawer.DrawOptionalRow(listing,
            "RimLLM_ServiceEmbedding".Translate(),
            ServiceStatus(embedding != RimLLMFrameworkSettings.DisabledProvider, () => EmbeddingSettingsDrawer.GetProviderDisplayName(embedding)),
            "RimLLM_SetupOptionalOpenPage".Translate(),
            RimLLMSettingsUI.OpenEmbeddingPage);

        string decision = Settings.DecisionProvider;
        SetupWizardDrawer.DrawOptionalRow(listing,
            "RimLLM_ServiceDecision".Translate(),
            ServiceStatus(decision != Manager.Services.RimLLMDecisionProviderCatalog.Disabled, () => DecisionSettingsDrawer.GetProviderDisplayName(decision)),
            "RimLLM_SetupOptionalOpenPage".Translate(),
            RimLLMSettingsUI.OpenDecisionPage);
    }

    private static string ServiceStatus(bool on, System.Func<string> name)
    {
        return on
            ? $"<color=#{ColorUtility.ToHtmlStringRGB(RimLLMUIStyle.StatusGreen)}>{"RimLLM_ServiceStatusOn".Translate(name())}</color>"
            : "RimLLM_ServiceStatusOff".Translate().ToString();
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

        Rect textRect = new(card.x + 16f, card.y + 10f, card.width - CardButtonWidth - 40f, card.height - 20f);
        Rect titleRect = new(textRect.x, textRect.y, textRect.width, 34f);
        Rect reasonRect = new(textRect.x, titleRect.yMax + 2f, textRect.width, textRect.height - 36f);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Medium))
        {
            Widgets.Label(titleRect, $"<color=#{ColorUtility.ToHtmlStringRGB(tint)}><b>{titleKey.Translate()}</b></color>");
        }
        Widgets.Label(reasonRect, reasonKey.Translate());

        Rect buttonRect = new(card.xMax - CardButtonWidth - 16f, card.y + (card.height - 44f) / 2f, CardButtonWidth, 44f);
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

    private static void DrawReadyDetails(Listing_Standard listing, string? activeEntry)
    {
        string provider = activeEntry ?? string.Empty;
        string model = "RimLLM_HomeDefaultModel".Translate().ToString();
        if (ProviderIds.TrySplitModelId(provider, out string? providerId, out string? explicitModel))
        {
            provider = providerId;
            model = explicitModel;
        }

        Rect row = listing.GetRect(CardHeight);
        const float gap = 10f;
        float w = (row.width - gap * 2f) / 3f;
        DrawInfoCard(new Rect(row.x, row.y, w, row.height), "RimLLM_HomeCardCurrent".Translate(), provider, model);
        DrawInfoCard(new Rect(row.x + w + gap, row.y, w, row.height), "RimLLM_HomeCardUsage".Translate(),
            Settings.DailyAccumulatedTokens.ToString("N0", CultureInfo.InvariantCulture), "RimLLM_HomeTokensUnit".Translate());
        DrawInfoCard(new Rect(row.x + (w + gap) * 2f, row.y, w, row.height), "RimLLM_HomeCardBackup".Translate(),
            null, "RimLLM_HomeBackupTip".Translate());
    }

    private static void DrawInfoCard(Rect card, string label, string? value, string sub)
    {
        Widgets.DrawBoxSolid(card, new Color(1f, 1f, 1f, 0.05f));
        GUI.color = RimLLMUIStyle.PanelBorder;
        Widgets.DrawBox(card, 1);
        GUI.color = Color.white;
        Rect inner = card.ContractedBy(10f);
        using (RimLLMUIStyle.With(TextAnchor.UpperLeft, GameFont.Tiny, wordWrap: false))
        {
            GUI.color = RimLLMUIStyle.Muted;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 18f), label);
            GUI.color = Color.white;
        }

        float subY = inner.y + 18f;
        if (value != null)
        {
            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Medium, wordWrap: false))
            {
                Widgets.Label(new Rect(inner.x, subY, inner.width, 30f), value);
            }
            subY += 30f;
        }

        using (RimLLMUIStyle.With(TextAnchor.UpperLeft, GameFont.Tiny, wordWrap: value == null))
        {
            GUI.color = RimLLMUIStyle.Muted;
            Widgets.Label(new Rect(inner.x, subY, inner.width, inner.yMax - subY), sub);
            GUI.color = Color.white;
        }
    }
}
