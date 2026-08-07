namespace RimLLM_Framework.Manager
{
    /// <summary>manager 內部流程的回傳結果（文字 + 實際使用的 provider/model + 用量）。</summary>
    internal sealed class RimLLMGenerationResult
    {
        public string Text { get; set; }
        public string ProviderId { get; set; }
        public string ModelName { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int CachedPromptTokens { get; set; }
    }
}
