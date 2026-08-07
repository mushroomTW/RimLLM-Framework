using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Groq API 供應商，提供極速推理，完全相容 OpenAI API 格式。
    /// </summary>
    public class GroqProvider : OpenAIProvider
    {
        /// <summary>
        /// Groq 支援 OpenAI 相容的 response_format: json_schema。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => true;

        public GroqProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Groq, "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile")
        {
        }
    }
}
