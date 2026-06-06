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
        public List<string> FallbackChain { get; set; } = new List<string>();

        // 可調節項 (全域配置)
        public float ApiTimeout { get; set; } = 30f;       // API 逾時時間 (秒)
        public int MaxRetries { get; set; } = 3;           // 單模型最多重試次數
        public float RetryDelay { get; set; } = 3f;        // 重試間隔 (秒)
        public bool DetailedLogging { get; set; } = true;  // 是否啟用詳細日誌
        public int MaxConcurrentRequests { get; set; } = 2; // 最大並行限制
        public List<string> ChatHistory { get; set; } = new List<string>();
        public List<RimLLMManager.RequestLogEntry> RequestLogs { get; set; } = new List<RimLLMManager.RequestLogEntry>();
        public LLMReasoningEffort DefaultReasoningEffort { get; set; } = LLMReasoningEffort.None;

        private readonly object _settingsLock = new object();
        private readonly Dictionary<string, string> _apiKeys = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _apiKeyIndices = new Dictionary<string, int>();
        private readonly Dictionary<string, string> _endpoints = new Dictionary<string, string>();
        
        private readonly Dictionary<string, bool> _chinaModeProviders = new Dictionary<string, bool>
        {
            ["MiniMax"] = false,
            ["Qwen"] = false,
            ["Kimi"] = false
        };

        private readonly Dictionary<string, bool> _enabledProviders = new Dictionary<string, bool>
        {
            ["OpenAI"] = false,
            ["Gemini"] = false,
            ["OpenAICompatible"] = false,
            ["DeepSeek"] = false,
            ["Groq"] = false,
            ["Anthropic"] = false,
            ["OpenRouter"] = false,
            ["Kimi"] = false,
            ["MiniMax"] = false,
            ["Qwen"] = false,
            ["Nvidia"] = false
        };

        private readonly Dictionary<string, List<string>> _providerModels = new Dictionary<string, List<string>>
        {
            ["OpenAI"] = new List<string>(),
            ["Gemini"] = new List<string>(),
            ["OpenAICompatible"] = new List<string>(),
            ["DeepSeek"] = new List<string>(),
            ["Groq"] = new List<string>(),
            ["Anthropic"] = new List<string>(),
            ["OpenRouter"] = new List<string>(),
            ["Kimi"] = new List<string>(),
            ["MiniMax"] = new List<string>(),
            ["Qwen"] = new List<string>(),
            ["Nvidia"] = new List<string>()
        };

        /// <summary>
        /// 用於 JSON 序列化與反序列化的 DTO 結構，避開 RimWorld Scribe 字典嵌套序列化的兼容問題。
        /// </summary>
        private class SettingsDto
        {
            public List<string> FallbackChain;
            public Dictionary<string, string> EncryptedApiKeys;
            public Dictionary<string, string> Endpoints;
            public Dictionary<string, bool> EnabledProviders;
            public Dictionary<string, List<string>> ProviderModels;
            public Dictionary<string, bool> ChinaModeProviders;
            public float ApiTimeout;
            public int MaxRetries;
            public float RetryDelay;
            public bool DetailedLogging;
            public int MaxConcurrentRequests;
            public List<string> ChatHistory;
            public List<RimLLMManager.RequestLogEntry> RequestLogs;
            public LLMReasoningEffort DefaultReasoningEffort;
        }

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
                        DetailedLogging = this.DetailedLogging,
                        MaxConcurrentRequests = this.MaxConcurrentRequests,
                        ChatHistory = this.ChatHistory,
                        RequestLogs = this.RequestLogs,
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
                                    this.FallbackChain = dto.FallbackChain;
                                    this.FallbackChain.RemoveAll(entry => string.IsNullOrEmpty(entry));
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

                                if (dto.ChatHistory != null)
                                {
                                    this.ChatHistory = dto.ChatHistory;
                                    // 限制大小在 100 條內，防設定 JSON 無限膨脹
                                    if (this.ChatHistory.Count > 100)
                                    {
                                        this.ChatHistory.RemoveRange(0, this.ChatHistory.Count - 100);
                                    }
                                }
                                if (dto.RequestLogs != null)
                                {
                                    this.RequestLogs = dto.RequestLogs;
                                }
                                this.DefaultReasoningEffort = dto.DefaultReasoningEffort;

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
                    if (providerId == "MiniMax") resolvedDefault = "https://api.minimaxi.com/v1";
                    else if (providerId == "Qwen") resolvedDefault = "https://dashscope.aliyuncs.com/compatible-mode/v1";
                    else if (providerId == "Kimi") resolvedDefault = "https://api.moonshot.cn/v1";
                }
                else
                {
                    if (providerId == "MiniMax") resolvedDefault = "https://api.minimax.io/v1";
                    else if (providerId == "Qwen") resolvedDefault = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
                    else if (providerId == "Kimi") resolvedDefault = "https://api.moonshot.ai/v1";
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
    }
}
