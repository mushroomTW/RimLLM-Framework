using System;
using Google.GenAI;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Providers
{
    /// <summary>使用 Google GenAI 官方 SDK 建立 Gemini Developer API 的 MEAI adapter。</summary>
    public sealed class GeminiChatClientFactory : IGeminiChatClientFactory
    {
        public IChatClient Create(string apiKey, string model)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("Gemini API key 不得為空。", nameof(apiKey));
            }
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentException("Gemini model 不得為空。", nameof(model));
            }

            var client = new Client(apiKey: apiKey);
            return client.AsIChatClient(model);
        }
    }
}
