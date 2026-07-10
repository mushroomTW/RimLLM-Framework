using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// 語意快取管理單元，負責維護記憶體快取項、計算 Embedding 與執行餘弦相似度比對。
    /// </summary>
    public class RimLLMSemanticCache
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        private readonly IRimLLMSettings _settings;
        private readonly List<SemanticCacheEntry> _cacheStore = new List<SemanticCacheEntry>();
        private readonly object _cacheLock = new object();

        // 運行統計指標
        private int _cacheHits = 0;
        private int _cacheMisses = 0;
        private long _estTokensSaved = 0;
        private long _accessSequence = 0;

        public int CacheHits => _cacheHits;
        public int CacheMisses => _cacheMisses;
        public long EstTokensSaved => _estTokensSaved;
        public int CacheCount
        {
            get
            {
                lock (_cacheLock) return _cacheStore.Count;
            }
        }

        public class SemanticCacheEntry
        {
            public string SystemPrompt { get; set; }
            public string Prompt { get; set; }
            public string Response { get; set; }
            public float[] Embedding { get; set; }
            public long LastAccessSequence { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        public RimLLMSemanticCache(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 手動清除快取並重設統計
        /// </summary>
        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _cacheStore.Clear();
                _cacheHits = 0;
                _cacheMisses = 0;
                _estTokensSaved = 0;
                _accessSequence = 0;
            }
        }

        /// <summary>
        /// 嘗試比對快取並獲取回傳，若命中則返回 Response 字串，未命中返回 null。
        /// </summary>
        public async Task<string> TryGetCachedResponseAsync(LLMRequest request)
        {
            if (!_settings.EnableSemanticCache || request.BypassSemanticCache)
            {
                return null;
            }

            string combinedQuery = request.GetEffectiveSystemPrompt() + "\n\n" + request.Prompt;
            int ttl = _settings.SemanticCacheTTL;

            lock (_cacheLock)
            {
                // 1. 優先精確字串比對，並過濾過期項目
                for (int i = _cacheStore.Count - 1; i >= 0; i--)
                {
                    var entry = _cacheStore[i];
                    if (ttl > 0 && (DateTime.UtcNow - entry.CreatedAt).TotalSeconds > ttl)
                    {
                        _cacheStore.RemoveAt(i);
                        continue;
                    }

                    if (entry.Prompt == request.Prompt && entry.SystemPrompt == request.SystemPrompt)
                    {
                        entry.LastAccessSequence = ++_accessSequence;
                        _cacheHits++;
                        _estTokensSaved += EstimateTokens(combinedQuery);
                        RimLLMLog.Message($"[RimLLM Cache] Exact Match Hit! Est. saved: {EstimateTokens(combinedQuery)} tokens.");
                        return entry.Response;
                    }
                }
            }

            // 2. 餘弦夾角相似度比對
            float[] queryEmbedding = null;
            string provider = _settings.EmbeddingProvider;

            if (provider != "Offline_Trigram")
            {
                try
                {
                    queryEmbedding = await ComputeEmbeddingAsync(combinedQuery, request.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM Cache] Failed to fetch embedding vector: {ex.Message}. Falling back to LLM direct execution.");
                    _cacheMisses++;
                    return null;
                }
            }

            lock (_cacheLock)
            {
                SemanticCacheEntry bestMatch = null;
                float bestSimilarity = -1f;

                for (int i = _cacheStore.Count - 1; i >= 0; i--)
                {
                    var entry = _cacheStore[i];
                    if (ttl > 0 && (DateTime.UtcNow - entry.CreatedAt).TotalSeconds > ttl)
                    {
                        _cacheStore.RemoveAt(i);
                        continue;
                    }

                    float similarity = 0f;
                    if (provider == "Offline_Trigram")
                    {
                        similarity = CalculateTrigramSimilarity(combinedQuery, (entry.SystemPrompt + "\n\n" + entry.Prompt));
                    }
                    else if (queryEmbedding != null && entry.Embedding != null)
                    {
                        similarity = CalculateCosineSimilarity(queryEmbedding, entry.Embedding);
                    }

                    if (similarity > bestSimilarity)
                    {
                        bestSimilarity = similarity;
                        bestMatch = entry;
                    }
                }

                float threshold = _settings.SemanticCacheThreshold;
                if (bestMatch != null && bestSimilarity >= threshold)
                {
                    bestMatch.LastAccessSequence = ++_accessSequence;
                    _cacheHits++;
                    _estTokensSaved += EstimateTokens(combinedQuery);
                    RimLLMLog.Message($"[RimLLM Cache] Semantic Match Hit! Similarity: {bestSimilarity:F3} (Threshold: {threshold:F3})");
                    return bestMatch.Response;
                }
            }

            RimLLMLog.Message("[RimLLM Cache] Cache Miss.");
            _cacheMisses++;
            return null;
        }

        /// <summary>
        /// 新增快取項目
        /// </summary>
        public async Task AddCacheEntryAsync(LLMRequest request, string response)
        {
            if (!_settings.EnableSemanticCache || request.BypassSemanticCache || string.IsNullOrEmpty(response))
            {
                return;
            }

            string combinedQuery = request.GetEffectiveSystemPrompt() + "\n\n" + request.Prompt;
            float[] embedding = null;
            string provider = _settings.EmbeddingProvider;

            if (provider != "Offline_Trigram")
            {
                try
                {
                    embedding = await ComputeEmbeddingAsync(combinedQuery, request.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM Cache] Failed to generate embedding to save in cache: {ex.Message}");
                }
            }

            int ttl = _settings.SemanticCacheTTL;

            lock (_cacheLock)
            {
                // 先清除過期項目，避免佔用快取空間
                for (int i = _cacheStore.Count - 1; i >= 0; i--)
                {
                    if (ttl > 0 && (DateTime.UtcNow - _cacheStore[i].CreatedAt).TotalSeconds > ttl)
                    {
                        _cacheStore.RemoveAt(i);
                    }
                }

                // 容量超出時，進行 LRU 剔除
                int maxCount = _settings.SemanticCacheMaxCount;
                if (maxCount <= 0) maxCount = 200;

                while (_cacheStore.Count >= maxCount && _cacheStore.Count > 0)
                {
                    int oldestIndex = 0;
                    for (int i = 1; i < _cacheStore.Count; i++)
                    {
                        if (_cacheStore[i].LastAccessSequence < _cacheStore[oldestIndex].LastAccessSequence)
                        {
                            oldestIndex = i;
                        }
                    }
                    _cacheStore.RemoveAt(oldestIndex);
                }

                _cacheStore.Add(new SemanticCacheEntry
                {
                    SystemPrompt = request.SystemPrompt,
                    Prompt = request.Prompt,
                    Response = response,
                    Embedding = embedding,
                    LastAccessSequence = ++_accessSequence,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        private async Task<float[]> ComputeEmbeddingAsync(string text, System.Threading.CancellationToken cancellationToken)
        {
            string provider = _settings.EmbeddingProvider;
            if (provider == "Offline_Trigram")
            {
                return null;
            }

            string model = _settings.EmbeddingModel;
            string apiKey = string.IsNullOrEmpty(_settings.EmbeddingApiKey)
                ? _settings.GetActiveApiKey(GetMainProviderIdForEmbedding(provider))
                : _settings.EmbeddingApiKey;

            string endpoint = _settings.EmbeddingEndpoint;

            if (provider == "Google")
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

                    using (var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new Exception($"Google embedding API returned error status {response.StatusCode}: {body}");
                        }
                        var resObj = JObject.Parse(body);
                        var values = resObj["embedding"]?["values"]?.ToObject<float[]>();
                        if (values == null) throw new Exception("Google embedding response did not contain values");
                        return values;
                    }
                }
            }
            else if (provider == "LocalAPI_Ollama")
            {
                string actualUrl = string.IsNullOrEmpty(endpoint)
                    ? "http://localhost:11434/api/embeddings"
                    : endpoint;

                var payloadObj = new JObject
                {
                    ["model"] = model,
                    ["prompt"] = text
                };

                using (var request = new HttpRequestMessage(HttpMethod.Post, actualUrl))
                {
                    request.Content = new StringContent(payloadObj.ToString(), Encoding.UTF8, "application/json");

                    using (var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new Exception($"Ollama embedding API returned error status {response.StatusCode}: {body}");
                        }
                        var resObj = JObject.Parse(body);
                        var values = resObj["embedding"]?.ToObject<float[]>();
                        if (values == null) throw new Exception("Ollama embedding response did not contain embedding list");
                        return values;
                    }
                }
            }
            else if (provider == "LocalAPI_OpenAI")
            {
                string actualUrl = string.IsNullOrEmpty(endpoint)
                    ? "http://localhost:1234/v1/embeddings"
                    : endpoint;

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

                    using (var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new Exception($"OpenAI embedding API returned error status {response.StatusCode}: {body}");
                        }
                        var resObj = JObject.Parse(body);
                        var values = resObj["data"]?[0]?["embedding"]?.ToObject<float[]>();
                        if (values == null) throw new Exception("OpenAI compatible response did not contain embedding list");
                        return values;
                    }
                }
            }

            throw new NotSupportedException($"Embedding provider {provider} is not supported");
        }

        private string GetMainProviderIdForEmbedding(string embeddingProvider)
        {
            if (embeddingProvider == "Google") return "Gemini";
            if (embeddingProvider == "LocalAPI_OpenAI") return "OpenAICompatible";
            return "OpenAI";
        }

        private float CalculateCosineSimilarity(float[] v1, float[] v2)
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

        private static long EstimateTokens(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0;
            return Math.Max(1, str.Length / 4);
        }
    }
}
