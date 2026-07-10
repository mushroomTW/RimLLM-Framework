using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// DeepSeek API 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class DeepSeekProvider : OpenAIProvider
    {
        public DeepSeekProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.DeepSeek, "https://api.deepseek.com", "deepseek-v4-flash")
        {
        }
    }
}
