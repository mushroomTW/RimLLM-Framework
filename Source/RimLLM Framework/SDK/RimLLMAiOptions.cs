namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// 建立 MEAI Chat Client 時使用的共用選項。
    /// API 金鑰只應由設定服務提供，禁止寫入程式碼或一般日誌。
    /// </summary>
    public sealed class RimLLMAiOptions
    {
        public string ProviderId { get; set; }
        public string ModelId { get; set; }
        public string ApiKey { get; set; }
        public string Endpoint { get; set; }
        public bool EnableNativeStructuredOutput { get; set; } = true;
        public float Temperature { get; set; } = 0.7f;
        public int MaxOutputTokens { get; set; } = 1024;
    }
}
