using System;
using System.Collections.Generic;
using System.Linq;

namespace RimLLM_Framework.Manager.Services;

/// <summary>
/// 一個決策模型供應商的預設值。所有欄位都可在設定中覆寫，這裡只提供第一次使用時的起點。
/// </summary>
public sealed class RimLLMDecisionProvider
{
    public RimLLMDecisionProvider(string id, string defaultEndpoint, string defaultModel, params string[] presetModels)
    {
        Id = id;
        DefaultEndpoint = defaultEndpoint;
        DefaultModel = defaultModel;
        PresetModels = presetModels ?? [];
    }

    /// <summary>穩定的內部識別碼，也是設定檔與語系鍵的一部分。</summary>
    public string Id { get; }

    /// <summary>JevClient 的 BaseAddress；必須是沒有路徑的 HTTPS origin。</summary>
    public string DefaultEndpoint { get; }

    /// <summary>尚未設定模型時送出的模型名稱。</summary>
    public string DefaultModel { get; }

    /// <summary>在抓取伺服器清單之前，先顯示的已知模型。</summary>
    public IReadOnlyList<string> PresetModels { get; }

    /// <summary>
    /// 決策金鑰留空時沿用哪個對話供應商的金鑰（同一家公司、同一把金鑰）；null 代表不沿用。
    /// 服務的金鑰解析與設定精靈的一鍵開啟都讀這個欄位。
    /// </summary>
    public string ChatKeyProviderId { get; init; }
}

/// <summary>
/// 決策模型供應商清單。這些服務都接受 System One 的 state/questions 結構；
/// 其中 TypeSafe 是原生 Jev，其餘是提供相同 wire format 的託管服務或聚合閘道；
/// 唯一例外是 Gemini，由 <see cref="GeminiDecisionsAdapterHandler"/> 在傳輸層轉換格式。
/// 端點與模型都可以在 UI 內修改，因此清單只是預設值，不是硬性限制。
/// </summary>
public static class RimLLMDecisionProviderCatalog
{
    /// <summary>停用決策服務。</summary>
    public const string Disabled = "Disabled";
    /// <summary>TypeSafe AI（原生 Jev 服務）。</summary>
    public const string TypeSafe = "TypeSafe";
    /// <summary>Clef 決策服務。</summary>
    public const string Clef = "Clef";
    /// <summary>Liquid 決策服務。</summary>
    public const string Liquid = "Liquid";
    /// <summary>Solar 決策服務。</summary>
    public const string Solar = "Solar";
    /// <summary>Kev 決策服務。</summary>
    public const string Kev = "Kev";
    /// <summary>Tev 決策服務。</summary>
    public const string Tev = "Tev";
    /// <summary>Laya 決策服務。</summary>
    public const string Laya = "Laya";
    /// <summary>Span 決策服務。</summary>
    public const string Span = "Span";
    /// <summary>Microsoft 決策服務。</summary>
    public const string Microsoft = "Microsoft";
    /// <summary>OpenAI 決策服務。</summary>
    public const string OpenAI = "OpenAI";
    /// <summary>Google Gemini Decision（Gemini Decisions API，經 <see cref="GeminiDecisionsAdapterHandler"/> 轉接）。</summary>
    public const string Gemini = "Gemini";

    /// <summary>Ollama 0.35 以上內建的 Jev 風格決策介面（/v1/systemone）。</summary>
    public const string Ollama = "Ollama";

    /// <summary>Unsloth 以 TypeSafe 相容的 Jev API 提供本機 Laya／Clef。</summary>
    public const string Unsloth = "Unsloth";

    /// <summary>其他 System One 相容的本機伺服器（自架閘道、vLLM 轉接層等）。</summary>
    public const string Local = "Local";

    /// <summary>
    /// decisions-api.dev 把多個供應商統一在 /v1/systemone 之下，因此非 TypeSafe 的原生服務
    /// 也能用同一個 JevClient。玩家仍可在 UI 改成供應商自己的端點。
    /// </summary>
    private const string AggregatorEndpoint = "https://decisions-api.dev";

