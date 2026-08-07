using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager
{
    /// <summary>SDK facade 與 Manager 之間的內部轉譯請求物件（取代 LLMRequest 角色）。</summary>
    internal sealed class RimLLMRequest
    {
        public string ModId { get; set; }
        public IList<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
        public string SystemPrompt { get; set; }
        public string CachedContext { get; set; }
        public bool EnableContextCaching { get; set; }
        public float? Temperature { get; set; }
        public int? MaxOutputTokens { get; set; }
        public ReasoningEffort? ReasoningEffort { get; set; }
        public bool DisableReasoning { get; set; }
        public int Priority { get; set; }
        public string MinFallbackLevel { get; set; }
        public string PreferredModelId { get; set; }
        public Action OnStreamRestart { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Type ResponseType { get; set; }

        public string GetEffectiveSystemPrompt()
        {
            if (string.IsNullOrEmpty(SystemPrompt)) return CachedContext;
            if (string.IsNullOrEmpty(CachedContext)) return SystemPrompt;
            return SystemPrompt + "\n\n" + CachedContext;
        }

        public RimLLMRequest Clone()
        {
            return new RimLLMRequest
            {
                ModId = ModId,
                Messages = new List<ChatMessage>(Messages),
                SystemPrompt = SystemPrompt,
                CachedContext = CachedContext,
                EnableContextCaching = EnableContextCaching,
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens,
                ReasoningEffort = ReasoningEffort,
                DisableReasoning = DisableReasoning,
                Priority = Priority,
                MinFallbackLevel = MinFallbackLevel,
                PreferredModelId = PreferredModelId,
                OnStreamRestart = OnStreamRestart,
                CancellationToken = CancellationToken,
                ResponseType = ResponseType
            };
        }
    }
}
