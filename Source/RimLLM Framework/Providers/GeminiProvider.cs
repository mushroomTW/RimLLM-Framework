using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// Google Gemini API 供應商，支援 generateContent 與 streamGenerateContent。
    /// </summary>
    public class GeminiProvider : BaseHttpProvider
    {
        public override string ProviderId => "Gemini";

        public override async Task<string> GenerateAsync(LLMRequest request, string model)
        {
            var settings = RimLLMFrameworkMod.Settings;
            string apiKey = settings.GetApiKey(ProviderId);
            string baseEndpoint = settings.GetEndpoint(ProviderId, "https://generativelanguage.googleapis.com/v1beta");
            string url = $"{baseEndpoint}/models/{model}:generateContent?key={apiKey}";

            var contents = new JArray
            {
                new JObject
                {
                    ["parts"] = new JArray
                    {
                        new JObject { ["text"] = request.Prompt }
                    }
                }
            };

            var payload = new JObject
            {
                ["contents"] = contents,
                ["generationConfig"] = new JObject
                {
                    ["temperature"] = request.Temperature,
                    ["maxOutputTokens"] = request.MaxTokens
                }
            };

            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                payload["systemInstruction"] = new JObject
                {
                    ["parts"] = new JArray
                    {
                        new JObject { ["text"] = request.SystemPrompt }
                    }
                };
            }

            string responseJson = await SendPostAsync(url, payload.ToString(), apiKey, "Gemini").ConfigureAwait(false);

            try
            {
                var responseObj = JObject.Parse(responseJson);
                var text = responseObj["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
                if (text == null)
                {
                    throw new RimLLMException(LLMError.InvalidResponse, "Gemini 回傳的 JSON 中缺少 text 欄位");
                }
                return text;
            }
            catch (Exception ex) when (!(ex is RimLLMException))
            {
                throw new RimLLMException(LLMError.InvalidResponse, $"解析 Gemini 回應失敗: {ex.Message}", ex);
            }
        }

        public override async Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived)
        {
            var settings = RimLLMFrameworkMod.Settings;
            string apiKey = settings.GetApiKey(ProviderId);
            string baseEndpoint = settings.GetEndpoint(ProviderId, "https://generativelanguage.googleapis.com/v1beta");
            string url = $"{baseEndpoint}/models/{model}:streamGenerateContent?key={apiKey}";

            var contents = new JArray
            {
                new JObject
                {
                    ["parts"] = new JArray
                    {
                        new JObject { ["text"] = request.Prompt }
                    }
                }
            };

            var payload = new JObject
            {
                ["contents"] = contents,
                ["generationConfig"] = new JObject
                {
                    ["temperature"] = request.Temperature,
                    ["maxOutputTokens"] = request.MaxTokens
                }
            };

            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                payload["systemInstruction"] = new JObject
                {
                    ["parts"] = new JArray
                    {
                        new JObject { ["text"] = request.SystemPrompt }
                    }
                };
            }

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");

            HttpResponseMessage response = null;
            try
            {
                response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                response?.Dispose();
                throw new RimLLMException(LLMError.NetworkError, $"Gemini Stream 請求失敗: {ex.Message}", ex);
            }

            // Gemini 的 streamGenerateContent 返回一個 JSON 陣列格式。
            // 使用正則表達式快速且安全地提取 "text" 值，防範部分轉義或空格異常。
            var textRegex = new Regex(@"\""text\""\s*:\s*\""([^\""\\]*(?:\\.[^\""\\]*)*)\""");

            using (response)
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream))
            {
                while (!reader.EndOfStream)
                {
                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) continue;

                    var match = textRegex.Match(line);
                    if (match.Success)
                    {
                        string rawText = match.Groups[1].Value;
                        string unescapedText = Regex.Unescape(rawText);
                        if (!string.IsNullOrEmpty(unescapedText))
                        {
                            onChunkReceived?.Invoke(unescapedText);
                        }
                    }
                }
            }
        }

        public override async Task<TestResult> TestConnectionAsync()
        {
            var settings = RimLLMFrameworkMod.Settings;
            string apiKey = settings.GetApiKey(ProviderId);
            if (string.IsNullOrEmpty(apiKey))
            {
                return new TestResult { Success = false, Provider = ProviderId, ErrorMessage = "未設定 API 金鑰", ErrorCode = LLMError.InvalidKey };
            }

            var result = new TestResult { Provider = ProviderId };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var request = new LLMRequest { Prompt = "ping", MaxTokens = 5 };
                string testModel = settings.GetDefaultModel(ProviderId, "gemini-2.5-flash");

                string content = await GenerateAsync(request, testModel).ConfigureAwait(false);
                stopwatch.Stop();

                result.Success = true;
                result.Model = testModel;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }
            catch (RimLLMException ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ErrorCode = ex.Error;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ErrorCode = LLMError.Unknown;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }

        public override async Task<List<string>> FetchAvailableModelsAsync()
        {
            var settings = RimLLMFrameworkMod.Settings;
            string apiKey = settings.GetApiKey(ProviderId);
            string baseEndpoint = settings.GetEndpoint(ProviderId, "https://generativelanguage.googleapis.com/v1beta");
            string url = $"{baseEndpoint.TrimEnd('/')}/models?key={apiKey}";

            string responseJson = await SendGetAsync(url, apiKey, "Gemini").ConfigureAwait(false);
            var list = new List<string>();
            try
            {
                var obj = JObject.Parse(responseJson);
                var modelsArray = obj["models"] as JArray;
                if (modelsArray != null)
                {
                    foreach (var item in modelsArray)
                    {
                        string name = item["name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            // 剝離 models/ 前綴
                            string cleanName = name.Replace("models/", "");
                            list.Add(cleanName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new RimLLMException(LLMError.InvalidResponse, $"獲取 Gemini 模型列表失敗: {ex.Message}", ex);
            }
            return list;
        }
    }
}
