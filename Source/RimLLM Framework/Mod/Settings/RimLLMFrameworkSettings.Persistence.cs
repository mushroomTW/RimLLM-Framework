using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Manager.Services;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 設定的存檔與讀檔：DTO 結構、金鑰加解密、Scribe 讀寫與舊版設定遷移。
/// </summary>
public partial class RimLLMFrameworkSettings
{
    /// <summary>
    /// 用於 JSON 序列化與反序列化的 DTO 結構，避開 RimWorld Scribe 字典嵌套序列化的兼容問題。
    /// ChatHistory / RequestLogs / Total* 欄位僅保留供舊版設定遷移讀取，新版不再寫入。
    /// </summary>
    // 遷移用欄位僅由 JSON 反序列化賦值（CS0649 / S3459）。
#pragma warning disable 0649, S3459
    private sealed class SettingsDto
    {
        public List<string> FallbackChain;
        public Dictionary<string, string> EncryptedApiKeys;
        public Dictionary<string, string> Endpoints;
        public Dictionary<string, bool> EnabledProviders;
        public Dictionary<string, List<string>> ProviderModels;
        public Dictionary<string, bool> ChinaModeProviders;
        public Dictionary<string, int> ModelLevelOverrides;
        public Dictionary<string, Dictionary<string, int>> ProviderContextWindows;
        public Dictionary<string, Dictionary<string, bool>> ProviderVisionSupport;
        public Dictionary<string, int> ContextWindowOverrides;
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
        public long DailyTokenBudgetLimit;

        /// <summary>
        /// 舊版美元上限，只讀不寫：新版存檔寫出 null，因此有值即代表這是舊版存檔（見載入處的遷移）。
        /// </summary>
        public float? DailyBudgetLimit;
        public int BudgetPolicy;
        public bool EnableAntiAbuse;
        public int MaxRequestsPerWindow;
        public int ThrottlingWindowSeconds;
        public int CoolDownDurationSeconds;
        public int RoutingStrategy = 0;
        public bool EnableNativeSchema = true;
        public bool EnableJsonRepair = true;
        public string EncryptedEmbeddingApiKey;
        public string EmbeddingProvider = DisabledProvider;
        public string EmbeddingModel = "gemini-embedding-2";
        public string EmbeddingEndpoint = "";
        public string EmbeddingApiKey = "";
        public Dictionary<string, string> EmbeddingModels;
        public Dictionary<string, string> EmbeddingEndpoints;
        public Dictionary<string, string> EncryptedEmbeddingApiKeys;
        public Dictionary<string, bool> CompatTakeovers;
        public Dictionary<string, string> CompatModelPins;
        public string DecisionProvider = RimLLMDecisionProviderCatalog.Disabled;
        public Dictionary<string, string> DecisionModels;
        public Dictionary<string, string> DecisionEndpoints;
        public Dictionary<string, string> EncryptedDecisionApiKeys;
        public List<string> EmbeddingFallbackChain;
        public List<string> DecisionFallbackChain;
        // 舊版單一供應商欄位。目前仍寫出目前啟用供應商的值作為跨格式冗餘（供降級情境），
        // 僅在新存檔缺少字典欄位時用於載入遷移，載入時字典欄位一律優先；金鑰欄位一律 null，避免明文殘留。
        public string DecisionModel = "";
        public string DecisionEndpoint = "";
        public string DecisionApiKey = "";
        public string EncryptedDecisionApiKey;

        /// <summary>設定格式版本，供一次性遷移判斷；舊檔沒有這個欄位，讀成 0。</summary>
        public int SettingsVersion;
    }
#pragma warning restore 0649, S3459

    /// <summary>
    /// 目前的設定格式版本。
    /// 1：DetailedLogging 預設改為關閉。版本 0 的檔案裡的 true 是舊預設值寫進去的，
    /// 無法與玩家主動開啟區分，因此載入時一律重設為關閉一次。
    /// </summary>
    private const int CurrentSettingsVersion = 1;

