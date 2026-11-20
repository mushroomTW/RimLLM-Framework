using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Providers;

/// <summary>
/// 為 OpenRouter 請求加上應用歸屬標頭，讓 OpenRouter 後台的 App 欄位顯示呼叫端，而不是 Unknown。
/// </summary>
/// <remarks>
/// OpenAI SDK 沒有逐請求設定標頭的選項，<see cref="ClientPipelineOptions.AddPolicy"/> 是它提供的擴充點。
/// 標頭名稱見 https://openrouter.ai/docs/app-attribution（<c>X-Title</c> 僅為向下相容保留）。
/// </remarks>
internal sealed class OpenRouterAttributionPolicy : PipelinePolicy
{
    public static readonly OpenRouterAttributionPolicy Instance = new();

    /// <summary>OpenRouter API 的主機名稱。</summary>
    public const string Host = "openrouter.ai";

    public const string RefererHeader = "HTTP-Referer";
    public const string TitleHeader = "X-OpenRouter-Title";

    /// <summary>位址是否指向 OpenRouter。供不經由 OpenRouterProvider 的路徑（embedding、決策）判斷要不要加標頭。</summary>
    public static bool Targets(Uri? uri)
    {
        return uri != null && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase);
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private static void Apply(PipelineMessage message)
    {
        RimLLMAppAttribution attribution = RimLLMAppAttribution.Current ?? RimLLMAppAttribution.Framework;
        message.Request.Headers.Set(RefererHeader, attribution.Url);
        message.Request.Headers.Set(TitleHeader, attribution.Title);
    }
}
