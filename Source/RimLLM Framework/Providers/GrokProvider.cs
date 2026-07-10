using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Grok (xAI) API 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class GrokProvider : OpenAIProvider
    {
        public GrokProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Grok, "https://api.x.ai/v1", "grok-2-1212")
        {
        }
    }
}
