namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// RimLLM 統一錯誤碼列舉。
    /// </summary>
    public enum LLMError
    {
        /// <summary>
        /// 無錯誤。
        /// </summary>
        None,

        /// <summary>
        /// 連線超時。
        /// </summary>
        Timeout,

        /// <summary>
        /// 觸發 API 頻率限制 (Rate Limit)。
        /// </summary>
        RateLimit,

        /// <summary>
        /// API Key 無效或已過期。
        /// </summary>
        InvalidKey,

        /// <summary>
        /// 供應商目前離線或無法連線。
        /// </summary>
        ProviderOffline,

        /// <summary>
        /// API 回傳無效或損毀的內容，無法正確解析。
        /// </summary>
        InvalidResponse,

        /// <summary>
        /// 本地網路異常或主機無網際網路連線。
        /// </summary>
        NetworkError,

        /// <summary>
        /// 找不到指定的模型名稱。
        /// </summary>
        ModelNotFound,

        /// <summary>
        /// 因安全策略或內容過濾遭到拒絕。
        /// </summary>
        ContentFilter,

        /// <summary>
        /// API 帳戶餘額或配額已耗盡。
        /// </summary>
        QuotaExceeded,

        /// <summary>
        /// 操作被使用者或系統取消。
        /// </summary>
        Cancelled,

        /// <summary>
        /// 未知錯誤。
        /// </summary>
        Unknown
    }
}
