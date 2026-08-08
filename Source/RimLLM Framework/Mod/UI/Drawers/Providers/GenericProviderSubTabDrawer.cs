using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimWorld;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 處理標準 HTTP / API Key 的通用 Provider 繪製與狀態維護邏輯。
    /// </summary>
    public static class GenericProviderSubTabDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        public static readonly Dictionary<string, string> FetchStatus = new Dictionary<string, string>();
        public static readonly Dictionary<string, bool> Fetching = new Dictionary<string, bool>();
        public static readonly Dictionary<string, string> TestStatus = new Dictionary<string, string>();
        public static readonly Dictionary<string, bool> Testing = new Dictionary<string, bool>();
        public static readonly Dictionary<string, Vector2> ModelScrollPositions = new Dictionary<string, Vector2>();

        public static void DrawGenericProviderSettings(Listing_Standard listing, string providerId)
        {
            // 1. 啟用 / 停用
            bool enabled = Settings.IsProviderEnabled(providerId);
            listing.CheckboxLabeled("RimLLM_EnableProvider".Translate(), ref enabled);
            Settings.SetProviderEnabled(providerId, enabled);
            if (!enabled) return;

            // 2. API 金鑰列表
            DrawApiKeyList(listing, providerId);

            // 3. Endpoint 清除
            Settings.SetEndpoint(providerId, null);

            // 3.1 支援中國端點切換 (僅 Kimi, MiniMax, Qwen)
            DrawChinaEndpointToggle(listing, providerId);

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

                Rect inputRect;
                Rect deleteRect = Rect.zero;

                if (canDelete)
                {
                    inputRect = new Rect(rowRect.x, rowRect.y, rowRect.width - 40f, rowRect.height);
                    deleteRect = new Rect(rowRect.x + rowRect.width - 32f, rowRect.y, 32f, rowRect.height);
                }
                else
                {
                    inputRect = new Rect(rowRect.x, rowRect.y, rowRect.width, rowRect.height);
                }

                string oldVal = keys[i];
                string newVal = Widgets.TextField(inputRect, oldVal);
                if (newVal != oldVal)
                {
                    keys[i] = newVal;
                }

                if (canDelete)
                {
                    if (Widgets.ButtonText(deleteRect, "-"))
                    {
                        keyToDelete = i;
                    }
                }
                listing.Gap(4f);
            }

            if (keyToDelete != -1)
            {
                keys.RemoveAt(keyToDelete);
                if (keys.Count == 0) keys.Add("");
            }

            Rect addKeyRowRect = listing.GetRect(28f);
            Rect addKeyBtnRect = new Rect(addKeyRowRect.x, addKeyRowRect.y, 140f, addKeyRowRect.height);
            if (Widgets.ButtonText(addKeyBtnRect, "RimLLM_AddApiKeyBtn".Translate()))
            {
                keys.Add("");
            }
            listing.Gap(8f);

            string newRawKey = string.Join(",", keys);
            Settings.SetApiKey(providerId, newRawKey);
        }

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
            }
            else
            {
                Rect scrollRect = listing.GetRect(220f);
                Widgets.DrawMenuSection(scrollRect);

                float contentWidth = scrollRect.width - 16f;
                float chipWidth = 220f;
                float chipHeight = 28f;
                float gap = 8f;

                int cols = Mathf.Max(1, Mathf.FloorToInt((contentWidth + gap) / (chipWidth + gap)));
                int rows = Mathf.CeilToInt((float)currentModels.Count / cols);
                float viewHeight = Mathf.Max(220f, rows * (chipHeight + gap) + gap);

                Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);

                if (!ModelScrollPositions.ContainsKey(providerId))
                {
                    ModelScrollPositions[providerId] = Vector2.zero;
                }
                Vector2 scrollPos = ModelScrollPositions[providerId];

                Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);
                ModelScrollPositions[providerId] = scrollPos;

                for (int i = 0; i < currentModels.Count; i++)
                {
                    string model = currentModels[i];
                    int col = i % cols;
                    int row = i / cols;

                    Rect chipRect = new Rect(
                        col * (chipWidth + gap) + gap,
                        row * (chipHeight + gap) + gap,
                        chipWidth,
                        chipHeight
                    );

                    Widgets.DrawBoxSolid(chipRect, new Color(1f, 1f, 1f, 0.05f));
                    Widgets.DrawBox(chipRect, 1);

                    if (Mouse.IsOver(chipRect))
                    {
                        Widgets.DrawHighlight(chipRect);
                    }
                    TooltipHandler.TipRegion(chipRect, model);

                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    bool originalWordWrap = Text.WordWrap;
                    Text.WordWrap = false;

                    Rect textRect = chipRect.ContractedBy(2f);
                    Widgets.Label(textRect, $"<color=silver>{model}</color>");

                    Text.WordWrap = originalWordWrap;
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.Font = GameFont.Small;
                }

                Widgets.EndScrollView();
            }
        }

        public static void DrawFetchModelsButton(Listing_Standard listing, string providerId)
        {
            bool isFetching = Fetching.TryGetValue(providerId, out bool f) && f;
            string fetchMsg = FetchStatus.TryGetValue(providerId, out string m) ? m : "RimLLM_FetchStatusNotRun".Translate().ToString();
            Rect fetchRect = listing.GetRect(60f);
            Rect fetchBtnRect = new Rect(fetchRect.x, fetchRect.y + 15f, 180f, 30f);
            Rect fetchMsgRect = new Rect(fetchRect.x + 190f, fetchRect.y, fetchRect.width - 190f, 60f);
            if (isFetching)
            {
                Widgets.Label(fetchBtnRect, "RimLLM_Fetching".Translate());
            }
            else
            {
                if (Widgets.ButtonText(fetchBtnRect, "RimLLM_FetchModelsBtn".Translate()))
                {
                    StartFetchModels(providerId);
                }
            }
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(fetchMsgRect, fetchMsg);
            Text.Anchor = TextAnchor.UpperLeft;
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

            Rect successBarRect = listing.GetRect(20f);
            Widgets.DrawBoxSolid(successBarRect, new Color(0.35f, 0.15f, 0.15f, 0.6f));
            if (totalCalls > 0)
            {
                float fillPercent = (float)successCount / totalCalls;
                if (fillPercent > 0f)
                {
                    Rect fillRect = new Rect(successBarRect.x, successBarRect.y, successBarRect.width * fillPercent, successBarRect.height);
                    Widgets.DrawBoxSolid(fillRect, new Color(0.18f, 0.48f, 0.18f, 0.8f));
                }
            }
            else
            {
                Widgets.DrawBoxSolid(successBarRect, new Color(0.2f, 0.2f, 0.2f, 0.6f));
            }
            Widgets.DrawBox(successBarRect, 1);

            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = GameFont.Tiny;
            Widgets.Label(successBarRect, totalCalls > 0 ? $"{successRate:F1}%" : "100.0% (N/A)");
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            if (apiTotalTokens > 0)
            {
                float apiCacheRate = (apiCachedTokens * 100f) / apiTotalTokens;
                listing.Label("RimLLM_ProviderApiCacheRateLabel".Translate(apiCacheRate.ToString("F1"), apiCachedTokens, apiTotalTokens));

                Rect apiCacheBarRect = listing.GetRect(20f);
                Widgets.DrawBoxSolid(apiCacheBarRect, new Color(0.2f, 0.2f, 0.2f, 0.6f));
                float fillPercent = (float)apiCachedTokens / apiTotalTokens;
                if (fillPercent > 0f)
                {
                    Rect fillRect = new Rect(apiCacheBarRect.x, apiCacheBarRect.y, apiCacheBarRect.width * fillPercent, apiCacheBarRect.height);
                    Widgets.DrawBoxSolid(fillRect, new Color(0.15f, 0.45f, 0.6f, 0.8f));
                }
                Widgets.DrawBox(apiCacheBarRect, 1);

                Text.Anchor = TextAnchor.MiddleCenter;
                Text.Font = GameFont.Tiny;
                Widgets.Label(apiCacheBarRect, $"{apiCacheRate:F1}%");
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                listing.Gap(12f);
            }
        }

        public static void DrawConnectionTest(Listing_Standard listing, string providerId)
        {
            listing.Label("RimLLM_ConnectionTestTitle".Translate());
            bool isTesting = Testing.TryGetValue(providerId, out bool val) && val;
            string status = TestStatus.TryGetValue(providerId, out string s) ? s : "RimLLM_TestStatusNotRun".Translate().ToString();
            Rect btnRect = listing.GetRect(60f);
            Rect leftRect = new Rect(btnRect.x, btnRect.y + 15f, 180f, 30f);
            Rect rightRect = new Rect(btnRect.x + 190f, btnRect.y, btnRect.width - 190f, 60f);
            if (isTesting)
            {
                Widgets.Label(leftRect, "RimLLM_Testing".Translate());
            }
            else
            {
                if (Widgets.ButtonText(leftRect, "RimLLM_TestConnectionBtn".Translate()))
                {
                    StartTest(providerId);
                }
            }
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(rightRect, "RimLLM_TestResult".Translate(status));
            Text.Anchor = TextAnchor.UpperLeft;
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
}
