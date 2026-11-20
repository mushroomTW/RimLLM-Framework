using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimWorld;
using Verse;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 管理並統計 API 呼叫量、Token 使用度、連線日誌記錄與每日預算審查。
/// 費率與模型分級見 <see cref="ModelPricing"/>；遙測寫檔由設定端的背景寫入者負責，這裡只做節流排程。
/// 所有狀態都屬於實例：同一程序中的多個 tracker（例如單元測試）彼此獨立。
/// </summary>
public class RimLLMUsageTracker
{
    /// <summary>日誌保留上限。與 Trim 邏輯共用，避免魔法數字散落。</summary>
    internal const int MaxRetainedLogs = 30;

    /// <summary>
    /// 呼叫端 modId 的收錄上限。modId 由呼叫端自取、可任意輪換，
    /// 超過上限就不再收錄，避免整場遊戲無限成長、設定頁列出成千上萬列。
    /// </summary>
    public const int MaxCallerModIds = 64;

    /// <summary>遙測寫檔節流間隔：成功請求的用量最多每這麼久寫一次檔，失敗日誌則立即寫。</summary>
    private static readonly TimeSpan TelemetrySaveInterval = TimeSpan.FromSeconds(15);

    /// <summary>預算超限應對策略：只警告並照常送出（<c>BudgetPolicy = 1</c>）。</summary>
    private const int BudgetPolicyWarnOnly = 1;

    /// <summary>沒有翻譯資料時（單元測試、headless）使用的英文字串，同時也是日誌內容。</summary>
    private const string BudgetWarnOnlyFallbackText =
        "Daily budget limit exceeded. Requests are still being sent because the exceeded policy " +
        "is set to 'warn only'. Raise the budget or switch the policy to 'block requests and " +
        "warn' in RimLLM settings -> Budget & Safety.";

    /// <summary>HardBlock 超限警告的英文字串（無翻譯資料時使用，同時也是日誌內容）。</summary>
    private const string BudgetHardBlockFallbackText =
        "Daily budget limit exceeded. RimLLM is blocking AI requests until the budget resets tomorrow. " +
        "Raise the budget or switch the policy to 'warn only' in RimLLM settings -> Budget & Safety.";

    private readonly IRimLLMSettings _settings;

    /// <summary>保護日誌、遙測排程與設定快照；需要同時持有兩把鎖時一律先取這把。</summary>
    private readonly Lock _logLock = new();
    /// <summary>保護用量累計與預算預留。</summary>
    private readonly Lock _usageLock = new();

    private DateTime _lastTelemetrySaveTime = DateTime.MinValue;
    private bool _telemetrySaveDeferred;
    private long _telemetrySaveScheduleVersion;

    /// <summary>每筆 reservation 在整個 logical request（含 fallback/retry）結束前都保留一份估算額度。</summary>
    private long _reservedBudgetTokens;

    /// <summary>最後一次發出超限警告的重置基準日，讓超限警告每天只吵一次。</summary>
    private string _budgetWarnedDate = "";

    /// <summary>
    /// 日誌佇列的近似計數。ConcurrentQueue.Count 可能需要走訪內部區段，
    /// 每請求呼叫一次會隨日誌量線性變貴；此計數器以 Interlocked 維護，Trim 只看它。
    /// </summary>
    private int _logCount;
    private long _unpricedUsageCount;

    /// <summary>存放最近 API 呼叫歷史的執行緒安全佇列。</summary>
    private readonly ConcurrentQueue<RimLLMManager.RequestLogEntry> _requestLogs = new();

    private readonly ConcurrentDictionary<string, ProviderStats> _providerStatistics =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次遊戲實際送出過請求的 modId（值不使用，僅當集合）。</summary>
    private readonly ConcurrentDictionary<string, byte> _callerModIds =
        new(StringComparer.OrdinalIgnoreCase);

    public RimLLMUsageTracker(IRimLLMSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        if (_settings.RequestLogs != null)
        {
            foreach (var log in _settings.RequestLogs)
            {
                // 遙測檔可能損毀或被手動編輯；壞條目不得讓 Mod 初始化失敗。
                if (log == null) continue;
                _requestLogs.Enqueue(log);
                Interlocked.Increment(ref _logCount);
                CountOutcome(log.Provider, log.Success);
            }
            TrimLogs();
        }
    }

