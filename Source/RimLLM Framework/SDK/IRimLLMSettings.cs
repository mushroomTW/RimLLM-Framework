using System.Collections.Generic;

namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// 定義 RimLLM Framework 的設定檔介面。
    /// 藉由介面隔離，解除核心邏輯與 RimWorld ModSettings 本體的直接耦合。
    /// </summary>
    public interface IRimLLMSettings
    {
        /// <summary>
        /// API 供應商的 Fallback Chain 順序。
        /// </summary>
        List<string> FallbackChain { get; }

        /// <summary>
        /// API 逾時時間 (秒)。
        /// </summary>
        float ApiTimeout { get; }

        /// <summary>
        /// 是否啟用詳細日誌輸出。
        /// </summary>
        bool DetailedLogging { get; }

        /// <summary>
        /// 單一模型最多重試次數。
        /// </summary>
        int MaxRetries { get; }

        /// <summary>
        /// 重試間隔 (秒)。
        /// </summary>
        float RetryDelay { get; }

        /// <summary>
        /// 獲取指定供應商的 API 金鑰。
        /// </summary>
        string GetApiKey(string providerId);

        /// <summary>
        /// 獲取指定供應商當前輪詢啟用的單一 API 金鑰。
        /// </summary>
        string GetActiveApiKey(string providerId);

        /// <summary>
        /// 獲取指定供應商的 API 端點。
        /// </summary>
        string GetEndpoint(string providerId, string defaultVal);

        /// <summary>
        /// 檢查指定供應商是否啟用。
        /// </summary>
        bool IsProviderEnabled(string providerId);

        /// <summary>
        /// 獲取指定供應商的可用模型清單。
        /// </summary>
        List<string> GetModelList(string providerId);

        /// <summary>
        /// 獲取指定供應商的預設模型名稱。
        /// </summary>
        string GetDefaultModel(string providerId, string defaultVal);

        /// <summary>
        /// 設定指定供應商的可用模型清單。
        /// </summary>
        void SetModelList(string providerId, List<string> models);

        /// <summary>
        /// 最大並行限制。
        /// </summary>
        int MaxConcurrentRequests { get; }

        /// <summary>
        /// 將設定寫入/持久化。
        /// </summary>
        void Write();
    }
}
