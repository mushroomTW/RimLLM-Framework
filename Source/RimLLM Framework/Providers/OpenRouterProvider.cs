using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// OpenRouter 聚合供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class OpenRouterProvider : OpenAIProvider
    {
        public override string ProviderId => "OpenRouter";
        protected override string DefaultEndpoint => "https://openrouter.ai/api/v1";

        public OpenRouterProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        protected override JObject BuildPayload(LLMRequest request, string model, bool stream = false)
        {
            var payload = base.BuildPayload(request, model, stream);

            // 支援 OpenRouter 內置 Model Fallback
            // 如果 model 參數中包含逗號 (例如 "model-a,model-b")，則轉為 models 陣列傳遞給 OpenRouter
            if (model != null && model.Contains(","))
            {
                payload.Remove("model");
                var modelsArray = new JArray();
                foreach (var m in model.Split(new char[] { ',' }))
                {
                    string trimmed = m.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        modelsArray.Add(trimmed);
                    }
                }
                payload["models"] = modelsArray;
            }

            // 支援 OpenRouter 的 deepseek R1 思考強度設定 (max_thinking_tokens)
            if (request.ReasoningEffort != LLMReasoningEffort.Auto)
            {
                bool isR1 = (model != null && ((model.Contains("deepseek") && model.Contains("r1")) || model.Contains("reasoning")));
                if (isR1)
                {
                    int budget = 0;
                    if (request.ReasoningEffort == LLMReasoningEffort.Low) budget = 1024;
                    else if (request.ReasoningEffort == LLMReasoningEffort.Medium) budget = 2048;
                    else if (request.ReasoningEffort == LLMReasoningEffort.High) budget = 4096;
                    // None remains 0
                    
                    payload["max_thinking_tokens"] = budget;
                }
            }

            return payload;
        }

        protected override string DefaultTestModel => "google/gemini-2.5-flash";
    }
}
