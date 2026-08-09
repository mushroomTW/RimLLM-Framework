using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// 直接送 HTTP 的備用傳輸層。對話與模型清單皆已改走官方 SDK，
    /// 目前只有官方 SDK 未涵蓋的端點會用到（Gemini 顯式快取 cachedContents）。
    /// 從 <see cref="BaseHttpProvider"/> 抽出來，避免所有供應商都繼承到一整套沒人用的 HTTP 機制。
    /// </summary>
    internal static class RimLLMHttpTransport
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            // 逾時改由 CancellationToken 掌管，不設預設硬超時。
            Timeout = Timeout.InfiniteTimeSpan
        };

        /// <summary>
        /// 送出 POST 請求：套用認證 Header、逾時控制、回應錯誤對照與傳輸層例外轉換。
        /// </summary>
        public static async Task<string> SendPostAsync(
            string url,
            string payload,
            string apiKey,
            string authScheme,
            float timeoutSeconds,
            CancellationToken cancellationToken)
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

                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
                    using (HttpResponseMessage response = await HttpClient.SendAsync(httpRequest, linkedCts.Token).ConfigureAwait(false))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (response.IsSuccessStatusCode)
                        {
                            return responseBody;
                        }

                        int statusCode = (int)response.StatusCode;
                        throw LLMErrorMapper.CreateException(
                            statusCode,
                            ExtractFriendlyError(responseBody, statusCode),
                            LLMErrorMapper.ParseRetryAfter(response));
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
        internal static string ExtractFriendlyError(string responseBody, int statusCode)
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
    }
}
