using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using RimLLM_Framework.Core;
using RimLLM_Framework.Mod;
using RimLLM_Framework.Providers;
using RimWorld;
using Verse;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// 負責端到端對話執行管道（Chat Execution Pipeline），
    /// 包含請求正規化、防濫用節流、每日預算檢查、並行排隊、Fallback 備援調度、
    /// 原生 Schema 降級重試、串流緩衝水槽以及結構化輸出反序列化與二次修復。
    /// </summary>
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    public class RimLLMChatExecutionPipeline
    {
        /// <summary>預算模擬回應的供應商／模型識別，供呼叫端在 ChatResponse.ModelId 上辨識。</summary>
        internal const string MockProviderId = "rimllm";
        internal const string MockModelName = "budget-mock";

        private readonly IRimLLMSettings _settings;
        private readonly RimLLMRequestQueue _requestQueue;
        private readonly RimLLMFallbackPipeline _fallbackPipeline;
        private readonly RimLLMUsageTracker _usageTracker;

        // Anti-abuse state
        private readonly ConcurrentDictionary<string, List<DateTime>> _requestTimestamps =
            new ConcurrentDictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _coolDownUntil =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        internal RimLLMChatExecutionPipeline(
            IRimLLMSettings settings,
            RimLLMRequestQueue requestQueue,
            RimLLMFallbackPipeline fallbackPipeline,
            RimLLMUsageTracker usageTracker)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _requestQueue = requestQueue ?? throw new ArgumentNullException(nameof(requestQueue));
            _fallbackPipeline = fallbackPipeline ?? throw new ArgumentNullException(nameof(fallbackPipeline));
            _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
        }

        /// <summary>
        /// 執行非串流文字生成。
        /// </summary>
        internal async Task<RimLLMGenerationResult> GenerateAsync(RimLLMRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            // 正規化與回應快取已上移為 IChatClient 中介層（見 RimLLMManager.CreateChatClient），
            // 因此抵達這裡的請求必然已正規化，且必然是快取沒有命中的。
            // 准入檢查一律在進入佇列之前執行，且整條請求路徑只執行一次。
            if (await RunAdmissionChecksAsync(request).ConfigureAwait(false) is string mockResult)
            {
                return BuildMockResult(mockResult);
            }

            return await _requestQueue.EnqueueRequestAsync(request, () =>
                GenerateDirectAsync(request)).ConfigureAwait(false);
        }

        /// <summary>
        /// 執行串流文字生成。
        /// </summary>
        internal async Task<RimLLMGenerationResult> StreamAsync(
            RimLLMRequest request,
            Action<string> onChunkReceived)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (await RunAdmissionChecksAsync(request).ConfigureAwait(false) is string mockResult)
            {
                onChunkReceived?.Invoke(mockResult);
                return BuildMockResult(mockResult);
            }

            return await _requestQueue.EnqueueRequestAsync(request, () =>
                StreamDirectAsync(request, onChunkReceived)).ConfigureAwait(false);
        }

        private Task<RimLLMGenerationResult> GenerateDirectAsync(RimLLMRequest request)
        {
            return _fallbackPipeline.ExecuteWithFallbackAsync(
                request,
                (provider, modelName) => GenerateProviderAsync(provider, request, modelName),
                LLMError.Unknown,
                "All fallback attempts failed.");
        }

        private async Task<RimLLMGenerationResult> StreamDirectAsync(RimLLMRequest request, Action<string> onChunkReceived)
        {
            var sink = new StreamAttemptSink(onChunkReceived, request.OnStreamRestart, DispatchRestart);

            RimLLMGenerationResult attempt = await _fallbackPipeline.ExecuteWithFallbackAsync(
                request,
                (provider, modelName) => StreamProviderAsync(provider, request, modelName, sink.Append),
                LLMError.ProviderOffline,
                "All fallback attempts failed, unable to establish stream connection.",
                onAttemptStarting: sink.BeginAttempt).ConfigureAwait(false);

            // 文字以 sink 為準（涵蓋 restart 後的重播），其餘一律取自成功那次嘗試：
            // 先前只回傳 Text 與 Contents，使得串流路徑的 ProviderId、ModelName 與三個 token
            // 計數全部遺失，呼叫端拿到空的 ModelId 與全零的 UsageContent。
            // 用量記帳本身不受影響（記錄發生在 executor 內），遺失的只是呼叫端可見的中繼資料。
            return new RimLLMGenerationResult
            {
                Text = sink.Result,
                Contents = attempt?.Contents,
                ProviderId = attempt?.ProviderId,
                ModelName = attempt?.ModelName,
                PromptTokens = attempt?.PromptTokens ?? 0,
                CompletionTokens = attempt?.CompletionTokens ?? 0,
                CachedPromptTokens = attempt?.CachedPromptTokens ?? 0
            };
        }

        private async Task<RimLLMGenerationResult> GenerateProviderAsync(ILLMProvider provider, RimLLMRequest request, string model)
        {
            request = StripUnsupportedTools(provider, request);
            bool useNativeSchema = request.ResponseType != null && IsNativeStructuredProvider(provider);
            if (useNativeSchema)
            {
                try
                {
                    using (IChatClient nativeClient = provider.CreateChatClient(model))
                    {
                        return await RimLLMChatClientExecutor.GenerateAsync(
                            nativeClient,
                            request,
                            model,
                            useNativeSchema: true,
                            provider.ProviderId,
                            _settings.ApiTimeout).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (IsNativeSchemaRejected(ex))
                {
                    return await GenerateWithoutNativeSchemaAsync(provider, request, model).ConfigureAwait(false);
                }
            }

            using (IChatClient client = provider.CreateChatClient(model))
            {
                RimLLMRequest providerRequest = PrepareRequestForProvider(provider, request);
                return await RimLLMChatClientExecutor.GenerateAsync(
                    client,
                    providerRequest,
                    model,
                    useNativeSchema: false,
                    provider.ProviderId,
                    _settings.ApiTimeout).ConfigureAwait(false);
            }
        }

        private async Task<RimLLMGenerationResult> StreamProviderAsync(
            ILLMProvider provider,
            RimLLMRequest request,
            string model,
            Action<string> onChunkReceived)
        {
            request = StripUnsupportedTools(provider, request);
            using (IChatClient client = provider.CreateChatClient(model))
            {
                RimLLMRequest providerRequest = PrepareRequestForProvider(provider, request);
                return await RimLLMChatClientExecutor.StreamAsync(
                    client,
                    providerRequest,
                    model,
                    request.ResponseType != null && IsNativeStructuredProvider(provider),
                    provider.ProviderId,
                    onChunkReceived,
                    _settings.ApiTimeout).ConfigureAwait(false);
            }
        }

        private async Task<RimLLMGenerationResult> GenerateWithoutNativeSchemaAsync(
            ILLMProvider provider,
            RimLLMRequest request,
            string model)
        {
            RimLLMRequest fallbackRequest = PrepareRequestForProvider(
                provider,
                request,
                forceJsonFallback: true);

            using (IChatClient client = provider.CreateChatClient(model))
            {
                return await RimLLMChatClientExecutor.GenerateAsync(
                    client,
                    fallbackRequest,
                    model,
                    useNativeSchema: false,
                    provider.ProviderId,
                    _settings.ApiTimeout).ConfigureAwait(false);
            }
        }

        private static bool IsNativeSchemaRejected(Exception exception)
        {
            if (exception == null || exception is OperationCanceledException)
            {
                return false;
            }

            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is RimLLMException rimException && rimException.IsSchemaRejection)
                {
                    return true;
                }
            }

            string message = exception.ToString().ToLowerInvariant();
            bool mentionsSchema = message.Contains("schema") ||
                                  message.Contains("response_format") ||
                                  message.Contains("response format") ||
                                  message.Contains("structured output");
            bool looksLikeRejection = message.Contains("400") ||
                                      message.Contains("invalid") ||
                                      message.Contains("unsupported") ||
                                      message.Contains("not support") ||
                                      message.Contains("unrecognized");
            return mentionsSchema && looksLikeRejection;
        }

        /// <summary>
        /// 供應商不支援原生工具呼叫時移除 Tools/ToolMode，並留下警告。
        /// 直接把 tools 送給不認得的供應商會被靜默忽略，呼叫端只會拿到一段散文而不知道工具沒送出去。
        /// </summary>
        private static RimLLMRequest StripUnsupportedTools(ILLMProvider provider, RimLLMRequest request)
        {
            if (request?.Tools == null || request.Tools.Count == 0) return request;
            if (provider?.Capabilities?.SupportsFunctionCalling == true) return request;

            RimLLMLog.Warning(
                $"[RimLLM] 供應商 {provider?.ProviderId} 不支援原生工具呼叫，本次請求的 {request.Tools.Count} 個工具已被移除。");

            RimLLMRequest clone = request.Clone();
            clone.Tools = null;
            clone.ToolMode = null;
            return clone;
        }

        private RimLLMRequest PrepareRequestForProvider(
            ILLMProvider provider,
            RimLLMRequest request,
            bool forceJsonFallback = false)
        {
            if (request.ResponseType == null ||
                (!forceJsonFallback && IsNativeStructuredProvider(provider)))
            {
                return request;
            }

            RimLLMRequest clone = request.Clone();
            string originalSystemPrompt = clone.SystemPrompt ?? string.Empty;
            string schemaInstructions =
                "\n\n[結構化輸出要求：只能回傳符合下列結構的原始 JSON，不要加入 Markdown code fence 或其他說明。範例：\n" +
                RimLLMJsonHelper.GetSampleJson(request.ResponseType) + "]";
            clone.SystemPrompt = originalSystemPrompt + schemaInstructions;
            if (clone.Messages != null && clone.Messages.Count > 0)
            {
                var messagesCopy = new List<ChatMessage>(clone.Messages);
                int sysIdx = messagesCopy.FindIndex(m => m.Role == ChatRole.System);
                if (sysIdx >= 0)
                {
                    messagesCopy[sysIdx] = new ChatMessage(ChatRole.System, clone.SystemPrompt);
                }
                else
                {
                    messagesCopy.Insert(0, new ChatMessage(ChatRole.System, clone.SystemPrompt));
                }
                clone.Messages = messagesCopy;
            }
            return clone;
        }

        private bool IsNativeStructuredProvider(ILLMProvider provider)
        {
            return provider?.Capabilities?.SupportsNativeStructuredOutput == true &&
                   _settings.EnableNativeSchema;
        }

        /// <summary>
        /// 結構化輸出的核心流程：直接解析 → JSON repair 回退 → LLM-assisted double-repair。
        /// </summary>
        internal T DeserializeStructured<T>(string rawResponse, RimLLMRequest request)
        {
            try
            {
                return RimLLMJsonHelper.DeserializeAndValidate<T>(rawResponse);
            }
            catch (Exception ex)
            {
                if (!_settings.EnableJsonRepair)
                {
                    throw new RimLLMException(
                        LLMError.InvalidResponse,
                        $"Unable to parse LLM response to target object {typeof(T).Name} (JSON Repair is disabled). Raw Response: {RimLLMLog.SanitizeForLog(rawResponse, 300)}. Parse error: {RimLLMLog.SanitizeForLog(ex.Message, 200)}",
                        ex);
                }

                string repairedJson = RimLLMJsonHelper.RepairJson(rawResponse);
                RimLLMLog.Warning($"[RimLLM] First JSON parse failed, attempting static repair. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}\nRepaired preview: {RimLLMLog.SanitizeForLog(repairedJson, 300)}\nError: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
                try
                {
                    string fallbackExtracted = RimLLMJsonHelper.ExtractJsonBlock(repairedJson);
                    return RimLLMJsonHelper.DeserializeAndValidate<T>(fallbackExtracted);
                }
                catch (Exception repairEx)
                {
                    throw new RimLLMException(
                        LLMError.InvalidResponse,
                        $"Unable to parse LLM response to target object {typeof(T).Name}. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}. Parse error: {RimLLMLog.SanitizeForLog(ex.Message, 200)}. Static repair error: {RimLLMLog.SanitizeForLog(repairEx.Message, 200)}",
                        repairEx);
                }
            }
        }

        internal static T DeserializeAndValidate<T>(string json)
        {
            return RimLLMJsonHelper.DeserializeAndValidate<T>(json);
        }

        private async Task<string> RunAdmissionChecksAsync(RimLLMRequest request)
        {
            if (_settings.EnableAntiAbuse)
            {
                CheckAntiAbuse(request.ModId);
            }

            bool budgetOk = await _usageTracker.CheckBudgetLimitAsync(request).ConfigureAwait(false);
            if (!budgetOk)
            {
                throw new RimLLMException(LLMError.QuotaExceeded, "Daily budget limit exceeded.");
            }

            return _usageTracker.IsBudgetMocked(request, out string mockResult) ? mockResult : null;
        }

        public void CheckAntiAbuse(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return;

            DateTime now = DateTime.UtcNow;
            if (_coolDownUntil.TryGetValue(modId, out DateTime cdTime) && now < cdTime)
            {
                throw new RimLLMException(LLMError.RateLimit, $"[RimLLM] Mod '{modId}' is in anti-abuse cooldown until {cdTime.ToLocalTime()}.");
            }

            var list = _requestTimestamps.GetOrAdd(modId, _ => new List<DateTime>());
            lock (list)
            {
                DateTime limit = now.AddSeconds(-_settings.ThrottlingWindowSeconds);
                list.RemoveAll(t => t < limit);
                list.Add(now);

                if (list.Count > _settings.MaxRequestsPerWindow)
                {
                    DateTime cdUntil = now.AddSeconds(_settings.CoolDownDurationSeconds);
                    _coolDownUntil[modId] = cdUntil;
                    RimLLMLog.Warning($"[RimLLM] Mod '{modId}' triggered anti-abuse throttling limit. Cooling down until {cdUntil.ToLocalTime()}.");
                    throw new RimLLMException(LLMError.RateLimit, $"[RimLLM] Mod '{modId}' triggered anti-abuse throttling limit. Cooling down until {cdUntil.ToLocalTime()}.");
                }
            }
        }

        public void ClearCooldowns()
        {
            _requestTimestamps.Clear();
            _coolDownUntil.Clear();
        }

        /// <summary>
        /// 預算靜默模擬（BudgetPolicy = SilentMocking）的回應。
        /// 給它明確的供應商／模型識別，否則呼叫端只會拿到空的 ModelId，
        /// 無從分辨「這是模擬回應」與「真的呼叫了但供應商沒回傳模型名」。
        /// </summary>
        private static RimLLMGenerationResult BuildMockResult(string text)
        {
            return new RimLLMGenerationResult
            {
                Text = text,
                ProviderId = MockProviderId,
                ModelName = MockModelName
            };
        }


        private static void DispatchRestart(Action callback)
        {
            if (callback == null) return;
            RimLLMDispatcher.EnqueueOnMainThread(callback);
        }

        private sealed class StreamAttemptSink
        {
            private readonly StringBuilder _buffer = new StringBuilder();
            private readonly Action<string> _forward;
            private readonly Action _onRestart;
            private readonly Action<Action> _dispatchRestart;
            private bool _emittedAnything;

            public StreamAttemptSink(Action<string> forward, Action onRestart, Action<Action> dispatchRestart)
            {
                _forward = forward;
                _onRestart = onRestart;
                _dispatchRestart = dispatchRestart;
            }

            public string Result => _buffer.ToString();

            public void BeginAttempt()
            {
                if (_emittedAnything)
                {
                    _dispatchRestart?.Invoke(_onRestart);
                }
                _buffer.Length = 0;
                _emittedAnything = false;
            }

            public void Append(string chunk)
            {
                if (string.IsNullOrEmpty(chunk)) return;
                _buffer.Append(chunk);
                _emittedAnything = true;
                _forward?.Invoke(chunk);
            }
        }
    }
#pragma warning restore S101
}
