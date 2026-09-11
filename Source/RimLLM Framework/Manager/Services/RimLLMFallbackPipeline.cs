using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;
#pragma warning disable S1066, S2325 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// 負責備用鏈（Fallback Chain）維護、Provider 失敗後自動嘗試下一個備用 Provider、
    /// 備用管道執行與健康帳本 (Health Ledger) 連動等邏輯的服務元件。
    /// </summary>
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
    public class RimLLMFallbackPipeline
    {
        private readonly IRimLLMSettings _settings;
        private readonly RimLLMHealthLedger _healthLedger;
        private readonly RimLLMUsageTracker _usageTracker;
        private readonly Func<string, ILLMProvider> _providerResolver;
        private readonly Func<string, bool> _isProviderEnabledFunc;

        internal struct ResolvedCandidate
        {
            public string Entry;
            public string ProviderId;
            public ILLMProvider Provider;
            public string ModelName;
        }

        public RimLLMFallbackPipeline(
            IRimLLMSettings settings,
            RimLLMHealthLedger healthLedger,
            RimLLMUsageTracker usageTracker,
            Func<string, ILLMProvider> providerResolver,
            Func<string, bool> isProviderEnabledFunc)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _healthLedger = healthLedger ?? throw new ArgumentNullException(nameof(healthLedger));
            _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
            _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
            _isProviderEnabledFunc = isProviderEnabledFunc ?? throw new ArgumentNullException(nameof(isProviderEnabledFunc));
        }

        /// <summary>
        /// 共用的 Fallback Chain 執行核心。
        /// 依序遍歷符合資格的供應商條目，對每個條目套用相同的重試策略，
        /// 並統一處理取消檢查、健康帳本記錄與用量統計。
        /// </summary>
        /// <summary>
        /// 解析出本次請求可用的候選，並依路由策略排序。
        /// </summary>
        /// <remarks>
        /// 只做「選誰、依什麼順序」的決策；實際的嘗試、重試與健康記錄由
        /// <see cref="RimLLMFailoverChatClient"/> 負責。
        /// </remarks>
        /// <param name="includeCoolingDown">
        /// 為 true 時不過濾冷卻中的候選。能力查詢用：冷卻會到期，查詢當下被過濾掉的候選
        /// 仍可能在稍後的請求中被路由到。
        /// </param>
        internal List<ResolvedCandidate> ResolveCandidates(string preferredModelId, string minFallbackLevel, bool includeCoolingDown = false)
        {
            var fallbackChain = GetFallbackChainSnapshot();
            if (fallbackChain == null || fallbackChain.Count == 0)
            {
                throw new RimLLMException(LLMError.ProviderOffline, "No valid API provider fallback chain configured.");
            }

            // PreferredModelId（格式 "ProviderId:ModelName"）指定的話，於 fallback chain 前優先嘗試
            var effectiveChain = new List<string>(fallbackChain);
            if (!string.IsNullOrEmpty(preferredModelId))
            {
                string preferredEntry = preferredModelId;
                if (!ResolveFallbackEntry(preferredEntry, out string prefProvider, out string prefModel)
                    || string.IsNullOrEmpty(prefModel))
                {
                    // 無 provider 前綴的純 model 名視為「不指定 provider」，忽略（交給 fallback chain）
                    prefProvider = null;
                }
                if (prefProvider != null && (_providerResolver(prefProvider) is ILLMProvider prefProviderInstance)
                    && IsProviderUsable(prefProvider, prefProviderInstance)
                    && !effectiveChain.Exists(e => string.Equals(e, preferredEntry, StringComparison.OrdinalIgnoreCase)))
                {
                    effectiveChain.Insert(0, preferredEntry);
                }
            }

            // 1. 解析所有符合資格的供應商候選
            var candidates = new List<ResolvedCandidate>();
            foreach (string entry in effectiveChain)
            {
                if (TryGetEligibleCandidate(entry, minFallbackLevel, out string pId, out ILLMProvider p, out string mName))
                {
                    candidates.Add(new ResolvedCandidate { Entry = entry, ProviderId = pId, Provider = p, ModelName = mName });
                }
            }

            if (candidates.Count == 0)
            {
                throw new RimLLMException(LLMError.ProviderOffline, "No eligible API providers found in the fallback chain.");
            }

            // 2. 過濾處於故障冷卻期的候選（若全部都在冷卻中，則破例放行）
            var activeCandidates = includeCoolingDown
                ? candidates
                : candidates.FindAll(c => !_healthLedger.IsInCooldown(HealthKey(c)));
            if (activeCandidates.Count == 0)
            {
                activeCandidates = candidates;
            }

            // 3. 套用路由與負載均衡策略
            switch (_settings.RoutingStrategy)
            {
                case 1: // MinLatency (最小延遲優先)
                    // 尚無延遲記錄的候選其平均延遲為 0，會自然排到最前面——這是刻意的探索行為：
                    // 每個候選都得先被呼叫一次才會有延遲數字，若把無記錄者排到最後，
                    // 第一個拿到記錄的候選就會被永久鎖定，其餘候選永遠沒有機會被測量。
                    StableSortBy(activeCandidates, c => _healthLedger.GetAverageLatency(HealthKey(c)));
                    break;

                case 2: // RoundRobin / Random (隨機輪詢負載均衡)
                    ShuffleCandidates(activeCandidates);
                    break;

                case 3: // LowestCost (成本優先)
                    // 直接沿用既有的模型分級（含使用者覆寫）作為成本代理值，由低到高排序。
                    // 分級本來就是依 API 費率自動判定的，不需要另外維護一份價格表。
                    StableSortBy(activeCandidates, c => GetModelLevel(c.Entry, c.ProviderId, c.ModelName));
                    break;

                default: // 0 = PriorityFailover：保留原始 fallbackChain 順序
                    break;
            }

            return activeCandidates;
        }


        /// <summary>
        /// 健康帳本的記錄鍵。冷卻以「供應商:模型」為單位，而非只看供應商——
        /// 同一個供應商底下常有多個模型同時掛在備用鏈上（例如三個 OpenRouter 模型），
        /// 只以供應商為鍵會讓其中一個模型限流就把另外兩個健康的模型一起連坐冷卻。
        /// </summary>
        internal static string HealthKey(ResolvedCandidate candidate)
        {
            return string.IsNullOrEmpty(candidate.ModelName)
                ? candidate.ProviderId
                : candidate.ProviderId + ":" + candidate.ModelName;
        }

        /// <summary>
        /// 計算下一次重試前的等待秒數。
        /// 以設定的重試間隔為基準做指數退避（第 n 次重試等待 delay × 2ⁿ）並加上 ±20% 抖動：
        /// 限流後以固定間隔連打只會再次一起撞牆，把重試額度白白耗光。
        /// 伺服器若透過 Retry-After 指定了更長的等待時間則以其為準；
        /// 整體上限 60 秒，免得玩家在遊戲中枯等。
        /// </summary>
        internal static float ResolveRetryDelay(float baseDelay, int attempt, Exception ex)
        {
            const float maxDelaySeconds = 60f;
            const int maxBackoffSteps = 6;

            float delay = baseDelay * (float)Math.Pow(2, Math.Min(attempt, maxBackoffSteps));
            if (delay > 0f)
            {
                delay *= 1f + ((float)NextRandomDouble() * 0.4f - 0.2f);
            }

            if (ex is RimLLMException rimEx && rimEx.RetryAfter.HasValue)
            {
                delay = Math.Max(delay, (float)rimEx.RetryAfter.Value.TotalSeconds);
            }

            return Math.Min(delay, maxDelaySeconds);
        }

        /// <summary>
        /// 以指定鍵值穩定排序候選清單。
        /// <see cref="List{T}.Sort(Comparison{T})"/> 不保證穩定，鍵值相同時會打亂備用鏈原本的
        /// 順序（成本排序時同級模型很常見），因此在此以原索引作為次要鍵。
        /// </summary>
        private static void StableSortBy(List<ResolvedCandidate> candidates, Func<ResolvedCandidate, float> keySelector)
        {
            var keyed = new List<KeyValuePair<float, int>>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                keyed.Add(new KeyValuePair<float, int>(keySelector(candidates[i]), i));
            }

            keyed.Sort((a, b) =>
            {
                int comparison = a.Key.CompareTo(b.Key);
                return comparison != 0 ? comparison : a.Value.CompareTo(b.Value);
            });

            var sorted = new List<ResolvedCandidate>(candidates.Count);
            foreach (var pair in keyed)
            {
                sorted.Add(candidates[pair.Value]);
            }

            candidates.Clear();
            candidates.AddRange(sorted);
        }

        /// <summary>Fisher-Yates 洗牌，用於隨機輪詢負載均衡。</summary>
        private static void ShuffleCandidates(List<ResolvedCandidate> candidates)
        {
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = NextRandomInt(i + 1);
                var temp = candidates[i];
                candidates[i] = candidates[j];
                candidates[j] = temp;
            }
        }

