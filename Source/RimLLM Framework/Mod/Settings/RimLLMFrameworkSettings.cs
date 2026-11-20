#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Manager.Services;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// RimWorld Mod 設定檔。
/// 將複雜的字典結構序列化為單一 JSON 字串儲存，並在序列化時調用 EncryptionUtility 加解密 API 金鑰。
/// </summary>
public partial class RimLLMFrameworkSettings : ModSettings, IRimLLMSettings
{
    /// <summary>
    /// API 供應商的 Fallback Chain 順序。
    /// </summary>
    private List<string> _fallbackChain = [];

    public List<string> FallbackChain
    {
        get => SnapshotFallbackChain();
        set
        {
            lock (_settingsLock)
            {
                _fallbackChain = value != null ? new List<string>(value) : [];
            }
        }
    }

    /// <summary>
    /// 取得備援鏈的複本。屬性不直接複製集合，複製邏輯集中在這裡，行為與原本一致。
    /// </summary>
    private List<string> SnapshotFallbackChain()
    {
        lock (_settingsLock)
        {
            return new List<string>(_fallbackChain);
        }
    }

    private List<string> _embeddingFallbackChain = [];
    private List<string> _decisionFallbackChain = [];

    /// <summary>Embedding 備援鏈（供應商識別碼，不含啟用中那一家）。取得的是複本，改完要整份設回來。</summary>
    public List<string> EmbeddingFallbackChain
    {
        get => SnapshotEmbeddingFallbackChain();
        set { lock (_settingsLock) { _embeddingFallbackChain = SanitizeProviderChain(value); } }
    }

    /// <summary>決策備援鏈（供應商識別碼，不含啟用中那一家）。取得的是複本，改完要整份設回來。</summary>
    public List<string> DecisionFallbackChain
    {
        get => SnapshotDecisionFallbackChain();
        set { lock (_settingsLock) { _decisionFallbackChain = SanitizeProviderChain(value); } }
    }

    /// <summary>取得 Embedding 備援鏈的複本。</summary>
    private List<string> SnapshotEmbeddingFallbackChain()
    {
        lock (_settingsLock)
        {
            return new List<string>(_embeddingFallbackChain);
        }
    }

    /// <summary>取得決策備援鏈的複本。</summary>
    private List<string> SnapshotDecisionFallbackChain()
    {
        lock (_settingsLock)
        {
            return new List<string>(_decisionFallbackChain);
        }
    }

    /// <summary>備援鏈去空去重；存檔裡手改出來的重複或空白條目不該讓同一家被嘗試兩次。</summary>
    private static List<string> SanitizeProviderChain(IEnumerable<string> chain)
    {
        var result = new List<string>();
        if (chain == null) return result;
        foreach (string entry in chain.Where(entry => !string.IsNullOrWhiteSpace(entry) && !result.Contains(entry)))
        {
            result.Add(entry);
        }
        return result;
    }

    // 可調節項 (全域配置)
    // 全域請求配置的預設值；屬性初始值與「恢復預設」按鈕共用，避免兩處各寫一份而漂移。
    internal const float DefaultApiTimeout = 30f;
    internal const int DefaultMaxRetries = 3;
    internal const float DefaultRetryDelay = 3f;
    internal const int DefaultMaxConcurrentRequests = 2;

    /// <summary>API 逾時時間（秒）。</summary>
    public float ApiTimeout { get; set; } = DefaultApiTimeout;

    /// <summary>單模型最多重試次數。</summary>
    public int MaxRetries { get; set; } = DefaultMaxRetries;

    /// <summary>重試間隔時間（秒）。</summary>
    public float RetryDelay { get; set; } = DefaultRetryDelay;

    /// <summary>
    /// 是否啟用詳細日誌。預設關閉：每次請求都會寫日誌，而 Verse 的日誌有全域 10000 筆上限，
    /// 被高頻 Mod 使用時長時間遊玩會把上限用完，之後所有 Mod 的錯誤都不再被記錄。
    /// </summary>
    public bool DetailedLogging { get; set; } = false;

    /// <summary>最大並行請求數限制。</summary>
    public int MaxConcurrentRequests { get; set; } = DefaultMaxConcurrentRequests;

    /// <summary>全域預設思考程度（Reasoning Effort）。</summary>
    public ReasoningEffort? DefaultReasoningEffort { get; set; } = null;

