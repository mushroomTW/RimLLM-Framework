using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    public class ZaiProvider : OpenAIProvider
    {
        public ZaiProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Zai, "https://api.z.ai/api/paas/v4", "glm-4.5-flash")
        {
        }
    }
}