#pragma warning disable S2245 // reason: 僅用於重試抖動與負載均衡輪詢，非安全相關隨機，無需密碼學強度
        private static readonly Random SharedRandom = new Random();
#pragma warning restore S2245
        private static readonly object RandomLock = new object();

        // Random 非執行緒安全，而重試與洗牌都可能來自不同的背景執行緒；
        // 共用一個加鎖實例，同時也避免每次 new Random() 在同一毫秒內產生相同序列。
        private static double NextRandomDouble()
        {
            lock (RandomLock) { return SharedRandom.NextDouble(); }
        }

        private static int NextRandomInt(int maxExclusive)
        {
            lock (RandomLock) { return SharedRandom.Next(maxExclusive); }
        }

        internal bool ResolveFallbackEntry(string entry, out string providerId, out string modelName)
        {
            providerId = ProviderIds.ParseProviderId(entry);
            modelName = "";

            if (providerId == null)
            {
                providerId = entry;
                return false;
            }

            int colonIndex = entry.IndexOf(':');
            modelName = colonIndex > 0
                ? entry.Substring(colonIndex + 1)
                // 純供應商：取該供應商的預設模型
                : _settings.GetDefaultModel(providerId, "default");

            return true;
        }

        public void ClearCooldowns()
        {
            _healthLedger.Clear();
        }

        private List<string> GetFallbackChainSnapshot()
        {
            var chain = _settings.FallbackChain;
#pragma warning disable S1168 // reason: null 表示未配置 fallback 鏈，與空集合語意不同，呼叫端需區分
            return chain != null ? new List<string>(chain) : null;
#pragma warning restore S1168
        }

        private bool IsProviderUsable(string providerId, ILLMProvider provider)
        {
            return _isProviderEnabledFunc(providerId) &&
                   (!provider.RequiresApiKey || !string.IsNullOrEmpty(_settings.GetApiKey(providerId)));
        }

        private bool TryGetEligibleCandidate(string entry, string minFallbackLevel, out string providerId, out ILLMProvider provider, out string modelName)
        {
            provider = null;

            if (!ResolveFallbackEntry(entry, out providerId, out modelName))
                return false;

            provider = _providerResolver(providerId);
            if (provider == null)
                return false;

            if (!IsProviderUsable(providerId, provider))
                return false;

            // Budget fallback to free (0=HardBlock, 1=SilentMocking, 2=FallbackToFree, 3=DialogPrompt)
            if (_settings.BudgetPolicy == 2 &&
                _settings.DailyBudgetLimit > 0f && _settings.DailyAccumulatedCost >= _settings.DailyBudgetLimit &&
                providerId != ProviderIds.OpenAICompatible && !modelName.ToLower().Contains("free"))
            {
                return false;
            }

            // 評估 MinFallbackLevel 模型分級
            int minLevel = ParseMinFallbackLevel(minFallbackLevel);
            if (minLevel > 0)
            {
                int currentModelLevel = GetModelLevel(entry, providerId, modelName);
                if (currentModelLevel < minLevel)
                {
                    RimLLMLog.Message($"[RimLLM] Skipped fallback entry '{entry}' because its model level ({currentModelLevel}) is lower than MinFallbackLevel ({minLevel}).");
                    return false;
                }
            }

            return true;
        }

        private int GetModelLevel(string entry, string providerId, string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return 1;

            // 使用者明確設定的分級覆寫優先於 API 費率判定
            int overrideLevel = _settings.GetModelLevelOverride(entry ?? modelName);
            if (overrideLevel >= RimLLMUsageTracker.ModelLevelLow && overrideLevel <= RimLLMUsageTracker.ModelLevelHigh)
            {
                return overrideLevel;
            }

            // 依據 UsageTracker 中的真實 API 費率客觀判定等級
            return _usageTracker.GetModelLevel(providerId, modelName);
        }

        private static int ParseMinFallbackLevel(string levelStr)
        {
            switch ((levelStr ?? string.Empty).ToLower())
            {
                case "high":
                case "3": return 3;
                case "medium":
                case "2": return 2;
                case "low":
                case "1": return 1;
                default: return 0;
            }
        }

        internal static bool IsRetryableException(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                return false;
            }

            if (ex is RimLLMException rimEx)
            {
                switch (rimEx.Error)
                {
                    case LLMError.Timeout:
                    case LLMError.RateLimit:
                    case LLMError.ProviderOffline:
                    case LLMError.NetworkError:
                    case LLMError.QuotaExceeded:
                    case LLMError.Unknown:
                        return true;
                    default:
                        return false;
                }
            }

            // 參數、狀態與解析類例外代表呼叫本身有問題，以相同輸入重試必然再次失敗。
            if (ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is InvalidOperationException ||
                ex is System.Text.Json.JsonException)
            {
                return false;
            }

            return true;
        }
    }
#pragma warning restore S101
#pragma warning restore S1066, S2325
}