    /// <summary>本次執行中有用量但查無費率的呼叫數；不把未知費用誤認為免費。</summary>
    public long UnpricedUsageCount => Interlocked.Read(ref _unpricedUsageCount);

    /// <summary>最近 API 呼叫歷史的唯讀檢視；寫入一律經由 <see cref="RecordLog"/>，以維持計數與存檔快照一致。</summary>
    public IReadOnlyCollection<RimLLMManager.RequestLogEntry> RequestLogs => _requestLogs;

    public IReadOnlyDictionary<string, ProviderStats> ProviderStatistics => _providerStatistics;

    /// <summary>收錄的 modId 數量；設定頁每幀用它判斷是否需要重組，不必複製整個集合。</summary>
    public int CallerModIdCount => _callerModIds.Count;

    /// <summary>收錄的 modId 快照，供設定頁「使用中的模組」。modId 為呼叫端自取的字串，不保證等於 packageId。</summary>
    public IReadOnlyCollection<string> GetCallerModIds() => new List<string>(_callerModIds.Keys);

    /// <summary>單一供應商的成功／失敗計數與 API 端上下文快取命中統計。</summary>
    public class ProviderStats
    {
        private readonly Lock _tokenLock = new();
        private int _successCount;
        private int _failureCount;
        private long _totalPromptTokens;
        private long _cachedPromptTokens;

        public int SuccessCount => Volatile.Read(ref _successCount);
        public int FailureCount => Volatile.Read(ref _failureCount);
        public int TotalCount => SuccessCount + FailureCount;
        public float SuccessRate => TotalCount > 0 ? (float)SuccessCount / TotalCount : 1f;

        public long TotalPromptTokens => Interlocked.Read(ref _totalPromptTokens);
        public long CachedPromptTokens => Interlocked.Read(ref _cachedPromptTokens);
        public float ContextCacheHitRate => TotalPromptTokens > 0 ? (float)CachedPromptTokens / TotalPromptTokens : 0f;

        internal void IncrementSuccess() => Interlocked.Increment(ref _successCount);
        internal void IncrementFailure() => Interlocked.Increment(ref _failureCount);

        /// <summary>累加此供應商的 prompt token 與 API 端快取命中 token；兩者同鎖更新，命中率不會讀到半套。</summary>
        internal void AddTokenCounts(long promptTokens, long cachedPromptTokens)
        {
            lock (_tokenLock)
            {
                Interlocked.Add(ref _totalPromptTokens, promptTokens);
                Interlocked.Add(ref _cachedPromptTokens, cachedPromptTokens);
            }
        }

        /// <summary>清除此供應商的 token 統計。</summary>
        internal void ResetTokenCounts()
        {
            lock (_tokenLock)
            {
                Interlocked.Exchange(ref _totalPromptTokens, 0);
                Interlocked.Exchange(ref _cachedPromptTokens, 0);
            }
        }
    }

    /// <summary>
    /// 記錄一次請求的呼叫端。在請求進入守衛時呼叫，而不是在建立 client 時：
    /// 許多 Mod 一啟動就先取 client，但從未真的送出請求。
    /// </summary>
    public void RecordCaller(string modId)
    {
        if (string.IsNullOrEmpty(modId) || _callerModIds.Count >= MaxCallerModIds) return;
        _callerModIds.TryAdd(modId, 0);
    }

    /// <summary>
    /// 記錄一次請求的日誌與結果，並以節流排程交由背景寫入遙測檔。
    /// token 數由呼叫端在成功且供應商有回報時填入；拿不到時保持 0，顯示層會省略。
    /// </summary>
    public void RecordLog(RimLLMManager.RequestLogEntry entry)
    {
        if (entry == null) return;
        entry.ErrorMessage = RimLLMLog.SanitizeForLog(entry.ErrorMessage, 300);
        entry.PromptTokens = Math.Max(0, entry.PromptTokens);
        entry.CompletionTokens = Math.Max(0, entry.CompletionTokens);

        TelemetrySaveSchedule schedule;
        lock (_logLock)
        {
            _requestLogs.Enqueue(entry);
            Interlocked.Increment(ref _logCount);
            TrimLogs();
            _settings.RequestLogs = new List<RimLLMManager.RequestLogEntry>(_requestLogs.ToArray());
            CountOutcome(entry.Provider, entry.Success);
            // Snapshot 與 dirty 標記同鎖更新，讓背景寫入及關閉 flush 都能看見最新日誌。
            schedule = MarkTelemetryDirtyLocked(immediate: !entry.Success);
        }

        RunTelemetrySaveSchedule(schedule);
    }

