using System;
using System.Diagnostics;
using System.Threading.Tasks;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// NVIDIA API 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class NvidiaProvider : OpenAIProvider
    {
        public override string ProviderId => "Nvidia";
        protected override string DefaultEndpoint => "https://integrate.api.nvidia.com/v1";

        public NvidiaProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        protected override string DefaultTestModel => "meta/llama-3.1-8b-instruct";
    }
}
