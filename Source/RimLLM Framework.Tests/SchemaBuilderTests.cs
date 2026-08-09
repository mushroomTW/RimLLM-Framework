using System;
using System.Collections.Generic;
using Google.GenAI.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NUnit.Framework;
using RimLLM_Framework.Manager;

// Google.GenAI.Types 也有一個 Type，會與 System.Type 撞名。
using Type = System.Type;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// <see cref="RimLLMSchemaBuilder"/> 的形狀不變式與契約對齊測試。
    ///
    /// 這裡守住的核心風險有兩個：
    /// 一是 schema 由 System.Text.Json 的 exporter 產生、反序列化卻由 Newtonsoft 執行，
    /// 兩邊的成員契約一旦漂移，模型就會照 schema 填一個 Newtonsoft 收不到的欄位；
    /// 二是 MEAI 的完整 JSON Schema（聯集型別、<c>$ref</c>）不是所有 provider 都接受。
    /// </summary>
    [TestFixture]
    public class SchemaBuilderTests
    {
        [TearDown]
        public void ResetForceLegacy()
        {
            RimLLMSchemaBuilder.ForceLegacy = false;
        }

        // -----------------------------------------------------------------
        // 價值證明：正規化後的輸出確實能被 Gemini 接受
        // -----------------------------------------------------------------

        /// <summary>
        /// 與 <c>ProviderSdkIntegrationTests.RawMeaiSchemaIsRejectedByGoogleSchemaFromJson</c> 成對。
        /// 前者證明 MEAI 的原始輸出會讓 <c>Schema.FromJson</c> 回傳 null，本測試證明正規化後可用。
        /// 這兩個測試合起來就是整層正規化存在的理由。
        /// </summary>
        [Test]
        public void GeminiProfileSchemaIsAcceptedByGoogleSchemaFromJson()
        {
            foreach (Type type in SampleTypes())
            {
                string json = RimLLMSchemaBuilder.BuildJson(type, RimLLMSchemaProfile.Gemini);
                Schema schema = Schema.FromJson(json);

                Assert.IsNotNull(schema, type.Name + " 的 Gemini profile schema 應能轉成 Google.GenAI 的 Schema。輸出：" + json);
                Assert.IsNotNull(schema.Type, type.Name + " 的 schema 應有單一 type。");
            }
        }

        /// <summary>
        /// MEAI exporter 必須真的跑得起來，不能靜默降級。
        /// 沒有這道防線的話，任何在 net472 上不存在的 API（例如 .NET Core 才有的
        /// <c>string.Split(char)</c> 多載）都會被 safety net 吞成「測試照樣全綠、但走的是舊實作」。
        /// </summary>
        [Test]
        public void ManagedExporterIsUsedWithoutFallingBackToLegacy()
        {
            foreach (Type type in SampleTypes())
            {
                Assert.IsFalse(
                    RimLLMSchemaBuilder.Build(type, RimLLMSchemaProfile.OpenAI).UsedLegacyFallback,
                    type.Name + " 不應觸發降級 —— MEAI exporter 在此環境應可用。");
            }
        }

        // -----------------------------------------------------------------
        // 契約對齊：STJ 產 schema、Newtonsoft 反序列化
        // -----------------------------------------------------------------

        [Test]
        public void SchemaPropertiesMatchNewtonsoftContract()
        {
            foreach (Type type in SampleTypes())
            {
                var contract = (JsonObjectContract)new DefaultContractResolver().ResolveContract(type);
                var expected = new List<string>();
                foreach (JsonProperty property in contract.Properties)
                {
                    if (!property.Ignored)
                    {
                        expected.Add(property.PropertyName);
                    }
                }

                JObject schema = ParseSchema(type, RimLLMSchemaProfile.OpenAI);
                var actual = new List<string>();
                foreach (KeyValuePair<string, JToken> property in (JObject)schema["properties"])
                {
                    actual.Add(property.Key);
                }

                expected.Sort(StringComparer.Ordinal);
                actual.Sort(StringComparer.Ordinal);
                CollectionAssert.AreEqual(
                    expected,
                    actual,
                    type.Name + " 的 schema 成員集合必須與 Newtonsoft 的反序列化契約一致。");
            }
        }

        [Test]
        public void SampleJsonSatisfiesRequiredMembers()
        {
            foreach (Type type in SampleTypes())
            {
                JObject schema = ParseSchema(type, RimLLMSchemaProfile.OpenAI);
                var sample = JObject.Parse(RimLLMJsonHelper.GetSampleJson(type));

                foreach (JToken requiredName in (JArray)schema["required"])
                {
                    string name = requiredName.Value<string>();
                    Assert.IsTrue(
                        sample.ContainsKey(name),
                        type.Name + " 的提示式範例 JSON 缺少 required 成員 " + name + "，兩條路徑對成員的認知已經分歧。");
                }

                Assert.DoesNotThrow(
                    () => JsonConvert.DeserializeObject(sample.ToString(), type),
                    type.Name + " 的範例 JSON 應能被 Newtonsoft 反序列化。");
            }
        }

        /// <summary>
        /// schema 說某個成員可以是 null，執行期驗證就必須接受 null，兩邊不能互相矛盾。
        ///
        /// 原本 <c>ValidateRequiredMembers</c> 的判斷式是反的：只在「值為 null 且型別**允許** null」
        /// 時才拋，也就是只會對合法可為 null 的成員開火。模型照 schema 回傳 null 會被判定為解析失敗，
        /// 白白走一次靜態修復再加一次付費的 double-repair，最後仍以 RimLLMException 收場。
        /// 這條路徑先前沒有任何測試涵蓋。
        /// </summary>
        [Test]
        public void NullOptionalMemberPassesValidationButNullRequiredMemberDoesNot()
        {
            NullableTestDataStructure parsed = RimLLMManager.DeserializeAndValidate<NullableTestDataStructure>(
                "{\"Name\":\"Randy\",\"OptionalCount\":null}");

            Assert.AreEqual("Randy", parsed.Name);
            Assert.IsNull(parsed.OptionalCount, "Nullable<T> 成員為 null 是合法的，schema 也明確允許。");

            Assert.Throws<InvalidOperationException>(
                () => RimLLMManager.DeserializeAndValidate<NullableTestDataStructure>(
                    "{\"Name\":null,\"OptionalCount\":3}"),
                "非選填成員為 null 仍應被擋下 —— 這才是這道驗證原本要防的情況。");
        }

        // -----------------------------------------------------------------
        // 形狀不變式
        // -----------------------------------------------------------------

        [Test]
        public void NormalizedSchemaHasNoCompositeOrReferenceKeywords()
        {
            string[] forbidden = { "$ref", "$defs", "$schema", "$id", "allOf", "anyOf", "oneOf" };

            foreach (Type type in SampleTypes())
            {
                foreach (RimLLMSchemaProfile profile in AllProfiles())
                {
                    JObject schema = ParseSchema(type, profile);
                    foreach (JObject node in EnumerateNodes(schema))
                    {
                        foreach (string keyword in forbidden)
                        {
                            Assert.IsNull(
                                node[keyword],
                                type.Name + " / " + profile + " 的 schema 不應含 " + keyword + "。");
                        }
                    }
                }
            }
        }

        [Test]
        public void KeywordWhitelistIsEnforced()
        {
            string[] allowed = { "type", "enum", "properties", "required", "items", "additionalProperties", "description", "nullable" };

            foreach (Type type in SampleTypes())
            {
                foreach (RimLLMSchemaProfile profile in AllProfiles())
                {
                    JObject schema = ParseSchema(type, profile);
                    foreach (JObject node in EnumerateNodes(schema))
                    {
                        foreach (KeyValuePair<string, JToken> member in node)
                        {
                            CollectionAssert.Contains(
                                allowed,
                                member.Key,
                                type.Name + " / " + profile + " 的 schema 出現白名單外的關鍵字 " + member.Key + "。");
                        }
                    }
                }
            }
        }

        [Test]
        public void GeminiProfileHasNoTypeUnions()
        {
            foreach (Type type in SampleTypes())
            {
                foreach (JObject node in EnumerateNodes(ParseSchema(type, RimLLMSchemaProfile.Gemini)))
                {
                    Assert.AreEqual(
                        JTokenType.String,
                        node["type"].Type,
                        type.Name + " 的 Gemini profile 每個 type 都必須是單一字串（Schema.Type 是單一列舉值）。");
                }
            }
        }

        [Test]
        public void OpenAiProfileUsesUnionsInsteadOfNullableKeyword()
        {
            foreach (Type type in SampleTypes())
            {
                foreach (JObject node in EnumerateNodes(ParseSchema(type, RimLLMSchemaProfile.OpenAI)))
                {
                    Assert.IsNull(node["nullable"], type.Name + " 的 OpenAI profile 不應使用 OpenAPI 的 nullable 關鍵字。");
                }
            }
        }

        /// <summary>
        /// OpenAI 的 strict structured output 要求 <c>required</c> 涵蓋所有 property，
        /// 選填只能靠聯集型別表達 —— 這正是舊實作把 <c>Nullable&lt;T&gt;</c> 排除在 required 之外時
        /// 會在 OpenAI 端被 400 的原因。兩個 profile 統一採用同一套 required 語意。
        /// </summary>
        [Test]
        public void EveryPropertyIsRequiredInBothProfiles()
        {
            foreach (Type type in SampleTypes())
            {
                foreach (RimLLMSchemaProfile profile in AllProfiles())
                {
                    foreach (JObject node in EnumerateNodes(ParseSchema(type, profile)))
                    {
                        var properties = node["properties"] as JObject;
                        if (properties == null) continue;

                        var required = new List<string>();
                        foreach (JToken name in (JArray)node["required"])
                        {
                            required.Add(name.Value<string>());
                        }

                        var declared = new List<string>();
                        foreach (KeyValuePair<string, JToken> property in properties)
                        {
                            declared.Add(property.Key);
                        }

                        required.Sort(StringComparer.Ordinal);
                        declared.Sort(StringComparer.Ordinal);
                        CollectionAssert.AreEqual(
                            declared,
                            required,
                            type.Name + " / " + profile + " 的 required 必須與 properties 完全一致。");
                    }
                }
            }
        }

        [Test]
        public void NullableMemberIsOptionalInProfileSpecificShape()
        {
            JObject openAi = ParseSchema(typeof(NullableTestDataStructure), RimLLMSchemaProfile.OpenAI);
            JToken openAiType = openAi["properties"]["OptionalCount"]["type"];
            Assert.AreEqual(JTokenType.Array, openAiType.Type, "OpenAI profile 的 int? 應寫成聯集型別。");
            CollectionAssert.AreEquivalent(new[] { "integer", "null" }, openAiType.ToObject<string[]>());

            JObject gemini = ParseSchema(typeof(NullableTestDataStructure), RimLLMSchemaProfile.Gemini);
            JToken geminiMember = gemini["properties"]["OptionalCount"];
            Assert.AreEqual("integer", geminiMember["type"].Value<string>(), "Gemini profile 的 int? 應維持單一 type。");
            Assert.IsTrue(geminiMember["nullable"].Value<bool>(), "Gemini profile 的 int? 應以 nullable 關鍵字表達選填。");

            // 專案未啟用 NRT，exporter 會把所有參考型別也寫成可為 null 的聯集。
            // 只有 Nullable<T> 才算選填 —— 與舊實作的 IsOptionalMember 判定一致。
            Assert.AreEqual(
                JTokenType.String,
                openAi["properties"]["Name"]["type"].Type,
                "參考型別成員不應被誤判為選填。");
        }

        // -----------------------------------------------------------------
        // 遞迴、深度與 $ref 去重
        // -----------------------------------------------------------------

        /// <summary>
        /// MEAI 的 <c>$ref</c> 不只用於循環，也用於去重：<c>List&lt;string&gt;</c> 第二次出現時會變成
        /// <c>{"$ref":"#/properties/Skills"}</c>。一律截斷 <c>$ref</c> 會誤刪這種正常成員，
        /// 所以正規化必須真的解析 JSON pointer，只在指向祖先時才視為循環。
        /// </summary>
        [Test]
        public void RecursiveMemberIsTruncatedButDeduplicatedMemberSurvives()
        {
            JObject schema = ParseSchema(typeof(ComplexTestDataStructure), RimLLMSchemaProfile.OpenAI);

            // 去重的 $ref 必須完整展開成原本的 schema，不能只剩空殼 —— 這是「一律截斷 $ref」會踩到的坑。
            var skills = (JObject)schema["properties"]["Skills"];
            Assert.IsNotNull(skills, "Skills 是 $ref 去重而非循環，不得被截斷。");
            Assert.AreEqual("array", skills["type"].Value<string>());
            Assert.AreEqual("string", skills["items"]["type"].Value<string>());

            // 循環在 CLR 型別層截斷，而非等到 JSON pointer 重現。exporter 會先把遞迴成員完整
            // 展開一輪、其中才出現指回祖先的 $ref，只靠 pointer 偵測會多送一整層
            // （實測 789 → 3119 字元，而那是每次請求都要付的 prompt token）。
            var nested = (JObject)schema["properties"]["Nested"];
            Assert.IsNotNull(nested, "非循環的巢狀成員應正常展開。");
            Assert.AreEqual("number", nested["properties"]["Weight"]["type"].Value<string>());

            Assert.IsNull(nested["properties"]["SelfRef"], "指回祖先型別的成員應被截斷。");
            CollectionAssert.DoesNotContain(RequiredNames(nested), "SelfRef", "被截斷的成員不得留在 required。");
        }

        /// <summary>
        /// 遞迴型別的 schema 體積是每次結構化請求都要付的 prompt token，
        /// 因此循環截斷點退步（例如改回只靠 JSON pointer 偵測）必須被擋下來。
        /// </summary>
        [Test]
        public void RecursiveSchemaStaysCompact()
        {
            int managed = RimLLMSchemaBuilder.BuildJson(typeof(ComplexTestDataStructure), RimLLMSchemaProfile.OpenAI).Length;

            RimLLMSchemaBuilder.ForceLegacy = true;
            int legacy = RimLLMSchemaBuilder.BuildJson(typeof(ComplexTestDataStructure), RimLLMSchemaProfile.OpenAI).Length;

            Assert.Less(
                managed,
                legacy * 2,
                "遞迴型別的 schema 不應比舊實作大上一個量級。managed=" + managed + " legacy=" + legacy);
        }

        /// <summary>
        /// OpenAI 的 strict structured output 明訂最多 5 層巢狀，超過會被服務端拒絕並靜默降級成
        /// 提示式 JSON。Gemini 沒有這條限制，不該被連累，所以深度上限依方言而異。
        /// </summary>
        [Test]
        public void DeepNestingIsTruncatedPerProfileDepthLimit()
        {
            int openAiDepth = MeasureNextChainDepth(ParseSchema(typeof(DeepChainLevel0), RimLLMSchemaProfile.OpenAI));
            int geminiDepth = MeasureNextChainDepth(ParseSchema(typeof(DeepChainLevel0), RimLLMSchemaProfile.Gemini));

            Assert.LessOrEqual(
                openAiDepth,
                RimLLMSchemaBuilder.OpenAIMaxSchemaDepth,
                "OpenAI 方言的巢狀層數不得超過服務端上限。實際：" + openAiDepth);
            Assert.LessOrEqual(
                geminiDepth,
                RimLLMSchemaBuilder.MaxSchemaDepth,
                "Gemini 方言仍受整體深度上限保護。實際：" + geminiDepth);
            Assert.Greater(
                geminiDepth,
                openAiDepth,
                "Gemini 沒有 5 層限制，不應被 OpenAI 的上限連累。");
        }

        private static int MeasureNextChainDepth(JObject schema)
        {
            int depth = 0;
            JObject current = schema;
            while (true)
            {
                var next = current["properties"]["Next"] as JObject;
                if (next == null) break;

                depth++;
                current = next;
                Assert.Less(depth, 20, "深度截斷失效，schema 無限展開。");
            }

            CollectionAssert.DoesNotContain(RequiredNames(current), "Next", "被截斷的成員不得留在 required。");
            return depth;
        }

        // -----------------------------------------------------------------
        // 型別對照
        // -----------------------------------------------------------------

        [Test]
        [Explicit("診斷用：比較新舊管線的 schema 體積（每次結構化請求都要送出，直接反映 token 成本）")]
        public void DumpSchemaSizeComparison()
        {
            foreach (Type type in SampleTypes())
            {
                RimLLMSchemaBuilder.ForceLegacy = false;
                int managed = RimLLMSchemaBuilder.BuildJson(type, RimLLMSchemaProfile.OpenAI).Length;
                RimLLMSchemaBuilder.ForceLegacy = true;
                int legacy = RimLLMSchemaBuilder.BuildJson(type, RimLLMSchemaProfile.OpenAI).Length;
                TestContext.WriteLine(type.Name + ": managed=" + managed + " legacy=" + legacy);
            }
        }

        [Test]
        [Explicit("診斷用：印出 Stage A 的原始輸出")]
        public void DumpRawExporterOutput()
        {
            foreach (Type type in SampleTypes())
            {
                TestContext.WriteLine(type.Name + " => " + RimLLMSchemaBuilder.ExportRaw(type).ToString(Formatting.None));
            }
        }

        [Test]
        public void EnumMemberBecomesStringEnum()
        {
            JObject schema = ParseSchema(typeof(EnumTestDataStructure), RimLLMSchemaProfile.OpenAI);
            var kind = (JObject)schema["properties"]["Kind"];

            Assert.AreEqual("string", kind["type"].Value<string>(), "列舉應以字串名稱表達，Newtonsoft 反序列化接受名稱。");
            CollectionAssert.AreEquivalent(
                new[] { "Alpha", "Beta" },
                kind["enum"].ToObject<string[]>());
        }

        [Test]
        public void DescriptionAttributeFlowsIntoSchema()
        {
            JObject schema = ParseSchema(typeof(DescribedTestDataStructure), RimLLMSchemaProfile.OpenAI);

            Assert.AreEqual(
                "殖民者的名字",
                schema["properties"]["Name"]["description"].Value<string>(),
                "成員層級的 [Description] 應傳進 schema —— 這是舊反射實作沒有的能力。");

            Assert.AreEqual(
                "一筆殖民者紀錄",
                schema["description"].Value<string>(),
                "類別層級的 [Description] 也應傳進 schema。");
        }

        [Test]
        public void DictionaryBecomesOpenMapAndDisablesStrict()
        {
            RimLLMSchemaResult result = RimLLMSchemaBuilder.Build(typeof(ComplexTestDataStructure), RimLLMSchemaProfile.OpenAI);
            var schema = JObject.Parse(result.Json);
            var mapping = (JObject)schema["properties"]["Mapping"];

            Assert.AreEqual("object", mapping["type"].Value<string>());
            Assert.AreEqual("integer", mapping["additionalProperties"]["type"].Value<string>());

            Assert.IsTrue(result.ContainsOpenEndedMap, "含 Dictionary 的型別應被判定為開放式 map。");
            Assert.IsFalse(result.StrictCompatible, "開放式 map 不相容於 OpenAI 的 strict structured output。");

            RimLLMSchemaResult plain = RimLLMSchemaBuilder.Build(typeof(NullableTestDataStructure), RimLLMSchemaProfile.OpenAI);
            Assert.IsFalse(plain.ContainsOpenEndedMap);
            Assert.IsTrue(plain.StrictCompatible);
        }

        [Test]
        public void ContainsOpenEndedMapMatchesLegacyReflection()
        {
            foreach (Type type in SampleTypes())
            {
#pragma warning disable CS0618
                bool legacy = RimLLMJsonHelper.ContainsOpenEndedMap(type);
#pragma warning restore CS0618
                Assert.AreEqual(
                    legacy,
                    RimLLMSchemaBuilder.ContainsOpenEndedMap(type),
                    type.Name + " 的開放式 map 判定在新舊兩條路徑上應一致。");
            }
        }

        // -----------------------------------------------------------------
        // 降級路徑與快取
        // -----------------------------------------------------------------

        [Test]
        public void LegacyFallbackProducesUsableSchema()
        {
            RimLLMSchemaBuilder.ForceLegacy = true;

            foreach (Type type in SampleTypes())
            {
                RimLLMSchemaResult result = RimLLMSchemaBuilder.Build(type, RimLLMSchemaProfile.Gemini);
                var schema = JObject.Parse(result.Json);

                Assert.AreEqual("object", schema["type"].Value<string>(), type.Name + " 降級後仍應產生可用 schema。");
                Assert.IsTrue(result.UsedLegacyFallback);
                Assert.IsFalse(
                    result.StrictCompatible,
                    "降級產物的 required 語意是舊的（Nullable 不列入），不得再宣告相容於 strict。");
                Assert.IsNotNull(Schema.FromJson(result.Json), type.Name + " 降級產物仍應能被 Gemini 接受。");
            }
        }

        [Test]
        public void ResultCacheReturnsSameImmutableInstance()
        {
            RimLLMSchemaResult first = RimLLMSchemaBuilder.Build(typeof(TestDataStructure), RimLLMSchemaProfile.OpenAI);
            RimLLMSchemaResult second = RimLLMSchemaBuilder.Build(typeof(TestDataStructure), RimLLMSchemaProfile.OpenAI);
            Assert.AreSame(first, second, "結果不可變，快取應直接共用同一個實例。");

            RimLLMSchemaResult gemini = RimLLMSchemaBuilder.Build(typeof(TestDataStructure), RimLLMSchemaProfile.Gemini);
            Assert.AreNotSame(first, gemini, "不同 profile 必須是不同的快取項。");
        }

        [Test]
        public void ResolveProfileMapsGeminiById()
        {
            Assert.AreEqual(RimLLMSchemaProfile.Gemini, RimLLMSchemaBuilder.ResolveProfile(ProviderIds.Gemini));
            Assert.AreEqual(RimLLMSchemaProfile.OpenAI, RimLLMSchemaBuilder.ResolveProfile(ProviderIds.OpenAI));
            Assert.AreEqual(RimLLMSchemaProfile.OpenAI, RimLLMSchemaBuilder.ResolveProfile(null));
        }

        // -----------------------------------------------------------------
        // 輔助
        // -----------------------------------------------------------------

        private static IEnumerable<Type> SampleTypes()
        {
            yield return typeof(TestDataStructure);
            yield return typeof(NullableTestDataStructure);
            yield return typeof(ComplexTestDataStructure);
            yield return typeof(EnumTestDataStructure);
        }

        private static IEnumerable<RimLLMSchemaProfile> AllProfiles()
        {
            yield return RimLLMSchemaProfile.OpenAI;
            yield return RimLLMSchemaProfile.Gemini;
        }

        private static JObject ParseSchema(Type type, RimLLMSchemaProfile profile)
        {
            return JObject.Parse(RimLLMSchemaBuilder.BuildJson(type, profile));
        }

        private static List<string> PropertyNames(JObject node)
        {
            var names = new List<string>();
            var properties = node["properties"] as JObject;
            if (properties != null)
            {
                foreach (KeyValuePair<string, JToken> property in properties)
                {
                    names.Add(property.Key);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static List<string> RequiredNames(JObject node)
        {
            var names = new List<string>();
            var required = node["required"] as JArray;
            if (required != null)
            {
                foreach (JToken name in required)
                {
                    names.Add(name.Value<string>());
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>深度優先走訪 schema 中的每個節點（含根節點）。</summary>
        private static IEnumerable<JObject> EnumerateNodes(JObject node)
        {
            if (node == null) yield break;

            yield return node;

            foreach (JObject child in EnumerateNodes(node["items"] as JObject))
            {
                yield return child;
            }

            foreach (JObject child in EnumerateNodes(node["additionalProperties"] as JObject))
            {
                yield return child;
            }

            var properties = node["properties"] as JObject;
            if (properties == null) yield break;

            foreach (KeyValuePair<string, JToken> property in properties)
            {
                foreach (JObject child in EnumerateNodes(property.Value as JObject))
                {
                    yield return child;
                }
            }
        }

        public enum TestKind
        {
            Alpha,
            Beta
        }

        public class EnumTestDataStructure
        {
            public TestKind Kind { get; set; }
            public string Label { get; set; }
        }

        [System.ComponentModel.Description("一筆殖民者紀錄")]
        public class DescribedTestDataStructure
        {
            [System.ComponentModel.Description("殖民者的名字")]
            public string Name { get; set; }
        }

        public class DeepChainLevel0 { public DeepChainLevel1 Next { get; set; } }
        public class DeepChainLevel1 { public DeepChainLevel2 Next { get; set; } }
        public class DeepChainLevel2 { public DeepChainLevel3 Next { get; set; } }
        public class DeepChainLevel3 { public DeepChainLevel4 Next { get; set; } }
        public class DeepChainLevel4 { public DeepChainLevel5 Next { get; set; } }
        public class DeepChainLevel5 { public DeepChainLevel6 Next { get; set; } }
        public class DeepChainLevel6 { public DeepChainLevel7 Next { get; set; } }
        public class DeepChainLevel7 { public DeepChainLevel8 Next { get; set; } }
        public class DeepChainLevel8 { public DeepChainLevel9 Next { get; set; } }
        public class DeepChainLevel9 { public DeepChainLevel10 Next { get; set; } }
        public class DeepChainLevel10 { public int Value { get; set; } }
    }
}
