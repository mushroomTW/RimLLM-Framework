using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// IRimLLM 介面的核心管理器實作。
    /// 統一調度 API 供應商註冊、生命週期管理與公開外觀轉發。
    /// 內部核心職責委託給：
    /// - 對話執行管道 (RimLLMChatExecutionPipeline)
    /// - 嵌入向量服務 (RimLLMEmbeddingService)
    /// - 使用量與日誌追蹤器 (RimLLMUsageTracker)
    /// - 備用管道 (RimLLMFallbackPipeline)
    /// - 請求佇列 (RimLLMRequestQueue)
    /// </summary>
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
    public class RimLLMManager
    {
        private readonly IRimLLMSettings _settings;
        private readonly Dictionary<string, ILLMProvider> _providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _providerOrder = new List<string>();
        private readonly HashSet<string> _builtInProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _providerLock = new object();

        private readonly RimLLMUsageTracker _usageTracker;
        private readonly RimLLMEmbeddingService _embeddingService;
        private readonly RimLLMFallbackPipeline _fallbackPipeline;
        private readonly RimLLMThrottleStore _throttleStore;
        private readonly RimLLMResponseCacheStore _responseCacheStore;
        private readonly RimLLMRequestQueue _requestQueue;
        private readonly RimLLMHealthLedger _healthLedger;

        public RimLLMEmbeddingService EmbeddingService => _embeddingService;
        public RimLLMUsageTracker UsageTracker => _usageTracker;
        internal IRimLLMSettings Settings => _settings;

        /// <summary>
        /// 使用量統計日誌實體，保持結構以相容 Scribe 序列化。
        /// </summary>
        public class RequestLogEntry
        {
            public DateTime Timestamp { get; set; } = DateTime.Now;
            public string ModId { get; set; }
            public string Provider { get; set; }
            public string Model { get; set; }
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public long LatencyMs { get; set; }
        }

        /// <summary>
        /// 提供外部 UI 查詢的呼叫日誌歷史記錄轉發。
        /// </summary>
        public ConcurrentQueue<RequestLogEntry> RequestLogs => _usageTracker.RequestLogs;

        public RimLLMManager(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            RimLLMLog.Enabled = _settings.DetailedLogging;

            // 建立子模組
            _requestQueue = new RimLLMRequestQueue(settings);
            _healthLedger = new RimLLMHealthLedger();
            _usageTracker = new RimLLMUsageTracker(settings);

            _fallbackPipeline = new RimLLMFallbackPipeline(
                settings,
                _healthLedger,
                _usageTracker,
                providerId => TryGetProvider(providerId, out var provider) ? provider : null,
                IsProviderEnabled);

            _throttleStore = new RimLLMThrottleStore(settings);
            // 節流狀態與回應快取都必須跨 client 共用：CreateChatClient 每次呼叫都組出全新的
            // 一疊中介層，狀態若跟著中介層走，換一個 client 就等同重置。
            _responseCacheStore = new RimLLMResponseCacheStore();

            // 初始化並註冊內建供應商
            RegisterBuiltInProvider(new OpenAIProvider(settings));
            RegisterBuiltInProvider(new GeminiProvider(settings));
            RegisterBuiltInProvider(new OpenAICompatibleProvider(settings));
            RegisterBuiltInProvider(new DeepSeekProvider(settings));
            RegisterBuiltInProvider(new GroqProvider(settings));
            RegisterBuiltInProvider(new GrokProvider(settings));
            RegisterBuiltInProvider(new OpenRouterProvider(settings));
            RegisterBuiltInProvider(new KimiProvider(settings));
            RegisterBuiltInProvider(new MiniMaxProvider(settings));
            RegisterBuiltInProvider(new QwenProvider(settings));
            RegisterBuiltInProvider(new NvidiaProvider(settings));
            RegisterBuiltInProvider(new ZaiProvider(settings));

            _embeddingService = new RimLLMEmbeddingService(settings);
        }

        private void RegisterBuiltInProvider(ILLMProvider provider)
        {
            lock (_providerLock)
            {
                _providers[provider.ProviderId] = provider;
                _providerOrder.Add(provider.ProviderId);
                _builtInProviderIds.Add(provider.ProviderId);
            }
        }

        /// <summary>
        /// 註冊外部供應商，供第三方 Mod 擴充自訂的 LLM 供應商。
        /// 外部供應商註冊後即視為啟用，使用者透過 Fallback Chain 控制其參與。
        /// </summary>
        /// <exception cref="InvalidOperationException">當 ProviderId 與既有供應商重複時擲出，防止覆蓋內建供應商。</exception>
        public void RegisterProvider(ILLMProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            if (string.IsNullOrEmpty(provider.ProviderId))
                throw new ArgumentException("ProviderId cannot be empty or null", nameof(provider));

            lock (_providerLock)
            {
                if (_providers.ContainsKey(provider.ProviderId))
                {
                    throw new InvalidOperationException($"[RimLLM] Provider ID '{provider.ProviderId}' is already registered and cannot be overridden.");
                }

                _providers[provider.ProviderId] = provider;
                _providerOrder.Add(provider.ProviderId);
            }
            RimLLMLog.Message($"[RimLLM] Registered external provider: {provider.ProviderId}");
        }

        /// <summary>
        /// 取得所有已註冊供應商的識別碼（依註冊順序）。
        /// </summary>
        public List<string> GetRegisteredProviderIds()
        {
            lock (_providerLock)
            {
                return new List<string>(_providerOrder);
            }
        }

        /// <summary>
        /// 取得指定供應商的能力描述。
        /// </summary>
        public LLMProviderCapabilities GetProviderCapabilities(string providerId)
        {
            if (string.IsNullOrEmpty(providerId))
            {
                return new LLMProviderCapabilities();
            }

            lock (_providerLock)
            {
                if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
                {
                    return new LLMProviderCapabilities();
                }

                return provider.Capabilities ?? new LLMProviderCapabilities();
            }
        }

        /// <summary>
        /// 檢查供應商是否啟用。內建供應商由設定 UI 控制；外部註冊的供應商視為註冊即啟用。
        /// </summary>
        public bool IsProviderEnabled(string providerId)
        {
            bool isBuiltIn;
            lock (_providerLock)
            {
                if (!_providers.ContainsKey(providerId))
                    return false;
                isBuiltIn = _builtInProviderIds.Contains(providerId);
            }
            return !isBuiltIn || _settings.IsProviderEnabled(providerId);
        }

        /// <summary>
        /// 執行緒安全地查找已註冊的供應商。
        /// </summary>
        private bool TryGetProvider(string providerId, out ILLMProvider provider)
        {
            lock (_providerLock)
            {
                return _providers.TryGetValue(providerId, out provider);
            }
        }

        internal IChatClient CreateChatClient(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                throw new ArgumentException("ModId cannot be empty or null", nameof(modId));
            }

            // 由內往外堆疊。層序是語意性的，不可調換：
            // 正規化必須在最外層，否則快取算鍵時看到的思考強度會和實際送出的不一致；
            // 快取必須排在防濫用與預算之前，因為命中不發 API 呼叫，攔阻零成本的重播沒有意義；
            // 預算則排在防濫用之後、佇列之前，被擋下的請求不該佔用併發名額；
            // 佇列在最內層，名額只留給真正要打 API 的請求。
            IChatClient client = RimLLMFailoverChatClient.Create(
                _settings, _healthLedger, _usageTracker, _fallbackPipeline, modId);
            client = new RimLLMRequestQueueChatClient(client, _requestQueue);
            client = new RimLLMBudgetChatClient(client, _usageTracker);
            client = new RimLLMAntiAbuseChatClient(client, _settings, _throttleStore, modId);
            client = new RimLLMResponseCacheChatClient(client, _settings, _responseCacheStore);
            client = new RimLLMOptionsNormalizingChatClient(client, _settings);
            return client;
        }

        /// <summary>建立綁定指定 Mod 的 embedding generator。modId 用於防濫用節流與遙測歸屬。</summary>
        internal RimLLMEmbeddingClient CreateEmbeddingGenerator(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                throw new ArgumentException("ModId cannot be empty or null", nameof(modId));
            }
            return new RimLLMEmbeddingClient(this, modId);
        }

        /// <summary>執行指定 Mod 的防濫用檢查（供 embedding facade 於每次呼叫時使用）。</summary>
        internal void CheckAntiAbuseForMod(string modId)
        {
            if (_settings.EnableAntiAbuse)
            {
                _throttleStore.CheckAntiAbuse(modId);
            }
        }

        /// <summary>
        /// 結構化輸出的核心流程轉發。
        /// </summary>
        internal T DeserializeStructured<T>(string rawResponse, IRimLLMSettings settings)
        {
            return RimLLMStructuredOutput.Deserialize<T>(rawResponse, settings ?? _settings);
        }

        internal static T DeserializeAndValidate<T>(string json)
        {
            return RimLLMJsonHelper.DeserializeAndValidate<T>(json);
        }

        public async Task<TestResult> TestProviderAsync(string providerId)
        {
            if (!TryGetProvider(providerId, out ILLMProvider provider))
            {
                return new TestResult
                {
                    Success = false,
                    Provider = providerId,
                    ErrorMessage = $"Unknown provider ID: {providerId}",
                    ErrorCode = LLMError.ProviderOffline
                };
            }

            return await provider.TestConnectionAsync().ConfigureAwait(false);
        }

        public async Task<List<string>> FetchProviderModelsAsync(string providerId)
        {
            if (!TryGetProvider(providerId, out ILLMProvider provider))
            {
                throw new RimLLMException(LLMError.ProviderOffline, $"Unknown provider ID: {providerId}");
            }

            return await provider.FetchAvailableModelsAsync().ConfigureAwait(false);
        }

#pragma warning disable S2325 // reason: 實例方法維持外部呼叫一致性，雖可 static 但保留實例語意
        internal string GetSampleJson(Type type)
        {
            return RimLLMJsonHelper.GetSampleJson(type);
        }
#pragma warning restore S2325

        internal bool ResolveFallbackEntry(string entry, out string providerId, out string modelName)
        {
            return _fallbackPipeline.ResolveFallbackEntry(entry, out providerId, out modelName);
        }

        public void ClearLogs()
        {
            _usageTracker.ClearLogs();
        }

        public void ClearCooldowns()
        {
            _fallbackPipeline.ClearCooldowns();
            _throttleStore.ClearCooldowns();
        }

        public void RecordUsage(string providerId, string modelName, int promptTokens, int completionTokens, int cachedPromptTokens = 0)
        {
            _usageTracker.RecordUsage(providerId, modelName, promptTokens, completionTokens, cachedPromptTokens);
        }

        public void ResetUsage()
        {
            _usageTracker.ResetUsage();
        }
    }
#pragma warning restore S101
}