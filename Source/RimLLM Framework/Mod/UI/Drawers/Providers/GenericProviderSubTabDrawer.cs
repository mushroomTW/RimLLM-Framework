using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimWorld;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 處理標準 HTTP / API Key 的通用 Provider 繪製與狀態維護邏輯。
    /// </summary>
    public static class GenericProviderSubTabDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

#pragma warning disable S2386, S3887 // reason: UI 狀態跨實例共享，刻意使用可變靜態集合並於存取處受控，維持現狀
        public static readonly Dictionary<string, string> FetchStatus = new Dictionary<string, string>();
        public static readonly Dictionary<string, bool> Fetching = new Dictionary<string, bool>();
        public static readonly Dictionary<string, string> TestStatus = new Dictionary<string, string>();
        public static readonly Dictionary<string, bool> Testing = new Dictionary<string, bool>();
        public static readonly Dictionary<string, Vector2> ModelScrollPositions = new Dictionary<string, Vector2>();

        /// <summary>各供應商的模型清單搜尋字串。供應商之間互不影響。</summary>
        public static readonly Dictionary<string, string> ModelFilters = new Dictionary<string, string>();

        /// <summary>哪幾把金鑰目前是明文顯示。鍵為 providerId + ":" + 索引；預設全部遮罩。</summary>
        public static readonly Dictionary<string, bool> RevealedKeys = new Dictionary<string, bool>();
