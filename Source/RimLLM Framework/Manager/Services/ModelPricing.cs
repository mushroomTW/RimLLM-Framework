using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 模型費率查詢、費用估算與模型分級。
/// </summary>
/// <remarks>
/// 費率來源依序為：models.dev 目錄（<see cref="SetCatalogRates"/> 載入，隨下載更新）→ 內建費率表。
/// 內建表只是離線後備：手寫價格一定會過時，能取得目錄資料時一律以目錄為準。
/// </remarks>
internal static class ModelPricing
{
    public const int ModelLevelHigh = 3;
    public const int ModelLevelMedium = 2;
    public const int ModelLevelLow = 1;

    private const float HighTierCompletionCostThreshold = 3.00f;
    private const float MediumTierCompletionCostThreshold = 0.50f;

    private const float DefaultCacheReadMultiplier = 0.25f;

    internal readonly struct CostRate
    {
        public readonly float PromptPerMillion;
        public readonly float CompletionPerMillion;
        public readonly float CachedPromptPerMillion;

        public CostRate(float promptPerMillion, float completionPerMillion, float? cachedPromptPerMillion = null)
        {
            PromptPerMillion = promptPerMillion;
            CompletionPerMillion = completionPerMillion;
            CachedPromptPerMillion = cachedPromptPerMillion ?? promptPerMillion * DefaultCacheReadMultiplier;
        }
    }

    private static readonly Dictionary<string, CostRate> BuiltInRates = new(StringComparer.OrdinalIgnoreCase)
    {
        { "openai:gpt-5", new CostRate(1.25f, 10.00f) },
        { "openai:gpt-5-mini", new CostRate(0.25f, 2.00f) },
        { "openai:gpt-5-nano", new CostRate(0.05f, 0.40f) },
        { "openai:gpt-5.1", new CostRate(1.25f, 10.00f) },
        { "openai:gpt-4.1", new CostRate(2.00f, 8.00f) },
        { "openai:gpt-4.1-mini", new CostRate(0.40f, 1.60f) },
        { "openai:gpt-4.1-nano", new CostRate(0.10f, 0.40f) },
        { "openai:gpt-4o", new CostRate(2.50f, 10.00f) },
        { "openai:gpt-4o-mini", new CostRate(0.15f, 0.60f) },
        { "openai:o1", new CostRate(15.00f, 60.00f) },
        { "openai:o1-mini", new CostRate(1.10f, 4.40f) },
        { "openai:o3", new CostRate(2.00f, 8.00f) },
        { "openai:o3-mini", new CostRate(1.10f, 4.40f) },
        { "openai:o3-pro", new CostRate(20.00f, 80.00f) },
        { "openai:o4-mini", new CostRate(1.10f, 4.40f) },
        { "openai:gpt-4-turbo", new CostRate(10.00f, 30.00f) },
        { "openai:gpt-4", new CostRate(30.00f, 60.00f) },
        { "openai:gpt-3.5-turbo", new CostRate(0.50f, 1.50f) },
        { "openai:text-embedding-3-small", new CostRate(0.02f, 0f) },
        { "openai:text-embedding-3-large", new CostRate(0.13f, 0f) },
        { "openai:text-embedding-ada-002", new CostRate(0.10f, 0f) },

        { "gemini:gemini-embedding-001", new CostRate(0.15f, 0f) },
        { "gemini:gemini-2.0-flash", new CostRate(0.10f, 0.40f) },
        { "gemini:gemini-2.0-flash-lite", new CostRate(0.075f, 0.30f) },
        { "gemini:gemini-1.5-flash", new CostRate(0.075f, 0.30f) },
        { "gemini:gemini-1.5-pro", new CostRate(1.25f, 5.00f) },
        { "gemini:gemini-2.5-pro", new CostRate(1.25f, 10.00f) },
        { "gemini:gemini-2.5-flash", new CostRate(0.30f, 2.50f) },
        { "gemini:gemini-2.5-flash-lite", new CostRate(0.10f, 0.40f) },
        { "gemini:gemini-3.1-pro-preview", new CostRate(2.00f, 12.00f) },
        { "gemini:gemini-3.1-flash-lite", new CostRate(0.25f, 1.50f) },
        { "gemini:gemini-3.5-flash", new CostRate(1.50f, 9.00f) },

        // DeepSeek（快取讀取價為輸入價的 2%）
        { "deepseek:deepseek-chat", new CostRate(0.14f, 0.28f, 0.14f * 0.02f) },
        { "deepseek:deepseek-reasoner", new CostRate(0.14f, 0.28f, 0.14f * 0.02f) },
        { "deepseek:deepseek-v3", new CostRate(0.14f, 0.28f, 0.14f * 0.02f) },
        { "deepseek:deepseek-r1", new CostRate(0.14f, 0.28f, 0.14f * 0.02f) },
        { "deepseek:deepseek-v4-flash", new CostRate(0.14f, 0.28f, 0.14f * 0.02f) },
        { "deepseek:deepseek-v4-pro", new CostRate(0.435f, 0.87f, 0.435f * 0.02f) },

        // Groq（模型 ID 含供應商前綴時整段入鍵）
        { "groq:llama-3.3-70b-versatile", new CostRate(0.59f, 0.79f) },
        { "groq:llama-3.1-8b-instant", new CostRate(0.05f, 0.08f) },
        { "groq:mixtral-8x7b-32768", new CostRate(0.24f, 0.24f) },
        { "groq:gemma2-9b-it", new CostRate(0.20f, 0.20f) },
        { "groq:openai/gpt-oss-120b", new CostRate(0.15f, 0.75f) },
        { "groq:openai/gpt-oss-20b", new CostRate(0.10f, 0.50f) },
        { "groq:qwen/qwen3-32b", new CostRate(0.29f, 0.59f) },
        { "groq:moonshotai/kimi-k2-instruct", new CostRate(1.00f, 3.00f) },

        { "qwen:qwen-max", new CostRate(2.80f, 8.40f) },
        { "qwen:qwen-plus", new CostRate(0.40f, 1.20f) },
        { "qwen:qwen-turbo", new CostRate(0.10f, 0.20f) },

        { "kimi:kimi-k2", new CostRate(0.60f, 2.50f) },
        { "kimi:moonshot-v1-8k", new CostRate(1.68f, 1.68f) },
        { "kimi:moonshot-v1-32k", new CostRate(3.36f, 3.36f) },
        { "kimi:moonshot-v1-128k", new CostRate(8.40f, 8.40f) },

        { "minimax:minimax-m3", new CostRate(0.30f, 1.20f) },
        { "minimax:minimax-m2", new CostRate(0.30f, 1.20f) },
        { "minimax:abab6.5s", new CostRate(0.14f, 0.28f) },

        { "z.ai:glm-4.6", new CostRate(0.60f, 2.20f) },
        { "z.ai:glm-4.5", new CostRate(0.60f, 2.20f) },
        { "z.ai:glm-4.5-air", new CostRate(0.20f, 1.10f) },
        { "z.ai:glm-4.5-flash", new CostRate(0f, 0f) },

        { "grok:grok-4", new CostRate(3.00f, 15.00f) },
        { "grok:grok-4-fast", new CostRate(0.20f, 0.50f) },
        { "grok:grok-3", new CostRate(3.00f, 15.00f) },
        { "grok:grok-3-mini", new CostRate(0.30f, 0.50f) },
        { "grok:grok-2", new CostRate(2.00f, 10.00f) },
        { "grok:grok-beta", new CostRate(5.00f, 15.00f) },

        // Player2：本地 App 免費，雲端以 joules 而非美元計費，因此 USD 費率記為已知的 0。
        { "player2:player2", new CostRate(0f, 0f) }
    };

