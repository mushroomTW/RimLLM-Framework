using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Manager.Services;

/// <summary>
/// 一個決策模型供應商的預設端點。不提供預設模型：模型一律由玩家從伺服器抓回的清單選擇。
/// </summary>
public sealed class RimLLMDecisionProvider
{
    public RimLLMDecisionProvider(string id, string defaultEndpoint)
    {
        Id = id;
        DefaultEndpoint = defaultEndpoint;
    }

    /// <summary>穩定的內部識別碼，也是設定檔與語系鍵的一部分。</summary>
    public string Id { get; }

    /// <summary>TypeSafeClient 的 BaseUrl；沒有路徑的 origin。</summary>
    public string DefaultEndpoint { get; }

    /// <summary>
    /// 決策金鑰留空時沿用哪個對話供應商的金鑰（同一家公司、同一把金鑰）；null 代表不沿用。
    /// 服務的金鑰解析與設定精靈的一鍵開啟都讀這個欄位。
    /// </summary>
    public string? ChatKeyProviderId { get; init; }

    /// <summary>
    /// 是否開放玩家自訂端點。只有自架的本機服務（埠號因人而異）需要；
    /// 官方與聚合閘道的端點已固定，開放修改只會讓人填錯。
    /// </summary>
    public bool AllowCustomEndpoint { get; init; }
}

/// <summary>
/// 決策模型供應商清單。這些服務都接受 System One 的 state/questions 結構；
/// 其中 TypeSafe 是原生 Jev，其餘是提供相同 wire format 的託管服務或聚合閘道；
/// 唯一例外是 Gemini，由 <see cref="GeminiDecisionsAdapterHandler"/> 在傳輸層轉換格式。
/// </summary>
public static class RimLLMDecisionProviderCatalog
{
    public const string Disabled = "Disabled";
    /// <summary>TypeSafe AI（原生 Jev 服務）。</summary>
    public const string TypeSafe = "TypeSafe";
    public const string Clef = "Clef";
    public const string Liquid = "Liquid";
    public const string Solar = "Solar";
    public const string Kev = "Kev";
    public const string Tev = "Tev";
    public const string Laya = "Laya";
    public const string Span = "Span";
    public const string Microsoft = "Microsoft";
    public const string OpenAI = "OpenAI";
    /// <summary>Google Gemini Decision（Gemini Decisions API，經 <see cref="GeminiDecisionsAdapterHandler"/> 轉接）。</summary>
    public const string Gemini = "Gemini";
    /// <summary>OpenRouter Decisions API（經 <see cref="OpenRouterDecisionsAdapterHandler"/> 改寫路徑）。</summary>
    public const string OpenRouter = "OpenRouter";

    /// <summary>Ollama 0.35 以上內建的 Jev 風格決策介面（/v1/systemone）。</summary>
    public const string Ollama = "Ollama";

    /// <summary>Unsloth 以 TypeSafe 相容的 Jev API 提供本機 Laya／Clef。</summary>
    public const string Unsloth = "Unsloth";

    /// <summary>其他 System One 相容的本機伺服器（自架閘道、vLLM 轉接層等）。</summary>
    public const string Local = "Local";

    /// <summary>
    /// decisions-api.dev 把多個供應商統一在 /v1/systemone 之下，因此非 TypeSafe 的原生服務
    /// 也能用同一個 TypeSafeClient。端點固定，UI 只對本機服務開放修改。
    /// </summary>
    private const string AggregatorEndpoint = "https://decisions-api.dev";

    public static readonly IReadOnlyList<RimLLMDecisionProvider> All =
    [
        // Gemini 不走 System One wire format，由傳輸層的轉接處理器換成 /v1beta/decisions。
        new RimLLMDecisionProvider(
            Gemini,
            "https://" + GeminiDecisionsAdapterHandler.GeminiHost)
        {
            ChatKeyProviderId = ProviderIds.Gemini
        },

        // OpenRouter 路徑與 System One 不同，由 OpenRouterDecisionsAdapterHandler 在傳輸層改寫。
        new RimLLMDecisionProvider(
            OpenRouter,
            "https://" + OpenRouterAttributionPolicy.Host)
        {
            ChatKeyProviderId = ProviderIds.OpenRouter
        },

        new RimLLMDecisionProvider(
            TypeSafe,
            "https://api.typesafe.ai"),
        new RimLLMDecisionProvider(
            Clef,
            AggregatorEndpoint),
        new RimLLMDecisionProvider(
            Liquid,
            AggregatorEndpoint),
        new RimLLMDecisionProvider(
            Solar,
            AggregatorEndpoint),
        new RimLLMDecisionProvider(
            Kev,
            AggregatorEndpoint),
        new RimLLMDecisionProvider(
            Tev,
            AggregatorEndpoint),
        new RimLLMDecisionProvider(
            Laya,
            AggregatorEndpoint),
        new RimLLMDecisionProvider(
            Span,
            AggregatorEndpoint),
        new RimLLMDecisionProvider(
            Microsoft,
            AggregatorEndpoint),
        new RimLLMDecisionProvider(
            OpenAI,
            AggregatorEndpoint),

        new RimLLMDecisionProvider(
            Ollama,
            "http://localhost:11434")
        {
            AllowCustomEndpoint = true
        },
        new RimLLMDecisionProvider(
            Unsloth,
            "http://localhost:8888")
        {
            AllowCustomEndpoint = true
        },
        new RimLLMDecisionProvider(
            Local,
            "http://localhost:8000")
        {
            AllowCustomEndpoint = true
        }
    ];

    private static readonly Dictionary<string, RimLLMDecisionProvider> ById =
        All.ToDictionary(provider => provider.Id, StringComparer.Ordinal);

    /// <summary>
    /// 嘗試取得指定識別碼的決策供應商資訊。
    /// </summary>
    public static bool TryGet(string providerId, [NotNullWhen(true)] out RimLLMDecisionProvider? provider)
    {
        if (!string.IsNullOrEmpty(providerId) && ById.TryGetValue(providerId, out RimLLMDecisionProvider? found))
        {
            provider = found;
            return true;
        }
        provider = null;
        return false;
    }

    /// <summary>
    /// 取得指定識別碼的決策供應商資訊；若未找到則退回預設的 TypeSafe 供應商。
    /// </summary>
    public static RimLLMDecisionProvider Get(string providerId)
    {
        return TryGet(providerId, out RimLLMDecisionProvider? provider) ? provider : ById[TypeSafe];
    }

    /// <summary>
    /// 取得指定決策供應商的預設端點 URL。
    /// </summary>
    public static string GetDefaultEndpoint(string providerId)
    {
        return Get(providerId).DefaultEndpoint;
    }
}
