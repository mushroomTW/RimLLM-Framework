
namespace RimLLM_Framework.Providers
{
    public class ZaiProvider : OpenAIProvider
    {
        /// <summary>
        /// Z.ai API 不確定支援 strict JSON Schema，改走提示式 JSON fallback。
        /// </summary>
        protected override bool SupportsNativeJsonSchemaPayload => false;

        /// <summary>
        /// GLM 以 thinking.type 開關思考；reasoning_effort 只有部分新模型支援，
        /// 不支援的模型會由服務端忽略或以 400 拒絕，後者交給框架的降級記憶處理。
        /// </summary>
        protected override ReasoningWireFormat ReasoningFormat => ReasoningWireFormat.ThinkingSwitch;

        public ZaiProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Zai, "https://api.z.ai/api/paas/v4", "glm-4.5-flash")
        {
        }
    }
}