    /// <summary>
    /// models.dev 目錄的費率（鍵格式同 <see cref="BuiltInRates"/>）與依它算出的模型分級快取，整份一起替換，讀取端不加鎖。
    /// 兩者放在同一個物件：用舊費率算到一半的分級只會寫進已被換掉的快取，不會汙染新的。
    /// 玩家的分級覆寫不進快取（由呼叫端即時查詢），因此設定頁調整立即生效。
    /// </summary>
    private sealed class Catalog
    {
        public readonly Dictionary<string, CostRate> Rates;
        public readonly ConcurrentDictionary<string, int> LevelCache = new(StringComparer.OrdinalIgnoreCase);

        public Catalog(Dictionary<string, CostRate> rates)
        {
            Rates = rates;
        }
    }

    private static volatile Catalog _catalog = new(new Dictionary<string, CostRate>(StringComparer.OrdinalIgnoreCase));

    internal static void SetCatalogRates(Dictionary<string, Dictionary<string, CostRate>> ratesByProvider)
    {
        var flattened = new Dictionary<string, CostRate>(StringComparer.OrdinalIgnoreCase);
        if (ratesByProvider != null)
        {
            foreach (var provider in ratesByProvider)
            {
                if (provider.Value == null) continue;
                foreach (var model in provider.Value)
                {
                    flattened[provider.Key + ":" + model.Key] = model.Value;
                }
            }
        }

        _catalog = new Catalog(flattened);
    }

