using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// HTTP 供應商基底類別，封裝 HttpClient 資源、安全協定配置與網路例外對照邏輯。
    /// </summary>
    public abstract class BaseHttpProvider : ILLMProvider
    {
        protected static readonly HttpClient HttpClient;

        static BaseHttpProvider()
        {
            // 初始化安全協定，解決 Unity/Mono 環境下部分舊版 HTTPS 憑證握手問題
            System.Net.ServicePointManager.SecurityProtocol = 
                System.Net.SecurityProtocolType.Tls12 | 
                System.Net.SecurityProtocolType.Tls11 | 
                System.Net.SecurityProtocolType.Tls;

            HttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60) // 預設 60 秒請求超時
            };
        }

        public abstract string ProviderId { get; }

        public abstract Task<string> GenerateAsync(LLMRequest request, string model);
        
        public abstract Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived);

        public abstract Task<TestResult> TestConnectionAsync();

        /// <summary>
        /// 從 API 伺服器獲取可用模型列表。
        /// </summary>
        public abstract Task<List<string>> FetchAvailableModelsAsync();

        /// <summary>
        /// 統一的 HTTP POST 請求發送方法，包含異常處理與 LLMError 對照。
        /// </summary>
        protected async Task<string> SendPostAsync(string url, string payload, string apiKey, string authScheme = "Bearer")
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
                        else if (authScheme == "Gemini")
                        {
                            // Gemini API 使用網址參數傳遞 Key，故此處無需設定 Authorization 標頭
                        }
                        else
                        {
                            httpRequest.Headers.Add(authScheme, apiKey);
                        }
                    }

                    using (HttpResponseMessage response = await HttpClient.SendAsync(httpRequest).ConfigureAwait(false))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (response.IsSuccessStatusCode)
                        {
                            return responseBody;
                        }

                        int statusCode = (int)response.StatusCode;
                        if (statusCode == 401 || statusCode == 403)
                        {
                            throw new RimLLMException(LLMError.InvalidKey, $"API 金鑰無效或授權失敗 (HTTP {statusCode}): {responseBody}");
                        }
                        if (statusCode == 429)
                        {
                            throw new RimLLMException(LLMError.RateLimit, $"觸發頻率限制 (HTTP 429): {responseBody}");
                        }
                        if (statusCode >= 500)
                        {
                            throw new RimLLMException(LLMError.ProviderOffline, $"伺服器內部錯誤 (HTTP {statusCode}): {responseBody}");
                        }
 
                        throw new RimLLMException(LLMError.Unknown, $"API 請求失敗 (HTTP {statusCode}): {responseBody}");
                    }
                }
            }
            catch (TaskCanceledException ex)
            {
                throw new RimLLMException(LLMError.Timeout, "請求超時", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new RimLLMException(LLMError.NetworkError, "網路連線異常，無法連線至 API 伺服器", ex);
            }
            catch (RimLLMException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RimLLMException(LLMError.Unknown, $"發送 API 請求時發生未預期錯誤: {ex.Message}", ex);
            }
        }
 
        /// <summary>
        /// 統一的 HTTP GET 請求發送方法，包含異常處理與 LLMError 對照。
        /// </summary>
        protected async Task<string> SendGetAsync(string url, string apiKey, string authScheme = "Bearer")
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
                        else if (authScheme == "Gemini")
                        {
                            // Gemini 使用 API key 作為 URL 參數
                        }
                        else
                        {
                            httpRequest.Headers.Add(authScheme, apiKey);
                        }
                    }
 
                    using (HttpResponseMessage response = await HttpClient.SendAsync(httpRequest).ConfigureAwait(false))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
 
                        if (response.IsSuccessStatusCode)
                        {
                            return responseBody;
                        }
 
                        int statusCode = (int)response.StatusCode;
                        if (statusCode == 401 || statusCode == 403)
                        {
                            throw new RimLLMException(LLMError.InvalidKey, $"API 金鑰無效或授權失敗 (HTTP {statusCode}): {responseBody}");
                        }
                        if (statusCode == 429)
                        {
                            throw new RimLLMException(LLMError.RateLimit, $"觸發頻率限制 (HTTP 429): {responseBody}");
                        }
                        if (statusCode >= 500)
                        {
                            throw new RimLLMException(LLMError.ProviderOffline, $"伺服器內部錯誤 (HTTP {statusCode}): {responseBody}");
                        }
 
                        throw new RimLLMException(LLMError.Unknown, $"API 請求失敗 (HTTP {statusCode}): {responseBody}");
                    }
                }
            }
            catch (TaskCanceledException ex)
            {
                throw new RimLLMException(LLMError.Timeout, "請求超時", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new RimLLMException(LLMError.NetworkError, "網路連線異常，無法連線至 API 伺服器", ex);
            }
            catch (RimLLMException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RimLLMException(LLMError.Unknown, $"發送 API 請求時發生未預期錯誤: {ex.Message}", ex);
            }
        }
    }
}
