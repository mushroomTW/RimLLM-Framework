using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// HTTP 供應商基底類別，封裝 HttpClient 資源、安全協定配置與網路例外對照邏輯。
    /// </summary>
    public abstract class BaseHttpProvider : ILLMProvider
    {
        protected static readonly HttpClient HttpClient;
        protected readonly IRimLLMSettings Settings;

        static BaseHttpProvider()
        {
            // 初始化安全協定，解決 Unity/Mono 環境下部分舊版 HTTPS 憑證握手問題
            System.Net.ServicePointManager.SecurityProtocol = 
                System.Net.SecurityProtocolType.Tls12;

            HttpClient = new HttpClient
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan // 改為由 CancellationToken 掌管，不設預設硬超時
            };
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

        /// <summary>
        /// 統一的 HTTP POST 請求發送方法：套用認證 Header、超時控制、回應錯誤對照與傳輸層例外轉換。
        /// 對話與模型清單皆已改走官方 SDK，此路徑僅供 SDK 未涵蓋的端點使用（如 Gemini 顯式快取）。
        /// </summary>
        protected virtual async Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    if (payload != null)
                    {
                        httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    ApplyAuthHeaders(httpRequest, apiKey, authScheme);

                    float timeoutSeconds = Settings?.ApiTimeout ?? 30f;
                    using (var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                    using (var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
                    using (HttpResponseMessage response = await HttpClient.SendAsync(httpRequest, linkedCts.Token).ConfigureAwait(false))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (response.IsSuccessStatusCode)
                        {
                            return responseBody;
                        }

                        ThrowHttpError(response, responseBody);
                        return null; // ThrowHttpError 一定會擲出，此行僅滿足編譯器
                    }
                }
            }
            catch (TaskCanceledException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                throw new RimLLMException(LLMError.Timeout, "Request timed out", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new RimLLMException(LLMError.NetworkError, "Network connection error, unable to connect to the API server", ex);
            }
            catch (RimLLMException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RimLLMException(LLMError.Unknown, $"Unexpected error occurred when sending API request: {RimLLMLog.SanitizeForLog(ex.Message, 300)}", ex);
            }
        }

        /// <summary>
        /// 依 authScheme 套用對應的認證 Header。Gemini 採 x-goog-api-key Header 認證，避免金鑰出現在 URL。
        /// </summary>
        private static void ApplyAuthHeaders(HttpRequestMessage httpRequest, string apiKey, string authScheme)
        {
            if (string.IsNullOrEmpty(apiKey))
                return;

            if (authScheme == AuthSchemes.Bearer)
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            else if (authScheme == AuthSchemes.Anthropic)
            {
                httpRequest.Headers.Add("x-api-key", apiKey);
                httpRequest.Headers.Add("anthropic-version", "2023-06-01");
                httpRequest.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31,thinking-2025-02-15");
            }
            else if (authScheme == AuthSchemes.Gemini)
            {
                httpRequest.Headers.Add("x-goog-api-key", apiKey);
            }
            else
            {
                httpRequest.Headers.Add(authScheme, apiKey);
            }
        }

        /// <summary>
        /// 從原始錯誤回應中，解析出最友善的錯誤明文字串，防止冗長 JSON 破壞 UI。
        /// </summary>
        protected string ExtractFriendlyError(string responseBody, int statusCode)
        {
            if (string.IsNullOrEmpty(responseBody))
                return $"HTTP {statusCode}";

            try
            {
                var json = JObject.Parse(responseBody);
                // 1. OpenAI 格式: { "error": { "message": "...", "type": "...", "code": "..." } }
                var errorObj = json["error"];
                if (errorObj != null)
                {
                    string code = errorObj["code"]?.ToString();
                    string message = errorObj["message"]?.ToString();
                    if (code == "insufficient_quota" || message?.Contains("quota") == true)
                    {
                        return "API insufficient quota (insufficient_quota), please check your account balance.";
                    }
                    if (!string.IsNullOrEmpty(message))
                    {
                    return RimLLMLog.SanitizeForLog(message, 300);
                    }
                }

                // 2. 一般格式直接取 message
                string directMessage = json["message"]?.ToString();
                if (!string.IsNullOrEmpty(directMessage))
                {
                    return RimLLMLog.SanitizeForLog(directMessage, 300);
                }
            }
            catch
            {
                // 無法解析為 JSON，則限制長度以防 UI 跑版
                if (responseBody.Length > 100)
                {
                    return RimLLMLog.SanitizeForLog(responseBody, 100);
                }
            }
 
            return RimLLMLog.SanitizeForLog(responseBody, 300);
        }

        protected void ThrowHttpError(HttpResponseMessage response, string responseBody)
        {
            int statusCode = (int)response.StatusCode;
            throw LLMErrorMapper.CreateException(
                statusCode,
                ExtractFriendlyError(responseBody, statusCode),
                ParseRetryAfter(response));
        }

        /// <summary>
        /// 解析回應中的 Retry-After Header（支援秒數與 HTTP 日期兩種格式），供重試邏輯參考。
        /// </summary>
        private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response?.Headers?.RetryAfter;
            if (retryAfter == null) return null;

            if (retryAfter.Delta.HasValue)
            {
                return retryAfter.Delta.Value > TimeSpan.Zero ? retryAfter.Delta : null;
            }
            if (retryAfter.Date.HasValue)
            {
                var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? (TimeSpan?)delta : null;
            }
            return null;
        }
    }
}
