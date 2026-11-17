using System;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace RimLLM_Framework.Providers;

/// <summary>
/// 串接 <see cref="ChatOptions.RawRepresentationFactory"/> 的 <see cref="ChatCompletionOptions.Patch"/> 修改。
///
/// 早期 OpenAI SDK（2.13）在 Patch 操作根屬性時會對 null 子屬性傳播而擲 NRE，當時以反射清空私有傳播委派繞過；
/// 2.15 起已不再發生，反射已移除。本專案用到的 Patch 操作由 OpenAIPatchCompatibilityTests 釘住，升級 SDK 時把關。
/// </summary>
internal static class OpenAIPatchExtensions
{
    /// <summary>
    /// 取得基礎工廠產生的 <see cref="ChatCompletionOptions"/>；工廠不存在或型別不符時建立新實例。
    /// </summary>
    public static ChatCompletionOptions GetOrCreateOptions(
        Func<IChatClient, object> baseFactory,
        IChatClient client)
    {
        return baseFactory?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();
    }

    /// <summary>
    /// 在既有工廠外再包一層 Patch 修改：先沿用基底工廠（含思考參數與 max_tokens 改寫），
    /// 再執行 <paramref name="mutate"/> 的增量修改。直接覆寫工廠會把基底的 Patch 一併丟掉，
    /// 因此各供應商一律走這裡串接而非取代。
    /// </summary>
    public static Func<IChatClient, object> ChainPatch(
        Func<IChatClient, object> baseFactory,
        Action<ChatCompletionOptions> mutate)
    {
        return client =>
        {
            ChatCompletionOptions options = GetOrCreateOptions(baseFactory, client);
            mutate?.Invoke(options);
            return options;
        };
    }
}

/// <summary>
/// Patch 常用的 JSON 路徑，預先編碼成 UTF-8。路徑字面值固定，每請求重新
/// <c>Encoding.UTF8.GetBytes</c> 只是製造短命配置；陣列唯讀不改，跨請求共用安全。
/// </summary>
internal static class OpenAIPatchPaths
{
    public static readonly byte[] ResponseFormat = System.Text.Encoding.UTF8.GetBytes("$.response_format");
    public static readonly byte[] MaxCompletionTokens = System.Text.Encoding.UTF8.GetBytes("$.max_completion_tokens");
    public static readonly byte[] MaxTokens = System.Text.Encoding.UTF8.GetBytes("$.max_tokens");
    public static readonly byte[] ReasoningEffort = System.Text.Encoding.UTF8.GetBytes("$.reasoning_effort");
    public static readonly byte[] Reasoning = System.Text.Encoding.UTF8.GetBytes("$.reasoning");
    public static readonly byte[] Thinking = System.Text.Encoding.UTF8.GetBytes("$.thinking");
    public static readonly byte[] EnableThinking = System.Text.Encoding.UTF8.GetBytes("$.enable_thinking");
    public static readonly byte[] ThinkingBudget = System.Text.Encoding.UTF8.GetBytes("$.thinking_budget");
    public static readonly byte[] StreamOptions = System.Text.Encoding.UTF8.GetBytes("$.stream_options");
    public static readonly byte[] Model = System.Text.Encoding.UTF8.GetBytes("$.model");
    public static readonly byte[] Models = System.Text.Encoding.UTF8.GetBytes("$.models");
}
