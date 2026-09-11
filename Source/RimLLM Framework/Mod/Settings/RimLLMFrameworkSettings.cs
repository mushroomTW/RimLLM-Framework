using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using Verse;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
#pragma warning disable S2365, S3260, S3267, S3459 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀

namespace RimLLM_Framework.Mod
{
#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
    /// <summary>
    /// RimWorld Mod 設定檔。
    /// 將複雜的字典結構序列化為單一 JSON 字串儲存，並在序列化時調用 EncryptionUtility 加解密 API 金鑰。
    /// </summary>
    public class RimLLMFrameworkSettings : ModSettings, IRimLLMSettings
    {
        /// <summary>
        /// API 供應商的 Fallback Chain 順序。
        /// </summary>
        private List<string> _fallbackChain = new List<string>();

        public List<string> FallbackChain
        {
            get
            {
                lock (_settingsLock)
                {
                    return new List<string>(_fallbackChain);
                }
            }
            set
            {
                lock (_settingsLock)
                {
                    _fallbackChain = value != null ? new List<string>(value) : new List<string>();
                }
            }
        }

        // 可調節項 (全域配置)
        public float ApiTimeout { get; set; } = 30f;       // API 逾時時間 (秒)
        public int MaxRetries { get; set; } = 3;           // 單模型最多重試次數
        public float RetryDelay { get; set; } = 3f;        // 重試間隔 (秒)
        public bool DetailedLogging { get; set; } = true;  // 是否啟用詳細日誌
        public int MaxConcurrentRequests { get; set; } = 2; // 最大並行限制
        public ReasoningEffort? DefaultReasoningEffort { get; set; } = null;

        // 遙測資料（對話歷史、請求日誌、用量統計）獨立存放於 JSON 檔案，不寫入設定 XML
        private readonly RimLLMTelemetryStore _telemetry = new RimLLMTelemetryStore();
        public List<string> ChatHistory
        {
            get => _telemetry.ChatHistory;
            set => _telemetry.ChatHistory = value ?? new List<string>();
        }
        public List<RimLLMManager.RequestLogEntry> RequestLogs
        {
            get => _telemetry.RequestLogs;
            set => _telemetry.RequestLogs = value ?? new List<RimLLMManager.RequestLogEntry>();
        }
        public long TotalPromptTokens
        {
            get => _telemetry.TotalPromptTokens;
            set => _telemetry.TotalPromptTokens = value;
        }
        public long TotalCompletionTokens
        {
            get => _telemetry.TotalCompletionTokens;
            set => _telemetry.TotalCompletionTokens = value;
        }
        public float TotalEstimatedCost
        {
            get => _telemetry.TotalEstimatedCost;
            set => _telemetry.TotalEstimatedCost = value;
        }

        public float DailyBudgetLimit { get; set; } = 0.0f;
        public int BudgetPolicy { get; set; } = 0; // 0 = HardBlock
        public bool EnableAntiAbuse { get; set; } = true;
        public int MaxRequestsPerWindow { get; set; } = 10;
        public int ThrottlingWindowSeconds { get; set; } = 10;
        public int CoolDownDurationSeconds { get; set; } = 60;
        /// <summary>
        /// 最大合法的路由策略值。新增策略時要同步更新這裡、
        /// <see cref="FallbackSettingsDrawer"/> 的策略名稱清單與 RimLLMFallbackPipeline 的分派。
        /// </summary>
        public const int MaxRoutingStrategy = 3; // 0=PriorityFailover, 1=MinLatency, 2=RoundRobin, 3=LowestCost

        public int RoutingStrategy { get; set; } = 2;
        public bool EnableNativeSchema { get; set; } = true;
        public bool EnableJsonRepair { get; set; } = true;
        // 預設關閉：命中即代表相同輸入拿到相同輸出，對敘事類文本未必是玩家要的，由玩家自行開啟。
        public bool EnableResponseCache { get; set; } = false;
        public float ResponseCacheTtlMinutes { get; set; } = 30f;