    // 遙測資料（對話歷史、請求日誌、用量統計）獨立存放於 JSON 檔案，不寫入設定 XML
    private readonly RimLLMTelemetryStore _telemetry = new();
    public List<string> ChatHistory
    {
        get => _telemetry.ChatHistory;
        set => _telemetry.ChatHistory = value ?? [];
    }
    public List<RimLLMManager.RequestLogEntry> RequestLogs
    {
        get => _telemetry.RequestLogs;
        set => _telemetry.RequestLogs = value ?? [];
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

    /// <summary>每日 token 預算上限（輸入＋輸出合計）。0 代表無限制，存於設定 XML。</summary>
    public long DailyTokenBudgetLimit { get; set; } = 0;

    /// <summary>
    /// 最大合法的預算超限應對策略值。新增策略時要同步更新這裡與
    /// <see cref="BudgetSettingsDrawer"/> 的策略名稱清單。
    /// </summary>
    public const int MaxBudgetPolicy = 1; // 0=HardBlock, 1=WarnOnly

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

    public int RoutingStrategy { get; set; } = 0;
    public bool EnableNativeSchema { get; set; } = true;
    public bool EnableJsonRepair { get; set; } = true;

    public const string DisabledProvider = "Disabled";
    public string EmbeddingProvider { get; set; } = DisabledProvider;

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

    /// <summary>
    /// 目前啟用的決策模型供應商。預設停用，與 Embedding 模組一致。
    /// </summary>
    public string DecisionProvider { get; set; } = RimLLMDecisionProviderCatalog.Disabled;

    public string DecisionModel
    {
        get => GetDecisionModel(DecisionProvider);
        set => SetDecisionModel(DecisionProvider, value);
    }

    public string DecisionEndpoint
    {
        get => GetDecisionEndpoint(DecisionProvider);
        set => SetDecisionEndpoint(DecisionProvider, value);
    }

    public string DecisionApiKey
    {
        get => GetDecisionApiKey(DecisionProvider);
        set => SetDecisionApiKey(DecisionProvider, value);
    }

    public float DailyAccumulatedCost
    {
        get => _telemetry.DailyAccumulatedCost;
        set => _telemetry.DailyAccumulatedCost = value;
    }
    /// <summary>當日已累計 token（輸入＋輸出合計），存於遙測檔，每日跨天重置。</summary>
    public long DailyAccumulatedTokens
    {
        get => _telemetry.DailyAccumulatedTokens;
        set => _telemetry.DailyAccumulatedTokens = value;
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
    /// 把遙測寫檔交給背景單寫者，不在呼叫端執行緒做 AES + JSON + 磁碟寫入。
    /// 先標記待寫入：背景寫檔失敗或來不及完成時，由關閉時的 <see cref="FlushTelemetryIfDirty"/> 補上。
    /// </summary>
    public void QueueTelemetrySave()
    {
        _telemetry.MarkDirty();
        _telemetry.QueueSave();
    }

    /// <summary>
    /// 清空對話歷史；若先前因 key 不可用而保留了密文，也一併明確捨棄。
    /// </summary>
    public void ClearChatHistory()
    {
        _telemetry.ClearChatHistory();
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

    private readonly object _settingsLock = new();
    private readonly Dictionary<string, string> _apiKeys = [];

    /// <summary>
    /// 載入時無法解密的 provider 金鑰密文（providerId → 原始密文）。
    /// 存檔時原樣寫回，避免無法取得原使用者的 OS 保護金鑰時被靜默清空。
    /// </summary>
    private readonly Dictionary<string, string> _undecryptableApiKeys = [];
    private readonly Dictionary<string, int> _apiKeyIndices = [];
    private readonly Dictionary<string, string> _endpoints = [];
    private readonly Dictionary<string, int> _modelLevelOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 重新整理模型清單時 API 回報的上下文上限（providerId → 模型名稱 → token 數）。
    /// 以供應商分開存放：同名模型在不同供應商的上限可能不同。
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, int>> _providerContextWindows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 重新整理模型清單時得知的圖片輸入支援（providerId → 模型名稱 → 是否支援）。
    /// 只記有明確資料的模型；沒有的不存，查詢時以 null 表示未知。
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, bool>> _providerVisionSupport = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>玩家手動填寫的上下文上限（"Provider:Model" → token 數），優先於 API 回報的值。</summary>
    private readonly Dictionary<string, int> _contextWindowOverrides = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _embeddingModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _embeddingEndpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _embeddingApiKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _undecryptableEmbeddingApiKeys = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _decisionModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _decisionEndpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _decisionApiKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _undecryptableDecisionApiKeys = new(StringComparer.Ordinal);

    /// <summary>中國端點切換旗標。鍵由 <see cref="ProviderIds.HasChinaEndpoint"/> 決定，不另外手寫一份清單。</summary>
    private readonly Dictionary<string, bool> _chinaModeProviders = BuildChinaModeMap();

    private static Dictionary<string, bool> BuildChinaModeMap()
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string providerId in ProviderIds.BuiltIn.Where(ProviderIds.HasChinaEndpoint))
        {
            map[providerId] = false;
        }
        return map;
    }

    /// <summary>
    /// 內建供應商的啟用旗標與模型清單快取。兩份字典的鍵一律等同 <see cref="ProviderIds.BuiltIn"/>，
    /// 因此直接由該清單產生 —— 先前是各自手寫一份 12 筆的字面清單，新增供應商得同步改三處。
    /// </summary>
    private readonly Dictionary<string, bool> _enabledProviders = BuildBuiltInMap(_ => false);

    private readonly Dictionary<string, List<string>> _providerModels = BuildBuiltInMap(_ => new List<string>());

    /// <summary>
    /// 模型清單的變更版本號。清單只在抓取模型或載入設定時變動，設定頁以此判斷
    /// 過濾結果快取是否仍有效，不必每幀複製整份清單來比對。
    /// </summary>
    private int _modelListVersion;
    public int ModelListVersion
    {
        get { lock (_settingsLock) { return _modelListVersion; } }
    }

    /// <summary>
    /// 第三方 Mod 強制接管登錄表（packageId → 是否把該 Mod 的 LLM 流量導向 RimLLM）。
    /// 預設全關；沒有登錄的 Mod 一律視為關閉。鍵由 Compat 層各目標自行宣告。
    /// </summary>
    private readonly Dictionary<string, bool> _compatTakeovers = new(StringComparer.Ordinal);

    /// <summary>
    /// 相容 Mod 的專用模型登錄表（packageId → "供應商:模型" 條目）。
    /// 沒有登錄的 Mod 不釘選，沿用預設備援鏈。
    /// </summary>
    private readonly Dictionary<string, string> _compatModelPins = new(StringComparer.Ordinal);

    private static Dictionary<string, TValue> BuildBuiltInMap<TValue>(Func<string, TValue> valueFactory)
    {
        var map = new Dictionary<string, TValue>(StringComparer.Ordinal);
        foreach (string providerId in ProviderIds.BuiltIn)
        {
            map[providerId] = valueFactory(providerId);
        }
        return map;
    }

    public string GetApiKey(string providerId)
    {
        lock (_settingsLock)
        {
            return _apiKeys.TryGetValue(providerId, out string val) ? val : "";
        }
    }

    /// <summary>
    /// 多把金鑰的分隔字元。輪替與設定頁的金鑰列表必須用同一組：
    /// 先前設定頁只認逗號，含分號的金鑰字串在畫面上是一把、實際卻被當成兩把輪替。
    /// </summary>
    internal static readonly char[] ApiKeySeparators = [',', ';'];

    public string GetActiveApiKey(string providerId)
    {
        lock (_settingsLock)
        {
            if (!_apiKeys.TryGetValue(providerId, out string raw) || string.IsNullOrEmpty(raw)) return "";

            var keys = raw.Split(ApiKeySeparators, StringSplitOptions.RemoveEmptyEntries);
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
        return providerId switch
        {
            ProviderIds.MiniMax => isChina ? "https://api.minimaxi.com/v1" : "https://api.minimax.io/v1",
            ProviderIds.Qwen => isChina
                                    ? "https://dashscope.aliyuncs.com/compatible-mode/v1"
                                    : "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
            ProviderIds.Kimi => isChina ? "https://api.moonshot.cn/v1" : "https://api.moonshot.ai/v1",
            _ => null,
        };
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

    /// <summary>指定第三方 Mod 是否被強制接管。未登錄一律 false。</summary>
    public bool IsCompatTakeoverEnabled(string modId)
    {
        lock (_settingsLock)
        {
            return !string.IsNullOrEmpty(modId) && _compatTakeovers.TryGetValue(modId, out bool enabled) && enabled;
        }
    }

    public void SetCompatTakeoverEnabled(string modId, bool enabled)
    {
        if (string.IsNullOrEmpty(modId)) return;
        lock (_settingsLock)
        {
            _compatTakeovers[modId] = enabled;
        }
    }

    /// <summary>取得相容 Mod 專用的 "供應商:模型" 條目；未指定回傳 null。</summary>
    public string GetCompatModelPin(string modId)
    {
        lock (_settingsLock)
        {
            return !string.IsNullOrEmpty(modId) && _compatModelPins.TryGetValue(modId, out string pin) ? pin : null;
        }
    }

    /// <summary>設定相容 Mod 專用模型；傳入空值代表清除，回到預設備援鏈。</summary>
    public void SetCompatModelPin(string modId, string entry)
    {
        if (string.IsNullOrEmpty(modId)) return;
        lock (_settingsLock)
        {
            if (string.IsNullOrEmpty(entry)) _compatModelPins.Remove(modId);
            else _compatModelPins[modId] = entry;
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

    /// <summary>
    /// 「等待與重試」分頁的「全部恢復預設」：只重設該頁的欄位，不動供應商、金鑰與備援鏈。
    /// 呼叫端負責 <see cref="ModSettings.Write"/>。
    /// </summary>
    public void ResetRequestSettingsToDefaults()
    {
        ApiTimeout = DefaultApiTimeout;
        MaxRetries = DefaultMaxRetries;
        RetryDelay = DefaultRetryDelay;
        MaxConcurrentRequests = DefaultMaxConcurrentRequests;
        EnableNativeSchema = true;
        EnableJsonRepair = true;
        DefaultReasoningEffort = null;
    }

    /// <summary>
    /// 首頁的「恢復預設」：所有設定（供應商、金鑰、備援鏈、Embedding、決策、逾時、預算上限…）回到剛安裝的狀態。
    /// 以一份新建的預設實例逐欄覆寫，之後新增的欄位也不會漏掉；集合欄位就地清空再填入，
    /// 外部持有的參考維持有效。遙測（用量統計、對話歷史、當日預算累計）不屬於設定，保留不動，
    /// 免得重設成為繞過每日預算的方法。呼叫端負責 <see cref="ModSettings.Write"/>。
    /// </summary>
    #pragma warning disable S3011 // 「恢復預設」需透過反射遍歷類別所有欄位並重設，故意存取 private 欄位；僅限本類別內部使用，不外漏反射結果。
    public void ResetAllSettingsToDefaults()
    {
        var defaults = new RimLLMFrameworkSettings();
        lock (_settingsLock)
        {
            foreach (FieldInfo field in typeof(RimLLMFrameworkSettings).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (field.Name is nameof(_settingsLock) or nameof(_telemetry) or nameof(_modelListVersion)) continue;
                ResetField(field, defaults);
            }
            // 模型清單已清空：讓設定頁的過濾快取失效。
            _modelListVersion++;
        }
    }
    #pragma warning restore S3011

    /// <summary>
    /// 將單一欄位重設為預設值。字典與清單就地覆寫（保留參考），其餘欄位直接賦值。
    /// 僅限 <see cref="ResetAllSettingsToDefaults"/> 內部呼叫。
    /// </summary>
    private void ResetField(FieldInfo field, RimLLMFrameworkSettings defaults)
    {
        object fresh = field.GetValue(defaults);
        object current = field.GetValue(this);
        if (current is IDictionary dictionary && fresh is IDictionary freshDictionary)
        {
            dictionary.Clear();
            foreach (DictionaryEntry entry in freshDictionary) dictionary[entry.Key] = entry.Value;
        }
        else if (current is IList list && fresh is IList freshList && field.IsInitOnly)
        {
            list.Clear();
            foreach (object item in freshList) list.Add(item);
        }
        else if (!field.IsInitOnly)
        {
            field.SetValue(this, fresh);
        }
    }

    public List<string> GetModelList(string providerId)
    {
        lock (_settingsLock)
        {
            if (_providerModels.TryGetValue(providerId, out List<string> models))
                return new List<string>(models); // 回傳複本，保證安全
            return [];
        }
    }

    /// <summary>
    /// 只讀第一筆，不複製整份清單：候選解析對每個純供應商條目都會呼叫一次，
    /// 相容層每秒輪詢也走這裡，OpenRouter 動輒數百筆模型的清單不該每次都複製。
    /// </summary>
    public string GetDefaultModel(string providerId, string defaultVal)
    {
        lock (_settingsLock)
        {
            return _providerModels.TryGetValue(providerId, out List<string> models) && models.Count > 0
                ? models[0]
                : defaultVal;
        }
    }

    /// <summary>只讀筆數，供設定頁每幀顯示用，不複製清單。</summary>
    public int GetModelCount(string providerId)
    {
        lock (_settingsLock)
        {
            return _providerModels.TryGetValue(providerId, out List<string> models) ? models.Count : 0;
        }
    }

    public void SetModelList(string providerId, List<string> models)
    {
        lock (_settingsLock)
        {
            _providerModels[providerId] = models != null ? new List<string>(models) : [];
            _modelListVersion++;
        }
    }

    /// <summary>
    /// 從備援鏈移除條目（含舊存檔殘留的重複），並清掉它專屬的上下文上限覆寫；
    /// 分級覆寫以模型名稱為鍵、可能被其他供應商的同名模型共用，只在鏈上已無同名模型時才清。
    /// </summary>
    public void RemoveFallbackEntry(string entry)
    {
        if (string.IsNullOrEmpty(entry)) return;
        lock (_settingsLock)
        {
            _fallbackChain.RemoveAll(e => e == entry);
            _contextWindowOverrides.Remove(entry);

            if (!ProviderIds.TrySplitModelId(entry, out _, out string modelName)) return;
            bool stillUsed = _fallbackChain.Exists(e =>
                ProviderIds.TrySplitModelId(e, out _, out string otherModel) && otherModel == modelName);
            if (!stillUsed) _modelLevelOverrides.Remove(modelName);
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
            if (ProviderIds.TrySplitModelId(modelName, out _, out string stripped) &&
                _modelLevelOverrides.TryGetValue(stripped, out level))
            {
                return level;
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

    public int? GetContextWindow(string providerId, string modelName)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelName)) return null;
        lock (_settingsLock)
        {
            if (_contextWindowOverrides.TryGetValue(providerId + ":" + modelName, out int manual))
            {
                return manual;
            }
            if (_providerContextWindows.TryGetValue(providerId, out var fetched)
                && fetched.TryGetValue(modelName, out int reported))
            {
                return reported;
            }
            return null;
        }
    }

    /// <summary>
    /// 把本次重新整理拉到的上限併入該供應商的既有值：新值覆寫舊值，本次沒拉到的保留。
    /// 不整份取代，是因為 models.dev 或 Gemini 原生端點偶發失敗時本次結果會缺漏，
    /// 整份取代會把先前拿到的有效值一起清掉；已下架模型殘留舊值則無害。
    /// </summary>
    public void SetProviderContextWindows(string providerId, Dictionary<string, int> contextWindows)
    {
        if (string.IsNullOrEmpty(providerId)) return;
        lock (_settingsLock)
        {
            if (!_providerContextWindows.TryGetValue(providerId, out var map))
            {
                map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _providerContextWindows[providerId] = map;
            }
            if (contextWindows != null)
            {
                foreach (var kvp in contextWindows)
                {
                    if (kvp.Value > 0) map[kvp.Key] = kvp.Value;
                }
            }
        }
    }

    /// <summary>
    /// 併入本次重新整理得知的圖片輸入支援，語意同 <see cref="SetProviderContextWindows"/>：
    /// 新值覆寫舊值，本次沒拿到的保留。
    /// </summary>
    public void SetProviderVisionSupport(string providerId, Dictionary<string, bool> support)
    {
        if (string.IsNullOrEmpty(providerId) || support == null) return;
        lock (_settingsLock)
        {
            if (!_providerVisionSupport.TryGetValue(providerId, out var map))
            {
                map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                _providerVisionSupport[providerId] = map;
            }
            foreach (var kvp in support) map[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>查詢模型是否支援圖片輸入；沒有資料時回傳 null。</summary>
    public bool? GetVisionSupport(string providerId, string modelName)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelName)) return null;
        lock (_settingsLock)
        {
            return _providerVisionSupport.TryGetValue(providerId, out var map)
                && map.TryGetValue(modelName, out bool supported)
                ? supported
                : (bool?)null;
        }
    }

    /// <summary>只取 API 回報的值（不含手動值），供設定頁顯示「自動」選項時使用。</summary>
    public int? GetFetchedContextWindow(string providerId, string modelName)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelName)) return null;
        lock (_settingsLock)
        {
            return _providerContextWindows.TryGetValue(providerId, out var fetched)
                && fetched.TryGetValue(modelName, out int reported)
                ? reported
                : (int?)null;
        }
    }

    /// <summary>取得玩家手動填寫的上限，0 代表未填寫。</summary>
    public int GetContextWindowOverride(string entry)
    {
        if (string.IsNullOrEmpty(entry)) return 0;
        lock (_settingsLock)
        {
            return _contextWindowOverrides.TryGetValue(entry, out int manual) ? manual : 0;
        }
    }

    /// <summary>
    /// 設定玩家手動填寫的上限。<paramref name="entry"/> 為 "Provider:Model"；傳入 0 或負值代表移除。
    /// </summary>
    public void SetContextWindowOverride(string entry, int tokens)
    {
        if (string.IsNullOrEmpty(entry)) return;
        lock (_settingsLock)
        {
            if (tokens <= 0)
            {
                _contextWindowOverrides.Remove(entry);
            }
            else
            {
                _contextWindowOverrides[entry] = tokens;
            }
        }
    }

    /// <summary>預設端點的唯一來源在 <see cref="RimLLMEmbeddingService"/>，這裡只是轉呼叫，避免兩張表各自漂移。</summary>
    public static string GetDefaultEmbeddingEndpoint(string provider)
    {
        return RimLLMEmbeddingService.GetDefaultEndpointOrEmpty(provider);
    }

    public string GetEmbeddingModel(string providerId)
    {
        lock (_settingsLock)
        {
            // 沒有預設模型：玩家沒選就回傳空字串，服務會拒絕送出並提示到設定頁選擇。
            return !string.IsNullOrEmpty(providerId) && _embeddingModels.TryGetValue(providerId, out string val) ? val ?? "" : "";
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

    public string GetEmbeddingEndpoint(string providerId)
    {
        lock (_settingsLock)
        {
            if (!string.IsNullOrEmpty(providerId) && _embeddingEndpoints.TryGetValue(providerId, out string val) && !string.IsNullOrEmpty(val))
                return val;
            return GetDefaultEmbeddingEndpoint(providerId);
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

    public string GetEmbeddingApiKey(string providerId)
    {
        lock (_settingsLock)
        {
            return !string.IsNullOrEmpty(providerId) && _embeddingApiKeys.TryGetValue(providerId, out string val) ? val : "";
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

    public static string GetDefaultDecisionEndpoint(string provider)
    {
        return RimLLMDecisionProviderCatalog.GetDefaultEndpoint(provider);
    }

    public string GetDecisionModel(string providerId)
    {
        lock (_settingsLock)
        {
            // 沒有預設模型：玩家沒選就回傳空字串，送決策前會被擋下。
            return !string.IsNullOrEmpty(providerId) && _decisionModels.TryGetValue(providerId, out string value)
                ? value ?? ""
                : "";
        }
    }

    /// <summary>玩家實際輸入的模型名稱（未設定時為空字串），供設定欄位顯示。</summary>
    public string GetDecisionModelRaw(string provider)
    {
        lock (_settingsLock)
        {
            return !string.IsNullOrEmpty(provider) && _decisionModels.TryGetValue(provider, out string value)
                ? value ?? ""
                : "";
        }
    }

    public void SetDecisionModel(string provider, string model)
    {
        if (string.IsNullOrEmpty(provider)) return;
        lock (_settingsLock)
        {
            _decisionModels[provider] = model ?? "";
        }
    }

    public string GetDecisionEndpoint(string providerId)
    {
        lock (_settingsLock)
        {
            if (!string.IsNullOrEmpty(providerId) &&
                _decisionEndpoints.TryGetValue(providerId, out string value) &&
                !string.IsNullOrEmpty(value))
            {
                return value;
            }
            return GetDefaultDecisionEndpoint(providerId);
        }
    }

    /// <summary>玩家實際輸入的端點（未設定時為空字串），供設定欄位顯示。</summary>
    public string GetDecisionEndpointRaw(string provider)
    {
        lock (_settingsLock)
        {
            return !string.IsNullOrEmpty(provider) && _decisionEndpoints.TryGetValue(provider, out string value)
                ? value ?? ""
                : "";
        }
    }

    public void SetDecisionEndpoint(string provider, string endpoint)
    {
        if (string.IsNullOrEmpty(provider)) return;
        lock (_settingsLock)
        {
            _decisionEndpoints[provider] = endpoint ?? "";
        }
    }

    public string GetDecisionApiKey(string providerId)
    {
        lock (_settingsLock)
        {
            return !string.IsNullOrEmpty(providerId) && _decisionApiKeys.TryGetValue(providerId, out string value)
                ? value
                : "";
        }
    }

    public void SetDecisionApiKey(string provider, string key)
    {
        if (string.IsNullOrEmpty(provider)) return;
        lock (_settingsLock)
        {
            _decisionApiKeys[provider] = key ?? "";
        }
    }
}
