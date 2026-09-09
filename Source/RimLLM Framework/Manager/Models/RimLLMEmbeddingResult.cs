namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，與其餘公開型別命名一致
    /// <summary>
    /// 單筆 embedding 的計算結果：向量本體，加上供應商回報的輸入 token 數。
    ///
    /// <see cref="InputTokenCount"/> 為 <c>null</c> 代表供應商沒有回報用量，
    /// 這與「0 token」是兩回事 —— 後者會讓呼叫端誤以為這次呼叫不耗配額。
    /// 目前經由 OpenAI 相容端點回報；供應商未回報時為 <c>null</c>。
    /// </summary>
    public class RimLLMEmbeddingResult
    {
        public float[] Vector { get; }

        public long? InputTokenCount { get; }

        public RimLLMEmbeddingResult(float[] vector, long? inputTokenCount)
        {
            Vector = vector;
            InputTokenCount = inputTokenCount;
        }
    }
#pragma warning restore S101
}
