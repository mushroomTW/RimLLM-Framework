using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 負責對話測試（ChatTest）分頁的 UI 渲染與對話生命週期狀態管理。
/// </summary>
public static class ChatTestDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    // 對話測試專屬的 UI 暫存狀態
    private static string chatInput = "";
    private static readonly List<string> chatHistory = [];
    private static Vector2 chatScrollPosition = Vector2.zero;
    private static bool chatLoading;
    private static ReasoningEffort? chatReasoningEffort;
    private static bool chatReasoningInitialized;
    /// <summary>對話測試釘選的模型（"Provider:Model"），null 代表自動、沿用備援鏈順序。</summary>
    private static string chatModelId;

    /// <summary>聊天輸入框的控制項名稱，用於將 Enter 鍵綁定限縮在該欄位取得焦點時。</summary>
    private const string ChatInputControlName = "RimLLM_ChatInput";
    private const string ChatAiTag = "RimLLM_ChatAi";

    /// <summary>對話測試頁用到的翻譯鍵，集中於此避免重複硬編碼。</summary>
    private const string ReasoningEffortLabelKey = "RimLLM_ReasoningEffortLabel";
    private const string ReasoningEffortAutoKey = "RimLLM_ReasoningEffort_Auto";
    private const string ChatUserPrefixKey = "RimLLM_ChatUser";

    private static readonly char[] SpaceTrimChars = [' '];

    /// <summary>目前進行中請求的取消來源，供「清空」按鈕中止長時間回應。</summary>
    private static System.Threading.CancellationTokenSource chatCts;
    // 取消或清除時遞增；回呼在主執行緒真正執行時才檢查，避免舊索引寫入新歷史。
    private static long chatRequestGeneration;

    /// <summary>
    /// 串流中重新渲染畫面的最短間隔。50ms 對眼睛已是連續更新，
    /// 但把每秒上百次的全文 Markdown 重排壓到最多 20 次。
    /// </summary>
    private const int StreamRenderIntervalMs = 50;
    private static readonly long StreamRenderIntervalTicks =
        System.Diagnostics.Stopwatch.Frequency * StreamRenderIntervalMs / 1000L;

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
        Interlocked.Increment(ref chatRequestGeneration);
        var cts = chatCts;
        chatCts = null;
        chatLoading = false;
        if (cts == null) return;

        try { cts.Cancel(); }
        catch
        { // 刻意忽略：CTS 可能已取消或已釋放，不影響主流程
        }
        // CTS 由擁有它的背景請求在 finally 釋放，避免請求尚未啟動就取不到 Token。
    }

    private static void EnqueueChatUpdate(long requestGeneration, Action update)
    {
        RimLLMDispatcher.EnqueueOnMainThread(() =>
        {
            if (requestGeneration != Interlocked.Read(ref chatRequestGeneration)) return;
            update();
        });
    }

    /// <summary>中繼資料標記的前後綴，用於在持久化歷史中保存應答模型、耗時與 Token 數。</summary>
    internal const string MetaPrefix = "\n<!--rimllm-meta:";
    internal const string MetaSuffix = "-->";

    /// <summary>
    /// AI 應答的微型中繼資料。
    /// </summary>
    internal sealed class ChatEntryMeta
    {
        public string ModelId { get; set; } = string.Empty;
        public long ElapsedMs { get; set; }
        public int TotalTokens { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public bool IsEstimatedTokens { get; set; }

        public string Serialize()
        {
            return $"model={ModelId ?? ""}|ms={ElapsedMs}|tok={TotalTokens}|p={PromptTokens}|c={CompletionTokens}|est={(IsEstimatedTokens ? 1 : 0)}";
        }

        private static readonly char[] PipeSeparator = ['|'];

        public static ChatEntryMeta Deserialize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var meta = new ChatEntryMeta();
            var parts = raw.Split(PipeSeparator);
            foreach (var part in parts)
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).Trim();
                string val = part.Substring(eq + 1).Trim();
                ParseMetaField(meta, key, val);
            }
            return meta;
        }

        private static void ParseMetaField(ChatEntryMeta meta, string key, string val)
        {
            switch (key)
            {
                case "model":
                    meta.ModelId = val;
                    break;
                case "ms":
                    if (long.TryParse(val, out long ms)) { meta.ElapsedMs = ms; }
                    break;
                case "tok":
                    if (int.TryParse(val, out int tok)) { meta.TotalTokens = tok; }
                    break;
                case "p":
                    if (int.TryParse(val, out int p)) { meta.PromptTokens = p; }
                    break;
                case "c":
                    if (int.TryParse(val, out int c)) { meta.CompletionTokens = c; }
                    break;
                case "est":
                    meta.IsEstimatedTokens = val == "1";
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// 於主線程更新本輪 AI 佔位項目；已取消、歷史重置或索引失效時略過整個回呼。
    /// </summary>
    /// <param name="requestGeneration">請求世代號，用於判斷回呼是否已過期。</param>
    /// <param name="index">要更新的歷史紀錄索引。</param>
    /// <param name="reply">模型回覆文字。</param>
    /// <param name="meta">此則回覆的遙測元資料（用量、模型、延遲等）。</param>
    /// <param name="persist">是否一併寫回遙測並解除載入狀態（串流結束時使用）。</param>
    /// <param name="scrollToBottom">是否把對話框捲到底。</param>
    private static void UpdateAiHistoryEntry(long requestGeneration, int index, string reply, ChatEntryMeta meta = null, bool persist = false, bool scrollToBottom = true)
    {
        EnqueueChatUpdate(requestGeneration, () =>
        {
            if (index < 0 || index >= chatHistory.Count) return;
            string entry = EntryPrefix(ChatAiTag) + reply;
            if (meta != null)
            {
                entry += MetaPrefix + meta.Serialize() + MetaSuffix;
            }
            chatHistory[index] = entry;
            if (persist)
            {
                PersistChatHistory();
                chatLoading = false;
                chatCts = null;
            }
            if (scrollToBottom)
            {
                chatScrollPosition.y = 999999f;
            }
        });
    }

    private static void UpdateChatError(long requestGeneration, int index, string message)
    {
        EnqueueChatUpdate(requestGeneration, () =>
        {
            if (index < 0 || index >= chatHistory.Count) return;
            chatHistory[index] = EntryPrefix("RimLLM_ChatAiError") +
                "<color=#ef4444>" + message + "</color>";
            PersistChatHistory();
            chatLoading = false;
            chatCts = null;
            chatScrollPosition.y = 999999f;
        });
    }

    /// <summary>
    /// 將目前的對話歷史複本交給遙測儲存，寫檔（AES + JSON + 磁碟）交由背景單寫者執行。
    /// 先前在主執行緒同步寫檔，背景寫入正持有檔案鎖時主執行緒還會被卡住。
    /// </summary>
    private static void PersistChatHistory()
    {
        Settings.ChatHistory = new List<string>(chatHistory);
        Settings.QueueTelemetrySave();
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
        return 686f;
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

        DrawChatHistoryScrollView(listing, ResolveRichLabelStyle());
        listing.Gap(6f);
        DrawModelSelectorRow(listing);
        listing.Gap(6f);
        DrawReasoningEffortRow(listing);
        listing.Gap(6f);
        DrawChatInputRow(listing);
    }

    private static void DrawChatHistoryScrollView(Listing_Standard listing, GUIStyle richLabelStyle)
    {
        Rect chatRect = listing.GetRect(480f);
        Widgets.DrawMenuSection(chatRect);

        float chatContentWidth = chatRect.width - 16f;
        float bubbleWidth = chatContentWidth - 8f;
        float bubbleInnerWidth = bubbleWidth - 16f;
        List<BubbleLayout> bubbleLayouts = ResolveBubbleLayouts(richLabelStyle, bubbleInnerWidth);
        float totalBubblesHeight = 8f;
        for (int i = 0; i < bubbleLayouts.Count; i++)
        {
            totalBubblesHeight += bubbleLayouts[i].Height + 8f;
        }

        float chatViewHeight = Math.Max(480f, totalBubblesHeight);
        Rect chatViewRect = new Rect(0f, 0f, chatContentWidth, chatViewHeight);

        Widgets.BeginScrollView(chatRect, ref chatScrollPosition, chatViewRect);

        if (chatHistory.Count == 0)
        {
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
            {
                Widgets.Label(new Rect(0f, 0f, chatContentWidth, 480f), "<color=grey>（目前尚無對話記錄，請在下方輸入測試訊息）</color>");
            }
        }
        else
        {
            float curY = 8f;
            float visibleTop = Mathf.Clamp(chatScrollPosition.y, 0f, Mathf.Max(0f, chatViewHeight - chatRect.height));
            float visibleBottom = visibleTop + chatRect.height;
            string copyLabel = "RimLLM_ChatCopyMessage".Translate();

            for (int i = 0; i < bubbleLayouts.Count; i++)
            {
                BubbleLayout layout = bubbleLayouts[i];
                float cardTop = curY;
                curY += layout.Height + 8f;
                if (cardTop > visibleBottom || cardTop + layout.Height < visibleTop)
                {
                    continue;
                }

                Rect bubbleRect = new Rect(4f, cardTop, bubbleWidth, layout.Height);
                DrawChatBubble(bubbleRect, layout, richLabelStyle, bubbleInnerWidth, copyLabel);
            }
        }

        Widgets.EndScrollView();
    }

    private static void DrawChatBubble(
        Rect bubbleRect,
        BubbleLayout layout,
        GUIStyle richLabelStyle,
        float bubbleInnerWidth,
        string copyLabel)
    {
        Color bgColor = layout.IsUser ? RimLLMUIStyle.BubbleUserFill : RimLLMUIStyle.BubbleAiFill;
        Widgets.DrawBoxSolid(bubbleRect, bgColor);
        Widgets.DrawBox(bubbleRect, 1);

        // 標題列
        Rect headerRect = new Rect(bubbleRect.x + 8f, bubbleRect.y + 4f, bubbleRect.width - 16f, 20f);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny))
        {
            Widgets.Label(headerRect, layout.HeaderText);
        }

        // 內文
        if (layout.Parts == null)
        {
            Rect bodyRect = new Rect(bubbleRect.x + 8f, bubbleRect.y + 24f, bubbleInnerWidth, layout.TextHeight + 2f);
            GUI.Label(bodyRect, layout.Body, richLabelStyle);
        }
        else
        {
            float partY = bubbleRect.y + 24f;
            foreach (BubblePart part in layout.Parts)
            {
                Rect partRect = new Rect(bubbleRect.x + 8f, partY, bubbleInnerWidth, part.Height + 2f);
                if (part.Table != null) part.Table.Draw(partRect);
                else GUI.Label(partRect, part.Rich, richLabelStyle);
                partY += part.Height + PartGap;
            }
        }

        // AI 訊息底部工具列（微型標籤與一鍵複製）
        if (!layout.IsUser)
        {
            float btnWidth = 80f;
            float btnHeight = 22f;
            float bottomY = bubbleRect.yMax - 27f;
            Rect copyBtnRect = new Rect(bubbleRect.xMax - 8f - btnWidth, bottomY, btnWidth, btnHeight);

            if (!chatLoading && Widgets.ButtonText(copyBtnRect, copyLabel))
            {
                GUIUtility.systemCopyBuffer = StripRichTextForClipboard(layout.Body);
                Messages.Message("RimLLM_CopiedToClipboard".Translate("AI"), MessageTypeDefOf.TaskCompletion, false);
            }

            if (layout.Meta != null)
            {
                Rect badgesArea = new Rect(bubbleRect.x + 8f, bottomY, copyBtnRect.x - 8f - (bubbleRect.x + 8f), btnHeight);
                DrawAiMetaBadges(badgesArea, layout);
            }
        }
    }

    private static void DrawChatInputRow(Listing_Standard listing)
    {
        Rect inputRowRect = listing.GetRect(30f);
        Rect textInputRect = new Rect(inputRowRect.x, inputRowRect.y, inputRowRect.width - 180f, inputRowRect.height);
        Rect clearBtnRect = new Rect(inputRowRect.x + inputRowRect.width - 170f, inputRowRect.y, 80f, inputRowRect.height);
        Rect sendBtnRect = new Rect(inputRowRect.x + inputRowRect.width - 80f, inputRowRect.y, 80f, inputRowRect.height);

        DrawChatInputField(textInputRect);
        HandleClearButton(clearBtnRect);
        HandleSendOrStopButton(sendBtnRect);
    }

    private static void DrawChatInputField(Rect textInputRect)
    {
        if (chatLoading)
        {
            int dotCount = 1 + (int)(Time.realtimeSinceStartup * 2.5) % 3;
            string dots = new('.', dotCount);
            Widgets.Label(textInputRect, $"<color=silver>{"RimLLM_AiThinking".Translate()}{dots}</color>");
        }
        else
        {
            GUI.SetNextControlName(ChatInputControlName);
            chatInput = Widgets.TextField(textInputRect, chatInput);
        }
    }

    private static void HandleClearButton(Rect clearBtnRect)
    {
        if (!chatLoading && Widgets.ButtonText(clearBtnRect, "RimLLM_ClearBtn".Translate()))
        {
            CancelActiveChatRequest();
            chatHistory.Clear();
            Settings.ClearChatHistory();
            Settings.SaveTelemetry();
            chatInput = "";
        }
    }

    private static void HandleSendOrStopButton(Rect sendBtnRect)
    {
        bool pressEnter = Event.current.type == EventType.KeyDown &&
                          Event.current.keyCode == KeyCode.Return &&
                          GUI.GetNameOfFocusedControl() == ChatInputControlName;

        if (chatLoading)
        {
            Color origColor = GUI.color;
            GUI.color = new Color(0.95f, 0.35f, 0.35f);
            if (Widgets.ButtonText(sendBtnRect, "RimLLM_ChatStop".Translate()))
            {
                CancelActiveChatRequest();
                PersistChatHistory();
            }
            GUI.color = origColor;
        }
        else if (Widgets.ButtonText(sendBtnRect, "RimLLM_Send".Translate()) || pressEnter)
        {
            if (pressEnter) Event.current.Use();

            if (ShouldSendChatInput(chatInput))
            {
                StartChatStreaming(chatInput.Trim(), chatModelId);
            }
        }
    }

    private sealed class StreamRenderState
    {
        public readonly object ReplyLock = new();
        public readonly StringBuilder AccumulatedReply = new();
        public long LastRenderTimestamp;
        public bool TrailingRenderScheduled;
        public bool StreamCompleted;
        public string ActualModelId;
        public int PromptTokens;
        public int CompletionTokens;
        public int TotalTokens;
        public bool HasExactTokens;
        public System.Diagnostics.Stopwatch Stopwatch;
        public long RequestGeneration;
        public int AiHistoryIndex;
        public CancellationToken RequestToken;

        public void RecordUsage(ChatResponseUpdate update)
        {
            if (update == null) return;
            if (string.IsNullOrEmpty(ActualModelId) && !string.IsNullOrEmpty(update.ModelId))
            {
                ActualModelId = update.ModelId;
            }
            if (update.Contents == null) return;

            foreach (var part in update.Contents)
            {
                if (part is UsageContent { Details: not null } usage)
                {
                    ApplyUsageDetails(usage.Details);
                }
            }
        }

        private void ApplyUsageDetails(UsageDetails details)
        {
            if (details.TotalTokenCount is > 0)
            {
                TotalTokens = (int)details.TotalTokenCount.Value;
                HasExactTokens = true;
            }
            if (details.InputTokenCount.HasValue) PromptTokens = (int)details.InputTokenCount.Value;
            if (details.OutputTokenCount.HasValue) CompletionTokens = (int)details.OutputTokenCount.Value;
        }

        public void Append(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            bool renderNow;
            bool scheduleTrailing = false;
            lock (ReplyLock)
            {
                AccumulatedReply.Append(text);
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                renderNow = now - LastRenderTimestamp >= StreamRenderIntervalTicks;
                if (renderNow)
                {
                    LastRenderTimestamp = now;
                }
                else if (!TrailingRenderScheduled)
                {
                    TrailingRenderScheduled = true;
                    scheduleTrailing = true;
                }
            }

            if (renderNow)
            {
                RenderLive();
            }
            else if (scheduleTrailing)
            {
                Task.Delay(StreamRenderIntervalMs, RequestToken).ContinueWith(_ =>
                {
                    lock (ReplyLock)
                    {
                        TrailingRenderScheduled = false;
                        if (StreamCompleted) return;
                        LastRenderTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    RenderLive();
                }, TaskScheduler.Default);
            }
        }

        public void RenderLive()
        {
            lock (ReplyLock)
            {
                if (StreamCompleted) return;
                string localReply = MarkdownBodyMarker + AccumulatedReply;
                var liveMeta = new ChatEntryMeta
                {
                    ModelId = ActualModelId,
                    ElapsedMs = Stopwatch.ElapsedMilliseconds,
                    TotalTokens = TotalTokens,
                    PromptTokens = PromptTokens,
                    CompletionTokens = CompletionTokens,
                    IsEstimatedTokens = !HasExactTokens
                };
                UpdateAiHistoryEntry(RequestGeneration, AiHistoryIndex, localReply, meta: liveMeta);
            }
        }
    }

    private static void StartChatStreaming(string userPrompt, string pinnedModel)
    {
        CancelActiveChatRequest();
        long requestGeneration = Interlocked.Read(ref chatRequestGeneration);

        chatHistory.Add(EntryPrefix(ChatUserPrefixKey) + userPrompt);
        chatHistory.Add(EntryPrefix(ChatAiTag));
        int aiHistoryIndex = chatHistory.Count - 1;

        PersistChatHistory();
        chatInput = "";
        chatLoading = true;

        var requestCts = new System.Threading.CancellationTokenSource();
        chatCts = requestCts;
        CancellationToken requestToken = requestCts.Token;

        Task.Run(() => ExecuteChatStreamingAsync(userPrompt, pinnedModel, requestGeneration, aiHistoryIndex, requestCts, requestToken), CancellationToken.None);
    }

    private static async Task ExecuteChatStreamingAsync(
        string userPrompt,
        string pinnedModel,
        long requestGeneration,
        int aiHistoryIndex,
        CancellationTokenSource requestCts,
        CancellationToken requestToken)
    {
        var renderState = new StreamRenderState
        {
            RequestGeneration = requestGeneration,
            AiHistoryIndex = aiHistoryIndex,
            RequestToken = requestToken,
            Stopwatch = System.Diagnostics.Stopwatch.StartNew()
        };

        try
        {
            using var client = RimLLMProvider.CreateChatClient(ServiceConsumerList.DebugChatModId);
            var messages = new List<ChatMessage> { new(ChatRole.User, userPrompt) };
            var options = new RimLLMChatOptions
            {
                MaxOutputTokens = 4096,
                Temperature = 0.7f,
                DisableReasoning = false,
                Reasoning = chatReasoningEffort.HasValue ? new ReasoningOptions { Effort = chatReasoningEffort.Value } : null,
                ModelId = pinnedModel
            };

            var thinkFormatter = new RimLLMThinkTagFormatter();
            var enumerator = client.GetStreamingResponseAsync(messages, options, requestToken).GetAsyncEnumerator(requestToken);
            try
            {
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    var update = enumerator.Current;
                    renderState.RecordUsage(update);
                    renderState.Append(thinkFormatter.Append(update));
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            string tail = thinkFormatter.Complete();
            string fullReply;
            lock (renderState.ReplyLock)
            {
                if (!string.IsNullOrEmpty(tail)) renderState.AccumulatedReply.Append(tail);
                renderState.StreamCompleted = true;
                fullReply = renderState.AccumulatedReply.ToString();
            }

            renderState.Stopwatch.Stop();
            var finalMeta = BuildFinalChatMeta(userPrompt, fullReply, renderState);
            UpdateAiHistoryEntry(requestGeneration, aiHistoryIndex, MarkdownBodyMarker + fullReply, meta: finalMeta, persist: true);
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
        {
            // 使用者停止為正常收尾，主執行緒已保留當時可見內容，不需額外處置。
        }
        catch (Exception ex)
        {
            lock (renderState.ReplyLock) { renderState.StreamCompleted = true; }
            UpdateChatError(requestGeneration, aiHistoryIndex, RimLLMLog.SanitizeForLog(ex.Message, 240));
        }
        finally
        {
            lock (renderState.ReplyLock) { renderState.StreamCompleted = true; }
            requestCts.Dispose();
        }
    }

    private static ChatEntryMeta BuildFinalChatMeta(string userPrompt, string fullReply, StreamRenderState state)
    {
        int totalTokens = state.TotalTokens;
        int promptTokens = state.PromptTokens;
        int completionTokens = state.CompletionTokens;
        bool hasExact = state.HasExactTokens;

        if (!hasExact || totalTokens <= 0)
        {
            promptTokens = RimLLMChatClientExecutor.EstimateTokens(userPrompt);
            completionTokens = RimLLMChatClientExecutor.EstimateTokens(fullReply);
            totalTokens = promptTokens + completionTokens;
            hasExact = false;
        }

        return new ChatEntryMeta
        {
            ModelId = state.ActualModelId,
            ElapsedMs = state.Stopwatch.ElapsedMilliseconds,
            TotalTokens = totalTokens,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            IsEstimatedTokens = !hasExact
        };
    }

    /// <summary>
    /// 測試模型選擇列。選單列出備援鏈上的 "Provider:Model" 條目，選中後以
    /// <see cref="ChatOptions.ModelId"/> 釘選為鏈首；選自動則不指定、沿用備援鏈順序。
    /// </summary>
    private static void DrawModelSelectorRow(Listing_Standard listing)
    {
        // 與模型設置頁共用同一份去空去重邏輯，兩處列出的條目才不會漂移。
        List<string> entries = ModelSettingsDrawer.CollectEntries();
        // 備援鏈被改掉後，舊的釘選已無意義，退回自動。
        if (!string.IsNullOrEmpty(chatModelId) && !entries.Contains(chatModelId))
        {
            chatModelId = null;
        }

        Rect rowRect = listing.GetRect(30f);
        float labelWidth = Text.CalcSize("RimLLM_ChatTestModelLabel".Translate()).x;
        Rect labelRect = new Rect(rowRect.x, rowRect.y, labelWidth + 5f, rowRect.height);
        Rect btnRect = new Rect(rowRect.x + labelWidth + 15f, rowRect.y, 300f, rowRect.height);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(labelRect, "RimLLM_ChatTestModelLabel".Translate());
        }

        string btnLabel = string.IsNullOrEmpty(chatModelId)
            ? "RimLLM_ChatTestModelAuto".Translate().ToString()
            : chatModelId;
        if (Widgets.ButtonText(btnRect, btnLabel))
        {
            var options = new List<FloatMenuOption>
            {
                new("RimLLM_ChatTestModelAuto".Translate(), () => { chatModelId = null; })
            };
            foreach (string entry in entries)
            {
                string captured = entry;
                options.Add(new FloatMenuOption(captured, () => { chatModelId = captured; }));
            }
            if (entries.Count == 0)
            {
                options.Add(new FloatMenuOption("RimLLM_ModelsEmptyWarning".Translate(), null));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    /// <summary>
    /// 思考強度設定列。
    /// </summary>
    private static void DrawReasoningEffortRow(Listing_Standard listing)
    {
        Rect effortRowRect = listing.GetRect(30f);
        float effortLabelWidth = Text.CalcSize(ReasoningEffortLabelKey.Translate()).x;
        Rect effortLabelRect = new Rect(effortRowRect.x, effortRowRect.y, effortLabelWidth + 5f, effortRowRect.height);
        Rect effortBtnRect = new Rect(effortRowRect.x + effortLabelWidth + 15f, effortRowRect.y, 160f, effortRowRect.height);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft))
        {
            Widgets.Label(effortLabelRect, ReasoningEffortLabelKey.Translate());
        }
        string chatEffortLabel = chatReasoningEffort == null ? ReasoningEffortAutoKey.Translate() : $"RimLLM_ReasoningEffort_{chatReasoningEffort}".Translate();
        if (Widgets.ButtonText(effortBtnRect, chatEffortLabel))
        {
            List<FloatMenuOption> options =
            [
                new(ReasoningEffortAutoKey.Translate(), () => { chatReasoningEffort = null; }),
                new("RimLLM_ReasoningEffort_Low".Translate(), () => { chatReasoningEffort = ReasoningEffort.Low; }),
                new("RimLLM_ReasoningEffort_Medium".Translate(), () => { chatReasoningEffort = ReasoningEffort.Medium; }),
                new("RimLLM_ReasoningEffort_High".Translate(), () => { chatReasoningEffort = ReasoningEffort.High; })
            ];
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    private static string ThinkStartLabel => "RimLLM_ThinkProcessLabel".Translate();
    private static string ThinkEndLabel => "RimLLM_ThinkProcessEndLabel".Translate();
    private static string ThinkingLabel => "RimLLM_ThinkingLabel".Translate();

    /// <summary>
    /// 新紀錄的 AI 內文存原始 Markdown 並以此標記開頭，繪製時才轉換，表格才能逐格畫。
    /// 沒有標記的舊紀錄已是轉好的 rich text，照原樣顯示。
    /// </summary>
    internal const string MarkdownBodyMarker = "<!--rimllm-md-->";
    private const float PartGap = 6f;

    /// <summary>一段內文：<see cref="Table"/> 不為 null 時是逐格繪製的表格，否則是 <see cref="Rich"/> 文字。</summary>
    private sealed class BubblePart
    {
        public string Rich;
        public RimLLMMarkdownTable Table;
        public float Height;
    }

    /// <summary>
    /// 先抽出思考過程並標灰，再轉 Markdown —— ```thought 圍籬必須先被思考處理吃掉，否則會被當成程式碼區塊。
    /// 思考區塊整段當文字，表格只從最後一個已閉合思考區塊之後切，否則灰色 wrapper 會被拆開；
    /// 仍在思考（未閉合）時整段都是文字。
    /// </summary>
    private static List<BubblePart> RenderMarkdownBody(string markdown, GUIStyle richLabelStyle, float width)
    {
        string formatted = FormatThinkProcess(markdown) ?? string.Empty;
        int answerStart = 0;
        if (formatted.Contains("<color=silver>" + ThinkingLabel))
        {
            answerStart = formatted.Length;
        }
        else
        {
            string thinkEnd = ThinkEndLabel + "</color>";
            int end = formatted.LastIndexOf(thinkEnd, StringComparison.Ordinal);
            if (end >= 0) answerStart = end + thinkEnd.Length;
        }

        var parts = new List<BubblePart>();
        void AddText(string text)
        {
            string rich = RimLLMMarkdown.ToRichText(text);
            if (string.IsNullOrWhiteSpace(rich)) return;
            parts.Add(new BubblePart { Rich = rich, Height = richLabelStyle.CalcHeight(new GUIContent(rich), width) });
        }

        AddText(formatted.Substring(0, answerStart));
        foreach (RimLLMMarkdownSegment segment in RimLLMMarkdownTable.Split(formatted.Substring(answerStart)))
        {
            if (!segment.IsTable)
            {
                AddText(segment.Markdown);
                continue;
            }
            RimLLMMarkdownTable table = RimLLMMarkdownTable.Layout(segment.Rows, width);
            parts.Add(new BubblePart { Table = table, Height = table.Height });
        }
        return parts;
    }

    private static string FormatThinkProcess(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 先跳脫供應商內容中的 Unity 標籤，再加入框架自行產生的灰色 thinking wrapper；
        // 否則供應商可以用同名的 </color> 提早關閉框架標籤。
        text = RimLLMMarkdown.EscapeUntrustedUnityTags(text);

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

    /// <summary>
    /// 對話紀錄項目的前綴（例如 "&lt;b&gt;[AI]:&lt;/b&gt; "）。
    /// 一定要以 ToString() 取 RawText 再串接：TaggedString 隱含轉成 string 時會呼叫 StripTags()，
    /// 把整條訊息裡所有 &lt;...&gt; 標籤（包含 Markdown 轉出的 color/size/b）一併剝掉，
    /// 畫面上就只剩沒有任何格式的純文字。
    /// </summary>
    private static string EntryPrefix(string key)
    {
        try
        {
            if (LanguageDatabase.activeLanguage != null)
            {
                return key.Translate().ToString() + " ";
            }
        }
        catch
        {
            // 單元測試或環境未載入語系時安全退回
        }

        return key switch
        {
            ChatUserPrefixKey => "<b>[Me]:</b> ",
            ChatAiTag => "<b>[AI]:</b> ",
            "RimLLM_ChatAiError" => "<b>[AI Error]:</b> ",
            _ => key + " ",
        };
    }

    /// <summary>
    /// 嘗試剝掉 <paramref name="key"/> 對應的前綴。
    /// 修正前持久化的舊紀錄前綴已被 StripTags 剝成純文字（"[AI]: "），兩種形式都要認得。
    /// </summary>
    private static bool TryStripPrefix(string entry, string key, out string label, out string body)
    {
        string raw = EntryPrefix(key);
        string plain = raw.StripTags();
        label = plain.Trim();
        if (entry.StartsWith(raw, StringComparison.Ordinal))
        {
            body = entry.Substring(raw.Length);
            return true;
        }
        if (entry.StartsWith(plain, StringComparison.Ordinal))
        {
            body = entry.Substring(plain.Length);
            return true;
        }
        body = string.Empty;
        return false;
    }

    internal static bool ParseMessage(string entry, out string label, out string body, out ChatEntryMeta meta)
    {
        meta = null;
        if (string.IsNullOrEmpty(entry))
        {
            label = string.Empty;
            body = string.Empty;
            return false;
        }

        // 抽離並淨化中繼資料標記，避免污染本文顯示與剪貼簿
        int metaStart = entry.LastIndexOf(MetaPrefix, StringComparison.Ordinal);
        if (metaStart >= 0)
        {
            int metaEnd = entry.IndexOf(MetaSuffix, metaStart + MetaPrefix.Length, StringComparison.Ordinal);
            if (metaEnd >= 0)
            {
                string rawMeta = entry.Substring(metaStart + MetaPrefix.Length, metaEnd - (metaStart + MetaPrefix.Length));
                meta = ChatEntryMeta.Deserialize(rawMeta);
                entry = entry.Substring(0, metaStart);
            }
        }

        if (TryStripPrefix(entry, ChatUserPrefixKey, out label, out body)) return true;
        if (TryStripPrefix(entry, "RimLLM_ChatAiError", out label, out body)) return false;
        if (TryStripPrefix(entry, ChatAiTag, out label, out body)) return false;

        // 舊格式或手動拼接的格式相容
        if (entry.StartsWith("<b>[我]:</b>", StringComparison.Ordinal) || entry.StartsWith("<b>[Me]:</b>", StringComparison.Ordinal))
        {
            int endTag = entry.IndexOf("</b>", StringComparison.Ordinal);
            label = entry.Substring(0, endTag + 4).Replace("<b>", "").Replace("</b>", "").Trim();
            body = entry.Substring(endTag + 4).TrimStart(SpaceTrimChars);
            return true;
        }

        if (entry.StartsWith("<b>[AI]:</b>", StringComparison.Ordinal))
        {
            int endTag = entry.IndexOf("</b>", StringComparison.Ordinal);
            label = entry.Substring(0, endTag + 4).Replace("<b>", "").Replace("</b>", "").Trim();
            body = entry.Substring(endTag + 4).TrimStart(SpaceTrimChars);
            return false;
        }

        label = EntryPrefix(ChatAiTag).StripTags().Trim();
        body = entry;
        return false;
    }

    internal static bool ParseMessage(string entry, out string label, out string body)
    {
        return ParseMessage(entry, out label, out body, out _);
    }

    /// <summary>
    /// 格式化模型名稱供微型標籤顯示。若為 "供應商:模型" 複合識別碼，轉為易讀的 "供應商 • 模型" 格式。
    /// </summary>
    internal static string FormatModelDisplayName(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return string.Empty;
        int colonIdx = modelId.IndexOf(':');
        if (colonIdx > 0 && colonIdx < modelId.Length - 1)
        {
            string provider = modelId.Substring(0, colonIdx);
            string model = modelId.Substring(colonIdx + 1);
            return $"{provider} • {model}";
        }
        return modelId;
    }

    /// <summary>
    /// 繪製 AI 訊息底部的微型中繼標籤（方案 A：極簡扁平資訊列）。
    /// 徹底拿掉刺眼的硬外框與高對比底色，以低調柔和的冷灰字階呈現，
    /// 滑鼠懸停時微顯高亮，既保留一鍵複製與詳細 Tooltip，又完全不搶正文焦點。
    /// </summary>
    private static void DrawAiMetaBadges(Rect area, BubbleLayout layout)
    {
        if (layout.Meta == null || area.width < 50f) return;

        float curX = area.x;
        float gap = 8f;

        // 1. 模型標籤（點擊可一鍵複製，懸停微高亮）
        DrawModelBadge(ref curX, area, layout, gap);

        // 2. 耗時與 Token 數（懸停顯示詳細分解 Tooltip）
        DrawMetricsBadge(curX, area, layout);
    }

    private static void DrawModelBadge(ref float curX, Rect area, BubbleLayout layout, float gap)
    {
        string modelDisplay = layout.ModelDisplay;
        if (string.IsNullOrEmpty(modelDisplay)) return;
        string modelId = layout.Meta.ModelId;

        float modelW = Mathf.Min(layout.ModelTextWidth + 6f, area.width * 0.6f);
        Rect modelRect = new Rect(curX, area.y, modelW, area.height);

        bool hovered = Mouse.IsOver(modelRect);
        if (hovered)
        {
            Widgets.DrawHighlight(modelRect);
        }

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny, wordWrap: false))
        {
            Widgets.Label(modelRect, "<color=#94a3b8>" + RimLLMUIStyle.TruncateCached(modelDisplay, modelRect.width) + "</color>");
        }

        if (hovered)
        {
            TooltipHandler.TipRegion(modelRect, "RimLLM_ChatModelBadgeTooltip".Translate(modelId));
        }

        if (Widgets.ButtonInvisible(modelRect))
        {
            GUIUtility.systemCopyBuffer = modelId;
            Messages.Message("RimLLM_CopiedToClipboard".Translate(modelId), MessageTypeDefOf.TaskCompletion, false);
        }

        curX += modelW;

        // 分隔點
        if (area.xMax - curX > 50f)
        {
            Rect dotRect = new Rect(curX, area.y, gap, area.height);
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter, GameFont.Tiny, wordWrap: false))
            {
                Widgets.Label(dotRect, "<color=#475569>•</color>");
            }
            curX += gap;
        }
    }

    private static void DrawMetricsBadge(float curX, Rect area, BubbleLayout layout)
    {
        float remainW = area.xMax - curX;
        if (remainW <= 40f) return;

        float statW = Mathf.Min(layout.StatTextWidth + 8f, remainW);
        Rect statRect = new Rect(curX, area.y, statW, area.height);

        bool hovered = Mouse.IsOver(statRect);
        if (hovered)
        {
            Widgets.DrawHighlight(statRect);
        }

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny, wordWrap: false))
        {
            Widgets.Label(statRect, layout.StatText);
        }

        if (hovered)
        {
            ChatEntryMeta meta = layout.Meta;
            float timeSec = meta.ElapsedMs / 1000f;
            string statTip = meta.IsEstimatedTokens
                ? "RimLLM_ChatStatBadgeTooltipEst".Translate(meta.ElapsedMs, timeSec.ToString("F2"), meta.TotalTokens.ToString("N0"))
                : "RimLLM_ChatStatBadgeTooltip".Translate(meta.ElapsedMs, timeSec.ToString("F2"), meta.TotalTokens.ToString("N0"), meta.PromptTokens.ToString("N0"), meta.CompletionTokens.ToString("N0"));
            TooltipHandler.TipRegion(statRect, statTip);
        }
    }

    /// <summary>
    /// 一張氣泡卡片繪製所需的全部預算結果：解析後的標籤與內文、量測過的高度、
    /// 徽章文字與寬度。<see cref="Entry"/> 記住來源字串，讓快取以參考相等判定是否過期。
    /// </summary>
    private sealed class BubbleLayout
    {
        public string Entry;
        public bool IsUser;
        public string Body;
        /// <summary>原始 Markdown 內文切成的段落；舊紀錄為 null，直接畫 <see cref="Body"/>。</summary>
        public List<BubblePart> Parts;
        public ChatEntryMeta Meta;
        public float Height;
        public float TextHeight;
        public string HeaderText;
        public string ModelDisplay;
        public float ModelTextWidth;
        public string StatText;
        public float StatTextWidth;
    }

    private static readonly List<BubbleLayout> _layoutCache = [];
    private static float _layoutCacheWidth = -1f;
    private static GameFont _layoutCacheFont;

    private static GUIStyle _richLabelStyle;
    private static GameFont _richLabelStyleFont;

    /// <summary>
    /// 內文用的 rich text 樣式。GUIStyle 是 Unity 原生物件，先前每個 OnGUI pass 都 new 一個；
    /// 只有字型變了才需要重建。
    /// </summary>
    private static GUIStyle ResolveRichLabelStyle()
    {
        if (_richLabelStyle == null || _richLabelStyleFont != Text.Font)
        {
            _richLabelStyle = new GUIStyle(Text.CurFontStyle)
            {
                richText = true,
                wordWrap = true
            };
            _richLabelStyleFont = Text.Font;
        }
        return _richLabelStyle;
    }

    /// <summary>
    /// 逐筆比對歷史項目與快取的來源字串：沒變的直接沿用，變了的（串流中的那一筆、新增的、
    /// 清空後重建的）才重新解析與量測。先前每個 OnGUI pass 都對全部歷史重做
    /// Translate、StripTags（Regex）與 CalcHeight，長對話開著設定頁就是持續的每幀開銷。
    /// 寬度或字型變了會讓所有量測失效，此時整批重算。
    /// </summary>
    private static List<BubbleLayout> ResolveBubbleLayouts(GUIStyle richLabelStyle, float bubbleInnerWidth)
    {
        if (Math.Abs(_layoutCacheWidth - bubbleInnerWidth) > 0.001f || _layoutCacheFont != Text.Font)
        {
            _layoutCache.Clear();
            _layoutCacheWidth = bubbleInnerWidth;
            _layoutCacheFont = Text.Font;
        }

        if (_layoutCache.Count > chatHistory.Count)
        {
            _layoutCache.RemoveRange(chatHistory.Count, _layoutCache.Count - chatHistory.Count);
        }

        for (int i = 0; i < chatHistory.Count; i++)
        {
            string entry = chatHistory[i];
            if (i < _layoutCache.Count)
            {
                if (ReferenceEquals(_layoutCache[i].Entry, entry)) continue;
                _layoutCache[i] = BuildBubbleLayout(entry, richLabelStyle, bubbleInnerWidth);
            }
            else
            {
                _layoutCache.Add(BuildBubbleLayout(entry, richLabelStyle, bubbleInnerWidth));
            }
        }

        return _layoutCache;
    }

    private static BubbleLayout BuildBubbleLayout(string entry, GUIStyle richLabelStyle, float bubbleInnerWidth)
    {
        bool isUser = ParseMessage(entry, out string label, out string body, out ChatEntryMeta meta);
        List<BubblePart> parts = null;
        float textHeight;
        if (body.StartsWith(MarkdownBodyMarker, StringComparison.Ordinal))
        {
            parts = RenderMarkdownBody(body.Substring(MarkdownBodyMarker.Length), richLabelStyle, bubbleInnerWidth);
            // 複製用：文字段保留 rich text（複製時再剝標籤），表格換成 tab 分欄的純文字。
            body = string.Join("\n\n", parts.Select(part => part.Table?.PlainText ?? part.Rich));
            textHeight = parts.Sum(part => part.Height) + PartGap * Math.Max(0, parts.Count - 1);
        }
        else
        {
            textHeight = richLabelStyle.CalcHeight(new GUIContent(body), bubbleInnerWidth);
        }

        var layout = new BubbleLayout
        {
            Entry = entry,
            IsUser = isUser,
            Body = body,
            Parts = parts,
            Meta = meta,
            TextHeight = textHeight,
            Height = 24f + textHeight + (isUser ? 10f : 34f),
            HeaderText = isUser
                ? "<color=#60a5fa><b>" + label + "</b></color>"
                : "<color=#4ade80><b>" + label + "</b></color>"
        };

        if (meta != null)
        {
            layout.ModelDisplay = FormatModelDisplayName(meta.ModelId);

            string timeStr = meta.ElapsedMs < 1000 ? $"{meta.ElapsedMs}ms" : $"{(meta.ElapsedMs / 1000f):F1}s";
            string tokenPrefix = meta.IsEstimatedTokens ? "~" : "";
            string tokenStr = meta.TotalTokens > 0 ? $"{tokenPrefix}{meta.TotalTokens:N0} tok" : "";
            layout.StatText = string.IsNullOrEmpty(tokenStr)
                ? $"<color=#64748b>{timeStr}</color>"
                : $"<color=#64748b>{timeStr}</color>  <color=#475569>•</color>  <color=#64748b>{tokenStr}</color>";

            using (RimLLMUIStyle.With(font: GameFont.Tiny))
            {
                layout.ModelTextWidth = string.IsNullOrEmpty(layout.ModelDisplay) ? 0f : Text.CalcSize(layout.ModelDisplay).x;
                layout.StatTextWidth = Text.CalcSize(layout.StatText.StripTags()).x;
            }
        }

        return layout;
    }

    /// <summary>
    /// 複製到剪貼簿前先移除真正的 rich text 標籤與可能存在的中繼標記，再把 <see cref="RimLLMMarkdown.EscapeCode"/> 為了不被 Unity 解讀
    /// 而改成全形的程式碼標籤（＜b＞、＜/color＞…）還原成 ASCII。只還原那六種標籤，
    /// 中文回覆裡本來就是全形的「＜注意＞」不能被改掉。順序不能顛倒，否則還原後的程式碼標籤會被當成格式一起剝掉。
    /// </summary>
    private static string StripRichTextForClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var timeout = TimeSpan.FromSeconds(1);
        string cleaned = Regex.Replace(text, @"<!--rimllm-meta:[\s\S]*?-->", "", RegexOptions.IgnoreCase, timeout);
        cleaned = Regex.Replace(cleaned, @"</?(?:b|i|size|color|material|quad)(?:=[^>]*)?>", "", RegexOptions.IgnoreCase, timeout);
        return Regex.Replace(cleaned, @"＜(/?(?:b|i|size|color|material|quad)(?:=[^＞]*)?)＞", "<$1>", RegexOptions.IgnoreCase, timeout);
    }
}
