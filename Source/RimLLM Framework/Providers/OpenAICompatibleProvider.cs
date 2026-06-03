using System;
using System.Diagnostics;
using System.Threading.Tasks;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// OpenAI 相容 API 供應商，用於自訂 Endpoint (如 Ollama, LM Studio, vLLM 等本地模型伺服器)。
    /// 繼承自 OpenAIProvider 以完全重用 OpenAI 通訊協定。
    /// </summary>
    public class OpenAICompatibleProvider : OpenAIProvider
    {
        public override string ProviderId => "OpenAICompatible";

        public override async Task<TestResult> TestConnectionAsync()
        {
            var settings = RimLLMFrameworkMod.Settings;
            // 本地相容 API 通常不需要 API 金鑰，故此處放寬檢查，不強制要求 API Key 必須存在。

            var result = new TestResult { Provider = ProviderId };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var request = new LLMRequest { Prompt = "ping", MaxTokens = 5 };
                // 預設测试模型使用 "default"
                string testModel = settings.GetDefaultModel(ProviderId, "default");

                string content = await GenerateAsync(request, testModel).ConfigureAwait(false);
                stopwatch.Stop();

                result.Success = true;
                result.Model = testModel;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }
            catch (RimLLMException ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ErrorCode = ex.Error;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ErrorCode = LLMError.Unknown;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }
    }
}
