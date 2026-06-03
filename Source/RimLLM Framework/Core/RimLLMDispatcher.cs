using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace RimLLM_Framework.Core
{
    /// <summary>
    /// Unity 主線程派遣器 (Main Thread Dispatcher)。
    /// 確保所有在背景線程（如 API 請求完成）的 Callback 能夠安全地回到 Unity 主線程執行，防止 TPS 劇烈震盪與 Unity API 非線程安全崩潰。
    /// </summary>
    public class RimLLMDispatcher : MonoBehaviour
    {
        private static RimLLMDispatcher _instance;
        private static readonly ConcurrentQueue<Action> ExecutionQueue = new ConcurrentQueue<Action>();

        /// <summary>
        /// 取得主線程派遣器單例。如果不存在，會自動於 Unity 系統中建立隱藏的持久 GameObject。
        /// </summary>
        public static RimLLMDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("RimLLMDispatcher");
                    _instance = go.AddComponent<RimLLMDispatcher>();
                    DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;
                }
                return _instance;
            }
        }

        /// <summary>
        /// 將 Action 排入佇列，以便在下一次 Unity Update 週期中於主線程執行。
        /// </summary>
        public void Enqueue(Action action)
        {
            if (action == null) return;
            ExecutionQueue.Enqueue(action);
        }

        private void Update()
        {
            // 在每一幀的 Update 週期中，消耗佇列中的所有 Actions
            while (ExecutionQueue.TryDequeue(out Action action))
            {
                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    RimLLMLog.Error($"[RimLLM] 主線程分發 Callback 執行失敗: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