#pragma warning restore S2386, S3887

        /// <summary>金鑰顯示切換鈕的寬度。</summary>
        private const float RevealButtonWidth = 40f;

        /// <summary>
        /// 清掉某供應商所有金鑰的顯示狀態。
        /// 新增或刪除金鑰會讓索引整體位移，不清除的話「已顯示」旗標會落到別把金鑰上。
        /// </summary>
        private static void ResetRevealedKeys(string providerId)
        {
            var stale = new List<string>();
#pragma warning disable S3267 // reason: 需同時篩選並收集鍵，Where 改寫可讀性未提升，維持現狀
            foreach (var pair in RevealedKeys)
            {
                if (pair.Key.StartsWith(providerId + ":", StringComparison.Ordinal))
                {
                    stale.Add(pair.Key);
                }
            }
#pragma warning restore S3267
            foreach (string key in stale)
            {
                RevealedKeys.Remove(key);
            }
        }

        public static void DrawGenericProviderSettings(Listing_Standard listing, string providerId)
        {
            // 1. 啟用 / 停用
            bool enabled = Settings.IsProviderEnabled(providerId);
            listing.CheckboxLabeled("RimLLM_EnableProvider".Translate(), ref enabled);
            Settings.SetProviderEnabled(providerId, enabled);
            if (!enabled) return;

            // 2. API 金鑰列表
            DrawApiKeyList(listing, providerId);

            // 3. Endpoint 設定（OpenAICompatible 模式顯示自訂端點與本地偵測；其他供應商若有中國端點則顯示切換）
            if (providerId == ProviderIds.OpenAICompatible)
            {
                string endpoint = Settings.GetEndpoint(providerId, "http://localhost:1234/v1");
                listing.Label("RimLLM_ApiEndpoint".Translate());
                endpoint = listing.TextEntry(endpoint);
                Settings.SetEndpoint(providerId, endpoint?.Trim());
                LocalProviderSubTabDrawer.DrawLocalDetectionControls(listing, providerId);
                listing.Gap(8f);
            }
            else
            {
                DrawChinaEndpointToggle(listing, providerId);
            }

            // 4. 動態獲取模型列表與展示
            DrawModelListSection(listing, providerId);

            // Fetch Models
            DrawFetchModelsButton(listing, providerId);

            listing.Gap(12f);

            // 4.9 呼叫統計與成功率及 API Cache Rate
            DrawProviderCallStats(listing, providerId);

            // 5. 連線測試
            DrawConnectionTest(listing, providerId);

            listing.GapLine(4f);
        }

        #pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
        public static void DrawApiKeyList(Listing_Standard listing, string providerId)
        {
            string rawApiKey = Settings.GetApiKey(providerId);
            var keys = new List<string>(rawApiKey.Split(new char[] { ',' }, StringSplitOptions.None));
            if (keys.Count == 0 || (keys.Count == 1 && string.IsNullOrEmpty(keys[0])))
            {
                keys = new List<string> { "" };
            }

            listing.Label("RimLLM_ApiKey".Translate());

            int keyToDelete = -1;
            for (int i = 0; i < keys.Count; i++)
            {
                Rect rowRect = listing.GetRect(30f);
                bool canDelete = keys.Count > 1 || !string.IsNullOrEmpty(keys[i]);

                // 由右至左配置：刪除鈕（可刪時）、顯示切換鈕，其餘給輸入框。
                float reservedRight = RevealButtonWidth + 8f + (canDelete ? 40f : 0f);
                Rect inputRect = new Rect(rowRect.x, rowRect.y, rowRect.width - reservedRight, rowRect.height);
                Rect revealRect = new Rect(inputRect.xMax + 8f, rowRect.y, RevealButtonWidth, rowRect.height);
                Rect deleteRect = new Rect(rowRect.x + rowRect.width - 32f, rowRect.y, 32f, rowRect.height);

                string revealKey = providerId + ":" + i;
                bool revealed = RevealedKeys.TryGetValue(revealKey, out bool r) && r;

                keys[i] = RimLLMUIStyle.DrawMaskableKeyField(inputRect, revealRect, keys[i], ref revealed);
                RevealedKeys[revealKey] = revealed;

                if (canDelete && Widgets.ButtonText(deleteRect, "-"))
                {
                    keyToDelete = i;
                }
                listing.Gap(4f);
            }

            if (keyToDelete != -1)
            {
                keys.RemoveAt(keyToDelete);
                if (keys.Count == 0) keys.Add("");
                ResetRevealedKeys(providerId);
            }

            Rect addKeyRowRect = listing.GetRect(28f);
            Rect addKeyBtnRect = new Rect(addKeyRowRect.x, addKeyRowRect.y, 140f, addKeyRowRect.height);
            if (Widgets.ButtonText(addKeyBtnRect, "RimLLM_AddApiKeyBtn".Translate()))
            {
                keys.Add("");
                ResetRevealedKeys(providerId);
            }
            listing.Gap(8f);

            string newRawKey = string.Join(",", keys);
            Settings.SetApiKey(providerId, newRawKey);
        }
        #pragma warning restore S3776

        public static void DrawChinaEndpointToggle(Listing_Standard listing, string providerId)
        {
            if (ProviderIds.HasChinaEndpoint(providerId))
            {
                bool isChina = Settings.IsChinaMode(providerId);
                bool oldIsChina = isChina;
                listing.CheckboxLabeled("RimLLM_ChinaEndpointToggle".Translate(), ref isChina);
                if (isChina != oldIsChina)
                {
                    Settings.SetChinaMode(providerId, isChina);
                    Settings.Write();
                }
            }
            listing.Gap(8f);
        }

        public static void DrawModelListSection(Listing_Standard listing, string providerId)
        {
            listing.Label("RimLLM_AvailableModelsTitle".Translate());

            var currentModels = Settings.GetModelList(providerId);
            if (currentModels.Count == 0)
            {
                listing.Label("RimLLM_NoCachedModels".Translate());
                return;
            }

            // 搜尋列：模型動輒數百筆，沒有過濾等於要在 220px 高的框裡目視搜尋。
            string filter = ModelFilters.TryGetValue(providerId, out string f) ? f : "";
            Rect searchRowRect = listing.GetRect(28f);
            float searchLabelWidth = Text.CalcSize("RimLLM_Search".Translate() + ": ").x;
            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
            {
                Widgets.Label(
                    new Rect(searchRowRect.x, searchRowRect.y, searchLabelWidth, searchRowRect.height),
                    "RimLLM_Search".Translate() + ": ");
            }
            Rect searchFieldRect = new Rect(
                searchRowRect.x + searchLabelWidth + 4f,
                searchRowRect.y,
                searchRowRect.width - searchLabelWidth - 4f,
                searchRowRect.height);
            ModelFilters[providerId] = Widgets.TextField(searchFieldRect, filter);
            listing.Gap(4f);

            List<string> visibleModels = RimLLMUIStyle.FilterModels(currentModels, ModelFilters[providerId]);

            Rect scrollRect = listing.GetRect(220f);
            Widgets.DrawMenuSection(scrollRect);

            if (visibleModels.Count == 0)
            {
                using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
                {
                    Widgets.Label(scrollRect, "<color=grey>" + "RimLLM_NoMatchingModels".Translate() + "</color>");
                }
                return;
            }

            float contentWidth = scrollRect.width - 16f;
            float chipHeight = 28f;
            float gap = 8f;
            RimLLMUIStyle.ComputeChipLayout(contentWidth, gap, 220f, out int cols, out float chipWidth);

            int rows = Mathf.CeilToInt((float)visibleModels.Count / cols);
            float viewHeight = Mathf.Max(220f, rows * (chipHeight + gap) + gap);
            Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);

            if (!ModelScrollPositions.ContainsKey(providerId))
            {
                ModelScrollPositions[providerId] = Vector2.zero;
            }
            Vector2 scrollPos = ModelScrollPositions[providerId];

            Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);
            ModelScrollPositions[providerId] = scrollPos;

            for (int i = 0; i < visibleModels.Count; i++)
            {
                string model = visibleModels[i];
                int col = i % cols;
                int row = i / cols;

                Rect chipRect = new Rect(
                    col * (chipWidth + gap) + gap,
                    row * (chipHeight + gap) + gap,
                    chipWidth,
                    chipHeight
                );

                Widgets.DrawBoxSolid(chipRect, RimLLMUIStyle.ChipFill);
                Widgets.DrawBox(chipRect, 1);

                if (Mouse.IsOver(chipRect))
                {
                    Widgets.DrawHighlight(chipRect);
                }
                TooltipHandler.TipRegion(chipRect, model + "\n\n" + "RimLLM_ClickToCopy".Translate());

                if (Widgets.ButtonInvisible(chipRect))
                {
                    string capturedModel = model;
                    var options = new List<FloatMenuOption>
                    {
                        new FloatMenuOption("RimLLM_ClickToCopyMenu".Translate(), () =>
                        {
                            GUIUtility.systemCopyBuffer = capturedModel;
                            Messages.Message("RimLLM_CopiedToClipboard".Translate(capturedModel), MessageTypeDefOf.TaskCompletion, false);
                        }),
                        new FloatMenuOption("RimLLM_AddToFallbackMenu".Translate(), () =>
                        {
                            string entry = $"{providerId}:{capturedModel}";
                            var chain = Settings.FallbackChain;
                            if (chain.Contains(entry))
                            {
                                Messages.Message("RimLLM_MsgModelExists".Translate(), MessageTypeDefOf.RejectInput, false);
                            }
                            else
                            {
                                chain.Add(entry);
                                Settings.FallbackChain = chain;
                                Settings.Write();
                                Messages.Message("RimLLM_MsgModelAdded".Translate(entry), MessageTypeDefOf.PositiveEvent, false);
                            }
                        })
                    };
                    Find.WindowStack.Add(new FloatMenu(options));
                }

                Rect textRect = chipRect.ContractedBy(4f);
                using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny, wordWrap: false))
                {
                    // 置中加硬切最難讀：改為左對齊並以省略號收尾，完整名稱留在 tooltip。
                    Widgets.Label(textRect, $"<color=silver>{model.Truncate(textRect.width)}</color>");
                }
            }

            Widgets.EndScrollView();
        }

        public static void DrawFetchModelsButton(Listing_Standard listing, string providerId)
        {
            DrawBusyActionRow(
                listing,
                Fetching.TryGetValue(providerId, out bool f) && f,
                "RimLLM_Fetching".Translate(),
                "RimLLM_FetchModelsBtn".Translate(),
                FetchStatus.TryGetValue(providerId, out string m) ? m : "RimLLM_FetchStatusNotRun".Translate().ToString(),
                () => StartFetchModels(providerId));
        }

        /// <summary>
        /// 「按鈕 + 右側狀態訊息」的共用列。忙碌時按鈕換成靜態標籤，避免重複觸發。
        /// 抓模型清單與連線測試的版面完全相同，共用同一份實作以免兩處的間距各自漂移。
        /// </summary>
        private static void DrawBusyActionRow(
            Listing_Standard listing, bool busy, string busyLabel, string buttonLabel, string statusText, Action onClick)
        {
            Rect rowRect = listing.GetRect(60f);
            Rect btnRect = new Rect(rowRect.x, rowRect.y + 15f, 180f, 30f);
            Rect msgRect = new Rect(rowRect.x + 190f, rowRect.y, rowRect.width - 190f, 60f);

            if (busy)
            {
                Widgets.Label(btnRect, busyLabel);
            }
            else if (Widgets.ButtonText(btnRect, buttonLabel))
            {
                onClick();
            }

            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
#pragma warning disable S1066, S3267 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀
            {
                Widgets.Label(msgRect, statusText);
            }
        }

        public static void DrawProviderCallStats(Listing_Standard listing, string providerId)
        {
            listing.Label($"<b>{"RimLLM_ProviderCallStatsTitle".Translate()}</b>");
            int successCount = 0;
            int failureCount = 0;
            long apiTotalTokens = 0;
            long apiCachedTokens = 0;
            if (RimLLMProvider.TryGetManager(out var managerInstance) &&
                managerInstance.UsageTracker.ProviderStatistics.TryGetValue(providerId, out var stats))
            {
                successCount = stats.SuccessCount;
                failureCount = stats.FailureCount;
                apiTotalTokens = stats.TotalPromptTokens;
                apiCachedTokens = stats.CachedPromptTokens;
            }
            int totalCalls = successCount + failureCount;
            float successRate = totalCalls > 0 ? (successCount * 100f) / totalCalls : 100f;

            listing.Label("RimLLM_ProviderTotalCallsLabel".Translate(totalCalls));
            listing.Label("RimLLM_ProviderSuccessCallsLabel".Translate(successCount, failureCount));
            listing.Label("RimLLM_ProviderSuccessRateLabel".Translate(successRate.ToString("F1")));

            RimLLMUIStyle.DrawRatioBar(
                listing.GetRect(20f),
                totalCalls > 0 ? (float)successCount / totalCalls : 0f,
                totalCalls > 0 ? RimLLMUIStyle.BarTrackDanger : RimLLMUIStyle.BarTrack,
                RimLLMUIStyle.BarFillSuccess,
                totalCalls > 0 ? $"{successRate:F1}%" : "100.0% (N/A)");
            listing.Gap(12f);

            if (apiTotalTokens > 0)
            {
                float apiCacheRate = (apiCachedTokens * 100f) / apiTotalTokens;
                listing.Label("RimLLM_ProviderApiCacheRateLabel".Translate(apiCacheRate.ToString("F1"), apiCachedTokens, apiTotalTokens));

                RimLLMUIStyle.DrawRatioBar(
                    listing.GetRect(20f),
                    (float)apiCachedTokens / apiTotalTokens,
                    RimLLMUIStyle.BarTrack,
                    RimLLMUIStyle.BarFillCache,
                    $"{apiCacheRate:F1}%");
                listing.Gap(12f);
            }
        }

        public static void DrawConnectionTest(Listing_Standard listing, string providerId)
        {
            listing.Label("RimLLM_ConnectionTestTitle".Translate());
            string status = TestStatus.TryGetValue(providerId, out string s) ? s : "RimLLM_TestStatusNotRun".Translate().ToString();
            DrawBusyActionRow(
                listing,
                Testing.TryGetValue(providerId, out bool val) && val,
                "RimLLM_Testing".Translate(),
                "RimLLM_TestConnectionBtn".Translate(),
                "RimLLM_TestResult".Translate(status),
                () => StartTest(providerId));
        }

        public static void StartFetchModels(string providerId)
        {
            RunProviderOperation(
                providerId,
                Fetching,
                FetchStatus,
                "RimLLM_Fetching".Translate(),
                async () =>
                {
                    var models = await RimLLMProvider.FetchProviderModelsAsync(providerId).ConfigureAwait(false);
                    // 寫入設定必須回到主線程，因此以延遲委派形式交還。
                    return () =>
                    {
                        if (models == null || models.Count == 0)
                        {
                            return "RimLLM_FetchSuccessEmpty".Translate();
                        }
                        Settings.SetModelList(providerId, models);
                        Settings.Write();
                        return "RimLLM_FetchSuccessCount".Translate(models.Count);
                    };
                },
                ex => "RimLLM_FetchFailed".Translate() + " (" + RimLLMLog.SanitizeForLog(ex.Message, 220) + ")");
        }

        public static void StartTest(string providerId)
        {
            RunProviderOperation(
                providerId,
                Testing,
                TestStatus,
                "RimLLM_Testing".Translate(),
                async () =>
                {
                    TestResult result = await RimLLMProvider.TestProviderAsync(providerId).ConfigureAwait(false);
                    return () => result.Success
                        ? "RimLLM_TestStatusSuccess".Translate(result.LatencyMs, result.Model).ToString()
                        : "RimLLM_TestStatusFailed".Translate(result.ErrorMessage).ToString();
                },
                ex => "RimLLM_TestStatusError".Translate(RimLLMLog.SanitizeForLog(ex.Message, 220)));
        }

        /// <summary>
        /// 「抓模型清單」與「連線測試」共用的背景操作外殼：
        /// 金鑰前置檢查、忙碌旗標、狀態訊息與主線程回寫。
        /// <paramref name="operation"/> 於背景執行緒執行，回傳一個在主線程執行並產生狀態字串的委派，
        /// 讓需要寫入設定的收尾動作能安全地回到主線程。
        /// </summary>
        private static void RunProviderOperation(
            string providerId,
            Dictionary<string, bool> busyFlags,
            Dictionary<string, string> statusMessages,
            string busyLabel,
            Func<Task<Func<string>>> operation,
            Func<Exception, string> describeError)
        {
            // 本地相容介面不需要金鑰；其餘供應商未填金鑰時不必真的送出請求。
            if (providerId != ProviderIds.OpenAICompatible && string.IsNullOrEmpty(Settings.GetApiKey(providerId)))
            {
                statusMessages[providerId] = "RimLLM_EnterApiKey".Translate();
                return;
            }

            busyFlags[providerId] = true;
            statusMessages[providerId] = busyLabel;

            Task.Run(async () =>
            {
                Func<string> applyResult;
                try
                {
                    applyResult = await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    string message = describeError(ex);
                    applyResult = () => message;
                }

                RimLLMDispatcher.EnqueueOnMainThread(() =>
                {
                    busyFlags[providerId] = false;
                    statusMessages[providerId] = applyResult();
                });
            });
        }
    }
#pragma warning restore S1066, S3267
}