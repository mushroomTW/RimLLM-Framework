using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 並行回歸測試：以壓力迴圈重現讀寫交錯。競態本質上是機率性的，迴圈次數取在本機能穩定重現修正前錯誤的量級。
/// </summary>
[TestFixture]
[NonParallelizable]
public class ConcurrencyRegressionTests
{
    [TearDown]
    public void ClearCatalogRates()
    {
        ModelPricing.SetCatalogRates(ratesByProvider: null);
    }

    /// <summary>每次寫入的快取量都等於總量，命中率恆為 1；總量與快取量分開讀時會讀到不同時間點而偏離。</summary>
    [Test]
    public void CacheHitRateIsReadFromOneSnapshot()
    {
        var stats = new RimLLMUsageTracker.ProviderStats();
        bool stop = false;
        Task writer = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop)) stats.AddTokenCounts(100, 100);
        });

        int torn = 0;
        for (int i = 0; i < 2_000_000; i++)
        {
            float rate = stats.ContextCacheHitRate;
            if (rate > 0f && Math.Abs(rate - 1f) > 1e-6f) torn++;
        }
        Volatile.Write(ref stop, true);
        writer.Wait();

        Assert.That(torn, Is.Zero, "命中率應為同一時間點的快取量／總量");
    }

    /// <summary>設定頁用快照自己算命中率，總量與快取量必須來自同一時間點。</summary>
    [Test]
    public void PromptTokenSnapshotIsReadAtOnePoint()
    {
        var stats = new RimLLMUsageTracker.ProviderStats();
        bool stop = false;
        Task writer = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop)) stats.AddTokenCounts(100, 100);
        });

        int torn = 0;
        for (int i = 0; i < 2_000_000; i++)
        {
            (long total, long cached) = stats.PromptTokenSnapshot;
            if (total != cached) torn++;
        }
        Volatile.Write(ref stop, true);
        writer.Wait();

        Assert.That(torn, Is.Zero);
    }

    /// <summary>以舊費率算出的分級可能在快取清除後才寫回，之後一直回傳過期分級。</summary>
    [Test]
    public void ModelLevelFollowsLatestCatalogRates()
    {
        const string Provider = "racetest";
        const string Model = "race-model";
        var high = new Dictionary<string, Dictionary<string, ModelPricing.CostRate>>
        {
            [Provider] = new() { [Model] = new ModelPricing.CostRate(1f, 30f) },
        };
        var low = new Dictionary<string, Dictionary<string, ModelPricing.CostRate>>
        {
            [Provider] = new() { [Model] = new ModelPricing.CostRate(0.01f, 0.01f) },
        };

        int stale = 0;
        for (int round = 0; round < 2_000; round++)
        {
            ModelPricing.SetCatalogRates(high);
            using var go = new Barrier(2);
            Task reader = Task.Run(() =>
            {
                go.SignalAndWait();
                for (int i = 0; i < 50; i++) ModelPricing.GetModelLevel(Provider, Model);
            });
            go.SignalAndWait();
            ModelPricing.SetCatalogRates(low);
            reader.Wait();

            if (ModelPricing.GetModelLevel(Provider, Model) != 1) stale++;
        }

        Assert.That(stale, Is.Zero, "費率更新後的分級必須以最新費率為準");
    }

    /// <summary>首次存取的快取在並行下必須回傳完整、與 Json 相同的 Element。</summary>
    [Test]
    public void SchemaElementIsConsistentUnderConcurrentFirstAccess()
    {
        int bad = 0;
        for (int round = 0; round < 2_000; round++)
        {
            RimLLMSchemaResult schema = RimLLMSchemaBuilder.Build(typeof(Payload));
            Parallel.For(0, 4, _ =>
            {
                if (schema.Element.GetRawText() != schema.Json) Interlocked.Increment(ref bad);
            });
        }
        Assert.That(bad, Is.Zero);
    }

    private sealed class Payload
    {
        public string Name { get; set; }
        public int Count { get; set; }
        public List<string> Tags { get; set; }
    }
}
