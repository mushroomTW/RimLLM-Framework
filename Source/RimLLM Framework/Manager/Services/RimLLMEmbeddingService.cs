using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// Embedding 向量運算服務，負責呼叫外部 embedding API 取得文字向量，
    /// 並提供餘弦與 Trigram 相似度計算工具供其他 Mod 直接使用。
    /// </summary>
    public class RimLLMEmbeddingService
    {
        /// <summary>
        /// 共用的 HttpClient。逾時一律交由 CancellationTokenSource 控制，
        /// 因此這裡設為無限，避免 HttpClient 內建逾時與 ApiTimeout 互相干擾。
        /// </summary>
        private static readonly HttpClient HttpClient;

        static RimLLMEmbeddingService()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        /// <summary>
        /// 代表「不呼叫外部 API，僅使用本機 Trigram 比對」的供應商代號。
        /// </summary>
        public const string OfflineProviderId = "Offline_Trigram";

        private readonly IRimLLMSettings _settings;

        public RimLLMEmbeddingService(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 計算單筆文字的 embedding 向量。
        /// </summary>
        /// <exception cref="RimLLMException">
        /// 當 EmbeddingProvider 設為離線模式、供應商不支援或 API 回傳錯誤時拋出。
        /// </exception>
        public async Task<float[]> ComputeEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(text))
            {
                throw new ArgumentException("要計算 embedding 的文字不可為空。", nameof(text));
            }

            string provider = _settings.EmbeddingProvider;
            if (string.IsNullOrEmpty(provider) || provider == OfflineProviderId)
            {
                throw new RimLLMException(
                    LLMError.Unknown,
                    "目前的 Embedding 供應商為離線 Trigram 模式，不支援向量運算。請先在設定中選擇線上 Embedding 供應商，或改用 CalculateTrigramSimilarity。");
            }

            string model = _settings.EmbeddingModel;
            string apiKey = string.IsNullOrEmpty(_settings.EmbeddingApiKey)
                ? _settings.GetActiveApiKey(GetMainProviderIdForEmbedding(provider))
                : _settings.EmbeddingApiKey;
            string endpoint = _settings.EmbeddingEndpoint;

            // 以 ApiTimeout 建立逾時來源，並與呼叫端的取消 Token 連動。
            float timeoutSeconds = _settings.ApiTimeout > 0 ? _settings.ApiTimeout : 30f;
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
            {
                try
                {
                    if (provider == "Google")
                    {
                        return await ComputeGoogleEmbeddingAsync(text, model, apiKey, endpoint, linkedCts.Token).ConfigureAwait(false);
                    }
                    if (provider == "LocalAPI_Ollama")
                    {
                        return await ComputeOllamaEmbeddingAsync(text, model, endpoint, linkedCts.Token).ConfigureAwait(false);
                    }
                    if (provider == "LocalAPI_OpenAI")
                    {
                        return await ComputeOpenAiEmbeddingAsync(text, model, apiKey, endpoint, linkedCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 呼叫端沒有取消，代表是 ApiTimeout 觸發的逾時。
                    throw new RimLLMException(LLMError.Timeout, $"Embedding 請求逾時（{timeoutSeconds} 秒）。");
                }
                catch (HttpRequestException ex)
                {
                    throw new RimLLMException(LLMError.NetworkError, $"Embedding 請求連線失敗：{ex.Message}", ex);
                }

                throw new RimLLMException(LLMError.Unknown, $"不支援的 Embedding 供應商：{provider}");
            }
        }

        /// <summary>
        /// 批次計算多筆文字的 embedding 向量。
        /// 三家供應商的批次請求格式不一致，因此採序列呼叫以維持行為一致。
        /// </summary>
        public async Task<IReadOnlyList<float[]>> ComputeEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
        {
            if (texts == null) throw new ArgumentNullException(nameof(texts));

            var results = new List<float[]>();
            foreach (string text in texts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await ComputeEmbeddingAsync(text, cancellationToken).ConfigureAwait(false));
            }
            return results;
        }

        private static async Task<float[]> ComputeGoogleEmbeddingAsync(
            string text, string model, string apiKey, string endpoint, CancellationToken cancellationToken)
        {
            string actualUrl = string.IsNullOrEmpty(endpoint)
                ? $"https://generativelanguage.googleapis.com/v1beta/models/{model}:embedContent"
                : endpoint;

            var payloadObj = new JObject
            {
                ["content"] = new JObject
                {
                    ["parts"] = new JArray { new JObject { ["text"] = text } }
                }
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, actualUrl))
            {
                request.Content = new StringContent(payloadObj.ToString(), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Add("x-goog-api-key", apiKey);
                }

                string body = await SendAndReadAsync(request, "Google", cancellationToken).ConfigureAwait(false);
                var values = JObject.Parse(body)["embedding"]?["values"]?.ToObject<float[]>();
                if (values == null)
                {
                    throw new RimLLMException(LLMError.InvalidResponse, "Google embedding 回應不含 values 欄位。");
                }
                return values;
            }
        }

        private static async Task<float[]> ComputeOllamaEmbeddingAsync(
            string text, string model, string endpoint, CancellationToken cancellationToken)
        {
            string actualUrl = string.IsNullOrEmpty(endpoint) ? "http://localhost:11434/api/embeddings" : endpoint;

            var payloadObj = new JObject
            {
                ["model"] = model,
                ["prompt"] = text
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, actualUrl))
            {
                request.Content = new StringContent(payloadObj.ToString(), Encoding.UTF8, "application/json");

                string body = await SendAndReadAsync(request, "Ollama", cancellationToken).ConfigureAwait(false);
                var values = JObject.Parse(body)["embedding"]?.ToObject<float[]>();
                if (values == null)
                {
                    throw new RimLLMException(LLMError.InvalidResponse, "Ollama embedding 回應不含 embedding 欄位。");
                }
                return values;
            }
        }

        private static async Task<float[]> ComputeOpenAiEmbeddingAsync(
            string text, string model, string apiKey, string endpoint, CancellationToken cancellationToken)
        {
            string actualUrl = string.IsNullOrEmpty(endpoint) ? "http://localhost:1234/v1/embeddings" : endpoint;

            var payloadObj = new JObject
            {
                ["model"] = model,
                ["input"] = text
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, actualUrl))
            {
                request.Content = new StringContent(payloadObj.ToString(), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }

                string body = await SendAndReadAsync(request, "OpenAI", cancellationToken).ConfigureAwait(false);
                var values = JObject.Parse(body)["data"]?[0]?["embedding"]?.ToObject<float[]>();
                if (values == null)
                {
                    throw new RimLLMException(LLMError.InvalidResponse, "OpenAI 相容 embedding 回應不含 embedding 欄位。");
                }
                return values;
            }
        }

        private static async Task<string> SendAndReadAsync(
            HttpRequestMessage request, string providerLabel, CancellationToken cancellationToken)
        {
            using (var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    LLMError error;
                    int status = (int)response.StatusCode;
                    if (status == 401 || status == 403) error = LLMError.InvalidKey;
                    else if (status == 404) error = LLMError.ModelNotFound;
                    else if (status == 429) error = LLMError.RateLimit;
                    else if (status >= 500) error = LLMError.ProviderOffline;
                    else error = LLMError.Unknown;

                    throw new RimLLMException(
                        error,
                        $"{providerLabel} embedding API 回傳錯誤狀態 {status}：{Core.RimLLMLog.SanitizeForLog(body, 300)}");
                }
                return body;
            }
        }

        private static string GetMainProviderIdForEmbedding(string embeddingProvider)
        {
            if (embeddingProvider == "Google") return ProviderIds.Gemini;
            if (embeddingProvider == "LocalAPI_OpenAI") return ProviderIds.OpenAICompatible;
            return ProviderIds.OpenAI;
        }

        /// <summary>
        /// 計算兩個向量的餘弦相似度。長度不一致或任一為 null 時回傳 0。
        /// </summary>
        public static float CalculateCosineSimilarity(float[] v1, float[] v2)
        {
            if (v1 == null || v2 == null || v1.Length != v2.Length) return 0f;
            double dotProduct = 0;
            double mag1 = 0;
            double mag2 = 0;

            for (int i = 0; i < v1.Length; i++)
            {
                dotProduct += v1[i] * v2[i];
                mag1 += v1[i] * v1[i];
                mag2 += v2[i] * v2[i];
            }

            if (mag1 == 0 || mag2 == 0) return 0f;
            return (float)(dotProduct / (Math.Sqrt(mag1) * Math.Sqrt(mag2)));
        }

        /// <summary>
        /// 以 Trigram 詞袋計算兩段文字的餘弦相似度。完全在本機運算，不需要外部 API。
        /// </summary>
        public static float CalculateTrigramSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0f;
            if (s1 == s2) return 1f;

            var grams1 = GetTrigrams(s1);
            var grams2 = GetTrigrams(s2);

            if (grams1.Count == 0 || grams2.Count == 0) return 0f;

            var allGrams = new HashSet<string>(grams1.Keys);
            foreach (var key in grams2.Keys) allGrams.Add(key);

            double dotProduct = 0;
            double mag1 = 0;
            double mag2 = 0;

            foreach (var gram in allGrams)
            {
                double val1 = grams1.TryGetValue(gram, out int count1) ? count1 : 0;
                double val2 = grams2.TryGetValue(gram, out int count2) ? count2 : 0;

                dotProduct += val1 * val2;
                mag1 += val1 * val1;
                mag2 += val2 * val2;
            }

            if (mag1 == 0 || mag2 == 0) return 0f;
            return (float)(dotProduct / (Math.Sqrt(mag1) * Math.Sqrt(mag2)));
        }

        private static Dictionary<string, int> GetTrigrams(string str)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(str)) return result;

            string normalized = str.ToLowerInvariant();
            if (normalized.Length <= 3)
            {
                result[normalized] = 1;
                return result;
            }

            for (int i = 0; i <= normalized.Length - 3; i++)
            {
                string gram = normalized.Substring(i, 3);
                if (result.TryGetValue(gram, out int count))
                {
                    result[gram] = count + 1;
                }
                else
                {
                    result[gram] = 1;
                }
            }

            return result;
        }
    }
}
