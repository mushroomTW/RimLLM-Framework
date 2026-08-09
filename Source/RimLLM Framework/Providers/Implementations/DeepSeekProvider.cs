
namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// DeepSeek API 供應商，完全相容 OpenAI API 格式。
    /// 支援 response_format: json_schema，因此沿用基底的原生 Schema 預設值。
    /// </summary>
    public class DeepSeekProvider : OpenAIProvider
    {
        /// <summary>
        /// DeepSeek 以 thinking.type 開關思考，並可同時附上 reasoning_effort 調整強度
        /// （官方範例即為兩者併送）。
        /// </summary>
        protected override ReasoningWireFormat ReasoningFormat => ReasoningWireFormat.ThinkingSwitch;

        public DeepSeekProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.DeepSeek, "https://api.deepseek.com", "deepseek-v4-flash")
        {
        }
    }
}
