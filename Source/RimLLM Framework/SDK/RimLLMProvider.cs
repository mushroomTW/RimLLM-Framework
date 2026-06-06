using System;
using System.Reflection;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// RimLLM SDK 全域靜態入口。
    /// 其他 Mod 可以透過此類別註冊客戶端與取得 IRimLLM 服務執行個體。
    /// </summary>
    public static class RimLLMProvider
    {
        private static IRimLLM _instance;

        /// <summary>
        /// 取得 IRimLLM 服務執行個體。
        /// </summary>
        public static IRimLLM Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("[RimLLM] SDK has not been initialized. Please make sure the RimLLM Framework mod is active.");
                }
                return _instance;
            }
        }

        /// <summary>
        /// 提供內部初始化 IRimLLM 實作實例的方法。
        /// </summary>
        internal static void Initialize(IRimLLM manager)
        {
            _instance = manager;
        }

        /// <summary>
        /// 註冊呼叫端 Mod。內部使用 Assembly.GetCallingAssembly() 獲取呼叫端組件並進行安全綁定。
        /// </summary>
        /// <param name="modId">呼叫端 Mod 的唯一識別碼</param>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void RegisterClient(string modId)
        {
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            ClientRegistry.RegisterClient(modId, callingAssembly);
        }
    }
}