    /// <summary>將一次請求結果計入該供應商的成功／失敗計數。</summary>
    private void CountOutcome(string? provider, bool success)
    {
        var stats = _providerStatistics.GetOrAdd(provider ?? "Unknown", _ => new ProviderStats());
        if (success)
        {
            stats.IncrementSuccess();
        }
        else
        {
            stats.IncrementFailure();
        }
    }

    private readonly struct TelemetrySaveSchedule
    {
        public bool QueueNow { get; }
        public bool QueueLater { get; }
        public TimeSpan Delay { get; }
        public long Version { get; }

        public TelemetrySaveSchedule(bool queueNow, bool queueLater, TimeSpan delay, long version)
        {
            QueueNow = queueNow;
            QueueLater = queueLater;
            Delay = delay;
            Version = version;
        }
    }

    /// <summary>_logLock 必須由呼叫端持有，讓快照、dirty 標記和節流時間保持同一個順序。</summary>
    private TelemetrySaveSchedule MarkTelemetryDirtyLocked(bool immediate)
    {
        _settings.MarkTelemetryDirty();
        DateTime now = DateTime.UtcNow;
        TimeSpan elapsed = _lastTelemetrySaveTime == DateTime.MinValue
            ? TelemetrySaveInterval
            : now - _lastTelemetrySaveTime;

        if (immediate || elapsed >= TelemetrySaveInterval)
        {
            _lastTelemetrySaveTime = now;
            _telemetrySaveDeferred = false;
            _telemetrySaveScheduleVersion++;
            return new TelemetrySaveSchedule(true, false, TimeSpan.Zero, _telemetrySaveScheduleVersion);
        }

        if (!_telemetrySaveDeferred)
        {
            _telemetrySaveDeferred = true;
            long version = ++_telemetrySaveScheduleVersion;
            return new TelemetrySaveSchedule(false, true, TelemetrySaveInterval - elapsed, version);
        }

        return default;
    }

    private void RunTelemetrySaveSchedule(TelemetrySaveSchedule schedule)
    {
        if (schedule.QueueNow)
        {
            _settings.QueueTelemetrySave();
        }
        else if (schedule.QueueLater)
        {
            _ = QueueTelemetrySaveAfterDelay(schedule.Delay, schedule.Version);
        }
    }

    private async Task QueueTelemetrySaveAfterDelay(TimeSpan delay, long version)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        bool shouldQueue;
        lock (_logLock)
        {
            shouldQueue = _telemetrySaveDeferred && version == _telemetrySaveScheduleVersion;
            if (shouldQueue)
            {
                _telemetrySaveDeferred = false;
                _lastTelemetrySaveTime = DateTime.UtcNow;
            }
        }

