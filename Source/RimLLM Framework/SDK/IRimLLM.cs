using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// RimLLM 統一大型語言模型 (LLM) 介面。
    /// 所有依賴此框架的 AI 模組應以此介面進行文字生成、結構化輸出與串流。
    /// </summary>
    public interface IRimLLM
    {
        /// <summary>
        /// 非同步生成文字回應。
        /// </summary>
        /// <param name="request">LLM 請求物件</param>
        /// <returns>生成之文字字串</returns>
        Task<string> GenerateAsync(LLMRequest request);

        /// <summary>
        /// 非同步生成文字回應的簡化 overload。
        /// </summary>
        Task<string> GenerateAsync(
            string modId,
            string prompt,
            string systemPrompt = null,
            string cachedContext = null,
            int maxTokens = 1024,
            float temperature = 0.7f,
            LLMReasoningEffort reasoningEffort = LLMReasoningEffort.Auto,
            CancellationToken cancellationToken = default);
        
        /// <summary>
        /// 非同步生成結構化物件。
        /// 自動將 C# 類別轉換為 Schema 傳送給模型，並將回應解析反序列化為目標物件。
        /// </summary>
        /// <typeparam name="T">目標資料型別</typeparam>
        /// <param name="request">LLM 請求物件</param>
        /// <returns>反序列化後之目標物件</returns>
        Task<T> GenerateObjectAsync<T>(LLMRequest request);

        /// <summary>
        /// 非同步生成結構化物件的簡化 overload。
        /// </summary>
        Task<T> GenerateObjectAsync<T>(
            string modId,
            string prompt,
            string systemPrompt = null,
            string cachedContext = null,
            int maxTokens = 1024,
            float temperature = 0.7f,
            LLMReasoningEffort reasoningEffort = LLMReasoningEffort.Auto,
            CancellationToken cancellationToken = default);
        
        /// <summary>
        /// 非同步串流生成回應。
        /// </summary>
        /// <param name="request">LLM 請求物件</param>
        /// <param name="onChunkReceived">收到字串片段時的回呼函式</param>
        Task StreamAsync(LLMRequest request, Action<string> onChunkReceived);

        /// <summary>
        /// 非同步串流生成回應的簡化 overload。
        /// </summary>
        Task<string> GenerateStreamingAsync(
            string modId,
            string prompt,
            Action<string> onChunkReceived,
            string systemPrompt = null,
            string cachedContext = null,
            int maxTokens = 1024,
            float temperature = 0.7f,
            LLMReasoningEffort reasoningEffort = LLMReasoningEffort.Auto,
            CancellationToken cancellationToken = default);
        
        /// <summary>
        /// 測試指定供應商 (Provider) 的連線狀態。
        /// </summary>
        /// <param name="providerId">供應商識別碼 (如 "OpenAI", "Gemini", "OpenAICompatible")</param>
        /// <returns>連線測試結果</returns>
        Task<TestResult> TestProviderAsync(string providerId);
        
        /// <summary>
        /// 註冊預期的回應結構定義型別。
        /// 用於快取 Schema 生成結果，提高運作效能。
        /// </summary>
        /// <typeparam name="T">要註冊的結構化型別</typeparam>
        void RegisterResponseType<T>();

        /// <summary>
        /// 從指定供應商 (Provider) 拉取可用的模型清單。
        /// </summary>
        /// <param name="providerId">供應商識別碼</param>
        /// <returns>可用模型名稱清單</returns>
        Task<List<string>> FetchProviderModelsAsync(string providerId);

        /// <summary>
        /// 註冊外部 LLM 供應商，供第三方 Mod 擴充自訂供應商（如自架伺服器或新興 API）。
        /// 外部供應商註冊後即視為啟用，使用者透過 Fallback Chain 控制其參與。
        /// ProviderId 不得與既有供應商重複。
        /// </summary>
        /// <param name="provider">供應商實作</param>
        void RegisterProvider(ILLMProvider provider);

        /// <summary>
        /// 取得所有已註冊供應商的識別碼（依註冊順序，內建供應商在前）。
        /// </summary>
        List<string> GetRegisteredProviderIds();
    }
}
