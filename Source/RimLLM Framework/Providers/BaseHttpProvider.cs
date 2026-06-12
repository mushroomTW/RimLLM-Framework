using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
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

        public abstract Task<string> GenerateAsync(LLMRequest request, string model);

        public abstract Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived);

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
                var request = new LLMRequest { Prompt = "ping", MaxTokens = 5 };
                // 優先使用 DefaultTestModel 作為連線測試模型，因為這是最便宜且穩定的內建對話模型。
                // 只有在 DefaultTestModel 為 "default" (如 OpenAICompatible 本地相容介面) 時，才去讀取快取清單的第一個模型。
                string testModel = DefaultTestModel;
                if (testModel == "default")
                {
                    testModel = Settings.GetDefaultModel(ProviderId, DefaultTestModel);
                }

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

        protected virtual string DefaultTestModel => "default";

        /// <summary>
        /// 從 API 伺服器獲取可用模型列表。
        /// </summary>
        public abstract Task<List<string>> FetchAvailableModelsAsync();

        /// <summary>
        /// 統一的 HTTP POST 請求發送方法，包含異常處理與 LLMError 對照。
        /// </summary>
        protected virtual Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            return SendRequestAsync(HttpMethod.Post, url, payload, apiKey, authScheme, cancellationToken);
        }

        /// <summary>
        /// 統一的 HTTP GET 請求發送方法，包含異常處理與 LLMError 對照。
        /// </summary>
        protected virtual Task<string> SendGetAsync(string url, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            return SendRequestAsync(HttpMethod.Get, url, null, apiKey, authScheme, cancellationToken);
        }

        /// <summary>
        /// GET 與 POST 共用的 HTTP 發送核心：套用認證 Header、超時控制、回應錯誤對照與傳輸層例外轉換。
        /// </summary>
        private async Task<string> SendRequestAsync(HttpMethod method, string url, string payload, string apiKey, string authScheme, System.Threading.CancellationToken cancellationToken)
        {
            try
            {
                using (var httpRequest = new HttpRequestMessage(method, url))
                {
                    if (payload != null)
                    {
                        httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    }

                    // Anthropic 的 thinking beta 僅在生成類 (POST) 請求需要
                    ApplyAuthHeaders(httpRequest, apiKey, authScheme, includeThinkingBeta: method == HttpMethod.Post);

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
        private static void ApplyAuthHeaders(HttpRequestMessage httpRequest, string apiKey, string authScheme, bool includeThinkingBeta)
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
                httpRequest.Headers.Add("anthropic-beta", includeThinkingBeta
                    ? "prompt-caching-2024-07-31,thinking-2025-02-15"
                    : "prompt-caching-2024-07-31");
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

        protected void ThrowIfStreamTimedOut(System.Threading.CancellationToken linkedToken, System.Threading.CancellationToken userToken)
        {
            if (!linkedToken.IsCancellationRequested) return;

            if (userToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(userToken);
            }

            throw new RimLLMException(LLMError.Timeout, "Stream request timed out");
        }

        protected Exception ConvertStreamTransportException(string providerName, Exception ex, System.Threading.CancellationToken userToken)
        {
            if (ex is OperationCanceledException)
            {
                if (userToken.IsCancellationRequested)
                {
                    return new OperationCanceledException(userToken);
                }
                return new RimLLMException(LLMError.Timeout, $"{providerName} stream request timed out", ex);
            }

            if (ex is HttpRequestException)
            {
                return new RimLLMException(LLMError.NetworkError, $"{providerName} stream network error", ex);
            }

            return new RimLLMException(LLMError.NetworkError, $"{providerName} stream request failed: {RimLLMLog.SanitizeForLog(ex.Message, 300)}", ex);
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
            string friendlyErr = ExtractFriendlyError(responseBody, statusCode);
            if (statusCode == 401 || statusCode == 403)
            {
                throw new RimLLMException(LLMError.InvalidKey, $"Invalid API key or authorization failed: {friendlyErr}");
            }
            if (statusCode == 429)
            {
                if (friendlyErr.Contains("quota") || friendlyErr.Contains("insufficient"))
                {
                    throw new RimLLMException(LLMError.QuotaExceeded, "API insufficient quota (insufficient_quota), please check your account balance.")
                    {
                        RetryAfter = ParseRetryAfter(response)
                    };
                }
                throw new RimLLMException(LLMError.RateLimit, $"Rate limit triggered: {friendlyErr}")
                {
                    RetryAfter = ParseRetryAfter(response)
                };
            }
            if (statusCode >= 500)
            {
                throw new RimLLMException(LLMError.ProviderOffline, $"Internal server error: {friendlyErr}")
                {
                    RetryAfter = ParseRetryAfter(response)
                };
            }
            throw new RimLLMException(LLMError.Unknown, $"API request failed: {friendlyErr}");
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
