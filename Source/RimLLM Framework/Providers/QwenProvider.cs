using System;
using System.Diagnostics;
using System.Threading.Tasks;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Qwen (通義千問) 供應商，完全相容 OpenAI API 格式。
    /// </summary>
    public class QwenProvider : OpenAIProvider
    {
        public override string ProviderId => "Qwen";
        protected override string DefaultEndpoint => "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";

        public QwenProvider(IRimLLMSettings settings) : base(settings)
        {
        }

        protected override string DefaultTestModel => "qwen-plus";
    }
}
