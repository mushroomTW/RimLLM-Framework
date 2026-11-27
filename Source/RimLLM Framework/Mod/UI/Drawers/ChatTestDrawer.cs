using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>對話測試（ChatTest）分頁：UI 渲染與對話生命週期狀態管理。</summary>
public static partial class ChatTestDrawer
{
    private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

    private static string chatInput = "";
    private static readonly List<string> chatHistory = [];
    private static Vector2 chatScrollPosition = Vector2.zero;
    private static bool chatLoading;
    private static ReasoningEffort? chatReasoningEffort;
    private static bool chatReasoningInitialized;
    /// <summary>對話測試釘選的模型（"Provider:Model"），null 代表自動、沿用備援鏈順序。</summary>
    private static string? chatModelId;

    private const string ChatInputControlName = "RimLLM_ChatInput";
    private const string ChatAiTag = "RimLLM_ChatAi";
    private const string ColorCloseTag = "</color>";

    private const string ReasoningEffortLabelKey = "RimLLM_ReasoningEffortLabel";
    private const string ReasoningEffortAutoKey = "RimLLM_ReasoningEffort_Auto";
    private const string ChatUserPrefixKey = "RimLLM_ChatUser";

    private static readonly char[] SpaceTrimChars = [' '];

    private static System.Threading.CancellationTokenSource? chatCts;
    // 取消或清除時遞增；回呼在主執行緒真正執行時才檢查，避免舊索引寫入新歷史。
    private static long chatRequestGeneration;

    /// <summary>串流重繪的最短間隔：50ms 對眼睛已是連續更新，但能把每秒上百次的全文 Markdown 重排壓到最多 20 次。</summary>
    private const int StreamRenderIntervalMs = 50;
    private static readonly long StreamRenderIntervalTicks =
        System.Diagnostics.Stopwatch.Frequency * StreamRenderIntervalMs / 1000L;

    internal static bool ShouldSendChatInput(string rawInput)
    {
        return !string.IsNullOrEmpty(rawInput) && rawInput.Trim().Length > 0;
    }

    internal static void CancelActiveChatRequest()
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

    /// <summary>於主線程更新本輪 AI 佔位項目；已取消、歷史重置或索引失效時略過整個回呼。</summary>
    private static void UpdateAiHistoryEntry(long requestGeneration, int index, string reply, ChatEntryMeta? meta = null, bool persist = false, bool scrollToBottom = true)
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

    private static void UpdateChatError(long requestGeneration, int index, string? message)
    {
        EnqueueChatUpdate(requestGeneration, () =>
        {
            if (index < 0 || index >= chatHistory.Count) return;
            chatHistory[index] = EntryPrefix("RimLLM_ChatAiError") +
                "<color=#ef4444>" + message + ColorCloseTag;
            PersistChatHistory();
            chatLoading = false;
            chatCts = null;
            chatScrollPosition.y = 999999f;
        });
    }

    /// <summary>將對話歷史複本交給遙測儲存；寫檔交由背景單寫者，避免主執行緒被檔案鎖卡住。</summary>
    private static void PersistChatHistory()
    {
        Settings.ChatHistory = new List<string>(chatHistory);
        Settings.QueueTelemetrySave();
    }

    public static void Initialize(RimLLMFrameworkSettings settings)
    {
        if (settings.ChatHistory != null && settings.ChatHistory.Count > 0)
        {
            chatHistory.Clear();
            chatHistory.AddRange(settings.ChatHistory);
        }
    }

    public static float GetHeight(float width)
    {
        return 686f;
    }

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
        Rect chatViewRect = new(0f, 0f, chatContentWidth, chatViewHeight);

        Widgets.BeginScrollView(chatRect, ref chatScrollPosition, chatViewRect);

        if (chatHistory.Count == 0)
        {
            using (RimLLMUIStyle.With(TextAnchor.MiddleCenter))
            {
                Widgets.Label(new Rect(0f, 0f, chatContentWidth, 480f), "<color=grey>" + "RimLLM_ChatEmptyHint".Translate() + ColorCloseTag);
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

                Rect bubbleRect = new(4f, cardTop, bubbleWidth, layout.Height);
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

        Rect headerRect = new(bubbleRect.x + 8f, bubbleRect.y + 4f, bubbleRect.width - 16f, 20f);
        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny))
        {
            Widgets.Label(headerRect, layout.HeaderText);
        }

        if (layout.Parts == null)
        {
            Rect bodyRect = new(bubbleRect.x + 8f, bubbleRect.y + 24f, bubbleInnerWidth, layout.TextHeight + 2f);
            GUI.Label(bodyRect, layout.Body, richLabelStyle);
        }
        else
        {
            float partY = bubbleRect.y + 24f;
            foreach (BubblePart part in layout.Parts)
            {
                Rect partRect = new(bubbleRect.x + 8f, partY, bubbleInnerWidth, part.Height + 2f);
                if (part.Table != null) part.Table.Draw(partRect);
                else GUI.Label(partRect, part.Rich, richLabelStyle);
                partY += part.Height + PartGap;
            }
        }

