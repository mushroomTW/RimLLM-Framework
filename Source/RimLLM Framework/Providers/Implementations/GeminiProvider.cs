
namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Google Gemini API 供應商，經由官方 OpenAI 相容端點存取。
    /// 對話、串流、工具呼叫、結構化輸出、思考強度（<c>reasoning_effort</c>）與模型清單
    /// 全由基底 <see cref="OpenAIProvider"/> 經 OpenAI SDK 處理，不再使用原生 Google.GenAI 路徑。
    /// 既有的供應商識別碼、API 金鑰與模型設定沿用不變。
    /// </summary>
    public class GeminiProvider : OpenAIProvider
    {
        public GeminiProvider(IRimLLMSettings settings)
            : base(settings, ProviderIds.Gemini, "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.5-flash")
        {
        }
    }
}
