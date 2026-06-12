using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// LLM 供應商對接介面。
    /// </summary>
    public interface ILLMProvider
    {
        /// <summary>
        /// 供應商唯一識別碼。
        /// </summary>
        string ProviderId { get; }

        /// <summary>
        /// 此供應商是否必須提供 API Key 才能使用。
        /// 本地相容介面（如 OpenAICompatible / LM Studio / Ollama）可回傳 false。
        /// </summary>
        bool RequiresApiKey { get; }

        /// <summary>
        /// 向該供應商發送非同步生成請求。
        /// </summary>
        /// <param name="request">請求參數</param>
        /// <param name="model">要使用的模型名稱</param>
        /// <returns>生成結果文字</returns>
        Task<string> GenerateAsync(LLMRequest request, string model);

        /// <summary>
        /// 向該供應商發送非同步串流請求。
        /// </summary>
        /// <param name="request">請求參數</param>
        /// <param name="model">要使用的模型名稱</param>
        /// <param name="onChunkReceived">收到字串片段時的回呼函式</param>
        Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived);

        /// <summary>
        /// 測試此供應商的 API 金鑰與連線狀態。
        /// </summary>
        /// <returns>連線測試結果</returns>
        Task<TestResult> TestConnectionAsync();

        /// <summary>
        /// 從 API 伺服器獲取可用模型列表。
        /// </summary>
        /// <returns>模型名稱清單</returns>
        Task<List<string>> FetchAvailableModelsAsync();
    }
}
