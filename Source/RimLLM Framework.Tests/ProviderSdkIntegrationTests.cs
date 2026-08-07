using System;
using System.IO;
using System.Reflection;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenAI.Chat;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Providers;
using RimLLM_Framework.SDK;

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
            Assert.IsNotNull(typeof(Client).Assembly, "Google.GenAI assembly 應可載入。");
            Assert.IsNotNull(typeof(IChatClient).Assembly, "Microsoft.Extensions.AI assembly 應可載入。");
            Assert.IsNotNull(typeof(ChatClient).Assembly, "OpenAI assembly 應可載入。");
        }

        [Test]
        public void RimWorldAssembliesIncludeRequiredValueTupleRuntimeAssembly()
        {
            string assemblyPath = FindWorkspaceAssembly("System.ValueTuple.dll");
            Assert.IsNotNull(assemblyPath, "RimWorld Mod Assemblies 應包含 System.ValueTuple.dll。");

            AssemblyName assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
            Assert.AreEqual(new Version(4, 0, 5, 0), assemblyName.Version);
        }

        [Test]
        public void MicrosoftExtensionsAiAssemblyCanBeReflectedWithoutTypeLoadFailures()
        {
            string assemblyPath = FindWorkspaceAssembly("Microsoft.Extensions.AI.dll");
            Assert.IsNotNull(assemblyPath, "RimWorld Mod Assemblies 應包含 Microsoft.Extensions.AI.dll。");

            Assembly assembly = Assembly.LoadFrom(assemblyPath);
            try
            {
                Assert.IsNotEmpty(assembly.GetTypes());
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
        public void OfficialFactoriesCreateAdaptersWithoutSendingRequests()
        {
            var openAiFactory = new OpenAIChatClientFactory();
            using (IChatClient openAiClient = openAiFactory.Create(
                "unit-test-key",
                "unit-test-model",
                "https://example.invalid/v1/chat/completions"))
            {
                Assert.IsNotNull(openAiClient);
            }

            var geminiFactory = new GeminiChatClientFactory();
            using (IChatClient geminiClient = geminiFactory.Create("unit-test-key", "gemini-2.5-flash"))
            {
                Assert.IsNotNull(geminiClient);
            }
        }

        [Test]
        public void FactoriesRejectMissingCredentialsAndModels()
        {
            var openAiFactory = new OpenAIChatClientFactory();
            Assert.Throws<ArgumentException>(() => openAiFactory.Create("", "model"));
            Assert.Throws<ArgumentException>(() => openAiFactory.Create("key", ""));

            var geminiFactory = new GeminiChatClientFactory();
            Assert.Throws<ArgumentException>(() => geminiFactory.Create("", "model"));
            Assert.Throws<ArgumentException>(() => geminiFactory.Create("key", ""));
        }

        [Test]
        public void NativeSchemaCanBeConvertedToGoogleSchema()
        {
            JObject jsonSchema = RimLLMJsonHelper.GenerateJsonSchema(typeof(StructuredResponse), uppercaseTypes: true);
            Schema schema = Schema.FromJson(jsonSchema.ToString());

            Assert.IsNotNull(schema);
        }

        [Test]
        public void OpenAiEndpointNormalizationRemainsNet472Compatible()
        {
            Assert.AreEqual(
                "https://example.invalid/v1",
                OpenAIChatClientFactory.NormalizeEndpoint(" https://example.invalid/v1/chat/completions/ "));
            Assert.IsNull(OpenAIChatClientFactory.NormalizeEndpoint(null));
        }

        [Test]
        public void GeminiNativeConfigMapsSchemaSystemPromptAndThinking()
        {
            var settings = new MockSettings();
            settings.ApiKeys[ProviderIds.Gemini] = "unit-test-key";
            var provider = new GeminiProvider(settings);
            var request = new RimLLMRequest
            {
                Messages = new System.Collections.Generic.List<Microsoft.Extensions.AI.ChatMessage>
                {
                    new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, "你是測試用助手。"),
                    new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "請回傳結構化資料。")
                },
                Temperature = 0.25f,
                MaxOutputTokens = 321,
                ResponseType = typeof(StructuredResponse),
                ReasoningEffort = ReasoningEffort.High,
                EnableContextCaching = false
            };

            MethodInfo method = typeof(GeminiProvider).GetMethod(
                "BuildNativeConfigAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var messages = RimLLMChatClientExecutor.BuildMessages(request);
            var options = RimLLMChatClientExecutor.BuildOptions(request, "gemini-2.5-flash", useNativeSchema: true, null);
            var task = (System.Threading.Tasks.Task)method.Invoke(
                provider,
                new object[] { messages, options, "gemini-2.5-flash", "unit-test-key" });
            task.GetAwaiter().GetResult();
            var config = (GenerateContentConfig)task.GetType().GetProperty("Result").GetValue(task, null);

            Assert.AreEqual(0.25d, config.Temperature);
            Assert.AreEqual(321, config.MaxOutputTokens);
            Assert.AreEqual("application/json", config.ResponseMimeType);
            Assert.IsNotNull(config.ResponseSchema);
            Assert.IsNotNull(config.SystemInstruction);
            Assert.AreEqual("你是測試用助手。", config.SystemInstruction.Parts[0].Text);
            Assert.IsNotNull(config.ThinkingConfig);
            Assert.AreEqual(4096, config.ThinkingConfig.ThinkingBudget);
            Assert.IsTrue(config.ThinkingConfig.IncludeThoughts);
        }

        [Test]
        public void BuiltInSdkProvidersExposeNativeCapabilities()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);

            LLMProviderCapabilities openAi = manager.GetProviderCapabilities(ProviderIds.OpenAI);
            Assert.IsTrue(openAi.SupportsNativeStructuredOutput);
            Assert.IsTrue(openAi.SupportsStreaming);
            Assert.IsTrue(openAi.SupportsUsageMetadata);

            LLMProviderCapabilities gemini = manager.GetProviderCapabilities(ProviderIds.Gemini);
            Assert.IsTrue(gemini.SupportsNativeStructuredOutput);
            Assert.IsTrue(gemini.SupportsStreaming);
            Assert.IsTrue(gemini.SupportsUsageMetadata);

            LLMProviderCapabilities unknown = manager.GetProviderCapabilities("missing-provider");
            Assert.IsFalse(unknown.SupportsNativeStructuredOutput);
            Assert.IsFalse(unknown.SupportsStreaming);

            // 任務 6 之後所有內建 provider 一律走官方 SDK（OpenAI / Google.GenAI）+ MEAI，
            // 不再保留 raw HTTP 對話路徑。
            var sdkOpenAi = new TestOpenAIProvider(settings);
            Assert.IsTrue(sdkOpenAi.UsesIChatClient);
            Assert.IsTrue(sdkOpenAi.Capabilities.SupportsNativeStructuredOutput);

            var sdkGemini = new TestGeminiProvider(settings);
            Assert.IsTrue(sdkGemini.UsesIChatClient);
            Assert.IsTrue(sdkGemini.Capabilities.SupportsNativeStructuredOutput);
        }

        [Test]
        public void CreateChatClient_ReturnsBoundFacade()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);
            RimLLMProvider.Initialize(manager);
            RimLLMProvider.RegisterClient("sdk.integration.test");
            IChatClient client = RimLLMProvider.CreateChatClient("sdk.integration.test");
            Assert.IsNotNull(client);
            Assert.IsInstanceOf<RimLLMChatClient>(client);
            ChatClientMetadata metadata = client.GetService<ChatClientMetadata>();
            Assert.IsNotNull(metadata);
            Assert.AreEqual("RimLLM", metadata.ProviderName);
        }

        [Test]
        public void CreateChatClient_UnregisteredModThrows()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);
            RimLLMProvider.Initialize(manager);
            Assert.Throws<RimLLMException>(() => RimLLMProvider.CreateChatClient("never.registered"));
        }

        private sealed class StructuredResponse
        {
            public string Name { get; set; }
            public StructuredChild Child { get; set; }
            public System.Collections.Generic.List<StructuredChild> Items { get; set; }
        }

        private sealed class StructuredChild
        {
            public int Value { get; set; }
        }
    }
}
