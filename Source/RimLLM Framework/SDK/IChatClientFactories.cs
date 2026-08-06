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

    public interface INativeStructuredOutputProvider
    {
        Task<string> GenerateStructuredAsync(LLMRequest request, string model);
    }
}
