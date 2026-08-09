using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework
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

        private bool? _enableContextCaching;

        /// <summary>是否啟用長上下文快取（Context Caching）。當 CachedContext 不為空時預設為 true。</summary>
        public bool EnableContextCaching
        {
            get => _enableContextCaching ?? !string.IsNullOrEmpty(CachedContext);
            set => _enableContextCaching = value;
        }

        /// <summary>串流中途被下一個供應商接手時的通知（呼叫端應於此清空已顯示內容）。</summary>
        public Action OnStreamRestart { get; set; }

        /// <summary>關閉思考（對應舊 LLMReasoningEffort.None；Auto 以不設定 Reasoning 表達）。</summary>
        public bool DisableReasoning { get; set; }

        /// <summary>
        /// ChatOptions 的可寫入公開屬性。以反射列舉而非硬編清單，
        /// 未來 MEAI 新增欄位時不需同步修改此處。
        /// </summary>
        private static readonly PropertyInfo[] BaseProperties = typeof(ChatOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();

        /// <summary>
        /// 覆寫 MEAI 的 <see cref="ChatOptions.Clone"/>，讓複製結果保留框架專屬欄位。
        /// MEAI 的 base.Clone() 是 <c>new ChatOptions()</c> 而非 MemberwiseClone，
        /// 因此不能直接轉型；此處先用它取得正確複製的基底值，再搬到新的衍生實例上。
        /// 少了這個覆寫，任何位於前方的 DelegatingChatClient（ConfigureOptions、
        /// FunctionInvocation 等）一 clone 就會靜默切掉本類別的所有設定。
        /// </summary>
        public override ChatOptions Clone()
        {
            // 交給 base 處理基底欄位的複製語意（AdditionalProperties、StopSequences、Tools 等集合）。
            ChatOptions baseClone = base.Clone();

            var clone = new RimLLMChatOptions
            {
                Priority = Priority,
                MinFallbackLevel = MinFallbackLevel,
                CachedContext = CachedContext,
                _enableContextCaching = _enableContextCaching,
                OnStreamRestart = OnStreamRestart,
                DisableReasoning = DisableReasoning
            };

            foreach (PropertyInfo property in BaseProperties)
            {
                property.SetValue(clone, property.GetValue(baseClone));
            }
            return clone;
        }
    }
}
