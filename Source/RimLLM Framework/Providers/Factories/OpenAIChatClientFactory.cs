using System;
using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
#pragma warning disable S3878 // reason: 批次抑制 MINOR/INFO 規則，語意保留，重構風險高於收益，維持現狀

namespace RimLLM_Framework.Providers
{
    /// <summary>使用 OpenAI 官方 SDK 建立 MEAI Chat Client。</summary>
    /// <remarks>相容 endpoint 只會在此 factory 正規化，不污染共用 manager。</remarks>
    public sealed class OpenAIChatClientFactory : IOpenAIChatClientFactory
    {
        public IChatClient Create(string apiKey, string model, string endpoint = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("OpenAI API key 不得為空。", nameof(apiKey));
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("OpenAI model 不得為空。", nameof(model));

            var options = new OpenAIClientOptions();
            string normalizedEndpoint = NormalizeEndpoint(endpoint);
            if (!string.IsNullOrEmpty(normalizedEndpoint))
                options.Endpoint = new Uri(normalizedEndpoint, UriKind.Absolute);

            var client = new ChatClient(model, new ApiKeyCredential(apiKey), options);
            return client.AsIChatClient();
        }

        public static string NormalizeEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            string normalized = endpoint.Trim().TrimEnd(new char[] { '/' });
            const string suffix = "/chat/completions";
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - suffix.Length).TrimEnd(new char[] { '/' });
            return normalized;
        }
    }
#pragma warning restore S3878
}