        public string EmbeddingProvider { get; set; } = "Disabled";

        public string EmbeddingModel
        {
            get => GetEmbeddingModel(EmbeddingProvider);
            set => SetEmbeddingModel(EmbeddingProvider, value);
        }

        public string EmbeddingEndpoint
        {
            get => GetEmbeddingEndpoint(EmbeddingProvider);
            set => SetEmbeddingEndpoint(EmbeddingProvider, value);
        }

        public string EmbeddingApiKey
        {
            get => GetEmbeddingApiKey(EmbeddingProvider);
            set => SetEmbeddingApiKey(EmbeddingProvider, value);
        }

        public float DailyAccumulatedCost
        {
            get => _telemetry.DailyAccumulatedCost;
            set => _telemetry.DailyAccumulatedCost = value;
        }
        public string DailyBudgetResetDate
        {
            get => _telemetry.DailyBudgetResetDate;
            set => _telemetry.DailyBudgetResetDate = value ?? "";
        }

        public RimLLMFrameworkSettings()
        {
            _telemetry.Load();
        }

        /// <summary>
        /// 將遙測資料（對話歷史、請求日誌、用量統計）寫入獨立 JSON 檔案。
        /// 設定本體請使用 Write()。
        /// </summary>
        public void SaveTelemetry()
        {
            _telemetry.Save();
        }

        /// <summary>
        /// 標記遙測有未寫入的變更（供節流路徑呼叫）。
        /// </summary>
        public void MarkTelemetryDirty()
        {
            _telemetry.MarkDirty();
        }

        /// <summary>
        /// 僅在有未寫入變更時才實際寫檔，供關閉遊戲時的強制 flush 使用。
        /// </summary>
        public void FlushTelemetryIfDirty()
        {
            if (_telemetry.IsDirty)
            {
                _telemetry.Save();
            }
        }

        private readonly object _settingsLock = new object();
        private readonly Dictionary<string, string> _apiKeys = new Dictionary<string, string>();

        /// <summary>
        /// 載入時無法解密的 provider 金鑰密文（providerId → 原始密文）。
        /// 存檔時原樣寫回，避免換裝置導致使用者的金鑰被靜默清空。
        /// </summary>
        private readonly Dictionary<string, string> _undecryptableApiKeys = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _apiKeyIndices = new Dictionary<string, int>();
        private readonly Dictionary<string, string> _endpoints = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _modelLevelOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _embeddingModels = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _embeddingEndpoints = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _embeddingApiKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _undecryptableEmbeddingApiKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        
        /// <summary>中國端點切換旗標。鍵由 <see cref="ProviderIds.HasChinaEndpoint"/> 決定，不另外手寫一份清單。</summary>
        private readonly Dictionary<string, bool> _chinaModeProviders = BuildChinaModeMap();

        private static Dictionary<string, bool> BuildChinaModeMap()
        {
            var map = new Dictionary<string, bool>();
            foreach (string providerId in ProviderIds.BuiltIn)
            {
                if (ProviderIds.HasChinaEndpoint(providerId)) map[providerId] = false;
            }
            return map;
        }

        /// <summary>
        /// 內建供應商的啟用旗標與模型清單快取。兩份字典的鍵一律等同 <see cref="ProviderIds.BuiltIn"/>，
        /// 因此直接由該清單產生 —— 先前是各自手寫一份 12 筆的字面清單，新增供應商得同步改三處。
        /// </summary>
        private readonly Dictionary<string, bool> _enabledProviders = BuildBuiltInMap(_ => false);

        private readonly Dictionary<string, List<string>> _providerModels = BuildBuiltInMap(_ => new List<string>());

