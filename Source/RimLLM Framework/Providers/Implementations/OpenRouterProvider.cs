using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

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
        /// 2) 思考強度 —— 送出 OpenRouter 的統一 reasoning 參數。
        ///
        /// 思考強度不再限定 deepseek R1：OpenRouter 對所有供應商都以同一個 reasoning 物件表達，
        /// 只支援 token 預算的模型由服務端自行把 effort 換算成預算，Gemini 則換算成 thinkingLevel。
        /// 先前送的是 max_thinking_tokens，那不是 OpenRouter 的欄位，等同沒有作用。
        /// </summary>
        protected override void BuildChatOptions(ChatOptions requestOptions, string model, ChatOptions options)
        {
            base.BuildChatOptions(requestOptions, model, options);

            bool splitModels = model != null && model.Contains(",");
            string reasoningEffort = ResolveReasoningEffort(requestOptions);
            if (!splitModels && reasoningEffort == null) return;

            // 以 null 清除 ModelId，讓 MEAI 的 PatchModelIfNotSet 跳過補寫 $.model，
            // 再由 Patch 完整掌控 model 相關欄位。
            if (splitModels)
            {
                options.ModelId = null;
            }

            // 基底類別可能已經設過 factory（例如非推理模型的 max_tokens 改寫），
            // 直接覆寫會把那些 Patch 一併弄丟，因此串接而非取代。
            Func<IChatClient, object> baseFactory = options.RawRepresentationFactory;
            options.RawRepresentationFactory = client =>
            {
                var chatCompletionOptions = baseFactory?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();
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
                if (reasoningEffort != null)
                {
                    // 兩種寫法擇一，否則服務端會看到互相矛盾的設定。
                    chatCompletionOptions.Patch.Remove(Encoding.UTF8.GetBytes("$.reasoning_effort"));
                    chatCompletionOptions.Patch.Set(
                        Encoding.UTF8.GetBytes("$.reasoning"),
                        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { { "effort", reasoningEffort } }));
                }
                return chatCompletionOptions;
            };
        }

        /// <summary>
        /// 把框架的思考強度換成 OpenRouter 的 reasoning.effort 字面值；未指定強度時回傳 null 代表不干預。
        /// 明確關閉思考對應 effort "none"。
        /// </summary>
        private static string ResolveReasoningEffort(ChatOptions requestOptions)
        {
            // 兩種來源都要認：呼叫端直接給 RimLLMChatOptions，或框架管線以 AdditionalProperties 轉遞。
            bool disableReasoning = false;
            if (requestOptions is RimLLMChatOptions rimOptions)
            {
                disableReasoning = rimOptions.DisableReasoning;
            }
            else if (requestOptions?.AdditionalProperties != null &&
                requestOptions.AdditionalProperties.TryGetValue("rimllm_disable_reasoning", out object disableValue) &&
                disableValue is bool disableFlag)
            {
                disableReasoning = disableFlag;
            }
            if (disableReasoning) return "none";

            ReasoningEffort? effort = requestOptions?.Reasoning?.Effort;
            if (effort == null) return null;
            if (effort == ReasoningEffort.Low) return "low";
            if (effort == ReasoningEffort.Medium) return "medium";
            if (effort == ReasoningEffort.High) return "high";
            return null;
        }

        protected override string DefaultTestModel => "openrouter/free";
    }
}
