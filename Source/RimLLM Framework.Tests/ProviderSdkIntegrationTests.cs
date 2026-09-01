using System;
using System.IO;
using System.Reflection;
using Google.GenAI;
using Google.GenAI.Types;
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
            ClassicAssert.IsNotNull(typeof(Client).Assembly, "Google.GenAI assembly 應可載入。");
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

        [Test]
        public void ResponseCacheStoreAssemblyShipsWithMatchingAbstractionsVersion()
        {
            string implementationPath = FindWorkspaceAssembly("Microsoft.Extensions.Caching.Memory.dll");
            ClassicAssert.IsNotNull(implementationPath, "RimWorld Mod Assemblies 應包含 Microsoft.Extensions.Caching.Memory.dll。");

            string abstractionsPath = FindWorkspaceAssembly("Microsoft.Extensions.Caching.Abstractions.dll");
            ClassicAssert.IsNotNull(abstractionsPath, "RimWorld Mod Assemblies 應包含 Microsoft.Extensions.Caching.Abstractions.dll。");

            // RimWorld 把所有 Mod 載入同一個 AppDomain，抽象層與實作層的組件版本一旦漂開
            // 就會在遊戲內炸開，而這在開發端完全看不出來。
            ClassicAssert.AreEqual(
                AssemblyName.GetAssemblyName(abstractionsPath).Version,
                AssemblyName.GetAssemblyName(implementationPath).Version,
                "Caching 抽象層與實作層的組件版本必須一致。");

            Assembly assembly = Assembly.LoadFrom(implementationPath);
            try
            {
                ClassicAssert.IsNotEmpty(assembly.GetTypes());
            }
            catch (ReflectionTypeLoadException exception)
            {
                Assert.Fail(
                    "Microsoft.Extensions.Caching.Memory.dll 反射載入失敗：" +
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

            using (IChatClient geminiClient = GeminiProvider.CreateGeminiChatClient("unit-test-key", "gemini-2.5-flash"))
            {
                ClassicAssert.IsNotNull(geminiClient);
            }
        }

        [Test]
        public void ProvidersRejectMissingCredentialsAndModels()
        {
            Assert.Throws<ArgumentException>(() => OpenAIProvider.CreateOpenAiChatClient("", "model"));
            Assert.Throws<ArgumentException>(() => OpenAIProvider.CreateOpenAiChatClient("key", ""));

            Assert.Throws<ArgumentException>(() => GeminiProvider.CreateGeminiChatClient("", "model"));
            Assert.Throws<ArgumentException>(() => GeminiProvider.CreateGeminiChatClient("key", ""));
        }

        [Test]
        public void NativeSchemaCanBeConvertedToGoogleSchema()
        {
            Schema schema = Schema.FromJson(RimLLMSchemaBuilder.BuildJson(typeof(StructuredResponse), RimLLMSchemaProfile.Gemini));

            ClassicAssert.IsNotNull(schema);
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

            ClassicAssert.IsNull(
                Schema.FromJson(rawJson),
                type.Name + " 的 MEAI 原始輸出不應能轉成 Google.GenAI 的 Schema（FromJson 失敗時回傳 null）。");
        }

        [Test]
        public void OpenAiEndpointNormalizationRemainsNet472Compatible()
        {
            ClassicAssert.AreEqual(
                "https://example.invalid/v1",
                OpenAIProvider.NormalizeEndpoint(" https://example.invalid/v1/chat/completions/ "));
            ClassicAssert.IsNull(OpenAIProvider.NormalizeEndpoint(null));
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

            ClassicAssert.AreEqual(0.25d, config.Temperature);
            ClassicAssert.AreEqual(321, config.MaxOutputTokens);
            ClassicAssert.AreEqual("application/json", config.ResponseMimeType);
            ClassicAssert.IsNotNull(config.ResponseSchema);
            ClassicAssert.IsNotNull(config.SystemInstruction);
            ClassicAssert.AreEqual("你是測試用助手。", config.SystemInstruction.Parts[0].Text);
            ClassicAssert.IsNotNull(config.ThinkingConfig);
            ClassicAssert.AreEqual(4096, config.ThinkingConfig.ThinkingBudget);
            ClassicAssert.IsTrue(config.ThinkingConfig.IncludeThoughts);
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
        public void GeminiCapabilitiesDeclareGeminiSchemaProfile()
        {
            var settings = new MockSettings();
            settings.ApiKeys[ProviderIds.Gemini] = "unit-test-key";

            ClassicAssert.AreEqual(
                RimLLMSchemaProfile.Gemini,
                new GeminiProvider(settings).Capabilities.PreferredSchemaProfile);
            ClassicAssert.AreEqual(
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
            ClassicAssert.IsNotNull(
                BuildGeminiResponseSchema(RimLLMSchemaProfile.Gemini),
                "Gemini 方言的 schema 應能建立 ResponseSchema。");

            ClassicAssert.IsNull(
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

            // 所有內建 provider 一律走官方 SDK（OpenAI / Google.GenAI）+ MEAI，
            // 不再保留 raw HTTP 對話路徑。
            settings.ApiKeys["OpenAI"] = "mock-key";
            settings.ApiKeys["Gemini"] = "mock-key";
            var sdkOpenAi = new TestOpenAIProvider(settings);
            ClassicAssert.IsNotNull(sdkOpenAi.CreateChatClient("gpt-4o"));
            ClassicAssert.IsTrue(sdkOpenAi.Capabilities.SupportsNativeStructuredOutput);

            var sdkGemini = new TestGeminiProvider(settings);
            ClassicAssert.IsNotNull(sdkGemini.CreateChatClient("gemini-1.5-pro"));
            ClassicAssert.IsTrue(sdkGemini.Capabilities.SupportsNativeStructuredOutput);
        }

        [Test]
        public void CreateChatClient_ReturnsBoundFacade()
        {
            var settings = new MockSettings();
            var manager = new RimLLMManager(settings);
            RimLLMProvider.Initialize(manager);
            IChatClient client = RimLLMProvider.CreateChatClient("sdk.integration.test");
            ClassicAssert.IsNotNull(client);
            ClassicAssert.IsInstanceOf<RimLLMChatClient>(client);
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