        private static Dictionary<string, TValue> BuildBuiltInMap<TValue>(Func<string, TValue> valueFactory)
        {
            var map = new Dictionary<string, TValue>();
            foreach (string providerId in ProviderIds.BuiltIn)
            {
                map[providerId] = valueFactory(providerId);
            }
            return map;
        }

        /// <summary>
        /// 用於 JSON 序列化與反序列化的 DTO 結構，避開 RimWorld Scribe 字典嵌套序列化的兼容問題。
        /// ChatHistory / RequestLogs / Total* 欄位僅保留供舊版設定遷移讀取，新版不再寫入。
        /// </summary>
#pragma warning disable 0649 // 遷移用欄位僅由 JSON 反序列化賦值
        private class SettingsDto
        {
            public List<string> FallbackChain;
            public Dictionary<string, string> EncryptedApiKeys;
            public Dictionary<string, string> Endpoints;
            public Dictionary<string, bool> EnabledProviders;
            public Dictionary<string, List<string>> ProviderModels;
            public Dictionary<string, bool> ChinaModeProviders;
            public Dictionary<string, int> ModelLevelOverrides;
            public float ApiTimeout;
            public int MaxRetries;
            public float RetryDelay;
            public bool DetailedLogging;
            public int MaxConcurrentRequests;
            public List<string> ChatHistory;
            public List<RimLLMManager.RequestLogEntry> RequestLogs;
            public int DefaultReasoningEffort;
            public long TotalPromptTokens;
            public long TotalCompletionTokens;
            public float TotalEstimatedCost;
            public float DailyBudgetLimit;
            public int BudgetPolicy;
            public bool EnableAntiAbuse;
            public int MaxRequestsPerWindow;
            public int ThrottlingWindowSeconds;
            public int CoolDownDurationSeconds;
            public int RoutingStrategy = 2;
            public bool EnableNativeSchema = true;
            public bool EnableJsonRepair = true;
            public bool EnableResponseCache;
            public float ResponseCacheTtlMinutes = 30f;
            public string EncryptedEmbeddingApiKey;
            public string EmbeddingProvider = "Disabled";
            public string EmbeddingModel = "gemini-embedding-2";
            public string EmbeddingEndpoint = "";
            public string EmbeddingApiKey = "";
            public Dictionary<string, string> EmbeddingModels;
            public Dictionary<string, string> EmbeddingEndpoints;
            public Dictionary<string, string> EncryptedEmbeddingApiKeys;
        }
#pragma warning restore 0649
#pragma warning disable S3776 // reason: 單一線性敘事含多分支與遞迴，拆分反而增加重組成本

