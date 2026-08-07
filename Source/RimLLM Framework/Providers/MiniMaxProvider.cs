using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// MiniMax 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class MiniMaxProvider : OpenAIProvider
    {
        /// <summary>
        /// MiniMax API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        public MiniMaxProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.MiniMax, "https://api.minimax.io/v1", "MiniMax-M3")
        {
        }
    }
}
