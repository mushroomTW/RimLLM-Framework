using System;
using System.Diagnostics;
using System.Threading.Tasks;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// DeepSeek API 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class DeepSeekProvider : OpenAIProvider
    {
        public override string ProviderId => "DeepSeek";
        protected override string DefaultEndpoint => "https://api.deepseek.com";

        public DeepSeekProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        protected override string DefaultTestModel => "deepseek-chat";
    }
}
