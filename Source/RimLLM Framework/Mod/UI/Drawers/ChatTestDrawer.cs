using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using UnityEngine;
using Verse;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責對話測試（ChatTest）分頁的 UI 渲染與對話生命週期狀態管理。
    /// </summary>
    public static class ChatTestDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        // 對話測試專屬的 UI 暫存狀態
        private static string chatInput = "";
        private static readonly List<string> chatHistory = new List<string>();
        private static Vector2 chatScrollPosition = Vector2.zero;
        private static bool chatLoading;
        private static ReasoningEffort? chatReasoningEffort;
        private static bool chatReasoningInitialized;

        /// <summary>聊天輸入框的控制項名稱，用於將 Enter 鍵綁定限縮在該欄位取得焦點時。</summary>
        private const string ChatInputControlName = "RimLLM_ChatInput";

        /// <summary>目前進行中請求的取消來源，供「清空」按鈕中止長時間回應。</summary>
        private static System.Threading.CancellationTokenSource chatCts;

        /// <summary>
        /// 判斷聊天輸入是否應該送出。純空白視為未輸入。
        /// </summary>
        internal static bool ShouldSendChatInput(string rawInput)
        {
            return !string.IsNullOrEmpty(rawInput) && rawInput.Trim().Length > 0;
        }

        /// <summary>
        /// 中止進行中的聊天請求（若有）。
        /// </summary>
        private static void CancelActiveChatRequest()
        {
            var cts = chatCts;
            chatCts = null;
            if (cts == null) return;

#pragma warning disable S108, S2486 // reason: 取消/釋放已釋放的 CTS 在 Unity 主線程屬預期競爭，刻意忽略不影響主流程
            try { cts.Cancel(); } catch { // 刻意忽略：CTS 可能已取消或已釋放，不影響主流程
            }
            try { cts.Dispose(); } catch { // 刻意忽略：重複釋放屬預期競爭，不影響主流程
            }
#pragma warning restore S108, S2486
            chatLoading = false;
        }

        /// <summary>
        /// 於主線程更新 AI 回覆佔位項目。索引失效（歷史已被清空）時只略過寫入，其餘收尾照常執行。
        /// </summary>
        /// <param name="persist">是否一併寫回遙測並解除載入狀態（串流結束時使用）。</param>
        /// <param name="scrollToBottom">是否把對話框捲到底。</param>
        private static void UpdateAiHistoryEntry(int index, string reply, bool persist = false, bool scrollToBottom = true)
        {
            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                if (index < chatHistory.Count)
                {
                    chatHistory[index] = "RimLLM_ChatAi".Translate() + " " + reply;
                }
                if (persist)
                {
                    PersistChatHistory();
                    chatLoading = false;
                }
                if (scrollToBottom)
                {
                    chatScrollPosition.y = 999999f;
                }
            });
        }

        /// <summary>
        /// 將目前的對話歷史複本寫回遙測儲存。
        /// </summary>
        private static void PersistChatHistory()
        {
            Settings.ChatHistory = new List<string>(chatHistory);
            Settings.SaveTelemetry();
        }

        /// <summary>
        /// 初始化對話歷史紀錄。
        /// </summary>
        public static void Initialize(RimLLMFrameworkSettings settings)
        {
            if (settings.ChatHistory != null && settings.ChatHistory.Count > 0)
            {
                chatHistory.Clear();
                chatHistory.AddRange(settings.ChatHistory);
            }
        }

        /// <summary>
        /// 獲取對話分頁詳細內容的滾動高度。
        /// </summary>
        public static float GetHeight(float width)
        {
            return 650f;
        }

        /// <summary>
        /// 繪製對話測試介面。
        /// </summary>
        #pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
        public static void DrawChatTestSettings(Listing_Standard listing)
        {
            if (!chatReasoningInitialized)
            {
                chatReasoningEffort = Settings.DefaultReasoningEffort;
                chatReasoningInitialized = true;
            }

            // 滾動顯示對話歷史
            Rect chatRect = listing.GetRect(480f);
            Widgets.DrawMenuSection(chatRect);

            float chatContentWidth = chatRect.width - 16f;

            // 分隔用 Environment.NewLine 而非 "\n"：與原本的 StringBuilder.AppendLine 一致，
            // 換行字元不同會讓 CalcHeight 的量測結果與實際繪製對不上。
            string allChatText = string.Join(Environment.NewLine + Environment.NewLine, chatHistory.ToArray());

            GUIStyle richLabelStyle = new GUIStyle(Text.CurFontStyle);
            richLabelStyle.richText = true;
            richLabelStyle.wordWrap = true;

            // 高度必須用同一個 rich text 樣式量測：Text.CalcHeight 會把標籤當成一般字元算進去，
            // 又不認得 <size> 造成的行高變化，換成 Markdown 之後兩邊的誤差會更明顯。
            float chatViewHeight = Math.Max(480f, richLabelStyle.CalcHeight(new GUIContent(allChatText), chatContentWidth - 8f) + 12f);
            Rect chatViewRect = new Rect(0f, 0f, chatContentWidth, chatViewHeight);

            Widgets.BeginScrollView(chatRect, ref chatScrollPosition, chatViewRect);
            Rect allChatRect = new Rect(4f, 4f, chatContentWidth - 8f, chatViewHeight - 8f);
            GUI.Label(allChatRect, allChatText, richLabelStyle);
            Widgets.EndScrollView();

            listing.Gap(6f);

            // 思考強度設定列
            Rect effortRowRect = listing.GetRect(30f);
            float effortLabelWidth = Text.CalcSize("RimLLM_ReasoningEffortLabel".Translate()).x;
            Rect effortLabelRect = new Rect(effortRowRect.x, effortRowRect.y, effortLabelWidth + 5f, effortRowRect.height);
            Rect effortBtnRect = new Rect(effortRowRect.x + effortLabelWidth + 15f, effortRowRect.y, 160f, effortRowRect.height);
            using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
#pragma warning disable S108, S1643, S2486, S8949 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀
            {
                Widgets.Label(effortLabelRect, "RimLLM_ReasoningEffortLabel".Translate());
            }
            string chatEffortLabel = chatReasoningEffort == null ? "RimLLM_ReasoningEffort_Auto".Translate() : $"RimLLM_ReasoningEffort_{chatReasoningEffort}".Translate();
            if (Widgets.ButtonText(effortBtnRect, chatEffortLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RimLLM_ReasoningEffort_Auto".Translate(), () => { chatReasoningEffort = null; }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_Low".Translate(), () => { chatReasoningEffort = ReasoningEffort.Low; }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_Medium".Translate(), () => { chatReasoningEffort = ReasoningEffort.Medium; }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_High".Translate(), () => { chatReasoningEffort = ReasoningEffort.High; })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
            listing.Gap(6f);

            // 輸入框、清空按鈕與發送按鈕
            Rect inputRowRect = listing.GetRect(30f);
            Rect textInputRect = new Rect(inputRowRect.x, inputRowRect.y, inputRowRect.width - 180f, inputRowRect.height);
            Rect clearBtnRect = new Rect(inputRowRect.x + inputRowRect.width - 170f, inputRowRect.y, 80f, inputRowRect.height);
            Rect sendBtnRect = new Rect(inputRowRect.x + inputRowRect.width - 80f, inputRowRect.y, 80f, inputRowRect.height);

            if (chatLoading)
            {
                int dotCount = 1 + (int)(Time.realtimeSinceStartup * 2.5) % 3;
                string dots = new string('.', dotCount);
                Widgets.Label(textInputRect, $"<color=silver>{"RimLLM_AiThinking".Translate()}{dots}</color>");
            }
            else
            {
                GUI.SetNextControlName(ChatInputControlName);
                chatInput = Widgets.TextField(textInputRect, chatInput);
            }

            // 點擊清空按鈕邏輯
            if (!chatLoading && Widgets.ButtonText(clearBtnRect, "RimLLM_ClearBtn".Translate()))
            {
                CancelActiveChatRequest();
                chatHistory.Clear();
                Settings.ChatHistory.Clear();
                Settings.SaveTelemetry();
                chatInput = "";
            }

            // Enter 僅在聊天輸入框取得焦點時才觸發送出。
            // 先前是全域 KeyDown 偵測，在設定視窗任何位置按 Enter 都會送出請求。
            bool pressEnter = Event.current.type == EventType.KeyDown &&
                              Event.current.keyCode == KeyCode.Return &&
                              GUI.GetNameOfFocusedControl() == ChatInputControlName;

            if (!chatLoading && (Widgets.ButtonText(sendBtnRect, "RimLLM_Send".Translate()) || pressEnter))
            {
                // 消耗事件，避免同一個 Enter 被 RimWorld 視窗系統重複處理（重複送出或誤關視窗）。
                if (pressEnter) Event.current.Use();

                if (ShouldSendChatInput(chatInput))
                {
                    // 先中止前一輪尚未完成的請求（此呼叫會把 chatLoading 歸零），再開始新一輪。
                    CancelActiveChatRequest();

                    string userPrompt = chatInput.Trim();
                    chatHistory.Add("RimLLM_ChatUser".Translate() + " " + userPrompt);

                    // 先新增一個 AI 回覆的佔位項目，以利後續串流更新
                    chatHistory.Add("RimLLM_ChatAi".Translate() + " ");
                    int aiHistoryIndex = chatHistory.Count - 1;

                    PersistChatHistory();
                    chatInput = "";
                    chatLoading = true;

                    object replyLock = new object();
                    string accumulatedReply = "";

                    var requestCts = new System.Threading.CancellationTokenSource();
                    chatCts = requestCts;

                    Task.Run(async () =>
                    {
                        try
                        {
                            var client = RimLLMProvider.CreateChatClient("RimLLM.DebugChat");
                            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, userPrompt) };
                            var options = new RimLLMChatOptions
                            {
                                MaxOutputTokens = 4096,
                                Temperature = 0.7f,
                                DisableReasoning = false,
                                Reasoning = chatReasoningEffort.HasValue ? new ReasoningOptions { Effort = chatReasoningEffort.Value } : null
                            };
                            var enumerator = client.GetStreamingResponseAsync(messages, options, requestCts.Token).GetAsyncEnumerator();
                            try
                            {
                                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                                {
                                    ChatResponseUpdate update = enumerator.Current;
                                    foreach (AIContent content in update.Contents)
                                    {
                                        if (content is TextContent textContent)
                                        {
                                            string localReply;
                                            lock (replyLock)
                                            {
                                                accumulatedReply += textContent.Text;
                                                localReply = RenderReply(accumulatedReply);
                                            }
                                            UpdateAiHistoryEntry(aiHistoryIndex, localReply);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                await enumerator.DisposeAsync().ConfigureAwait(false);
                            }

                            string formattedFinal = RenderReply(accumulatedReply);
                            UpdateAiHistoryEntry(aiHistoryIndex, formattedFinal, persist: true);
                        }
                        catch (Exception ex)
                        {
                            string safeError = "RimLLM_ChatAiError".Translate() +
                                " <color=#ef4444>" + RimLLMLog.SanitizeForLog(ex.Message, 240) + "</color>";
                            RimLLMDispatcher.EnqueueOnMainThread(() =>
                            {
                                if (aiHistoryIndex < chatHistory.Count)
                                {
                                    chatHistory[aiHistoryIndex] = safeError;
                                }
                                else
                                {
                                    chatHistory.Add(safeError);
                                }
                                PersistChatHistory();
                                chatLoading = false;
                                chatScrollPosition.y = 999999f;
                            });
                        }
                    });
                }
            }
        }
        #pragma warning restore S3776

        private static string ThinkStartLabel => "RimLLM_ThinkProcessLabel".Translate();
        private static string ThinkEndLabel => "RimLLM_ThinkProcessEndLabel".Translate();
        private static string ThinkingLabel => "RimLLM_ThinkingLabel".Translate();

        /// <summary>
        /// 把模型回覆整理成可顯示的 rich text：先抽出思考過程並標灰，再把剩下的 Markdown 轉成標籤。
        /// 順序不能顛倒 —— ```thought 圍籬必須先被思考處理吃掉，否則會被當成一般程式碼區塊。
        /// </summary>
        private static string RenderReply(string text)
        {
            return RimLLMMarkdown.ToRichText(FormatThinkProcess(text));
        }

        private static string FormatThinkProcess(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 註：此處原本在 DetailedLogging 下記錄完整的模型輸出。
            // 該日誌未經 SanitizeForLog、未截斷，且因為由 OnChunkReceived 觸發，
            // 會對每個 chunk 重複輸出整段累積內容，使 PII、prompt 或意外出現的金鑰
            // 長期留存於 Player.log。已移除（稽核 SEC-002）。

            // 1. 處理已閉合的 <think>...</think> 或 <thought>...</thought> -> 標記為灰色
            string result = Regex.Replace(text, @"<(think|thought)>([\s\S]*?)</\1>", m => WrapClosedThought(m.Groups[2].Value), RegexOptions.None, TimeSpan.FromSeconds(1));

            // 2. 處理未閉合的 <think> 或 <thought>（串流中常遇到） -> 將後續全部標記為灰色
            var matchUnclosedXml = Regex.Match(result, @"<(think|thought)>(?![\s\S]*</\1>)", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (matchUnclosedXml.Success)
            {
                result = WrapTrailingThought(result, matchUnclosedXml.Index, matchUnclosedXml.Length);
            }

            // 3. 處理已閉合的 markdown 思考區塊 ```thought...``` -> 標記為灰色
            result = Regex.Replace(result, @"```thought([\s\S]*?)```", m => WrapClosedThought(m.Groups[1].Value), RegexOptions.None, TimeSpan.FromSeconds(1));

            // 4. 處理未閉合的 markdown 思考區塊 ```thought （串流中常遇到）-> 將後續全部標記為灰色
            int markdownMarker = result.IndexOf(MarkdownThoughtMarker, StringComparison.Ordinal);
            if (markdownMarker >= 0)
            {
                result = WrapTrailingThought(result, markdownMarker, MarkdownThoughtMarker.Length);
            }

            return result.Trim();
        }

        private const string MarkdownThoughtMarker = "```thought";

        /// <summary>已閉合的思考區塊：內容為空則整段捨棄，否則以灰色包起來。</summary>
        private static string WrapClosedThought(string content)
        {
            string thinkContent = content.Trim();
            return string.IsNullOrEmpty(thinkContent)
                ? ""
                : $"\n<color=silver>{ThinkStartLabel}\n{thinkContent}\n{ThinkEndLabel}</color>\n\n";
        }

        /// <summary>未閉合的思考區塊：把標記之後的所有內容都視為思考中並標灰。</summary>
        private static string WrapTrailingThought(string text, int markerIndex, int markerLength)
        {
            string before = text.Substring(0, markerIndex);
            string after = text.Substring(markerIndex + markerLength);
            return before + $"\n<color=silver>{ThinkingLabel}\n{after} ...</color>";
        }
    }
#pragma warning restore S108, S1643, S2486, S8949
}