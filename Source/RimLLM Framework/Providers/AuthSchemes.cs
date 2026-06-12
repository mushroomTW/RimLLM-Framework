namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// HTTP 認證方案常數，供 BaseHttpProvider 與各供應商實作引用。
    /// </summary>
    public static class AuthSchemes
    {
        /// <summary>標準 Authorization: Bearer 認證。</summary>
        public const string Bearer = "Bearer";

        /// <summary>Anthropic 的 x-api-key 與版本 Header 認證。</summary>
        public const string Anthropic = "Anthropic";

        /// <summary>Gemini 採 URL query 認證，不加任何 Header。</summary>
        public const string Gemini = "Gemini";
    }
}
