using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.AI;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// OpenAI API 供應商，支援 Chat Completion 與 SSE 串流。
    /// </summary>
    public class OpenAIProvider : BaseHttpProvider, IChatClientProvider, INativeStructuredOutputProvider
    {
        private readonly string _providerId;
        private readonly string _defaultEndpoint;
        private readonly string _defaultTestModel;
        private readonly IOpenAIChatClientFactory _chatClientFactory;

        public override string ProviderId => _providerId;
        protected virtual string DefaultEndpoint => _defaultEndpoint;

        /// <summary>
        /// 只有內建 OpenAI provider 使用官方 SDK；衍生 provider 保留各自的 HTTP 格式。
        /// </summary>
        protected virtual bool UseChatClientAdapter => GetType() == typeof(OpenAIProvider);

        public bool UsesIChatClient => UseChatClientAdapter;

        /// <summary>
        /// 衍生 provider 是否確定支援 OpenAI 相容的 <c>response_format: json_schema</c> 欄位。
        /// 預設為 false：不支援的服務端收到此欄位會直接回 400，且會讓框架原本的
        /// 提示式 JSON fallback 失效。僅在已驗證支援的 provider 覆寫為 true。
        /// 此旗標只作用於原生 HTTP 路徑；走官方 SDK 的 OpenAIProvider 本體不受影響。
        /// </summary>
        protected virtual bool SupportsNativeJsonSchemaPayload => false;

        public LLMProviderCapabilities Capabilities => new LLMProviderCapabilities
        {
            SupportsNativeStructuredOutput = UsesIChatClient || SupportsNativeJsonSchemaPayload,
            SupportsStreaming = true,
            SupportsUsageMetadata = true
        };

        public OpenAIProvider(IRimLLMSettings settings)
            : this(settings, ProviderIds.OpenAI, "https://api.openai.com/v1/chat/completions", "gpt-4o-mini", new OpenAIChatClientFactory())
        {
        }

        protected OpenAIProvider(IRimLLMSettings settings, string providerId, string defaultEndpoint, string defaultTestModel)
            : this(settings, providerId, defaultEndpoint, defaultTestModel, new OpenAIChatClientFactory())
        {
        }

        protected OpenAIProvider(
            IRimLLMSettings settings,
            string providerId,
            string defaultEndpoint,
            string defaultTestModel,
            IOpenAIChatClientFactory chatClientFactory)
            : base(settings)
        {
            _providerId = providerId;
            _defaultEndpoint = defaultEndpoint;
            _defaultTestModel = defaultTestModel;
            _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        }

        public IChatClient CreateChatClient(string model)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);
            return _chatClientFactory.Create(apiKey, model, endpoint);
        }

        public Task<string> GenerateStructuredAsync(LLMRequest request, string model)
        {
            return GenerateWithChatClientAsync(request, model, true);
        }

        private async Task<string> GenerateWithChatClientAsync(LLMRequest request, string model, bool useNativeSchema)
        {
            using (IChatClient client = CreateChatClient(model))
            {
                return await RimLLMChatClientExecutor.GenerateAsync(
                    client,
                    request,
                    model,
                    useNativeSchema,
                    ProviderId,
                    Settings?.ApiTimeout ?? 30f).ConfigureAwait(false);
            }
        }

        private async Task StreamWithChatClientAsync(LLMRequest request, string model, Action<string> onChunkReceived)
        {
            using (IChatClient client = CreateChatClient(model))
            {
                await RimLLMChatClientExecutor.StreamAsync(
                    client,
                    request,
                    model,
                    request.ResponseType != null && Settings.EnableNativeSchema,
                    ProviderId,
                    onChunkReceived,
                    Settings?.ApiTimeout ?? 30f).ConfigureAwait(false);
            }
        }

        protected virtual JObject BuildPayload(LLMRequest request, string model, bool stream = false)
        {
            var messages = new JArray();
            string systemContext = request.GetEffectiveSystemPrompt();
            if (!string.IsNullOrEmpty(systemContext))
            {
                messages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = systemContext
                });
            }
            messages.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = request.Prompt
            });

            var payload = new JObject
            {
                ["model"] = model,
                ["messages"] = messages
            };

            if (request.ResponseType != null && Settings.EnableNativeSchema && SupportsNativeJsonSchemaPayload)
            {
                payload["response_format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JObject
                    {
                        ["name"] = "custom_type",
                        // strict 模式不允許 additionalProperties 為 schema 物件，
                        // 因此含 Dictionary 的型別必須關閉 strict，否則服務端會直接拒絕。
                        ["strict"] = !RimLLMJsonHelper.ContainsOpenEndedMap(request.ResponseType),
                        ["schema"] = RimLLMJsonHelper.GenerateJsonSchema(request.ResponseType, uppercaseTypes: false)
                    }
                };
            }

            if (IsOpenAiReasoningModel(model))
            {
                payload["max_completion_tokens"] = request.MaxTokens;
                if (request.ReasoningEffort != LLMReasoningEffort.None && request.ReasoningEffort != LLMReasoningEffort.Auto)
                {
                    payload["reasoning_effort"] = request.ReasoningEffort.ToString().ToLower();
                }
            }
            else
            {
                payload["temperature"] = request.Temperature;
                payload["max_tokens"] = request.MaxTokens;
            }

            if (stream)
            {
                payload["stream"] = true;
                if (SupportsStreamUsageOption)
                {
                    payload["stream_options"] = new JObject { ["include_usage"] = true };
                }
            }

            return payload;
        }

        /// <summary>
        /// 串流請求是否附帶 stream_options.include_usage（部分相容伺服器不支援）。
        /// </summary>
        protected virtual bool SupportsStreamUsageOption => true;

        protected bool IsOpenAiReasoningModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            string name = modelName.Contains("/") ? modelName.Substring(modelName.LastIndexOf('/') + 1) : modelName;
            name = name.ToLowerInvariant();
            return name.StartsWith("o1") || name.StartsWith("o3");
        }

        public override async Task<string> GenerateAsync(LLMRequest request, string model)
        {
            if (UseChatClientAdapter)
            {
                return await GenerateWithChatClientAsync(
                    request,
                    model,
                    request.ResponseType != null && Settings.EnableNativeSchema).ConfigureAwait(false);
            }

            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);
            if (!endpoint.EndsWith("/chat/completions"))
            {
                endpoint = endpoint.TrimEnd(new char[] { '/' }) + "/chat/completions";
            }

            var payload = BuildPayload(request, model, false);
            string responseJson = await SendPostAsync(endpoint, payload.ToString(), apiKey, cancellationToken: request.CancellationToken).ConfigureAwait(false);

            try
            {
                var responseObj = JObject.Parse(responseJson);

                // 檢查是否有 top-level error (即使 HTTP 狀態碼為 200，有些 Gateway 也可能在 JSON 中回傳錯誤)
                var errorObj = responseObj["error"];
                if (errorObj != null)
                {
                    string errMsg = errorObj["message"]?.ToString() ?? errorObj.ToString();
                    throw new RimLLMException(LLMError.InvalidResponse, $"API Error: {RimLLMLog.SanitizeForLog(errMsg, 300)}");
                }

                var message = responseObj["choices"]?[0]?["message"];
                if (message == null)
                {
                    throw new RimLLMException(LLMError.InvalidResponse, $"OpenAI response JSON is missing message field. Response preview: {RimLLMLog.SanitizeForLog(responseJson, 300)}");
                }
                var content = message["content"]?.ToString() ?? "";
                var reasoning = message["reasoning_content"]?.ToString();
                if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(reasoning))
                {
                    throw new RimLLMException(LLMError.InvalidResponse, $"OpenAI response message content is empty. Response preview: {RimLLMLog.SanitizeForLog(responseJson, 300)}");
                }
                // 記錄 Token 使用量
                var usage = responseObj["usage"];
                if (usage != null)
                {
                    int prompt = usage["prompt_tokens"]?.Value<int>() ?? 0;
                    int completion = usage["completion_tokens"]?.Value<int>() ?? 0;
                    int cached = usage["prompt_tokens_details"]?["cached_tokens"]?.Value<int>() ?? 0;
                    if (cached == 0)
                    {
                        cached = usage["cached_tokens"]?.Value<int>() ?? 0;
                    }
                    if (RimLLMProvider.Instance is RimLLMManager manager)
                    {
                        manager.RecordUsage(ProviderId, model, prompt, completion, cached);
                    }
                }

                if (!string.IsNullOrEmpty(reasoning))
                {
                    if (!string.IsNullOrEmpty(content))
                    {
                        return $"<think>\n{reasoning}\n</think>\n\n{content}";
                    }
                    return $"<think>\n{reasoning}\n</think>";
                }
                return content;
            }
            catch (Exception ex) when (!(ex is RimLLMException))
            {
                throw new RimLLMException(LLMError.InvalidResponse, $"Failed to parse OpenAI response: {RimLLMLog.SanitizeForLog(ex.Message, 200)}. Response preview: {RimLLMLog.SanitizeForLog(responseJson, 300)}", ex);
            }
        }

        public override async Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived)
        {
            if (UseChatClientAdapter)
            {
                await StreamWithChatClientAsync(request, model, onChunkReceived).ConfigureAwait(false);
                return;
            }

            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);
            if (!endpoint.EndsWith("/chat/completions"))
            {
                endpoint = endpoint.TrimEnd(new char[] { '/' }) + "/chat/completions";
            }

            var payload = BuildPayload(request, model, true);

            float timeoutSeconds = Settings?.ApiTimeout ?? 30f;
            float streamTimeout = Math.Max(timeoutSeconds * 2f, 120f); // 串流給予寬鬆的超時保護

            using (var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(streamTimeout)))
            using (var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, request.CancellationToken))
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                httpRequest.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                HttpResponseMessage response = null;
                try
                {
                    response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        ThrowHttpError(response, responseBody);
                    }
                }
                catch (RimLLMException)
                {
                    response?.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    response?.Dispose();
                    throw ConvertStreamTransportException("OpenAI", ex, request.CancellationToken);
                }

                using (response)
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream))
                {
                    bool inReasoning = false;
                    int totalCompletionChars = 0;
                    int finalPromptTokens = 0;
                    int finalCompletionTokens = 0;
                    int finalCachedTokens = 0;
                    bool hasUsage = false;
                    bool producedText = false;
                    int malformedFrames = 0;

                    while (!reader.EndOfStream)
                    {
                        if (cts.Token.IsCancellationRequested)
                        {
                            ThrowIfStreamTimedOut(cts.Token, request.CancellationToken);
                        }

                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) continue;
                        line = line.Trim();

                        // 部分 OpenAI 相容服務端不會在 data: 後加空白，因此放寬比對。
                        if (line.StartsWith("data:") && line.Substring(5).Trim() == "[DONE]")
                            break;

                        if (line.StartsWith("data: "))
                        {
                            string json = line.Substring(6);
                            string content = null;
                            string reasoning = null;
                            try
                            {
                                var token = JObject.Parse(json);
                                content = token["choices"]?[0]?["delta"]?["content"]?.ToString();
                                reasoning = token["choices"]?[0]?["delta"]?["reasoning_content"]?.ToString();
                                
                                var usageObj = token["usage"];
                                if (usageObj != null)
                                {
                                    finalPromptTokens = usageObj["prompt_tokens"]?.Value<int>() ?? 0;
                                    finalCompletionTokens = usageObj["completion_tokens"]?.Value<int>() ?? 0;
                                    finalCachedTokens = usageObj["prompt_tokens_details"]?["cached_tokens"]?.Value<int>() ?? 0;
                                    if (finalCachedTokens == 0)
                                    {
                                        finalCachedTokens = usageObj["cached_tokens"]?.Value<int>() ?? 0;
                                    }
                                    hasUsage = true;
                                }
                            }
                            catch
                            {
                                // 損毀或心跳包等非 JSON 片段：不中斷串流，但計數以利診斷。
                                malformedFrames++;
                                if (Settings != null && Settings.DetailedLogging)
                                {
                                    RimLLMLog.Message($"[RimLLM] {ProviderId} SSE 封包解析失敗: {RimLLMLog.SanitizeForLog(line, 200)}");
                                }
                            }

                            if (!string.IsNullOrEmpty(reasoning))
                            {
                                producedText = true;
                                totalCompletionChars += reasoning.Length;
                                if (!inReasoning)
                                {
                                    inReasoning = true;
                                    onChunkReceived?.Invoke("<think>");
                                }
                                onChunkReceived?.Invoke(reasoning);
                            }

                            if (!string.IsNullOrEmpty(content))
                            {
                                producedText = true;
                                totalCompletionChars += content.Length;
                                if (inReasoning)
                                {
                                    inReasoning = false;
                                    onChunkReceived?.Invoke("</think>");
                                }
                                onChunkReceived?.Invoke(content);
                            }
                        }
                    }
                    if (inReasoning)
                    {
                        onChunkReceived?.Invoke("</think>");
                    }

                    // 零輸出的串流不得視為成功，否則會阻擋 fallback 並讓呼叫端收到空字串。
                    // 選用 NetworkError 而非 InvalidResponse：這種情況幾乎都是連線被中斷，屬可重試。
                    if (!producedText)
                    {
                        throw new RimLLMException(
                            LLMError.NetworkError,
                            $"{ProviderId} 串流未回傳任何內容（損毀封包 {malformedFrames} 筆）。");
                    }

                    if (RimLLMProvider.Instance is RimLLMManager manager)
                    {
                        if (hasUsage)
                        {
                            manager.RecordUsage(ProviderId, model, finalPromptTokens, finalCompletionTokens, finalCachedTokens);
                        }
                        else
                        {
                            int systemLen = request.GetEffectiveSystemPrompt()?.Length ?? 0;
                            int promptLen = request.Prompt?.Length ?? 0;
                            int estPrompt = (int)((systemLen + promptLen) * 0.8f);
                            int estCompletion = (int)(totalCompletionChars * 0.8f);
                            manager.RecordUsage(ProviderId, model, Math.Max(1, estPrompt), Math.Max(1, estCompletion));
                        }
                    }
                }
            }
        }

        protected override string DefaultTestModel => _defaultTestModel;

        public override async Task<List<string>> FetchAvailableModelsAsync()
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);

            string url = endpoint;
            if (url.EndsWith("/chat/completions"))
            {
                url = url.Replace("/chat/completions", "/models");
            }
            else if (!url.EndsWith("/models"))
            {
                url = url.TrimEnd(new char[] { '/' }) + "/models";
            }

            string responseJson = await SendGetAsync(url, apiKey).ConfigureAwait(false);
            var list = new List<string>();
            try
            {
                var obj = JObject.Parse(responseJson);
                var data = obj["data"] as JArray;
                if (data != null)
                {
                    foreach (var item in data)
                    {
                        string id = item["id"]?.ToString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            list.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new RimLLMException(LLMError.InvalidResponse, $"Failed to fetch {ProviderId} models list: {RimLLMLog.SanitizeForLog(ex.Message, 200)}", ex);
            }
            return list;
        }
    }
}
