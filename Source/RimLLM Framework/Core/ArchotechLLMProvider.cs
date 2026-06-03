using System;
using System.Reflection;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Core
{
    /// <summary>
    /// Archotech Nexus SDK 全域靜態入口。
    /// 其他 Mod 可以透過此類別註冊客戶端與取得 IArchotechLLM 服務執行個體。
    /// </summary>
    public static class ArchotechLLMProvider
    {
        private static IArchotechLLM _instance;

        /// <summary>
        /// 取得 IArchotechLLM 服務執行個體。
        /// </summary>
        public static IArchotechLLM Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("[ArchotechNexus] SDK 尚未初始化。請確認 Archotech Nexus 基礎框架模組已啟用。");
                }
                return _instance;
            }
        }

        /// <summary>
        /// 提供內部初始化 IArchotechLLM 實作實例的方法。
        /// </summary>
        internal static void Initialize(IArchotechLLM manager)
        {
            _instance = manager;
        }

        /// <summary>
        /// 註冊呼叫端 Mod。內部使用 Assembly.GetCallingAssembly() 獲取呼叫端組件並進行安全綁定。
        /// </summary>
        /// <param name="modId">呼叫端 Mod 的唯一識別碼</param>
        public static void RegisterClient(string modId)
        {
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            ClientRegistry.RegisterClient(modId, callingAssembly);
        }
    }
}
