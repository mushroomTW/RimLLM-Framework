using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// OpenRouter 聚合供應商，完全相容 OpenAI API 格式。
    /// 支援 response_format: json_schema，因此沿用基底的原生 Schema 預設值。
    /// </summary>
    public class OpenRouterProvider : OpenAIProvider
    {
        public override string ProviderId => ProviderIds.OpenRouter;
        protected override string DefaultEndpoint => "https://openrouter.ai/api/v1";

        public OpenRouterProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        /// <summary>
        /// OpenRouter 專屬 options 客製化：
        /// 1) 內置 Model Fallback —— model 含逗號時轉為 models 陣列（走 RawRepresentationFactory Patch）；
        /// 2) deepseek R1 思考強度 —— 設定 max_thinking_tokens。
        /// </summary>
        protected override void BuildChatOptions(ChatOptions requestOptions, string model, ChatOptions options)
        {
            base.BuildChatOptions(requestOptions, model, options);

            bool splitModels = model != null && model.Contains(",");
            bool isR1 = model != null &&
                ((model.Contains("deepseek") && model.Contains("r1")) || model.Contains("reasoning"));
            bool needThinking = requestOptions?.Reasoning?.Effort != null && isR1;
            if (!splitModels && !needThinking) return;

            // 以 null 清除 ModelId，讓 MEAI 的 PatchModelIfNotSet 跳過補寫 $.model，
            // 再由 Patch 完整掌控 model 相關欄位。
            if (splitModels)
            {
                options.ModelId = null;
            }

            options.RawRepresentationFactory = _ =>
            {
                var chatCompletionOptions = new ChatCompletionOptions();
                if (splitModels)
                {
                    chatCompletionOptions.Patch.Remove(Encoding.UTF8.GetBytes("$.model"));
                    var modelsArray = new List<string>();
                    foreach (string m in model.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = m.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            modelsArray.Add(trimmed);
                        }
                    }
                    chatCompletionOptions.Patch.Set(Encoding.UTF8.GetBytes("$.models"), JsonSerializer.SerializeToUtf8Bytes(modelsArray));
                }
                if (needThinking)
                {
                    int budget = requestOptions.Reasoning.Effort switch
                    {
                        ReasoningEffort.Low => 1024,
                        ReasoningEffort.Medium => 2048,
                        ReasoningEffort.High => 4096,
                        _ => 0
                    };
                    chatCompletionOptions.Patch.Set(Encoding.UTF8.GetBytes("$.max_thinking_tokens"), budget);
                }
                return chatCompletionOptions;
            };
        }

        protected override string DefaultTestModel => "openrouter/free";
    }
}
