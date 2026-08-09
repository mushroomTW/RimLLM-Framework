using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// LLM 供應商對接介面（MEAI 慣例：messages + options）。
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
        /// <param name="messages">對話訊息清單</param>
        /// <param name="options">生成選項</param>
        /// <param name="model">要使用的模型名稱</param>
        /// <returns>生成結果文字</returns>
        Task<string> GenerateAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model);

        /// <summary>
        /// 向該供應商發送非同步串流請求。
        /// </summary>
        /// <param name="messages">對話訊息清單</param>
        /// <param name="options">生成選項</param>
        /// <param name="model">要使用的模型名稱</param>
        /// <param name="onChunkReceived">收到字串片段時的回呼函式</param>
        Task StreamAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model, Action<string> onChunkReceived);

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
