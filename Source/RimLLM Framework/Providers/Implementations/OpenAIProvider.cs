using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Core;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// OpenAI API 供應商，支援 Chat Completion 與 SSE 串流。
    /// </summary>
    public class OpenAIProvider : BaseHttpProvider, IChatClientProvider, INativeStructuredOutputProvider, IChatOptionsCustomizer
    {
        private readonly string _providerId;
        private readonly string _defaultEndpoint;
        private readonly string _defaultTestModel;
        private readonly IOpenAIChatClientFactory _chatClientFactory;

        public override string ProviderId => _providerId;
        protected virtual string DefaultEndpoint => _defaultEndpoint;

        /// <summary>
        /// OpenAI 系列一律走官方 SDK + MEAI，不保留 raw HTTP 對話路徑。
        /// </summary>
        public bool UsesIChatClient => true;

        /// <summary>
        /// 衍生 provider 是否支援 OpenAI 相容的 <c>response_format: json_schema</c> 欄位。
        /// 預設為 true：OpenAI 官方支援 strict JSON Schema；不支援的服務端（Grok/Kimi/MiniMax/
        /// Nvidia/Qwen/Zai/OpenAICompatible）應覆寫為 false，讓框架改走提示式 JSON fallback。
        /// </summary>
        protected virtual bool SupportsNativeJsonSchemaPayload => true;

        public LLMProviderCapabilities Capabilities => new LLMProviderCapabilities
        {
            SupportsNativeStructuredOutput = SupportsNativeJsonSchemaPayload,
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

        public virtual IChatClient CreateChatClient(string model)
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            string endpoint = Settings.GetEndpoint(ProviderId, DefaultEndpoint);
            return _chatClientFactory.Create(apiKey, model, endpoint);
        }

        /// <summary>
        /// 供應商專屬 options 客製化鉤子：交由 <see cref="BuildChatOptions"/> 實作。
        /// </summary>
        public Action<ChatOptions> CreateChatOptionsCustomizer(ChatOptions options, string model)
        {
            return o => BuildChatOptions(options, model, o);
        }

        /// <summary>
        /// 依模型類型調整 MEAI ChatOptions：推理模型清空 temperature 並對應 reasoning effort，
        /// 其餘維持 executor 已設定的基礎選項。
        /// </summary>
        protected virtual void BuildChatOptions(ChatOptions requestOptions, string model, ChatOptions options)
        {
            if (requestOptions?.ResponseFormat != null)
            {
                options.ResponseFormat = requestOptions.ResponseFormat;
            }
            if (requestOptions?.AdditionalProperties != null)
            {
                if (requestOptions.AdditionalProperties.TryGetValue("strict", out object strictVal) && strictVal is bool strictBool)
                {
                    options.AdditionalProperties["strict"] = strictBool;
                }
                if (requestOptions.AdditionalProperties.TryGetValue("rimllm_response_schema", out object schemaVal) && schemaVal is string schemaJsonStr)
                {
                    using (System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(schemaJsonStr))
                    {
                        bool strict = !schemaJsonStr.Contains("additionalProperties\": true") && !schemaJsonStr.Contains("additionalProperties\":true");
                        if (requestOptions.AdditionalProperties.TryGetValue("strict", out object sObj) && sObj is bool sBool)
                        {
                            strict = sBool;
                        }
                        options.ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(
                            document.RootElement.Clone(),
                            "custom_type",
                            "RimLLM structured response");
                        options.AdditionalProperties["strict"] = strict;
                    }
                }
            }

            bool disableReasoning = false;
            if (requestOptions?.AdditionalProperties != null &&
                requestOptions.AdditionalProperties.TryGetValue("rimllm_disable_reasoning", out object val) &&
                val is bool b)
            {
                disableReasoning = b;
            }

            if (IsOpenAiReasoningModel(model))
            {
                options.Temperature = null;
                if (!disableReasoning && requestOptions?.Reasoning?.Effort != null)
                {
                    options.Reasoning = new ReasoningOptions
                    {
                        Effort = requestOptions.Reasoning.Effort
                    };
                }
            }
            else
            {
                options.Reasoning = null;
                if ((requestOptions?.MaxOutputTokens ?? 0) > 0)
                {
                    // 對齊 raw 路徑：非 reasoning 模型走 max_tokens（OpenAI SDK 預設一律
                    // 序列化為 max_completion_tokens），以 Patch 移除後改寫。
                    int maxTokens = requestOptions.MaxOutputTokens.Value;
                    options.RawRepresentationFactory = _ =>
                    {
                        var chatCompletionOptions = new ChatCompletionOptions();
                        chatCompletionOptions.Patch.Remove(Encoding.UTF8.GetBytes("$.reasoning_effort"));
                        chatCompletionOptions.Patch.Remove(Encoding.UTF8.GetBytes("$.max_completion_tokens"));
                        chatCompletionOptions.Patch.Set(Encoding.UTF8.GetBytes("$.max_tokens"), maxTokens);
                        return chatCompletionOptions;
                    };
                }
                else
                {
                    options.RawRepresentationFactory = _ =>
                    {
                        var chatCompletionOptions = new ChatCompletionOptions();
                        chatCompletionOptions.Patch.Remove(Encoding.UTF8.GetBytes("$.reasoning_effort"));
                        return chatCompletionOptions;
                    };
                }
            }
        }

        public Task<string> GenerateStructuredAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model)
        {
            return GenerateWithChatClientAsync(messages, options, model, true);
        }

        public override Task<string> GenerateAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model)
        {
            return GenerateWithChatClientAsync(messages, options, model, options?.ResponseFormat != null && Settings.EnableNativeSchema);
        }

        public override Task StreamAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, Action<string> onChunkReceived)
        {
            return StreamWithChatClientAsync(messages, options, model, onChunkReceived);
        }

        private async Task<string> GenerateWithChatClientAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, bool useNativeSchema)
        {
            // 供應商不支援原生 JSON Schema（如 Kimi/Grok）時，
            // 即使呼叫端要求 structured output 也改走提示式 JSON fallback，
            // 與 raw 路徑 BuildRequestPayloadAsync 的判斷一致。
            bool effectiveNativeSchema = useNativeSchema && SupportsNativeJsonSchemaPayload;

            RimLLMRequest translated = RimLLMChatClientExecutor.CreateFromChatOptions(messages, options, model);

            using (IChatClient client = CreateChatClient(model))
            {
                var result = await RimLLMChatClientExecutor.GenerateAsync(
                    client,
                    translated,
                    model,
                    effectiveNativeSchema,
                    ProviderId,
                    Settings?.ApiTimeout ?? 30f,
                    o => BuildChatOptions(options, model, o)).ConfigureAwait(false);
                return result.Text;
            }
        }

        private async Task StreamWithChatClientAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, Action<string> onChunkReceived)
        {
            RimLLMRequest translated = RimLLMChatClientExecutor.CreateFromChatOptions(messages, options, model);

            using (IChatClient client = CreateChatClient(model))
            {
                await RimLLMChatClientExecutor.StreamAsync(
                    client,
                    translated,
                    model,
                    options?.ResponseFormat != null && Settings.EnableNativeSchema,
                    ProviderId,
                    onChunkReceived,
                    Settings?.ApiTimeout ?? 30f,
                    o => BuildChatOptions(options, model, o)).ConfigureAwait(false);
            }
        }

        protected bool IsOpenAiReasoningModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            string name = modelName.Contains("/") ? modelName.Substring(modelName.LastIndexOf('/') + 1) : modelName;
            name = name.ToLowerInvariant();
            return name.StartsWith("o1") || name.StartsWith("o3");
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
