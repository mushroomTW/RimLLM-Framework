using NUnit.Framework;
using System;
using System.Reflection;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Mod;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class FrameworkTests
    {
        [OneTimeSetUp]
        public void Setup()
        {
            // 在運行任何測試前，如果 Settings 為 null，利用反射初始化一個預設 Settings
            if (RimLLMFrameworkMod.Settings == null)
            {
                var settingsInstance = new RimLLMFrameworkSettings();
                var prop = typeof(RimLLMFrameworkMod).GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
                if (prop != null)
                {
                    prop.SetValue(null, settingsInstance);
                }
            }
        }

        [Test]
        public void TestEncryption()
        {
            string original = "sk-proj-1234567890abcdefghijklmnopqrstuvwxyz";
            string cipher = EncryptionUtility.Encrypt(original);
            Assert.IsNotEmpty(cipher);
            Assert.AreNotEqual(original, cipher);

            string decrypted = EncryptionUtility.Decrypt(cipher);
            Assert.AreEqual(original, decrypted);

            Assert.AreEqual("", EncryptionUtility.Encrypt(""));
            Assert.AreEqual("", EncryptionUtility.Decrypt(""));
        }

        [Test]
        public void TestClientRegistry()
        {
            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            Assembly externalAssembly = typeof(string).Assembly;
            string modId = "test.unit.mod.id";

            // 1. 正常註冊與驗證
            ClientRegistry.RegisterClient(modId, thisAssembly);
            Assert.IsTrue(ClientRegistry.Verify(modId, thisAssembly));

            // 2. 阻擋假冒安全驗證
            Assert.IsFalse(ClientRegistry.Verify(modId, externalAssembly));

            // 3. 自動補註冊機制驗證
            string newModId = "auto.unit.mod.id";
            Assert.IsTrue(ClientRegistry.Verify(newModId, thisAssembly));
            Assert.IsFalse(ClientRegistry.Verify(newModId, externalAssembly));
        }

        [Test]
        public void TestFallbackMechanism()
        {
            var manager = new RimLLMManager();
            var registerMethod = manager.GetType().GetMethod("RegisterProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(registerMethod, "找不到 RegisterProvider 反射方法");

            int failCalls = 0;
            int successCalls = 0;

            var mockFail = new MockTestProvider
            {
                ProviderId = "MockFail",
                GenerateHandler = (req, model) =>
                {
                    failCalls++;
                    throw new Exception("Simulated connection failure");
                }
            };

            var mockSuccess = new MockTestProvider
            {
                ProviderId = "MockSuccess",
                GenerateHandler = (req, model) =>
                {
                    successCalls++;
                    return System.Threading.Tasks.Task.FromResult("success-data");
                }
            };

            registerMethod.Invoke(manager, new object[] { mockFail });
            registerMethod.Invoke(manager, new object[] { mockSuccess });

            var backupChain = RimLLMFrameworkMod.Settings.FallbackChain;
            bool failEnabled = RimLLMFrameworkMod.Settings.IsProviderEnabled("MockFail");
            bool successEnabled = RimLLMFrameworkMod.Settings.IsProviderEnabled("MockSuccess");

            RimLLMFrameworkMod.Settings.FallbackChain = new List<string> { "MockFail:model-x", "MockSuccess:model-y" };
            RimLLMFrameworkMod.Settings.SetProviderEnabled("MockFail", true);
            RimLLMFrameworkMod.Settings.SetProviderEnabled("MockSuccess", true);
            RimLLMFrameworkMod.Settings.SetApiKey("MockFail", "mock-key-1");
            RimLLMFrameworkMod.Settings.SetApiKey("MockSuccess", "mock-key-2");

            try
            {
                var request = new LLMRequest 
                { 
                    ModId = "test.fallback.unit.id",
                    Prompt = "test" 
                };

                string result = manager.GenerateAsync(request).GetAwaiter().GetResult();

                Assert.AreEqual("success-data", result);
                Assert.AreEqual(1, failCalls);
                Assert.AreEqual(1, successCalls);
            }
            finally
            {
                RimLLMFrameworkMod.Settings.FallbackChain = backupChain;
                RimLLMFrameworkMod.Settings.SetProviderEnabled("MockFail", failEnabled);
                RimLLMFrameworkMod.Settings.SetProviderEnabled("MockSuccess", successEnabled);
                RimLLMFrameworkMod.Settings.SetApiKey("MockFail", null);
                RimLLMFrameworkMod.Settings.SetApiKey("MockSuccess", null);
            }
        }
    }

    public class MockTestProvider : ILLMProvider
    {
        public string ProviderId { get; set; }
        
        public Func<LLMRequest, string, System.Threading.Tasks.Task<string>> GenerateHandler { get; set; }

        public System.Threading.Tasks.Task<string> GenerateAsync(LLMRequest request, string model)
        {
            return GenerateHandler != null ? GenerateHandler(request, model) : System.Threading.Tasks.Task.FromResult("");
        }

        public System.Threading.Tasks.Task StreamAsync(LLMRequest request, string model, Action<string> onChunkReceived)
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task<TestResult> TestConnectionAsync()
        {
            return System.Threading.Tasks.Task.FromResult(new TestResult { Success = true });
        }

        public System.Threading.Tasks.Task<List<string>> FetchAvailableModelsAsync()
        {
            return System.Threading.Tasks.Task.FromResult(new List<string> { "model-test" });
        }
    }
}
