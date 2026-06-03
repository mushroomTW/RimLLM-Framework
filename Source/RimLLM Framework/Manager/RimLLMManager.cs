using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;
using RimLLM_Framework.Providers;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// IRimLLM 介面的核心管理器實作。
    /// 統一調度 API 供應商、執行雙重 Fallback 容錯、校驗調用者來源、並對結構化輸出進行 JSON 修復。
    /// </summary>
    public class RimLLMManager : IRimLLM
    {
        private readonly Dictionary<string, ILLMProvider> _providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Type> _registeredTypes = new HashSet<Type>();

        public RimLLMManager()
        {
            // 初始化並註冊三大供應商
            RegisterProvider(new OpenAIProvider());
            RegisterProvider(new GeminiProvider());
            RegisterProvider(new OpenAICompatibleProvider());
        }

        private void RegisterProvider(ILLMProvider provider)
        {
            _providers[provider.ProviderId] = provider;
        }

        public async Task<string> GenerateAsync(LLMRequest request)
        {
            // 1. 來源身分安全校驗 (Caller Verification)
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            if (!ClientRegistry.Verify(request.ModId, callingAssembly))
            {
                throw new RimLLMException(LLMError.InvalidKey, $"[RimLLM] 安全驗證失敗。ModId '{request.ModId}' 的組件驗證不通過。");
            }

            // 2. 獲取全域設定的 Fallback Chain
            var settings = RimLLMFrameworkMod.Settings;
            var fallbackChain = settings.FallbackChain;

            if (fallbackChain == null || fallbackChain.Count == 0)
            {
                throw new RimLLMException(LLMError.ProviderOffline, "沒有設定任何有效的 API 供應商 Fallback 鏈。");
            }

            Exception lastException = null;

            // 3. 依據 Fallback Chain 進行模型級輪詢嘗試
            foreach (string entry in fallbackChain)
            {
                if (string.IsNullOrEmpty(entry))
                    continue;

                int colonIndex = entry.IndexOf(':');
                if (colonIndex <= 0)
                    continue;

                string providerId = entry.Substring(0, colonIndex);
                string modelName = entry.Substring(colonIndex + 1);

                if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
                    continue;

                // 檢查該 Provider 是否啟用
                if (!settings.IsProviderEnabled(providerId))
                    continue;

                string apiKey = settings.GetApiKey(providerId);
                // OpenAICompatible 放寬金鑰要求，其餘則必須提供 API Key
                if (string.IsNullOrEmpty(apiKey) && providerId != "OpenAICompatible")
                    continue;

                try
                {
                    RimLLMLog.Message($"[RimLLM] 嘗試呼叫供應商: {providerId} (模型: {modelName})");
                    string result = await provider.GenerateAsync(request, modelName).ConfigureAwait(false);
                    return result;
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] 供應商 {providerId} (模型: {modelName}) 請求異常: {ex.Message}。準備 Fallback。");
                    lastException = ex;
                }
            }

            throw new RimLLMException(
                LLMError.Unknown, 
                $"所有 Fallback 嘗試皆已失敗。最後一次錯誤原因: {lastException?.Message}", 
                lastException);
        }

        public async Task<T> GenerateObjectAsync<T>(LLMRequest request)
        {
            request.ResponseType = typeof(T);

            // 在 SystemPrompt 後方附加 JSON schema 格式指示
            string schemaInstructions = $"\n\n[CRITICAL REQUIREMENT: You MUST respond ONLY with a raw JSON object matching the following structure. Do not include markdown code block syntax (like ```json). Just the raw json text. Example:\n{GetSampleJson<T>()}]";
            
            string originalSystemPrompt = request.SystemPrompt ?? "";
            request.SystemPrompt = originalSystemPrompt + schemaInstructions;

            string rawResponse = await GenerateAsync(request).ConfigureAwait(false);
            
            // 執行 JSON 修復
            string repairedJson = RepairJson(rawResponse);

            try
            {
                T result = JsonConvert.DeserializeObject<T>(repairedJson);
                return result;
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"[RimLLM] 第一次 JSON 解析失敗，嘗試極限修補。原始回覆: {rawResponse}\n修復後: {repairedJson}\n錯誤: {ex.Message}");
                try
                {
                    string fallbackExtracted = ExtractJsonBlock(repairedJson);
                    T result = JsonConvert.DeserializeObject<T>(fallbackExtracted);
                    return result;
                }
                catch (Exception finalEx)
                {
                    throw new RimLLMException(
                        LLMError.InvalidResponse, 
                        $"無法將 LLM 回應解析為目標物件 {typeof(T).Name}。\n原始回覆: {rawResponse}\n解析錯誤: {finalEx.Message}", 
                        finalEx);
                }
            }
        }

        public async Task StreamAsync(LLMRequest request, Action<string> onChunkReceived)
        {
            // 1. 來源身分校驗
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            if (!ClientRegistry.Verify(request.ModId, callingAssembly))
            {
                throw new RimLLMException(LLMError.InvalidKey, $"[RimLLM] 來源驗證失敗。ModId '{request.ModId}' 的組件驗證不通過。");
            }

            var settings = RimLLMFrameworkMod.Settings;
            var fallbackChain = settings.FallbackChain;

            if (fallbackChain == null || fallbackChain.Count == 0)
            {
                throw new RimLLMException(LLMError.ProviderOffline, "沒有設定 any 有效的 API 供應商 Fallback 鏈。");
            }

            bool connected = false;
            Exception lastException = null;

            // 尋找第一個可用的 Provider 與其模型，並嘗試建立連線與串流
            foreach (string entry in fallbackChain)
            {
                if (string.IsNullOrEmpty(entry))
                    continue;

                int colonIndex = entry.IndexOf(':');
                if (colonIndex <= 0)
                    continue;

                string providerId = entry.Substring(0, colonIndex);
                string modelName = entry.Substring(colonIndex + 1);

                if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
                    continue;

                if (!settings.IsProviderEnabled(providerId))
                    continue;

                string apiKey = settings.GetApiKey(providerId);
                if (string.IsNullOrEmpty(apiKey) && providerId != "OpenAICompatible")
                    continue;

                try
                {
                    await provider.StreamAsync(request, modelName, onChunkReceived).ConfigureAwait(false);
                    connected = true;
                    break;
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] 串流連線失敗: {providerId} ({modelName}) -> {ex.Message}，將嘗試下一個 Fallback。");
                    lastException = ex;
                }
            }

            if (!connected)
            {
                throw new RimLLMException(LLMError.ProviderOffline, $"所有 Fallback 嘗試皆已失敗，無法建立串流連線。最後錯誤: {lastException?.Message}", lastException);
            }
        }

        public async Task<TestResult> TestProviderAsync(string providerId)
        {
            if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
            {
                return new TestResult
                {
                    Success = false,
                    Provider = providerId,
                    ErrorMessage = $"未知的供應商 ID: {providerId}",
                    ErrorCode = LLMError.ProviderOffline
                };
            }

            return await provider.TestConnectionAsync().ConfigureAwait(false);
        }

        public async Task<List<string>> FetchProviderModelsAsync(string providerId)
        {
            if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
            {
                throw new RimLLMException(LLMError.ProviderOffline, $"未知的供應商 ID: {providerId}");
            }

            return await provider.FetchAvailableModelsAsync().ConfigureAwait(false);
        }

        public void RegisterResponseType<T>()
        {
            lock (_registeredTypes)
            {
                _registeredTypes.Add(typeof(T));
                RimLLMLog.Message($"[RimLLM] 註冊結構化回應型別: {typeof(T).FullName}");
            }
        }

        #region Helper Methods

        private string GetSampleJson<T>()
        {
            try
            {
                var instance = Activator.CreateInstance(typeof(T));
                return JsonConvert.SerializeObject(instance, Formatting.None);
            }
            catch
            {
                return "{}";
            }
        }

        private string RepairJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return json;

            json = json.Trim();

            // 1. 移除 Markdown 標記
            if (json.StartsWith("```"))
            {
                int startIndex = json.IndexOf('\n');
                if (startIndex != -1)
                {
                    json = json.Substring(startIndex + 1);
                }
                else
                {
                    json = json.Substring(3);
                }
            }
            if (json.EndsWith("```"))
            {
                json = json.Substring(0, json.Length - 3);
            }
            json = json.Trim();

            // 2. 移除尾隨逗號
            json = Regex.Replace(json, @",\s*([\]}])", "$1");

            // 3. 補齊缺失括號
            int braceCount = 0;
            int bracketCount = 0;
            foreach (char c in json)
            {
                if (c == '{') braceCount++;
                else if (c == '}') braceCount--;
                else if (c == '[') bracketCount++;
                else if (c == ']') bracketCount--;
            }

            if (braceCount > 0)
            {
                json += new string('}', braceCount);
            }
            if (bracketCount > 0)
            {
                json += new string(']', bracketCount);
            }

            return json;
        }

        private string ExtractJsonBlock(string input)
        {
            var match = Regex.Match(input, @"(\{.*\}|\[.*\])", RegexOptions.Singleline);
            if (match.Success)
            {
                return match.Value;
            }
            return input;
        }

        #endregion
    }
}
