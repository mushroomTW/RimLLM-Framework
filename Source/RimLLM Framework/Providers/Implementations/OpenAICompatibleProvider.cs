using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace RimLLM_Framework.Providers;

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
        bool useMaxTokens = !RimLLMReasoningSupport.IsMaxTokensUnsupported(ProviderId, model);

        // 串接而非覆寫基底類別的 factory：直接覆寫會把思考參數的 Patch 一併丟掉。
        options.RawRepresentationFactory = OpenAIPatchExtensions.ChainPatch(
            options.RawRepresentationFactory,
            chatCompletionOptions =>
            {
                chatCompletionOptions.Patch.Remove(OpenAIPatchPaths.StreamOptions);
                if (useMaxTokens)
                {
                    chatCompletionOptions.Patch.Remove(OpenAIPatchPaths.MaxCompletionTokens);
                    chatCompletionOptions.Patch.Set(OpenAIPatchPaths.MaxTokens, maxTokens);
                }
            });
    }
}
