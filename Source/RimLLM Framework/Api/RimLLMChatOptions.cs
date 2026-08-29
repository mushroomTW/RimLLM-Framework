using System;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework
{
#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
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
        /// 從 <see cref="ChatOptions.AdditionalProperties"/> 取出框架私有欄位。
        /// 框架把這些欄位以 AdditionalProperties 轉遞給 provider（呼叫端未使用
        /// <see cref="RimLLMChatOptions"/> 時的唯一管道），而「不存在或型別不符就用預設值」
        /// 的三段式判斷先前散落在 executor 與各 provider，逐字重複了六次。
        /// </summary>
        internal static T ReadAdditional<T>(ChatOptions options, string key, T fallback)
        {
            return options?.AdditionalProperties != null &&
                   options.AdditionalProperties.TryGetValue(key, out object value) &&
                   value is T typed
                ? typed
                : fallback;
        }

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

            return new RimLLMChatOptions
            {
                ModelId = baseClone.ModelId,
                Temperature = baseClone.Temperature,
                MaxOutputTokens = baseClone.MaxOutputTokens,
                TopP = baseClone.TopP,
                TopK = baseClone.TopK,
                FrequencyPenalty = baseClone.FrequencyPenalty,
                PresencePenalty = baseClone.PresencePenalty,
                Seed = baseClone.Seed,
                StopSequences = baseClone.StopSequences,
                Tools = baseClone.Tools,
                ToolMode = baseClone.ToolMode,
                ResponseFormat = baseClone.ResponseFormat,
                Reasoning = baseClone.Reasoning,
                AdditionalProperties = baseClone.AdditionalProperties,
                RawRepresentationFactory = baseClone.RawRepresentationFactory,

                Priority = Priority,
                MinFallbackLevel = MinFallbackLevel,
                CachedContext = CachedContext,
                _enableContextCaching = _enableContextCaching,
                OnStreamRestart = OnStreamRestart,
                DisableReasoning = DisableReasoning
            };
        }
    }
#pragma warning restore S101, S2342
}