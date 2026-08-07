using System;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// RimLLM 框架專屬選項（繼承 MEAI ChatOptions）。
    /// 純 MEAI 使用者不需知道此型別；不設定時全部使用框架預設值。
    /// </summary>
    public class RimLLMChatOptions : ChatOptions
    {
        /// <summary>請求優先權。數值越高，在全域請求佇列中越先執行。</summary>
        public int Priority { get; set; }

        /// <summary>最低相容模型等級（High/Medium/Low 或 3/2/1），供 Fallback 決定降級下限。</summary>
        public string MinFallbackLevel { get; set; }

        /// <summary>可重複使用的大型穩定上下文（如世界觀規則、輸出 Schema）。啟用 Context Caching 時 provider 優先快取此內容。</summary>
        public string CachedContext { get; set; }

        /// <summary>是否啟用長上下文快取（Context Caching）。</summary>
        public bool EnableContextCaching { get; set; }

        /// <summary>串流中途被下一個供應商接手時的通知（呼叫端應於此清空已顯示內容）。</summary>
        public Action OnStreamRestart { get; set; }

        /// <summary>關閉思考（對應舊 LLMReasoningEffort.None；Auto 以不設定 Reasoning 表達）。</summary>
        public bool DisableReasoning { get; set; }
    }
}
