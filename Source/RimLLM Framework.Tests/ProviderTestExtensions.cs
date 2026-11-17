using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 測試專用捷徑：直接以供應商的 IChatClient 發送一次請求並取回正文，
/// 用來檢查各供應商實際送出的 wire payload，不經過路由、重試與用量管線。
/// </summary>
internal static class ProviderTestExtensions
{
    public static async Task<string> GenerateAsync(
        this ILLMProvider provider,
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        string model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        using IChatClient client = provider.CreateChatClient(model);
        ChatResponse response = await client.GetResponseAsync(messages, options).ConfigureAwait(false);
        return response?.Text ?? string.Empty;
    }
}
