#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager;

/// <summary>
/// models.dev 模型目錄下載器，將框架供應商對應到 models.dev 供應商鍵。
/// 每次下載成功都會把費率交給 <see cref="ModelPricing"/>，讓費用估算與模型分級以目錄為準。
/// </summary>
#pragma warning disable S1075 // 本地／固定雲端端點的產品預設值，不在執行期變動。
internal static class ModelsDevCatalog
{
    private const string ApiUrl = "https://models.dev/api.json";

    /// <summary>
    /// 框架供應商 → models.dev 供應商鍵。OpenRouter 的 API 本身就回報全部模型的上限，不需要另外下載；
    /// OpenAICompatible 的模型名稱由使用者自訂，無從對照。兩者皆不列入。
    /// </summary>
    private static readonly Dictionary<string, string> ProviderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProviderIds.OpenAI] = "openai",
        [ProviderIds.Gemini] = "google",
        [ProviderIds.DeepSeek] = "deepseek",
        [ProviderIds.Groq] = "groq",
        [ProviderIds.Grok] = "xai",
        [ProviderIds.Kimi] = "moonshotai",
        [ProviderIds.MiniMax] = "minimax",
        [ProviderIds.Qwen] = "alibaba",
        [ProviderIds.Nvidia] = "nvidia",
        [ProviderIds.Zai] = "zai",
    };

    /// <summary>資料庫未壓縮約 5 MB，開啟 gzip 傳輸。逾時由呼叫端的 CancellationToken 控制。</summary>
    // SDK 豁免：models.dev 是公開的 JSON 目錄，不屬於任何 LLM API，沒有 SDK 可用。
