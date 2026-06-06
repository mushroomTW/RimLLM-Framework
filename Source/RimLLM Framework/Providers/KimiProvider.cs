using System;
using System.Diagnostics;
using System.Threading.Tasks;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Kimi (月之暗面) 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class KimiProvider : OpenAIProvider
    {
        public override string ProviderId => "Kimi";
        protected override string DefaultEndpoint => "https://api.moonshot.ai/v1";

        public KimiProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        protected override string DefaultTestModel => "moonshot-v1-8k";
    }
}
