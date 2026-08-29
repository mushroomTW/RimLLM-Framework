namespace RimLLM_Framework.Manager
{
#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
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
#pragma warning restore S101, S2342
}