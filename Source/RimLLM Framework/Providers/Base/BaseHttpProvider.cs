using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// 供應商基底類別：提供設定存取、連線測試與共用的能力宣告。
    /// 對話、模型清單與 Gemini 顯式快取全走官方 SDK，框架已無 raw HTTP 路徑。
    /// </summary>
    public abstract class BaseHttpProvider : ILLMProvider
    {
        protected readonly IRimLLMSettings Settings;

        static BaseHttpProvider()
        {
            // 初始化安全協定，解決 Unity/Mono 環境下部分舊版 HTTPS 憑證握手問題。
            // 放在供應商基底而非傳輸層，確保任何供應商被建立時就生效（含只走官方 SDK 的路徑）。
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12;
        }

        protected BaseHttpProvider(IRimLLMSettings settings)
        {
            Settings = settings;
        }

        public abstract string ProviderId { get; }

        /// <summary>
        /// 此供應商是否必須提供 API Key 才能使用。預設為 true，本地相容介面可覆寫為 false。
        /// </summary>
        public virtual bool RequiresApiKey => true;

        public abstract Task<string> GenerateAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model);

        public abstract Task StreamAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, Action<string> onChunkReceived);

        public virtual async Task<TestResult> TestConnectionAsync()
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            if (string.IsNullOrEmpty(apiKey) && RequiresApiKey)
            {
                return new TestResult { Success = false, Provider = ProviderId, ErrorMessage = "API Key not configured", ErrorCode = LLMError.InvalidKey };
            }

            var result = new TestResult { Provider = ProviderId };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "ping") };
                var options = new ChatOptions { MaxOutputTokens = 5 };
                // 優先使用 DefaultTestModel 作為連線測試模型，因為這是最便宜且穩定的內建對話模型。
                // 只有在 DefaultTestModel 為 "default" (如 OpenAICompatible 本地相容介面) 時，才去讀取快取清單的第一個模型。
                string testModel = DefaultTestModel;
                if (testModel == "default")
                {
                    testModel = Settings.GetDefaultModel(ProviderId, DefaultTestModel);
                }

                string content = await GenerateAsync(messages, options, testModel).ConfigureAwait(false);
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

        protected virtual string DefaultTestModel => "default";

        /// <summary>
        /// 從 API 伺服器獲取可用模型列表。
        /// </summary>
        public abstract Task<List<string>> FetchAvailableModelsAsync();

    }
}
