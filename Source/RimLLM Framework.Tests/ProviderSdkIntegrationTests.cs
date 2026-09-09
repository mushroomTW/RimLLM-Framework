using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using OpenAI.Chat;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// 驗證官方 SDK、MEAI adapter 與 provider capability 分流。
    /// 測試不使用真實 API key，也不發出網路請求。
    /// </summary>
    [TestFixture]
    public class ProviderSdkIntegrationTests
    {
        [Test]
        public void OfficialProviderAssembliesCanBeLoaded()
        {
            ClassicAssert.IsNotNull(typeof(IChatClient).Assembly, "Microsoft.Extensions.AI assembly 應可載入。");
            ClassicAssert.IsNotNull(typeof(ChatClient).Assembly, "OpenAI assembly 應可載入。");
        }

        [Test]
        public void RimWorldAssembliesIncludeRequiredValueTupleRuntimeAssembly()
        {
            string assemblyPath = FindWorkspaceAssembly("System.ValueTuple.dll");
            ClassicAssert.IsNotNull(assemblyPath, "RimWorld Mod Assemblies 應包含 System.ValueTuple.dll。");

            AssemblyName assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
            ClassicAssert.AreEqual(new Version(4, 0, 5, 0), assemblyName.Version);
        }

        [Test]
        public void MicrosoftExtensionsAiAssemblyCanBeReflectedWithoutTypeLoadFailures()
        {
            string assemblyPath = FindWorkspaceAssembly("Microsoft.Extensions.AI.dll");
            ClassicAssert.IsNotNull(assemblyPath, "RimWorld Mod Assemblies 應包含 Microsoft.Extensions.AI.dll。");

            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            try
            {
                ClassicAssert.IsNotEmpty(assembly.GetTypes());
            }
            catch (ReflectionTypeLoadException exception)
            {
                Assert.Fail(
                    "Microsoft.Extensions.AI.dll 反射載入失敗：" +
                    string.Join(
                        "\n",
                        Array.ConvertAll(
                            exception.LoaderExceptions,
                            loaderException => loaderException?.Message ?? "未知載入例外")));
            }
        }



        private static string FindWorkspaceAssembly(string fileName)
        {
            string directory = TestContext.CurrentContext.TestDirectory;
            for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(directory); depth++)
            {
                string candidate = Path.Combine(directory, "Assemblies", fileName);
                if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }

                DirectoryInfo parent = Directory.GetParent(directory);
                directory = parent?.FullName;
            }

            return null;
        }

        [Test]
        public void OfficialProvidersCreateAdaptersWithoutSendingRequests()
        {
            using (IChatClient openAiClient = OpenAIProvider.CreateOpenAiChatClient(
                "unit-test-key",
                "unit-test-model",
                "https://example.invalid/v1/chat/completions"))
            {
                ClassicAssert.IsNotNull(openAiClient);
            }

            // Gemini 經官方 OpenAI 相容端點存取，建立 client 不發出網路請求。
            var geminiSettings = new MockSettings();
            geminiSettings.ApiKeys[ProviderIds.Gemini] = "unit-test-key";
            using (IChatClient geminiClient = new GeminiProvider(geminiSettings).CreateChatClient("gemini-2.5-flash"))
            {
                ClassicAssert.IsNotNull(geminiClient);
            }
        }

        [Test]
        public void ProvidersRejectMissingCredentialsAndModels()
        {
            Assert.Throws<ArgumentException>(() => OpenAIProvider.CreateOpenAiChatClient("", "model"));
            Assert.Throws<ArgumentException>(() => OpenAIProvider.CreateOpenAiChatClient("key", ""));

            // Gemini 沿用 OpenAI 路徑：無金鑰時同樣拒絕建立。
            Assert.Throws<ArgumentException>(() => new GeminiProvider(new MockSettings()).CreateChatClient("gemini-2.5-flash"));
        }

        [Test]
        public void GeminiDeclaresOpenAiFamilyCapabilities()
        {
            var settings = new MockSettings();
            settings.ApiKeys[ProviderIds.Gemini] = "unit-test-key";
            var provider = new GeminiProvider(settings);

            ClassicAssert.AreEqual(ProviderIds.Gemini, provider.ProviderId);
            ClassicAssert.IsTrue(provider.Capabilities.SupportsNativeStructuredOutput);
            ClassicAssert.IsTrue(provider.Capabilities.SupportsStreaming);
            ClassicAssert.IsTrue(provider.Capabilities.SupportsUsageMetadata);
            ClassicAssert.IsTrue(provider.Capabilities.SupportsFunctionCalling);
        }

        [Test]
        public void OpenAiEndpointNormalizationRemainsNet472Compatible()
        {
            ClassicAssert.AreEqual(
                "https://example.invalid/v1",
                OpenAIProvider.NormalizeEndpoint(" https://example.invalid/v1/chat/completions/ "));
            ClassicAssert.IsNull(OpenAIProvider.NormalizeEndpoint(null));
        }

        /// <summary>
        /// Schema 產生不得經由 MEAI 的 <c>AIJsonUtilities.CreateJsonSchema</c> 包裝層。
        ///
        /// 該包裝層出貨的是 net462 資產，會參考 <c>System.ComponentModel.DataAnnotations</c>
        /// （用來讀 <c>[EmailAddress]</c> 等驗證屬性豐富 schema）。RimWorld 的 Mono BCL 沒有那個組件，
        /// 實機上會拋 <c>TypeLoadException</c>，整份 schema 產生靜默降級成舊的反射實作 ——
        /// 而單元測試跑在有 GAC 的真 .NET Framework 上，完全看不出來。
        ///
        /// 因此改直呼 <c>System.Text.Json.Schema.JsonSchemaExporter</c>（MEAI 內部用的同一個引擎）。
        /// 本測試釘住這個相依差異：哪天 MEAI 拿掉該參考，這裡會失敗，屆時才可以考慮改回包裝層。
        /// </summary>
        [Test]
        public void SchemaGenerationEngineHasNoDataAnnotationsDependency()
        {
            const string dataAnnotations = "System.ComponentModel.DataAnnotations";

            CollectionAssert.DoesNotContain(
                ReferencedAssemblyNames("System.Text.Json.dll"),
                dataAnnotations,
                "System.Text.Json 不得相依 DataAnnotations —— 這是 schema 產生引擎能在 RimWorld Mono 上執行的前提。");

            CollectionAssert.Contains(
                ReferencedAssemblyNames("Microsoft.Extensions.AI.Abstractions.dll"),
                dataAnnotations,
                "MEAI 仍相依 DataAnnotations，所以仍不可改用 AIJsonUtilities.CreateJsonSchema。若此處失敗代表限制已解除。");
        }

        private static System.Collections.Generic.List<string> ReferencedAssemblyNames(string fileName)
        {
            string assemblyPath = FindWorkspaceAssembly(fileName);
            ClassicAssert.IsNotNull(assemblyPath, "RimWorld Mod Assemblies 應包含 " + fileName + "。");

            var names = new System.Collections.Generic.List<string>();
            foreach (AssemblyName reference in Assembly.ReflectionOnlyLoadFrom(assemblyPath).GetReferencedAssemblies())
            {
                names.Add(reference.Name);
            }

            return names;
        }

        [Test]
        public void BuiltInSdkProvidersExposeNativeCapabilities()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);

            LLMProviderCapabilities openAi = manager.GetProviderCapabilities(ProviderIds.OpenAI);
            ClassicAssert.IsTrue(openAi.SupportsNativeStructuredOutput);
            ClassicAssert.IsTrue(openAi.SupportsStreaming);
            ClassicAssert.IsTrue(openAi.SupportsUsageMetadata);

            LLMProviderCapabilities gemini = manager.GetProviderCapabilities(ProviderIds.Gemini);
            ClassicAssert.IsTrue(gemini.SupportsNativeStructuredOutput);
            ClassicAssert.IsTrue(gemini.SupportsStreaming);
            ClassicAssert.IsTrue(gemini.SupportsUsageMetadata);

            LLMProviderCapabilities unknown = manager.GetProviderCapabilities("missing-provider");
            ClassicAssert.IsFalse(unknown.SupportsNativeStructuredOutput);
            ClassicAssert.IsFalse(unknown.SupportsStreaming);

            // 所有內建 provider 一律走 OpenAI SDK（含 OpenAI 相容端點）+ MEAI，
            // 不再保留 raw HTTP 對話路徑，也不再使用 Google.GenAI。
            settings.ApiKeys["OpenAI"] = "mock-key";
            settings.ApiKeys["Gemini"] = "mock-key";
            var sdkOpenAi = new TestOpenAIProvider(settings);
            ClassicAssert.IsNotNull(sdkOpenAi.CreateChatClient("gpt-4o"));
            ClassicAssert.IsTrue(sdkOpenAi.Capabilities.SupportsNativeStructuredOutput);

            var sdkGemini = new TestGeminiProvider(settings);
            ClassicAssert.IsNotNull(sdkGemini.CreateChatClient("gemini-1.5-pro"));
            ClassicAssert.IsTrue(sdkGemini.Capabilities.SupportsNativeStructuredOutput);

            // 測試 OpenAICompatibleProvider (RequiresApiKey = false) 且無 API Key 時自動填入 PlaceholderApiKey
            var localSettings = new MockSettings();
            var localProvider = new OpenAICompatibleProvider(localSettings);
            using (var client = localProvider.CreateChatClient("local-model"))
            {
                ClassicAssert.IsNotNull(client);
            }
        }

        [Test]
        public void CreateChatClient_ReturnsBoundFacade()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);
            RimLLMProvider.Initialize(manager);
            IChatClient client = RimLLMProvider.CreateChatClient("sdk.integration.test");
            ClassicAssert.IsNotNull(client);
            // 回傳的是包了中介層的堆疊，不再是裸的 facade；
            // 「這條堆疊屬於框架」改由 GetService 表達（DelegatingChatClient 會往內層轉發）。
            ClassicAssert.IsInstanceOf<RimLLMFailoverChatClient>(client.GetService(typeof(RimLLMFailoverChatClient)));
            ChatClientMetadata metadata = client.GetService<ChatClientMetadata>();
            ClassicAssert.IsNotNull(metadata);
            ClassicAssert.AreEqual("RimLLM", metadata.ProviderName);
        }

        [Test]
        public void CreateChatClient_NeedsNoRegistration()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);
            RimLLMProvider.Initialize(manager);

            // 已移除呼叫者組件驗證：任何 modId 都能直接取得 client，不需事先註冊。
            ClassicAssert.IsNotNull(RimLLMProvider.CreateChatClient("never.registered"));

            // modId 仍為必填，因為防濫用節流與遙測歸屬都以它為鍵。
            Assert.Throws<ArgumentException>(() => RimLLMProvider.CreateChatClient(string.Empty));
            Assert.Throws<ArgumentException>(() => RimLLMProvider.CreateEmbeddingGenerator(null));
        }

    }
}
