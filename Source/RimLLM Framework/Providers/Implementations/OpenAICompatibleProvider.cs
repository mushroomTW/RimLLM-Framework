using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using RimLLM_Framework.SDK;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// OpenAI 相容 API 供應商，用於自訂 Endpoint (如 Ollama, LM Studio, vLLM 等本地模型伺服器)。
    /// 繼承自 OpenAIProvider 以完全重用 OpenAI 通訊協定。
    /// </summary>
    public class OpenAICompatibleProvider : OpenAIProvider
    {
        public override string ProviderId => ProviderIds.OpenAICompatible;
        protected override string DefaultEndpoint => "http://localhost:1234/v1";

        /// <summary>
        /// 本地相容 API 通常不需要 API 金鑰。
        /// </summary>
        public override bool RequiresApiKey => false;

        /// <summary>
        /// 多數本地相容伺服器不支援 json_schema response_format，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        public OpenAICompatibleProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        /// <summary>
        /// 本地相容伺服器的 wire 差異補救：
        /// 移除 SDK 自動附加的 stream_options，並還原舊版 max_tokens 欄位。
        /// </summary>
        protected override void BuildChatOptions(ChatOptions requestOptions, string model, ChatOptions options)
        {
            base.BuildChatOptions(requestOptions, model, options);

            int maxTokens = requestOptions?.MaxOutputTokens ?? 1024;
            options.RawRepresentationFactory = _ =>
            {
                var chatCompletionOptions = new ChatCompletionOptions();
                chatCompletionOptions.Patch.Remove(Encoding.UTF8.GetBytes("$.stream_options"));
                chatCompletionOptions.Patch.Remove(Encoding.UTF8.GetBytes("$.max_completion_tokens"));
                chatCompletionOptions.Patch.Set(Encoding.UTF8.GetBytes("$.max_tokens"), maxTokens);
                return chatCompletionOptions;
            };
        }

        public override async Task<TestResult> TestConnectionAsync()
        {
            // 本地相容 API 通常不需要 API 金鑰，故此處放寬檢查，不強制要求 API Key 必須存在。
            var result = new TestResult { Provider = ProviderId };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "ping") };
                var options = new ChatOptions { MaxOutputTokens = 5 };
                // 預設測試模型使用 "default"
                string testModel = Settings.GetDefaultModel(ProviderId, "default");

                string content = await GenerateAsync(messages, options, testModel).ConfigureAwait(false);
                stopwatch.Stop();

                result.Success = true;
                result.Model = testModel;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }
            catch (RimLLMException ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ErrorCode = ex.Error;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ErrorCode = LLMError.Unknown;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }
    }
}
