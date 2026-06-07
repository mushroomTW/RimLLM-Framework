using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// 管理 API 請求的優先權佇列與並行限流。
    /// 依據 LLMRequest 的優先級（Priority）與先進先出（FIFO）規則調度執行。
    /// </summary>
    public class RimLLMRequestQueue
    {
        private readonly IRimLLMSettings _settings;
        private readonly object _queueLock = new object();
        private readonly List<QueueEntry> _waitingQueue = new List<QueueEntry>();
        private int _activeRequests = 0;

        /// <summary>
        /// 佇列實體定義。
        /// </summary>
        private class QueueEntry : IComparable<QueueEntry>
        {
            public LLMRequest Request { get; set; }
            public TaskCompletionSource<string> Tcs { get; set; }
            public Func<Task<string>> Action { get; set; }
            public DateTime EnqueueTime { get; set; } = DateTime.UtcNow;

            public int CompareTo(QueueEntry other)
            {
                // 優先級高（數值大）的排在前面
                int cmp = other.Request.Priority.CompareTo(this.Request.Priority);
                if (cmp == 0)
                {
                    // 優先級相同時，先入列的排在前面（FIFO）
                    return this.EnqueueTime.CompareTo(other.EnqueueTime);
                }
                return cmp;
            }
        }

        public RimLLMRequestQueue(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 將非同步請求包裝入優先權佇列排隊。
        /// </summary>
        public async Task<string> EnqueueRequestAsync(LLMRequest request, Func<Task<string>> action)
        {
            var tcs = new TaskCompletionSource<string>();
            var entry = new QueueEntry
            {
                Request = request,
                Tcs = tcs,
                Action = action
            };

            // 如果一開始就被取消
            if (request.CancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(request.CancellationToken);
                return await tcs.Task;
            }

            CancellationTokenRegistration registration = default;
            if (request.CancellationToken != default)
            {
                registration = request.CancellationToken.Register(() =>
                {
                    lock (_queueLock)
                    {
                        if (_waitingQueue.Remove(entry))
                        {
                            tcs.TrySetCanceled(request.CancellationToken);
                        }
                    }
                });
            }

            lock (_queueLock)
            {
                _waitingQueue.Add(entry);
                _waitingQueue.Sort();
            }

            ProcessQueue();

            try
            {
                return await tcs.Task;
            }
            finally
            {
                registration.Dispose();
            }
        }

        private void ProcessQueue()
        {
            lock (_queueLock)
            {
                int limit = _settings.MaxConcurrentRequests;
                while (_activeRequests < limit && _waitingQueue.Count > 0)
                {
                    var entry = _waitingQueue[0];
                    _waitingQueue.RemoveAt(0);
                    _activeRequests++;

                    // 啟動排程非同步執行而不進行阻塞
                    _ = ExecuteQueuedRequestAsync(entry);
                }
            }
        }

        private async Task ExecuteQueuedRequestAsync(QueueEntry entry)
        {
            try
            {
                if (entry.Request.CancellationToken.IsCancellationRequested)
                {
                    entry.Tcs.TrySetCanceled(entry.Request.CancellationToken);
                    return;
                }

                string result = await entry.Action().ConfigureAwait(false);
                entry.Tcs.TrySetResult(result);
            }
            catch (OperationCanceledException ex)
            {
                entry.Tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                entry.Tcs.TrySetException(ex);
            }
            finally
            {
                lock (_queueLock)
                {
                    _activeRequests--;
                }
                ProcessQueue();
            }
        }
    }
}
