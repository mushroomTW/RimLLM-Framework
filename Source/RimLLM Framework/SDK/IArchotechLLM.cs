using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// Archotech Nexus 統一大型語言模型 (LLM) 接口。
    /// 所有依賴此框架的 AI 模組應以此接口進行文字生成、結構化輸出與串流。
    /// </summary>
    public interface IArchotechLLM
    {
        /// <summary>
        /// 非同步生成文字回應。
        /// </summary>
        /// <param name="request">LLM 請求物件</param>
        /// <returns>生成之文字字串</returns>
        Task<string> GenerateAsync(LLMRequest request);
        
        /// <summary>
        /// 非同步生成結構化物件。
        /// 自動將 C# 類別轉換為 Schema 傳送給模型，並將回應解析反序列化為目標物件。
        /// </summary>
        /// <typeparam name="T">目標資料型別</typeparam>
        /// <param name="request">LLM 請求物件</param>
        /// <returns>反序列化後之目標物件</returns>
        Task<T> GenerateObjectAsync<T>(LLMRequest request);
        
        /// <summary>
        /// 非同步串流生成回應。
        /// </summary>
        /// <param name="request">LLM 請求物件</param>
        /// <returns>非同步可列舉之文字片段串流</returns>
        IAsyncEnumerable<string> StreamAsync(LLMRequest request);
        
        /// <summary>
        /// 測試指定供應商 (Provider) 的連線狀態。
        /// </summary>
        /// <param name="providerId">供應商識別碼 (如 "OpenAI", "Gemini", "DeepSeek", "OpenAICompatible")</param>
        /// <returns>連線測試結果</returns>
        Task<TestResult> TestProviderAsync(string providerId);
        
        /// <summary>
        /// 註冊預期的回應結構定義型別。
        /// 用於快取 Schema 生成結果，提高運作效能。
        /// </summary>
        /// <typeparam name="T">要註冊的結構化型別</typeparam>
        void RegisterResponseType<T>();
    }
}
