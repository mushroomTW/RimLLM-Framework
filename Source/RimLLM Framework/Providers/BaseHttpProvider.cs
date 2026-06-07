using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using RimLLM_Framework.SDK;

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

        public abstract Task<string> GenerateAsync(LLMRequest request, string model);
        
        public abstract Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived);

        public virtual async Task<TestResult> TestConnectionAsync()
        {
            string apiKey = Settings.GetActiveApiKey(ProviderId);
            if (string.IsNullOrEmpty(apiKey) && ProviderId != "OpenAICompatible")
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
        protected virtual async Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        if (authScheme == "Bearer")
                        {
                            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                        }
                        else if (authScheme == "Anthropic")
                        {
                            httpRequest.Headers.Add("x-api-key", apiKey);
                            httpRequest.Headers.Add("anthropic-version", "2023-06-01");
                            httpRequest.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31,thinking-2025-02-15");
                        }
                        else if (authScheme != "Gemini")
                        {
                            httpRequest.Headers.Add(authScheme, apiKey);
                        }
                    }

                    float timeoutSeconds = Settings?.ApiTimeout ?? 30f;
                    using (var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                    using (var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
                    {
                        using (HttpResponseMessage response = await HttpClient.SendAsync(httpRequest, linkedCts.Token).ConfigureAwait(false))
                        {
                            string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                            if (response.IsSuccessStatusCode)
                            {
                                return responseBody;
                            }

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
                                    throw new RimLLMException(LLMError.QuotaExceeded, "API insufficient quota (insufficient_quota), please check your account balance.");
                                }
                                throw new RimLLMException(LLMError.RateLimit, $"Rate limit triggered: {friendlyErr}");
                            }
                            if (statusCode >= 500)
                            {
                                throw new RimLLMException(LLMError.ProviderOffline, $"Internal server error: {friendlyErr}");
                            }

                            throw new RimLLMException(LLMError.Unknown, $"API request failed: {friendlyErr}");
                        }
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
                throw new RimLLMException(LLMError.Unknown, $"Unexpected error occurred when sending API request: {ex.Message}", ex);
            }
        }
 
        /// <summary>
        /// 統一的 HTTP GET 請求發送方法，包含異常處理與 LLMError 對照。
        /// </summary>
        protected async Task<string> SendGetAsync(string url, string apiKey, string authScheme = "Bearer", System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                using (var httpRequest = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        if (authScheme == "Bearer")
                        {
                            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                        }
                        else if (authScheme == "Anthropic")
                        {
                            httpRequest.Headers.Add("x-api-key", apiKey);
                            httpRequest.Headers.Add("anthropic-version", "2023-06-01");
                            httpRequest.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");
                        }
                        else if (authScheme != "Gemini")
                        {
                            httpRequest.Headers.Add(authScheme, apiKey);
                        }
                    }
 
                    float timeoutSeconds = Settings?.ApiTimeout ?? 30f;
                    using (var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                    using (var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
                    {
                        using (HttpResponseMessage response = await HttpClient.SendAsync(httpRequest, linkedCts.Token).ConfigureAwait(false))
                        {
                            string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                            if (response.IsSuccessStatusCode)
                            {
                                return responseBody;
                            }

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
                                    throw new RimLLMException(LLMError.QuotaExceeded, "API insufficient quota (insufficient_quota), please check your account balance.");
                                }
                                throw new RimLLMException(LLMError.RateLimit, $"Rate limit triggered: {friendlyErr}");
                            }
                            if (statusCode >= 500)
                            {
                                throw new RimLLMException(LLMError.ProviderOffline, $"Internal server error: {friendlyErr}");
                            }

                            throw new RimLLMException(LLMError.Unknown, $"API request failed: {friendlyErr}");
                        }
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
                throw new RimLLMException(LLMError.Unknown, $"Unexpected error occurred when sending API request: {ex.Message}", ex);
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
                        return message;
                    }
                }

                // 2. 一般格式直接取 message
                string directMessage = json["message"]?.ToString();
                if (!string.IsNullOrEmpty(directMessage))
                {
                    return directMessage;
                }
            }
            catch
            {
                // 無法解析為 JSON，則限制長度以防 UI 跑版
                if (responseBody.Length > 100)
                {
                    return responseBody.Substring(0, 97) + "...";
                }
            }
 
            return responseBody;
        }
    }
}
