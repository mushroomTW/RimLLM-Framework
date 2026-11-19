using System.Text.Json;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

/// <summary>
/// models.dev 目錄費率的解析與優先順序。目錄費率是全域狀態，測試結束一律清空，
/// 並只使用其他測試不會查詢的模型名稱。
/// </summary>
[TestFixture]
[NonParallelizable]
public class ModelPricingTests
{
    [TearDown]
    public void ClearCatalogRates()
    {
        ModelPricing.SetCatalogRates(null);
    }

    [Test]
    public void ModelsDevReadsCostAndSkipsModelsWithoutBothPrices()
    {
        const string json = @"{ ""openai"": { ""models"": {
            ""priced"": { ""cost"": { ""input"": 2, ""output"": 8, ""cache_read"": 0.5 } },
            ""no-cache"": { ""cost"": { ""input"": 4, ""output"": 16 } },
            ""input-only"": { ""cost"": { ""input"": 1 } },
            ""no-cost"": { ""limit"": { ""context"": 1000 } }
        } } }";

        using var doc = JsonDocument.Parse(json);
        var rates = ModelsDevCatalog.ReadAllRates(doc.RootElement)[ProviderIds.OpenAI];

        ClassicAssert.AreEqual(2f, rates["priced"].PromptPerMillion);
        ClassicAssert.AreEqual(8f, rates["priced"].CompletionPerMillion);
        ClassicAssert.AreEqual(0.5f, rates["priced"].CachedPromptPerMillion);
        // 沒有快取讀取價時，沿用輸入價的預設折扣。
        ClassicAssert.AreEqual(1f, rates["no-cache"].CachedPromptPerMillion, 0.0001f);
        // 缺輸入或輸出價的模型不收錄，查詢時退回內建表。
        ClassicAssert.IsFalse(rates.ContainsKey("input-only"));
        ClassicAssert.IsFalse(rates.ContainsKey("no-cost"));
    }

    [Test]
    public void CatalogRateTakesPrecedenceOverBuiltInPrefixMatch()
    {
        // 未載入目錄時，"gpt-4o-catalogtest" 以前綴命中內建的 gpt-4o（輸出 $10）。
        ModelPricing.TryEstimateCost(ProviderIds.OpenAI, "gpt-4o-catalogtest", 0, 1000000, 0, out float builtIn);
        ClassicAssert.AreEqual(10f, builtIn, 0.01f);
        ClassicAssert.AreEqual(ModelPricing.ModelLevelHigh, ModelPricing.GetModelLevel(ProviderIds.OpenAI, "gpt-4o-catalogtest"));

        const string json = @"{ ""openai"": { ""models"": {
            ""gpt-4o-catalogtest"": { ""cost"": { ""input"": 0.1, ""output"": 0.2 } }
        } } }";
        using (var doc = JsonDocument.Parse(json))
        {
            ModelPricing.SetCatalogRates(ModelsDevCatalog.ReadAllRates(doc.RootElement));
        }

        ClassicAssert.IsTrue(ModelPricing.TryEstimateCost(ProviderIds.OpenAI, "gpt-4o-catalogtest", 0, 1000000, 0, out float fromCatalog));
        ClassicAssert.AreEqual(0.2f, fromCatalog, 0.0001f);
        // 目錄更新時分級快取必須失效，否則分級仍停留在舊費率。
        ClassicAssert.AreEqual(ModelPricing.ModelLevelLow, ModelPricing.GetModelLevel(ProviderIds.OpenAI, "gpt-4o-catalogtest"));
    }

    [Test]
    public void BuiltInRatesStillApplyWhenCatalogLacksTheModel()
    {
        const string json = @"{ ""openai"": { ""models"": {
            ""some-other-model"": { ""cost"": { ""input"": 1, ""output"": 1 } }
        } } }";
        using (var doc = JsonDocument.Parse(json))
        {
            ModelPricing.SetCatalogRates(ModelsDevCatalog.ReadAllRates(doc.RootElement));
        }

        ClassicAssert.IsTrue(ModelPricing.TryEstimateCost(ProviderIds.OpenAI, "gpt-4.1-mini", 1000000, 1000000, 0, out float cost));
        ClassicAssert.AreEqual(2.00f, cost, 0.01f);
    }
}
