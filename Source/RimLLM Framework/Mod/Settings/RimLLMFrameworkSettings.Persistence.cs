using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Manager.Services;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 設定的存檔與讀檔：DTO 結構、金鑰加解密與 Scribe 讀寫。
/// </summary>
public partial class RimLLMFrameworkSettings
{
    /// <summary>JSON 序列化用的 DTO，避開 RimWorld Scribe 字典嵌套序列化的兼容問題。</summary>
    private sealed class SettingsDto
    {
        // 以下欄位由 System.Text.Json 反序列化時以反射填入；宣告時尚未有值，故以 null! 標示（未出現在 JSON 中的欄位保持 null）。
        public List<string> FallbackChain = null!;
        public Dictionary<string, string> EncryptedApiKeys = null!;
        public Dictionary<string, string> Endpoints = null!;
        public Dictionary<string, bool> EnabledProviders = null!;
        public Dictionary<string, List<string>> ProviderModels = null!;
        public Dictionary<string, bool> ChinaModeProviders = null!;
        public Dictionary<string, int> ModelLevelOverrides = null!;
        public Dictionary<string, Dictionary<string, int>> ProviderContextWindows = null!;
        public Dictionary<string, Dictionary<string, bool>> ProviderVisionSupport = null!;
        public Dictionary<string, Dictionary<string, int>> ProviderOutputLimits = null!;
        public Dictionary<string, int> ContextWindowOverrides = null!;
        public float ApiTimeout;
        public int MaxRetries;
        public float RetryDelay;
        public bool DetailedLogging;
        public int MaxConcurrentRequests;
        public int DefaultReasoningEffort;
        public long DailyTokenBudgetLimit;
        public int BudgetPolicy;
        public bool EnableAntiAbuse;
        public int MaxRequestsPerWindow;
        public int ThrottlingWindowSeconds;
        public int CoolDownDurationSeconds;
        public int RoutingStrategy = 0;
        public bool EnableNativeSchema = true;
        public bool EnableJsonRepair = true;
        public string EmbeddingProvider = DisabledProvider;
        public Dictionary<string, string> EmbeddingModels = null!;
        public Dictionary<string, string> EmbeddingEndpoints = null!;
        public Dictionary<string, string> EncryptedEmbeddingApiKeys = null!;
        public Dictionary<string, bool> CompatTakeovers = null!;
        public Dictionary<string, string> CompatModelPins = null!;
        public string DecisionProvider = RimLLMDecisionProviderCatalog.Disabled;
        public Dictionary<string, string> DecisionModels = null!;
        public Dictionary<string, string> DecisionEndpoints = null!;
        public Dictionary<string, string> EncryptedDecisionApiKeys = null!;
        public List<string> EmbeddingFallbackChain = null!;
        public List<string> DecisionFallbackChain = null!;
    }

