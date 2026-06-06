using System;
using System.Diagnostics;
using System.Threading.Tasks;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// MiniMax 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class MiniMaxProvider : OpenAIProvider
    {
        public override string ProviderId => "MiniMax";
        protected override string DefaultEndpoint => "https://api.minimax.io/v1";

        public MiniMaxProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        protected override string DefaultTestModel => "abab6.5g-chat";
    }
}