    /// <summary>所有內建支援的決策供應商預設目錄。</summary>
    public static readonly IReadOnlyList<RimLLMDecisionProvider> All =
    [
        new RimLLMDecisionProvider(
            TypeSafe,
            "https://api.typesafe.ai",
            "jev-latest",
            "jev-latest", "jev-1.14", "jev-1.13", "jev-1.13.0"),
        new RimLLMDecisionProvider(
            Clef,
            AggregatorEndpoint,
            "cloudflare/clef",
            "cloudflare/clef", "cloudflare/clef-flash", "cloudflare/clef-omni"),
        new RimLLMDecisionProvider(
            Liquid,
            AggregatorEndpoint,
            "liquid/d1",
            "liquid/d1"),
        new RimLLMDecisionProvider(
            Solar,
            AggregatorEndpoint,
            "upstage/solar-decide",
            "upstage/solar-decide", "upstage/solar-decide-flash"),
        new RimLLMDecisionProvider(
            Kev,
            AggregatorEndpoint,
            "jaredpalmer/kev-4b",
            "jaredpalmer/kev-4b"),
        new RimLLMDecisionProvider(
            Tev,
            AggregatorEndpoint,
            "togethercomputer/tev1-4b-experimental",
            "togethercomputer/tev1-4b-experimental"),
        new RimLLMDecisionProvider(
            Laya,
            AggregatorEndpoint,
            "convaiinnovations/laya",
            "convaiinnovations/laya", "laya-auto", "laya-english", "laya-multilingual"),
        new RimLLMDecisionProvider(
            Span,
            AggregatorEndpoint,
            "respan/span-01",
            "respan/span-01", "respan/span-01-lite"),
        new RimLLMDecisionProvider(
            Microsoft,
            AggregatorEndpoint,
            "microsoft/microsoft-decision-1",
            "microsoft/microsoft-decision-1"),
        new RimLLMDecisionProvider(
            OpenAI,
            AggregatorEndpoint,
            "openai/gpt-6-luna-decisions",
            "openai/gpt-6-luna-decisions"),
        // Gemini 不走 System One wire format，由傳輸層的轉接處理器換成 /v1beta/decisions。
        new RimLLMDecisionProvider(
            Gemini,
            "https://" + GeminiDecisionsAdapterHandler.GeminiHost,
            "gemini-decision-1",
            "gemini-decision-1")
        {
            ChatKeyProviderId = ProviderIds.Gemini
        },

        // 本機架設：走明文 HTTP，Jev SDK 只接受 HTTPS origin，
        // 由 DecisionEndpointRouting 在傳輸層把兩者接起來。
        new RimLLMDecisionProvider(
            Ollama,
            "http://localhost:11434",
            "tev1:4b",
            "tev1:4b"),
        new RimLLMDecisionProvider(
            Unsloth,
            "http://localhost:8888",
            "laya",
            "laya", "laya-multilingual", "laya-english", "laya-typed-decisions"),
        new RimLLMDecisionProvider(
            Local,
            "http://localhost:8000",
            "jev-latest",
            "jev-latest")
    ];

    private static readonly Dictionary<string, RimLLMDecisionProvider> ById =
        All.ToDictionary(provider => provider.Id, StringComparer.Ordinal);

    /// <summary>
    /// 嘗試取得指定識別碼的決策供應商資訊。
    /// </summary>
    /// <param name="providerId">決策供應商識別碼。</param>
    /// <param name="provider">找到時傳回供應商物件，否則為 null。</param>
    /// <returns>若找到該供應商則為 true，否則為 false。</returns>
    public static bool TryGet(string providerId, out RimLLMDecisionProvider provider)
    {
        if (!string.IsNullOrEmpty(providerId) && ById.TryGetValue(providerId, out provider))
        {
            return true;
        }
        provider = null;
        return false;
    }

    /// <summary>
    /// 取得指定識別碼的決策供應商資訊；若未找到則退回預設的 TypeSafe 供應商。
    /// </summary>
    /// <param name="providerId">決策供應商識別碼。</param>
    /// <returns>對應的決策供應商物件。</returns>
    public static RimLLMDecisionProvider Get(string providerId)
    {
        return TryGet(providerId, out RimLLMDecisionProvider provider) ? provider : ById[TypeSafe];
    }

    /// <summary>
    /// 取得指定決策供應商的預設模型名稱。
    /// </summary>
    /// <param name="providerId">決策供應商識別碼。</param>
    /// <returns>預設模型名稱。</returns>
    public static string GetDefaultModel(string providerId)
    {
        return Get(providerId).DefaultModel;
    }

    /// <summary>
    /// 取得指定決策供應商的預設端點 URL。
    /// </summary>
    /// <param name="providerId">決策供應商識別碼。</param>
    /// <returns>預設端點 URL。</returns>
    public static string GetDefaultEndpoint(string providerId)
    {
        return Get(providerId).DefaultEndpoint;
    }
}
