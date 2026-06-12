using System;

namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// RimLLM SDK 拋出的專屬例外。
    /// 包含統一錯誤碼，利於呼叫端 Mod 進行錯誤處理與 Fallback。
    /// </summary>
    public class RimLLMException : Exception
    {
        /// <summary>
        /// 統一錯誤碼。
        /// </summary>
        public LLMError Error { get; }

        /// <summary>
        /// 伺服器透過 Retry-After Header 建議的重試等待時間（若有提供）。
        /// 重試邏輯會以此值與使用者設定的重試延遲取較大者。
        /// </summary>
        public TimeSpan? RetryAfter { get; set; }

        public RimLLMException(LLMError error, string message) : base(message)
        {
            Error = error;
        }

        public RimLLMException(LLMError error, string message, Exception innerException) : base(message, innerException)
        {
            Error = error;
        }
    }
}
