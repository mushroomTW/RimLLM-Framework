using System;
using System.Collections.Generic;
using System.ClientModel;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using OpenAI;
using OpenAI.Embeddings;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// Embedding 向量運算服務。線上供應商一律透過官方 SDK 呼叫
    /// （Google 走 Google.GenAI，Ollama 與自架服務走 OpenAI 相容的 EmbeddingClient），
    /// 並提供餘弦與 Trigram 相似度計算工具供其他 Mod 直接使用。
    /// </summary>
    public class RimLLMEmbeddingService
    {
        /// <summary>
        /// 代表「尚未設定 Embedding 供應商」的代號，選用時向量運算一律擲回例外。
        /// 這不是一種離線演算法：<see cref="CalculateTrigramSimilarity"/> 是獨立的靜態工具，
        /// 與本設定無關，任何供應商設定下都能呼叫。
        /// </summary>
        public const string DisabledProviderId = "Disabled";

        /// <summary>
        /// 本地相容伺服器通常不驗證金鑰，但 OpenAI SDK 不接受空憑證，
        /// 因此在未設定金鑰時填入佔位字串。
        /// </summary>
        private const string PlaceholderApiKey = "not-required";

        private readonly IRimLLMSettings _settings;

        public RimLLMEmbeddingService(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 計算單筆文字的 embedding 向量。
        /// </summary>
        /// <exception cref="RimLLMException">
        /// 當 EmbeddingProvider 尚未設定、供應商不支援或 API 回傳錯誤時拋出。
        /// </exception>
        public async Task<float[]> ComputeEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(text))
            {
                throw new ArgumentException("要計算 embedding 的文字不可為空。", nameof(text));
            }

            string provider = _settings.EmbeddingProvider;
            if (string.IsNullOrEmpty(provider) || provider == DisabledProviderId)
            {
                throw new RimLLMException(
                    LLMError.Unknown,
                    "Embedding 尚未設定供應商，無法產生向量。請先在設定中選擇 Embedding 供應商；若只需要本機字串比對，可改用 RimLLMEmbeddingService.CalculateTrigramSimilarity。");
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
                    switch (provider)
                    {
                        case "Google":
                            return await ComputeGoogleEmbeddingAsync(text, model, apiKey, linkedCts.Token).ConfigureAwait(false);

                        case "LocalAPI_Ollama":
                            return await ComputeOpenAiCompatibleEmbeddingAsync(
                                text, model, apiKey, endpoint, "http://localhost:11434/v1", linkedCts.Token).ConfigureAwait(false);

                        case "LocalAPI_OpenAI":
                            return await ComputeOpenAiCompatibleEmbeddingAsync(
                                text, model, apiKey, endpoint, "http://localhost:1234/v1", linkedCts.Token).ConfigureAwait(false);

                        default:
                            throw new RimLLMException(LLMError.Unknown, $"不支援的 Embedding 供應商：{provider}");
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 呼叫端沒有取消，代表是 ApiTimeout 觸發的逾時。
                    throw new RimLLMException(LLMError.Timeout, $"Embedding 請求逾時（{timeoutSeconds} 秒）。");
                }
                catch (ClientResultException ex)
                {
                    throw LLMErrorMapper.CreateException(
                        ex.Status,
                        $"Embedding API：{Core.RimLLMLog.SanitizeForLog(ex.Message, 300)}",
                        innerException: ex);
                }
                catch (RimLLMException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Google.GenAI 的 ClientError／ServerError 與 Gemini 對話路徑共用同一份轉譯。
                    throw GeminiProvider.TranslateGoogleException(ex, "embedContent");
                }
            }
        }

        /// <summary>
        /// 批次計算多筆文字的 embedding 向量。
        /// Google 的批次請求語意與 OpenAI 不同，因此統一採序列呼叫以維持行為一致。
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
            string text, string model, string apiKey, CancellationToken cancellationToken)
        {
            using (var client = new Client(apiKey: apiKey))
            {
                EmbedContentResponse response = await client.Models
                    .EmbedContentAsync(model, text, null, cancellationToken)
                    .ConfigureAwait(false);

                // Google.GenAI 以 double 表示向量元素，框架統一使用 float。
                List<double> values = response?.Embeddings != null && response.Embeddings.Count > 0
                    ? response.Embeddings[0]?.Values
                    : null;

                if (values == null)
                {
                    throw new RimLLMException(LLMError.InvalidResponse, "Google embedding 回應不含向量資料。");
                }

                var vector = new float[values.Count];
                for (int i = 0; i < values.Count; i++)
                {
                    vector[i] = (float)values[i];
                }
                return vector;
            }
        }

        private static async Task<float[]> ComputeOpenAiCompatibleEmbeddingAsync(
            string text, string model, string apiKey, string endpoint, string defaultEndpoint, CancellationToken cancellationToken)
        {
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(NormalizeEmbeddingEndpoint(endpoint) ?? defaultEndpoint, UriKind.Absolute)
            };
            var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? PlaceholderApiKey : apiKey);
            var client = new EmbeddingClient(model, credential, options);

            OpenAIEmbedding embedding = await client
                .GenerateEmbeddingAsync(text, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (embedding == null)
            {
                throw new RimLLMException(LLMError.InvalidResponse, "OpenAI 相容 embedding 回應不含向量資料。");
            }
            return embedding.ToFloats().ToArray();
        }

        /// <summary>
        /// SDK 需要的是服務根位址（如 http://localhost:11434/v1），
        /// 因此把使用者可能填入的完整 embeddings 路徑收斂回根位址。
        /// </summary>
        internal static string NormalizeEmbeddingEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return null;

            string normalized = endpoint.Trim().TrimEnd(new char[] { '/' });
            foreach (string suffix in new[] { "/embeddings", "/api/embed" })
            {
                if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(0, normalized.Length - suffix.Length).TrimEnd(new char[] { '/' });
                    break;
                }
            }
            return normalized.Length == 0 ? null : normalized;
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
        /// 以 Trigram 詞袋計算兩段文字的餘弦相似度，回傳 0~1。
        /// 這是獨立的字串相似度工具，<b>不是</b> Embedding 供應商：它不產生向量、
        /// 不受 EmbeddingProvider 設定影響，任何設定下都可直接呼叫。
        /// 適合在沒有 Embedding 服務時做模糊比對的替代方案。
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
