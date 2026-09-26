using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 本地端點探測的一個候選目標：顯示名稱、寫入設定的端點、實際送出探測請求的位址。
    /// BaseUrl 與 TestUrl 分開是因為 Ollama 原生位址（/api/tags）與 OpenAI 相容位址（/v1）不同，
    /// 探測成功後一律寫入相容位址（見 <see cref="DrawerShared.OllamaV1Endpoint"/>）。
    /// </summary>
    public struct LocalDetectTarget
    {
        public string Name;
        public string BaseUrl;
        public string TestUrl;

        public LocalDetectTarget(string name, string baseUrl, string testUrl)
        {
            Name = name;
            BaseUrl = baseUrl;
            TestUrl = testUrl;
        }
    }

    /// <summary>
    /// 各設定分頁 Drawer 共用的繪製與背景工作輔助。
    /// 先前 SubTab 選單列、下拉列、模型晶片網格、抓取／測試外殼與本地端點探測
    /// 以近乎相同的程式碼散落在各 Drawer，改一處就得逐檔跟著改，因此收攏至此。
    /// 收攏只搬移版面與流程，間距、寬度與翻譯鍵皆沿用原值，外觀與行為不變。
    /// </summary>
    public static class DrawerShared
    {
        /// <summary>Ollama 原生探測目標的顯示名稱，命中時改寫入下方的相容端點。</summary>
        public const string OllamaRawTargetName = "Ollama (Raw)";

        /// <summary>Ollama 的 OpenAI 相容端點，探測成功後一律寫入此值。</summary>
        public const string OllamaV1Endpoint = "http://localhost:11434/v1";

        /// <summary>
        /// 中欄 SubTab 選單的外框：標題列＋捲動清單。供應商分頁與 Embedding 分頁的
        /// 選單骨架完全相同（內縮 6、標題 20、清單位移 4／24、內容高度＝筆數×（高＋間隙）＋10），
        /// 差異只有每列的狀態文字，因此列的繪製由呼叫端以委派傳入。
        /// </summary>
        public static void DrawSubTabMenu(
            Rect rect,
            string titleKey,
            int entryCount,
            float buttonHeight,
            float buttonGap,
            ref Vector2 scrollPos,
            Action<Listing_Standard> drawEntries)
        {
            Rect contentRect = rect.ContractedBy(6f);

            Rect titleRect = new Rect(contentRect.x, contentRect.y, contentRect.width, 20f);
            Text.Font = GameFont.Small;
            Widgets.Label(titleRect, titleKey.Translate());

            Rect listRect = new Rect(contentRect.x, titleRect.yMax + 4f, contentRect.width, contentRect.height - 24f);
            float viewHeight = entryCount * (buttonHeight + buttonGap) + 10f;
            Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, viewHeight);

            Widgets.BeginScrollView(listRect, ref scrollPos, viewRect);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            if (drawEntries != null)
            {
                drawEntries(listing);
            }
            listing.End();

            Widgets.EndScrollView();
        }

        /// <summary>
        /// SubTab 選單的一列：選取外框＋隱形按鈕＋名稱列＋狀態列。
        /// 選中時名稱為白色粗體，否則為銀灰色；狀態文字與顏色由呼叫端依自身規則算好傳入。
        /// </summary>
        public static void DrawSubTabButton(
            Listing_Standard listing,
            string label,
            string entryId,
            string activeId,
            float buttonHeight,
            string statusText,
            Color statusColor,
            Action onSelect)
        {
            Rect btnRect = listing.GetRect(buttonHeight);
            RimLLMUIStyle.DrawSelectableFrame(btnRect, activeId == entryId);

            if (Widgets.ButtonInvisible(btnRect) && onSelect != null)
            {
                onSelect();
            }

            Rect nameRect = new Rect(btnRect.x + 8f, btnRect.y + 3f, btnRect.width - 16f, 22f);
            Rect statusRect = new Rect(btnRect.x + 8f, btnRect.y + 25f, btnRect.width - 16f, 18f);
            Text.Font = GameFont.Small;
            string nameText = activeId == entryId
                ? "<color=white><b>" + label + "</b></color>"
                : "<color=silver>" + label + "</color>";
            Widgets.Label(nameRect, nameText);

            Color oldColor = GUI.color;
            GUI.color = statusColor;
            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                Widgets.Label(statusRect, statusText);
            }
            GUI.color = oldColor;
        }

        /// <summary>
        /// 「左側標籤＋右側下拉按鈕」的共用列。路由策略、預算政策、思考強度、
        /// 對話測試模型選擇原本各寫一份相同的標籤量測與按鈕排版，共用後不再各自漂移。
        /// <paramref name="labelWidth"/> 小於 0 時以標籤文字自動量測（＋5）且按鈕間隙取 15，
        /// 否則使用固定標籤寬且間隙取 10；兩種組合恰好覆蓋既有呼叫端的原值。
        /// </summary>
        public static void DrawDropdownRow(
            Listing_Standard listing,
            string labelText,
            string buttonText,
            float buttonWidth,
            Action onClick,
            float labelWidth = -1f)
        {
            Rect rowRect = listing.GetRect(30f);
            float resolvedLabelWidth = labelWidth >= 0f ? labelWidth : Text.CalcSize(labelText).x + 5f;
            float gap = labelWidth >= 0f ? 10f : 15f;
            Rect labelRect = new Rect(rowRect.x, rowRect.y, resolvedLabelWidth, rowRect.height);
            Rect btnRect = new Rect(rowRect.x + resolvedLabelWidth + gap, rowRect.y, buttonWidth, rowRect.height);

            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
            {
                Widgets.Label(labelRect, labelText);
            }

            if (Widgets.ButtonText(btnRect, buttonText) && onClick != null)
            {
                onClick();
            }
        }

        /// <summary>
        /// 以字串陣列為本的索引式下拉選單：索引即為設定值，顯示與選單共用同一份清單，
        /// 不需要另外維護一份 switch 對照。路由策略與預算政策共用此實作。
        /// </summary>
        public static List<FloatMenuOption> BuildIndexedOptions(
            string[] names,
            Func<int, string> labelKeyFor,
            Action<int> onSelect)
        {
            var options = new List<FloatMenuOption>(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                int captured = i;
                options.Add(new FloatMenuOption(
                    labelKeyFor(captured).Translate(),
                    () =>
                    {
                        if (onSelect != null)
                        {
                            onSelect(captured);
                        }
                    }));
            }
            return options;
        }

        /// <summary>
        /// 模型清單的搜尋列並回傳最新過濾字串。供應商分頁與 Embedding 分頁的搜尋排版相同，
        /// Embedding 多出的預設／已抓取提示列維持由呼叫端自行繪製。
        /// </summary>
        public static string DrawModelSearchRow(Listing_Standard listing, string currentFilter)
        {
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
            string result = Widgets.TextField(searchFieldRect, currentFilter ?? string.Empty);
            listing.Gap(4f);
            return result;
        }

        /// <summary>
        /// 模型晶片網格：欄數自適應、只畫可視列、懸停提示＋點擊委派。
        /// 供應商分頁與 Embedding 分頁的晶片繪製完全相同，差異只有捲動最小高度
        /// 與點擊後的選單（複製／加入備援鏈／設為模型），因此以後兩者為參數。
        /// 空清單時直接在框內顯示無符合提示，不開啟捲動視圖。
        /// </summary>
        public static void DrawModelChipGrid(
            Rect scrollRect,
            IList<string> visibleModels,
            ref Vector2 scrollPos,
            float minViewHeight,
            Action<string> onChipClick)
        {
            if (visibleModels == null || visibleModels.Count == 0)
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
            float viewHeight = Mathf.Max(minViewHeight, rows * (chipHeight + gap) + gap);
            Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);

            Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);

            // 只畫可視範圍內的列；tooltip 後綴的翻譯提到迴圈外，不必每個晶片各查一次。
            RimLLMUIStyle.GetVisibleRowRange(scrollPos.y, scrollRect.height, chipHeight + gap, gap, rows, out int firstRow, out int endRow);
            string copyHint = "\n\n" + "RimLLM_ClickToCopy".Translate();

            for (int i = firstRow * cols; i < visibleModels.Count && i < endRow * cols; i++)
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
                    TooltipHandler.TipRegion(chipRect, model + copyHint);
                }

                if (Widgets.ButtonInvisible(chipRect) && onChipClick != null)
                {
                    onChipClick(model);
                }

                Rect textRect = chipRect.ContractedBy(4f);
                using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny, wordWrap: false))
                {
                    // 置中加硬切最難讀：改為左對齊並以省略號收尾，完整名稱留在 tooltip。
                    Widgets.Label(textRect, "<color=silver>" + RimLLMUIStyle.TruncateCached(model, textRect.width) + "</color>");
                }
            }

            Widgets.EndScrollView();
        }

        /// <summary>
        /// 「按鈕＋右側狀態訊息」的共用列。忙碌時按鈕換成靜態標籤，避免重複觸發。
        /// 抓模型清單與連線測試的版面完全相同，共用同一份實作以免兩處的間距各自漂移。
        /// 按鈕寬預設 200（Embedding 用值），供應商分頁傳 180；狀態區一律接在按鈕右 10 處。
        /// </summary>
        public static void DrawBusyActionRow(
            Listing_Standard listing,
            bool busy,
            string busyLabel,
            string buttonLabel,
            string statusText,
            Action onClick,
            float buttonWidth = 200f)
        {
            Rect rowRect = listing.GetRect(60f);
            Rect btnRect = new Rect(rowRect.x, rowRect.y + 15f, buttonWidth, 30f);
            Rect msgRect = new Rect(rowRect.x + buttonWidth + 10f, rowRect.y, rowRect.width - buttonWidth - 10f, 60f);

            if (busy)
            {
                Widgets.Label(btnRect, busyLabel);
            }
            else if (Widgets.ButtonText(btnRect, buttonLabel) && onClick != null)
            {
                onClick();
            }

            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
            {
                Widgets.Label(msgRect, statusText);
            }
        }

        /// <summary>
        /// 本地端點探測列：左側按鈕（偵測中改為灰色靜態標籤）＋右側狀態訊息。
        /// 按鈕寬由呼叫端傳入（供應商分頁 250、Embedding 分頁 200），狀態區一律接在右 10 處。
        /// </summary>
        public static void DrawDetectButtonRow(
            Listing_Standard listing,
            bool detecting,
            string statusText,
            Action onDetect,
            float buttonWidth = 200f)
        {
            Rect detectRect = listing.GetRect(30f);
            Rect detectBtnRect = new Rect(detectRect.x, detectRect.y, buttonWidth, detectRect.height);
            Rect detectStatusRect = new Rect(detectRect.x + buttonWidth + 10f, detectRect.y, detectRect.width - buttonWidth - 10f, detectRect.height);

            if (detecting)
            {
                GUI.color = Color.gray;
                Widgets.ButtonText(detectBtnRect, "RimLLM_DetectingLocal".Translate());
                GUI.color = Color.white;
            }
            else
            {
                if (Widgets.ButtonText(detectBtnRect, "RimLLM_DetectLocalBtn".Translate()) && onDetect != null)
                {
                    onDetect();
                }
            }

            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
            {
                Widgets.Label(detectStatusRect, statusText ?? string.Empty);
            }
        }

        /// <summary>
        /// 「抓取／測試」類背景操作的外殼：忙碌旗標、狀態訊息與主線程回寫。
        /// <paramref name="operation"/> 於背景執行緒執行，回傳一個在主線程執行並產生狀態字串的委派，
        /// 讓需要寫入設定的收尾動作能安全地回到主線程。
        /// </summary>
        public static void RunBackgroundOperation(
            Dictionary<string, bool> busyFlags,
            Dictionary<string, string> statusMessages,
            string key,
            string busyLabel,
            Func<Task<Func<string>>> operation,
            Func<Exception, string> describeError)
        {
            if (operation == null) return;

            busyFlags[key] = true;
            statusMessages[key] = busyLabel;

            Task.Run(async () =>
            {
                Func<string> applyResult;
                try
                {
                    applyResult = await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    string message = describeError != null ? describeError(ex) : ex.Message;
                    applyResult = () => message;
                }

                RimLLMDispatcher.EnqueueOnMainThread(() =>
                {
                    busyFlags[key] = false;
                    statusMessages[key] = applyResult();
                });
            });
        }

        /// <summary>
        /// 本地端點探測迴圈：依序對候選位址送出探測請求，首個成功者回報名稱與應寫入的端點，
        /// 全部失敗則回報失敗。成功／失敗回呼一律回到主線程執行，呼叫端可在其中安全寫入設定與彈訊息。
        /// 探測失敗屬正常情形（服務未啟動），直接繼續嘗試下一個候選端點。
        /// </summary>
        public static void ProbeLocalEndpoints(
            System.Net.Http.HttpClient client,
            IList<LocalDetectTarget> targets,
            Action<string, string> onSuccess,
            Action onFailure)
        {
            Task.Run(async () =>
            {
                if (client != null && targets != null)
                {
                    for (int i = 0; i < targets.Count; i++)
                    {
                        LocalDetectTarget target = targets[i];
                        try
                        {
                            var response = await client.GetAsync(target.TestUrl).ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                string finalUrl = target.Name == OllamaRawTargetName
                                    ? OllamaV1Endpoint
                                    : target.BaseUrl;
                                string capturedName = target.Name;
                                string capturedUrl = finalUrl;

                                RimLLMDispatcher.EnqueueOnMainThread(() =>
                                {
                                    if (onSuccess != null)
                                    {
                                        onSuccess(capturedName, capturedUrl);
                                    }
                                });
                                return;
                            }
                        }
                        catch
                        {
                            // 探測失敗屬正常情形，繼續嘗試下一個
                        }
                    }
                }

                RimLLMDispatcher.EnqueueOnMainThread(() =>
                {
                    if (onFailure != null)
                    {
                        onFailure();
                    }
                });
            });
        }

        /// <summary>
        /// 備援鏈去空去重後的有序條目。鏈內保證唯一，但舊存檔可能殘留重複，
        /// 去重後顯示才不會出現調了兩次的同一列。對話測試的模型下拉與模型設置頁共用。
        /// </summary>
        public static List<string> CollectDedupedChain(IList<string> chain)
        {
            var entries = new List<string>();
            if (chain == null) return entries;
            foreach (string entry in chain)
            {
                if (!string.IsNullOrEmpty(entry) && !entries.Contains(entry))
                {
                    entries.Add(entry);
                }
            }
            return entries;
        }
    }
}
