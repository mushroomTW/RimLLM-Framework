using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework
{
    /// <summary>
    /// RimLLM SDK 全域靜態入口。第三方 Mod 以 CreateChatClient /
    /// CreateEmbeddingGenerator 取得標準 MEAI client 使用框架，無須事先註冊。
    /// </summary>
    public static class RimLLMProvider
    {
        private static RimLLMManager _manager;

        /// <summary>內部存取目前 manager 執行個體（同 assembly 使用）。</summary>
        internal static RimLLMManager Manager
        {
            get
            {
                if (_manager == null)
                {
                    throw new InvalidOperationException("[RimLLM] SDK has not been initialized. Please make sure the RimLLM Framework mod is active.");
                }
                return _manager;
            }
        }

        /// <summary>
        /// 嘗試取得 manager。SDK 尚未初始化時回傳 false 而不擲出例外，
        /// 供設定 UI 在框架未載入時安全地略過相依於 manager 的區塊。
        /// </summary>
        internal static bool TryGetManager(out RimLLMManager manager)
        {
            manager = _manager;
            return manager != null;
        }

        internal static void Initialize(RimLLMManager manager)
        {
            _manager = manager;
        }

        /// <summary>
        /// 建立標準 MEAI <see cref="IChatClient"/>。modId 為呼叫端自取的識別字串，
        /// 用於防濫用節流與遙測歸屬，不需事先註冊。
        /// </summary>
        public static IChatClient CreateChatClient(string modId)
        {
            return Manager.CreateChatClient(modId);
        }

        /// <summary>
        /// 建立標準 MEAI embedding generator。modId 同 <see cref="CreateChatClient"/>。
        /// </summary>
        public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(string modId)
        {
            return Manager.CreateEmbeddingGenerator(modId);
        }

        /// <summary>註冊外部 LLM 供應商的靜態便捷入口。</summary>
        public static void RegisterProvider(Providers.ILLMProvider provider)
        {
            Manager.RegisterProvider(provider);
        }

        /// <summary>測試指定供應商的連線狀態。</summary>
        public static Task<TestResult> TestProviderAsync(string providerId)
        {
            return Manager.TestProviderAsync(providerId);
        }

        /// <summary>從指定供應商拉取可用模型清單。</summary>
        public static Task<List<string>> FetchProviderModelsAsync(string providerId)
        {
            return Manager.FetchProviderModelsAsync(providerId);
        }

        /// <summary>取得所有已註冊供應商的識別碼（依註冊順序）。</summary>
        public static List<string> GetRegisteredProviderIds()
        {
            return Manager.GetRegisteredProviderIds();
        }
    }
}
