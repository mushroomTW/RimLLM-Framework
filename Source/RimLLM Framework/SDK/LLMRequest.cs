using System;

namespace RimLLM_Framework.SDK
{
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
    }
}
