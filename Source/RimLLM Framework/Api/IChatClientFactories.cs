using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework
{
#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀

    public interface IChatClientProvider
    {
        bool UsesIChatClient { get; }
        IChatClient CreateChatClient(string model);
        LLMProviderCapabilities Capabilities { get; }
    }

    /// <summary>
    /// 供應商專屬的 ChatOptions 客製化鉤子。
    /// 由 <see cref="RimLLMChatClientExecutor"/> 在送達 SDK 前最後套用，
    /// 可用於設定 reasoning effort、temperature 清空、或透過 RawRepresentationFactory 補入逃生門欄位。
    /// </summary>
    public interface IChatOptionsCustomizer
    {
        Action<ChatOptions> CreateChatOptionsCustomizer(ChatOptions options, string model);
    }

    public interface INativeStructuredOutputProvider
    {
        Task<string> GenerateStructuredAsync(IEnumerable<ChatMessage> messages, ChatOptions options, string model);
    }
#pragma warning restore S101, S2342
}