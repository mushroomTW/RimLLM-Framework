using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Groq API 供應商，提供極速推理，完全相容 OpenAI API 格式。
    /// 支援 response_format: json_schema，因此沿用基底的原生 Schema 預設值。
    /// </summary>
    public class GroqProvider : OpenAIProvider
    {
        public GroqProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Groq, "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile")
        {
        }
    }
}
