using RimLLM_Framework.Manager;

namespace RimLLM_Framework
{
    /// <summary>
    /// 描述 LLM 供應商可使用的原生功能，讓共用服務層不必依賴 provider-specific SDK。
    /// </summary>
    public sealed class LLMProviderCapabilities
    {
        /// <summary>是否支援由服務端原生驗證結構化輸出 Schema。</summary>
        public bool SupportsNativeStructuredOutput { get; set; }

        /// <summary>
        /// 結構化輸出的 JSON Schema 方言。
        /// 預設為 OpenAI（選填成員以聯集型別表達）；Gemini 只接受單一 <c>type</c> 加 <c>nullable</c>。
        /// 第三方供應商可據此宣告自己的方言，不必修改框架。
        /// </summary>
        public RimLLMSchemaProfile PreferredSchemaProfile { get; set; } = RimLLMSchemaProfile.OpenAI;

        /// <summary>是否支援串流輸出。</summary>
        public bool SupportsStreaming { get; set; }

        /// <summary>是否會回傳可用的 Token 使用量 metadata。</summary>
        public bool SupportsUsageMetadata { get; set; }
    }
}
