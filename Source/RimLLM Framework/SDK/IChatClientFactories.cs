using System;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.SDK
{
    public interface IOpenAIChatClientFactory
    {
        IChatClient Create(string apiKey, string model, string endpoint = null);
    }

    public interface IGeminiChatClientFactory
    {
        IChatClient Create(string apiKey, string model);
    }

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
        Action<ChatOptions> CreateChatOptionsCustomizer(LLMRequest request, string model);
    }

    public interface INativeStructuredOutputProvider
    {
        Task<string> GenerateStructuredAsync(LLMRequest request, string model);
    }
}
