using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>「Embedding 向量」設定分頁，版面與「API 供應商」分頁一致（中欄供應商選單 + 右欄詳細設定）。</summary>
public static class EmbeddingSettingsDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private const string ProviderGoogle = "Google";
    private const string ProviderOpenAI = "OpenAI";
    private const string ProviderOpenRouter = RimLLMEmbeddingService.OpenRouterProvider;
    private const string ProviderOllama = "LocalAPI_Ollama";
    private const string ProviderLocalOpenAI = "LocalAPI_OpenAI";
    private const string ColorTagOpen = "<color=";
    private const string ColorTagClose = "</color>";

    // 中欄選單的後備顯示名稱；實際顯示走 RimLLM_EmbeddingProvider_{id} 語系鍵，後備名稱只在語系未載入（單元測試）時使用。
    /// 實際顯示走 <c>RimLLM_EmbeddingProvider_{id}</c> 語系鍵，後備名稱只在語系未載入（單元測試）時使用。
    private static readonly List<KeyValuePair<string, string>> MenuEntries =
    [
        new KeyValuePair<string, string>("Google Gemini", ProviderGoogle),
        new KeyValuePair<string, string>("OpenAI", ProviderOpenAI),
        new KeyValuePair<string, string>("OpenRouter", ProviderOpenRouter),
        new KeyValuePair<string, string>("Ollama (本地)", ProviderOllama),
        new KeyValuePair<string, string>("OpenAI 相容 (本地/自訂)", ProviderLocalOpenAI)
    ];

    private const float SubButtonHeight = 46f;
    private const float SubButtonGap = 4f;

    public static string ActiveEmbeddingSubTab { get; set; } = ProviderGoogle;

    private static Vector2 _midScrollPosition = Vector2.zero;

    private static readonly Dictionary<string, bool> Fetching = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> FetchStatus = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> Testing = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> TestStatus = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Vector2> ModelScrollPositions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ModelFilters = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> RevealedKeys = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, bool> Detecting = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> DetectStatus = new(StringComparer.Ordinal);

    public static string GetProviderDisplayName(string providerId)
    {
        string? fallback = null;
        foreach (var entry in MenuEntries)
        {
            if (entry.Value == providerId)
            {
                fallback = entry.Key;
                break;
            }
        }
        if (fallback == null) return providerId ?? "";

        try
        {
            if (LanguageDatabase.activeLanguage != null)
            {
                return $"RimLLM_EmbeddingProvider_{providerId}".Translate();
            }
        }
        catch
        {
            // 單元測試或語系尚未載入時退回後備名稱
        }
        return fallback;
    }

    private static bool IsLocalProvider(string providerId)
    {
        return providerId == ProviderOllama || providerId == ProviderLocalOpenAI;
    }

    public static float GetHeight(float width)
    {
        string providerId = ActiveEmbeddingSubTab;
        int modelCount = Settings.GetModelCount(RimLLMEmbeddingService.GetModelListKey(providerId));
        float modelSectionHeight = modelCount > 0 ? 280f : 70f;
        float endpointHeight = IsLocalProvider(providerId) ? 40f : -70f;
        return 460f + ProviderFallbackChainControls.Height + modelSectionHeight + endpointHeight;
    }

    public static void DrawMiddleEmbeddingMenu(Rect rect)
    {
        Rect contentRect = rect.ContractedBy(6f);

        Rect titleRect = new(contentRect.x, contentRect.y, contentRect.width, 20f);
        Text.Font = GameFont.Small;
        Widgets.Label(titleRect, "RimLLM_EmbeddingProvidersTitle".Translate());

        Rect listRect = new(contentRect.x, titleRect.yMax + 4f, contentRect.width, contentRect.height - 24f);
        float viewHeight = MenuEntries.Count * (SubButtonHeight + SubButtonGap) + 10f;
        Rect viewRect = new(0f, 0f, listRect.width - 16f, viewHeight);

        Widgets.BeginScrollView(listRect, ref _midScrollPosition, viewRect);
        Listing_Standard listing = new();
        listing.Begin(viewRect);

        foreach (var providerId in MenuEntries.Select(entry => entry.Value))
        {
            DrawEmbeddingSubButton(listing, GetProviderDisplayName(providerId), providerId);
            listing.Gap(SubButtonGap);
        }
        listing.End();

        Widgets.EndScrollView();
    }

    private static void DrawEmbeddingSubButton(Listing_Standard listing, string label, string providerId)
    {
        Rect btnRect = listing.GetRect(SubButtonHeight);
        RimLLMUIStyle.DrawNavSelection(btnRect, ActiveEmbeddingSubTab == providerId);

        if (Widgets.ButtonInvisible(btnRect))
        {
            ActiveEmbeddingSubTab = providerId;
        }

        Rect nameRect = new(btnRect.x + 8f, btnRect.y + 3f, btnRect.width - 16f, 22f);
        Rect statusRect = new(btnRect.x + 8f, btnRect.y + 25f, btnRect.width - 16f, 18f);
        Text.Font = GameFont.Small;
        string nameText = ActiveEmbeddingSubTab == providerId ? $"{ColorTagOpen}#F0C773>{label}{ColorTagClose}" : $"{ColorTagOpen}silver>{label}{ColorTagClose}";
        Widgets.Label(nameRect, nameText);

        bool isActive = Settings.EmbeddingProvider == providerId;
        string statusText;
        Color statusColor;

        if (isActive)
        {
            if (ProviderNeedsApiKey(providerId) && !HasEffectiveApiKey(providerId))
            {
                statusText = "RimLLM_StatusNoApiKey".Translate();
                statusColor = RimLLMUIStyle.Warning;
            }
            else
            {
                string model = Settings.GetEmbeddingModel(providerId);
                string modelDisplay = string.IsNullOrEmpty(model) ? "Default" : model.Truncate(12);
                statusText = "RimLLM_EmbeddingStatusActiveDetail".Translate(modelDisplay);
                statusColor = RimLLMUIStyle.Success;
            }
        }
        else
        {
            statusText = "RimLLM_EmbeddingStatusInactive".Translate();
            statusColor = RimLLMUIStyle.Muted;
        }

        Color oldColor = GUI.color;
        GUI.color = statusColor;
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Widgets.Label(statusRect, statusText);
        }
        GUI.color = oldColor;
    }

    /// <summary>與服務端規則一致：本機服務以外的雲端供應商都需要金鑰。</summary>
    private static bool ProviderNeedsApiKey(string providerId)
    {
        return !IsLocalProvider(providerId);
    }

    private static bool HasEffectiveApiKey(string providerId)
    {
        string customKey = Settings.GetEmbeddingApiKey(providerId);
        if (!string.IsNullOrEmpty(customKey)) return true;

        string mainProvider = RimLLMEmbeddingService.GetMainProviderIdForEmbedding(providerId);
        return !string.IsNullOrEmpty(Settings.GetApiKey(mainProvider));
    }

    public static void DrawRightDetailContent(Listing_Standard listing)
    {
        string providerId = ActiveEmbeddingSubTab;

        DrawActiveToggle(listing, providerId);

        DrawExplanationAndFallback(listing, providerId);

        DrawApiKeySection(listing, providerId);

        // 官方服務的端點已固定，只有本機服務才開放自訂
        if (IsLocalProvider(providerId))
        {
            DrawEndpointSection(listing, providerId);
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
            Settings.EmbeddingProvider == providerId,
            "Embedding",
            active =>
            {
                Settings.EmbeddingProvider = active ? providerId : "Disabled";
                Settings.Write();
            },
            () => Settings.EmbeddingFallbackChain,
            chain => { Settings.EmbeddingFallbackChain = chain; Settings.Write(); });
    }

    private static void DrawExplanationAndFallback(Listing_Standard listing, string providerId)
    {
        Rect expRect = listing.GetRect(32f);
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            Color oldC = GUI.color;
            GUI.color = RimLLMUIStyle.Muted;
            Widgets.Label(expRect, "RimLLM_EmbeddingExplanation".Translate());
            GUI.color = oldC;
        }
        listing.Gap(6f);

        ProviderFallbackChainControls.Draw(
            listing,
            providerId,
            Settings.EmbeddingProvider,
            "Disabled",
            Settings.EmbeddingFallbackChain,
            updated =>
            {
                Settings.EmbeddingFallbackChain = updated;
                Settings.Write();
            },
            GetProviderDisplayName);
        listing.Gap(6f);
    }

    private static void DrawApiKeySection(Listing_Standard listing, string providerId)
    {
        if (ProviderNeedsApiKey(providerId))
        {
            listing.Label("RimLLM_EmbeddingKeyHintDefault".Translate());
        }
        else
        {
            listing.Label("RimLLM_EmbeddingKeyHintLocal".Translate());
        }

        string currentKey = Settings.GetEmbeddingApiKey(providerId);
        Rect rowRect = listing.GetRect(30f);
        Rect inputRect = new(rowRect.x, rowRect.y, rowRect.width - 48f, rowRect.height);
        Rect revealRect = new(inputRect.xMax + 8f, rowRect.y, 40f, rowRect.height);

        bool revealed = RevealedKeys.TryGetValue(providerId, out bool r) && r;
        string newKey = RimLLMUIStyle.DrawMaskableKeyField(inputRect, revealRect, currentKey, ref revealed);
        RevealedKeys[providerId] = revealed;

        if (newKey != currentKey)
        {
            Settings.SetEmbeddingApiKey(providerId, newKey);
            Settings.Write();
        }

        if (string.IsNullOrEmpty(newKey) && ProviderNeedsApiKey(providerId))
        {
            string mainProvider = RimLLMEmbeddingService.GetMainProviderIdForEmbedding(providerId);
            string mainKey = Settings.GetApiKey(mainProvider);
            bool isCurrentlyActive = Settings.EmbeddingProvider == providerId;

            string hint;
            if (!string.IsNullOrEmpty(mainKey))
            {
                hint = "RimLLM_EmbeddingInheritedKeyHint".Translate();
            }
            else if (isCurrentlyActive)
            {
                hint = "RimLLM_EmbeddingNoKeyWarning".Translate();
            }
            else
            {
                hint = "RimLLM_EmbeddingInheritKeyNotice".Translate();
            }

            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                listing.Label(hint);
            }
        }
        listing.Gap(8f);
    }

    private static void DrawEndpointSection(Listing_Standard listing, string providerId)
    {
        bool isLocal = IsLocalProvider(providerId);
        string defaultEndpoint = RimLLMFrameworkSettings.GetDefaultEmbeddingEndpoint(providerId);

        listing.Label("RimLLM_EmbeddingEndpointLabel".Translate() + $" {ColorTagOpen}grey>({defaultEndpoint}){ColorTagClose}");

        // 欄位顯示原始輸入值；空白代表使用預設端點，不可回填預設值，否則玩家無法清空重打。
        string currentEndpoint = Settings.GetEmbeddingEndpointRaw(providerId);
        string newEndpoint = listing.TextEntry(currentEndpoint);
        if (newEndpoint != currentEndpoint)
        {
            Settings.SetEmbeddingEndpoint(providerId, newEndpoint?.Trim());
            Settings.Write();
        }

        if (isLocal)
        {
            DrawLocalDetectionControls(listing, providerId);
        }
        listing.Gap(8f);
    }

    private static void DrawLocalDetectionControls(Listing_Standard listing, string providerId)
    {
        Rect detectRect = listing.GetRect(30f);
        Rect detectBtnRect = new(detectRect.x, detectRect.y, 200f, detectRect.height);
        Rect detectStatusRect = new(detectRect.x + 210f, detectRect.y, detectRect.width - 210f, detectRect.height);

        if (Detecting.TryGetValue(providerId, out bool isD) && isD)
        {
            GUI.color = Color.gray;
            Widgets.ButtonText(detectBtnRect, "RimLLM_DetectingLocal".Translate());
            GUI.color = Color.white;
        }
        else
        {
            if (Widgets.ButtonText(detectBtnRect, "RimLLM_DetectLocalBtn".Translate()))
            {
                StartDetectLocalEndpoint(providerId);
            }
        }

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(detectStatusRect, DetectStatus.TryGetValue(providerId, out string? ds) ? ds : "");
        }
    }

    /// <summary>依供應商決定探測順序：Ollama 分頁只找 Ollama；OpenAI 相容分頁先找通用伺服器、最後才退回 Ollama 的 /v1，否則 Ollama 分頁會被恰好在跑的 LM Studio 搶走端點。</summary>
    private static LocalEndpointProbe.Target[] GetDetectTargets(string providerId)
    {
        var ollama = new LocalEndpointProbe.Target { Name = "Ollama", BaseUrl = LocalEndpointProbe.OllamaBaseUrl, TestUrl = LocalEndpointProbe.OllamaV1ModelsUrl };
        var ollamaRaw = new LocalEndpointProbe.Target { Name = "Ollama (Raw)", BaseUrl = LocalEndpointProbe.OllamaBaseUrl, TestUrl = LocalEndpointProbe.OllamaApiTagsUrl };
        if (providerId == ProviderOllama)
        {
            return [ollama, ollamaRaw];
        }
        return
        [
            new LocalEndpointProbe.Target { Name = "LM Studio", BaseUrl = LocalEndpointProbe.LmStudioBaseUrl, TestUrl = LocalEndpointProbe.LmStudioV1ModelsUrl },
            new LocalEndpointProbe.Target { Name = "LocalAI/vLLM (8080)", BaseUrl = LocalEndpointProbe.LocalAI8080BaseUrl, TestUrl = LocalEndpointProbe.LocalAI8080V1ModelsUrl },
            new LocalEndpointProbe.Target { Name = "LocalAI/vLLM (8000)", BaseUrl = LocalEndpointProbe.LocalAI8000BaseUrl, TestUrl = LocalEndpointProbe.LocalAI8000V1ModelsUrl },
            ollama,
            ollamaRaw
        ];
    }

    private static void StartDetectLocalEndpoint(string providerId)
    {
        Detecting[providerId] = true;
        DetectStatus[providerId] = "RimLLM_DetectingLocal".Translate();

        LocalEndpointProbe.StartDetect(
            GetDetectTargets(providerId),
            hit =>
            {
                Settings.SetEmbeddingEndpoint(providerId, hit.BaseUrl);
                Settings.Write();
                Detecting[providerId] = false;
                DetectStatus[providerId] = "RimLLM_DetectSuccess".Translate(hit.Name, hit.BaseUrl);
            },
            () =>
            {
                Detecting[providerId] = false;
                DetectStatus[providerId] = "RimLLM_DetectFailed".Translate();
            });
    }

    private static void DrawModelSection(Listing_Standard listing, string providerId)
    {
        DrawModelInputField(listing, providerId);

        listing.Label("RimLLM_AvailableModelsTitle".Translate());
        string modelListKey = RimLLMEmbeddingService.GetModelListKey(providerId);
        if (Settings.GetModelCount(modelListKey) == 0)
        {
            listing.Label("RimLLM_EmbeddingNoModelList".Translate());
        }
        else
        {
            ModelChipGrid.DrawFilterBar(listing, "RimLLM_EmbeddingFetchedModelsHint", ModelFilters, providerId);

            List<string> visibleModels = RimLLMUIStyle.FilterModelsCached(
                "embedding:" + providerId,
                Settings.ModelListVersion,
                ModelFilters[providerId],
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
                    ModelChipGrid.ShowModelMenu(model, "RimLLM_EmbeddingSetAsModel", "RimLLM_EmbeddingModelLabel", picked =>
                    {
                        Settings.SetEmbeddingModel(providerId, picked);
                        Settings.Write();
                    }));
            }
        }

        listing.Gap(6f);

        GenericProviderSubTabDrawer.DrawBusyActionRow(
            listing,
            Fetching.TryGetValue(providerId, out bool isF) && isF,
            "RimLLM_Fetching".Translate(),
            "RimLLM_FetchModelsBtn".Translate(),
            FetchStatus.TryGetValue(providerId, out string? fs) ? fs : "RimLLM_FetchStatusNotRun".Translate().ToString(),
            () => StartFetchEmbeddingModels(providerId),
            buttonWidth: 200f);
    }

    private static void DrawModelInputField(Listing_Standard listing, string providerId)
    {
        listing.Label("RimLLM_EmbeddingModelLabel".Translate());
        string currentModel = Settings.GetEmbeddingModelRaw(providerId);
        string newModel = listing.TextEntry(currentModel);
        if (newModel != currentModel)
        {
            Settings.SetEmbeddingModel(providerId, newModel);
            Settings.Write();
        }
        listing.Gap(4f);
    }

    private static void StartFetchEmbeddingModels(string providerId)
    {
        Fetching[providerId] = true;
        FetchStatus[providerId] = "RimLLM_Fetching".Translate();

        string endpoint = Settings.GetEmbeddingEndpoint(providerId);
        string apiKey = Settings.GetEmbeddingApiKey(providerId);

        _ = Task.Run(async () =>
        {
            Func<string> applyResult;
            try
            {
                RimLLMEmbeddingModelList result = await new RimLLMEmbeddingService(Settings)
                    .FetchAvailableModelsAsync(providerId, endpoint, apiKey)
                    .ConfigureAwait(false);

                applyResult = () =>
                {
                    List<string> models = result.Models;
                    if (models.Count == 0)
                    {
                        return result.Filtered
                            ? "RimLLM_EmbeddingFetchSuccessEmpty".Translate()
                            : "RimLLM_FetchSuccessEmpty".Translate();
                    }
                    Settings.SetModelList(RimLLMEmbeddingService.GetModelListKey(providerId), models);
                    Settings.Write();
                    // 沒過濾的清單可能混有對話模型，狀態列要講清楚，玩家才知道不能照單全收。
                    return result.Filtered
                        ? "RimLLM_EmbeddingFetchSuccessCount".Translate(models.Count)
                        : "RimLLM_EmbeddingFetchSuccessUnfiltered".Translate(models.Count);
                };
            }
            catch (Exception ex)
            {
                string message = "RimLLM_FetchFailed".Translate() +
                    " (" + RimLLMLog.SanitizeForLog(ex.Message, 220) + ")";
                applyResult = () => message;
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
            Testing.TryGetValue(providerId, out bool isT) && isT,
            "RimLLM_EmbeddingTesting".Translate(),
            "RimLLM_EmbeddingTestBtn".Translate(),
            TestStatus.TryGetValue(providerId, out string? ts) ? ts : "RimLLM_TestStatusNotRun".Translate().ToString(),
            () => StartTestEmbedding(providerId),
            buttonWidth: 200f);
    }

    private static void StartTestEmbedding(string providerId)
    {
        Testing[providerId] = true;
        TestStatus[providerId] = "RimLLM_EmbeddingTesting".Translate();

        string model = Settings.GetEmbeddingModel(providerId);
        string endpoint = Settings.GetEmbeddingEndpoint(providerId);
        string apiKey = Settings.GetEmbeddingApiKey(providerId);
        var sw = Stopwatch.StartNew();

        _ = Task.Run(async () =>
        {
            Func<string> applyResult;
            try
            {
                var service = new RimLLMEmbeddingService(Settings);
                var result = await service.TestEmbeddingAsync(providerId, model, endpoint, apiKey).ConfigureAwait(false);
                sw.Stop();
                int dims = result.Vector != null ? result.Vector.Length : 0;
                long elapsed = sw.ElapsedMilliseconds;
                applyResult = () => "RimLLM_EmbeddingTestSuccess".Translate(dims, elapsed);
            }
            catch (Exception ex)
            {
                sw.Stop();
                string msg = "RimLLM_EmbeddingTestFailed".Translate(RimLLMLog.SanitizeForLog(ex.Message, 200));
                applyResult = () => msg;
            }

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                Testing[providerId] = false;
                TestStatus[providerId] = applyResult();
            });
        });
    }

    /// <summary>向下相容既有呼叫。</summary>
    public static void DrawEmbeddingSettings(Listing_Standard listing)
    {
        DrawRightDetailContent(listing);
    }
}