    /// <summary>
    /// 建立要寫入設定 XML 的 DTO。純加密與組裝，與 Scribe 的讀寫流程分離。
    /// </summary>
    private string BuildSettingsJson()
    {
        var encryptedKeys = EncryptKeys(_apiKeys, _undecryptableApiKeys);
        var encryptedEmbeddingKeys = EncryptKeys(_embeddingApiKeys, _undecryptableEmbeddingApiKeys);
        AddUndecryptableEmbeddingFallback(encryptedEmbeddingKeys);

        bool preserveUndecryptableEmbeddingApiKey =
            !string.IsNullOrEmpty(_undecryptableEmbeddingApiKey) &&
            !string.IsNullOrEmpty(_undecryptableEmbeddingApiKeyProvider) &&
            string.Equals(this.EmbeddingProvider, _undecryptableEmbeddingApiKeyProvider, StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(this.EmbeddingProvider) &&
            !_embeddingApiKeys.ContainsKey(this.EmbeddingProvider);
        string encryptedEmbeddingApiKey;
        if (preserveUndecryptableEmbeddingApiKey)
        {
            encryptedEmbeddingApiKey = _undecryptableEmbeddingApiKey;
        }
        else if (!TryEncryptForSave(this.EmbeddingApiKey ?? "", out encryptedEmbeddingApiKey))
        {
            encryptedEmbeddingApiKey = null;
        }

        var encryptedDecisionKeys = EncryptKeys(_decisionApiKeys, _undecryptableDecisionApiKeys);

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
            ProviderContextWindows = this._providerContextWindows,
            ProviderVisionSupport = this._providerVisionSupport,
            ContextWindowOverrides = new Dictionary<string, int>(this._contextWindowOverrides),
            DetailedLogging = this.DetailedLogging,
            MaxConcurrentRequests = this.MaxConcurrentRequests,
            DefaultReasoningEffort = EncodeReasoningEffort(this.DefaultReasoningEffort),
            DailyTokenBudgetLimit = this.DailyTokenBudgetLimit,
            BudgetPolicy = this.BudgetPolicy,
            EnableAntiAbuse = this.EnableAntiAbuse,
            MaxRequestsPerWindow = this.MaxRequestsPerWindow,
            ThrottlingWindowSeconds = this.ThrottlingWindowSeconds,
            CoolDownDurationSeconds = this.CoolDownDurationSeconds,
            RoutingStrategy = this.RoutingStrategy,
            EnableNativeSchema = this.EnableNativeSchema,
            EnableJsonRepair = this.EnableJsonRepair,
            EmbeddingProvider = this.EmbeddingProvider,
            EmbeddingModel = this.EmbeddingModel,
            EmbeddingEndpoint = this.EmbeddingEndpoint,
            // Embedding 金鑰與 provider 金鑰採同一套加密；明文欄位明確寫 null 以清除舊資料。
            EncryptedEmbeddingApiKey = encryptedEmbeddingApiKey,
            EmbeddingApiKey = null,
            EmbeddingModels = new Dictionary<string, string>(this._embeddingModels),
            EmbeddingEndpoints = new Dictionary<string, string>(this._embeddingEndpoints),
            EncryptedEmbeddingApiKeys = encryptedEmbeddingKeys,
            CompatTakeovers = new Dictionary<string, bool>(this._compatTakeovers),
            CompatModelPins = new Dictionary<string, string>(this._compatModelPins),
            DecisionProvider = this.DecisionProvider,
            DecisionModels = new Dictionary<string, string>(this._decisionModels),
            DecisionEndpoints = new Dictionary<string, string>(this._decisionEndpoints),
            EncryptedDecisionApiKeys = encryptedDecisionKeys,
            EmbeddingFallbackChain = this.EmbeddingFallbackChain,
            DecisionFallbackChain = this.DecisionFallbackChain,
            DecisionModel = this.GetDecisionModelRaw(this.DecisionProvider),
            DecisionEndpoint = this.GetDecisionEndpointRaw(this.DecisionProvider),
            DecisionApiKey = null,
            EncryptedDecisionApiKey = null,
            SettingsVersion = CurrentSettingsVersion
        };

        return RimLLMJsonHelper.Serialize(dto);
    }

