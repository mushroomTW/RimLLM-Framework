using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 「連接 AI」三步驟精靈：選 AI 公司 → 貼金鑰 → 一鍵測試。
/// 第三步會依序抓模型清單、測試連線，成功後把測試用的模型自動加進備援鏈，
/// 省掉原本最容易漏掉的「到模型設置分頁手動新增」。
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
        new() { Id = ProviderIds.Gemini, Name = "Google Gemini", DescKey = "RimLLM_SetupDesc_Gemini", TagKey = "RimLLM_SetupTag_Recommended", Url = "https://aistudio.google.com/apikey" },
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
    private static Choice _pick;
    private static bool _revealKey;
    private static bool _busy;
    private static string _progressKey;
    private static Outcome _outcome;
    private static string _addedEntry;
    private static string _failKey;
    private static string _failDetail;

    private const float CardHeight = 64f;
    private const float CardGap = 10f;
    private const float BigButtonHeight = 40f;

    /// <summary>回到第一步，清掉上一次的結果。從首頁進來時呼叫。</summary>
    public static void Restart()
    {
        if (_busy) return;
        _step = 1;
        _pick = null;
        _revealKey = false;
        _outcome = Outcome.None;
    }

    public static float GetHeight(float width)
    {
        return 460f;
    }

    public static void DrawSetupWizard(Listing_Standard listing)
    {
        DrawStepBar(listing);
        listing.Gap(12f);

        switch (_step)
        {
            case 1: DrawStepPick(listing); break;
            case 2: DrawStepKey(listing); break;
            default: DrawStepTest(listing); break;
        }
    }

    private static void DrawStepBar(Listing_Standard listing)
    {
        Rect bar = listing.GetRect(32f);
        float gap = 6f;
        float width = (bar.width - gap * 2f) / 3f;
        for (int i = 1; i <= 3; i++)
        {
            Rect cell = new Rect(bar.x + (i - 1) * (width + gap), bar.y, width, bar.height);
            bool now = i == _step;
            bool done = i < _step || (i == 3 && _outcome == Outcome.Success);
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
        });
    }

    // ── 第 3 步：一鍵測試並自動設定 ──────────────────────

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
        }
        else if (_outcome == Outcome.Failed)
        {
            DrawResultBox(listing, RimLLMUIStyle.Danger,
                "RimLLM_SetupFailTitle".Translate(),
                _failKey.Translate(),
                "RimLLM_SetupBackBtn".Translate(),
                () => { _outcome = Outcome.None; _step = 2; });
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
    /// 依序：（本機 AI）找到伺服器 → 抓模型清單 → 測試連線 → 成功就把測試模型加進備援鏈。
    /// 網路動作在背景執行，所有設定寫入都回到主線程。
    /// </summary>
    private static void StartSetup(string providerId)
    {
        Settings.SetProviderEnabled(providerId, true);
        Settings.Write();

        _busy = true;
        _outcome = Outcome.None;
        _progressKey = providerId == ProviderIds.OpenAICompatible ? "RimLLM_SetupProgressDetect" : "RimLLM_SetupProgressFetch";

        Task.Run(async () =>
        {
            try
            {
                if (!RimLLMProvider.TryGetManager(out var manager))
                {
                    await Finish(() => Fail("RimLLM_SetupFail_Other", "SDK not initialized")).ConfigureAwait(false);
                    return;
                }

                if (providerId == ProviderIds.OpenAICompatible)
                {
                    LocalEndpointProbe.Target hit = await LocalEndpointProbe.FindReachableAsync(GenericProviderSubTabDrawer.LocalTargets).ConfigureAwait(false);
                    if (hit == null)
                    {
                        await Finish(() => Fail("RimLLM_SetupFail_LocalNotFound", null)).ConfigureAwait(false);
                        return;
                    }
                    await Finish(() =>
                    {
                        Settings.SetEndpoint(providerId, hit.BaseUrl);
                        Settings.Write();
                        _progressKey = "RimLLM_SetupProgressFetch";
                    }).ConfigureAwait(false);
                }

                // 模型清單抓不到不算失敗：金鑰錯誤時下一步的連線測試會給出更明確的錯誤碼。
                try
                {
                    var catalog = await manager.FetchProviderModelCatalogAsync(providerId).ConfigureAwait(false);
                    await Finish(() =>
                    {
                        if (catalog.Models != null && catalog.Models.Count > 0)
                        {
                            Settings.SetModelList(providerId, catalog.Models);
                            Settings.SetProviderContextWindows(providerId, catalog.ContextWindows);
                            Settings.Write();
                        }
                        _progressKey = "RimLLM_SetupProgressTest";
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"Setup wizard: fetching models for {providerId} failed: {RimLLMLog.SanitizeForLog(ex.Message, 220)}");
                    await Finish(() => _progressKey = "RimLLM_SetupProgressTest").ConfigureAwait(false);
                }

                TestResult result = await RimLLMProvider.TestProviderAsync(providerId).ConfigureAwait(false);
                await Finish(() =>
                {
                    if (!result.Success)
                    {
                        Fail(SetupStatus.DescribeFailureKey(result.ErrorCode), result.ErrorMessage);
                        return;
                    }

                    string entry = $"{providerId}:{result.Model}";
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
