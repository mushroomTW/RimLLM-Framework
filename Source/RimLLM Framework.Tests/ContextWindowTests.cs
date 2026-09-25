using System.Collections.Generic;
using System.Text.Json;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class ContextWindowTests
    {
        [Test]
        public void ManualOverrideWinsOverFetchedValue()
        {
            var settings = new RimLLMFrameworkSettings();
            settings.SetProviderContextWindows("OpenRouter", new Dictionary<string, int> { ["vendor/model"] = 128000 });

            ClassicAssert.AreEqual(128000, settings.GetContextWindow("OpenRouter", "vendor/model"));

            settings.SetContextWindowOverride("OpenRouter:vendor/model", 32000);
            ClassicAssert.AreEqual(32000, settings.GetContextWindow("OpenRouter", "vendor/model"));

            // 0 代表移除手動值，退回 API 拉到的值
            settings.SetContextWindowOverride("OpenRouter:vendor/model", 0);
            ClassicAssert.AreEqual(128000, settings.GetContextWindow("OpenRouter", "vendor/model"));
        }

        [Test]
        public void ManualOverrideWorksWithoutFetchedValue()
        {
            var settings = new RimLLMFrameworkSettings();
            settings.SetContextWindowOverride("OpenAICompatible:local-llama", 8192);

            ClassicAssert.AreEqual(8192, settings.GetContextWindow("OpenAICompatible", "local-llama"));
            ClassicAssert.IsNull(settings.GetContextWindow("OpenAICompatible", "other-model"));
        }

        [Test]
        public void FetchedValuesAreScopedPerProviderAndMergedOnRefresh()
        {
            var settings = new RimLLMFrameworkSettings();
            settings.SetProviderContextWindows("Groq", new Dictionary<string, int> { ["llama-3.3-70b-versatile"] = 131072 });

            // 同名模型在不同供應商的上限可能不同，不可跨供應商套用
            ClassicAssert.IsNull(settings.GetContextWindow("OpenRouter", "llama-3.3-70b-versatile"));

            // 重新整理時來源偶發失敗（本次結果缺漏）不可清掉先前的有效值；有新值則覆寫
            settings.SetProviderContextWindows("Groq", new Dictionary<string, int> { ["other"] = 8192 });
            ClassicAssert.AreEqual(131072, settings.GetContextWindow("Groq", "llama-3.3-70b-versatile"));
            ClassicAssert.AreEqual(8192, settings.GetContextWindow("Groq", "other"));

            settings.SetProviderContextWindows("Groq", new Dictionary<string, int> { ["other"] = 32768 });
            ClassicAssert.AreEqual(32768, settings.GetContextWindow("Groq", "other"));
        }

        [Test]
        public void PublicApiOnlyAcceptsExplicitProviderAndModel()
        {
            var settings = new RimLLMFrameworkSettings();
            settings.SetProviderContextWindows("OpenRouter", new Dictionary<string, int> { ["vendor/model"] = 200000 });
            RimLLMProvider.Initialize(new RimLLMManager(settings));

            ClassicAssert.AreEqual(200000, RimLLMProvider.GetContextWindow("OpenRouter:vendor/model"));
            ClassicAssert.IsNull(RimLLMProvider.GetContextWindow(null));
            ClassicAssert.IsNull(RimLLMProvider.GetContextWindow(""));
            ClassicAssert.IsNull(RimLLMProvider.GetContextWindow("OpenRouter"));
            ClassicAssert.IsNull(RimLLMProvider.GetContextWindow("OpenRouter:"));
            ClassicAssert.IsNull(RimLLMProvider.GetContextWindow("OpenRouter:unknown"));
        }

        [Test]
        public void ParsesKnownContextFieldsFromOpenAiCompatibleModelList()
        {
            const string json = @"{""object"":""list"",""data"":[
                {""id"":""openrouter/model"",""context_length"":1048576},
                {""id"":""groq-model"",""context_window"":131072},
                {""id"":""vllm-model"",""max_model_len"":32768},
                {""id"":""openai-model"",""object"":""model"",""owned_by"":""openai""},
                {""id"":""broken"",""context_length"":null},
                {""id"":""zero"",""context_length"":0}
            ]}";

            var windows = OpenAIProvider.ParseContextWindows(json);

            ClassicAssert.AreEqual(3, windows.Count);
            ClassicAssert.AreEqual(1048576, windows["openrouter/model"]);
            ClassicAssert.AreEqual(131072, windows["groq-model"]);
            ClassicAssert.AreEqual(32768, windows["vllm-model"]);
        }

        [Test]
        public void ParseContextWindowsToleratesUnexpectedShapes()
        {
            CollectionAssert.IsEmpty(OpenAIProvider.ParseContextWindows(null));
            CollectionAssert.IsEmpty(OpenAIProvider.ParseContextWindows("not json"));
            CollectionAssert.IsEmpty(OpenAIProvider.ParseContextWindows(@"{""data"":{}}"));
            CollectionAssert.IsEmpty(OpenAIProvider.ParseContextWindows(@"{""data"":[{""context_length"":""128k""}]}"));
        }

        private const string ModelsDevSample = @"{
            ""moonshotai"": { ""id"": ""moonshotai"", ""models"": {
                ""kimi-k3"": { ""id"": ""kimi-k3"", ""limit"": { ""context"": 1048576, ""output"": 131072 } },
                ""gpt-like"": { ""id"": ""gpt-like"", ""limit"": { ""context"": 400000, ""input"": 272000, ""output"": 128000 } },
                ""image-model"": { ""id"": ""image-model"", ""limit"": { ""context"": 0, ""output"": 0 } },
                ""no-limit"": { ""id"": ""no-limit"" }
            } },
            ""openai"": { ""id"": ""openai"", ""models"": {
                ""gpt-4o"": { ""id"": ""gpt-4o"", ""limit"": { ""context"": 128000, ""output"": 16384 } }
            } }
        }";

        [Test]
        public void ModelsDevPrefersInputLimitAndSkipsZero()
        {
            var windows = new Dictionary<string, int>();
            using (var doc = JsonDocument.Parse(ModelsDevSample))
            {
                ModelsDevCatalog.FillMissing(ModelsDevCatalog.ReadAll(doc.RootElement), ProviderIds.Kimi, windows);
            }

            ClassicAssert.AreEqual(2, windows.Count);
            ClassicAssert.AreEqual(1048576, windows["kimi-k3"]);
            // 有輸入上限就用輸入上限：壓縮歷史要控制的是輸入量，不是含輸出的整個視窗
            ClassicAssert.AreEqual(272000, windows["gpt-like"]);
        }

        [Test]
        public void ModelsDevNeverOverridesProviderApiValues()
        {
            var windows = new Dictionary<string, int> { ["kimi-k3"] = 262144 };
            using (var doc = JsonDocument.Parse(ModelsDevSample))
            {
                ModelsDevCatalog.FillMissing(ModelsDevCatalog.ReadAll(doc.RootElement), ProviderIds.Kimi, windows);
            }

            ClassicAssert.AreEqual(262144, windows["kimi-k3"]);
            ClassicAssert.AreEqual(272000, windows["gpt-like"]);
        }

        [Test]
        public void ModelsDevIgnoresUnmappedProviders()
        {
            ClassicAssert.IsFalse(ModelsDevCatalog.IsCovered(ProviderIds.OpenAICompatible));
            ClassicAssert.IsFalse(ModelsDevCatalog.IsCovered("SomeExternalProvider"));

            var windows = new Dictionary<string, int>();
            using (var doc = JsonDocument.Parse(ModelsDevSample))
            {
                ModelsDevCatalog.FillMissing(ModelsDevCatalog.ReadAll(doc.RootElement), ProviderIds.OpenAICompatible, windows);
            }
            CollectionAssert.IsEmpty(windows);
        }

        private sealed class FilteringProvider : OpenAIProvider
        {
            public FilteringProvider(IRimLLMSettings settings)
                : base(settings, "FilteringTest", "http://localhost:1/v1", "m") { }

            public override System.Threading.Tasks.Task<List<string>> FetchAvailableModelsAsync()
            {
                return System.Threading.Tasks.Task.FromResult(new List<string> { "only-this" });
            }
        }

        [Test]
        public void SubclassModelListOverrideIsHonoredByCatalogFetch()
        {
            var manager = new RimLLMManager(new MockSettings());
            manager.RegisterProvider(new FilteringProvider(new MockSettings()));

            var catalog = manager.FetchProviderModelCatalogAsync("FilteringTest").GetAwaiter().GetResult();

            // 必須走子類別的覆寫（不連網），而不是基底的 /models 請求
            CollectionAssert.AreEqual(new[] { "only-this" }, catalog.Models);
        }

        [Test]
        public void ReadsGeminiNativeInputTokenLimit()
        {
            const string json = @"{""models"":[
                {""name"":""models/gemini-2.5-flash"",""inputTokenLimit"":1048576,""outputTokenLimit"":65536},
                {""name"":""models/no-limit""}
            ]}";

            var windows = new Dictionary<string, int>();
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (JsonElement model in doc.RootElement.GetProperty("models").EnumerateArray())
                {
                    GeminiProvider.ReadInputTokenLimit(model, windows);
                }
            }

            ClassicAssert.AreEqual(1, windows.Count);
            // 去掉 models/ 前綴，與相容端點回傳、設定裡存的模型名稱一致
            ClassicAssert.AreEqual(1048576, windows["gemini-2.5-flash"]);
        }
    }
}
