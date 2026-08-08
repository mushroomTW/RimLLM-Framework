using System;

namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// HTTP 狀態碼 → <see cref="LLMError"/> 的單一對照來源。
    /// 官方 SDK 路徑（ClientResultException）、raw HTTP 路徑與 embedding 路徑共用同一份語意，
    /// 避免相同的狀態碼判斷散落多處而逐漸走樣。
    /// </summary>
    public static class LLMErrorMapper
    {
        /// <summary>
        /// 依 HTTP 狀態碼建立對應的 <see cref="RimLLMException"/>。
        /// </summary>
        /// <param name="statusCode">HTTP 狀態碼；無法取得時傳入 null，會對應為 <see cref="LLMError.Unknown"/>。</param>
        /// <param name="friendlyMessage">已淨化、可直接顯示給使用者的錯誤訊息。</param>
        /// <param name="retryAfter">伺服器透過 Retry-After 建議的等待時間（若有）。</param>
        /// <param name="innerException">原始例外（若有）。</param>
        /// <param name="detectionText">
        /// 用於關鍵字偵測（配額不足、Schema 遭拒）的文字。
        /// 省略時沿用 <paramref name="friendlyMessage"/>；當原始回應比淨化後的訊息更完整時可另外傳入。
        /// </param>
        public static RimLLMException CreateException(
            int? statusCode,
            string friendlyMessage,
            TimeSpan? retryAfter = null,
            Exception innerException = null,
            string detectionText = null)
        {
            string probe = detectionText ?? friendlyMessage;

            switch (statusCode)
            {
                case 401:
                case 403:
                    return Create(LLMError.InvalidKey,
                        $"Invalid API key or authorization failed: {friendlyMessage}", null, innerException);

                case 402:
                    return Create(LLMError.QuotaExceeded,
                        $"Payment required, please check your account balance: {friendlyMessage}", null, innerException);

                case 404:
                    // 模型或端點不存在屬於不可重試錯誤，重試只會空耗延遲。
                    return Create(LLMError.ModelNotFound,
                        $"Model or endpoint not found: {friendlyMessage}", null, innerException);

                case 408:
                    return Create(LLMError.Timeout,
                        $"Request timed out on the server side: {friendlyMessage}", retryAfter, innerException);

                case 400:
                case 413:
                case 422:
                    // 請求本身有問題，以同一份 payload 重試必然再次失敗。
                    RimLLMException rejected = Create(LLMError.InvalidResponse,
                        $"The request was rejected by the provider: {friendlyMessage}", null, innerException);
                    rejected.IsSchemaRejection = LooksLikeSchemaRejection(probe);
                    return rejected;

                case 429:
                    return ContainsIgnoreCase(probe, "quota") || ContainsIgnoreCase(probe, "insufficient")
                        ? Create(LLMError.QuotaExceeded,
                            "API insufficient quota (insufficient_quota), please check your account balance.", retryAfter, innerException)
                        : Create(LLMError.RateLimit,
                            $"Rate limit triggered: {friendlyMessage}", retryAfter, innerException);
            }

            if (statusCode.HasValue && statusCode.Value >= 500)
            {
                return Create(LLMError.ProviderOffline,
                    $"Internal server error: {friendlyMessage}", retryAfter, innerException);
            }

            return Create(LLMError.Unknown, $"API request failed: {friendlyMessage}", null, innerException);
        }

        /// <summary>
        /// 判斷 4xx 錯誤訊息是否指向「服務端不接受原生 JSON Schema」，
        /// 供框架決定是否降級為提示式 JSON 重打一次。
        /// </summary>
        public static bool LooksLikeSchemaRejection(string message)
        {
            return ContainsIgnoreCase(message, "response_format") ||
                   ContainsIgnoreCase(message, "json_schema") ||
                   ContainsIgnoreCase(message, "schema");
        }

        public static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static RimLLMException Create(LLMError error, string message, TimeSpan? retryAfter, Exception innerException)
        {
            RimLLMException exception = innerException == null
                ? new RimLLMException(error, message)
                : new RimLLMException(error, message, innerException);
            exception.RetryAfter = retryAfter;
            return exception;
        }
    }
}
