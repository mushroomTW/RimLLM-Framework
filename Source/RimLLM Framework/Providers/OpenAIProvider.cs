using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// OpenAI API 供應商，支援 Chat Completion 與 SSE 串流。
    /// </summary>
    public class OpenAIProvider : BaseHttpProvider
    {
        public override string ProviderId => "OpenAI";

        public override async Task<string> GenerateAsync(LLMRequest request, string model)
        {
            var settings = ArchotechNexusMod.Settings;
            string apiKey = settings.GetApiKey(ProviderId);
            string endpoint = settings.GetEndpoint(ProviderId, "https://api.openai.com/v1/chat/completions");

            var messages = new JArray();
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                messages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = request.SystemPrompt
                });
            }
            messages.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = request.Prompt
            });

            var payload = new JObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["temperature"] = request.Temperature,
                ["max_tokens"] = request.MaxTokens
            };

            string responseJson = await SendPostAsync(endpoint, payload.ToString(), apiKey).ConfigureAwait(false);

            try
            {
                var responseObj = JObject.Parse(responseJson);
                var content = responseObj["choices"]?[0]?["message"]?["content"]?.ToString();
                if (content == null)
                {
                    throw new ArchotechException(LLMError.InvalidResponse, "OpenAI 回傳的 JSON 中缺少 content 欄位");
                }
                return content;
            }
            catch (Exception ex) when (!(ex is ArchotechException))
            {
                throw new ArchotechException(LLMError.InvalidResponse, $"解析 OpenAI 回應失敗: {ex.Message}", ex);
            }
        }

        public override async IAsyncEnumerable<string> StreamAsync(LLMRequest request, string model)
        {
            var settings = ArchotechNexusMod.Settings;
            string apiKey = settings.GetApiKey(ProviderId);
            string endpoint = settings.GetEndpoint(ProviderId, "https://api.openai.com/v1/chat/completions");

            var messages = new JArray();
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                messages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = request.SystemPrompt
                });
            }
            messages.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = request.Prompt
            });

            var payload = new JObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["temperature"] = request.Temperature,
                ["max_tokens"] = request.MaxTokens,
                ["stream"] = true
            };

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage response = null;
            try
            {
                response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                response?.Dispose();
                throw new ArchotechException(LLMError.NetworkError, $"OpenAI Stream 請求失敗: {ex.Message}", ex);
            }

            using (response)
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream))
            {
                while (!reader.EndOfStream)
                {
                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) continue;
                    line = line.Trim();

                    if (line == "data: [DONE]")
                        break;

                    if (line.StartsWith("data: "))
                    {
                        string json = line.Substring(6);
                        string content = null;
                        try
                        {
                            var token = JObject.Parse(json);
                            content = token["choices"]?[0]?["delta"]?["content"]?.ToString();
                        }
                        catch
                        {
                            // 忽略損毀或心跳包等非 JSON 片段
                        }

                        if (!string.IsNullOrEmpty(content))
                        {
                            yield return content;
                        }
                    }
                }
            }
        }

        public override async Task<TestResult> TestConnectionAsync()
        {
            var settings = ArchotechNexusMod.Settings;
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
                string testModel = settings.GetDefaultModel(ProviderId, "gpt-4o-mini");

                string content = await GenerateAsync(request, testModel).ConfigureAwait(false);
                stopwatch.Stop();

                result.Success = true;
                result.Model = testModel;
                result.LatencyMs = stopwatch.ElapsedMilliseconds;
            }
            catch (ArchotechException ex)
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
    }
}
