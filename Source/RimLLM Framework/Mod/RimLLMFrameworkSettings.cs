using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Verse;
using RimLLM_Framework.Core;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod
{
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
        public LLMReasoningEffort DefaultReasoningEffort { get; set; } = LLMReasoningEffort.Auto;

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

        private readonly object _settingsLock = new object();
        private readonly Dictionary<string, string> _apiKeys = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _apiKeyIndices = new Dictionary<string, int>();
        private readonly Dictionary<string, string> _endpoints = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _modelLevelOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        private readonly Dictionary<string, bool> _chinaModeProviders = new Dictionary<string, bool>
        {
            [ProviderIds.MiniMax] = false,
            [ProviderIds.Qwen] = false,
            [ProviderIds.Kimi] = false
        };

        private readonly Dictionary<string, bool> _enabledProviders = new Dictionary<string, bool>
        {
            [ProviderIds.OpenAI] = false,
            [ProviderIds.Gemini] = false,
            [ProviderIds.OpenAICompatible] = false,
            [ProviderIds.DeepSeek] = false,
            [ProviderIds.Groq] = false,
            [ProviderIds.Anthropic] = false,
            [ProviderIds.OpenRouter] = false,
            [ProviderIds.Kimi] = false,
            [ProviderIds.MiniMax] = false,
            [ProviderIds.Qwen] = false,
            [ProviderIds.Nvidia] = false
        };

        private readonly Dictionary<string, List<string>> _providerModels = new Dictionary<string, List<string>>
        {
            [ProviderIds.OpenAI] = new List<string>(),
            [ProviderIds.Gemini] = new List<string>(),
            [ProviderIds.OpenAICompatible] = new List<string>(),
            [ProviderIds.DeepSeek] = new List<string>(),
            [ProviderIds.Groq] = new List<string>(),
            [ProviderIds.Anthropic] = new List<string>(),
            [ProviderIds.OpenRouter] = new List<string>(),
            [ProviderIds.Kimi] = new List<string>(),
            [ProviderIds.MiniMax] = new List<string>(),
            [ProviderIds.Qwen] = new List<string>(),
            [ProviderIds.Nvidia] = new List<string>()
        };

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
            public LLMReasoningEffort DefaultReasoningEffort;
            public long TotalPromptTokens;
            public long TotalCompletionTokens;
            public float TotalEstimatedCost;
        }
#pragma warning restore 0649

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
                        DefaultReasoningEffort = this.DefaultReasoningEffort
                    };
     
                    jsonStr = JsonConvert.SerializeObject(dto, Formatting.None);
                    Scribe_Values.Look(ref jsonStr, "SettingsData", "");
                }
                else if (Scribe.mode == LoadSaveMode.LoadingVars)
                {
                    Scribe_Values.Look(ref jsonStr, "SettingsData", "");
                    if (!string.IsNullOrEmpty(jsonStr))
                    {
                        try
                        {
                            var dto = JsonConvert.DeserializeObject<SettingsDto>(jsonStr);
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
                                if (dto.EncryptedApiKeys != null)
                                {
                                    foreach (var kvp in dto.EncryptedApiKeys)
                                    {
                                        _apiKeys[kvp.Key] = EncryptionUtility.Decrypt(kvp.Value);
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

                                this.DefaultReasoningEffort = dto.DefaultReasoningEffort;

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
                string raw = GetApiKey(providerId);
                if (string.IsNullOrEmpty(raw)) return "";

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
            string resolvedDefault = defaultVal;
            lock (_settingsLock)
            {
                if (_chinaModeProviders.TryGetValue(providerId, out bool isChina) && isChina)
                {
                    if (providerId == ProviderIds.MiniMax) resolvedDefault = "https://api.minimaxi.com/v1";
                    else if (providerId == ProviderIds.Qwen) resolvedDefault = "https://dashscope.aliyuncs.com/compatible-mode/v1";
                    else if (providerId == ProviderIds.Kimi) resolvedDefault = "https://api.moonshot.cn/v1";
                }
                else
                {
                    if (providerId == ProviderIds.MiniMax) resolvedDefault = "https://api.minimax.io/v1";
                    else if (providerId == ProviderIds.Qwen) resolvedDefault = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
                    else if (providerId == ProviderIds.Kimi) resolvedDefault = "https://api.moonshot.ai/v1";
                }

                return _endpoints.TryGetValue(providerId, out string val) ? (string.IsNullOrEmpty(val) ? resolvedDefault : val) : resolvedDefault;
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
                return _modelLevelOverrides.TryGetValue(modelName, out int level) ? level : 0;
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

        /// <summary>
        /// 取得目前所有模型分級覆寫的複本，供 UI 列表顯示。
        /// </summary>
        public Dictionary<string, int> GetModelLevelOverridesSnapshot()
        {
            lock (_settingsLock)
            {
                return new Dictionary<string, int>(_modelLevelOverrides, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
