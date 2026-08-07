extern alias bclasync;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Google Gemini API 供應商，支援 generateContent 與 streamGenerateContent。
    /// </summary>
    public class GeminiProvider : BaseHttpProvider, IChatClientProvider, INativeStructuredOutputProvider
    {
        public override string ProviderId => ProviderIds.Gemini;

        private class GeminiCacheEntry
        {
            public string CacheId { get; set; }
            public DateTime ExpireTime { get; set; }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GeminiCacheEntry> _contextCaches =
            new System.Collections.Concurrent.ConcurrentDictionary<string, GeminiCacheEntry>();

        // 對同一 cacheKey 的快取建立流程加鎖，避免並發時重複建立資源（重複付建立費）。
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> _cacheCreationLocks =
            new System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim>();

        private readonly IGeminiChatClientFactory _chatClientFactory;

        /// <summary>
        /// Gemini 原生安全設定。維持空集合時使用 Gemini API 預設安全策略。
        /// 這是 provider-specific 設定，不會洩漏到共用 SDK facade。
        /// </summary>
        public IList<SafetySetting> SafetySettings { get; } = new List<SafetySetting>();

        /// <summary>
        /// Gemini 一律走官方 Google.GenAI SDK，不保留 raw HTTP 對話路徑。
        /// </summary>
        public bool UsesIChatClient => true;

        public LLMProviderCapabilities Capabilities => new LLMProviderCapabilities
        {
            SupportsNativeStructuredOutput = true,
            SupportsStreaming = true,
            SupportsUsageMetadata = true
        };

        public GeminiProvider(IRimLLMSettings settings) : this(settings, new GeminiChatClientFactory())
        {
        }

        protected GeminiProvider(IRimLLMSettings settings, IGeminiChatClientFactory chatClientFactory) : base(settings)
        {
            _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        }

        public IChatClient CreateChatClient(string model)
        {
            return _chatClientFactory.Create(Settings.GetActiveApiKey(ProviderId), model);
        }

        public Task<string> GenerateStructuredAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model)
        {
            return GenerateWithGoogleGenAiAsync(messages, options, model);
        }

        public override Task<string> GenerateAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model)
        {
            return GenerateWithGoogleGenAiAsync(messages, options, model);
        }

        public override Task StreamAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, Action<string> onChunkReceived)
        {
            return StreamWithGoogleGenAiAsync(messages, options, model, onChunkReceived);
        }

        /// <summary>建立 Google.GenAI 用戶端（測試縫）。</summary>
        protected virtual Client CreateGenAiClient(string apiKey)
        {
            return new Client(apiKey: apiKey);
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

                    if (hasUsage)
                    {
                        RimLLMProvider.Manager.RecordUsage(ProviderId, model, promptTokens, completionTokens, cachedTokens);
                    }
                    else
                    {
                        int promptChars = 0;
                        if (messages != null)
                        {
                            foreach (var m in messages)
                            {
                                if (m != null && !string.IsNullOrEmpty(m.Text)) promptChars += m.Text.Length;
                            }
                        }
                        RimLLMProvider.Manager.RecordUsage(
                            ProviderId,
                            model,
                            Math.Max(1, (int)(promptChars * 0.8f)),
                            Math.Max(1, (int)(completionChars * 0.8f)));
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

        private async Task<GenerateContentConfig> BuildNativeConfigAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options,
            string model,
            string apiKey)
        {
            bool disableReasoning = false;
            if (options is RimLLMChatOptions rimOptsDR)
            {
                disableReasoning = rimOptsDR.DisableReasoning;
            }
            else if (options?.AdditionalProperties != null &&
                options.AdditionalProperties.TryGetValue("rimllm_disable_reasoning", out object dr) &&
                dr is bool drBool)
            {
                disableReasoning = drBool;
            }

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

            string systemContext = null;
            string ccStr = (options as RimLLMChatOptions)?.CachedContext;
            if (string.IsNullOrEmpty(ccStr) &&
                options?.AdditionalProperties != null &&
                options.AdditionalProperties.TryGetValue("rimllm_cached_context", out object cc) &&
                cc is string ccVal)
            {
                ccStr = ccVal;
            }

            if (!string.IsNullOrEmpty(ccStr))
            {
                if (!string.IsNullOrEmpty(systemPromptMsg) && systemPromptMsg != ccStr)
                {
                    systemContext = systemPromptMsg + "\n\n" + ccStr;
                }
                else
                {
                    systemContext = ccStr;
                }
            }
            else
            {
                systemContext = systemPromptMsg;
            }

            bool enableContextCaching = (options as RimLLMChatOptions)?.EnableContextCaching ?? false;
            if (!enableContextCaching &&
                options?.AdditionalProperties != null &&
                options.AdditionalProperties.TryGetValue("rimllm_enable_context_caching", out object ec) &&
                ec is bool ecBool)
            {
                enableContextCaching = ecBool;
            }

            string baseEndpoint = Settings.GetEndpoint(ProviderId, "https://generativelanguage.googleapis.com/v1beta");
            string cacheId = null;
            if (enableContextCaching && !string.IsNullOrEmpty(systemContext))
            {
                cacheId = await GetOrCreateCachedContentAsync(
                    apiKey,
                    baseEndpoint,
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

            string schemaJson = null;
            if (options?.AdditionalProperties != null &&
                options.AdditionalProperties.TryGetValue("rimllm_response_schema", out object rs) &&
                rs is string rsStr)
            {
                schemaJson = rsStr;
            }
            else if (options?.ResponseFormat is ChatResponseFormatJson jsonFormat)
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

        private ThinkingConfig BuildNativeThinkingConfig(string model, ReasoningEffort? effort, bool disableReasoning)
        {
            if (string.IsNullOrEmpty(model))
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
            bool isThought = part.Thought == true;
            if (isThought)
            {
                if (!hasFinishedReasoning)
                {
                    if (!inReasoning)
                    {
                        inReasoning = true;
                        emit("<think>\n");
                    }
                    emit(part.Text);
                }
                else
                {
                    emit(part.Text);
                }
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

        private static Exception TranslateGoogleException(Exception exception, string operation)
        {
            if (exception is OperationCanceledException)
            {
                return new RimLLMException(LLMError.Cancelled, $"Gemini {operation} was cancelled.", exception);
            }
            if (exception is ClientError clientError)
            {
                LLMError error = clientError.StatusCode == 429
                    ? LLMError.RateLimit
                    : clientError.StatusCode == 401 || clientError.StatusCode == 403
                        ? LLMError.InvalidKey
                        : clientError.StatusCode == 404
                            ? LLMError.ModelNotFound
                            : LLMError.InvalidResponse;
                return new RimLLMException(
                    error,
                    $"Gemini {operation} failed ({clientError.StatusCode}): {RimLLMLog.SanitizeForLog(clientError.Message, 300)}",
                    exception);
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

        private void DetermineGeminiThinkingConfig(string model, out bool isThinkingBudgetModel, out bool isThinkingLevelModel)
        {
            isThinkingBudgetModel = false;
            isThinkingLevelModel = false;
            if (model == null) return;
 
            isThinkingBudgetModel = model.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                    model.IndexOf("gemini-2.5", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    model.IndexOf("gemini-2-5", StringComparison.OrdinalIgnoreCase) >= 0;
 
            isThinkingLevelModel = model.IndexOf("gemma-4", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                   model.IndexOf("gemini-3", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                   model.IndexOf("gemini-4", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<string> GetOrCreateCachedContentAsync(string apiKey, string baseEndpoint, string model, string cacheableContext, System.Threading.CancellationToken cancellationToken)
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

                return await CreateCachedContentAsync(apiKey, baseEndpoint, model, cacheKey, cacheableContext, cancellationToken).ConfigureAwait(false);
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

        private async Task<string> CreateCachedContentAsync(string apiKey, string baseEndpoint, string model, string cacheKey, string cacheableContext, System.Threading.CancellationToken cancellationToken)
        {
            // 建立新的 Cached Content 資源
            // API url 格式: POST https://generativelanguage.googleapis.com/v1beta/cachedContents（金鑰走 x-goog-api-key Header）
            string cacheUrl = $"{baseEndpoint.TrimEnd(new char[] { '/' })}/cachedContents";

            // 剥離 model 中的 "models/" 前綴以對齊格式 (Gemini 官方要求建立快取時 model 必須包含 models/ 前綴)
            string modelWithPrefix = model.StartsWith("models/") ? model : $"models/{model}";

            var cachePayload = new JObject
            {
                ["model"] = modelWithPrefix,
                ["systemInstruction"] = new JObject
                {
                    ["parts"] = new JArray
                    {
                        new JObject { ["text"] = cacheableContext }
                    }
                },
                ["ttl"] = "300s" // 預設保留 5 分鐘
            };

            try
            {
                string cacheResponseJson = await SendPostAsync(cacheUrl, cachePayload.ToString(), apiKey, AuthSchemes.Gemini, cancellationToken).ConfigureAwait(false);
                var cacheObj = JObject.Parse(cacheResponseJson);
                string cacheId = cacheObj["name"]?.ToString();
                string expireTimeStr = cacheObj["expireTime"]?.ToString();

                if (!string.IsNullOrEmpty(cacheId))
                {
                    DateTime expireTime = DateTime.UtcNow.AddSeconds(300); // 預設 300 秒
                    if (DateTime.TryParse(expireTimeStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsedTime))
                    {
                        expireTime = parsedTime;
                    }

                    var newEntry = new GeminiCacheEntry
                    {
                        CacheId = cacheId,
                        ExpireTime = expireTime
                    };
                    _contextCaches[cacheKey] = newEntry;
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
}
