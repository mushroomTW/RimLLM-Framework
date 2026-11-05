using System;
using System.Reflection;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Providers;

/// <summary>
/// OpenAI SDK 的 <see cref="ChatCompletionOptions.Patch"/>
/// 內部註冊了未對 null 做防護的 PropagateSet/PropagateGet 委派。
/// 當對根屬性（如 $.response_format、$.stream_options）操作 Patch 時，SDK 會直接對為 null 的子屬性調用
/// 其 .Patch 屬性而擲出 <see cref="NullReferenceException"/>。
///
/// 此擴充方法在對 Patch 進行自訂修改前，透過反射清空 _patch 的傳播委派，使其還原為直接寫入底層 JSON Patch 屬性字典的行為。
/// </summary>
internal static class OpenAIPatchExtensions
{
    private static readonly FieldInfo PatchField =
        typeof(ChatCompletionOptions).GetField("_patch", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo PropagatorSetterField =
        PatchField?.FieldType.GetField("_propagatorSetter", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo PropagatorGetterField =
        PatchField?.FieldType.GetField("_propagatorGetter", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// 停用 <see cref="ChatCompletionOptions"/> 內部 <see cref="System.ClientModel.Primitives.JsonPatch"/> 的屬性傳播器，
    /// 避免 OpenAI SDK 在子屬性為 null 時拋出 NullReferenceException。
    /// </summary>
    public static ChatCompletionOptions DisablePatchPropagators(this ChatCompletionOptions options)
    {
        if (options == null) return options;

        if (!CanDisablePropagators)
        {
            WarnOnce("private JsonPatch fields were not found");
            return options;
        }

        try
        {
            object patchObj = PatchField.GetValue(options);
            if (patchObj != null)
            {
                PropagatorSetterField.SetValue(patchObj, null);
                PropagatorGetterField?.SetValue(patchObj, null);
                PatchField.SetValue(options, patchObj);
            }
        }
        catch (Exception ex)
        {
            // 反射失敗不擋請求：SDK 可能已修掉這個問題，但要留下線索，否則之後的 NRE 無從追查。
            WarnOnce(ex.Message);
        }

        return options;
    }

    /// <summary>目前的 SDK 版本是否還有這段反射依賴的私有欄位。升級 SDK 時由測試把關。</summary>
    internal static bool CanDisablePropagators => PatchField != null && PropagatorSetterField != null;

    private static int _warned;

    private static void WarnOnce(string reason)
    {
        if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0)
        {
            RimLLMLog.Warning($"[RimLLM] Could not disable OpenAI SDK patch propagators ({reason}); requests may fail if the SDK still has the null-propagation bug.");
        }
    }

    /// <summary>
    /// 從基礎工廠或新建實例中取得已停用傳播器的 <see cref="ChatCompletionOptions"/>。
    /// </summary>
    public static ChatCompletionOptions GetOrCreateSanitizedOptions(
        Func<IChatClient, object> baseFactory,
        IChatClient client)
    {
        return (baseFactory?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions()).DisablePatchPropagators();
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
            ChatCompletionOptions sanitized = GetOrCreateSanitizedOptions(baseFactory, client);
            mutate?.Invoke(sanitized);
            return sanitized;
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
