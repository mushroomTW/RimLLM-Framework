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
    /// IArchotechLLM 介面的核心管理器實作。
    /// 統一調度 API 供應商、執行雙重 Fallback 容錯、校驗調用者來源、並對結構化輸出進行 JSON 修復。
    /// </summary>
    public class ArchotechLLMManager : IArchotechLLM
    {
        private readonly Dictionary<string, ILLMProvider> _providers = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Type> _registeredTypes = new HashSet<Type>();

        public ArchotechLLMManager()
        {
            // 初始化並註冊四大供應商
            RegisterProvider(new OpenAIProvider());
            RegisterProvider(new GeminiProvider());
            RegisterProvider(new DeepSeekProvider());
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
                throw new ArchotechException(LLMError.InvalidKey, $"[ArchotechNexus] 安全驗證失敗。ModId '{request.ModId}' 的組件驗證不通過。");
            }

            // 2. 獲取全域設定的 Fallback Chain
            var settings = ArchotechNexusMod.Settings;
            var fallbackChain = settings.FallbackChain;

            if (fallbackChain == null || fallbackChain.Count == 0)
            {
                throw new ArchotechException(LLMError.ProviderOffline, "沒有設定任何有效的 API 供應商 Fallback 鏈。");
            }

            Exception lastException = null;

            // 3. 依據 Fallback Chain 進行輪詢嘗試 (Provider Fallback)
            foreach (string providerId in fallbackChain)
            {
                if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
                    continue;

                // 檢查該 Provider 是否啟用
                if (!settings.IsProviderEnabled(providerId))
                    continue;

                string apiKey = settings.GetApiKey(providerId);
                // OpenAICompatible 放寬金鑰要求，其餘則必須提供 API Key
                if (string.IsNullOrEmpty(apiKey) && providerId != "OpenAICompatible")
                    continue;

                // 獲取該 Provider 配置的模型列表
                var models = settings.GetModelList(providerId);
                if (models == null || models.Count == 0)
                    continue;

                // 依序嘗試主要模型與備用模型 (Model Fallback)
                foreach (string model in models)
                {
                    try
                    {
                        ArchotechLog.Message($"[ArchotechNexus] 嘗試呼叫供應商: {providerId} (模型: {model})");
                        string result = await provider.GenerateAsync(request, model).ConfigureAwait(false);
                        return result;
                    }
                    catch (ArchotechException ex) when (ex.Error == LLMError.InvalidKey)
                    {
                        // 金鑰無效為永久配置問題，跳過 Model Fallback，直接進入下一個 Provider
                        ArchotechLog.Warning($"[ArchotechNexus] 供應商 {providerId} 金鑰失效，跳過此供應商: {ex.Message}");
                        lastException = ex;
                        break;
                    }
                    catch (Exception ex)
                    {
                        ArchotechLog.Warning($"[ArchotechNexus] 供應商 {providerId} (模型: {model}) 請求異常: {ex.Message}。準備 Fallback。");
                        lastException = ex;
                    }
                }
            }

            throw new ArchotechException(
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
                ArchotechLog.Warning($"[ArchotechNexus] 第一次 JSON 解析失敗，嘗試極限修補。原始回覆: {rawResponse}\n修復後: {repairedJson}\n錯誤: {ex.Message}");
                try
                {
                    string fallbackExtracted = ExtractJsonBlock(repairedJson);
                    T result = JsonConvert.DeserializeObject<T>(fallbackExtracted);
                    return result;
                }
                catch (Exception finalEx)
                {
                    throw new ArchotechException(
                        LLMError.InvalidResponse, 
                        $"無法將 LLM 回應解析為目標物件 {typeof(T).Name}。\n原始回覆: {rawResponse}\n解析錯誤: {finalEx.Message}", 
                        finalEx);
                }
            }
        }

        public async IAsyncEnumerable<string> StreamAsync(LLMRequest request)
        {
            // 1. 來源身分校驗
            Assembly callingAssembly = Assembly.GetCallingAssembly();
            if (!ClientRegistry.Verify(request.ModId, callingAssembly))
            {
                throw new ArchotechException(LLMError.InvalidKey, $"[ArchotechNexus] 來源驗證失敗。ModId '{request.ModId}' 的組件驗證不通過。");
            }

            var settings = ArchotechNexusMod.Settings;
            var fallbackChain = settings.FallbackChain;

            if (fallbackChain == null || fallbackChain.Count == 0)
            {
                throw new ArchotechException(LLMError.ProviderOffline, "沒有設定任何有效的 API 供應商 Fallback 鏈。");
            }

            ILLMProvider activeProvider = null;
            string activeModel = null;

            // 尋找第一個可用的 Provider 與其首選模型
            foreach (string providerId in fallbackChain)
            {
                if (!_providers.TryGetValue(providerId, out ILLMProvider provider))
                    continue;

                if (!settings.IsProviderEnabled(providerId))
                    continue;

                string apiKey = settings.GetApiKey(providerId);
                if (string.IsNullOrEmpty(apiKey) && providerId != "OpenAICompatible")
                    continue;

                var models = settings.GetModelList(providerId);
                if (models == null || models.Count == 0)
                    continue;

                activeProvider = provider;
                activeModel = models[0];
                break;
            }

            if (activeProvider == null)
            {
                throw new ArchotechException(LLMError.ProviderOffline, "無可用之已配置 API 供應商。");
            }

            // 讀取並轉發串流
            IAsyncEnumerator<string> enumerator = activeProvider.StreamAsync(request, activeModel).GetAsyncEnumerator();
            while (true)
            {
                string chunk = null;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        break;
                    chunk = enumerator.Current;
                }
                catch (Exception ex)
                {
                    ArchotechLog.Error($"[ArchotechNexus] 串流傳輸中途異常: {ex.Message}");
                    throw new ArchotechException(LLMError.NetworkError, "串流傳輸中斷", ex);
                }

                yield return chunk;
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

        public void RegisterResponseType<T>()
        {
            lock (_registeredTypes)
            {
                _registeredTypes.Add(typeof(T));
                ArchotechLog.Message($"[ArchotechNexus] 註冊結構化回應型別: {typeof(T).FullName}");
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
