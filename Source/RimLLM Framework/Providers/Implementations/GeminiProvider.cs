extern alias bclasync;
extern alias ste;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Google Gemini API 供應商，支援 generateContent 與 streamGenerateContent。
    /// </summary>
    public class GeminiProvider : BaseHttpProvider
    {
        public override string ProviderId => ProviderIds.Gemini;

        private sealed class GeminiCacheEntry
        {
            public string CacheId { get; set; }
            public DateTime ExpireTime { get; set; }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GeminiCacheEntry> _contextCaches =
            new System.Collections.Concurrent.ConcurrentDictionary<string, GeminiCacheEntry>();

        // 對同一 cacheKey 的快取建立流程加鎖，避免並發時重複建立資源（重複付建立費）。
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> _cacheCreationLocks =
            new System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim>();

        /// <summary>
        /// Gemini 原生安全設定。維持空集合時使用 Gemini API 預設安全策略。
        /// 這是 provider-specific 設定，不會洩漏到共用 SDK facade。
        /// </summary>
        public IList<SafetySetting> SafetySettings { get; } = new List<SafetySetting>();

        public override LLMProviderCapabilities Capabilities => new LLMProviderCapabilities
        {
            SupportsNativeStructuredOutput = true,
            SupportsStreaming = true,
            SupportsUsageMetadata = true,
            // Google.GenAI 的 Schema.Type 是單一列舉值，聯集型別會讓 Schema.FromJson 靜默回傳 null。
            PreferredSchemaProfile = RimLLMSchemaProfile.Gemini
        };

        public GeminiProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        public override IChatClient CreateChatClient(string model)
        {
            return new GeminiChatClientAdapter(this, model);
        }

        public static IChatClient CreateGeminiChatClient(string apiKey, string model)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("Gemini API key 不得為空。", nameof(apiKey));
            }
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentException("Gemini model 不得為空。", nameof(model));
            }

            var client = new Client(apiKey: apiKey);
            return client.AsIChatClient(model);
        }

        private sealed class GeminiChatClientAdapter : IChatClient
        {
            private readonly GeminiProvider _provider;
            private readonly string _model;

            public GeminiChatClientAdapter(GeminiProvider provider, string model)
            {
                _provider = provider;
                _model = model;
            }

            public void Dispose()
            {
                // No unmanaged resources
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                System.Threading.CancellationToken cancellationToken = default)
            {
                try
                {
                    string text = await _provider.GenerateWithGoogleGenAiAsync(messages, options, _model).ConfigureAwait(false);
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
                    {
                        ModelId = _model
                    };
                }
                catch (RimLLMException ex)
                {
                    if (_provider.MarkReasoningUnsupported(_model, ex))
                    {
                        string text = await _provider.GenerateWithGoogleGenAiAsync(messages, options, _model).ConfigureAwait(false);
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
                        {
                            ModelId = _model
                        };
                    }
                    throw;
                }
            }

            public bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions options = null,
                System.Threading.CancellationToken cancellationToken = default)
            {
                return new GeminiStreamEnumerable(_provider, messages, options, _model, cancellationToken);
            }

            public object GetService(System.Type serviceType, object serviceKey = null)
            {
                if (serviceType == typeof(ChatClientMetadata))
                {
                    return new ChatClientMetadata("Gemini", null, _model);
                }
                return null;
            }
        }

        private sealed class GeminiStreamEnumerable : bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate>
        {
            private readonly GeminiProvider _provider;
            private readonly IEnumerable<ChatMessage> _messages;
            private readonly ChatOptions _options;
            private readonly string _model;
            private readonly System.Threading.CancellationToken _cancellationToken;

            public GeminiStreamEnumerable(
                GeminiProvider provider,
                IEnumerable<ChatMessage> messages,
                ChatOptions options,
                string model,
                System.Threading.CancellationToken cancellationToken)
            {
                _provider = provider;
                _messages = messages;
                _options = options;
                _model = model;
                _cancellationToken = cancellationToken;
            }

            public bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(
                System.Threading.CancellationToken cancellationToken = default)
            {
                var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
                var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>(
                    new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

                StartProducer(channel.Writer, linkedCts.Token);

                return new GeminiStreamEnumerator(
                    channel.Reader.ReadAllAsync(linkedCts.Token).GetAsyncEnumerator(linkedCts.Token),
                    linkedCts);
            }

#pragma warning disable S3776 // reason: 串流生產者之重試與通道完成處理，維持內聚
            private void StartProducer(
                System.Threading.Channels.ChannelWriter<ChatResponseUpdate> writer,
                System.Threading.CancellationToken cancellationToken)
            {
                Task.Run(async () =>
                {
                    bool hasWrittenAnyChunk = false;
                    try
                    {
                        await _provider.StreamWithGoogleGenAiAsync(
                            _messages,
                            _options,
                            _model,
                            chunk =>
                            {
                                if (!string.IsNullOrEmpty(chunk))
                                {
                                    hasWrittenAnyChunk = true;
                                    writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, chunk));
                                }
                            }).ConfigureAwait(false);
                        writer.TryComplete();
                    }
                    catch (RimLLMException ex)
                    {
                        if (!hasWrittenAnyChunk && _provider.MarkReasoningUnsupported(_model, ex))
                        {
                            try
                            {
                                await _provider.StreamWithGoogleGenAiAsync(
                                    _messages,
                                    _options,
                                    _model,
                                    chunk =>
                                    {
                                        if (!string.IsNullOrEmpty(chunk))
                                        {
                                            writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, chunk));
                                        }
                                    }).ConfigureAwait(false);
                                writer.TryComplete();
                                return;
                            }
                            catch (Exception retryEx)
                            {
                                writer.TryComplete(retryEx);
                                return;
                            }
                        }
                        writer.TryComplete(ex);
                    }
                    catch (Exception ex)
                    {
                        writer.TryComplete(ex);
                    }
                }, cancellationToken);
            }
        }

        private sealed class GeminiStreamEnumerator : bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate>
        {
            private readonly bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> _inner;
            private readonly System.Threading.CancellationTokenSource _linkedCts;

            public GeminiStreamEnumerator(
                bclasync::System.Collections.Generic.IAsyncEnumerator<ChatResponseUpdate> inner,
                System.Threading.CancellationTokenSource linkedCts)
            {
                _inner = inner;
                _linkedCts = linkedCts;
            }

            public ChatResponseUpdate Current => _inner.Current;

            public async ste::System.Threading.Tasks.ValueTask<bool> MoveNextAsync()
            {
                try
                {
                    return await _inner.MoveNextAsync().ConfigureAwait(false);
                }
                catch (System.Threading.Channels.ChannelClosedException ex) when (ex.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
            }

            public ste::System.Threading.Tasks.ValueTask DisposeAsync()
            {
                try
                {
                    _linkedCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 若 CTS 已被釋放則安全忽略，不拋出異常
                }

                _linkedCts.Dispose();
                return _inner.DisposeAsync();
            }
        }

        /// <summary>
        /// 服務端拒絕 thinkingConfig 時記下來，讓後續請求略過思考設定。
        /// 模型名的判斷只是快速路徑，真正的權威是服務端的回應。
        /// </summary>
        private bool MarkReasoningUnsupported(string model, RimLLMException exception)
        {
            if (!exception.IsReasoningRejection ||
                !RimLLMReasoningSupport.MarkReasoningUnsupported(ProviderId, model))
            {
                return false;
            }

            RimLLMLog.Warning($"[RimLLM] {ProviderId} 的模型 {model} 不接受思考設定，之後將不再送出。");
            return true;
        }

        /// <summary>建立 Google.GenAI 用戶端（測試縫）。</summary>
        protected virtual Client CreateGenAiClient(string apiKey)
        {
            return new Client(apiKey: apiKey);
        }

        /// <summary>建立 cachedContents 資源（測試縫）。走官方 SDK 的 Caches.CreateAsync。</summary>
        protected virtual async Task<CachedContent> CreateCachedContentNativeAsync(
            string apiKey,
            string modelWithPrefix,
            CreateCachedContentConfig config,
            System.Threading.CancellationToken cancellationToken)
        {
            using (Client client = CreateGenAiClient(apiKey))
            {
                return await client.Caches.CreateAsync(modelWithPrefix, config, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>呼叫非串流 generateContent（測試縫）。</summary>
        protected virtual Task<GenerateContentResponse> GenerateContentNativeAsync(
            Client client,
            string model,
            List<Content> contents,
            GenerateContentConfig config,
            System.Threading.CancellationToken ct)
        {
            return client.Models.GenerateContentAsync(model, contents, config, ct);
        }

        /// <summary>呼叫串流 generateContent（測試縫）。</summary>
        protected virtual bclasync::System.Collections.Generic.IAsyncEnumerable<GenerateContentResponse> GenerateContentStreamNativeAsync(
            Client client,
            string model,
            List<Content> contents,
            GenerateContentConfig config,
            System.Threading.CancellationToken ct)
        {
            return client.Models.GenerateContentStreamAsync(model, contents, config, ct);
        }

        private static Content BuildTextContent(string text)
        {
            return new Content
            {
                Parts = new List<Part>
                {
                    new Part { Text = text ?? string.Empty }
                }
            };
        }

        private static List<Content> BuildContents(IEnumerable<ChatMessage> messages)
        {
            var contents = new List<Content>();
            if (messages != null)
            {
                foreach (var m in messages)
                {
                    if (m != null && m.Role != ChatRole.System && !string.IsNullOrEmpty(m.Text))
                    {
                        contents.Add(BuildTextContent(m.Text));
                    }
                }
            }
            if (contents.Count == 0)
            {
                contents.Add(BuildTextContent(string.Empty));
            }
            return contents;
        }

        private async Task<string> GenerateWithGoogleGenAiAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            try
            {
                using (Client client = CreateGenAiClient(apiKey))
                {
                    List<Content> contents = BuildContents(messages);
                    GenerateContentConfig config = await BuildNativeConfigAsync(messages, options, model, apiKey).ConfigureAwait(false);
                    GenerateContentResponse response = await GenerateContentNativeAsync(
                        client,
                        model,
                        contents,
                        config,
                        default).ConfigureAwait(false);
                    return ReadGeminiResponse(response, model);
                }
            }
            catch (RimLLMException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw TranslateGoogleException(ex, "generateContent");
            }
        }

        #pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
        private async Task StreamWithGoogleGenAiAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options,
            string model,
            Action<string> onChunkReceived)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            try
            {
                using (Client client = CreateGenAiClient(apiKey))
                {
                    List<Content> contents = BuildContents(messages);
                    GenerateContentConfig config = await BuildNativeConfigAsync(messages, options, model, apiKey).ConfigureAwait(false);
                    bool inReasoning = false;
                    bool hasFinishedReasoning = false;
                    int completionChars = 0;
                    int promptTokens = 0;
                    int completionTokens = 0;
                    int cachedTokens = 0;
                    bool hasUsage = false;

                    await foreach (GenerateContentResponse response in GenerateContentStreamNativeAsync(
                        client,
                        model,
                        contents,
                        config,
                        default))
                    {
                        if (response?.PromptFeedback != null && (response.Parts == null || response.Parts.Count == 0))
                        {
                            throw new RimLLMException(
                                LLMError.ContentFilter,
                                "Gemini blocked the prompt or response because of safety settings.");
                        }

                        if (response?.UsageMetadata != null)
                        {
                            promptTokens = response.UsageMetadata.PromptTokenCount ?? 0;
                            completionTokens = response.UsageMetadata.CandidatesTokenCount ?? 0;
                            cachedTokens = response.UsageMetadata.CachedContentTokenCount ?? 0;
                            hasUsage = true;
                        }

                        if (response?.Parts == null)
                        {
                            continue;
                        }

                        foreach (Part part in response.Parts)
                        {
                            if (string.IsNullOrEmpty(part?.Text))
                            {
                                continue;
                            }
                            completionChars += part.Text.Length;
                            EmitGeminiPart(part, onChunkReceived, ref inReasoning, ref hasFinishedReasoning);
                        }
                    }

                    if (inReasoning)
                    {
                        onChunkReceived?.Invoke("</think>");
                    }

                    // 零輸出的串流不得視為成功，否則會阻擋 fallback 並讓呼叫端收到空字串。
                    if (completionChars == 0)
                    {
                        throw new RimLLMException(LLMError.NetworkError, $"{ProviderId} 串流未回傳任何內容。");
                    }

                    try
                    {
                        var manager = RimLLMProvider.Manager;
                        if (manager != null)
                        {
                            if (hasUsage)
                            {
                                manager.RecordUsage(ProviderId, model, promptTokens, completionTokens, cachedTokens);
                            }
                            else
                            {
                                int promptChars = 0;
                                if (messages != null)
                                {
#pragma warning disable S3267 // reason: 累加字元長度需條件累積，Where 可讀性未提升，維持現狀
                                    foreach (var m in messages)
                                    {
                                        if (m != null && !string.IsNullOrEmpty(m.Text)) promptChars += m.Text.Length;
                                    }
#pragma warning restore S3267
                                }
                                manager.RecordUsage(
                                    ProviderId,
                                    model,
                                    Math.Max(1, (int)(promptChars * 0.8f)),
                                    Math.Max(1, (int)(completionChars * 0.8f)));
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // SDK not initialized in standalone unit tests
                    }
                }
            }
            catch (RimLLMException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw TranslateGoogleException(ex, "streamGenerateContent");
            }
        }
        #pragma warning restore S3776

        #pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
        private async Task<GenerateContentConfig> BuildNativeConfigAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options,
            string model,
            string apiKey)
        {
            bool disableReasoning = options is RimLLMChatOptions rimOptsDR
                ? rimOptsDR.DisableReasoning
                : RimLLMChatOptions.ReadAdditional(options, "rimllm_disable_reasoning", false);

            var config = new GenerateContentConfig
            {
                Temperature = options?.Temperature,
                MaxOutputTokens = options?.MaxOutputTokens,
                ThinkingConfig = BuildNativeThinkingConfig(model, options?.Reasoning?.Effort, disableReasoning),
                SafetySettings = this.SafetySettings.Count == 0
                    ? null
                    : new List<SafetySetting>(this.SafetySettings)
            };

            string systemPromptMsg = null;
            if (messages != null)
            {
                foreach (var m in messages)
                {
                    if (m != null && m.Role == ChatRole.System && !string.IsNullOrEmpty(m.Text))
                    {
                        systemPromptMsg = m.Text;
                        break;
                    }
                }
            }

            string ccStr = (options as RimLLMChatOptions)?.CachedContext;
            if (string.IsNullOrEmpty(ccStr))
            {
                ccStr = RimLLMChatOptions.ReadAdditional<string>(options, "rimllm_cached_context", null);
            }

            string systemContext;
            if (string.IsNullOrEmpty(ccStr))
            {
                systemContext = systemPromptMsg;
            }
            else
            {
                systemContext = !string.IsNullOrEmpty(systemPromptMsg) && systemPromptMsg != ccStr
                    ? systemPromptMsg + "\n\n" + ccStr
                    : ccStr;
            }

            bool enableContextCaching = (options as RimLLMChatOptions)?.EnableContextCaching ?? false;
            if (!enableContextCaching)
            {
                enableContextCaching = RimLLMChatOptions.ReadAdditional(options, "rimllm_enable_context_caching", false);
            }

            string cacheId = null;
            if (enableContextCaching && !string.IsNullOrEmpty(systemContext))
            {
                cacheId = await GetOrCreateCachedContentAsync(
                    apiKey,
                    model,
                    systemContext,
                    default).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(cacheId))
            {
                config.CachedContent = cacheId;
            }
            else if (!string.IsNullOrEmpty(systemContext))
            {
                config.SystemInstruction = BuildTextContent(systemContext);
            }

            string schemaJson = RimLLMChatOptions.ReadAdditional<string>(options, "rimllm_response_schema", null);
            if (schemaJson == null && options?.ResponseFormat is ChatResponseFormatJson jsonFormat)
            {
                schemaJson = jsonFormat.Schema?.GetRawText();
            }

            if (!string.IsNullOrEmpty(schemaJson))
            {
                config.ResponseMimeType = "application/json";
                config.ResponseSchema = Schema.FromJson(schemaJson);
            }
            else if (options?.ResponseFormat != null && Settings.EnableNativeSchema)
            {
                config.ResponseMimeType = "application/json";
            }

            return config;
        }
        #pragma warning restore S3776

        #pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本
        private ThinkingConfig BuildNativeThinkingConfig(string model, ReasoningEffort? effort, bool disableReasoning)
        {
            if (string.IsNullOrEmpty(model))
            {
                return null;
            }

            // 服務端先前明確拒絕過思考設定的模型不再嘗試。
            if (RimLLMReasoningSupport.IsReasoningUnsupported(ProviderId, model))
            {
                return null;
            }

            DetermineGeminiThinkingConfig(model, out bool isThinkingBudgetModel, out bool isThinkingLevelModel);
            if (isThinkingBudgetModel)
            {
                if (disableReasoning)
                {
                    return new ThinkingConfig
                    {
                        ThinkingBudget = 0,
                        IncludeThoughts = false
                    };
                }

                int budget = -1;
                if (effort == ReasoningEffort.Low) budget = 1024;
                else if (effort == ReasoningEffort.Medium) budget = 2048;
                else if (effort == ReasoningEffort.High) budget = 4096;

                return new ThinkingConfig
                {
                    ThinkingBudget = budget,
                    IncludeThoughts = true
                };
            }

            if (isThinkingLevelModel)
            {
                if (disableReasoning)
                {
                    return new ThinkingConfig
                    {
                        ThinkingLevel = ThinkingLevel.Minimal,
                        IncludeThoughts = false
                    };
                }

                if (!effort.HasValue)
                {
                    return null;
                }

                ThinkingLevel level = ThinkingLevel.ThinkingLevelUnspecified;
                if (effort == ReasoningEffort.Low) level = ThinkingLevel.Low;
                else if (effort == ReasoningEffort.Medium) level = ThinkingLevel.Medium;
                else if (effort == ReasoningEffort.High) level = ThinkingLevel.High;

                return new ThinkingConfig
                {
                    ThinkingLevel = level,
                    IncludeThoughts = true
                };
            }

            return null;
        }
        #pragma warning restore S3776

        private string ReadGeminiResponse(GenerateContentResponse response, string model)
        {
            if (response == null)
            {
                throw new RimLLMException(LLMError.InvalidResponse, "Gemini returned no response.");
            }
            if (response.PromptFeedback != null && (response.Parts == null || response.Parts.Count == 0))
            {
                throw new RimLLMException(
                    LLMError.ContentFilter,
                    "Gemini blocked the prompt or response because of safety settings.");
            }
            if (response.Parts == null || response.Parts.Count == 0)
            {
                throw new RimLLMException(LLMError.InvalidResponse, "Gemini response contains no content parts.");
            }

            var builder = new StringBuilder();
            bool inReasoning = false;
            bool hasFinishedReasoning = false;
            foreach (Part part in response.Parts)
            {
                if (string.IsNullOrEmpty(part?.Text))
                {
                    continue;
                }
                EmitGeminiPart(part, value => builder.Append(value), ref inReasoning, ref hasFinishedReasoning);
            }
            if (inReasoning)
            {
                builder.Append("\n</think>");
            }

            string result = builder.ToString();
            if (string.IsNullOrEmpty(result))
            {
                throw new RimLLMException(LLMError.InvalidResponse, "Gemini response text is empty.");
            }

            if (response.UsageMetadata != null)
            {
                RimLLMProvider.Manager.RecordUsage(
                    ProviderId,
                    model,
                    response.UsageMetadata.PromptTokenCount ?? 0,
                    response.UsageMetadata.CandidatesTokenCount ?? 0,
                    response.UsageMetadata.CachedContentTokenCount ?? 0);
            }
            return result;
        }

        private static void EmitGeminiPart(
            Part part,
            Action<string> emit,
            ref bool inReasoning,
            ref bool hasFinishedReasoning)
        {
            if (part.Thought == true)
            {
                // 思考已經收尾過的話不再開新的 <think>，但內容照樣輸出 ——
                // 原本的 if/else 兩個分支結尾都是同一行 emit，只有開頭的標記需要條件判斷。
                if (!hasFinishedReasoning && !inReasoning)
                {
                    inReasoning = true;
                    emit("<think>\n");
                }
                emit(part.Text);
                return;
            }

            if (inReasoning)
            {
                inReasoning = false;
                hasFinishedReasoning = true;
                emit("\n</think>\n");
            }
            emit(part.Text);
        }

        /// <summary>
        /// 將 Google.GenAI 的例外轉為框架的 <see cref="RimLLMException"/>。
        /// 對話與 embedding 兩條路徑共用同一份對照。
        /// </summary>
        internal static Exception TranslateGoogleException(Exception exception, string operation)
        {
            if (exception is OperationCanceledException)
            {
                return new RimLLMException(LLMError.Cancelled, $"Gemini {operation} was cancelled.", exception);
            }
            if (exception is ClientError clientError)
            {
                LLMError error;
                switch (clientError.StatusCode)
                {
                    case 429: error = LLMError.RateLimit; break;
                    case 401:
                    case 403: error = LLMError.InvalidKey; break;
                    case 404: error = LLMError.ModelNotFound; break;
                    default: error = LLMError.InvalidResponse; break;
                }
                var translated = new RimLLMException(
                    error,
                    $"Gemini {operation} failed ({clientError.StatusCode}): {RimLLMLog.SanitizeForLog(clientError.Message, 300)}",
                    exception);
                if (clientError.StatusCode == 400)
                {
                    // 服務端指名 thinking 相關欄位時標記起來，讓上層去掉思考設定重打一次。
                    translated.IsReasoningRejection = LLMErrorMapper.LooksLikeReasoningRejection(clientError.Message);
                }
                return translated;
            }
            if (exception is ServerError serverError)
            {
                return new RimLLMException(
                    LLMError.ProviderOffline,
                    $"Gemini {operation} failed with a server error: {RimLLMLog.SanitizeForLog(serverError.Message, 300)}",
                    exception);
            }
            return new RimLLMException(
                LLMError.Unknown,
                $"Gemini {operation} failed: {RimLLMLog.SanitizeForLog(exception.Message, 300)}",
                exception);
        }

        protected override string DefaultTestModel => "gemini-3.5-flash";

        public override async Task<List<string>> FetchAvailableModelsAsync()
        {
            string sdkApiKey = Settings.GetActiveApiKey(ProviderId);
            try
            {
                using (var client = CreateGenAiClient(sdkApiKey))
#pragma warning disable S2325, S3260, S3267 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀
                {
                    var pager = await client.Models.ListAsync().ConfigureAwait(false);
                    var models = new List<string>();
                    await foreach (Model item in pager)
                    {
                        string name = item?.Name;
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }
                        models.Add(name.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                            ? name.Substring("models/".Length)
                            : name);
                    }
                    return models;
                }
            }
            catch (RimLLMException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw TranslateGoogleException(ex, "list models");
            }
        }

        /// <summary>
        /// 判斷模型用哪一種 thinkingConfig 表達方式。
        ///
        /// 兩份清單都是快速路徑而非白名單：認不出來的模型（例如未來的 gemini-5）
        /// 一律歸到 thinkingLevel —— 那是 Google 自 gemini-3 起的表達方式，也是往後的方向。
        /// 猜錯時服務端會以 400 拒絕，框架記下來重打一次即可（見 <see cref="RimLLMReasoningSupport"/>），
        /// 因此漏列的代價是一次重試，而不是設定永久靜默失效。
        /// </summary>
        private void DetermineGeminiThinkingConfig(string model, out bool isThinkingBudgetModel, out bool isThinkingLevelModel)
        {
            isThinkingBudgetModel = false;
            isThinkingLevelModel = false;
            if (model == null) return;

            isThinkingBudgetModel = model.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    model.IndexOf("gemini-2.5", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    model.IndexOf("gemini-2-5", StringComparison.OrdinalIgnoreCase) >= 0;

            isThinkingLevelModel = !isThinkingBudgetModel && !IsKnownNonThinkingGeminiModel(model);
        }

        /// <summary>
        /// 已知不具備思考能力的 Gemini 世代。對這些模型送 thinkingConfig 只會白白換來一次 400。
        /// </summary>
        private static bool IsKnownNonThinkingGeminiModel(string model)
        {
            return model.IndexOf("gemini-1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   model.IndexOf("gemini-2.0", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   model.IndexOf("gemini-2-0", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   model.IndexOf("embedding", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<string> GetOrCreateCachedContentAsync(string apiKey, string model, string cacheableContext, System.Threading.CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(cacheableContext)) return null;

            // 顯式快取需付「建立費 + 儲存費」，內容過小時這些成本會超過節省，因此低於最低門檻直接改走一般 systemInstruction。
            // Gemini 官方對 2.5 系列的最低可快取輸入量：Pro 約 2048 token、Flash / Flash-Lite 約 1024 token。
            // 以「字元數 < 最低 token 數」作為「必定不足」的保守下界（即使最密集的 CJK 也約為 1 token/字元），避免送出注定失敗的建立請求。
            int minCacheableTokens = (model != null && model.IndexOf("pro", StringComparison.OrdinalIgnoreCase) >= 0) ? 2048 : 1024;
            if (cacheableContext.Length < minCacheableTokens)
            {
                if (Settings.DetailedLogging)
                {
                    RimLLMLog.Message($"[RimLLM] Context too small for Gemini explicit cache ({cacheableContext.Length} chars < {minCacheableTokens}); using inline systemInstruction instead.");
                }
                return null;
            }

            string cacheKey = $"{model}\n{cacheableContext}";

            CleanupExpiredCaches();

            string existing = TryGetValidCachedId(cacheKey);
            if (existing != null) return existing;

            // 串行化同一 cacheKey 的建立流程，避免並發請求各自建立一份重複的快取資源
            var gate = _cacheCreationLocks.GetOrAdd(cacheKey, _ => new System.Threading.SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 雙重檢查：等待鎖期間可能已由其他請求建立完成
                existing = TryGetValidCachedId(cacheKey);
                if (existing != null) return existing;

                return await CreateCachedContentAsync(apiKey, model, cacheKey, cacheableContext, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 清理已過期的快取 entry 與其對應鎖，避免記憶體洩漏。
        /// </summary>
        private void CleanupExpiredCaches()
        {
            foreach (var kvp in _contextCaches)
            {
                if (kvp.Value.ExpireTime <= DateTime.UtcNow)
                {
                    _contextCaches.TryRemove(kvp.Key, out _);
                    _cacheCreationLocks.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// 取回未過期（含 10 秒安全緩衝）的快取 ID；查無或已逼近過期則回傳 null。
        /// </summary>
        private string TryGetValidCachedId(string cacheKey)
        {
            if (_contextCaches.TryGetValue(cacheKey, out var entry) &&
                entry.ExpireTime > DateTime.UtcNow.AddSeconds(10))
            {
                return entry.CacheId;
            }
            return null;
        }

        /// <summary>快取的預設存活時間。</summary>
        private const int CacheTtlSeconds = 300;

        private async Task<string> CreateCachedContentAsync(string apiKey, string model, string cacheKey, string cacheableContext, System.Threading.CancellationToken cancellationToken)
        {
            // Gemini 官方要求建立快取時 model 必須包含 models/ 前綴。
            string modelWithPrefix = model.StartsWith("models/") ? model : $"models/{model}";

            var config = new CreateCachedContentConfig
            {
                SystemInstruction = BuildTextContent(cacheableContext),
                Ttl = $"{CacheTtlSeconds}s"
            };

            try
            {
                CachedContent created = await CreateCachedContentNativeAsync(
                    apiKey, modelWithPrefix, config, cancellationToken).ConfigureAwait(false);

                string cacheId = created?.Name;
                if (!string.IsNullOrEmpty(cacheId))
                {
                    // SDK 直接給 DateTime?，不需要再解析字串；未回傳時退回本地推算的到期時間。
                    _contextCaches[cacheKey] = new GeminiCacheEntry
                    {
                        CacheId = cacheId,
                        ExpireTime = created.ExpireTime?.ToUniversalTime()
                            ?? DateTime.UtcNow.AddSeconds(CacheTtlSeconds)
                    };
                    return cacheId;
                }
            }
            catch (Exception ex)
            {
                // 記錄警告並 fallback。不拋出異常以防整體請求中斷。
                RimLLMLog.Warning($"[RimLLM] Failed to create Gemini Context Cache, fallback to normal call: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
            }

            return null;
        }
    }
#pragma warning restore S2325, S3260, S3267
}