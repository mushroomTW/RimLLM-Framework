#nullable disable

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 把此 Mod 的應用歸屬放進 <see cref="RimLLMAppAttribution.Current"/>，
/// 讓下游的 OpenRouter pipeline policy 送出對應標頭。
/// </summary>
/// <remarks>
/// 兩個入口都必須是 async 方法：AsyncLocal 在 async 方法內設定時只隨該次呼叫往下游流動，
/// 返回時自動還原；在一般方法內設定則會殘留在呼叫端的執行上下文。
/// </remarks>
internal sealed class RimLLMAttributionChatClient(IChatClient innerClient, RimLLMAppAttribution attribution)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        CancellationToken cancellationToken = default)
    {
        RimLLMAppAttribution.Current = attribution;
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // HTTP 請求在第一次列舉時送出，此時值已就位。
        RimLLMAppAttribution.Current = attribution;
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }
}