    private string BuildSettingsJson()
    {
        var encryptedKeys = EncryptKeys(_apiKeys, _undecryptableApiKeys);
        var encryptedEmbeddingKeys = EncryptKeys(_embeddingApiKeys, _undecryptableEmbeddingApiKeys);

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
            ModelLevelOverrides = new Dictionary<string, int>(this._modelLevelOverrides, StringComparer.Ordinal),
            ProviderContextWindows = this._providerContextWindows,
            ProviderVisionSupport = this._providerVisionSupport,
            ProviderOutputLimits = this._providerOutputLimits,
            ContextWindowOverrides = new Dictionary<string, int>(this._contextWindowOverrides, StringComparer.Ordinal),
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
            EmbeddingModels = new Dictionary<string, string>(this._embeddingModels, StringComparer.Ordinal),
            EmbeddingEndpoints = new Dictionary<string, string>(this._embeddingEndpoints, StringComparer.Ordinal),
            EncryptedEmbeddingApiKeys = encryptedEmbeddingKeys,
            CompatTakeovers = new Dictionary<string, bool>(this._compatTakeovers, StringComparer.Ordinal),
            CompatModelPins = new Dictionary<string, string>(this._compatModelPins, StringComparer.Ordinal),
            DecisionProvider = this.DecisionProvider,
            DecisionModels = new Dictionary<string, string>(this._decisionModels, StringComparer.Ordinal),
            DecisionEndpoints = new Dictionary<string, string>(this._decisionEndpoints, StringComparer.Ordinal),
            EncryptedDecisionApiKeys = encryptedDecisionKeys,
            EmbeddingFallbackChain = this.EmbeddingFallbackChain,
            DecisionFallbackChain = this.DecisionFallbackChain
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
        var encryptedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in sourceKeys)
        {
            if (TryEncryptForSave(kvp.Value, out string? cipher))
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
        if (dto.ProviderOutputLimits != null)
        {
            _providerOutputLimits.Clear();
            foreach (var kvp in dto.ProviderOutputLimits)
            {
                if (kvp.Value != null) SetProviderOutputLimits(kvp.Key, kvp.Value);
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
            string? plain = EncryptionUtility.Decrypt(kvp.Value);
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

    private void ApplyGlobalPolicies(SettingsDto dto)
    {
        this.ApiTimeout = dto.ApiTimeout <= 0f ? 30f : dto.ApiTimeout;
        this.MaxRetries = dto.MaxRetries < 0 ? 3 : dto.MaxRetries;
        this.RetryDelay = dto.RetryDelay < 0f ? 3f : dto.RetryDelay;
        this.DetailedLogging = dto.DetailedLogging;
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
        int storedPolicy = dto.BudgetPolicy;
        if (SanitizeBudgetPolicy(storedPolicy) != storedPolicy)
        {
            RimLLMLog.Warning(
                string.Create(CultureInfo.InvariantCulture, $"[RimLLM] Unsupported budget policy {storedPolicy} in settings; reset to HardBlock (0)."));
            RimLLMStartupNotices.Post("RimLLM_MsgBudgetPolicyReset");
        }
        this.BudgetPolicy = SanitizeBudgetPolicy(storedPolicy);
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

        if (dto.EmbeddingEndpoints != null)
        {
            foreach (var kvp in dto.EmbeddingEndpoints) this._embeddingEndpoints[kvp.Key] = kvp.Value;
        }

        if (dto.EmbeddingFallbackChain != null) this.EmbeddingFallbackChain = dto.EmbeddingFallbackChain;
    }

    private void ApplyEmbeddingApiKeys(SettingsDto dto)
    {
        _embeddingApiKeys.Clear();
        _undecryptableEmbeddingApiKeys.Clear();
        DecryptApiKeyDictionary(dto.EncryptedEmbeddingApiKeys, _embeddingApiKeys, _undecryptableEmbeddingApiKeys);
    }

    private void ApplyDecisionConfigurations(SettingsDto dto)
    {
        string decisionProvider = SanitizeDecisionProvider(dto.DecisionProvider);
        ApplyDecisionModelsAndEndpoints(dto, decisionProvider);
        ApplyDecisionApiKeys(dto);
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

        if (dto.DecisionEndpoints != null)
        {
            foreach (var kvp in dto.DecisionEndpoints) _decisionEndpoints[kvp.Key] = kvp.Value;
        }
    }

    private void ApplyDecisionApiKeys(SettingsDto dto)
    {
        _decisionApiKeys.Clear();
        _undecryptableDecisionApiKeys.Clear();
        DecryptApiKeyDictionary(dto.EncryptedDecisionApiKeys, _decisionApiKeys, _undecryptableDecisionApiKeys);
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
    /// 設定檔中的預算策略值正規化。未知值一律退回 HardBlock：
    /// 不得靜默變成放行，否則手動編輯過的設定會在無預警的情況下繼續燒 token。
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
    internal static bool TryEncryptForSave(string plainText, [NotNullWhen(true)] out string? cipherText)
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

    /// <summary><see cref="EncodeReasoningEffort"/> 的反向；合法範圍以列舉實際定義為準，不寫死上限（MEAI 的 ReasoningEffort 是 None=0…ExtraHigh=4）。</summary>
    internal static ReasoningEffort? DecodeReasoningEffort(int encoded)
    {
        if (encoded <= 0) return null;
        int value = encoded - 1;
        return Enum.IsDefined(typeof(ReasoningEffort), value) ? (ReasoningEffort?)(ReasoningEffort)value : null;
    }
}
