using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework;

/// <summary>
/// 綁定單一 Mod 的 IEmbeddingGenerator facade。內部接到既有 RimLLMEmbeddingService
/// （線上供應商 + 防濫用）。透過 RimLLMProvider.CreateEmbeddingGenerator(modId) 取得。
/// 類別名稱保留 RimLLM 品牌名，不重新命名為 RimLlm，以避免破壞品牌識別與下游 Mod 相容性。
/// </summary>
internal sealed class RimLLMEmbeddingClient : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly RimLLMManager _manager;
    private readonly string _modId;

    /// <summary>此 Mod 的 OpenRouter 應用歸屬，於每次呼叫時放進 <see cref="RimLLMAppAttribution.Current"/>。</summary>
    private readonly RimLLMAppAttribution _attribution;

    internal RimLLMEmbeddingClient(RimLLMManager manager, string modId)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _modId = modId ?? throw new ArgumentNullException(nameof(modId));
        _attribution = manager.AttributionResolver?.Invoke(modId);
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        _manager.UsageTracker.RecordCaller(_modId);
        _manager.CheckAntiAbuseForMod(_modId);
        RimLLMAppAttribution.Current = _attribution;

        var inputValues = new List<string>(values);
        long estimatedTokens = EstimateTotalTokens(inputValues);

        using (RimLLMUsageTracker.BudgetReservation reservation = _manager.UsageTracker.ReserveBudgetOrThrow(estimatedTokens))
        {
            IReadOnlyList<RimLLMEmbeddingResult> results = await _manager.EmbeddingService
                .ComputeEmbeddingsAsync(
                    inputValues,
                    options,
                    batchResults => RecordBatchUsage(reservation, batchResults),
                    cancellationToken)
                .ConfigureAwait(false);

            // 標上實際算出向量的模型與供應商——由服務在請求開始時捕捉（見
            // RimLLMEmbeddingResult.ModelId），而不是 await 之後重新讀設定：
            // 請求期間玩家切換設定，若這裡重讀，向量會被標成新模型、用量記到新供應商，
            // 污染向量索引與帳本。少了它，呼叫端把不同模型的向量存進同一個索引
            // 也不會發現——維度相同但語意空間不同的向量，比對結果只會是雜訊。
            string modelId = ResolveModelId(results, options);
            return BuildGeneratedEmbeddings(results, modelId);
        }
    }

    /// <summary>
    /// 估算整批輸入的 token 數，總和以 <see cref="SaturatingMath.Add"/> 夾在 <see cref="long.MaxValue"/> 以下。
    /// </summary>
    private static long EstimateTotalTokens(IReadOnlyList<string> values)
    {
        long estimatedTokens = 0;
        foreach (string value in values)
        {
            estimatedTokens = SaturatingMath.Add(estimatedTokens, RimLLMChatClientExecutor.EstimateTokens(value));
        }
        return estimatedTokens;
    }

    /// <summary>
    /// 單一批次完成時，把供應商回報的 token 記到預算保留額度上。
    /// 供應商未回報 token 時不記錄用量，保留估計值。
    /// </summary>
    private static void RecordBatchUsage(
        RimLLMUsageTracker.BudgetReservation reservation,
        IReadOnlyList<RimLLMEmbeddingResult> batchResults)
    {
        long? batchTokens = SumInputTokens(batchResults);
        if (!batchTokens.HasValue)
        {
            return;
        }

        RimLLMEmbeddingResult batchResult = batchResults[0];
        reservation.RecordUsage(
            RimLLMEmbeddingService.GetMainProviderIdForEmbedding(batchResult.ProviderId),
            batchResult.ModelId,
            batchTokens.Value > int.MaxValue ? int.MaxValue : (int)batchTokens.Value,
            0);
    }

    /// <summary>
    /// 決定回傳向量要標記的模型：優先採用服務捕捉到的實際模型，其次為呼叫端覆寫，
    /// 最後才退回目前設定。順序不可顛倒，理由見 <see cref="GenerateAsync"/> 內的說明。
    /// </summary>
    private string ResolveModelId(IReadOnlyList<RimLLMEmbeddingResult> results, EmbeddingGenerationOptions options)
    {
        if (results.Count > 0)
        {
            return results[0].ModelId;
        }

        if (!string.IsNullOrWhiteSpace(options?.ModelId))
        {
            return options.ModelId;
        }

        return _manager.Settings.EmbeddingModel;
    }

    /// <summary>
    /// 把每筆結果轉成 <see cref="Embedding{T}"/>，並彙總供應商回報的輸入 token 數。
    /// </summary>
    private static GeneratedEmbeddings<Embedding<float>> BuildGeneratedEmbeddings(
        IReadOnlyList<RimLLMEmbeddingResult> results,
        string modelId)
    {
        var embeddings = new List<Embedding<float>>(results.Count);
        long? inputTokens = null;
        foreach (RimLLMEmbeddingResult result in results)
        {
            embeddings.Add(new Embedding<float>(result.Vector) { ModelId = result.ModelId ?? modelId });
            if (result.InputTokenCount.HasValue)
            {
                inputTokens = SaturatingMath.Add(inputTokens ?? 0L, result.InputTokenCount.Value);
            }
        }

        var generated = new GeneratedEmbeddings<Embedding<float>>(embeddings);
        if (inputTokens.HasValue)
        {
            // Embedding 沒有輸出 token，總量即輸入量。沒有任何一筆回報時整個
            // Usage 維持 null，讓呼叫端分得出「供應商沒說」與「真的是 0」。
            generated.Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                TotalTokenCount = inputTokens
            };
        }

        return generated;
    }

    private static long? SumInputTokens(IReadOnlyList<RimLLMEmbeddingResult> results)
    {
        long? total = null;
        foreach (long tokenCount in results
                     .Where(result => result.InputTokenCount.HasValue)
                     .Select(result => result.InputTokenCount.Value))
        {
            total = SaturatingMath.Add(total ?? 0L, tokenCount);
        }
        return total;
    }

    public object GetService(Type serviceType, object serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey != null) return null;

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return new EmbeddingGeneratorMetadata(
                _manager.Settings.EmbeddingProvider,
                null,
                _manager.Settings.EmbeddingModel);
        }
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>
    /// 不需清理：本類別只是 <see cref="RimLLMManager"/> 的 facade，不持有任何資源，
    /// 服務生命週期由 Manager 管理。
    /// </summary>
    public void Dispose()
    {
        // 刻意留空：沒有可釋放的資源。
    }
}