    /// <summary>
    /// 將來源金鑰與無法解密的金鑰加密為可儲存的密文字典。無法解密的金鑰若尚未在密文中，
    /// 會原樣寫回，避免換裝置後密文永久遺失。
    /// </summary>
    private static Dictionary<string, string> EncryptKeys(
        Dictionary<string, string> sourceKeys,
        Dictionary<string, string> undecryptableKeys)
    {
        var encryptedKeys = new Dictionary<string, string>();
        foreach (var kvp in sourceKeys)
        {
            if (TryEncryptForSave(kvp.Value, out string cipher))
            {
                encryptedKeys[kvp.Key] = cipher;
            }
        }
        foreach (var kvp in undecryptableKeys.Where(k => !encryptedKeys.ContainsKey(k.Key)))
        {
            encryptedKeys[kvp.Key] = kvp.Value;
        }
        return encryptedKeys;
    }

    /// <summary>
    /// 將舊版單一 embedding 金鑰遷移進密文字典，避免切換 provider 後密文遺失。
    /// </summary>
    private void AddUndecryptableEmbeddingFallback(Dictionary<string, string> encryptedEmbeddingKeys)
    {
        if (!string.IsNullOrEmpty(_undecryptableEmbeddingApiKey) &&
            !string.IsNullOrEmpty(_undecryptableEmbeddingApiKeyProvider) &&
            !_embeddingApiKeys.ContainsKey(_undecryptableEmbeddingApiKeyProvider) &&
            !encryptedEmbeddingKeys.ContainsKey(_undecryptableEmbeddingApiKeyProvider))
        {
            // 舊版只有一個 active provider 欄位；切換 provider 後改放入 map，避免密文遺失。
            encryptedEmbeddingKeys[_undecryptableEmbeddingApiKeyProvider] = _undecryptableEmbeddingApiKey;
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();

        string jsonStr = "";
        lock (_settingsLock)
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                jsonStr = BuildSettingsJson();
                Scribe_Values.Look(ref jsonStr, "SettingsData", "");
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Scribe_Values.Look(ref jsonStr, "SettingsData", "");
                if (!string.IsNullOrEmpty(jsonStr))
                {
                    try
                    {
                        var dto = RimLLMJsonHelper.Deserialize<SettingsDto>(jsonStr);
                        if (dto != null)
                        {
                            ApplySettingsDto(dto);
                        }
                    }
                    catch (Exception ex)
                    {
                        RimLLMLog.Error($"[RimLLM] Failed to load settings: {ex.Message}");
                    }
                }
            }
        }
    }

    private void ApplySettingsDto(SettingsDto dto)
    {
        ApplyProviderConfigurations(dto);
        ApplyDecryptedApiKeys(dto);
        ApplyTelemetryMigration(dto);
        ApplyGlobalPolicies(dto);
        ApplyEmbeddingConfigurations(dto);
        ApplyDecisionConfigurations(dto);
        ApplyCompatConfigurations(dto);
    }

    private void ApplyProviderConfigurations(SettingsDto dto)
    {
        if (dto.FallbackChain != null)
        {
            var fallbackChain = new List<string>(dto.FallbackChain);
            fallbackChain.RemoveAll(entry => string.IsNullOrEmpty(entry));
            this.FallbackChain = fallbackChain;
        }
        ApplyProviderMaps(dto);
        ApplyModelOverrides(dto);
        this.DefaultReasoningEffort = DecodeReasoningEffort(dto.DefaultReasoningEffort);
    }

    private void ApplyProviderMaps(SettingsDto dto)
    {
        if (dto.Endpoints != null)
        {
            foreach (var kvp in dto.Endpoints) this._endpoints[kvp.Key] = kvp.Value;
        }
        if (dto.EnabledProviders != null)
        {
            foreach (var kvp in dto.EnabledProviders) this._enabledProviders[kvp.Key] = kvp.Value;
        }
        if (dto.ProviderModels != null)
        {
            foreach (var kvp in dto.ProviderModels) this._providerModels[kvp.Key] = kvp.Value;
            _modelListVersion++;
        }
        if (dto.ChinaModeProviders != null)
        {
            foreach (var kvp in dto.ChinaModeProviders) this._chinaModeProviders[kvp.Key] = kvp.Value;
        }
    }

    private void ApplyModelOverrides(SettingsDto dto)
    {
        if (dto.ModelLevelOverrides != null)
        {
            _modelLevelOverrides.Clear();
            foreach (var kvp in dto.ModelLevelOverrides) _modelLevelOverrides[kvp.Key] = kvp.Value;
        }
        ApplyContextWindowsAndVision(dto);
        if (dto.ContextWindowOverrides != null)
        {
            _contextWindowOverrides.Clear();
            foreach (var kvp in dto.ContextWindowOverrides) SetContextWindowOverride(kvp.Key, kvp.Value);
        }
    }

    private void ApplyContextWindowsAndVision(SettingsDto dto)
    {
        if (dto.ProviderContextWindows != null)
        {
            _providerContextWindows.Clear();
            foreach (var kvp in dto.ProviderContextWindows)
            {
                if (kvp.Value != null) SetProviderContextWindows(kvp.Key, kvp.Value);
            }
        }
        if (dto.ProviderVisionSupport != null)
        {
            _providerVisionSupport.Clear();
            foreach (var kvp in dto.ProviderVisionSupport)
            {
                if (kvp.Value != null) SetProviderVisionSupport(kvp.Key, kvp.Value);
            }
        }
    }

    private void ApplyDecryptedApiKeys(SettingsDto dto)
    {
        _apiKeys.Clear();
        _undecryptableApiKeys.Clear();
        DecryptApiKeyDictionary(dto.EncryptedApiKeys, _apiKeys, _undecryptableApiKeys);
    }

    private static void DecryptApiKeyDictionary(
        Dictionary<string, string> encryptedKeys,
        Dictionary<string, string> targetDecrypted,
        Dictionary<string, string> targetUndecryptable)
    {
        if (encryptedKeys == null) return;
        foreach (var kvp in encryptedKeys)
        {
            string plain = EncryptionUtility.Decrypt(kvp.Value);
            if (plain != null)
            {
                targetDecrypted[kvp.Key] = plain;
            }
            else
            {
                targetUndecryptable[kvp.Key] = kvp.Value;
                RimLLMStartupNotices.Post("RimLLM_MsgApiKeyDecryptFailed", kvp.Key);
            }
        }
    }

    private void ApplyTelemetryMigration(SettingsDto dto)
    {
        if (_telemetry.LoadedFromDisk ||
            (dto.ChatHistory == null && dto.RequestLogs == null &&
             dto.TotalPromptTokens <= 0 && dto.TotalCompletionTokens <= 0 && dto.TotalEstimatedCost <= 0f))
        {
            return;
        }

        if (dto.ChatHistory != null) this.ChatHistory = dto.ChatHistory;
        if (dto.RequestLogs != null) this.RequestLogs = dto.RequestLogs;
        this.TotalPromptTokens = dto.TotalPromptTokens;
        this.TotalCompletionTokens = dto.TotalCompletionTokens;
        this.TotalEstimatedCost = dto.TotalEstimatedCost;
        SaveTelemetry();
        RimLLMLog.Message("[RimLLM] Migrated telemetry from the legacy settings XML to RimLLM_Telemetry.json.");
    }

    private void ApplyGlobalPolicies(SettingsDto dto)
    {
        this.ApiTimeout = dto.ApiTimeout <= 0f ? 30f : dto.ApiTimeout;
        this.MaxRetries = dto.MaxRetries < 0 ? 3 : dto.MaxRetries;
        this.RetryDelay = dto.RetryDelay < 0f ? 3f : dto.RetryDelay;
        this.DetailedLogging = dto.SettingsVersion >= 1 && dto.DetailedLogging;
        this.MaxConcurrentRequests = dto.MaxConcurrentRequests <= 0 ? 2 : dto.MaxConcurrentRequests;
        RimLLMLog.Enabled = this.DetailedLogging;

        ApplyBudgetPolicies(dto);

        this.EnableAntiAbuse = dto.EnableAntiAbuse;
        this.MaxRequestsPerWindow = dto.MaxRequestsPerWindow <= 0 ? 10 : dto.MaxRequestsPerWindow;
        this.ThrottlingWindowSeconds = dto.ThrottlingWindowSeconds <= 0 ? 10 : dto.ThrottlingWindowSeconds;
        this.CoolDownDurationSeconds = dto.CoolDownDurationSeconds < 0 ? 60 : dto.CoolDownDurationSeconds;
        this.RoutingStrategy = dto.RoutingStrategy < 0 || dto.RoutingStrategy > MaxRoutingStrategy ? 0 : dto.RoutingStrategy;
        this.EnableNativeSchema = dto.EnableNativeSchema;
        this.EnableJsonRepair = dto.EnableJsonRepair;
    }

    private void ApplyBudgetPolicies(SettingsDto dto)
    {
        this.DailyTokenBudgetLimit = dto.DailyTokenBudgetLimit < 0 ? 0 : dto.DailyTokenBudgetLimit;
        bool legacyBudgetSave = dto.DailyBudgetLimit.HasValue;
        if (legacyBudgetSave && dto.DailyBudgetLimit.Value > 0f)
        {
            RimLLMLog.Warning(
                $"[RimLLM] The daily budget is now counted in tokens; the old USD limit ({dto.DailyBudgetLimit.Value:F2}) cannot be converted and was removed. Set a token budget under Budget & Safety.");
            RimLLMStartupNotices.Post("RimLLM_MsgLegacyBudgetLimitDropped");
        }

        int storedPolicy = dto.BudgetPolicy;
        bool legacySilentMocking = legacyBudgetSave && storedPolicy == 1;
        if (legacySilentMocking || SanitizeBudgetPolicy(storedPolicy) != storedPolicy)
        {
            RimLLMLog.Warning(
                $"[RimLLM] Unsupported budget policy {storedPolicy} in settings; reset to HardBlock (0).");
            RimLLMStartupNotices.Post("RimLLM_MsgBudgetPolicyReset");
        }
        this.BudgetPolicy = legacySilentMocking ? 0 : SanitizeBudgetPolicy(storedPolicy);
    }

    private void ApplyEmbeddingConfigurations(SettingsDto dto)
    {
        ApplyEmbeddingModelsAndEndpoints(dto);
        ApplyEmbeddingApiKeys(dto);
    }

    private void ApplyEmbeddingModelsAndEndpoints(SettingsDto dto)
    {
        this.EmbeddingProvider = string.IsNullOrEmpty(dto.EmbeddingProvider) ? DisabledProvider : dto.EmbeddingProvider;
        if (dto.EmbeddingModels != null)
        {
            foreach (var kvp in dto.EmbeddingModels) this._embeddingModels[kvp.Key] = kvp.Value;
        }
        else if (!string.IsNullOrEmpty(dto.EmbeddingModel) && this.EmbeddingProvider != DisabledProvider)
        {
            this._embeddingModels[this.EmbeddingProvider] = dto.EmbeddingModel;
        }

        if (dto.EmbeddingEndpoints != null)
        {
            foreach (var kvp in dto.EmbeddingEndpoints) this._embeddingEndpoints[kvp.Key] = kvp.Value;
        }
        else if (!string.IsNullOrEmpty(dto.EmbeddingEndpoint) && this.EmbeddingProvider != DisabledProvider)
        {
            this._embeddingEndpoints[this.EmbeddingProvider] = dto.EmbeddingEndpoint;
        }

        if (dto.EmbeddingFallbackChain != null) this.EmbeddingFallbackChain = dto.EmbeddingFallbackChain;
    }

    private void ApplyEmbeddingApiKeys(SettingsDto dto)
    {
        _embeddingApiKeys.Clear();
        _undecryptableEmbeddingApiKeys.Clear();
        _undecryptableEmbeddingApiKey = null;
        _undecryptableEmbeddingApiKeyProvider = null;
        if (dto.EncryptedEmbeddingApiKeys != null)
        {
            DecryptApiKeyDictionary(dto.EncryptedEmbeddingApiKeys, _embeddingApiKeys, _undecryptableEmbeddingApiKeys);
        }
        else if (!string.IsNullOrEmpty(dto.EncryptedEmbeddingApiKey))
        {
            string embeddingProvider = this.EmbeddingProvider;
            string plain = EncryptionUtility.Decrypt(dto.EncryptedEmbeddingApiKey);
            if (plain != null && embeddingProvider != DisabledProvider)
            {
                _embeddingApiKeys[embeddingProvider] = plain;
            }
            else if (plain == null && !string.IsNullOrEmpty(embeddingProvider) && embeddingProvider != DisabledProvider)
            {
                _undecryptableEmbeddingApiKey = dto.EncryptedEmbeddingApiKey;
                _undecryptableEmbeddingApiKeyProvider = embeddingProvider;
            }
        }
        else if (!string.IsNullOrEmpty(dto.EmbeddingApiKey) && this.EmbeddingProvider != DisabledProvider)
        {
            _embeddingApiKeys[this.EmbeddingProvider] = dto.EmbeddingApiKey;
        }
    }

    private void ApplyDecisionConfigurations(SettingsDto dto)
    {
        string decisionProvider = SanitizeDecisionProvider(dto.DecisionProvider);
        ApplyDecisionModelsAndEndpoints(dto, decisionProvider);
        ApplyDecisionApiKeys(dto, decisionProvider);
    }

    private void ApplyDecisionModelsAndEndpoints(SettingsDto dto, string decisionProvider)
    {
        if (dto.DecisionFallbackChain != null) this.DecisionFallbackChain = dto.DecisionFallbackChain;

        this.DecisionProvider = decisionProvider;

        _decisionModels.Clear();
        _decisionEndpoints.Clear();
        if (dto.DecisionModels != null)
        {
            foreach (var kvp in dto.DecisionModels) _decisionModels[kvp.Key] = kvp.Value;
        }
        else if (!string.IsNullOrEmpty(dto.DecisionModel) && decisionProvider != RimLLMDecisionProviderCatalog.Disabled)
        {
            _decisionModels[decisionProvider] = dto.DecisionModel;
        }

        if (dto.DecisionEndpoints != null)
        {
            foreach (var kvp in dto.DecisionEndpoints) _decisionEndpoints[kvp.Key] = kvp.Value;
        }
        else if (!string.IsNullOrEmpty(dto.DecisionEndpoint) && decisionProvider != RimLLMDecisionProviderCatalog.Disabled)
        {
            _decisionEndpoints[decisionProvider] = dto.DecisionEndpoint;
        }
    }

    private void ApplyDecisionApiKeys(SettingsDto dto, string decisionProvider)
    {
        _decisionApiKeys.Clear();
        _undecryptableDecisionApiKeys.Clear();
        if (dto.EncryptedDecisionApiKeys != null)
        {
            DecryptApiKeyDictionary(dto.EncryptedDecisionApiKeys, _decisionApiKeys, _undecryptableDecisionApiKeys);
        }

        ApplyLegacyDecisionApiKey(dto, decisionProvider);
    }

    private void ApplyLegacyDecisionApiKey(SettingsDto dto, string decisionProvider)
    {
        if (decisionProvider == RimLLMDecisionProviderCatalog.Disabled ||
            _decisionApiKeys.ContainsKey(decisionProvider) ||
            _undecryptableDecisionApiKeys.ContainsKey(decisionProvider))
        {
            return;
        }

        if (!string.IsNullOrEmpty(dto.EncryptedDecisionApiKey))
        {
            string plain = EncryptionUtility.Decrypt(dto.EncryptedDecisionApiKey);
            if (plain != null)
            {
                _decisionApiKeys[decisionProvider] = plain;
            }
            else
            {
                _undecryptableDecisionApiKeys[decisionProvider] = dto.EncryptedDecisionApiKey;
            }
        }
        else if (!string.IsNullOrEmpty(dto.DecisionApiKey))
        {
            _decisionApiKeys[decisionProvider] = dto.DecisionApiKey;
        }
    }

    private void ApplyCompatConfigurations(SettingsDto dto)
    {
        _compatTakeovers.Clear();
        if (dto.CompatTakeovers != null)
        {
            foreach (var kvp in dto.CompatTakeovers) _compatTakeovers[kvp.Key] = kvp.Value;
        }

        _compatModelPins.Clear();
        if (dto.CompatModelPins != null)
        {
            foreach (var kvp in dto.CompatModelPins.Where(kv => !string.IsNullOrEmpty(kv.Value)))
            {
                _compatModelPins[kvp.Key] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// 設定檔中的預算策略值正規化。已移除的策略（SilentMocking / FallbackToFree / DialogPrompt）
    /// 與未知值一律退回 HardBlock：不得靜默變成放行，否則舊存檔升級後會在無預警的情況下繼續燒 token。
    /// 舊版 SilentMocking 的值 1 與新版 WarnOnly 撞號，單看數值無法分辨，由載入處依舊版存檔另行處理。
    /// </summary>
    internal static int SanitizeBudgetPolicy(int stored)
    {
        return stored < 0 || stored > MaxBudgetPolicy ? 0 : stored;
    }

    /// <summary>
    /// 設定檔中的決策供應商 id 正規化。空值保持 Disabled；未知 id（手動編輯或跨版本搬運）也退回 Disabled：
    /// <see cref="DecisionProvider"/> 欄位本身承載啟用狀態，不得擅自把未知供應商換成別家已啟用，
    /// 否則一座沒有決策服務的存檔會帶著「TypeSafe 已啟用、但沒有金鑰」的狀態回到遊戲。
    /// </summary>
    internal static string SanitizeDecisionProvider(string stored)
    {
        if (string.IsNullOrEmpty(stored) ||
            !RimLLMDecisionProviderCatalog.TryGet(stored, out _))
        {
            return RimLLMDecisionProviderCatalog.Disabled;
        }
        return stored;
    }

    /// <summary>
    /// 存檔路徑專用的加密：失敗時回傳 false 並略過該金鑰，而不是讓例外穿出 <see cref="ExposeData"/>。
    /// 這裡是在 Scribe 存檔中途被呼叫的，例外一旦冒出去，整份設定（備援鏈、端點、開關）都會存不下來；
    /// 系統鑰匙圈可能鎖定或無法連線，DPAPI 也可能因使用者設定檔不可讀寫而失敗。
    /// 金鑰仍留在記憶體中，加密恢復正常後的下一次存檔就會寫回。
    /// </summary>
    internal static bool TryEncryptForSave(string plainText, out string cipherText)
    {
        try
        {
            cipherText = EncryptionUtility.Encrypt(plainText);
            return true;
        }
        catch (Exception ex)
        {
            cipherText = null;
            RimLLMLog.Error($"[RimLLM] API key could not be encrypted and was not written to settings; other settings were saved. Reason: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 預設思考強度的存檔編碼：0 代表 Auto（null），其餘為列舉值 + 1。
    /// 與既有設定檔相容：舊檔的 2／3 仍讀成 Low／Medium。
    /// </summary>
    internal static int EncodeReasoningEffort(ReasoningEffort? effort)
    {
        return effort == null ? 0 : (int)effort.Value + 1;
    }

    /// <summary>
    /// <see cref="EncodeReasoningEffort"/> 的反向。合法範圍以列舉實際定義為準，不再寫死上限：
    /// MEAI 10.10 的 ReasoningEffort 是 None=0…ExtraHigh=4，先前寫死的「&gt; 3 即無效」
    /// 讓 High（存成 4）每次載入都被丟回 Auto。
    /// </summary>
    internal static ReasoningEffort? DecodeReasoningEffort(int encoded)
    {
        if (encoded <= 0) return null;
        int value = encoded - 1;
        return Enum.IsDefined(typeof(ReasoningEffort), value) ? (ReasoningEffort?)(ReasoningEffort)value : null;
    }
}
