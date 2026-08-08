using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace RimLLM_Framework.Core
{
    /// <summary>
    /// Unity 主線程派遣器 (Main Thread Dispatcher)。
    /// 確保所有在背景線程（如 API 請求完成）的 Callback 能夠安全地回到 Unity 主線程執行，防止 TPS 劇烈震盪與 Unity API 非線程安全崩潰。
    /// </summary>
    public class RimLLMDispatcher : MonoBehaviour
    {
        /// <summary>佇列長度上限。超過後丟棄最舊的項目，避免高 chunk 率時無限成長。</summary>
        private const int MaxQueuedActions = 4096;

        /// <summary>單一幀最多執行的項目數。</summary>
        private const int MaxActionsPerFrame = 128;

        /// <summary>單一幀執行的時間預算（毫秒）。</summary>
        private const long FrameBudgetMs = 2;

        private static RimLLMDispatcher _instance;
        private static readonly object InstanceLock = new object();
        private static readonly ConcurrentQueue<Action> ExecutionQueue = new ConcurrentQueue<Action>();
        private static int _queuedCount;
        private static int _droppedCount;

        /// <summary>
        /// 是否已有可運作的主線程 pump（即元件已 Awake 且尚未 OnDestroy）。
        /// </summary>
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

                GameObject go = new GameObject("RimLLMDispatcher");
                _instance = go.AddComponent<RimLLMDispatcher>();
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
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

            // 沒有可運作的 pump（單元測試、headless 反射執行，或元件尚未建立）。
            // 此時同步執行是唯一選擇，但這是明確的分支而非以例外控制流程。
            action.Invoke();
        }

        /// <summary>
        /// 有界入列。超過上限時丟棄最舊項目並計數，回傳是否發生丟棄。
        /// </summary>
        /// <remarks>
        /// 遙測寫入也是透過本派遣器排入的；丟棄最舊項目理論上可能丟掉一次遙測寫入請求。
        /// 因為遙測本身有 15 秒節流且關閉遊戲時會強制 flush，此風險可接受。
        /// </remarks>
        internal static bool TryEnqueueBounded(Action action)
        {
            if (action == null) return false;

            ExecutionQueue.Enqueue(action);
            int count = Interlocked.Increment(ref _queuedCount);

            bool dropped = false;
            while (count > MaxQueuedActions && ExecutionQueue.TryDequeue(out _))
            {
                count = Interlocked.Decrement(ref _queuedCount);
                dropped = true;

                int totalDropped = Interlocked.Increment(ref _droppedCount);
                if (totalDropped % 100 == 1)
                {
                    RimLLMLog.Warning($"[RimLLM] 主線程佇列已達上限 {MaxQueuedActions}，已累計丟棄 {totalDropped} 個回呼。");
                }
            }

            return !dropped;
        }

        /// <summary>
        /// 依項目數與時間預算清空佇列，回傳實際執行的項目數。
        /// </summary>
        internal static int DrainWithBudget(int maxActions, long budgetMs)
        {
            var stopwatch = Stopwatch.StartNew();
            int processed = 0;

            while (processed < maxActions &&
                   stopwatch.ElapsedMilliseconds < budgetMs &&
                   ExecutionQueue.TryDequeue(out Action action))
            {
                Interlocked.Decrement(ref _queuedCount);
                processed++;

                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    RimLLMLog.Error($"[RimLLM] 主線程分發 Callback 執行失敗: {ex.Message}\n{ex.StackTrace}");
                }
            }

            return processed;
        }

        /// <summary>
        /// 測試用：清空佇列與計數。
        /// </summary>
        internal static void ResetQueueForTests()
        {
            while (ExecutionQueue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _queuedCount, 0);
            Interlocked.Exchange(ref _droppedCount, 0);
        }

        internal static int QueuedCount => Volatile.Read(ref _queuedCount);

        private void Awake()
        {
            _hasPump = true;
        }

        private void Update()
        {
            // 每幀受項目數與時間預算雙重限制，避免長回應的大量 chunk 在單一幀內全部執行造成卡頓。
            DrainWithBudget(MaxActionsPerFrame, FrameBudgetMs);
        }

        private void OnDestroy()
        {
            _hasPump = false;

            // 先把剩餘項目排乾，避免關閉時遺失遙測寫入等重要回呼。
            int drained = DrainWithBudget(int.MaxValue, long.MaxValue);
            if (drained > 0)
            {
                RimLLMLog.Message($"[RimLLM] 派遣器關閉前排空了 {drained} 個待執行回呼。");
            }

            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
