extern alias bclasync;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;
using RimLLM_Framework.Mod;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class SettingsAndTelemetryTests
    {
        [Test]
        public void TestClearLogs()
        {
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);
            
            var entry = new RimLLMManager.RequestLogEntry
            {
                Timestamp = DateTime.UtcNow,
                ModId = "test-mod",
                Provider = "OpenAI",
                Model = "gpt-4",
                Success = true,
                LatencyMs = 150
            };
            manager.RequestLogs.Enqueue(entry);
            Assert.AreEqual(1, manager.RequestLogs.Count);
            
            manager.ClearLogs();
            Assert.AreEqual(0, manager.RequestLogs.Count);
        }

        [Test]
        public void TestMultipleApiKeysRoundRobin()
        {
            var settings = new RimLLMFrameworkSettings();
            settings.SetApiKey("TestProvider", "key-a, key-b; key-c");
            
            // 驗證輪詢邏輯 (多個以逗號或分號分隔的 key 會循環回傳)
            Assert.AreEqual("key-a", settings.GetActiveApiKey("TestProvider"));
            Assert.AreEqual("key-b", settings.GetActiveApiKey("TestProvider"));
            Assert.AreEqual("key-c", settings.GetActiveApiKey("TestProvider"));
            Assert.AreEqual("key-a", settings.GetActiveApiKey("TestProvider")); // 繞回第一個金鑰
        }

        [Test]
        public void TestSettingsDefaultRoutingStrategy()
        {
            var settings = new RimLLMFrameworkSettings();
            Assert.AreEqual(2, settings.RoutingStrategy);
        }

        [Test]
        public void TestTokenUsageAndCostRecording()
        {
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);

            // 1. 初始狀態應該是 0
            Assert.AreEqual(0, mockSettings.TotalPromptTokens);
            Assert.AreEqual(0, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(0f, mockSettings.TotalEstimatedCost);

            // 2. 未知或未維護精確費率的模型只累計 tokens，不再用 provider 類別粗估金額。
            manager.RecordUsage("OpenAI", "gpt-4o", 100000, 50000);

            Assert.AreEqual(100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(50000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(0f, mockSettings.TotalEstimatedCost);

            // 3. 已知精確費率模型才累計估算金額。
            manager.RecordUsage("Gemini", "gemini-2.5-flash", 1000000, 1000000);

            Assert.AreEqual(1100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(1050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(2.80f, mockSettings.TotalEstimatedCost, 0.0001f);

            // 4. Gemini 模型若帶官方 models/ 前綴也能正規化。
            manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 1000000);

            Assert.AreEqual(2100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(2050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(5.60f, mockSettings.TotalEstimatedCost, 0.0001f);

            manager.RecordUsage("Gemini", "gemini-3.5-flash", 1000000, 1000000);

            Assert.AreEqual(3100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(3050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(16.10f, mockSettings.TotalEstimatedCost, 0.0001f);

            manager.RecordUsage("DeepSeek", "deepseek-v4-flash", 1000000, 1000000);
            Assert.AreEqual(4100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(4050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(16.52f, mockSettings.TotalEstimatedCost, 0.0001f);

            manager.RecordUsage("Groq", "llama-3.3-70b-versatile", 1000000, 1000000);
            Assert.AreEqual(5100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(5050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(17.90f, mockSettings.TotalEstimatedCost, 0.0001f);

            manager.RecordUsage("MiniMax", "MiniMax-M3", 1000000, 1000000);
            Assert.AreEqual(6100000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(6050000, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(19.40f, mockSettings.TotalEstimatedCost, 0.0001f);
        }

        [Test]
        public void TestCachedTokenDiscountReducesCost()
        {
            // 快取命中的輸入 Token 應以折扣費率計價，藉此反映 Context Caching 的節省。
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);

            // Gemini：輸入價 $0.3/M，cache read 折扣 0.25x。
            // 1,000,000 輸入中有 800,000 為快取命中 → 200,000*0.3 + 800,000*0.3*0.25 = $0.06 + $0.06 = $0.12
            manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 0, 800000);
            Assert.AreEqual(1000000, mockSettings.TotalPromptTokens);
            Assert.AreEqual(0.12f, mockSettings.TotalEstimatedCost, 0.0001f);

            // 對照組：相同輸入但完全無快取應為 $0.30。
            mockSettings.TotalEstimatedCost = 0f;
            manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 0, 0);
            Assert.AreEqual(0.30f, mockSettings.TotalEstimatedCost, 0.0001f);

            // 防呆：cachedPromptTokens 超過 promptTokens 時應被夾到上限，不會出現負值或溢出。
            mockSettings.TotalEstimatedCost = 0f;
            manager.RecordUsage("Gemini", "models/gemini-2.5-flash", 1000000, 0, 5000000);
            // 全部視為快取命中：1,000,000 * 0.30/M * 0.25 = $0.075
            Assert.AreEqual(0.075f, mockSettings.TotalEstimatedCost, 0.0001f);

            mockSettings.TotalEstimatedCost = 0f;
            manager.RecordUsage("DeepSeek", "deepseek-v4-flash", 1000000, 0, 1000000);
            Assert.AreEqual(0.0028f, mockSettings.TotalEstimatedCost, 0.0001f);
        }

        [Test]
        public void TestResetUsage()
        {
            var mockSettings = new MockSettings();
            var manager = new RimLLMManager(mockSettings);

            // 1. 設置一些初始使用量
            mockSettings.TotalPromptTokens = 5000;
            mockSettings.TotalCompletionTokens = 3000;
            mockSettings.TotalEstimatedCost = 0.05f;

            // 2. 執行重置
            manager.ResetUsage();

            // 3. 應該歸零
            Assert.AreEqual(0, mockSettings.TotalPromptTokens);
            Assert.AreEqual(0, mockSettings.TotalCompletionTokens);
            Assert.AreEqual(0f, mockSettings.TotalEstimatedCost);
        }

        [Test]
        public void TestBudgetReset()
        {
            var mockSettings = new MockSettings();
            var tracker = new RimLLMUsageTracker(mockSettings);

            // 設置非今日重置日期
            mockSettings.DailyBudgetResetDate = "2026-01-01";
            mockSettings.DailyAccumulatedCost = 5.5f;

            // 觸發重置
            tracker.CheckDailyReset();

            string todayStr = DateTime.Today.ToString("yyyy-MM-dd");
            Assert.AreEqual(todayStr, mockSettings.DailyBudgetResetDate);
            Assert.AreEqual(0f, mockSettings.DailyAccumulatedCost);
        }

        [Test]
        public void TestThrottlingAntiAbuse()
        {
            var mockSettings = new MockSettings
            {
                EnableAntiAbuse = true,
                MaxRequestsPerWindow = 3,
                ThrottlingWindowSeconds = 5,
                CoolDownDurationSeconds = 10,
                FallbackChain = new List<string> { "MockSuccess:model-z" }
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("ok")
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.abuse.mod";
            RimLLMProvider.Initialize(manager);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") };

            // 呼叫 3 次應該都成功
            Assert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);
            Assert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);
            Assert.AreEqual("ok", client.GetResponseAsync(messages).GetAwaiter().GetResult().Text);

            // 第 4 次呼叫超出頻率限制，預期觸發 RateLimit 錯誤
            var ex = Assert.Throws<RimLLMException>(() =>
            {
                client.GetResponseAsync(messages).GetAwaiter().GetResult();
            });
            Assert.AreEqual(LLMError.RateLimit, ex.Error);
        }

        [Test]
        public void TestBudgetPolicyHardBlock()
        {
            var mockSettings = new MockSettings
            {
                DailyBudgetLimit = 1.0f,
                DailyAccumulatedCost = 1.2f,
                DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd"),
                BudgetPolicy = 0, // HardBlock
                FallbackChain = new List<string> { "MockSuccess:model-z" }
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("ok")
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.budget.block.mod";
            RimLLMProvider.Initialize(manager);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") };

            var ex = Assert.Throws<RimLLMException>(() =>
            {
                client.GetResponseAsync(messages).GetAwaiter().GetResult();
            });
            Assert.AreEqual(LLMError.QuotaExceeded, ex.Error);
        }

        [Test]
        public void TestBudgetPolicySilentMocking()
        {
            var mockSettings = new MockSettings
            {
                DailyBudgetLimit = 1.0f,
                DailyAccumulatedCost = 1.2f,
                DailyBudgetResetDate = DateTime.Today.ToString("yyyy-MM-dd"),
                BudgetPolicy = 1, // SilentMocking
                FallbackChain = new List<string> { "MockSuccess:model-z" }
            };
            mockSettings.EnabledProviders["MockSuccess"] = true;
            mockSettings.ApiKeys["MockSuccess"] = "mock-key-z";

            var manager = new RimLLMManager(mockSettings);
            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (msgs, opts, model) => System.Threading.Tasks.Task.FromResult("ok")
            };
            manager.RegisterProvider(mockSuccess);

            const string modId = "test.budget.mock.mod";
            RimLLMProvider.Initialize(manager);
            IChatClient client = RimLLMProvider.CreateChatClient(modId);

            // 1. 一般文字請求
            var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "hello") };
            string resText = client.GetResponseAsync(messages).GetAwaiter().GetResult().Text;
            Assert.IsTrue(resText.Contains("沉思") || resText.Contains("resting") || resText.Contains("thinking") || resText.Contains("REST"));

            // 2. 結構化輸出請求，預期回傳空 JSON "{}"
            var resObj = client.GetResponseObjectAsync<TestDataStructure>(messages).GetAwaiter().GetResult();
            Assert.IsNotNull(resObj);
            Assert.AreEqual(100, resObj.Value);
            Assert.AreEqual("default", resObj.Message);
        }

        [Test]
        public void TestTelemetrySaveIsAtomicAndEncryptsChatHistory()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "telemetry.json");

            var previousResolver = RimLLMTelemetryStore.FilePathResolver;
            RimLLMTelemetryStore.FilePathResolver = () => path;
            try
            {
                var store = new RimLLMTelemetryStore();
                store.ChatHistory.Add("SENTINEL-CONVERSATION-CONTENT");
                store.Save();

                Assert.IsFalse(System.IO.File.Exists(path + ".tmp"),
                    "遙測寫入必須先寫暫存檔再原子替換，不得留下 .tmp");

                string raw = System.IO.File.ReadAllText(path);
                Assert.IsFalse(raw.Contains("SENTINEL-CONVERSATION-CONTENT"),
                    "對話歷史不得以明文寫入遙測檔");

                var reloaded = new RimLLMTelemetryStore();
                reloaded.Load();
                CollectionAssert.Contains(reloaded.ChatHistory, "SENTINEL-CONVERSATION-CONTENT");
            }
            finally
            {
                RimLLMTelemetryStore.FilePathResolver = previousResolver;
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void TestTelemetryLoadRecoversFromCorruptFileUsingBackup()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "telemetry.json");

            var previousResolver = RimLLMTelemetryStore.FilePathResolver;
            RimLLMTelemetryStore.FilePathResolver = () => path;
            try
            {
                // 先寫一份有效檔，再寫第二次讓第一份成為 .bak，最後把主檔弄壞。
                var store = new RimLLMTelemetryStore { TotalPromptTokens = 1234 };
                store.Save();
                store.TotalPromptTokens = 5678;
                store.Save();
                System.IO.File.WriteAllText(path, "{ this is not valid json");

                var reloaded = new RimLLMTelemetryStore();
                reloaded.Load();

                Assert.AreEqual(1234, reloaded.TotalPromptTokens,
                    "主檔損毀時應改由備份檔還原");
            }
            finally
            {
                RimLLMTelemetryStore.FilePathResolver = previousResolver;
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void TestLegacyPlaintextChatHistoryIsMigratedOnSave()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RimLLMTelemetryTest_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "telemetry.json");

            var previousResolver = RimLLMTelemetryStore.FilePathResolver;
            RimLLMTelemetryStore.FilePathResolver = () => path;
            try
            {
                // 模擬舊版明文格式
                System.IO.File.WriteAllText(path,
                    "{\"ChatHistory\":[\"LEGACY-PLAINTEXT-LINE\"],\"TotalPromptTokens\":7}");

                var store = new RimLLMTelemetryStore();
                store.Load();
                CollectionAssert.Contains(store.ChatHistory, "LEGACY-PLAINTEXT-LINE");
                Assert.IsTrue(store.IsDirty, "讀到舊版明文歷史後應標記為待重寫以完成加密遷移");

                store.Save();
                string raw = System.IO.File.ReadAllText(path);
                Assert.IsFalse(raw.Contains("LEGACY-PLAINTEXT-LINE"),
                    "遷移後舊版明文歷史必須從磁碟消失");
            }
            finally
            {
                RimLLMTelemetryStore.FilePathResolver = previousResolver;
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        [Test]
        public void TestProviderStatisticsTracking()
        {
            var mockSettings = new MockSettings();
            var tracker = new RimLLMUsageTracker(mockSettings);

            // 1. 記錄 2 次成功與 1 次失敗
            tracker.RecordLog(DateTime.UtcNow, "mod", "Gemini", "gemini-model", true, "", 100);
            tracker.RecordLog(DateTime.UtcNow, "mod", "Gemini", "gemini-model", true, "", 100);
            tracker.RecordLog(DateTime.UtcNow, "mod", "Gemini", "gemini-model", false, "Error", 100);

            Assert.IsTrue(tracker.ProviderStatistics.TryGetValue("Gemini", out var stats));
            Assert.AreEqual(3, stats.TotalCount);
            Assert.AreEqual(2, stats.SuccessCount);
            Assert.AreEqual(1, stats.FailureCount);
            Assert.AreEqual(2.0f / 3.0f, stats.SuccessRate, 0.001f);

            // 2. 清空日誌後應清空統計
            tracker.ClearLogs();
            Assert.AreEqual(0, tracker.ProviderStatistics.Count);
        }
    }
}
