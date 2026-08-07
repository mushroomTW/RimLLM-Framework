using RimLLM_Framework.SDK;

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

        public QwenProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Qwen, "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-plus")
        {
        }
    }
}