    /// <summary>
    /// 依據模型的輸出費率（每百萬 Token）判定其等級：
    /// Level 3 (High): 輸出費率 &gt;= $3.00；Level 2 (Medium): &gt;= $0.50；Level 1 (Low): 其餘或本地免費模型。
    /// 查無費率的雲端模型視為 Medium。
    /// </summary>
    public static int GetModelLevel(string providerId, string modelName)
    {
        if (string.IsNullOrEmpty(modelName) ||
            string.Equals(providerId, ProviderIds.OpenAICompatible, StringComparison.OrdinalIgnoreCase))
        {
            return ModelLevelLow;
        }

        Catalog catalog = _catalog;
        string cacheKey = BuildKey(providerId, modelName);
        if (catalog.LevelCache.TryGetValue(cacheKey, out int cached))
        {
            return cached;
        }

        int level;
        if (TryFindRate(catalog, providerId, modelName, out CostRate rate))
        {
            if (rate.CompletionPerMillion >= HighTierCompletionCostThreshold) level = ModelLevelHigh;
            else if (rate.CompletionPerMillion >= MediumTierCompletionCostThreshold) level = ModelLevelMedium;
            else level = ModelLevelLow;
        }
        else
        {
            level = ModelLevelMedium;
        }

        catalog.LevelCache[cacheKey] = level;
        return level;
    }

    /// <summary>
    /// 估算單次呼叫的美元成本。回傳是否找到費率，讓記帳區分未知費用與明確的零元費率。
    /// <paramref name="cachedPromptTokens"/> 由呼叫端保證已落在 [0, promptTokens] 範圍內。
    /// </summary>
    internal static bool TryEstimateCost(string providerId, string? modelName, int promptTokens, int completionTokens, int cachedPromptTokens, out float cost)
    {
        if (!TryFindRate(providerId, modelName, out CostRate rate))
        {
            cost = 0f;
            return false;
        }

        int fullRatePromptTokens = promptTokens - cachedPromptTokens;
        float promptCost = (fullRatePromptTokens / 1000000f) * rate.PromptPerMillion
                           + (cachedPromptTokens / 1000000f) * rate.CachedPromptPerMillion;
        float completionCost = (completionTokens / 1000000f) * rate.CompletionPerMillion;
        cost = promptCost + completionCost;
        return true;
    }

    private static bool TryFindRate(string providerId, string? modelName, out CostRate rate)
    {
        return TryFindRate(_catalog, providerId, modelName, out rate);
    }

    private static bool TryFindRate(Catalog catalog, string providerId, string? modelName, out CostRate rate)
    {
        return TryFindRate(catalog.Rates, providerId, modelName, out rate)
            || TryFindRate(BuiltInRates, providerId, modelName, out rate);
    }

#pragma warning disable S3267 // 需同時比對前綴邊界並保留最長匹配鍵，LINQ 運算式會讓邏輯更難讀且無法提前結束。
    private static bool TryFindRate(Dictionary<string, CostRate> rates, string? providerId, string? modelName, out CostRate rate)
    {
        string normModel = StripModelsPrefix(modelName?.Trim());
        if (rates.TryGetValue(BuildKey(providerId, modelName), out rate))
        {
            return true;
        }

        // 前綴模糊匹配（例如 gpt-4o-2024-08-06 匹配 gpt-4o）。前綴之後必須是分隔字元或結尾：
        // 單純的 StartsWith 會讓 "gpt-4" 吃到 "gpt-4.1-mini"，把 $0.40 的模型以 $30 計價。
        string providerPrefix = (providerId?.Trim() ?? "") + ":";
        string? bestKey = null;
        int bestLength = 0;
        foreach (var kvp in rates)
        {
            if (!kvp.Key.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            string candidateModel = kvp.Key.Substring(providerPrefix.Length);
            if (candidateModel.Length > bestLength &&
                normModel.StartsWith(candidateModel, StringComparison.OrdinalIgnoreCase) &&
                IsPrefixBoundary(normModel, candidateModel.Length))
            {
                bestLength = candidateModel.Length;
                bestKey = kvp.Key;
            }
        }

        if (bestKey != null)
        {
            rate = rates[bestKey];
            return true;
        }

        rate = default;
        return false;
    }
#pragma warning restore S3267

    /// <summary>組查詢鍵：僅剝除 models/ 前綴，不做大小寫正規化（比對器負責）。</summary>
    private static string BuildKey(string? providerId, string? modelName)
    {
        return (providerId?.Trim() ?? "") + ":" + StripModelsPrefix(modelName?.Trim());
    }

    private static string StripModelsPrefix(string? model)
    {
        if (!string.IsNullOrEmpty(model) && model.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            return model.Substring("models/".Length);
        }
        return model ?? "";
    }

    /// <summary>
    /// 前綴比對的版本邊界：緊接在前綴後的字元必須是分隔符（或已到結尾），
    /// 讓 "gpt-4o" 命中 "gpt-4o-2024-08-06" 但 "gpt-4" 不命中 "gpt-4.1"。
    /// </summary>
    private static bool IsPrefixBoundary(string model, int prefixLength)
    {
        if (prefixLength >= model.Length) return true;
        char next = model[prefixLength];
        return next == '-' || next == '_' || next == ':' || next == '@' || next == ' ';
    }
}
