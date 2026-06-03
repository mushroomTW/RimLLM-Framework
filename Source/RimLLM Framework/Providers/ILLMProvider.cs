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
        /// <returns>非同步字串片段串流</returns>
        IAsyncEnumerable<string> StreamAsync(LLMRequest request, string model);

        /// <summary>
        /// 測試此供應商的 API 金鑰與連線狀態。
        /// </summary>
        /// <returns>連線測試結果</returns>
        Task<TestResult> TestConnectionAsync();
    }
}
