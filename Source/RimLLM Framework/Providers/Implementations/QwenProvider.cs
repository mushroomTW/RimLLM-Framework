
namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Qwen (通義千問) 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class QwenProvider : OpenAIProvider
    {
        /// <summary>
        /// Qwen API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        /// <summary>
        /// DashScope 相容模式以頂層 enable_thinking 開關思考，並以 thinking_budget 指定 token 預算。
        /// </summary>
        protected override ReasoningWireFormat ReasoningFormat => ReasoningWireFormat.EnableThinkingFlag;

        public QwenProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Qwen, "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-plus")
        {
        }
    }
}
