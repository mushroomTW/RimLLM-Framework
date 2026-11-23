using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace RimLLM_Framework.Providers;

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

    /// <summary>多模型備援（逗號分隔）靠 Chat Completions 的 models 陣列，留在原路徑。</summary>
    internal override bool PrefersResponsesApi(string model) => !model.Contains(',', StringComparison.Ordinal);

    internal override bool ReplaysChatReasoning => true;

    public OpenRouterProvider(IRimLLMSettings settings) : base(settings)
    {
    }

    /// <summary>
    /// OpenRouter 專屬 options 客製化：內置 Model Fallback ——
    /// model 含逗號時轉為 models 陣列（走 RawRepresentationFactory Patch）。
    /// </summary>
    protected override void BuildChatOptions(ChatOptions? requestOptions, string model, ChatOptions options)
    {
        base.BuildChatOptions(requestOptions, model, options);

        if (model == null || !model.Contains(',', StringComparison.Ordinal)) return;

        // 以 null 清除 ModelId，讓 MEAI 的 PatchModelIfNotSet 跳過補寫 $.model，
        // 再由 Patch 完整掌控 model 相關欄位。
        options.ModelId = null;

        options.RawRepresentationFactory = OpenAIPatchExtensions.ChainPatch(
            options.RawRepresentationFactory,
            chatCompletionOptions =>
            {
                chatCompletionOptions.Patch.Remove(OpenAIPatchPaths.Model);
                var modelsArray = new List<string>();
                foreach (string m in model.Split([','], StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = m.Trim();
                    if (trimmed.Length > 0) modelsArray.Add(trimmed);
                }
                chatCompletionOptions.Patch.Set(OpenAIPatchPaths.Models, JsonSerializer.SerializeToUtf8Bytes(modelsArray));
            });
    }
}
