using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Verse;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// RimWorld Mod 設定檔。
    /// 將複雜的字典結構序列化為單一 JSON 字串儲存，並在序列化時調用 EncryptionUtility 加解密 API 金鑰。
    /// </summary>
    public class RimLLMFrameworkSettings : ModSettings
    {
        /// <summary>
        /// API 供應商的 Fallback Chain 順序。
        /// </summary>
        public List<string> FallbackChain = new List<string>();

        private readonly Dictionary<string, string> _apiKeys = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _endpoints = new Dictionary<string, string>();
        
        private readonly Dictionary<string, bool> _enabledProviders = new Dictionary<string, bool>
        {
            ["OpenAI"] = true,
            ["Gemini"] = true,
            ["OpenAICompatible"] = true
        };

        private readonly Dictionary<string, List<string>> _providerModels = new Dictionary<string, List<string>>
        {
            ["OpenAI"] = new List<string>(),
            ["Gemini"] = new List<string>(),
            ["OpenAICompatible"] = new List<string>()
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
        }

        public override void ExposeData()
        {
            base.ExposeData();

            string jsonStr = "";
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
                    ProviderModels = this._providerModels
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
                                this.FallbackChain.RemoveAll(entry => string.IsNullOrEmpty(entry) || !entry.Contains(":"));
                            }
                            if (dto.Endpoints != null)
                            {
                                this._endpoints.Clear();
                                foreach (var kvp in dto.Endpoints) this._endpoints[kvp.Key] = kvp.Value;
                            }
                            if (dto.EnabledProviders != null)
                            {
                                this._enabledProviders.Clear();
                                foreach (var kvp in dto.EnabledProviders) this._enabledProviders[kvp.Key] = kvp.Value;
                            }
                            if (dto.ProviderModels != null)
                            {
                                this._providerModels.Clear();
                                foreach (var kvp in dto.ProviderModels) this._providerModels[kvp.Key] = kvp.Value;
                            }

                            _apiKeys.Clear();
                            if (dto.EncryptedApiKeys != null)
                            {
                                foreach (var kvp in dto.EncryptedApiKeys)
                                {
                                    _apiKeys[kvp.Key] = EncryptionUtility.Decrypt(kvp.Value);
                                }
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

        public string GetApiKey(string providerId)
        {
            return _apiKeys.TryGetValue(providerId, out string val) ? val : "";
        }

        public void SetApiKey(string providerId, string val)
        {
            _apiKeys[providerId] = val;
        }

        public string GetEndpoint(string providerId, string defaultVal)
        {
            return _endpoints.TryGetValue(providerId, out string val) ? (string.IsNullOrEmpty(val) ? defaultVal : val) : defaultVal;
        }

        public void SetEndpoint(string providerId, string val)
        {
            _endpoints[providerId] = val;
        }

        public bool IsProviderEnabled(string providerId)
        {
            return _enabledProviders.TryGetValue(providerId, out bool enabled) && enabled;
        }

        public void SetProviderEnabled(string providerId, bool enabled)
        {
            _enabledProviders[providerId] = enabled;
        }

        public List<string> GetModelList(string providerId)
        {
            if (_providerModels.TryGetValue(providerId, out List<string> models))
                return models;
            return new List<string>();
        }

        public string GetDefaultModel(string providerId, string defaultVal)
        {
            var list = GetModelList(providerId);
            return list.Count > 0 ? list[0] : defaultVal;
        }

        public void SetModelList(string providerId, List<string> models)
        {
            _providerModels[providerId] = models;
        }
    }
}
