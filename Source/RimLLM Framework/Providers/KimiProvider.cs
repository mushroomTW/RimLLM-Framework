using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Kimi (月之暗面) 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class KimiProvider : OpenAIProvider
    {
        public KimiProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Kimi, "https://api.moonshot.ai/v1", "moonshot-v1-8k")
        {
        }
    }
}
