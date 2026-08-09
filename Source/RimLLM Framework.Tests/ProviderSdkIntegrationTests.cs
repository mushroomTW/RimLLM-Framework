using System;
using System.IO;
using System.Reflection;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using NUnit.Framework;
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
            Schema schema = Schema.FromJson(RimLLMSchemaBuilder.BuildJson(typeof(StructuredResponse), RimLLMSchemaProfile.Gemini));

            Assert.IsNotNull(schema);
        }

        /// <summary>
        /// 迴歸保護：MEAI <c>AIJsonUtilities.CreateJsonSchema</c> 的**原始**輸出無法被
        /// <c>Google.GenAI</c> 的 <c>Schema.FromJson</c> 接受，所以框架必須在其上加一層正規化。
        ///
        /// 根因是可為 null 的成員被表達成 <c>"type": ["string","null"]</c> 聯集，
        /// 而 <c>Google.GenAI.Types.Schema.Type</c> 是單一列舉值：
        /// <c>The JSON value could not be converted to System.Nullable&lt;Google.GenAI.Types.Type&gt;</c>。
        ///
        /// 特別注意失敗模式：<c>Schema.FromJson</c> **不會拋例外**，它吞掉 JsonException、
        /// 把堆疊印到 stderr，然後回傳 <see langword="null"/>。而 <c>GeminiProvider.BuildNativeConfigAsync</c>
        /// 是直接 <c>config.ResponseSchema = Schema.FromJson(schemaJson)</c>，所以 Gemini 會靜默地
        /// 收不到任何 schema，只剩 <c>responseMimeType: application/json</c> —— 沒有任何錯誤浮上來。
        ///
        /// 這個宣稱長期只寫在 README 而沒有測試佐證。若哪天 MEAI 或 Google.GenAI 改版讓它通過，
        /// 本測試會失敗 —— 那是重新評估正規化層是否還有必要的訊號，不是把測試刪掉的理由。
        /// </summary>
        [Test]
        public void RawMeaiSchemaIsRejectedByGoogleSchemaFromJson()
        {
            AssertRawMeaiSchemaIsRejected(typeof(NullableTestDataStructure));
            AssertRawMeaiSchemaIsRejected(typeof(ComplexTestDataStructure));
            AssertRawMeaiSchemaIsRejected(typeof(StructuredResponse));
        }

        private static void AssertRawMeaiSchemaIsRejected(System.Type type)
        {
            string rawJson = AIJsonUtilities.CreateJsonSchema(type).GetRawText();
            TestContext.WriteLine(type.Name + " 的 MEAI 原始輸出：" + rawJson);

            StringAssert.Contains(
                "\",\"null\"]",
                rawJson,
                type.Name + " 的 MEAI 輸出應含可為 null 的聯集型別，這正是 Gemini 無法解析的形狀。");

            Assert.IsNull(
                Schema.FromJson(rawJson),
                type.Name + " 的 MEAI 原始輸出不應能轉成 Google.GenAI 的 Schema（FromJson 失敗時回傳 null）。");
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
            Assert.IsNotNull(assemblyPath, "RimWorld Mod Assemblies 應包含 " + fileName + "。");

            var names = new System.Collections.Generic.List<string>();
            foreach (AssemblyName reference in Assembly.ReflectionOnlyLoadFrom(assemblyPath).GetReferencedAssemblies())
            {
                names.Add(reference.Name);
            }

            return names;
        }

        [Test]
        public void GeminiCapabilitiesDeclareGeminiSchemaProfile()
        {
            var settings = new MockSettings();
            settings.ApiKeys[ProviderIds.Gemini] = "unit-test-key";

            Assert.AreEqual(
                RimLLMSchemaProfile.Gemini,
                new GeminiProvider(settings).Capabilities.PreferredSchemaProfile);
            Assert.AreEqual(
                RimLLMSchemaProfile.OpenAI,
                new OpenAIProvider(settings).Capabilities.PreferredSchemaProfile,
                "OpenAI 家族沿用預設方言。");
        }

        /// <summary>
        /// 方言接線的端到端驗證：帶 <c>int?</c> 成員的型別在 OpenAI 方言下會產生聯集型別，
        /// 而 <c>Schema.FromJson</c> 對聯集會靜默回傳 null，導致 Gemini 收不到 schema。
        /// 只有把 Gemini 方言一路傳到 <c>BuildOptions</c>，<c>ResponseSchema</c> 才會真的建立起來。
        /// </summary>
        [Test]
        public void GeminiNativeConfigAcceptsSchemaWithNullableMember()
        {
            Assert.IsNotNull(
                BuildGeminiResponseSchema(RimLLMSchemaProfile.Gemini),
                "Gemini 方言的 schema 應能建立 ResponseSchema。");

            Assert.IsNull(
                BuildGeminiResponseSchema(RimLLMSchemaProfile.OpenAI),
                "反向對照：OpenAI 方言的聯集型別會讓 Gemini 靜默收不到 schema —— 方言接線斷掉時就會變成這樣。");
        }

        private static object BuildGeminiResponseSchema(RimLLMSchemaProfile profile)
        {
            var settings = new MockSettings();
            settings.ApiKeys[ProviderIds.Gemini] = "unit-test-key";
            var provider = new GeminiProvider(settings);
            var request = new RimLLMRequest
            {
                Messages = new System.Collections.Generic.List<Microsoft.Extensions.AI.ChatMessage>
                {
                    new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "請回傳結構化資料。")
                },
                ResponseType = typeof(NullableTestDataStructure)
            };

            MethodInfo method = typeof(GeminiProvider).GetMethod(
                "BuildNativeConfigAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var messages = RimLLMChatClientExecutor.BuildMessages(request);
            var options = RimLLMChatClientExecutor.BuildOptions(request, "gemini-2.5-flash", true, null, profile);
            var task = (System.Threading.Tasks.Task)method.Invoke(
                provider,
                new object[] { messages, options, "gemini-2.5-flash", "unit-test-key" });
            task.GetAwaiter().GetResult();
            var config = (GenerateContentConfig)task.GetType().GetProperty("Result").GetValue(task, null);
            return config.ResponseSchema;
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
            IChatClient client = RimLLMProvider.CreateChatClient("sdk.integration.test");
            Assert.IsNotNull(client);
            Assert.IsInstanceOf<RimLLMChatClient>(client);
            ChatClientMetadata metadata = client.GetService<ChatClientMetadata>();
            Assert.IsNotNull(metadata);
            Assert.AreEqual("RimLLM", metadata.ProviderName);
        }

        [Test]
        public void CreateChatClient_NeedsNoRegistration()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);
            RimLLMProvider.Initialize(manager);

            // 已移除呼叫者組件驗證：任何 modId 都能直接取得 client，不需事先註冊。
            Assert.IsNotNull(RimLLMProvider.CreateChatClient("never.registered"));

            // modId 仍為必填，因為防濫用節流與遙測歸屬都以它為鍵。
            Assert.Throws<ArgumentException>(() => RimLLMProvider.CreateChatClient(string.Empty));
            Assert.Throws<ArgumentException>(() => RimLLMProvider.CreateEmbeddingGenerator(null));
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
