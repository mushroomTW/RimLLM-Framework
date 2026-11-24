using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod;

public static partial class ChatTestDrawer
{
    private sealed class StreamRenderState
    {
        public readonly Lock ReplyLock = new();
        public readonly StringBuilder Reasoning = new();
        public readonly StringBuilder Answer = new();
        public long LastRenderTimestamp;
        public bool TrailingRenderScheduled;
        public bool StreamCompleted;
        public string? ActualModelId;
        public int PromptTokens;
        public int CompletionTokens;
        public int TotalTokens;
        public bool HasExactTokens;
        public required System.Diagnostics.Stopwatch Stopwatch;
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

        public void Append(ChatResponseUpdate update)
        {
            if (update?.Contents == null) return;

            bool renderNow;
            bool scheduleTrailing = false;
            lock (ReplyLock)
            {
                bool appended = false;
                foreach (AIContent part in update.Contents)
                {
                    if (part is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    {
                        Reasoning.Append(reasoning.Text);
                        appended = true;
                    }
                    else if (part is TextContent text && !string.IsNullOrEmpty(text.Text))
                    {
                        Answer.Append(text.Text);
                        appended = true;
                    }
                }
                if (!appended) return;

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
                _ = Task.Delay(StreamRenderIntervalMs, RequestToken).ContinueWith(_ =>
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
                string localReply = BuildMarkdownBody(Reasoning.ToString(), Answer.ToString());
                var liveMeta = new ChatEntryMeta
                {
                    ModelId = ActualModelId ?? string.Empty,
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

    private static void StartChatStreaming(string userPrompt, string? pinnedModel)
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

        _ = Task.Run(() => ExecuteChatStreamingAsync(userPrompt, pinnedModel, requestGeneration, aiHistoryIndex, requestCts, requestToken), CancellationToken.None);
    }

    private static async Task ExecuteChatStreamingAsync(
        string userPrompt,
        string? pinnedModel,
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
            await StreamChatAsync(userPrompt, pinnedModel, renderState).ConfigureAwait(false);
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

    private static async Task StreamChatAsync(string userPrompt, string? pinnedModel, StreamRenderState renderState)
    {
        using var client = RimLLMProvider.CreateChatClient(ServiceConsumerList.DebugChatModId);
        var messages = new List<ChatMessage> { new(ChatRole.User, userPrompt) };
        var options = new RimLLMChatOptions
        {
            MaxOutputTokens = 4096,
            Temperature = 0.7f,
            DisableReasoning = false,
            Reasoning = chatReasoningEffort.HasValue ? new ReasoningOptions { Effort = chatReasoningEffort.Value } : null,
            ModelId = pinnedModel,
            // 測試介面選定哪個模型就只測哪個，失敗要顯示失敗，不能被備援鏈悄悄換成別的模型。
            StrictModel = !string.IsNullOrEmpty(pinnedModel)
        };

        var enumerator = client.GetStreamingResponseAsync(messages, options, renderState.RequestToken).GetAsyncEnumerator(renderState.RequestToken);
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var update = enumerator.Current;
                renderState.RecordUsage(update);
                renderState.Append(update);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        string reasoningText;
        string answerText;
        lock (renderState.ReplyLock)
        {
            renderState.StreamCompleted = true;
            reasoningText = renderState.Reasoning.ToString();
            answerText = renderState.Answer.ToString();
        }

        renderState.Stopwatch.Stop();
        var finalMeta = BuildFinalChatMeta(userPrompt, reasoningText + answerText, renderState);
        UpdateAiHistoryEntry(renderState.RequestGeneration, renderState.AiHistoryIndex, BuildMarkdownBody(reasoningText, answerText), meta: finalMeta, persist: true);
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
            ModelId = state.ActualModelId ?? string.Empty,
            ElapsedMs = state.Stopwatch.ElapsedMilliseconds,
            TotalTokens = totalTokens,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            IsEstimatedTokens = !hasExact
        };
    }
}
