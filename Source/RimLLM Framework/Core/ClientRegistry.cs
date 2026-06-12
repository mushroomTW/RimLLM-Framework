using System;
using System.Collections.Generic;
using System.Reflection;

namespace RimLLM_Framework.Core
{
    /// <summary>
    /// 管理外部 Mod 註冊與呼叫來源校驗，降低 ModId 誤用與來源混淆 (Caller Verification)。
    /// </summary>
    public static class ClientRegistry
    {
        private static readonly Dictionary<string, Assembly> RegisteredClients = new Dictionary<string, Assembly>();
        private static readonly object LockObj = new object();
        private static readonly Assembly FrameworkAssembly = typeof(ClientRegistry).Assembly;

        /// <summary>
        /// 註冊客戶端 Mod。將 Mod ID 與呼叫者的 Assembly 進行綁定。
        /// </summary>
        /// <param name="modId">Mod 唯一識別碼</param>
        /// <param name="callingAssembly">呼叫者的 Assembly</param>
        public static void RegisterClient(string modId, Assembly callingAssembly)
        {
            if (string.IsNullOrEmpty(modId))
                throw new ArgumentException("ModId cannot be empty or null", nameof(modId));

            if (callingAssembly == null)
                throw new ArgumentNullException(nameof(callingAssembly));

            lock (LockObj)
            {
                if (RegisteredClients.TryGetValue(modId, out Assembly existingAssembly))
                {
                    if (existingAssembly != callingAssembly)
                    {
                        throw new InvalidOperationException(
                            $"[RimLLM] Security conflict: ModId '{modId}' is already registered by another assembly ({existingAssembly.GetName().Name}), assembly ({callingAssembly.GetName().Name}) tried to register again.");
                    }
                    return;
                }
 
                RegisteredClients[modId] = callingAssembly;
                RimLLMLog.Message($"[RimLLM] Registered client Mod: {modId} (Assembly: {callingAssembly.GetName().Name})");
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
 
            // 若為 RimLLM Framework 自身組件，直接放行 (例如內部呼叫 GenerateObjectAsync -> GenerateAsync)
            if (callingAssembly == FrameworkAssembly)
            {
                return true;
            }

            lock (LockObj)
            {
                if (RegisteredClients.TryGetValue(modId, out Assembly registeredAssembly))
                {
                    return registeredAssembly == callingAssembly;
                }
 
                RimLLMLog.Warning($"[RimLLM] Unregistered API call detected. Mod: {modId} (Assembly: {callingAssembly.GetName().Name}) is not registered. Please call RimLLMProvider.RegisterClient first.");
                return false;
            }
        }
    }
}