        if (!layout.IsUser)
        {
            float btnWidth = 80f;
            float btnHeight = 22f;
            float bottomY = bubbleRect.yMax - 27f;
            Rect copyBtnRect = new(bubbleRect.xMax - 8f - btnWidth, bottomY, btnWidth, btnHeight);

            if (!chatLoading && Widgets.ButtonText(copyBtnRect, copyLabel))
            {
                GUIUtility.systemCopyBuffer = StripRichTextForClipboard(layout.Body);
                Messages.Message("RimLLM_CopiedToClipboard".Translate("AI"), MessageTypeDefOf.TaskCompletion, historical: false);
            }

            if (layout.Meta != null)
            {
                Rect badgesArea = new(bubbleRect.x + 8f, bottomY, copyBtnRect.x - 8f - (bubbleRect.x + 8f), btnHeight);
                DrawAiMetaBadges(badgesArea, layout);
            }
        }
    }

    private static void DrawChatInputRow(Listing_Standard listing)
    {
        Rect inputRowRect = listing.GetRect(30f);
        Rect textInputRect = new(inputRowRect.x, inputRowRect.y, inputRowRect.width - 180f, inputRowRect.height);
        Rect clearBtnRect = new(inputRowRect.x + inputRowRect.width - 170f, inputRowRect.y, 80f, inputRowRect.height);
        Rect sendBtnRect = new(inputRowRect.x + inputRowRect.width - 80f, inputRowRect.y, 80f, inputRowRect.height);

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
            Settings.QueueTelemetrySave();
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

    /// <summary>測試模型選擇列：選中後以 <see cref="ChatOptions.ModelId"/> 釘選為鏈首；選自動則不指定、沿用備援鏈順序。</summary>
    private static void DrawModelSelectorRow(Listing_Standard listing)
    {
        List<string> entries = ModelSettingsDrawer.CollectEntries();
        if (!string.IsNullOrEmpty(chatModelId) && !entries.Contains(chatModelId))
        {
            chatModelId = null;
        }

        Rect rowRect = listing.GetRect(30f);
        float labelWidth = Text.CalcSize("RimLLM_ChatTestModelLabel".Translate()).x;
        Rect labelRect = new(rowRect.x, rowRect.y, labelWidth + 5f, rowRect.height);
        Rect btnRect = new(rowRect.x + labelWidth + 15f, rowRect.y, 300f, rowRect.height);
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
                options.Add(new FloatMenuOption("RimLLM_ModelsEmptyWarning".Translate(), action: null));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    private static void DrawReasoningEffortRow(Listing_Standard listing)
    {
        Rect effortRowRect = listing.GetRect(30f);
        float effortLabelWidth = Text.CalcSize(ReasoningEffortLabelKey.Translate()).x;
        Rect effortLabelRect = new(effortRowRect.x, effortRowRect.y, effortLabelWidth + 5f, effortRowRect.height);
        Rect effortBtnRect = new(effortRowRect.x + effortLabelWidth + 15f, effortRowRect.y, 160f, effortRowRect.height);
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

    private const float PartGap = 6f;

    /// <summary>一段內文：<see cref="Table"/> 不為 null 時是逐格繪製的表格，否則是 <see cref="Rich"/> 文字。</summary>
    private sealed class BubblePart
    {
        public string? Rich;
        public RimLLMMarkdownTable? Table;
        public float Height;
    }

    /// <summary>
    /// 先抽出思考過程並標灰，再轉 Markdown —— ```thought 圍籬必須先被思考處理吃掉，否則會被當成程式碼區塊。
    /// 思考區塊整段當文字，表格只從最後一個已閉合思考區塊之後切，否則灰色 wrapper 會被拆開；
    /// 仍在思考（未閉合）時整段都是文字。
    /// </summary>
    private static List<BubblePart> RenderMarkdownBody(string body, GUIStyle richLabelStyle, float width)
    {
        SplitMarkdownBody(body, out string? reasoning, out string? answer);
        string formatted = (FormatReasoning(reasoning, !string.IsNullOrEmpty(answer)) + (FormatThinkProcess(answer) ?? string.Empty)).Trim();
        int answerStart = 0;
        if (formatted.Contains("<color=silver>" + ThinkingLabel, StringComparison.Ordinal))
        {
            answerStart = formatted.Length;
        }
        else
        {
            string thinkEnd = ThinkEndLabel + ColorCloseTag;
            int end = formatted.LastIndexOf(thinkEnd, StringComparison.Ordinal);
            if (end >= 0) answerStart = end + thinkEnd.Length;
        }

        var parts = new List<BubblePart>();
        void AddText(string text)
        {
            string rich = RimLLMMarkdown.ToRichText(text) ?? string.Empty;
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
            RimLLMMarkdownTable table = RimLLMMarkdownTable.Layout(segment.Rows ?? [], width);
            parts.Add(new BubblePart { Table = table, Height = table.Height });
        }
        return parts;
    }

    /// <summary>繪製 AI 訊息底部的微型中繼標籤（模型、耗時與 Token 數），懸停時微顯高亮。</summary>
    private static void DrawAiMetaBadges(Rect area, BubbleLayout layout)
    {
        if (layout.Meta == null || area.width < 50f) return;

        float curX = area.x;
        float gap = 8f;

        DrawModelBadge(ref curX, area, layout, gap);

        DrawMetricsBadge(curX, area, layout);
    }

    private static void DrawModelBadge(ref float curX, Rect area, BubbleLayout layout, float gap)
    {
        string? modelDisplay = layout.ModelDisplay;
        if (string.IsNullOrEmpty(modelDisplay)) return;
        string modelId = layout.Meta?.ModelId ?? string.Empty;

        float modelW = Mathf.Min(layout.ModelTextWidth + 6f, area.width * 0.6f);
        Rect modelRect = new(curX, area.y, modelW, area.height);

        bool hovered = Mouse.IsOver(modelRect);
        if (hovered)
        {
            Widgets.DrawHighlight(modelRect);
        }

        using (RimLLMUIStyle.With(TextAnchor.MiddleLeft, GameFont.Tiny, wordWrap: false))
        {
            Widgets.Label(modelRect, "<color=#94a3b8>" + RimLLMUIStyle.TruncateCached(modelDisplay, modelRect.width) + ColorCloseTag);
        }

        if (hovered)
        {
            TooltipHandler.TipRegion(modelRect, "RimLLM_ChatModelBadgeTooltip".Translate(modelId));
        }

        if (Widgets.ButtonInvisible(modelRect))
        {
            GUIUtility.systemCopyBuffer = modelId;
            Messages.Message("RimLLM_CopiedToClipboard".Translate(modelId), MessageTypeDefOf.TaskCompletion, historical: false);
        }

        curX += modelW;

        if (area.xMax - curX > 50f)
        {
            Rect dotRect = new(curX, area.y, gap, area.height);
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
        Rect statRect = new(curX, area.y, statW, area.height);

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
            ChatEntryMeta? meta = layout.Meta;
            if (meta is not { } stat) return;
            float timeSec = stat.ElapsedMs / 1000f;
            string statTip = stat.IsEstimatedTokens
                ? "RimLLM_ChatStatBadgeTooltipEst".Translate(stat.ElapsedMs, timeSec.ToString("F2", CultureInfo.InvariantCulture), stat.TotalTokens.ToString("N0", CultureInfo.InvariantCulture))
                : "RimLLM_ChatStatBadgeTooltip".Translate(stat.ElapsedMs, timeSec.ToString("F2", CultureInfo.InvariantCulture), stat.TotalTokens.ToString("N0", CultureInfo.InvariantCulture), stat.PromptTokens.ToString("N0", CultureInfo.InvariantCulture), stat.CompletionTokens.ToString("N0", CultureInfo.InvariantCulture));
            TooltipHandler.TipRegion(statRect, statTip);
        }
    }

    /// <summary>一張氣泡卡片的預算結果（解析後的標籤與內文、量測高度、徽章）；<see cref="Entry"/> 記住來源字串，讓快取以參考相等判定是否過期。</summary>
    private sealed class BubbleLayout
    {
        public required string Entry;
        public bool IsUser;
        public required string Body;
        /// <summary>原始 Markdown 內文切成的段落；舊紀錄為 null，直接畫 <see cref="Body"/>。</summary>
        public List<BubblePart>? Parts;
        public ChatEntryMeta? Meta;
        public float Height;
        public float TextHeight;
        public required string HeaderText;
        public string? ModelDisplay;
        public float ModelTextWidth;
        public string? StatText;
        public float StatTextWidth;
    }

    private static readonly List<BubbleLayout> _layoutCache = [];
    private static float _layoutCacheWidth = -1f;
    private static GameFont _layoutCacheFont;

    private static GUIStyle? _richLabelStyle;
    private static GameFont _richLabelStyleFont;

    /// <summary>內文用的 rich text 樣式；GUIStyle 是 Unity 原生物件，只有字型變了才重建。</summary>
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

    /// <summary>逐筆比對歷史項目與快取的來源字串：沒變的沿用，變了的才重新解析與量測；寬度或字型變了則整批重算（Translate、StripTags 與 CalcHeight 成本高）。</summary>
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
        bool isUser = ParseMessage(entry, out string? label, out string? parsedBody, out ChatEntryMeta? meta);
        string body = parsedBody ?? string.Empty;
        List<BubblePart>? parts = null;
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

            string timeStr = meta.ElapsedMs < 1000 ? string.Create(CultureInfo.CurrentCulture, $"{meta.ElapsedMs}ms") : string.Create(CultureInfo.CurrentCulture, $"{(meta.ElapsedMs / 1000f):F1}s");
            string tokenPrefix = meta.IsEstimatedTokens ? "~" : "";
            string tokenStr = meta.TotalTokens > 0 ? string.Create(CultureInfo.CurrentCulture, $"{tokenPrefix}{meta.TotalTokens:N0} tok") : "";
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
}
