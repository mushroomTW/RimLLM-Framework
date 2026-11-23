using System;
using System.ClientModel;
using System.Net.Http;
using System.Net.Http.Headers;

namespace RimLLM_Framework;

/// <summary>
/// HTTP 狀態碼 → <see cref="LLMError"/> 的單一對照來源。
/// </summary>
public static class LLMErrorMapper
{
    /// <summary>
    /// 解析 Retry-After 標頭值，支援 RFC 7231 的「延遲秒數」與「HTTP 日期」兩種格式。
    /// 無法解析或已過期時回傳 null。
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        return RetryConditionHeaderValue.TryParse(headerValue, out RetryConditionHeaderValue? parsed)
            ? ToDelay(parsed)
            : null;
    }

    private static TimeSpan? ToDelay(RetryConditionHeaderValue retryAfter)
    {
        if (retryAfter == null) return null;

        if (retryAfter.Delta.HasValue)
        {
            return retryAfter.Delta.Value > TimeSpan.Zero ? retryAfter.Delta : null;
        }
        if (retryAfter.Date.HasValue)
        {
            TimeSpan delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? (TimeSpan?)delta : null;
        }
        return null;
    }

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
        Exception? innerException = null,
        string? detectionText = null)
    {
        RimLLMException exception = MapStatusCode(statusCode, friendlyMessage, retryAfter, innerException, detectionText);
        exception.HttpStatusCode = statusCode;
        return exception;
    }

    /// <summary>
    /// 將官方 SDK 的 <see cref="ClientResultException"/> 對照為 <see cref="RimLLMException"/>。
    /// SDK 的 Message 只有狀態碼，服務端說明原因的內文與 Retry-After 都在原始回應裡，
    /// 因此一併讀出：內文供顯示與關鍵字偵測（配額、Schema、思考參數降級），Retry-After 供重試排程。
    /// </summary>
    /// <param name="exception">SDK 拋出的例外。</param>
    /// <param name="messagePrefix">附加在顯示訊息前的來源說明（例如 "Embedding API: "）。</param>
    internal static RimLLMException FromClientResultException(ClientResultException exception, string? messagePrefix = null)
    {
        string body = ReadResponseBody(exception);
        string fullMessage = string.IsNullOrEmpty(body) ? exception.Message : $"{exception.Message} {body}";

        // 顯示用訊息要淨化與截斷；關鍵字偵測仍看完整內容，避免關鍵字被切掉。
        return CreateException(
            exception.Status,
            messagePrefix + Core.RimLLMLog.SanitizeForLog(fullMessage, 300),
            ReadRetryAfter(exception),
            exception,
            detectionText: fullMessage);
    }

    private static string ReadResponseBody(ClientResultException exception)
    {
        try
        {
            string? body = exception.GetRawResponse()?.Content?.ToString();
            return string.IsNullOrWhiteSpace(body) ? string.Empty : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TimeSpan? ReadRetryAfter(ClientResultException exception)
    {
        try
        {
            return exception.GetRawResponse()?.Headers.TryGetValue("Retry-After", out string? value) == true
                ? ParseRetryAfter(value)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static RimLLMException MapStatusCode(
        int? statusCode,
        string friendlyMessage,
        TimeSpan? retryAfter,
        Exception? innerException,
        string? detectionText)
    {
        string probe = detectionText ?? friendlyMessage;

        switch (statusCode)
        {
            case 0:
                // System.ClientModel 在沒收到任何回應時（連線被拒、DNS 失敗、連線中斷）
                // 擲出 Status 為 0 的 ClientResultException，內層才是 HttpRequestException。
                // 這是網路層失敗，歸成 Unknown 會讓 Embedding／決策備援不換家、設定精靈也顯示不出「離線」。
                return Create(LLMError.NetworkError,
                    $"Could not reach the provider: {friendlyMessage}", retryAfter: null, innerException);

            case 401:
            case 403:
                return Create(LLMError.InvalidKey,
                    $"Invalid API key or authorization failed: {friendlyMessage}", retryAfter: null, innerException);

            case 402:
            {
                RimLLMException payment = Create(LLMError.QuotaExceeded,
                    $"Payment required, please check your account balance: {friendlyMessage}", retryAfter: null, innerException);
                payment.IsBalanceExhausted = true;
                return payment;
            }

            case 404:
                return Create(LLMError.ModelNotFound,
                    $"Model or endpoint not found: {friendlyMessage}", retryAfter: null, innerException);

            case 408:
                return Create(LLMError.Timeout,
                    $"Request timed out on the server side: {friendlyMessage}", retryAfter, innerException);

            case 400:
            case 413:
            case 422:
                return MapRejection(statusCode, probe, friendlyMessage, innerException);

            case 429:
                return MapTooManyRequests(probe, friendlyMessage, retryAfter, innerException);
        }

        if (statusCode.HasValue && statusCode.Value >= 500)
        {
            return Create(LLMError.ProviderOffline,
                $"Internal server error: {friendlyMessage}", retryAfter, innerException);
        }

        return Create(LLMError.Unknown, $"API request failed: {friendlyMessage}", retryAfter: null, innerException);
    }

    private static RimLLMException MapRejection(int? statusCode, string probe, string friendlyMessage, Exception? innerException)
    {
        RimLLMException rejected = Create(ResolveRejectionError(statusCode, probe),
            $"The request was rejected by the provider: {friendlyMessage}", retryAfter: null, innerException);
        rejected.IsSchemaRejection = LooksLikeSchemaRejection(probe);
        rejected.IsReasoningRejection = LooksLikeReasoningRejection(probe);
        rejected.IsTemperatureRejection = LooksLikeTemperatureRejection(probe);
        rejected.IsMaxTokensRejection = LooksLikeMaxTokensRejection(probe);
        return rejected;
    }

    private static RimLLMException MapTooManyRequests(string probe, string friendlyMessage, TimeSpan? retryAfter, Exception? innerException)
    {
        if (ContainsIgnoreCase(probe, "quota") || ContainsIgnoreCase(probe, "insufficient"))
        {
            // 訊息只提到 "quota" 的多半是每分鐘配額（Gemini 免費層），等一下就恢復；
            // 明確寫 insufficient 的才是額度用完。
            bool exhausted = ContainsIgnoreCase(probe, "insufficient");
            string message = exhausted
                ? $"Account quota exhausted (insufficient_quota), please check your account balance: {friendlyMessage}"
                : $"Provider quota or rate limit reached, try again later: {friendlyMessage}";
            RimLLMException quota = Create(LLMError.QuotaExceeded, message, retryAfter, innerException);
            quota.IsBalanceExhausted = exhausted;
            return quota;
        }
        return Create(LLMError.RateLimit,
            $"Rate limit triggered: {friendlyMessage}", retryAfter, innerException);
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「服務端不接受原生 JSON Schema」，
    /// 供框架決定是否降級為提示式 JSON 重打一次。
    /// </summary>
    public static bool LooksLikeSchemaRejection(string message)
    {
        return ContainsIgnoreCase(message, "response_format") ||
               ContainsIgnoreCase(message, "schema");
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「服務端不接受思考相關參數」。
    /// 各家的欄位名不同（reasoning_effort、reasoning、thinking、enable_thinking、thinking_budget），
    /// 而且支援範圍細到模型層級，因此以服務端回報的欄位名判定，再由框架去掉參數重打一次。
    /// </summary>
    /// <remarks>
    /// 續傳類錯誤（要求送回 reasoning_content／thought_signature、加密推理無效、改用 /v1/responses）也會提到推理，
    /// 但問題不在思考參數；誤判會讓模型整個 session 收不到思考強度，還蓋掉真正的原因。
    /// </remarks>
    public static bool LooksLikeReasoningRejection(string message)
    {
        if (ContainsIgnoreCase(message, "reasoning_content") ||
            ContainsIgnoreCase(message, "reasoning_details") ||
            ContainsIgnoreCase(message, "thought_signature") ||
            ContainsIgnoreCase(message, "passed back") ||
            ContainsIgnoreCase(message, "encrypted") ||
            ContainsIgnoreCase(message, "/v1/responses"))
        {
            return false;
        }

        return ContainsIgnoreCase(message, "reasoning") ||
               ContainsIgnoreCase(message, "thinking");
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「服務端不接受 temperature」。
    /// 推理模型多半禁用取樣參數，OpenAI gpt-5 系列會直接回 400 而非忽略。
    /// </summary>
    public static bool LooksLikeTemperatureRejection(string message)
    {
        return ContainsIgnoreCase(message, "temperature");
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「服務端不接受 max_tokens 這個欄位」。
    /// 只提到 max_tokens 不夠：「max_tokens 數值太大」也會提到它，那種換欄位也沒用。
    /// </summary>
    public static bool LooksLikeMaxTokensRejection(string message)
    {
        return ContainsIgnoreCase(message, "max_tokens") &&
               (ContainsIgnoreCase(message, "max_completion_tokens") ||
                ContainsIgnoreCase(message, "unsupported parameter"));
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「提示詞超出模型的上下文視窗」。
    /// 各家用詞不同，但都會提到上下文長度或 token 數量的上限。
    /// 只列具體到不會誤傷的字串；例如單獨的 "too long" 太寬鬆，不納入。
    /// </summary>
    public static bool LooksLikeContextWindowRejection(string message)
    {
        // context_length_exceeded / context_window_exceeded 都含有 "context"，
        // 但單獨的 "context" 會誤傷 context caching 相關訊息，因此逐項列出。
        return ContainsIgnoreCase(message, "context_length") ||
               ContainsIgnoreCase(message, "context length") ||
               ContainsIgnoreCase(message, "context_window") ||
               ContainsIgnoreCase(message, "context window") ||
               ContainsIgnoreCase(message, "maximum context") ||
               ContainsIgnoreCase(message, "too many tokens") ||
               ContainsIgnoreCase(message, "prompt is too long");
    }

    /// <summary>
    /// 判斷 4xx 錯誤訊息是否指向「內容被安全策略或內容過濾擋下」。
    /// OpenAI 協定家族一律以 400 回報，若不分辨就會和「請求組壞了」混為一談，
    /// 呼叫端無從得知該換一家還是該修請求。
    /// </summary>
    public static bool LooksLikeContentPolicyRejection(string message)
    {
        return ContainsIgnoreCase(message, "content_policy") ||
               ContainsIgnoreCase(message, "content policy") ||
               ContainsIgnoreCase(message, "content_filter") ||
               ContainsIgnoreCase(message, "content filter") ||
               ContainsIgnoreCase(message, "safety") ||
               ContainsIgnoreCase(message, "moderation");
    }

    /// <summary>
    /// 400／413／422 的細分。可降級重打的拒絕類型維持 <see cref="LLMError.InvalidResponse"/>，
    /// 以免影響既有的降級路徑。
    /// </summary>
    private static LLMError ResolveRejectionError(int? statusCode, string probe)
    {
        if (LooksLikeSchemaRejection(probe) ||
            LooksLikeReasoningRejection(probe) ||
            LooksLikeTemperatureRejection(probe) ||
            LooksLikeMaxTokensRejection(probe))
        {
            return LLMError.InvalidResponse;
        }

        if (statusCode == 413 || LooksLikeContextWindowRejection(probe))
        {
            return LLMError.ContextWindowExceeded;
        }

        return LooksLikeContentPolicyRejection(probe) ? LLMError.ContentFilter : LLMError.InvalidResponse;
    }

    /// <summary>
    /// 檢查字串是否包含指定的子字串（不區分大小寫的序數比對）；來源字串為 null 或空時回傳 false。
    /// </summary>
    public static bool ContainsIgnoreCase(string haystack, string needle)
    {
        return !string.IsNullOrEmpty(haystack) &&
               haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static RimLLMException Create(LLMError error, string message, TimeSpan? retryAfter, Exception? innerException)
    {
        RimLLMException exception = innerException == null
            ? new RimLLMException(error, message)
            : new RimLLMException(error, message, innerException);
        exception.RetryAfter = retryAfter;
        return exception;
    }
}
