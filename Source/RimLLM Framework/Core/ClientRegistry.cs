using System;
using System.Collections.Generic;
using System.Reflection;

namespace RimLLM_Framework.Core
{
    /// <summary>
    /// 管理外部 Mod 註冊與安全校驗，防止惡意 Mod 假冒其他 Mod ID (Caller Verification)。
    /// </summary>
    public static class ClientRegistry
    {
        private static readonly Dictionary<string, Assembly> RegisteredClients = new Dictionary<string, Assembly>();
        private static readonly object LockObj = new object();

        /// <summary>
        /// 註冊客戶端 Mod。將 Mod ID 與呼叫者的 Assembly 進行綁定。
        /// </summary>
        /// <param name="modId">Mod 唯一識別碼</param>
        /// <param name="callingAssembly">呼叫者的 Assembly</param>
        public static void RegisterClient(string modId, Assembly callingAssembly)
        {
            if (string.IsNullOrEmpty(modId))
                throw new ArgumentException("ModId 不能為空或 Null", nameof(modId));

            if (callingAssembly == null)
                throw new ArgumentNullException(nameof(callingAssembly));

            lock (LockObj)
            {
                if (RegisteredClients.TryGetValue(modId, out Assembly existingAssembly))
                {
                    if (existingAssembly != callingAssembly)
                    {
                        throw new InvalidOperationException(
                            $"[RimLLM] 安全衝突：ModId '{modId}' 已經被其他組件 ({existingAssembly.GetName().Name}) 註冊，組件 ({callingAssembly.GetName().Name}) 試圖重複註冊。");
                    }
                    return;
                }
 
                RegisteredClients[modId] = callingAssembly;
                RimLLMLog.Message($"[RimLLM] 註冊客戶端 Mod: {modId} (組件: {callingAssembly.GetName().Name})");
            }
        }
 
        /// <summary>
        /// 校驗目前呼叫者的 Assembly 是否與註冊的 Mod ID 吻合。
        /// </summary>
        /// <param name="modId">Mod 唯一識別碼</param>
        /// <param name="callingAssembly">當前 API 呼叫者的 Assembly</param>
        /// <returns>校驗是否通過</returns>
        public static bool Verify(string modId, Assembly callingAssembly)
        {
            if (string.IsNullOrEmpty(modId))
                return false;
 
            if (callingAssembly == null)
                return false;
 
            lock (LockObj)
            {
                if (RegisteredClients.TryGetValue(modId, out Assembly registeredAssembly))
                {
                    return registeredAssembly == callingAssembly;
                }
 
                RegisteredClients[modId] = callingAssembly;
                RimLLMLog.Message($"[RimLLM] 偵測到未註冊的 API 調用，已自動補註冊 Mod: {modId} (組件: {callingAssembly.GetName().Name})");
                return true;
            }
        }
    }
}
