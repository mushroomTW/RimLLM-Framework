using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager.Services;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>Decision 模組設定頁，版面與 Embedding 模組一致。</summary>
public static class DecisionSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private const float SubButtonHeight = 46f;
    private const float SubButtonGap = 4f;
    private const string ColorTagOpen = "<color=";
    private const string ColorTagClose = "</color>";

    /// <summary>目前在中欄選取的供應商；與實際啟用的 DecisionProvider 分開，方便先編輯再啟用。</summary>
    public static string ActiveDecisionSubTab { get; set; } = RimLLMDecisionProviderCatalog.TypeSafe;

    private static Vector2 _midScrollPosition = Vector2.zero;

    private static readonly Dictionary<string, bool> Fetching = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> FetchStatus = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> Testing = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> TestStatus = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Vector2> ModelScrollPositions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ModelFilters = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> RevealedKeys = new(StringComparer.Ordinal);

    private static bool IsPlaintextEndpoint(string providerId)
    {
        return DecisionEndpointRouting.IsPlaintext(Settings.GetDecisionEndpoint(providerId));
    }

    public static string GetProviderDisplayName(string providerId)
    {
        RimLLMDecisionProvider provider = RimLLMDecisionProviderCatalog.Get(providerId);
        string fallback = provider.Id;
        try
        {
            if (LanguageDatabase.activeLanguage != null)
            {
                return $"RimLLM_DecisionProvider_{provider.Id}".Translate();
            }
        }
        catch
        {
            // 單元測試或語言資料尚未載入時，退回可讀的預設值。
        }
        return fallback;
    }

    public static float GetHeight(float width)
    {
        string providerId = ActiveDecisionSubTab;
        int modelCount = Settings.GetModelCount(RimLLMDecisionService.GetModelListKey(providerId));
        float modelSectionHeight = modelCount > 0 ? 280f : 70f;
        float endpointHeight = RimLLMDecisionProviderCatalog.Get(providerId).AllowCustomEndpoint ? 0f : -70f;
        return 460f + endpointHeight + ProviderFallbackChainControls.Height + modelSectionHeight;
    }

    /// <summary>繪製決策供應商清單。</summary>
    #pragma warning disable S3267 // 迴圈內有 UI 繪製副作用（DrawDecisionSubButton 與 listing.Gap），無法以 LINQ 取代。
    public static void DrawMiddleDecisionMenu(Rect rect)
    {
        Rect contentRect = rect.ContractedBy(6f);

        Rect titleRect = new(contentRect.x, contentRect.y, contentRect.width, 20f);
        Text.Font = GameFont.Small;
        Widgets.Label(titleRect, "RimLLM_DecisionProvidersTitle".Translate());

        Rect listRect = new(contentRect.x, titleRect.yMax + 4f, contentRect.width, contentRect.height - 24f);
        float viewHeight = (RimLLMDecisionProviderCatalog.All.Count * (SubButtonHeight + SubButtonGap)) + 10f;
        Rect viewRect = new(0f, 0f, listRect.width - 16f, viewHeight);

        Widgets.BeginScrollView(listRect, ref _midScrollPosition, viewRect);
        Listing_Standard listing = new();
        listing.Begin(viewRect);

        foreach (RimLLMDecisionProvider provider in RimLLMDecisionProviderCatalog.All)
        {
            DrawDecisionSubButton(listing, GetProviderDisplayName(provider.Id), provider.Id);
            listing.Gap(SubButtonGap);
        }

        listing.End();
        Widgets.EndScrollView();
    }
    #pragma warning restore S3267

    private static void DrawDecisionSubButton(Listing_Standard listing, string label, string providerId)
    {
        Rect btnRect = listing.GetRect(SubButtonHeight);
        RimLLMUIStyle.DrawNavSelection(btnRect, ActiveDecisionSubTab == providerId);

        if (Widgets.ButtonInvisible(btnRect))
        {
            ActiveDecisionSubTab = providerId;
        }

        Rect nameRect = new(btnRect.x + 8f, btnRect.y + 3f, btnRect.width - 16f, 22f);
        Rect statusRect = new(btnRect.x + 8f, btnRect.y + 25f, btnRect.width - 16f, 18f);
        Text.Font = GameFont.Small;
        string nameText = ActiveDecisionSubTab == providerId
            ? $"{ColorTagOpen}#F0C773>{label}{ColorTagClose}"
            : $"{ColorTagOpen}silver>{label}{ColorTagClose}";
        Widgets.Label(nameRect, nameText);

        bool isActive = string.Equals(Settings.DecisionProvider, providerId, StringComparison.Ordinal);
        Color statusColor;

        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            string statusText;
            if (isActive)
            {
                if (!RimLLMDecisionService.HasEffectiveApiKey(Settings, providerId) && !IsPlaintextEndpoint(providerId))
                {
                    statusText = "RimLLM_StatusNoApiKey".Translate();
                    statusColor = RimLLMUIStyle.Warning;
                }
                else
                {
                    string model = Settings.GetDecisionModel(providerId);
                    string modelDisplay = string.IsNullOrEmpty(model)
                        ? "Default"
                        : RimLLMUIStyle.TruncateCached(model, Math.Max(24f, statusRect.width - 55f));
                    statusText = "RimLLM_DecisionStatusActiveDetail".Translate(modelDisplay);
                    statusColor = RimLLMUIStyle.Success;
                }
            }
            else
            {
                statusText = "RimLLM_DecisionStatusInactive".Translate();
                statusColor = RimLLMUIStyle.Muted;
            }

            Color oldColor = GUI.color;
            GUI.color = statusColor;
            Widgets.Label(statusRect, statusText);
            GUI.color = oldColor;
        }
    }

    public static void DrawRightDetailContent(Listing_Standard listing)
    {
        RimLLMDecisionProvider provider = RimLLMDecisionProviderCatalog.Get(ActiveDecisionSubTab);
        string providerId = provider.Id;

        // 不在這裡把選取項目拉回「目前啟用」的供應商：玩家需要能先選另一個供應商、
        // 填好金鑰與模型，再勾選啟用。啟用狀態只由下面的核取方塊改變。
        DrawActiveToggle(listing, providerId);

        Rect expRect = listing.GetRect(44f);
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Color oldC = GUI.color;
            GUI.color = RimLLMUIStyle.Muted;
            Widgets.Label(expRect, "RimLLM_DecisionExplanation".Translate());
            GUI.color = oldC;
        }
        listing.Gap(6f);

        ProviderFallbackChainControls.Draw(
            listing,
            providerId,
            Settings.DecisionProvider,
            RimLLMDecisionProviderCatalog.Disabled,
            Settings.DecisionFallbackChain,
            updated =>
            {
                Settings.DecisionFallbackChain = updated;
                Settings.Write();
            },
            GetProviderDisplayName);
        listing.Gap(6f);

        DrawApiKeySection(listing, providerId);
        if (provider.AllowCustomEndpoint)
        {
            DrawEndpointSection(listing, providerId, provider);
        }
        DrawModelSection(listing, providerId);

        listing.Gap(8f);
        DrawConnectionTest(listing, providerId);
        listing.GapLine(4f);
    }

    private static void DrawActiveToggle(Listing_Standard listing, string providerId)
    {
        ProviderFallbackChainControls.DrawActiveToggle(
            listing,
            providerId,
            string.Equals(Settings.DecisionProvider, providerId, StringComparison.Ordinal),
            "Decision",
            active =>
            {
                Settings.DecisionProvider = active ? providerId : RimLLMDecisionProviderCatalog.Disabled;
                Settings.Write();
            },
            () => Settings.DecisionFallbackChain,
            chain => { Settings.DecisionFallbackChain = chain; Settings.Write(); });
    }

    private static void DrawApiKeySection(Listing_Standard listing, string providerId)
    {
        bool isPlaintext = IsPlaintextEndpoint(providerId);
        listing.Label(isPlaintext
            ? "RimLLM_DecisionLocalKeyHint".Translate()
            : "RimLLM_DecisionKeyHint".Translate());

        string currentKey = Settings.GetDecisionApiKey(providerId);
        Rect rowRect = listing.GetRect(30f);
        Rect inputRect = new(rowRect.x, rowRect.y, rowRect.width - 48f, rowRect.height);
        Rect revealRect = new(inputRect.xMax + 8f, rowRect.y, 40f, rowRect.height);

        bool revealed = RevealedKeys.TryGetValue(providerId, out bool r) && r;
        string newKey = RimLLMUIStyle.DrawMaskableKeyField(inputRect, revealRect, currentKey, ref revealed);
        RevealedKeys[providerId] = revealed;

        if (newKey != currentKey)
        {
            Settings.SetDecisionApiKey(providerId, newKey);
        }

        // 本機端點通常不驗證金鑰，留空是正常狀態，不必提醒玩家補上。
        if (string.IsNullOrEmpty(newKey) && !isPlaintext)
        {
            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                listing.Label(RimLLMDecisionService.HasEffectiveApiKey(Settings, providerId)
                    ? "RimLLM_DecisionInheritedKeyHint".Translate()
                    : "RimLLM_DecisionNoKeyWarning".Translate());
            }
        }
        listing.Gap(8f);
    }

    private static void DrawEndpointSection(
        Listing_Standard listing,
        string providerId,
        RimLLMDecisionProvider provider)
    {
        listing.Label(
            "RimLLM_DecisionEndpointLabel".Translate() +
            $" {ColorTagOpen}grey>({provider.DefaultEndpoint}){ColorTagClose}");

        // 欄位顯示原始輸入；空白代表使用預設端點，玩家可以清空後回到預設值。
        string currentEndpoint = Settings.GetDecisionEndpointRaw(providerId);
        string newEndpoint = listing.TextEntry(currentEndpoint);
        if (newEndpoint != currentEndpoint)
        {
            Settings.SetDecisionEndpoint(providerId, newEndpoint?.Trim());
        }
        listing.Gap(4f);

        if (IsPlaintextEndpoint(providerId))
        {
            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                listing.Label("RimLLM_DecisionLocalEndpointHint".Translate());
            }
            listing.Gap(4f);
        }

        listing.Gap(8f);
    }

    private static void DrawModelSection(Listing_Standard listing, string providerId)
    {
        listing.Label("RimLLM_DecisionModelLabel".Translate());

        string currentModel = Settings.GetDecisionModelRaw(providerId);
        string newModel = listing.TextEntry(currentModel);
        if (newModel != currentModel)
        {
            Settings.SetDecisionModel(providerId, newModel);
        }
        listing.Gap(4f);

        listing.Label("RimLLM_AvailableModelsTitle".Translate());
        string modelListKey = RimLLMDecisionService.GetModelListKey(providerId);
        if (Settings.GetModelCount(modelListKey) == 0)
        {
            listing.Label("RimLLM_DecisionNoModelList".Translate());
        }
        else
        {
            DrawFetchedModels(listing, providerId, modelListKey);
        }

        listing.Gap(6f);
        GenericProviderSubTabDrawer.DrawBusyActionRow(
            listing,
            Fetching.TryGetValue(providerId, out bool fetching) && fetching,
            "RimLLM_Fetching".Translate(),
            "RimLLM_FetchModelsBtn".Translate(),
            FetchStatus.TryGetValue(providerId, out string? fetchStatus)
                ? fetchStatus
                : "RimLLM_FetchStatusNotRun".Translate().ToString(),
            () => StartFetchDecisionModels(providerId),
            buttonWidth: 200f);
    }

    private static void DrawFetchedModels(Listing_Standard listing, string providerId, string modelListKey)
    {
        ModelChipGrid.DrawFilterBar(listing, "RimLLM_DecisionFetchedModelsHint", ModelFilters, providerId);

        List<string> visibleModels = RimLLMUIStyle.FilterModelsCached(
            "decision:" + providerId,
            Settings.ModelListVersion,
            ModelFilters.TryGetValue(providerId, out string? filter) ? filter : "",
            () => Settings.GetModelList(modelListKey));

        Rect scrollRect = listing.GetRect(200f);
        Widgets.DrawMenuSection(scrollRect);

        if (visibleModels.Count == 0)
        {
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
            {
                Widgets.Label(scrollRect, $"{ColorTagOpen}grey>" + "RimLLM_NoMatchingModels".Translate() + ColorTagClose);
            }
        }
        else
        {
            ModelChipGrid.DrawGrid(scrollRect, providerId, visibleModels, ModelScrollPositions, model =>
                ModelChipGrid.ShowModelMenu(model, "RimLLM_DecisionSetAsModel", "RimLLM_DecisionModelLabel", picked =>
                {
                    Settings.SetDecisionModel(providerId, picked);
                    Settings.Write();
                }));
        }
    }

    private static void StartFetchDecisionModels(string providerId)
    {
        Fetching[providerId] = true;
        FetchStatus[providerId] = "RimLLM_Fetching".Translate();

        DecisionPreviewArguments args = new RimLLMDecisionService(Settings).GetPreviewArguments(providerId);

        _ = Task.Run(async () =>
        {
            Func<string> applyResult;
            try
            {
                IReadOnlyList<TypeSafeAI.ModelInfo> models = await new RimLLMDecisionService(Settings)
                    .FetchModelsAsync(args.Endpoint, args.Model, args.ApiKey)
                    .ConfigureAwait(false);

                applyResult = () =>
                {
                    List<string> names = [.. models
                        .Select(entry => entry.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.OrdinalIgnoreCase)];

                    if (names.Count == 0)
                    {
                        return "RimLLM_FetchSuccessEmpty".Translate();
                    }

                    Settings.SetModelList(RimLLMDecisionService.GetModelListKey(providerId), names);
                    Settings.Write();
                    return "RimLLM_FetchSuccessCount".Translate(names.Count);
                };
            }
            catch (Exception ex)
            {
                string detail = RimLLMLog.SanitizeForLog(ex.Message, 220) ?? string.Empty;
                applyResult = () => "RimLLM_FetchFailed".Translate() + " (" + detail + ")";
            }

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                Fetching[providerId] = false;
                FetchStatus[providerId] = applyResult();
            });
        });
    }

    private static void DrawConnectionTest(Listing_Standard listing, string providerId)
    {
        GenericProviderSubTabDrawer.DrawBusyActionRow(
            listing,
            Testing.TryGetValue(providerId, out bool testing) && testing,
            "RimLLM_DecisionTesting".Translate(),
            "RimLLM_DecisionTestBtn".Translate(),
            TestStatus.TryGetValue(providerId, out string? testStatus)
                ? testStatus
                : "RimLLM_TestStatusNotRun".Translate().ToString(),
            () => StartTestDecision(providerId),
            buttonWidth: 200f);
    }

    private static void StartTestDecision(string providerId)
    {
        Testing[providerId] = true;
        TestStatus[providerId] = "RimLLM_DecisionTesting".Translate();

        DecisionPreviewArguments args = new RimLLMDecisionService(Settings).GetPreviewArguments(providerId);

        _ = Task.Run(async () =>
        {
            Func<string> applyResult;
            try
            {
                await new RimLLMDecisionService(Settings)
                    .TestConnectionAsync(args.Endpoint, args.Model, args.ApiKey)
                    .ConfigureAwait(false);
                applyResult = () => "RimLLM_DecisionTestSuccess".Translate();
            }
            catch (Exception ex)
            {
                string detail = RimLLMLog.SanitizeForLog(ex.Message, 200) ?? string.Empty;
                applyResult = () => "RimLLM_DecisionTestFailed".Translate(detail);
            }

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                Testing[providerId] = false;
                TestStatus[providerId] = applyResult();
            });
        });
    }
}
