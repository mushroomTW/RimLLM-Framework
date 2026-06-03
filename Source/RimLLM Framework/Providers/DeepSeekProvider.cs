using System;
using System.Diagnostics;
using System.Threading.Tasks;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// DeepSeek API 供應商。
    /// 由於 DeepSeek 協定與 OpenAI 完全相容，此處繼承 OpenAIProvider 並重寫相關設定，實現高代碼重用。
    /// </summary>
    public class DeepSeekProvider : OpenAIProvider
    {
        public override string ProviderId => "DeepSeek";

        public override async Task<TestResult> TestConnectionAsync()
        {
            var settings = ArchotechNexusMod.Settings;
            string apiKey = settings.GetApiKey(ProviderId);
            if (string.IsNullOrEmpty(apiKey))
            {
                return new TestResult { Success = false, Provider = ProviderId, ErrorMessage = "未設定 API 金鑰", ErrorCode = LLMError.InvalidKey };
            }

            var result = new TestResult { Provider = ProviderId };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var request = new LLMRequest { Prompt = "ping", MaxTokens = 5 };
                // 預設使用 deepseek-chat (v3/coder 均共用此 Endpoint)
                string testModel = settings.GetDefaultModel(ProviderId, "deepseek-chat");

                string content = await GenerateAsync(request, testModel).ConfigureAwait(false);
                stopwatch.Stop();

                result.Success = true;
                result.Model = testModel;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }
            catch (ArchotechException ex)
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
