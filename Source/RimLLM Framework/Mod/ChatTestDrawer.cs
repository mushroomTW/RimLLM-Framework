using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimLLM_Framework.SDK;
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
        private static bool chatLoading = false;
        private static LLMReasoningEffort chatReasoningEffort = LLMReasoningEffort.Auto;
        private static bool chatReasoningInitialized = false;

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

            StringBuilder chatBuilder = new StringBuilder();
            for (int i = 0; i < chatHistory.Count; i++)
            {
                chatBuilder.Append(chatHistory[i]);
                if (i < chatHistory.Count - 1)
                {
                    chatBuilder.AppendLine();
                    chatBuilder.AppendLine();
                }
            }
            string allChatText = chatBuilder.ToString();

            float chatViewHeight = Math.Max(480f, Text.CalcHeight(allChatText, chatContentWidth) + 12f);
            Rect chatViewRect = new Rect(0f, 0f, chatContentWidth, chatViewHeight);

            Widgets.BeginScrollView(chatRect, ref chatScrollPosition, chatViewRect);
            Rect allChatRect = new Rect(4f, 4f, chatContentWidth - 8f, chatViewHeight - 8f);
            GUIStyle richLabelStyle = new GUIStyle(Text.CurFontStyle);
            richLabelStyle.richText = true;
            richLabelStyle.wordWrap = true;
            GUI.Label(allChatRect, allChatText, richLabelStyle);
            Widgets.EndScrollView();

            listing.Gap(6f);

            // 思考強度設定列
            Rect effortRowRect = listing.GetRect(30f);
            Rect effortLabelRect = new Rect(effortRowRect.x, effortRowRect.y, 250f, effortRowRect.height);
            Rect effortBtnRect = new Rect(effortRowRect.x + 260f, effortRowRect.y, 200f, effortRowRect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(effortLabelRect, "RimLLM_ReasoningEffortLabel".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            string chatEffortLabel = $"RimLLM_ReasoningEffort_{chatReasoningEffort}".Translate();
            if (Widgets.ButtonText(effortBtnRect, chatEffortLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RimLLM_ReasoningEffort_Auto".Translate(), () => { chatReasoningEffort = LLMReasoningEffort.Auto; }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_None".Translate(), () => { chatReasoningEffort = LLMReasoningEffort.None; }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_Low".Translate(), () => { chatReasoningEffort = LLMReasoningEffort.Low; }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_Medium".Translate(), () => { chatReasoningEffort = LLMReasoningEffort.Medium; }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_High".Translate(), () => { chatReasoningEffort = LLMReasoningEffort.High; })
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
                chatInput = Widgets.TextField(textInputRect, chatInput);
            }

            // 點擊清空按鈕邏輯
            if (!chatLoading && Widgets.ButtonText(clearBtnRect, "RimLLM_ClearBtn".Translate()))
            {
                chatHistory.Clear();
                Settings.ChatHistory.Clear();
                Settings.SaveTelemetry();
                chatInput = "";
            }

            bool pressEnter = (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return);

            if (!chatLoading && (Widgets.ButtonText(sendBtnRect, "RimLLM_Send".Translate()) || pressEnter))
            {
                if (!string.IsNullOrEmpty(chatInput))
                {
                    string trimmedInput = chatInput.Trim();
                    if (trimmedInput == "/clear")
                    {
                        chatHistory.Clear();
                        Settings.ChatHistory.Clear();
                        Settings.SaveTelemetry();
                        chatInput = "";
                    }
                    else
                    {
                        string userPrompt = trimmedInput;
                        chatHistory.Add("RimLLM_ChatUser".Translate() + " " + userPrompt);

                        // 先新增一個 AI 回覆的佔位項目，以利後續串流更新
                        chatHistory.Add("RimLLM_ChatAi".Translate() + " ");
                        int aiHistoryIndex = chatHistory.Count - 1;

                        Settings.ChatHistory = new List<string>(chatHistory);
                        Settings.SaveTelemetry();
                        chatLoading = true;
                        chatInput = "";

                        object replyLock = new object();
                        string accumulatedReply = "";

                        Task.Run(async () =>
                        {
                            try
                            {
                                var request = new LLMRequest
                                {
                                    ModId = "RimLLM.DebugChat",
                                    Prompt = userPrompt,
                                    MaxTokens = 4096,
                                    Temperature = 0.7f,
                                    ReasoningEffort = chatReasoningEffort,
                                    EnableStreaming = true,
                                    OnChunkReceived = chunk =>
                                    {
                                        string localReply;
                                        lock (replyLock)
                                        {
                                            accumulatedReply += chunk;
                                            localReply = FormatThinkProcess(accumulatedReply);
                                        }
                                        RimLLMDispatcher.EnqueueOnMainThread(() =>
                                        {
                                            if (aiHistoryIndex < chatHistory.Count)
                                            {
                                                chatHistory[aiHistoryIndex] = "RimLLM_ChatAi".Translate() + " " + localReply;
                                                chatScrollPosition.y = 999999f;
                                            }
                                        });
                                    }
                                };
                                string finalReply = await RimLLMProvider.Instance.GenerateAsync(request).ConfigureAwait(false);
                                string formattedFinal = FormatThinkProcess(finalReply);
                                RimLLMDispatcher.EnqueueOnMainThread(() =>
                                {
                                    if (aiHistoryIndex < chatHistory.Count)
                                    {
                                        chatHistory[aiHistoryIndex] = "RimLLM_ChatAi".Translate() + " " + formattedFinal;
                                    }
                                    Settings.ChatHistory = new List<string>(chatHistory);
                                    Settings.SaveTelemetry();
                                    chatLoading = false;
                                    chatScrollPosition.y = 999999f;
                                });
                            }
                            catch (Exception ex)
                            {
                                RimLLMDispatcher.EnqueueOnMainThread(() =>
                                {
                                    string safeError = RimLLMLog.SanitizeForLog(ex.Message, 240);
                                    if (aiHistoryIndex < chatHistory.Count)
                                    {
                                        chatHistory[aiHistoryIndex] = "RimLLM_ChatAiError".Translate() + " <color=#ef4444>" + safeError + "</color>";
                                    }
                                    else
                                    {
                                        chatHistory.Add("RimLLM_ChatAiError".Translate() + " <color=#ef4444>" + safeError + "</color>");
                                    }
                                    Settings.ChatHistory = new List<string>(chatHistory);
                                    Settings.SaveTelemetry();
                                    chatLoading = false;
                                    chatScrollPosition.y = 999999f;
                                });
                            }
                        });
                    }
                }
            }
        }

        private static string FormatThinkProcess(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (Settings.DetailedLogging)
            {
                RimLLMLog.Message($"[RimLLM-DEBUG] FormatThinkProcess Input: {text}");
            }
            // 1. 處理已閉合的 <think>...</think> 或 <thought>...</thought> -> 標記為灰色
            string result = System.Text.RegularExpressions.Regex.Replace(text, @"<(think|thought)>([\s\S]*?)</\1>", m =>
            {
                string thinkContent = m.Groups[2].Value.Trim();
                return string.IsNullOrEmpty(thinkContent) ? "" : $"\n<color=silver>[思考過程]\n{thinkContent}\n[/思考過程]</color>\n\n";
            });
            // 2. 處理未閉合的 <think> 或 <thought>（串流中常遇到） -> 將後續全部標記為灰色
            var matchUnclosedXml = System.Text.RegularExpressions.Regex.Match(result, @"<(think|thought)>(?![\s\S]*</\1>)");
            if (matchUnclosedXml.Success)
            {
                int index = matchUnclosedXml.Index;
                string before = result.Substring(0, index);
                string after = result.Substring(index + matchUnclosedXml.Length);
                result = before + $"\n<color=silver>[思考中...]\n{after} ...</color>";
            }
            // 3. 處理已閉合的 markdown 思考區塊 ```thought...``` -> 標記為灰色
            result = System.Text.RegularExpressions.Regex.Replace(result, @"```thought([\s\S]*?)```", m =>
            {
                string thinkContent = m.Groups[1].Value.Trim();
                return string.IsNullOrEmpty(thinkContent) ? "" : $"\n<color=silver>[思考過程]\n{thinkContent}\n[/思考過程]</color>\n\n";
            });
            // 4. 處理未閉合的 markdown 思考區塊 ```thought （串流中常遇到）-> 將後續全部標記為灰色
            if (result.Contains("```thought"))
            {
                int index = result.IndexOf("```thought");
                string before = result.Substring(0, index);
                string after = result.Substring(index + 10);
                result = before + $"\n<color=silver>[思考中...]\n{after} ...</color>";
            }
            return result.Trim();
        }
    }
}