        if (shouldQueue)
        {
            _settings.QueueTelemetrySave();
        }
    }

    /// <summary>手動清除／重設後立即同步寫檔，並作廢尚在等待的延遲寫入。_logLock 必須由呼叫端持有。</summary>
    private void SaveTelemetryNowLocked(string operation)
    {
        _settings.MarkTelemetryDirty();
        _lastTelemetrySaveTime = DateTime.UtcNow;
        _telemetrySaveDeferred = false;
        _telemetrySaveScheduleVersion++;
        try
        {
            _settings.SaveTelemetry();
        }
        catch (Exception ex)
        {
            RimLLMLog.Warning($"[RimLLM] {operation} telemetry write failed: {ex.Message}");
        }
    }

    /// <summary>以計數器為準修剪多餘日誌，避免 ConcurrentQueue.Count 的加鎖列舉。</summary>
    private void TrimLogs()
    {
        while (Volatile.Read(ref _logCount) > MaxRetainedLogs)
        {
            if (!_requestLogs.TryDequeue(out _))
            {
                break;
            }
            Interlocked.Decrement(ref _logCount);
        }
    }

    /// <summary>清空所有快取的請求日誌，並儲存設定。</summary>
    public void ClearLogs()
    {
        lock (_logLock)
        {
            _requestLogs.Clear();
            Interlocked.Exchange(ref _logCount, 0);
            _providerStatistics.Clear();
            _settings.RequestLogs = [];
            SaveTelemetryNowLocked("Clear logs");
        }
    }

    /// <summary>檢查並執行跨天重置日預算累計（token 與美元估計值一併重置）。</summary>
    public void CheckDailyReset()
    {
        string todayStr = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (_logLock)
        {
            lock (_usageLock)
            {
                if (_settings.DailyBudgetResetDate != todayStr)
                {
                    _settings.DailyAccumulatedCost = 0f;
                    _settings.DailyAccumulatedTokens = 0;
                    _settings.DailyBudgetResetDate = todayStr;
                }
            }
        }
    }

    /// <summary>
    /// 累加 Token 統計值，並估計該次 API 消耗的美元成本。
    /// </summary>
    /// <param name="providerId">LLM 供應商識別碼。</param>
    /// <param name="modelName">使用的模型名稱。</param>
    /// <param name="promptTokens">本次請求的「輸入 Token 總量」，須包含被快取命中的部分，以反映真實用量。</param>
    /// <param name="completionTokens">本次請求的「輸出 Token 總量」。</param>
    /// <param name="cachedPromptTokens">
    /// 輸入 Token 中由上下文快取（cache read / cachedContent）命中的部分；
    /// 這些 Token 以折扣費率計價，是 Context Caching 節省成本的來源。
    /// </param>
    public void RecordUsage(string providerId, string? modelName, int promptTokens, int completionTokens, int cachedPromptTokens = 0)
    {
        promptTokens = Math.Max(0, promptTokens);
        completionTokens = Math.Max(0, completionTokens);
        if (promptTokens == 0 && completionTokens == 0) return;

        cachedPromptTokens = Math.Clamp(cachedPromptTokens, 0, promptTokens);

        CheckDailyReset();

        TelemetrySaveSchedule schedule;
        lock (_logLock)
        {
            lock (_usageLock)
            {
                _settings.TotalPromptTokens += promptTokens;
                _settings.TotalCompletionTokens += completionTokens;

                // Token 預算不看費率：免費、本地與查無費率的請求一樣消耗 token。
                _settings.DailyAccumulatedTokens += (long)promptTokens + completionTokens;

                if (ModelPricing.TryEstimateCost(providerId, modelName, promptTokens, completionTokens, cachedPromptTokens, out float cost))
                {
                    _settings.TotalEstimatedCost += cost;
                    _settings.DailyAccumulatedCost += cost;
                }
                else
                {
                    Interlocked.Increment(ref _unpricedUsageCount);
                }
            }

            _providerStatistics.GetOrAdd(providerId ?? "Unknown", _ => new ProviderStats())
                .AddTokenCounts(promptTokens, cachedPromptTokens);

            schedule = MarkTelemetryDirtyLocked(immediate: false);
        }

        RunTelemetrySaveSchedule(schedule);
    }

    /// <summary>重設所有的 Token 與費用計量器。</summary>
    public void ResetUsage()
    {
        lock (_logLock)
        {
            lock (_usageLock)
            {
                _settings.TotalPromptTokens = 0;
                _settings.TotalCompletionTokens = 0;
                _settings.TotalEstimatedCost = 0f;
                Interlocked.Exchange(ref _unpricedUsageCount, 0);

                foreach (var stats in _providerStatistics.Values)
                {
                    stats.ResetTokenCounts();
                }
            }

            SaveTelemetryNowLocked("Reset usage");
        }
    }

    #region Budget Ledger & Policy Gatekeeping

    /// <summary>
    /// 原子地檢查預算並保留整個 logical request 的估算 token。每個 fallback/retry attempt
    /// 的實際用量仍會獨立累加；直到請求成功、失敗、取消或串流結束時 Dispose 才釋放 reservation。
    /// </summary>
    internal bool TryReserveBudget(long estimatedTokens, [NotNullWhen(true)] out BudgetReservation? reservation)
    {
        return TryReserveBudget(estimatedTokens, out reservation, out _);
    }

    /// <summary>
    /// 嘗試預留指定數量的每日 Token 預算。
    /// </summary>
    /// <param name="estimatedTokens">預估消耗的 Token 數量。</param>
    /// <param name="reservation">成功預留時傳回的預留物件憑據，失敗時為 null。</param>
    /// <param name="contended">
    /// true 代表擋下的原因只是剩餘額度暫時被在途請求預留，並非真的超限。
    /// 這種情況不發超限警告：預留釋放後請求就能通過，而超限警告每天只發一次，
    /// 誤報會把當天真正超限時的警告吃掉。
    /// </param>
    /// <returns>若成功預留額度則傳回 true，否則傳回 false。</returns>
    internal bool TryReserveBudget(long estimatedTokens, [NotNullWhen(true)] out BudgetReservation? reservation, out bool contended)
    {
        CheckDailyReset();

        bool overBudget;
        bool warnOnly;
        reservation = null;
        lock (_usageLock)
        {
            overBudget = IsBudgetExceededLocked();
            contended = !overBudget && IsBudgetContendedLocked();
            warnOnly = _settings.BudgetPolicy == BudgetPolicyWarnOnly;
            if ((!overBudget && !contended) || warnOnly)
            {
                reservation = ReserveBudgetLocked(estimatedTokens);
            }
        }

        if (overBudget)
        {
            try
            {
                WarnBudgetExceeded(
                    warnOnly ? "RimLLM_BudgetWarnOnlyExceeded" : "RimLLM_BudgetHardBlockExceeded",
                    warnOnly ? BudgetWarnOnlyFallbackText : BudgetHardBlockFallbackText);
            }
            catch
            {
                reservation?.Dispose();
                reservation = null;
                throw;
            }
        }

        return (!overBudget && !contended) || warnOnly;
    }

    /// <summary>
    /// 保留預算，擋下時依原因擲出對應的 <see cref="RimLLMException"/>：
    /// 真的超限是 QuotaExceeded；只是剩餘額度暫時被在途請求預留時是可重試的 RateLimit。
    /// </summary>
    internal BudgetReservation ReserveBudgetOrThrow(long estimatedTokens)
    {
        if (TryReserveBudget(estimatedTokens, out BudgetReservation? reservation, out bool contended))
        {
            return reservation;
        }

        throw contended
            ? new RimLLMException(LLMError.RateLimit, "The remaining daily budget is reserved by in-flight requests; retry shortly.")
            {
                RetryAfter = TimeSpan.FromSeconds(5)
            }
            : new RimLLMException(LLMError.QuotaExceeded, "Daily budget limit exceeded.");
    }

    /// <summary>在 _usageLock 保護下執行預留邏輯。呼叫端必須已持有鎖。</summary>
    private BudgetReservation ReserveBudgetLocked(long estimatedTokens)
    {
        long reservedTokens = Math.Max(1L, estimatedTokens);
        long limit = _settings.DailyTokenBudgetLimit;
        if (limit > 0)
        {
            // 單筆估算不當成精準上限；保留量最多到達門檻，阻止後續請求
            // 共用同一份剩餘額度，實際超出估算的 token 仍會完整記帳。
            reservedTokens = Math.Min(reservedTokens, limit);
        }
        _reservedBudgetTokens = SaturatingMath.Add(_reservedBudgetTokens, reservedTokens);
        return new BudgetReservation(this, reservedTokens);
    }

    /// <summary>已結算的用量是否真的達到上限。</summary>
    private bool IsBudgetExceededLocked()
    {
        long limit = _settings.DailyTokenBudgetLimit;
        return limit > 0 && Math.Max(0, _settings.DailyAccumulatedTokens) >= limit;
    }

    /// <summary>尚未超限，但剩餘額度已被在途請求全部預留。</summary>
    private bool IsBudgetContendedLocked()
    {
        long limit = _settings.DailyTokenBudgetLimit;
        return limit > 0 && _reservedBudgetTokens >= limit - Math.Max(0, _settings.DailyAccumulatedTokens);
    }

    private void ReleaseBudgetReservation(BudgetReservation reservation)
    {
        lock (_usageLock)
        {
            if (!reservation.TryReleaseAllLocked(out long released)) return;
            _reservedBudgetTokens = Math.Max(0, _reservedBudgetTokens - released);
        }
    }

    internal sealed class BudgetReservation : IDisposable
    {
        private readonly long _reservedTokens;
        private bool _disposed;

        internal RimLLMUsageTracker Tracker { get; }

        internal BudgetReservation(RimLLMUsageTracker tracker, long reservedTokens)
        {
            Tracker = tracker;
            _reservedTokens = reservedTokens;
        }

        internal void RecordUsage(string providerId, string? modelName, int promptTokens, int completionTokens, int cachedPromptTokens = 0)
        {
            Tracker.RecordUsage(providerId, modelName, promptTokens, completionTokens, cachedPromptTokens);
        }

        internal bool TryReleaseAllLocked(out long released)
        {
            if (_disposed)
            {
                released = 0;
                return false;
            }

            _disposed = true;
            released = _reservedTokens;
            return true;
        }

        public void Dispose()
        {
            Tracker.ReleaseBudgetReservation(this);
        }
    }

    /// <summary>
    /// 審查每日預算限額（輸入＋輸出 token 合計）。上限為 0 代表無限制。
    /// </summary>
    /// <remarks>
    /// WarnOnly 不攔截：超支要不要繼續是使用者自己的取捨，
    /// 框架的責任是把數字講清楚，而不是代替他決定要不要繼續花錢。
    /// </remarks>
    internal bool CheckBudgetLimit()
    {
        CheckDailyReset();

        bool overBudget;
        bool warnOnly;
        lock (_usageLock)
        {
            overBudget = IsBudgetExceededLocked();
            warnOnly = _settings.BudgetPolicy == BudgetPolicyWarnOnly;
        }

        if (!overBudget)
        {
            return true;
        }

        if (warnOnly)
        {
            WarnBudgetExceeded("RimLLM_BudgetWarnOnlyExceeded", BudgetWarnOnlyFallbackText);
            return true;
        }

        // HardBlock 也要讓玩家知道是 RimLLM 預算擋下的，否則只會看到各 Mod 回報 QuotaExceeded。
        WarnBudgetExceeded("RimLLM_BudgetHardBlockExceeded", BudgetHardBlockFallbackText);
        return false;
    }

    /// <summary>
    /// 超限警告，每天只發一次。這個檢查每個請求（含串流）都會跑，
    /// 逐次輸出會把日誌洗成一片空白，反而讓真正要看的警告被淹沒。
    /// </summary>
    private void WarnBudgetExceeded(string translationKey, string fallbackText)
    {
        if (!ShouldWarnBudgetExceeded())
        {
            return;
        }

        RimLLMLog.Warning("[RimLLM] " + fallbackText);
        RimLLMDispatcher.EnqueueOnMainThread(() => ShowBudgetExceededMessage(translationKey, fallbackText));
    }

    /// <summary>
    /// 判斷這次超限是否該再次警告，並記下已警告的基準日。
    /// </summary>
    /// <remarks>
    /// 以重置基準日為去重鍵：跨日後自然會再警告一次。當日手動重置不會重新允許警告，
    /// 因為重置後預算已不再超限，沒有再吵一次的道理。
    /// </remarks>
    internal bool ShouldWarnBudgetExceeded()
    {
        lock (_usageLock)
        {
            string resetDate = _settings.DailyBudgetResetDate;
            if (_budgetWarnedDate == resetDate)
            {
                return false;
            }
            _budgetWarnedDate = resetDate;
            return true;
        }
    }

    /// <summary>
    /// 在主執行緒彈出遊戲訊息。這個檢查可能發生在背景執行緒（呼叫端自行決定 await 續行的位置），
    /// 而 <see cref="Messages"/> 是主執行緒狀態，直接呼叫並不安全。
    /// </summary>
    private static void ShowBudgetExceededMessage(string translationKey, string fallbackText)
    {
        try
        {
            // 語言資料尚未就緒時（單元測試、headless）跳過翻譯，改用英文字串。
            string text = LanguageDatabase.activeLanguage != null
                ? translationKey.Translate().ToString()
                : fallbackText;
            Messages.Message(text, MessageTypeDefOf.CautionInput, false);
        }
        catch (Exception ex)
        {
            RimLLMLog.Warning($"[RimLLM] In-game budget warning unavailable: {ex.Message}");
        }
    }

    #endregion
}
