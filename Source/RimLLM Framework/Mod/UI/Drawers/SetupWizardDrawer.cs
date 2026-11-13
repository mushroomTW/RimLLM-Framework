using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Manager.Services;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 「連接 AI」四步驟精靈：選 AI 公司（預選 Gemini）→ 貼金鑰 → 選模型 → 一鍵測試。
/// 框架不提供預設模型，因此第三步會抓模型清單讓玩家挑對話模型；能沿用同一把金鑰的
/// Embedding 與決策模型（例如 Gemini）也在同一頁選，不選就不開啟。第四步用選好的模型
/// 實際測試，成功後把它加進備援鏈，省掉原本最容易漏掉的「到模型設置分頁手動新增」。
/// 它只是既有設定的捷徑：寫入的都是供應商分頁與模型設置分頁本來就會寫的欄位。
/// </summary>
public static class SetupWizardDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private sealed class Choice
    {
        public string Id;
        /// <summary>品牌名稱；為 null 時改用 <see cref="NameKey"/> 的翻譯。</summary>
        public string Name;
        public string NameKey;
        public string DescKey;
        public string TagKey;
        /// <summary>取得金鑰（或下載 App）的網頁；本機 AI 沒有網頁。</summary>
        public string Url;

        public string DisplayName => Name ?? NameKey.Translate().ToString();
    }

    /// <summary>第一步的大卡片：常見、好上手的六家。</summary>
    private static readonly Choice[] Featured =
    [
        new() { Id = ProviderIds.Gemini, Name = "Google Gemini", DescKey = "RimLLM_SetupDesc_Gemini", TagKey = "RimLLM_SetupTag_Recommended", Url = "https://aistudio.google.com/api-keys" },
        new() { Id = ProviderIds.DeepSeek, Name = "DeepSeek", DescKey = "RimLLM_SetupDesc_DeepSeek", TagKey = "RimLLM_SetupTag_Cheap", Url = "https://platform.deepseek.com/api_keys" },
        new() { Id = ProviderIds.OpenRouter, Name = "OpenRouter", DescKey = "RimLLM_SetupDesc_OpenRouter", TagKey = "RimLLM_SetupTag_FreeModels", Url = "https://openrouter.ai/keys" },
        new() { Id = ProviderIds.OpenAI, Name = "OpenAI (ChatGPT)", DescKey = "RimLLM_SetupDesc_OpenAI", Url = "https://platform.openai.com/api-keys" },
        new() { Id = ProviderIds.Player2, Name = "Player2", DescKey = "RimLLM_SetupDesc_Player2", TagKey = "RimLLM_SetupTag_NoKey", Url = "https://player2.game" },
        new() { Id = ProviderIds.OpenAICompatible, NameKey = "RimLLM_SetupLocalName", DescKey = "RimLLM_SetupDesc_Local", TagKey = "RimLLM_SetupTag_NoKey" }
    ];

    /// <summary>收在「更多」選單裡的其餘內建供應商。</summary>
    private static readonly Choice[] More =
    [
        new() { Id = ProviderIds.Groq, Name = "Groq", Url = "https://console.groq.com/keys" },
        new() { Id = ProviderIds.Grok, Name = "Grok (xAI)", Url = "https://console.x.ai" },
        new() { Id = ProviderIds.Zai, Name = "Z.ai", Url = "https://z.ai/manage-apikey/apikey-list" },
        new() { Id = ProviderIds.Kimi, Name = "Kimi", Url = "https://platform.kimi.ai/console/api-keys" },
        new() { Id = ProviderIds.MiniMax, Name = "MiniMax", Url = "https://platform.minimax.io/console/keys" },
        new() { Id = ProviderIds.Qwen, Name = "Qwen", Url = "https://modelstudio.console.alibabacloud.com/?tab=playground#/api-key" },
        new() { Id = ProviderIds.Nvidia, Name = "NVIDIA", Url = "https://build.nvidia.com/settings/api-keys" }
    ];

    private enum Outcome { None, Success, Failed }

    private static int _step = 1;
    private static Choice _pick = Featured[0];
    private static bool _revealKey;
    private static bool _busy;
    private static string _progressKey;
    private static Outcome _outcome;
    private static string _addedEntry;
    private static string _failKey;
    private static string _failDetail;

    // 第 3 步「選模型」。_fetchGeneration 讓玩家回上一步後，舊的背景抓取結果不會蓋掉新的。
    private static int _fetchGeneration;
    private static bool _modelsBusy;
    private static List<string> _chatModels = [];
    private static string _chatModel = "";
    private static string _modelsFailKey;
    private static string _modelsFailDetail;
    /// <summary>能沿用對話金鑰、且目前沒開啟的 Embedding 供應商；null 代表這頁不提供選擇。</summary>
    private static string _embeddingPickProvider;
    private static bool _embeddingListBusy;
    private static List<string> _embeddingModels = [];
    /// <summary>玩家選的 Embedding 模型；空字串代表不開啟。</summary>
    private static string _embeddingModel = "";
    private static string _decisionPickProvider;
    private static bool _decisionListBusy;
    private static List<string> _decisionModels = [];
    private static string _decisionModel = "";

    // 成功頁「選用」區的 Embedding 一鍵開啟狀態
    private static bool _embeddingBusy;
    private static Outcome _embeddingOutcome;
    private static string _embeddingFailDetail;

    // 成功頁「選用」區的決策模型一鍵開啟狀態
    private static bool _decisionBusy;
    private static Outcome _decisionOutcome;
    private static string _decisionFailDetail;

    private const float CardHeight = 64f;
    private const float CardGap = 10f;
    private const float BigButtonHeight = 40f;

    /// <summary>回到第一步，清掉上一次的結果。從首頁進來時呼叫。</summary>
    public static void Restart()
    {
        // 只有對話測試進行中才擋：它的回呼會改步驟與結果。Embedding／決策的背景測試
        // 只更新自己那一列的狀態，玩家按「完成」時照常重設精靈。
        if (_busy) return;
        _fetchGeneration++;
        _modelsBusy = _embeddingListBusy = _decisionListBusy = false;
        _step = 1;
        _pick = Featured[0];
        _revealKey = false;
        _outcome = Outcome.None;
        _embeddingOutcome = Outcome.None;
        _decisionOutcome = Outcome.None;
    }

    public static float GetHeight(float width)
    {
        return 600f;
    }

    public static void DrawSetupWizard(Listing_Standard listing)
    {
        DrawStepBar(listing);
        listing.Gap(12f);

        switch (_step)
        {
            case 1: DrawStepPick(listing); break;
            case 2: DrawStepKey(listing); break;
            case 3: DrawStepModels(listing); break;
            default: DrawStepTest(listing); break;
        }
    }

    private static void DrawStepBar(Listing_Standard listing)
    {
        Rect bar = listing.GetRect(32f);
        float gap = 6f;
        const int steps = 4;
        float width = (bar.width - gap * (steps - 1)) / steps;
        for (int i = 1; i <= steps; i++)
        {
            Rect cell = new Rect(bar.x + (i - 1) * (width + gap), bar.y, width, bar.height);
            bool now = i == _step;
            bool done = i < _step || (i == steps && _outcome == Outcome.Success);
            RimLLMUIStyle.DrawSelectableFrame(cell, now);
            Color color = done ? RimLLMUIStyle.StatusGreen : now ? Color.white : RimLLMUIStyle.Muted;
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
            {
                Widgets.Label(cell, $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{$"RimLLM_SetupStep{i}".Translate()}</color>");
            }
        }
    }

    // ── 第 1 步：選 AI 公司 ──────────────────────────────

    private static void DrawStepPick(Listing_Standard listing)
    {
        DrawHeading(listing, "RimLLM_SetupPickTitle".Translate());

        const int cols = 3;
        int rows = Mathf.CeilToInt(Featured.Length / (float)cols);
        Rect grid = listing.GetRect(rows * (CardHeight + CardGap));
        float cardWidth = (grid.width - CardGap * (cols - 1)) / cols;
        for (int i = 0; i < Featured.Length; i++)
        {
            Rect card = new Rect(
                grid.x + (i % cols) * (cardWidth + CardGap),
                grid.y + (i / cols) * (CardHeight + CardGap),
                cardWidth,
                CardHeight);
            DrawChoiceCard(card, Featured[i]);
        }

        Rect moreRow = listing.GetRect(30f);
        if (Widgets.ButtonText(new Rect(moreRow.x, moreRow.y, 260f, moreRow.height), "RimLLM_SetupMoreBtn".Translate()))
        {
            var options = new List<FloatMenuOption>();
            foreach (Choice choice in More)
            {
                Choice captured = choice;
                options.Add(new FloatMenuOption(choice.DisplayName, () => _pick = captured));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        if (_pick != null && Array.IndexOf(More, _pick) >= 0)
        {
            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
            {
                Widgets.Label(new Rect(moreRow.x + 270f, moreRow.y, moreRow.width - 270f, moreRow.height),
                    "RimLLM_SetupPicked".Translate(_pick.DisplayName));
            }
        }

        listing.Gap(16f);
        DrawNavRow(listing, showBack: false, "RimLLM_SetupNextBtn".Translate(), _pick != null, () =>
        {
            _revealKey = false;
            _step = 2;
        });
    }

    private static void DrawChoiceCard(Rect card, Choice choice)
    {
        bool selected = _pick == choice;
        Widgets.DrawBoxSolid(card, RimLLMUIStyle.ChipFill);
        RimLLMUIStyle.DrawSelectableFrame(card, selected);
        if (selected)
        {
            Color old = GUI.color;
            GUI.color = RimLLMUIStyle.StatusBlue;
            Widgets.DrawBox(card, 2);
            GUI.color = old;
        }
        if (Mouse.IsOver(card)) Widgets.DrawHighlight(card);
        if (Widgets.ButtonInvisible(card)) _pick = choice;

        Rect inner = card.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), $"<b>{choice.DisplayName}</b>");
        using (RimLLMUIStyle.With(font: GameFont.Tiny))
        {
            GUI.color = RimLLMUIStyle.Muted;
            Widgets.Label(new Rect(inner.x, inner.y + 26f, inner.width, inner.height - 26f), choice.DescKey.Translate());
            GUI.color = Color.white;

            if (choice.TagKey != null)
            {
                string tag = choice.TagKey.Translate();
                float tagWidth = Text.CalcSize(tag).x + 10f;
                Rect tagRect = new Rect(inner.xMax - tagWidth, inner.y, tagWidth, 18f);
                Widgets.DrawBoxSolid(tagRect, new Color(0.18f, 0.42f, 0.2f, 0.9f));
                using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
                {
                    Widgets.Label(tagRect, tag);
                }
            }
        }
    }

    // ── 第 2 步：貼金鑰（或確認本機 AI 已開啟）──────────────

    private static bool NeedsKey(string providerId)
    {
        return ProviderIds.RequiresApiKey(providerId, Settings.GetEndpoint(providerId, null));
    }

    private static void DrawStepKey(Listing_Standard listing)
    {
        string id = _pick.Id;
        bool canNext = true;

        if (NeedsKey(id))
        {
            DrawHeading(listing, "RimLLM_SetupKeyTitle".Translate());
            listing.Label("RimLLM_SetupKeyHelp".Translate(_pick.DisplayName));
            listing.Gap(6f);

            Rect urlRow = listing.GetRect(32f);
            if (Widgets.ButtonText(new Rect(urlRow.x, urlRow.y, 300f, urlRow.height), "RimLLM_SetupOpenSiteBtn".Translate(_pick.DisplayName)))
            {
                Application.OpenURL(_pick.Url);
            }
            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny))
            {
                GUI.color = RimLLMUIStyle.Muted;
                Widgets.Label(new Rect(urlRow.x + 310f, urlRow.y, urlRow.width - 310f, urlRow.height), _pick.Url);
                GUI.color = Color.white;
            }
            listing.Gap(10f);

            string raw = Settings.GetApiKey(id);
            int keyCount = string.IsNullOrEmpty(raw) ? 0 : raw.Split(RimLLMFrameworkSettings.ApiKeySeparators, StringSplitOptions.RemoveEmptyEntries).Length;
            if (keyCount > 1)
            {
                // 多把金鑰只在進階的「AI 公司」頁編輯，精靈裡改一把會把其他把一起覆蓋掉。
                listing.Label("RimLLM_SetupKeysAlreadySet".Translate(keyCount));
            }
            else
            {
                listing.Label("RimLLM_SetupKeyFieldLabel".Translate());
                Rect keyRow = listing.GetRect(30f);
                Rect inputRect = new Rect(keyRow.x, keyRow.y, keyRow.width - 48f, keyRow.height);
                Rect revealRect = new Rect(inputRect.xMax + 8f, keyRow.y, 40f, keyRow.height);
                string edited = RimLLMUIStyle.DrawMaskableKeyField(inputRect, revealRect, raw ?? "", ref _revealKey);
                if (edited != (raw ?? ""))
                {
                    // 從網頁複製常會多帶空白或換行，直接去掉，免得第三步才失敗。
                    Settings.SetApiKey(id, edited.Trim());
                }
                canNext = !string.IsNullOrEmpty(Settings.GetApiKey(id));
            }

            if (ProviderIds.HasChinaEndpoint(id))
            {
                listing.Gap(6f);
                RimLLMUIStyle.DrawHint(listing, "RimLLM_SetupChinaHint".Translate());
            }
        }
        else
        {
            DrawHeading(listing, "RimLLM_SetupNoKeyTitle".Translate());
            listing.Label((id == ProviderIds.Player2 ? "RimLLM_SetupPlayer2Help" : "RimLLM_SetupLocalHelp").Translate());
            if (_pick.Url != null)
            {
                listing.Gap(6f);
                Rect urlRow = listing.GetRect(32f);
                if (Widgets.ButtonText(new Rect(urlRow.x, urlRow.y, 300f, urlRow.height), "RimLLM_SetupOpenSiteBtn".Translate(_pick.DisplayName)))
                {
                    Application.OpenURL(_pick.Url);
                }
            }
        }

        listing.Gap(16f);
        DrawNavRow(listing, showBack: true, "RimLLM_SetupNextBtn".Translate(), canNext, () =>
        {
            Settings.Write();
            _outcome = Outcome.None;
            _step = 3;
            StartFetchModels(id);
        });
    }

    // ── 第 3 步：選模型 ──────────────────────────────────

    private static void DrawStepModels(Listing_Standard listing)
    {
        DrawHeading(listing, "RimLLM_SetupModelsTitle".Translate());
        listing.Label("RimLLM_SetupModelsHelp".Translate());
        listing.Gap(8f);

        if (_modelsBusy)
        {
            listing.Label(_progressKey.Translate());
        }
        else
        {
            if (_modelsFailKey != null)
            {
                RimLLMUIStyle.DrawHint(listing, _modelsFailKey.Translate());
                if (!string.IsNullOrEmpty(_modelsFailDetail))
                {
                    RimLLMUIStyle.DrawHint(listing, "RimLLM_SetupFailDetail".Translate(_modelsFailDetail));
                }
                Rect retryRow = listing.GetRect(30f);
                if (Widgets.ButtonText(new Rect(retryRow.x, retryRow.y, 220f, retryRow.height), "RimLLM_SetupRetryFetchBtn".Translate()))
                {
                    StartFetchModels(_pick.Id);
                }
                listing.Gap(6f);
            }

            if (_chatModels.Count > 0)
            {
                DrawOptionalRow(listing, "RimLLM_SetupChatModel".Translate(), DescribePick(_chatModel, "RimLLM_SelectModelPlaceholder"),
                    "RimLLM_SetupChooseModelBtn".Translate(),
                    () => Find.WindowStack.Add(new Dialog_SelectModel(_chatModels, model => _chatModel = model)));
            }
            else
            {
                // 抓不到清單（或伺服器回傳空清單）時讓玩家手動輸入模型名稱。
                listing.Label("RimLLM_SetupManualModelLabel".Translate());
                _chatModel = listing.TextEntry(_chatModel ?? "").Trim();
            }

            if (_embeddingPickProvider != null || _decisionPickProvider != null)
            {
                listing.Gap(14f);
                listing.Label($"<b>{"RimLLM_SetupOptionalTitle".Translate()}</b>");
                RimLLMUIStyle.DrawHint(listing, "RimLLM_SetupOptionalModelsHint".Translate(_pick.DisplayName));
                if (_embeddingPickProvider != null)
                {
                    DrawOptionalModelRow(listing, "RimLLM_ServiceEmbedding".Translate(), _embeddingListBusy, _embeddingModels, _embeddingModel, model => _embeddingModel = model);
                }
                if (_decisionPickProvider != null)
                {
                    DrawOptionalModelRow(listing, "RimLLM_ServiceDecision".Translate(), _decisionListBusy, _decisionModels, _decisionModel, model => _decisionModel = model);
                }
            }
        }

        listing.Gap(16f);
        DrawNavRow(listing, showBack: true, "RimLLM_SetupNextBtn".Translate(), !_modelsBusy && !string.IsNullOrEmpty(_chatModel), () =>
        {
            _outcome = Outcome.None;
            _step = 4;
        });
    }

    private static string DescribePick(string model, string emptyKey)
    {
        return string.IsNullOrEmpty(model)
            ? $"<color=#{ColorUtility.ToHtmlStringRGB(RimLLMUIStyle.Muted)}>{emptyKey.Translate()}</color>"
            : model;
    }

    /// <summary>選用服務的模型列：清單抓取中、抓不到、或「不使用＋各模型」的選單。</summary>
    private static void DrawOptionalModelRow(Listing_Standard listing, string name, bool busy, List<string> models, string current, Action<string> onPick)
    {
        if (busy)
        {
            DrawOptionalRow(listing, name, "RimLLM_SetupProgressFetch".Translate(), null, null);
            return;
        }
        if (models.Count == 0)
        {
            DrawOptionalRow(listing, name, "RimLLM_SetupOptionalNoModels".Translate(), null, null);
            return;
        }
        DrawOptionalRow(listing, name, DescribePick(current, "RimLLM_SetupOptionalSkip"), "RimLLM_SetupChooseModelBtn".Translate(), () =>
        {
            var options = new List<FloatMenuOption> { new("RimLLM_SetupOptionalSkip".Translate(), () => onPick("")) };
            options.AddRange(models.Select(model => new FloatMenuOption(model, () => onPick(model))));
            Find.WindowStack.Add(new FloatMenu(options));
        });
    }

    /// <summary>
    /// 抓對話模型清單（本機 AI 先找伺服器）；同時替能沿用這把金鑰的 Embedding／決策模型抓清單。
    /// 網路動作在背景執行，結果回到主線程才寫入；回上一步再進來時舊結果會被丟掉。
    /// </summary>
    private static void StartFetchModels(string providerId)
    {
        Settings.SetProviderEnabled(providerId, true);
        Settings.Write();

        int generation = ++_fetchGeneration;
        _modelsBusy = true;
        _chatModels = [];
        _chatModel = "";
        _modelsFailKey = null;
        _modelsFailDetail = null;
        _progressKey = providerId == ProviderIds.OpenAICompatible ? "RimLLM_SetupProgressDetect" : "RimLLM_SetupProgressFetch";

        // 玩家已經自己開啟的服務不在這裡改。
        string embedding = SetupStatus.OneClickEmbeddingProviderFor(providerId);
        _embeddingPickProvider = embedding != null && Settings.EmbeddingProvider == RimLLMFrameworkSettings.DisabledProvider ? embedding : null;
        string decision = SetupStatus.OneClickDecisionProviderFor(providerId);
        _decisionPickProvider = decision != null && Settings.DecisionProvider == RimLLMDecisionProviderCatalog.Disabled ? decision : null;
        _embeddingModel = "";
        _decisionModel = "";
        _embeddingModels = [];
        _decisionModels = [];

        Task.Run(async () =>
        {
            try
            {
                if (!RimLLMProvider.TryGetManager(out var manager))
                {
                    await FinishFetch(generation, () => FailFetch("RimLLM_SetupFail_Other", "SDK not initialized")).ConfigureAwait(false);
                    return;
                }

                if (providerId == ProviderIds.OpenAICompatible)
                {
                    LocalEndpointProbe.Target hit = await LocalEndpointProbe.FindReachableAsync(GenericProviderSubTabDrawer.LocalTargets).ConfigureAwait(false);
                    if (hit == null)
                    {
                        await FinishFetch(generation, () => FailFetch("RimLLM_SetupFail_LocalNotFound", null)).ConfigureAwait(false);
                        return;
                    }
                    await FinishFetch(generation, () =>
                    {
                        Settings.SetEndpoint(providerId, hit.BaseUrl);
                        Settings.Write();
                        _progressKey = "RimLLM_SetupProgressFetch";
                    }).ConfigureAwait(false);
                }

                var catalog = await manager.FetchProviderModelCatalogAsync(providerId).ConfigureAwait(false);
                await FinishFetch(generation, () =>
                {
                    if (catalog.Models != null && catalog.Models.Count > 0)
                    {
                        Settings.SetModelList(providerId, catalog.Models);
                        Settings.SetProviderContextWindows(providerId, catalog.ContextWindows);
                        Settings.Write();
                        _chatModels = new List<string>(catalog.Models);
                    }
                    else
                    {
                        _modelsFailKey = "RimLLM_SetupModelsEmpty";
                    }
                    _modelsBusy = false;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"Setup wizard: fetching models for {providerId} failed: {RimLLMLog.SanitizeForLog(ex.Message, 220)}");
                string key = ex is RimLLMException llm ? SetupStatus.DescribeFailureKey(llm.Error) : "RimLLM_SetupFail_Other";
                await FinishFetch(generation, () => FailFetch(key, ex.Message)).ConfigureAwait(false);
            }
        });

        if (_embeddingPickProvider != null) StartFetchEmbeddingList(_embeddingPickProvider, generation);
        if (_decisionPickProvider != null) StartFetchDecisionList(_decisionPickProvider, generation);
    }

    private static void FailFetch(string key, string detail)
    {
        _modelsFailKey = key;
        _modelsFailDetail = string.IsNullOrEmpty(detail) ? null : RimLLMLog.SanitizeForLog(detail, 220);
        _modelsBusy = false;
    }

    /// <summary>回到主線程執行；這段期間玩家若回上一步重新抓取，就丟掉這次的結果。</summary>
    private static Task FinishFetch(int generation, Action action)
    {
        return Finish(() =>
        {
            if (generation == _fetchGeneration) action();
        });
    }

    /// <summary>Embedding 金鑰留空時服務會沿用對話金鑰，所以第二步貼的金鑰就能抓清單。</summary>
    private static void StartFetchEmbeddingList(string embeddingProviderId, int generation)
    {
        _embeddingListBusy = true;
        string endpoint = Settings.GetEmbeddingEndpoint(embeddingProviderId);
        string apiKey = Settings.GetEmbeddingApiKey(embeddingProviderId);
        Task.Run(async () =>
        {
            List<string> models = [];
            try
            {
                var result = await new RimLLMEmbeddingService(Settings).FetchAvailableModelsAsync(embeddingProviderId, endpoint, apiKey).ConfigureAwait(false);
                models = result?.Models ?? [];
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"Setup wizard: fetching embedding models for {embeddingProviderId} failed: {RimLLMLog.SanitizeForLog(ex.Message, 220)}");
            }
            await FinishFetch(generation, () =>
            {
                if (models.Count > 0)
                {
                    Settings.SetModelList(RimLLMEmbeddingService.GetModelListKey(embeddingProviderId), models);
                    Settings.Write();
                }
                _embeddingModels = models;
                _embeddingListBusy = false;
            }).ConfigureAwait(false);
        });
    }

    /// <summary>決策金鑰留空時沿用對話金鑰（見 <see cref="RimLLMDecisionProvider.ChatKeyProviderId"/>）。</summary>
    private static void StartFetchDecisionList(string decisionProviderId, int generation)
    {
        _decisionListBusy = true;
        var service = new RimLLMDecisionService(Settings);
        DecisionPreviewArguments args = service.GetPreviewArguments(decisionProviderId);
        Task.Run(async () =>
        {
            List<string> models = [];
            try
            {
                var fetched = await service.FetchModelsAsync(args.Endpoint, args.Model, args.ApiKey).ConfigureAwait(false);
                models = fetched.Where(model => model != null && !string.IsNullOrEmpty(model.Name)).Select(model => model.Name).ToList();
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"Setup wizard: fetching decision models for {decisionProviderId} failed: {RimLLMLog.SanitizeForLog(ex.Message, 220)}");
            }
            await FinishFetch(generation, () =>
            {
                if (models.Count > 0)
                {
                    Settings.SetModelList(RimLLMDecisionService.GetModelListKey(decisionProviderId), models);
                    Settings.Write();
                }
                _decisionModels = models;
                _decisionListBusy = false;
            }).ConfigureAwait(false);
        });
    }

    // ── 第 4 步：用選好的模型測試並自動設定 ──────────────────

    private static void DrawStepTest(Listing_Standard listing)
    {
        DrawHeading(listing, "RimLLM_SetupTestTitle".Translate());
        listing.Label("RimLLM_SetupTestHelp".Translate());
        listing.Gap(10f);

        Rect row = listing.GetRect(BigButtonHeight);
        Rect btn = new Rect(row.x, row.y, 220f, row.height);
        if (_busy)
        {
            GUI.color = RimLLMUIStyle.Muted;
            Widgets.ButtonText(btn, "RimLLM_SetupTestingBtn".Translate(), active: false);
            GUI.color = Color.white;
            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
            {
                Widgets.Label(new Rect(btn.xMax + 12f, row.y, row.width - btn.width - 12f, row.height), _progressKey.Translate());
            }
        }
        else if (Widgets.ButtonText(btn, "RimLLM_SetupTestBtn".Translate()))
        {
            StartSetup(_pick.Id);
        }
        listing.Gap(14f);

        if (_outcome == Outcome.Success)
        {
            DrawResultBox(listing, RimLLMUIStyle.StatusGreen,
                "RimLLM_SetupSuccessTitle".Translate(),
                "RimLLM_SetupSuccessBody".Translate(_addedEntry),
                "RimLLM_SetupDoneBtn".Translate(),
                () =>
                {
                    Restart();
                    RimLLMSettingsUI.OpenPage(RimLLMSettingsUI.HomePageId);
                });
            DrawOptionalServices(listing);
        }
        else if (_outcome == Outcome.Failed)
        {
            DrawResultBox(listing, RimLLMUIStyle.Danger,
                "RimLLM_SetupFailTitle".Translate(),
                _failKey.Translate(),
                "RimLLM_SetupBackBtn".Translate(),
                () => { _outcome = Outcome.None; _step = 3; });
            if (!string.IsNullOrEmpty(_failDetail))
            {
                RimLLMUIStyle.DrawHint(listing, "RimLLM_SetupFailDetail".Translate(_failDetail));
            }
        }

        // 失敗時結果框已有「上一步」按鈕，這裡只在尚未測試時顯示。
        if (!_busy && _outcome == Outcome.None)
        {
            listing.Gap(10f);
            DrawNavRow(listing, showBack: true, null, false, null);
        }
    }

    /// <summary>
    /// 成功頁的「選用」區：只有部分模組會用到 Embedding 與決策模型，因此不列入必要步驟、
    /// 也不影響首頁紅／綠。第三步選了模型的服務已在測試成功後開啟；其餘到進階頁選模型。
    /// </summary>
    private static void DrawOptionalServices(Listing_Standard listing)
    {
        listing.Gap(14f);
        listing.Label($"<b>{"RimLLM_SetupOptionalTitle".Translate()}</b>");
        bool choseOptional = !string.IsNullOrEmpty(_embeddingModel) || !string.IsNullOrEmpty(_decisionModel);
        // 第三步選了模型的服務已在測試成功後自動開啟，提示要說明這一點，而不是叫玩家「需要時才設定」。
        RimLLMUIStyle.DrawHint(listing, (choseOptional
            ? "RimLLM_SetupOptionalAutoHint"
            : "RimLLM_SetupOptionalHint").Translate());

        // Embedding
        bool embeddingOn = Settings.EmbeddingProvider != RimLLMFrameworkSettings.DisabledProvider;

        string embeddingStatus;
        if (_embeddingBusy)
            embeddingStatus = "RimLLM_SetupEmbeddingTesting".Translate();
        else if (_embeddingOutcome == Outcome.Failed)
            embeddingStatus = $"<color=#{ColorUtility.ToHtmlStringRGB(RimLLMUIStyle.Danger)}>{"RimLLM_SetupEmbeddingFailed".Translate()}</color>";
        else if (embeddingOn)
            embeddingStatus = $"<color=#{ColorUtility.ToHtmlStringRGB(RimLLMUIStyle.StatusGreen)}>{"RimLLM_ServiceStatusOn".Translate(EmbeddingSettingsDrawer.GetProviderDisplayName(Settings.EmbeddingProvider))}</color>";
        else
            embeddingStatus = "RimLLM_ServiceStatusOff".Translate();

        DrawOptionalRow(listing, "RimLLM_ServiceEmbedding".Translate(), embeddingStatus, "RimLLM_SetupOptionalOpenPage".Translate(), _embeddingBusy ? null : RimLLMSettingsUI.OpenEmbeddingPage);
        if (_embeddingOutcome == Outcome.Failed && !string.IsNullOrEmpty(_embeddingFailDetail))
        {
            RimLLMUIStyle.DrawHint(listing, "RimLLM_SetupFailDetail".Translate(_embeddingFailDetail));
        }

        // 決策模型：Gemini 可沿用同一把金鑰（第三步選模型）；其他對話供應商要另外申請決策服務的金鑰，只能到進階頁設定。
        bool decisionOn = Settings.DecisionProvider != RimLLMDecisionProviderCatalog.Disabled;

        string decisionStatus;
        if (_decisionBusy)
            decisionStatus = "RimLLM_SetupDecisionTesting".Translate();
        else if (_decisionOutcome == Outcome.Failed)
            decisionStatus = $"<color=#{ColorUtility.ToHtmlStringRGB(RimLLMUIStyle.Danger)}>{"RimLLM_SetupDecisionFailed".Translate()}</color>";
        else if (decisionOn)
            decisionStatus = $"<color=#{ColorUtility.ToHtmlStringRGB(RimLLMUIStyle.StatusGreen)}>{"RimLLM_ServiceStatusOn".Translate(DecisionSettingsDrawer.GetProviderDisplayName(Settings.DecisionProvider))}</color>";
        else
            decisionStatus = "RimLLM_ServiceStatusOff".Translate();

        DrawOptionalRow(listing, "RimLLM_ServiceDecision".Translate(), decisionStatus, "RimLLM_SetupOptionalOpenPage".Translate(), _decisionBusy ? null : RimLLMSettingsUI.OpenDecisionPage);
        if (_decisionOutcome == Outcome.Failed && !string.IsNullOrEmpty(_decisionFailDetail))
        {
            RimLLMUIStyle.DrawHint(listing, "RimLLM_SetupFailDetail".Translate(_decisionFailDetail));
        }
        if (SetupStatus.OneClickDecisionProviderFor(_pick.Id) == null)
        {
            RimLLMUIStyle.DrawHint(listing, "RimLLM_SetupDecisionHint".Translate());
        }
    }

    /// <summary>
    /// 對話測試成功後，開啟玩家在第三步選了模型、能沿用同一把金鑰的 Embedding 與決策模型
    /// （例如 Gemini 三項全包），玩家只要貼一次金鑰。沒選模型的服務不開；玩家已經自己開啟的服務不覆蓋；
    /// 每項都實際測試，失敗就還原。
    /// </summary>
    private static void EnableChosenOptionalServices()
    {
        if (_embeddingPickProvider != null && !string.IsNullOrEmpty(_embeddingModel) &&
            Settings.EmbeddingProvider == RimLLMFrameworkSettings.DisabledProvider)
        {
            Settings.SetEmbeddingModel(_embeddingPickProvider, _embeddingModel);
            StartEmbeddingOneClick(_embeddingPickProvider);
        }

        if (_decisionPickProvider != null && !string.IsNullOrEmpty(_decisionModel) &&
            Settings.DecisionProvider == RimLLMDecisionProviderCatalog.Disabled)
        {
            Settings.SetDecisionModel(_decisionPickProvider, _decisionModel);
            StartDecisionOneClick(_decisionPickProvider);
        }
    }

    /// <summary>
    /// 開啟決策模型並馬上送一題真的決策驗證；失敗就還原，免得留下「顯示使用中、實際不能用」的設定。
    /// 決策金鑰留空，交給服務沿用對話金鑰。
    /// </summary>
    private static void StartDecisionOneClick(string decisionProviderId)
    {
        string previous = Settings.DecisionProvider;
        Settings.DecisionProvider = decisionProviderId;
        Settings.Write();

        _decisionBusy = true;
        _decisionOutcome = Outcome.None;

        var service = new RimLLMDecisionService(Settings);
        DecisionPreviewArguments args = service.GetPreviewArguments(decisionProviderId);

        Task.Run(async () =>
        {
            string error = null;
            try
            {
                await service.TestConnectionAsync(args.Endpoint, args.Model, args.ApiKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = RimLLMLog.SanitizeForLog(ex.Message, 220);
            }

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                _decisionBusy = false;
                if (error == null)
                {
                    _decisionOutcome = Outcome.Success;
                    return;
                }
                // 測試期間玩家可能已到決策頁改選別家；只有設定仍是這裡開的那一家才還原。
                if (Settings.DecisionProvider == decisionProviderId)
                {
                    Settings.DecisionProvider = previous;
                    Settings.Write();
                }
                _decisionOutcome = Outcome.Failed;
                _decisionFailDetail = error;
            });
        });
    }

    /// <summary>「名稱｜狀態｜按鈕」一列。首頁與成功頁共用，<paramref name="onClick"/> 為 null 時不畫按鈕。</summary>
    internal static void DrawOptionalRow(Listing_Standard listing, string name, string status, string buttonLabel, Action onClick)
    {
        Rect row = listing.GetRect(32f);
        Widgets.DrawLineHorizontal(row.x, row.yMax, row.width);
        const float buttonWidth = 240f;
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(new Rect(row.x + 4f, row.y, 170f, row.height), name);
            Widgets.Label(new Rect(row.x + 180f, row.y, row.width - 180f - buttonWidth - 8f, row.height), status);
        }
        if (onClick != null && Widgets.ButtonText(new Rect(row.xMax - buttonWidth, row.y + 2f, buttonWidth, row.height - 4f), buttonLabel))
        {
            onClick();
        }
    }

    /// <summary>
    /// 開啟 Embedding 並馬上用真的一次向量請求驗證；失敗就還原成原本的設定，
    /// 免得留下一個「顯示使用中、實際不能用」的 Embedding。金鑰留空，交給服務沿用對話金鑰。
    /// </summary>
    private static void StartEmbeddingOneClick(string embeddingProviderId)
    {
        string previous = Settings.EmbeddingProvider;
        Settings.EmbeddingProvider = embeddingProviderId;
        Settings.Write();

        _embeddingBusy = true;
        _embeddingOutcome = Outcome.None;

        string model = Settings.GetEmbeddingModel(embeddingProviderId);
        string endpoint = Settings.GetEmbeddingEndpoint(embeddingProviderId);
        string apiKey = Settings.GetEmbeddingApiKey(embeddingProviderId);

        Task.Run(async () =>
        {
            string error = null;
            try
            {
                var service = new RimLLMEmbeddingService(Settings);
                await service.TestEmbeddingAsync(embeddingProviderId, model, endpoint, apiKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = RimLLMLog.SanitizeForLog(ex.Message, 220);
            }

            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                _embeddingBusy = false;
                if (error == null)
                {
                    _embeddingOutcome = Outcome.Success;
                    return;
                }
                Settings.EmbeddingProvider = previous;
                Settings.Write();
                _embeddingOutcome = Outcome.Failed;
                _embeddingFailDetail = error;
            });
        });
    }

    private static void DrawResultBox(Listing_Standard listing, Color tint, string title, string body, string buttonLabel, Action onClick)
    {
        Rect box = listing.GetRect(76f);
        Widgets.DrawBoxSolid(box, new Color(tint.r, tint.g, tint.b, 0.12f));
        GUI.color = tint;
        Widgets.DrawBox(box, 2);
        GUI.color = Color.white;

        Rect text = new Rect(box.x + 12f, box.y + 8f, box.width - 200f, box.height - 16f);
        using (RimLLMUIStyle.With(TextAnchor.UpperLeft, GameFont.Medium))
        {
            Widgets.Label(new Rect(text.x, text.y, text.width, 30f), $"<color=#{ColorUtility.ToHtmlStringRGB(tint)}><b>{title}</b></color>");
        }
        Widgets.Label(new Rect(text.x, text.y + 32f, text.width, text.height - 32f), body);

        if (Widgets.ButtonText(new Rect(box.xMax - 176f, box.y + (box.height - 36f) / 2f, 160f, 36f), buttonLabel))
        {
            onClick();
        }
    }

    /// <summary>
    /// 用第三步選好的模型送一則極短訊息測試；成功就把它加進備援鏈，
    /// 再開啟玩家在第三步選了模型的 Embedding／決策模型。網路動作在背景執行，設定寫入都回到主線程。
    /// </summary>
    private static void StartSetup(string providerId)
    {
        string model = _chatModel;
        _busy = true;
        _outcome = Outcome.None;
        _progressKey = "RimLLM_SetupProgressTest";

        Task.Run(async () =>
        {
            try
            {
                if (!RimLLMProvider.TryGetManager(out var manager))
                {
                    await Finish(() => Fail("RimLLM_SetupFail_Other", "SDK not initialized")).ConfigureAwait(false);
                    return;
                }

                TestResult result = await manager.TestProviderAsync(providerId, model).ConfigureAwait(false);
                await Finish(() =>
                {
                    if (!result.Success)
                    {
                        Fail(SetupStatus.DescribeFailureKey(result.ErrorCode), result.ErrorMessage);
                        return;
                    }

                    string entry = $"{providerId}:{model}";
                    List<string> chain = Settings.FallbackChain;
                    if (!chain.Contains(entry))
                    {
                        chain.Add(entry);
                        Settings.FallbackChain = chain;
                        Settings.Write();
                    }
                    _addedEntry = entry;
                    _outcome = Outcome.Success;
                    _busy = false;
                    EnableChosenOptionalServices();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string message = RimLLMLog.SanitizeForLog(ex.Message, 220);
                await Finish(() => Fail("RimLLM_SetupFail_Other", message)).ConfigureAwait(false);
            }
        });
    }

    private static void Fail(string key, string detail)
    {
        _failKey = key;
        _failDetail = string.IsNullOrEmpty(detail) ? null : RimLLMLog.SanitizeForLog(detail, 220);
        _outcome = Outcome.Failed;
        _busy = false;
    }

    /// <summary>把動作排到主線程執行，並等它跑完再繼續下一個階段。</summary>
    private static Task Finish(Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RimLLMDispatcher.EnqueueOnMainThread(() =>
        {
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                _busy = false;
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    // ── 共用小元件 ──────────────────────────────────

    private static void DrawHeading(Listing_Standard listing, string text)
    {
        using (RimLLMUIStyle.With(font: GameFont.Medium))
        {
            listing.Label($"<b>{text}</b>");
        }
        listing.Gap(4f);
    }

    /// <summary>底部「上一步／下一步」列。下一步不可按時以灰色顯示，避免玩家以為按了沒反應。</summary>
    private static void DrawNavRow(Listing_Standard listing, bool showBack, string nextLabel, bool canNext, Action onNext)
    {
        Rect row = listing.GetRect(BigButtonHeight);
        if (showBack && !_busy && Widgets.ButtonText(new Rect(row.x, row.y, 140f, row.height), "RimLLM_SetupBackBtn".Translate()))
        {
            _outcome = Outcome.None;
            _step = Math.Max(1, _step - 1);
        }

        if (nextLabel == null) return;
        Rect next = new Rect(row.xMax - 200f, row.y, 200f, row.height);
        if (!canNext) GUI.color = RimLLMUIStyle.Muted;
        bool clicked = Widgets.ButtonText(next, nextLabel, active: canNext);
        GUI.color = Color.white;
        if (clicked && canNext) onNext();
    }
}
