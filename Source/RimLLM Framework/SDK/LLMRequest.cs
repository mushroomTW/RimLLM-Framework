using System;

namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// LLM 思考強度 (Reasoning Effort / Thinking Budget) 等級。
    /// </summary>
    public enum LLMReasoningEffort
    {
        Auto,   // 自動 / 預設 (由 API 供應商決定或為動態思維)
        Low,    // 低強度 (OpenAI: low, Anthropic/DeepSeek/Gemini: 1024 tokens)
        Medium, // 中強度 (OpenAI: medium, Anthropic/DeepSeek/Gemini: 2048 tokens)
        High,   // 高強度 (OpenAI: high, Anthropic/DeepSeek/Gemini: 4096 tokens)
        None    // 關閉思考
    }

    /// <summary>
    /// LLM 請求結構定義，描述發送給語言模型的各項參數。
    /// </summary>
    public class LLMRequest
    {
        /// <summary>
        /// 呼叫端 Mod 的唯一識別碼。
        /// 用於配額限制、成本統計與安全校驗。
        /// </summary>
        public string ModId { get; set; }

        /// <summary>
        /// 使用者提示詞內容。
        /// </summary>
        public string Prompt { get; set; }

        /// <summary>
        /// 系統提示詞內容。用以規範 AI 角色行為或輸出格式。
        /// </summary>
        public string SystemPrompt { get; set; }

        /// <summary>
        /// 模型溫度參數，控制生成內容的隨機性 (範圍通常為 0.0 ~ 2.0，預設為 0.7)。
        /// </summary>
        public float Temperature { get; set; } = 0.7f;

        /// <summary>
        /// 生成內容的最大 Token 數限制。
        /// </summary>
        public int MaxTokens { get; set; } = 1024;

        /// <summary>
        /// 結構化輸出的目標型別 (當呼叫 GenerateObjectAsync 時使用)。
        /// </summary>
        public Type ResponseType { get; set; }

        /// <summary>
        /// 請求的優先權。數值越高，在全域請求隊列中會優先被執行。
        /// </summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// 最低相容模型等級，供 Fallback 決定降級下限。
        /// </summary>
        public string MinFallbackLevel { get; set; }

        /// <summary>
        /// 非同步請求取消 Token。
        /// </summary>
        public System.Threading.CancellationToken CancellationToken { get; set; } = default;

        /// <summary>
        /// 請求進入佇列的時間戳記，供日誌與外部統計參考（實際調度以 QueueEntry.EnqueueTime 為準）。
        /// </summary>
        public DateTime RequestTime { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 是否啟用長上下文快取 (Context Caching)。
        /// </summary>
        public bool EnableContextCaching { get; set; } = false;

        /// <summary>
        /// 推理性模型的思考強度 (Reasoning Effort / Thinking Budget)。
        /// </summary>
        public LLMReasoningEffort ReasoningEffort { get; set; } = LLMReasoningEffort.Auto;

        /// <summary>
        /// 是否啟用串流輸出 (Streaming)。
        /// </summary>
        public bool EnableStreaming { get; set; } = false;

        /// <summary>
        /// 當啟用串流且收到新的文字片段時的呼叫回呼。
        /// </summary>
        public Action<string> OnChunkReceived { get; set; }

        /// <summary>
        /// 創建當前 Request 的深拷貝，避免在傳遞與修復過程中產生全域副作用。
        /// </summary>
        public LLMRequest Clone()
        {
            return new LLMRequest
            {
                ModId = this.ModId,
                Prompt = this.Prompt,
                SystemPrompt = this.SystemPrompt,
                Temperature = this.Temperature,
                MaxTokens = this.MaxTokens,
                ResponseType = this.ResponseType,
                Priority = this.Priority,
                MinFallbackLevel = this.MinFallbackLevel,
                CancellationToken = this.CancellationToken,
                RequestTime = this.RequestTime,
                EnableContextCaching = this.EnableContextCaching,
                ReasoningEffort = this.ReasoningEffort,
                EnableStreaming = this.EnableStreaming,
                OnChunkReceived = this.OnChunkReceived
            };
        }
    }
}
