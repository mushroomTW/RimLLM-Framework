using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    public class ZaiProvider : OpenAIProvider
    {
        /// <summary>
        /// Z.ai API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        public ZaiProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Zai, "https://api.z.ai/api/paas/v4", "glm-4.5-flash")
        {
        }
    }
}