#pragma warning disable RS0030
    private static readonly HttpClient Http = new(
        new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
#pragma warning restore RS0030
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>
    /// 下載結果保留的時間。玩家常一次重新整理好幾個供應商，每次重下約 5 MB 的資料庫是浪費。
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly Lock CacheLock = new();
    private static Task<ModelsDevData> _cached;
    private static DateTime _cachedAt;

    internal static bool IsCovered(string providerId)
    {
        return !string.IsNullOrEmpty(providerId) && ProviderKeys.ContainsKey(providerId);
    }

    /// <summary>
    /// 取得所有已對照供應商的上限、圖片支援與費率（框架供應商 → 模型名稱 → 值）。
    /// 同時進行的呼叫共用同一次下載；成功結果快取 <see cref="CacheDuration"/>，失敗則下次重試。
    /// 失敗只記警告並回傳 null，上限只是附帶資訊，不讓模型清單的重新整理失敗。
    /// </summary>
    internal static Task<ModelsDevData> GetAllAsync(float timeoutSeconds)
    {
        lock (CacheLock)
        {
            #pragma warning disable VSTHRD103 // 只在 Task 已完成時讀 Result；DownloadAsync 會捕捉所有例外，不會丟出
            bool reusable = _cached != null
                && DateTime.UtcNow - _cachedAt < CacheDuration
                && !(_cached.IsCompleted && _cached.Result == null);
            #pragma warning restore VSTHRD103
            if (!reusable)
            {
                _cached = DownloadAsync(timeoutSeconds);
                _cachedAt = DateTime.UtcNow;
            }
            return _cached;
        }
    }

    private static async Task<ModelsDevData> DownloadAsync(float timeoutSeconds)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 30f));
            using HttpResponseMessage response = await Http.GetAsync(ApiUrl, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);
            ModelPricing.SetCatalogRates(ReadAllRates(doc.RootElement));
            return new ModelsDevData(ReadAll(doc.RootElement), ReadAllVision(doc.RootElement));
        }
        catch (Exception ex)
        {
            RimLLMLog.Warning($"[RimLLM] Could not read the model catalog from models.dev: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
            return null;
        }
    }

    /// <summary>
    /// 讀出各供應商每個模型的費率（美元／每百萬 token）：<c>cost.input</c>、<c>cost.output</c>，
    /// 有 <c>cost.cache_read</c> 時一併採用。缺少輸入或輸出價的模型不收錄，查詢時退回內建費率表。
    /// </summary>
    internal static Dictionary<string, Dictionary<string, ModelPricing.CostRate>> ReadAllRates(JsonElement root)
    {
        var all = new Dictionary<string, Dictionary<string, ModelPricing.CostRate>>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object) return all;

        foreach (var mapping in ProviderKeys)
        {
            if (!root.TryGetProperty(mapping.Value, out JsonElement provider)
                || provider.ValueKind != JsonValueKind.Object
                || !provider.TryGetProperty("models", out JsonElement models)
                || models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var rates = new Dictionary<string, ModelPricing.CostRate>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty model in models.EnumerateObject())
            {
                if (model.Value.ValueKind == JsonValueKind.Object
                    && model.Value.TryGetProperty("cost", out JsonElement cost)
                    && cost.ValueKind == JsonValueKind.Object
                    && TryReadNonNegative(cost, "input", out float input)
                    && TryReadNonNegative(cost, "output", out float output))
                {
                    float? cacheRead = TryReadNonNegative(cost, "cache_read", out float cached) ? cached : null;
                    rates[model.Name] = new ModelPricing.CostRate(input, output, cacheRead);
                }
            }
            all[mapping.Key] = rates;
        }
        return all;
    }

    private static bool TryReadNonNegative(JsonElement obj, string name, out float value)
    {
        if (obj.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out double number)
            && number >= 0)
        {
            value = (float)number;
            return true;
        }

        value = 0f;
        return false;
    }

    /// <summary>
    /// 一次讀出所有已對照供應商的上限，只保留這一小部分，不保留整份約 5 MB 的文件。
    /// 每個模型有 <c>limit.input</c>（輸入上限）就用它，否則用 <c>limit.context</c>（整個視窗）：
    /// 壓縮對話歷史要控制的是輸入量，輸入上限比含輸出的整個視窗貼切。
    /// </summary>
    internal static Dictionary<string, Dictionary<string, int>> ReadAll(JsonElement root)
    {
        var all = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object) return all;

        foreach (var mapping in ProviderKeys)
        {
            if (!root.TryGetProperty(mapping.Value, out JsonElement provider)
                || provider.ValueKind != JsonValueKind.Object
                || !provider.TryGetProperty("models", out JsonElement models)
                || models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            all[mapping.Key] = ReadModelWindows(models);
        }
        return all;
    }

    /// <summary>
    /// 讀出各供應商每個模型是否支援圖片輸入：<c>modalities.input</c> 含 "image" 即支援。
    /// 沒有 modalities 欄位的模型不收錄，查詢時視為未知而非不支援。
    /// </summary>
    internal static Dictionary<string, Dictionary<string, bool>> ReadAllVision(JsonElement root)
    {
        var all = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object) return all;

        foreach (var mapping in ProviderKeys)
        {
            if (!root.TryGetProperty(mapping.Value, out JsonElement provider)
                || provider.ValueKind != JsonValueKind.Object
                || !provider.TryGetProperty("models", out JsonElement models)
                || models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var support = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty model in models.EnumerateObject())
            {
                if (model.Value.ValueKind == JsonValueKind.Object
                    && model.Value.TryGetProperty("modalities", out JsonElement modalities)
                    && modalities.ValueKind == JsonValueKind.Object
                    && modalities.TryGetProperty("input", out JsonElement input)
                    && input.ValueKind == JsonValueKind.Array)
                {
                    support[model.Name] = input.EnumerateArray().Any(
                        m => m.ValueKind == JsonValueKind.String && m.GetString() == "image");
                }
            }
            all[mapping.Key] = support;
        }
        return all;
    }

    /// <summary>把該供應商的圖片支援補進 <paramref name="support"/>，已有的值（供應商 API 回報）不覆寫。</summary>
    internal static void FillMissingVision(Dictionary<string, Dictionary<string, bool>> all, string providerId, Dictionary<string, bool> support)
    {
        if (all == null || support == null || string.IsNullOrEmpty(providerId)
            || !all.TryGetValue(providerId, out var fromDatabase))
        {
            return;
        }

        foreach (var kvp in fromDatabase.Where(k => !support.ContainsKey(k.Key)))
        {
            support[kvp.Key] = kvp.Value;
        }
    }

    private static Dictionary<string, int> ReadModelWindows(JsonElement models)
    {
        var windows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty model in models.EnumerateObject())
        {
            if (model.Value.ValueKind != JsonValueKind.Object
                || !model.Value.TryGetProperty("limit", out JsonElement limit)
                || limit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int tokens = ReadPositiveInt(limit, "input");
            if (tokens <= 0) tokens = ReadPositiveInt(limit, "context");
            if (tokens > 0) windows[model.Name] = tokens;
        }
        return windows;
    }

    /// <summary>把該供應商的上限補進 <paramref name="windows"/>，已有的值（供應商 API 回報）不覆寫。</summary>
    internal static void FillMissing(Dictionary<string, Dictionary<string, int>> all, string providerId, Dictionary<string, int> windows)
    {
        if (all == null || windows == null || string.IsNullOrEmpty(providerId)
            || !all.TryGetValue(providerId, out var fromDatabase))
        {
            return;
        }

        foreach (var kvp in fromDatabase.Where(k => !windows.ContainsKey(k.Key)))
        {
            windows[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>models.dev 一次下載的結果：上下文上限與圖片輸入支援，皆為「框架供應商 → 模型名稱 → 值」。</summary>
    internal sealed class ModelsDevData
    {
        public ModelsDevData(Dictionary<string, Dictionary<string, int>> windows, Dictionary<string, Dictionary<string, bool>> vision)
        {
            Windows = windows;
            Vision = vision;
        }

        public Dictionary<string, Dictionary<string, int>> Windows { get; }
        public Dictionary<string, Dictionary<string, bool>> Vision { get; }
    }

    private static int ReadPositiveInt(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && number > 0
            ? number
            : 0;
    }
}
#pragma warning restore S1075
