using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace RimLLM_Framework.Core;

/// <summary>
/// Unity 主線程派遣器 (Main Thread Dispatcher)。
/// 讓背景線程（如 API 請求完成）的 Callback 安全地回到 Unity 主線程執行。
/// </summary>
public class RimLLMDispatcher : MonoBehaviour
{
    private const int MaxQueuedActions = 4096;

    private const int MaxActionsPerFrame = 128;

    private const long FrameBudgetMs = 2;

    private static RimLLMDispatcher? _instance;
    private static readonly Lock InstanceLock = new();
    private static readonly ConcurrentQueue<QueuedAction> ExecutionQueue = new();
    private static int _queuedCount;
    private static int _droppedCount;

    private static volatile bool _hasPump;

    /// <summary>
    /// 於主線程建立派遣器單例。必須由 Mod 進入點在主線程呼叫。
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_instance != null) return;

        lock (InstanceLock)
        {
            if (_instance != null) return;

            GameObject go = new("RimLLMDispatcher");
            _instance = go.AddComponent<RimLLMDispatcher>();
            DontDestroyOnLoad(go);
            // 不設 hideFlags：RimWorld 1.7（Waylith）的 UnityStubs 只實作空殼，每次啟動都會記一筆 Stub 警告。
        }
    }

    /// <summary>
    /// 將 Action 排入佇列，以便在下一次 Unity Update 週期中於主線程執行。
    /// 無可運作的 pump 時（單元測試、headless）改為同步執行。
    /// </summary>
    public static void EnqueueOnMainThread(Action action)
    {
        if (action == null) return;

        if (_hasPump)
        {
            TryEnqueueBounded(action);
            return;
        }

        action.Invoke();
    }

    /// <summary>
    /// 將非同步 Func 排入佇列，並以非同步方式等待其在主線程執行後的結果。
    /// </summary>
    public static System.Threading.Tasks.Task<T> EnqueueOnMainThreadAsync<T>(Func<System.Threading.Tasks.Task<T>> func)
    {
        return EnqueueOnMainThreadAsync(func, CancellationToken.None);
    }

    /// <summary>
    /// 將非同步 Func 排入佇列；尚未派送時取消會取消等待並略過委派，已開始的委派則自行處理取消。
    /// </summary>
    public static System.Threading.Tasks.Task<T> EnqueueOnMainThreadAsync<T>(
        Func<System.Threading.Tasks.Task<T>> func,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (cancellationToken.IsCancellationRequested)
        {
            return System.Threading.Tasks.Task.FromCanceled<T>(cancellationToken);
        }

        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        int state = 0; // 0 = 排隊中、1 = 已派送、2 = 派送前取消
        CancellationTokenRegistration registration = default;
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    registration.Dispose();
                }
            });
        }

        // token 已取消時，Register 會同步執行 callback。
        if (Volatile.Read(ref state) == 2)
        {
            #pragma warning disable VSTHRD103, MA0042 // 同步方法回傳 tcs.Task，無法 await DisposeAsync；登記只需解除，不會阻塞
            registration.Dispose();
            #pragma warning restore VSTHRD103, MA0042
            return tcs.Task;
        }

        // 內層刻意以 ConfigureAwait(true) 保留同步內容，func 的續行才會回到主線程。
        // 佇列只接受 Action；委派內已捕捉所有例外並轉交 tcs。
        #pragma warning disable MA0147
        EnqueueWaitable(async () =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                registration.Dispose();
                return;
            }

            try
            {
                await CompleteFromAsync(func, tcs).ConfigureAwait(true);
            }
            finally
            {
                registration.Dispose();
            }
        }, tcs, () => registration.Dispose());
        #pragma warning restore MA0147

        return tcs.Task;
    }

    /// <summary>
    /// 將同步 Func 排入佇列，並以非同步方式等待其在主線程執行後的結果。
    /// </summary>
    public static System.Threading.Tasks.Task<T> EnqueueOnMainThreadAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

        EnqueueWaitable(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, tcs);

        return tcs.Task;
    }

    private static async System.Threading.Tasks.Task CompleteFromAsync<T>(
        Func<System.Threading.Tasks.Task<T>> func,
        System.Threading.Tasks.TaskCompletionSource<T> tcs)
    {
        try
        {
            T result = await func().ConfigureAwait(true);
            tcs.TrySetResult(result);
        }
        catch (OperationCanceledException oce)
        {
            tcs.TrySetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private static void EnqueueWaitable<T>(
        Action action,
        System.Threading.Tasks.TaskCompletionSource<T> tcs,
        Action? onDropped = null)
    {
        if (_hasPump)
        {
            TryEnqueueBounded(new QueuedAction(action, () =>
            {
                tcs.TrySetException(
                    new InvalidOperationException($"[RimLLM] The main-thread queue is full (limit {MaxQueuedActions}); the pending main-thread work was dropped."));
                onDropped?.Invoke();
            }));
            return;
        }

        action.Invoke();
    }

    // 取捨：超過上限時丟棄最舊項目，可能丟掉一次遙測寫入；遙測有 15 秒節流且關閉遊戲時會強制 flush，風險可接受。
    internal static bool TryEnqueueBounded(Action action)
    {
        if (action == null) return false;
        return TryEnqueueBounded(new QueuedAction(action, onDropped: null));
    }

    private static bool TryEnqueueBounded(QueuedAction item)
    {
        ExecutionQueue.Enqueue(item);
        int count = Interlocked.Increment(ref _queuedCount);

        bool dropped = false;
        while (count > MaxQueuedActions && ExecutionQueue.TryDequeue(out QueuedAction discarded))
        {
            count = Interlocked.Decrement(ref _queuedCount);
            dropped = true;

            // 被丟棄的項目若有人在等它的結果，必須通知，否則對方會永遠掛在 await 上。
            discarded.NotifyDropped();

            int totalDropped = Interlocked.Increment(ref _droppedCount);
            if (totalDropped % 100 == 1)
            {
                RimLLMLog.Warning(string.Create(CultureInfo.InvariantCulture, $"[RimLLM] Main-thread queue reached its limit of {MaxQueuedActions}; {totalDropped} callbacks dropped so far."));
            }
        }

        return !dropped;
    }

    private struct QueuedAction
    {
        private readonly Action _run;
        private readonly Action? _onDropped;

        public QueuedAction(Action run, Action? onDropped)
        {
            _run = run;
            _onDropped = onDropped;
        }

        public readonly void Run()
        {
            _run?.Invoke();
        }

        public readonly void NotifyDropped()
        {
            _onDropped?.Invoke();
        }
    }

    // 時間比較用 ElapsedTicks：ElapsedMilliseconds 在 Windows 上粒度約 15ms，2ms 預算會形同虛設。
    internal static int DrainWithBudget(int maxActions, long budgetMs)
    {
        if (ExecutionQueue.IsEmpty || maxActions <= 0 || budgetMs <= 0)
        {
            return 0;
        }

        // OnDestroy 以 long.MaxValue 表示「不設時間上限」；直接跳過時間比較以免換算溢位。
        bool checkTime = budgetMs != long.MaxValue;
        long budgetTicks = checkTime ? budgetMs * Stopwatch.Frequency / 1000L : 0L;
        var stopwatch = Stopwatch.StartNew();
        int processed = 0;

        while (processed < maxActions &&
               (!checkTime || stopwatch.ElapsedTicks < budgetTicks) &&
               ExecutionQueue.TryDequeue(out QueuedAction action))
        {
            Interlocked.Decrement(ref _queuedCount);
            processed++;

            try
            {
                action.Run();
            }
            catch (Exception ex)
            {
                RimLLMLog.Error($"[RimLLM] Main-thread callback failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        return processed;
    }

    internal static void ResetQueueForTests()
    {
        while (ExecutionQueue.TryDequeue(out _))
        {
            // 只需出列。
        }
        Interlocked.Exchange(ref _queuedCount, 0);
        Interlocked.Exchange(ref _droppedCount, 0);
    }

    internal static int QueuedCount => Volatile.Read(ref _queuedCount);

    internal static bool HasPump => _hasPump;

    // Waylith（遊戲的 Unity 相容層）只呼叫執行個體形式的 Unity 訊息方法；
    // Awake／Update 宣告成 static 就不會被呼叫，主線程 pump 會失效。
    #pragma warning disable CA1822, S2325 // Unity 訊息方法必須是執行個體方法。
    private void Awake() // NOSONAR：Unity 訊息方法必須是執行個體方法
    {
        SetPumpActive(active: true);
    }
    #pragma warning restore CA1822, S2325

    #pragma warning disable CA1822, S2325 // Unity 訊息方法必須是執行個體方法。
    private void Update() // NOSONAR：Unity 訊息方法必須是執行個體方法
    {
        DrainWithBudget(MaxActionsPerFrame, FrameBudgetMs);
    }
    #pragma warning restore CA1822, S2325

    private void OnDestroy()
    {
        SetPumpActive(active: false);

        // 先排乾剩餘項目，避免遺失遙測寫入等重要回呼。
        int drained = DrainWithBudget(int.MaxValue, long.MaxValue);
        if (drained > 0)
        {
            RimLLMLog.Message(string.Create(CultureInfo.InvariantCulture, $"[RimLLM] Dispatcher drained {drained} pending callbacks before shutdown."));
        }

        ReleaseInstance(this);
    }

    private static void SetPumpActive(bool active)
    {
        _hasPump = active;
    }

    // 僅當單例就是正在銷毀的實例時才清除，避免誤清關機期間已重建的新實例。
    private static void ReleaseInstance(RimLLMDispatcher dispatcher)
    {
        if (_instance == dispatcher)
        {
            _instance = null;
        }
    }
}
