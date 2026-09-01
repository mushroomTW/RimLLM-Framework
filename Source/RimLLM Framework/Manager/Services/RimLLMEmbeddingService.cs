using System;
using System.Collections.Generic;
using System.ClientModel;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using OpenAI;
using OpenAI.Embeddings;
using OpenAI.Models;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
#pragma warning disable S3267 // reason: foreach+if 在此可讀性高於 Where，刻意保留現狀
    /// <summary>
    /// Embedding 向量運算服務。線上供應商一律透過官方 SDK 呼叫
    /// （Google 走 Google.GenAI，Ollama 與自架服務走 OpenAI 相容的 EmbeddingClient）。
    /// </summary>
    public class RimLLMEmbeddingService
    {
        /// <summary>
        /// 代表「尚未設定 Embedding 供應商」的代號，選用時向量運算一律擲回例外。
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
                    "Embedding 尚未設定供應商，無法產生向量。請先在設定中選擇 Embedding 供應商。");
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
        /// 取得目前 Embedding 供應商可用的模型清單。
        ///
        /// Google 走 <c>models.list</c>，並以模型自己宣告的 <c>supportedActions</c> 是否包含
        /// <c>embedContent</c> 精確篩選 —— 這是服務端給的事實，不是名稱猜測。
        /// OpenAI 相容端點（Ollama、LM Studio 等）的 <c>/v1/models</c> 不回傳能力資訊，
        /// 因此不做過濾，只把看起來像 embedding 的名稱排到前面，
        /// 避免把使用者自行命名的本地模型藏起來。
        /// </summary>
        public async Task<List<string>> FetchAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            string provider = _settings.EmbeddingProvider;
            if (string.IsNullOrEmpty(provider) || provider == DisabledProviderId)
            {
                throw new RimLLMException(LLMError.Unknown, "Embedding 尚未設定供應商，無法取得模型清單。");
            }

            string apiKey = string.IsNullOrEmpty(_settings.EmbeddingApiKey)
                ? _settings.GetActiveApiKey(GetMainProviderIdForEmbedding(provider))
                : _settings.EmbeddingApiKey;

            try
            {
                switch (provider)
                {
                    case "Google":
                        return await FetchGoogleEmbeddingModelsAsync(apiKey).ConfigureAwait(false);

                    case "LocalAPI_Ollama":
                        return await FetchOpenAiCompatibleModelsAsync(
                            apiKey, _settings.EmbeddingEndpoint, "http://localhost:11434/v1").ConfigureAwait(false);

                    case "LocalAPI_OpenAI":
                        return await FetchOpenAiCompatibleModelsAsync(
                            apiKey, _settings.EmbeddingEndpoint, "http://localhost:1234/v1").ConfigureAwait(false);

                    default:
                        throw new RimLLMException(LLMError.Unknown, $"不支援的 Embedding 供應商：{provider}");
                }
            }
            catch (ClientResultException ex)
            {
                throw LLMErrorMapper.CreateException(
                    ex.Status,
                    $"Embedding 模型清單：{Core.RimLLMLog.SanitizeForLog(ex.Message, 300)}",
                    innerException: ex);
            }
            catch (RimLLMException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw GeminiProvider.TranslateGoogleException(ex, "list models");
            }
        }

        /// <summary>
        /// 設定中快取此 Embedding 供應商模型清單所用的鍵。
        /// 與對話供應商共用同一份持久化字典，因此加上前綴避免與 providerId 相撞。
        /// </summary>
        public static string GetModelListKey(string embeddingProvider)
        {
            return "Embedding:" + (embeddingProvider ?? string.Empty);
        }

        private static async Task<List<string>> FetchGoogleEmbeddingModelsAsync(string apiKey)
        {
            using (var client = new Client(apiKey: apiKey))
            {
                var pager = await client.Models.ListAsync().ConfigureAwait(false);
                var all = new List<string>();
                var declaresEmbedding = new List<string>();

                await foreach (Model item in pager)
                {
                    string name = item?.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                    {
                        name = name.Substring("models/".Length);
                    }

                    all.Add(name);
                    if (DeclaresEmbedContent(item.SupportedActions))
                    {
                        declaresEmbedding.Add(name);
                    }
                }

                // 舊版端點可能不回傳 supportedActions，此時退回名稱排序而不是給出空清單。
                return declaresEmbedding.Count > 0 ? declaresEmbedding : OrderEmbeddingCandidatesFirst(all);
            }
        }

        /// <summary>
        /// 建立 OpenAI 相容端點的連線參數。模型清單與 embedding 兩條路徑共用，
        /// 否則端點正規化與佔位金鑰的規則會在兩處各自漂移。
        /// </summary>
        private static void BuildOpenAiCompatibleClientArgs(
            string apiKey, string endpoint, string defaultEndpoint,
            out ApiKeyCredential credential, out OpenAIClientOptions options)
        {
            options = new OpenAIClientOptions
            {
                Endpoint = new Uri(NormalizeEmbeddingEndpoint(endpoint) ?? defaultEndpoint, UriKind.Absolute)
            };
            credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? PlaceholderApiKey : apiKey);
        }

        private static async Task<List<string>> FetchOpenAiCompatibleModelsAsync(
            string apiKey, string endpoint, string defaultEndpoint)
        {
            BuildOpenAiCompatibleClientArgs(apiKey, endpoint, defaultEndpoint, out var credential, out var options);

            OpenAIModelCollection models = await new OpenAIClient(credential, options)
                .GetOpenAIModelClient()
                .GetModelsAsync()
                .ConfigureAwait(false);

            var ids = new List<string>();
            foreach (OpenAIModel model in models)
            {
                if (!string.IsNullOrEmpty(model?.Id))
                {
                    ids.Add(model.Id);
                }
            }
            return OrderEmbeddingCandidatesFirst(ids);
        }

        internal static bool DeclaresEmbedContent(IEnumerable<string> supportedActions)
        {
            if (supportedActions == null) return false;
            foreach (string action in supportedActions)
            {
                if (string.Equals(action, "embedContent", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 常見的 embedding 模型命名片段。只用來排序，不用來過濾 ——
        /// 本地伺服器的模型名由使用者自訂，過濾會把合法選項藏起來。
        /// </summary>
        private static readonly string[] EmbeddingNameHints =
        {
            "embed", "bge", "gte", "e5-", "nomic", "minilm", "mxbai", "jina", "qwen3-emb"
        };

        internal static List<string> OrderEmbeddingCandidatesFirst(IEnumerable<string> modelIds)
        {
            var likely = new List<string>();
            var others = new List<string>();
            if (modelIds != null)
            {
                foreach (string id in modelIds)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    (LooksLikeEmbeddingModel(id) ? likely : others).Add(id);
                }
            }
            likely.AddRange(others);
            return likely;
        }

        internal static bool LooksLikeEmbeddingModel(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return false;
            string lower = modelId.ToLowerInvariant();
            return Array.Exists(EmbeddingNameHints, hint => lower.IndexOf(hint, StringComparison.Ordinal) >= 0);
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
            BuildOpenAiCompatibleClientArgs(apiKey, endpoint, defaultEndpoint, out var credential, out var options);
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

        private static readonly char[] SlashTrimChars = { '/' };

        /// <summary>
        /// SDK 需要的是服務根位址（如 http://localhost:11434/v1），
        /// 因此把使用者可能填入的完整 embeddings 路徑收斂回根位址。
        /// </summary>
        internal static string NormalizeEmbeddingEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return null;

            string normalized = endpoint.Trim().TrimEnd(SlashTrimChars);
            foreach (string suffix in new[] { "/embeddings", "/api/embed" })
            {
                if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(0, normalized.Length - suffix.Length).TrimEnd(SlashTrimChars);
                    break;
                }
            }
            return normalized.Length == 0 ? null : normalized;
        }

        private static string GetMainProviderIdForEmbedding(string embeddingProvider)
        {
            switch (embeddingProvider)
            {
                case "Google": return ProviderIds.Gemini;
                case "LocalAPI_OpenAI": return ProviderIds.OpenAICompatible;
                default: return ProviderIds.OpenAI;
            }
        }
    }
#pragma warning restore S3267
#pragma warning restore S101, S2342
}