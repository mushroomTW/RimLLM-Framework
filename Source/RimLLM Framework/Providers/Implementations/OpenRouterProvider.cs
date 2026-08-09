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

        /// <summary>
        /// OpenRouter 對所有供應商都以同一個 reasoning 物件表達思考強度。
        /// 只支援 token 預算的模型由服務端自行把 effort 換算成預算，Gemini 則換算成 thinkingLevel。
        /// </summary>
        protected override ReasoningWireFormat ReasoningFormat => ReasoningWireFormat.OpenRouterReasoning;

        public OpenRouterProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        /// <summary>
        /// OpenRouter 專屬 options 客製化：內置 Model Fallback ——
        /// model 含逗號時轉為 models 陣列（走 RawRepresentationFactory Patch）。
        /// 思考強度由基底類別依 <see cref="ReasoningFormat"/> 統一處理。
        /// </summary>
        protected override void BuildChatOptions(ChatOptions requestOptions, string model, ChatOptions options)
        {
            base.BuildChatOptions(requestOptions, model, options);

            if (model == null || !model.Contains(",")) return;

            // 以 null 清除 ModelId，讓 MEAI 的 PatchModelIfNotSet 跳過補寫 $.model，
            // 再由 Patch 完整掌控 model 相關欄位。
            options.ModelId = null;

            // 基底類別已經設過 factory（思考參數與 max_tokens 改寫），
            // 直接覆寫會把那些 Patch 一併弄丟，因此串接而非取代。
            Func<IChatClient, object> baseFactory = options.RawRepresentationFactory;
            options.RawRepresentationFactory = client =>
            {
                var chatCompletionOptions = baseFactory?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();
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
                return chatCompletionOptions;
            };
        }

        protected override string DefaultTestModel => "openrouter/free";
    }
}
