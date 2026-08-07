using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// NVIDIA API 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class NvidiaProvider : OpenAIProvider
    {
        /// <summary>
        /// NVIDIA API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        public NvidiaProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Nvidia, "https://integrate.api.nvidia.com/v1", "meta/llama-3.1-8b-instruct")
        {
        }
    }
}
