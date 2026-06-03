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
