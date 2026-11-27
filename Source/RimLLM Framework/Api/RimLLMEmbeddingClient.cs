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
/// 綁定單一 Mod 的 IEmbeddingGenerator facade，透過 RimLLMProvider.CreateEmbeddingGenerator(modId) 取得。
/// </summary>
internal sealed class RimLLMEmbeddingClient : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly RimLLMManager _manager;
    private readonly string _modId;

    private readonly RimLLMAppAttribution? _attribution;

    internal RimLLMEmbeddingClient(RimLLMManager manager, string modId)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _modId = modId ?? throw new ArgumentNullException(nameof(modId));
        _attribution = manager.AttributionResolver?.Invoke(modId);
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var inputValues = new List<string>(values);
        if (inputValues.Count == 0)
        {
            return new GeneratedEmbeddings<Embedding<float>>();
        }

        _manager.UsageTracker.RecordCaller(_modId);
        _manager.CheckAntiAbuseForMod(_modId);
        RimLLMAppAttribution.Current = _attribution;

        long estimatedTokens = EstimateTotalTokens(inputValues);

        using RimLLMUsageTracker.BudgetReservation reservation = _manager.UsageTracker.ReserveBudgetOrThrow(estimatedTokens);
        IReadOnlyList<RimLLMEmbeddingResult> results = await _manager.EmbeddingService
            .ComputeEmbeddingsAsync(
                inputValues,
                options,
                batchResults => RecordBatchUsage(reservation, batchResults),
                cancellationToken)
            .ConfigureAwait(false);

        // 模型與供應商取自服務在請求開始時捕捉的值，不可在 await 之後重讀設定，
        // 否則請求期間切換設定會讓向量被標成新模型、用量記到新供應商。
        string? modelId = ResolveModelId(results, options);
        return BuildGeneratedEmbeddings(results, modelId);
    }

    private static long EstimateTotalTokens(IReadOnlyList<string> values)
    {
        long estimatedTokens = 0;
        foreach (string value in values)
        {
            estimatedTokens = SaturatingMath.Add(estimatedTokens, RimLLMChatClientExecutor.EstimateTokens(value));
        }
        return estimatedTokens;
    }

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

    // 順序：服務捕捉到的實際模型 > 呼叫端覆寫 > 目前設定，不可顛倒。
    private string? ResolveModelId(IReadOnlyList<RimLLMEmbeddingResult> results, EmbeddingGenerationOptions? options)
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

    private static GeneratedEmbeddings<Embedding<float>> BuildGeneratedEmbeddings(
        IReadOnlyList<RimLLMEmbeddingResult> results,
        string? modelId)
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
            // 沒有任何一筆回報時 Usage 維持 null，讓呼叫端分得出「供應商沒說」與「真的是 0」。
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
        foreach (RimLLMEmbeddingResult result in results)
        {
            if (result.InputTokenCount is not { } tokenCount) continue;
            total = SaturatingMath.Add(total ?? 0L, tokenCount);
        }
        return total;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey != null) return null;

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return new EmbeddingGeneratorMetadata(
                _manager.Settings.EmbeddingProvider,
                providerUri: null,
                _manager.Settings.EmbeddingModel);
        }
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // 刻意留空：facade 不持有資源，生命週期由 Manager 管理。
    }
}