        public override void ExposeData()
        {
            base.ExposeData();

            string jsonStr = "";
            lock (_settingsLock)
            {
                if (Scribe.mode == LoadSaveMode.Saving)
                {
                    var encryptedKeys = new Dictionary<string, string>();
                    foreach (var kvp in _apiKeys)
                    {
                        encryptedKeys[kvp.Key] = EncryptionUtility.Encrypt(kvp.Value);
                    }

                    // 載入時解不開的金鑰原樣寫回密文。若不這麼做，這些 provider 不在 _apiKeys 中，
                    // 存檔後其密文就永久消失（使用者換回原本裝置也救不回來）。
                    foreach (var kvp in _undecryptableApiKeys)
                    {
                        if (!encryptedKeys.ContainsKey(kvp.Key))
                        {
                            encryptedKeys[kvp.Key] = kvp.Value;
                        }
                    }

                    var encryptedEmbeddingKeys = new Dictionary<string, string>();
                    foreach (var kvp in _embeddingApiKeys)
                    {
                        encryptedEmbeddingKeys[kvp.Key] = EncryptionUtility.Encrypt(kvp.Value);
                    }
                    foreach (var kvp in _undecryptableEmbeddingApiKeys)
                    {
                        if (!encryptedEmbeddingKeys.ContainsKey(kvp.Key))
                        {
                            encryptedEmbeddingKeys[kvp.Key] = kvp.Value;
                        }
                    }

                    var dto = new SettingsDto
                    {
                        FallbackChain = this.FallbackChain,
                        EncryptedApiKeys = encryptedKeys,
                        Endpoints = this._endpoints,
                        EnabledProviders = this._enabledProviders,
                        ProviderModels = this._providerModels,
                        ApiTimeout = this.ApiTimeout,
                        MaxRetries = this.MaxRetries,
                        RetryDelay = this.RetryDelay,
                        ChinaModeProviders = this._chinaModeProviders,
                        ModelLevelOverrides = new Dictionary<string, int>(this._modelLevelOverrides),
                        DetailedLogging = this.DetailedLogging,
                        MaxConcurrentRequests = this.MaxConcurrentRequests,
                        DefaultReasoningEffort = this.DefaultReasoningEffort == null ? 0 : (int)this.DefaultReasoningEffort.Value + 1,
                        DailyBudgetLimit = this.DailyBudgetLimit,
                        BudgetPolicy = this.BudgetPolicy,
                        EnableAntiAbuse = this.EnableAntiAbuse,
                        MaxRequestsPerWindow = this.MaxRequestsPerWindow,
                        ThrottlingWindowSeconds = this.ThrottlingWindowSeconds,
                        CoolDownDurationSeconds = this.CoolDownDurationSeconds,
                        RoutingStrategy = this.RoutingStrategy,
                        EnableNativeSchema = this.EnableNativeSchema,
                        EnableJsonRepair = this.EnableJsonRepair,
                        EnableResponseCache = this.EnableResponseCache,
                        ResponseCacheTtlMinutes = this.ResponseCacheTtlMinutes,
                        EmbeddingProvider = this.EmbeddingProvider,
                        EmbeddingModel = this.EmbeddingModel,
                        EmbeddingEndpoint = this.EmbeddingEndpoint,
                        // Embedding 金鑰與 provider 金鑰採同一套加密；明文欄位明確寫 null 以清除舊資料。
                        EncryptedEmbeddingApiKey = EncryptionUtility.Encrypt(this.EmbeddingApiKey ?? ""),
                        EmbeddingApiKey = null,
                        EmbeddingModels = new Dictionary<string, string>(this._embeddingModels),
                        EmbeddingEndpoints = new Dictionary<string, string>(this._embeddingEndpoints),
                        EncryptedEmbeddingApiKeys = encryptedEmbeddingKeys
                    };
     
                    jsonStr = RimLLMJson.Serialize(dto);
                    Scribe_Values.Look(ref jsonStr, "SettingsData", "");
                }
                else if (Scribe.mode == LoadSaveMode.LoadingVars)
                {
                    Scribe_Values.Look(ref jsonStr, "SettingsData", "");
                    if (!string.IsNullOrEmpty(jsonStr))
                    {
                        try
                        {
                            var dto = RimLLMJson.Deserialize<SettingsDto>(jsonStr);
                            if (dto != null)
                            {
                                if (dto.FallbackChain != null)
                                {
                                    var fallbackChain = new List<string>(dto.FallbackChain);
                                    fallbackChain.RemoveAll(entry => string.IsNullOrEmpty(entry));
                                    this.FallbackChain = fallbackChain;
                                }
                                if (dto.Endpoints != null)
                                {
                                    // 移除 Clear，直接覆寫，保留新版本預設值
                                    foreach (var kvp in dto.Endpoints) this._endpoints[kvp.Key] = kvp.Value;
                                }
                                if (dto.EnabledProviders != null)
                                {
                                    // 移除 Clear，直接覆寫，保留新版本預設值
                                    foreach (var kvp in dto.EnabledProviders) this._enabledProviders[kvp.Key] = kvp.Value;
                                }
                                if (dto.ProviderModels != null)
                                {
                                    // 移除 Clear，直接覆寫，保留新版本預設值
                                    foreach (var kvp in dto.ProviderModels) this._providerModels[kvp.Key] = kvp.Value;
                                }
     
                                // _apiKeys 依然可以 Clear，因為這是完全由用戶配置決定
                                _apiKeys.Clear();
                                _undecryptableApiKeys.Clear();
                                if (dto.EncryptedApiKeys != null)
                                {
                                    foreach (var kvp in dto.EncryptedApiKeys)
                                    {
                                        string plain = EncryptionUtility.Decrypt(kvp.Value);
                                        if (plain == null)
                                        {
                                            // 解不開（通常是換了裝置）：保留原始密文，不要放進 _apiKeys，
                                            // 以免下次存檔把它覆寫成空字串而永久遺失。
                                            _undecryptableApiKeys[kvp.Key] = kvp.Value;
                                        }
                                        else
                                        {
                                            _apiKeys[kvp.Key] = plain;
                                        }
                                    }
                                }

                                if (dto.ChinaModeProviders != null)
                                {
                                    // 移除 Clear，直接覆寫，保留新版本預設值
                                    foreach (var kvp in dto.ChinaModeProviders) this._chinaModeProviders[kvp.Key] = kvp.Value;
                                }

                                if (dto.ModelLevelOverrides != null)
                                {
                                    _modelLevelOverrides.Clear();
                                    foreach (var kvp in dto.ModelLevelOverrides) _modelLevelOverrides[kvp.Key] = kvp.Value;
                                }

                                this.DefaultReasoningEffort = dto.DefaultReasoningEffort <= 0 || dto.DefaultReasoningEffort > 3 ? (ReasoningEffort?)null : (ReasoningEffort?)(dto.DefaultReasoningEffort - 1);

                                // 舊版設定 XML 內嵌的遙測資料：若獨立遙測檔尚不存在，執行一次性遷移
                                if (!_telemetry.LoadedFromDisk &&
                                    (dto.ChatHistory != null || dto.RequestLogs != null ||
                                     dto.TotalPromptTokens > 0 || dto.TotalCompletionTokens > 0 || dto.TotalEstimatedCost > 0f))
                                {
                                    if (dto.ChatHistory != null) this.ChatHistory = dto.ChatHistory;
                                    if (dto.RequestLogs != null) this.RequestLogs = dto.RequestLogs;
                                    this.TotalPromptTokens = dto.TotalPromptTokens;
                                    this.TotalCompletionTokens = dto.TotalCompletionTokens;
                                    this.TotalEstimatedCost = dto.TotalEstimatedCost;
                                    SaveTelemetry();
                                    RimLLMLog.Message("[RimLLM] 已將舊版設定中的遙測資料遷移至獨立檔案 RimLLM_Telemetry.json。");
                                }

                                // 載入可調節項 (全域配置) 並防呆
                                this.ApiTimeout = dto.ApiTimeout <= 0f ? 30f : dto.ApiTimeout;
                                this.MaxRetries = dto.MaxRetries < 0 ? 3 : dto.MaxRetries;
                                this.RetryDelay = dto.RetryDelay < 0f ? 3f : dto.RetryDelay;
                                this.DetailedLogging = dto.DetailedLogging;
                                this.MaxConcurrentRequests = dto.MaxConcurrentRequests <= 0 ? 2 : dto.MaxConcurrentRequests;
                                RimLLMLog.Enabled = this.DetailedLogging;

                                this.DailyBudgetLimit = dto.DailyBudgetLimit < 0f ? 0f : dto.DailyBudgetLimit;
                                this.BudgetPolicy = dto.BudgetPolicy < 0 ? 0 : dto.BudgetPolicy;
                                this.EnableAntiAbuse = dto.EnableAntiAbuse;
                                // Use 10 as default if the value is <= 0 for MaxRequestsPerWindow and ThrottlingWindowSeconds
                                this.MaxRequestsPerWindow = dto.MaxRequestsPerWindow <= 0 ? 10 : dto.MaxRequestsPerWindow;
                                this.ThrottlingWindowSeconds = dto.ThrottlingWindowSeconds <= 0 ? 10 : dto.ThrottlingWindowSeconds;
                                this.CoolDownDurationSeconds = dto.CoolDownDurationSeconds < 0 ? 60 : dto.CoolDownDurationSeconds;
                                // 超出範圍的策略值退回 PriorityFailover，避免未知值悄悄改變路由行為
                                this.RoutingStrategy = dto.RoutingStrategy < 0 || dto.RoutingStrategy > MaxRoutingStrategy ? 0 : dto.RoutingStrategy;
                                this.EnableNativeSchema = dto.EnableNativeSchema;
                                this.EnableJsonRepair = dto.EnableJsonRepair;
                                this.EnableResponseCache = dto.EnableResponseCache;
                                this.ResponseCacheTtlMinutes = dto.ResponseCacheTtlMinutes <= 0f ? 30f : dto.ResponseCacheTtlMinutes;
                                this.EmbeddingProvider = string.IsNullOrEmpty(dto.EmbeddingProvider) ? "Disabled" : dto.EmbeddingProvider;
                                if (dto.EmbeddingModels != null)
                                {
                                    foreach (var kvp in dto.EmbeddingModels) this._embeddingModels[kvp.Key] = kvp.Value;
                                }
                                else if (!string.IsNullOrEmpty(dto.EmbeddingModel) && this.EmbeddingProvider != "Disabled")
                                {
                                    this._embeddingModels[this.EmbeddingProvider] = dto.EmbeddingModel;
                                }

                                if (dto.EmbeddingEndpoints != null)
                                {
                                    foreach (var kvp in dto.EmbeddingEndpoints) this._embeddingEndpoints[kvp.Key] = kvp.Value;
                                }
                                else if (!string.IsNullOrEmpty(dto.EmbeddingEndpoint) && this.EmbeddingProvider != "Disabled")
                                {
                                    this._embeddingEndpoints[this.EmbeddingProvider] = dto.EmbeddingEndpoint;
                                }

                                _embeddingApiKeys.Clear();
                                _undecryptableEmbeddingApiKeys.Clear();
                                if (dto.EncryptedEmbeddingApiKeys != null)
                                {
                                    foreach (var kvp in dto.EncryptedEmbeddingApiKeys)
                                    {
                                        string plain = EncryptionUtility.Decrypt(kvp.Value);
                                        if (plain == null)
                                        {
                                            _undecryptableEmbeddingApiKeys[kvp.Key] = kvp.Value;
                                        }
                                        else
                                        {
                                            _embeddingApiKeys[kvp.Key] = plain;
                                        }
                                    }
                                }
                                else if (!string.IsNullOrEmpty(dto.EncryptedEmbeddingApiKey))
                                {
                                    string plain = EncryptionUtility.Decrypt(dto.EncryptedEmbeddingApiKey);
                                    if (plain != null && this.EmbeddingProvider != "Disabled")
                                    {
                                        _embeddingApiKeys[this.EmbeddingProvider] = plain;
                                    }
                                }
                                else if (!string.IsNullOrEmpty(dto.EmbeddingApiKey) && this.EmbeddingProvider != "Disabled")
                                {
                                    _embeddingApiKeys[this.EmbeddingProvider] = dto.EmbeddingApiKey;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            RimLLMLog.Error($"[RimLLM] 載入設定失敗: {ex.Message}");
                        }
                    }
                }
            }
        }
#pragma warning restore S3776

        public string GetApiKey(string providerId)
        {
            lock (_settingsLock)
            {
                return _apiKeys.TryGetValue(providerId, out string val) ? val : "";
            }
        }

        public string GetActiveApiKey(string providerId)
        {
            lock (_settingsLock)
            {
                if (!_apiKeys.TryGetValue(providerId, out string raw) || string.IsNullOrEmpty(raw)) return "";

                var keys = raw.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (keys.Length == 0) return "";
                if (keys.Length == 1) return keys[0].Trim();

                if (!_apiKeyIndices.TryGetValue(providerId, out int index))
                {
                    index = 0;
                }

                string selected = keys[index % keys.Length].Trim();
                _apiKeyIndices[providerId] = (index + 1) % keys.Length;
                return selected;
            }
        }

        public void SetApiKey(string providerId, string val)
        {
            lock (_settingsLock)
            {
                _apiKeys[providerId] = val;
            }
        }

        public string GetEndpoint(string providerId, string defaultVal)
        {
            lock (_settingsLock)
            {
                bool isChina = _chinaModeProviders.TryGetValue(providerId, out bool china) && china;
                string resolvedDefault = ResolveRegionalDefaultEndpoint(providerId, isChina) ?? defaultVal;

                return _endpoints.TryGetValue(providerId, out string val) && !string.IsNullOrEmpty(val)
                    ? val
                    : resolvedDefault;
            }
        }

        /// <summary>
        /// 有中國／國際兩組網域的供應商的預設端點。其餘供應商回傳 null，由呼叫端沿用傳入的預設值。
        /// </summary>
        private static string ResolveRegionalDefaultEndpoint(string providerId, bool isChina)
        {
            switch (providerId)
            {
                case ProviderIds.MiniMax:
                    return isChina ? "https://api.minimaxi.com/v1" : "https://api.minimax.io/v1";
                case ProviderIds.Qwen:
                    return isChina
                        ? "https://dashscope.aliyuncs.com/compatible-mode/v1"
                        : "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
                case ProviderIds.Kimi:
                    return isChina ? "https://api.moonshot.cn/v1" : "https://api.moonshot.ai/v1";
                default:
                    return null;
            }
        }

        public bool IsChinaMode(string providerId)
        {
            lock (_settingsLock)
            {
                return _chinaModeProviders.TryGetValue(providerId, out bool val) && val;
            }
        }

        public void SetChinaMode(string providerId, bool val)
        {
            lock (_settingsLock)
            {
                _chinaModeProviders[providerId] = val;
            }
        }

        public void SetEndpoint(string providerId, string val)
        {
            lock (_settingsLock)
            {
                _endpoints[providerId] = val;
            }
        }

        public bool IsProviderEnabled(string providerId)
        {
            lock (_settingsLock)
            {
                return _enabledProviders.TryGetValue(providerId, out bool enabled) && enabled;
            }
        }

        public void SetProviderEnabled(string providerId, bool enabled)
        {
            lock (_settingsLock)
            {
                _enabledProviders[providerId] = enabled;
            }
        }

        public List<string> GetModelList(string providerId)
        {
            lock (_settingsLock)
            {
                if (_providerModels.TryGetValue(providerId, out List<string> models))
                    return new List<string>(models); // 回傳複本，保證安全
                return new List<string>();
            }
        }

        public string GetDefaultModel(string providerId, string defaultVal)
        {
            var list = GetModelList(providerId);
            return list.Count > 0 ? list[0] : defaultVal;
        }

        public void SetModelList(string providerId, List<string> models)
        {
            lock (_settingsLock)
            {
                _providerModels[providerId] = models != null ? new List<string>(models) : new List<string>();
            }
        }

        public int GetModelLevelOverride(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return 0;
            lock (_settingsLock)
            {
                if (_modelLevelOverrides.TryGetValue(modelName, out int level))
                {
                    return level;
                }
                int colonIndex = modelName.IndexOf(':');
                if (colonIndex >= 0 && colonIndex < modelName.Length - 1)
                {
                    string stripped = modelName.Substring(colonIndex + 1);
                    if (_modelLevelOverrides.TryGetValue(stripped, out level))
                    {
                        return level;
                    }
                }
                return 0;
            }
        }

        /// <summary>
        /// 設定模型分級覆寫 (1=低, 2=中, 3=高)。傳入 0 或負值代表移除覆寫。
        /// </summary>
        public void SetModelLevelOverride(string modelName, int level)
        {
            if (string.IsNullOrEmpty(modelName)) return;
            lock (_settingsLock)
            {
                if (level <= 0)
                {
                    _modelLevelOverrides.Remove(modelName);
                }
                else
                {
                    _modelLevelOverrides[modelName] = Math.Min(level, 3);
                }
            }
        }

        public static string GetDefaultEmbeddingModel(string provider)
        {
            switch (provider)
            {
                case "Google": return "gemini-embedding-2";
                case "OpenAI": return "text-embedding-3-small";
                case "LocalAPI_Ollama": return "nomic-embed-text";
                case "LocalAPI_OpenAI": return "text-embedding-3-small";
                default: return "gemini-embedding-2";
            }
        }

        /// <summary>預設端點的唯一來源在 <see cref="RimLLMEmbeddingService"/>，這裡只是轉呼叫，避免兩張表各自漂移。</summary>
        public static string GetDefaultEmbeddingEndpoint(string provider)
        {
            return RimLLMEmbeddingService.GetDefaultEndpointOrEmpty(provider);
        }

        public string GetEmbeddingModel(string provider)
        {
            lock (_settingsLock)
            {
                if (!string.IsNullOrEmpty(provider) && _embeddingModels.TryGetValue(provider, out string val) && !string.IsNullOrEmpty(val))
                    return val;
                return GetDefaultEmbeddingModel(provider);
            }
        }

        /// <summary>玩家實際輸入的模型名稱（未設定時為空字串），供設定欄位顯示；使用端請改用 <see cref="GetEmbeddingModel"/>。</summary>
        public string GetEmbeddingModelRaw(string provider)
        {
            lock (_settingsLock)
            {
                return !string.IsNullOrEmpty(provider) && _embeddingModels.TryGetValue(provider, out string val) ? val ?? "" : "";
            }
        }

        public void SetEmbeddingModel(string provider, string model)
        {
            if (string.IsNullOrEmpty(provider)) return;
            lock (_settingsLock)
            {
                _embeddingModels[provider] = model;
            }
        }

        public string GetEmbeddingEndpoint(string provider)
        {
            lock (_settingsLock)
            {
                if (!string.IsNullOrEmpty(provider) && _embeddingEndpoints.TryGetValue(provider, out string val) && !string.IsNullOrEmpty(val))
                    return val;
                return GetDefaultEmbeddingEndpoint(provider);
            }
        }

        /// <summary>玩家實際輸入的端點（未設定時為空字串），供設定欄位顯示；使用端請改用 <see cref="GetEmbeddingEndpoint"/>。</summary>
        public string GetEmbeddingEndpointRaw(string provider)
        {
            lock (_settingsLock)
            {
                return !string.IsNullOrEmpty(provider) && _embeddingEndpoints.TryGetValue(provider, out string val) ? val ?? "" : "";
            }
        }

        public void SetEmbeddingEndpoint(string provider, string endpoint)
        {
            if (string.IsNullOrEmpty(provider)) return;
            lock (_settingsLock)
            {
                _embeddingEndpoints[provider] = endpoint;
            }
        }

        public string GetEmbeddingApiKey(string provider)
        {
            lock (_settingsLock)
            {
                return !string.IsNullOrEmpty(provider) && _embeddingApiKeys.TryGetValue(provider, out string val) ? val : "";
            }
        }

        public void SetEmbeddingApiKey(string provider, string key)
        {
            if (string.IsNullOrEmpty(provider)) return;
            lock (_settingsLock)
            {
                _embeddingApiKeys[provider] = key;
            }
        }
    }
#pragma warning restore S101, S2342
#pragma warning restore S2365, S3260, S3267, S3459
}