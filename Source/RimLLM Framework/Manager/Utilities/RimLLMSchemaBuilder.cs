using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// 送往 provider 的 JSON Schema 方言。
    /// 兩家對「可為 null 的成員」要求不同的寫法，其餘形狀相同。
    /// </summary>
    public enum RimLLMSchemaProfile
    {
        /// <summary>OpenAI 家族：選填成員以 <c>"type": ["integer","null"]</c> 聯集表達。</summary>
        OpenAI,

        /// <summary>Gemini：<c>type</c> 只能是單一值，選填成員以 <c>"nullable": true</c> 表達。</summary>
        Gemini
    }

    /// <summary>
    /// 一次 schema 產生的完整結果。不可變，因此可直接由快取共用而不需複製。
    /// </summary>
    public sealed class RimLLMSchemaResult
    {
        internal RimLLMSchemaResult(string json, bool containsOpenEndedMap, bool strictCompatible, bool usedLegacyFallback)
        {
            Json = json;
            ContainsOpenEndedMap = containsOpenEndedMap;
            StrictCompatible = strictCompatible;
            UsedLegacyFallback = usedLegacyFallback;
        }

        /// <summary>已套用目標 provider 方言的 schema JSON。</summary>
        public string Json { get; private set; }

        /// <summary>schema 中是否含開放式 map（由 Dictionary 產生的 <c>additionalProperties</c> 物件）。</summary>
        public bool ContainsOpenEndedMap { get; private set; }

        /// <summary>是否可安全地以 OpenAI strict structured output 送出。</summary>
        public bool StrictCompatible { get; private set; }

        /// <summary>是否因 MEAI exporter 不可用而降級走舊反射實作。</summary>
        public bool UsedLegacyFallback { get; private set; }
    }

    /// <summary>
    /// 結構化輸出的 JSON Schema 產生器。
    ///
    /// 管線分三段：
    /// <list type="number">
    /// <item>Stage A：<c>System.Text.Json.Schema.JsonSchemaExporter</c> 產生完整的 JSON Schema。</item>
    /// <item>Stage B：正規化成所有 provider 都吃得下的受限子集 —— 展開 <c>$ref</c>、截斷循環與過深巢狀、
    /// 把可為 null 的聯集收斂成單一 <c>type</c>、補上 <c>[Description]</c>、只保留關鍵字白名單。</item>
    /// <item>Stage C：套用目標 provider 的方言（選填成員寫成聯集或 <c>nullable</c>）。</item>
    /// </list>
    ///
    /// 為什麼不能直接送 exporter 的原始輸出：它把可為 null 的成員寫成 <c>"type": ["string","null"]</c>，
    /// 而 <c>Google.GenAI.Types.Schema.Type</c> 是單一列舉值，<c>Schema.FromJson</c> 會靜默回傳 null。
    /// 見 <c>ProviderSdkIntegrationTests.RawMeaiSchemaIsRejectedByGoogleSchemaFromJson</c>。
    /// </summary>
    public static class RimLLMSchemaBuilder
    {
        /// <summary>
        /// Schema 遞迴的最大深度。超過此深度的巢狀成員會被略過，避免病態型別造成堆疊耗盡。
        /// </summary>
        public const int MaxSchemaDepth = 8;

        /// <summary>
        /// OpenAI 的 strict structured output 明訂 schema 最多 5 層巢狀（另有全域 100 個 property 的上限）。
        /// 超過就會被服務端拒絕，接著被 <c>IsNativeSchemaRejected</c> 靜默降級成提示式 JSON ——
        /// 與其送出已知會被拒的 schema，不如在此先截斷。Gemini 沒有這條限制，因此不受影響。
        /// </summary>
        public const int OpenAIMaxSchemaDepth = 5;

        private static int ResolveMaxDepth(RimLLMSchemaProfile profile)
        {
            return profile == RimLLMSchemaProfile.Gemini ? MaxSchemaDepth : OpenAIMaxSchemaDepth;
        }

        /// <summary>Stage B 用來標記「這個成員是 <c>Nullable&lt;T&gt;</c>」的私有關鍵字，Stage C 會翻譯並移除它。</summary>
        private const string OptionalMarker = "x-rimllm-optional";

        /// <summary>Stage C 之前允許存在的關鍵字。其餘一律剝除。</summary>
        private static readonly HashSet<string> AllowedKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "type", "enum", "properties", "required", "items", "additionalProperties", "description", OptionalMarker
        };

        private static readonly ConcurrentDictionary<string, JObject> CanonicalCache = new ConcurrentDictionary<string, JObject>();
        private static readonly ConcurrentDictionary<string, RimLLMSchemaResult> ResultCache = new ConcurrentDictionary<string, RimLLMSchemaResult>();

        private static readonly object OptionsLock = new object();
        private static JsonSerializerOptions _serializerOptions;

        /// <summary>
        /// 強制略過 MEAI exporter、一律走舊的反射實作。
        /// RimWorld 的 Mono 執行環境無法保證 <c>JsonSchemaExporter</c> 可用（它依賴 Reflection.Emit
        /// 產生 getter/setter），因此保留這個逃生門；exporter 第一次拋例外時也會自動打開。
        /// </summary>
        public static bool ForceLegacy
        {
            get { return _forceLegacy; }
            set
            {
                if (_forceLegacy == value) return;

                _forceLegacy = value;
                // 切換產生方式會讓既有快取失效 —— 已快取的 canonical 與結果是用另一條路徑產生的。
                CanonicalCache.Clear();
                ResultCache.Clear();
            }
        }

        private static bool _forceLegacy;

        /// <summary>
        /// MEAI exporter 最近一次失敗的完整型別與訊息，未失敗過則為 null。
        /// 降級是靜默的（功能仍可運作），所以必須把原因留下來供診斷讀取 ——
        /// 只寫進日誌的話，玩家回報時往往已經被其他訊息沖掉。
        /// </summary>
        public static string LastExporterFailure { get; private set; }

        /// <summary>
        /// 產生指定型別在目標 provider 方言下的 schema。結果不可變，可直接共用。
        /// </summary>
        public static RimLLMSchemaResult Build(Type type, RimLLMSchemaProfile profile)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            string cacheKey = type.AssemblyQualifiedName + "|" + (int)profile;
            RimLLMSchemaResult cached;
            if (ResultCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            bool usedLegacyFallback;
            JObject canonical = GetCanonical(type, ResolveMaxDepth(profile), out usedLegacyFallback);
            JObject shaped = ApplyProfile(canonical, profile);

            bool containsOpenEndedMap = HasOpenEndedMap(shaped);
            bool strictCompatible = !containsOpenEndedMap && !usedLegacyFallback && profile != RimLLMSchemaProfile.Gemini;

            var result = new RimLLMSchemaResult(shaped.ToString(), containsOpenEndedMap, strictCompatible, usedLegacyFallback);
            ResultCache[cacheKey] = result;
            return result;
        }

        /// <summary>產生 schema 的 JSON 字串。</summary>
        public static string BuildJson(Type type, RimLLMSchemaProfile profile)
        {
            return Build(type, profile).Json;
        }

        /// <summary>
        /// 判斷型別產生的 schema 是否含開放式 map。OpenAI 的 strict structured output 不接受這種形狀。
        /// 由實際產生的 schema 推導，而非型別樹反射 —— 被深度截斷掉的 Dictionary 不該再關閉 strict。
        /// </summary>
        public static bool ContainsOpenEndedMap(Type type)
        {
            if (type == null) return false;
            return Build(type, RimLLMSchemaProfile.OpenAI).ContainsOpenEndedMap;
        }

        /// <summary>
        /// 由 provider id 推導方言。只在拿不到 provider 實例（因此讀不到
        /// <c>LLMProviderCapabilities.PreferredSchemaProfile</c>）時使用。
        /// </summary>
        public static RimLLMSchemaProfile ResolveProfile(string providerId)
        {
            return string.Equals(providerId, ProviderIds.Gemini, StringComparison.OrdinalIgnoreCase)
                ? RimLLMSchemaProfile.Gemini
                : RimLLMSchemaProfile.OpenAI;
        }

        // ---------------------------------------------------------------------
        // Stage A + B：canonical schema
        // ---------------------------------------------------------------------

        private static JObject GetCanonical(Type type, int maxDepth, out bool usedLegacyFallback)
        {
            // 深度上限依方言而異，因此必須進 cache key —— 否則 Gemini 會拿到被 OpenAI 上限截斷過的樹。
            string cacheKey = type.AssemblyQualifiedName + "|d" + maxDepth;
            JObject cached;
            if (CanonicalCache.TryGetValue(cacheKey, out cached))
            {
                usedLegacyFallback = cached[LegacyMarker] != null;
                return (JObject)cached.DeepClone();
            }

            JObject canonical = BuildCanonical(type, maxDepth, out usedLegacyFallback);
            if (usedLegacyFallback)
            {
                canonical[LegacyMarker] = true;
            }

            CanonicalCache[cacheKey] = canonical;
            return (JObject)canonical.DeepClone();
        }

        /// <summary>快取內用來記住「這份 canonical 是降級產物」的私有關鍵字，Stage C 會移除。</summary>
        private const string LegacyMarker = "x-rimllm-legacy";

        private static JObject BuildCanonical(Type type, int maxDepth, out bool usedLegacyFallback)
        {
            if (!ForceLegacy)
            {
                try
                {
                    JObject raw = ExportRaw(type);
                    JObject normalized = Normalize(raw, GetTypeInfo(type), new NormalizeContext(raw, maxDepth), 0);
                    usedLegacyFallback = false;
                    return normalized ?? CreateEmptyObjectSchema();
                }
                catch (Exception exception)
                {
                    // 一次失敗即永久降級，避免每次請求都吃例外成本。
                    LastExporterFailure = DescribeExporterFailure(exception);
                    ForceLegacy = true;
                    RimLLMLog.Warning("MEAI schema exporter 不可用，永久降級至舊反射實作：" + LastExporterFailure);
                }
            }

            usedLegacyFallback = true;
            return BuildLegacyCanonical(type, maxDepth);
        }

        /// <summary>
        /// 展平例外鏈。exporter 的失敗常被包成 <see cref="TypeInitializationException"/> 或
        /// <see cref="System.Reflection.TargetInvocationException"/>，只看最外層那一句幾乎沒有診斷價值。
        /// </summary>
        private static string DescribeExporterFailure(Exception exception)
        {
            var description = new System.Text.StringBuilder();
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (description.Length > 0) description.Append(" ---> ");
                description.Append(current.GetType().FullName).Append(": ").Append(current.Message);
            }

            return description.ToString();
        }

        /// <summary>
        /// Stage A：由 System.Text.Json 的 <see cref="JsonSchemaExporter"/> 產生完整 JSON Schema。
        ///
        /// 刻意直接呼叫 exporter，而不是 MEAI 的 <c>AIJsonUtilities.CreateJsonSchema</c> 包裝 ——
        /// 後者出貨的是 net462 資產，其中含 <c>System.ComponentModel.DataAnnotations</c> 參考
        /// （用來讀 <c>[EmailAddress]</c> / <c>[Range]</c> 之類的驗證屬性豐富 schema）。
        /// RimWorld 的 Mono BCL 沒有那個組件，實機上會拋：
        ///   <c>TypeLoadException: Could not resolve type ... 'EmailAddressAttribute' in assembly
        ///   'System.ComponentModel.DataAnnotations, Version=4.0.0.0'</c>
        /// 而整份 schema 產生就靜默降級。<c>System.Text.Json</c> 本身完全沒有該參考，
        /// 且 exporter 正是 MEAI 內部使用的同一個引擎，所以直呼它既能繞開地雷又不損失能力。
        /// MEAI 唯一多做而我們仍需要的是 <c>[Description]</c>，由 Stage B 自行讀取補上。
        /// </summary>
        internal static JObject ExportRaw(Type type)
        {
            JsonNode node = JsonSchemaExporter.GetJsonSchemaAsNode(EnsureSerializerOptions(), type);
            return JObject.Parse(node.ToJsonString());
        }

        /// <summary>
        /// 正規化遞迴過程中的路徑狀態。
        /// 兩份路徑刻意都用 <see cref="List{T}"/> 而非 <c>Stack&lt;T&gt;</c>：
        /// RimWorld Mono 無法載入後者（同 <c>RimLLMJsonHelper.RepairJson</c> 的理由）。
        /// </summary>
        private sealed class NormalizeContext
        {
            public NormalizeContext(JObject rawRoot, int maxDepth)
            {
                RawRoot = rawRoot;
                MaxDepth = maxDepth;
            }

            /// <summary>exporter 原始輸出的根節點，<c>$ref</c> 的 JSON pointer 以它為基準。</summary>
            public JObject RawRoot { get; private set; }

            /// <summary>本次產生適用的巢狀深度上限，依目標方言而異。</summary>
            public int MaxDepth { get; private set; }

            /// <summary>目前展開路徑上已解析過的 pointer。</summary>
            public List<string> PointerPath { get; } = new List<string>();

            /// <summary>目前展開路徑上的 CLR 物件型別，用來在型別層截斷循環。</summary>
            public List<Type> TypePath { get; } = new List<Type>();
        }

        /// <summary>
        /// 正規化遞迴核心。
        ///
        /// 回傳 <see langword="null"/> 的語意是「這個節點無法表達，父層必須刪掉對應成員並從
        /// <c>required</c> 移除」—— 與舊實作把循環成員截斷為 null 的行為一致。
        /// </summary>
        /// <param name="node">exporter 原始輸出中的節點。</param>
        /// <param name="typeInfo">該節點對應的 CLR 型別資訊，可能為 null（此時退化成純 JSON 正規化）。</param>
        private static JObject Normalize(JObject node, JsonTypeInfo typeInfo, NormalizeContext context, int depth)
        {
            if (node == null || depth > context.MaxDepth)
            {
                return null;
            }

            // exporter 的 $ref 是指向樹內既有節點的 JSON pointer，而且不只用於循環，也用於去重。
            // 例如 List<string> 第二次出現時會變成 {"$ref":"#/properties/Skills"} —— 一律截斷會誤刪正常成員，
            // 所以必須真的解析 pointer，只在它指向目前展開路徑上的祖先時才視為循環。
            JToken refToken = node["$ref"];
            if (refToken != null && refToken.Type == JTokenType.String)
            {
                string pointer = refToken.Value<string>();
                if (context.PointerPath.Contains(pointer))
                {
                    return null;
                }

                JObject target = ResolvePointer(context.RawRoot, pointer);
                if (target == null)
                {
                    return null;
                }

                context.PointerPath.Add(pointer);
                try
                {
                    return Normalize(target, typeInfo, context, depth);
                }
                finally
                {
                    context.PointerPath.RemoveAt(context.PointerPath.Count - 1);
                }
            }

            JObject collapsed = CollapseCompositeKeywords(node);
            if (collapsed == null)
            {
                return null;
            }

            string typeName = ExtractTypeName(collapsed);
            if (typeName == null)
            {
                return null;
            }

            var result = new JObject();
            result["type"] = typeName;

            JToken description = collapsed["description"];
            if (description != null && description.Type == JTokenType.String)
            {
                result["description"] = description.Value<string>();
            }

            if (typeName == "array")
            {
                JObject itemSchema = Normalize(
                    collapsed["items"] as JObject,
                    typeInfo != null ? GetTypeInfo(typeInfo.ElementType) : null,
                    context,
                    depth + 1);

                // 陣列的元素無法表達時，整個陣列成員一併捨棄（與舊實作一致）。
                if (itemSchema == null) return null;
                result["items"] = itemSchema;
                return result;
            }

            if (typeName == "object")
            {
                return NormalizeObject(collapsed, result, typeInfo, context, depth);
            }

            JArray enumValues = collapsed["enum"] as JArray;
            if (enumValues != null)
            {
                result["enum"] = enumValues.DeepClone();
            }

            return result;
        }

        private static JObject NormalizeObject(
            JObject node, JObject result, JsonTypeInfo typeInfo, NormalizeContext context, int depth)
        {
            // Dictionary 會產生開放式 map（additionalProperties 是一份 value schema）；
            // 自訂類別則沒有 additionalProperties，由我們補上 false。
            JObject valueSchema = node["additionalProperties"] as JObject;
            if (valueSchema != null)
            {
                JObject normalizedValue = Normalize(
                    valueSchema,
                    typeInfo != null ? GetTypeInfo(typeInfo.ElementType) : null,
                    context,
                    depth + 1);

                if (normalizedValue != null)
                {
                    result["additionalProperties"] = normalizedValue;
                }

                return result;
            }

            // 循環在 CLR 型別層截斷，而不是等到 JSON pointer 重現才截斷。
            // exporter 會把遞迴成員先完整展開一輪、其中才出現指回祖先的 $ref，
            // 若只靠 pointer 偵測就會多送一整層 —— 實測 ComplexTestDataStructure 的 schema
            // 從 789 字元漲到 3119 字元，而那是每次結構化請求都要付的 prompt token。
            Type clrType = typeInfo != null ? typeInfo.Type : null;
            if (clrType != null)
            {
                if (context.TypePath.Contains(clrType)) return null;
                context.TypePath.Add(clrType);
            }

            try
            {
                ApplyDescription(result, clrType);

                var properties = new JObject();
                var required = new JArray();
                JObject rawProperties = node["properties"] as JObject;

                if (rawProperties != null)
                {
                    Dictionary<string, JsonPropertyInfo> memberLookup = BuildMemberLookup(typeInfo);

                    foreach (KeyValuePair<string, JToken> property in rawProperties)
                    {
                        JsonPropertyInfo memberInfo;
                        memberLookup.TryGetValue(property.Key, out memberInfo);

                        JObject memberSchema = Normalize(
                            property.Value as JObject,
                            memberInfo != null ? GetTypeInfo(memberInfo.PropertyType) : null,
                            context,
                            depth + 1);

                        if (memberSchema == null) continue;

                        ApplyMemberDescription(memberSchema, memberInfo);

                        // 專案未啟用 NRT，所以 exporter 會把所有參考型別都寫成可為 null 的聯集。
                        // 只有 Nullable<T> 才是真正的選填成員 —— 與舊實作的判定一致。
                        if (memberInfo != null && Nullable.GetUnderlyingType(memberInfo.PropertyType) != null)
                        {
                            memberSchema[OptionalMarker] = true;
                        }

                        properties[property.Key] = memberSchema;
                        required.Add(property.Key);
                    }
                }

                result["properties"] = properties;
                result["required"] = required;
                result["additionalProperties"] = false;
                return result;
            }
            finally
            {
                if (clrType != null)
                {
                    context.TypePath.RemoveAt(context.TypePath.Count - 1);
                }
            }
        }

        /// <summary>
        /// 把成員上的 <see cref="DescriptionAttribute"/> 寫進 schema。
        ///
        /// System.Text.Json 的 exporter 沒有 description 的概念 —— 這是 MEAI 包裝層多做的事，
        /// 而那層因為 DataAnnotations 相依在 RimWorld 的 Mono 上無法載入（見 <see cref="ExportRaw"/>）。
        /// 只讀 <c>System.ComponentModel.DescriptionAttribute</c>，它在 mscorlib 旁的 System.dll 內，
        /// 任何 .NET 執行環境都有，不會重蹈覆轍。
        /// </summary>
        private static void ApplyMemberDescription(JObject memberSchema, JsonPropertyInfo memberInfo)
        {
            ApplyDescription(memberSchema, memberInfo?.AttributeProvider as MemberInfo);
        }

        /// <summary>成員層級與類別層級的 <see cref="DescriptionAttribute"/> 共用同一套讀取邏輯。</summary>
        private static void ApplyDescription(JObject schema, MemberInfo attributeSource)
        {
            if (schema == null || attributeSource == null) return;
            if (schema["description"] != null) return;

            object[] attributes = attributeSource.GetCustomAttributes(typeof(DescriptionAttribute), true);
            if (attributes.Length == 0) return;

            string description = ((DescriptionAttribute)attributes[0]).Description;
            if (!string.IsNullOrEmpty(description))
            {
                schema["description"] = description;
            }
        }

        private static Dictionary<string, JsonPropertyInfo> BuildMemberLookup(JsonTypeInfo typeInfo)
        {
            var lookup = new Dictionary<string, JsonPropertyInfo>(StringComparer.Ordinal);
            if (typeInfo == null) return lookup;

            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                lookup[property.Name] = property;
            }

            return lookup;
        }

        /// <summary>
        /// 攤平 <c>allOf</c> / <c>anyOf</c> / <c>oneOf</c>。
        /// 只處理兩種可還原成單一 schema 的情形：單元素的 allOf，以及「某型別或 null」的兩元素聯集。
        /// 其餘（真正的多型）回傳 null，讓父層捨棄該成員 —— 寧可少一個欄位，也不要送出 provider 不吃的形狀。
        /// </summary>
        private static JObject CollapseCompositeKeywords(JObject node)
        {
            JArray composite = (node["allOf"] as JArray) ?? (node["anyOf"] as JArray) ?? (node["oneOf"] as JArray);
            if (composite == null)
            {
                return node;
            }

            JObject candidate = null;
            foreach (JToken branch in composite)
            {
                var branchObject = branch as JObject;
                if (branchObject == null) return null;

                // "或 null" 的那一支不帶資訊，略過。
                if (IsNullOnlySchema(branchObject)) continue;

                if (candidate != null) return null;
                candidate = branchObject;
            }

            if (candidate == null) return null;

            // 外層若帶了 description 之類的兄弟關鍵字，合併進被選中的分支。
            var merged = (JObject)candidate.DeepClone();
            foreach (KeyValuePair<string, JToken> sibling in node)
            {
                if (sibling.Key == "allOf" || sibling.Key == "anyOf" || sibling.Key == "oneOf") continue;
                if (merged[sibling.Key] == null)
                {
                    merged[sibling.Key] = sibling.Value.DeepClone();
                }
            }

            return merged;
        }

        private static bool IsNullOnlySchema(JObject node)
        {
            JToken type = node["type"];
            return type != null && type.Type == JTokenType.String && type.Value<string>() == "null";
        }

        /// <summary>
        /// 取出單一 type 名稱。可為 null 的聯集在此收斂 —— 選填語意改由 <see cref="OptionalMarker"/> 攜帶，
        /// 由 Stage C 依 provider 方言還原。
        /// </summary>
        private static string ExtractTypeName(JObject node)
        {
            JToken typeToken = node["type"];

            // exporter 對列舉只輸出 {"enum":[...]}，不帶 type（補上 type 是 MEAI 包裝層做的事，
            // 而那層在 RimWorld 的 Mono 上無法載入）。沒有 type 的節點會被視為無法表達而丟棄，
            // 所以在此由列舉值反推 —— 否則所有列舉成員都會從 schema 中消失。
            if (typeToken == null) return InferTypeFromEnum(node["enum"] as JArray);

            if (typeToken.Type == JTokenType.String)
            {
                string single = typeToken.Value<string>();
                return single == "null" ? null : single;
            }

            var candidates = typeToken as JArray;
            if (candidates == null) return null;

            foreach (JToken candidate in candidates)
            {
                if (candidate.Type != JTokenType.String) continue;
                string name = candidate.Value<string>();
                if (name != "null") return name;
            }

            return null;
        }

        /// <summary>
        /// 由列舉值反推 <c>type</c>。<c>JsonStringEnumConverter</c> 會產出字串值，
        /// 未套用該轉換器的列舉則是整數值。
        /// </summary>
        private static string InferTypeFromEnum(JArray enumValues)
        {
            if (enumValues == null || enumValues.Count == 0) return null;

            foreach (JToken value in enumValues)
            {
                if (value.Type == JTokenType.String) return "string";
                if (value.Type == JTokenType.Integer) return "integer";
            }

            return null;
        }

        /// <summary>
        /// 解析 exporter 產生的 JSON pointer（形如 <c>#/properties/Nested/properties/Child</c>）。
        /// MEAI 不使用 <c>$defs</c>，pointer 一律指向輸出樹內的既有路徑。
        /// </summary>
        private static JObject ResolvePointer(JObject rawRoot, string pointer)
        {
            if (string.IsNullOrEmpty(pointer)) return null;
            if (pointer == "#") return rawRoot;
            if (!pointer.StartsWith("#/", StringComparison.Ordinal)) return null;

            // 必須用 char[] 多載：Split(char) 是 .NET Core 才有的，
            // 在 net472／RimWorld Mono 上會拋 MissingMethodException。
            JToken current = rawRoot;
            foreach (string rawSegment in pointer.Substring(2).Split(new char[] { '/' }))
            {
                var container = current as JObject;
                if (container == null) return null;

                // RFC 6901 的轉義：~1 代表 '/'，~0 代表 '~'。順序不可顛倒。
                string segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
                current = container[segment];
                if (current == null) return null;
            }

            return current as JObject;
        }

        // ---------------------------------------------------------------------
        // Stage C：provider 方言
        // ---------------------------------------------------------------------

        private static JObject ApplyProfile(JObject canonical, RimLLMSchemaProfile profile)
        {
            var shaped = (JObject)canonical.DeepClone();
            shaped.Remove(LegacyMarker);
            ApplyProfileRecursive(shaped, profile);
            return shaped;
        }

        private static void ApplyProfileRecursive(JObject node, RimLLMSchemaProfile profile)
        {
            if (node == null) return;

            bool optional = node[OptionalMarker] != null && node[OptionalMarker].Value<bool>();
            node.Remove(OptionalMarker);

            if (optional)
            {
                if (profile == RimLLMSchemaProfile.Gemini)
                {
                    node["nullable"] = true;
                }
                else
                {
                    JToken type = node["type"];
                    if (type != null && type.Type == JTokenType.String)
                    {
                        node["type"] = new JArray(type.Value<string>(), "null");
                    }
                }
            }

            // 白名單過濾放在最後：nullable 是 Stage C 才加的，必須在此之後才允許存在。
            var removable = new List<string>();
            foreach (KeyValuePair<string, JToken> member in node)
            {
                if (!AllowedKeywords.Contains(member.Key) && member.Key != "nullable")
                {
                    removable.Add(member.Key);
                }
            }
            foreach (string key in removable)
            {
                node.Remove(key);
            }

            ApplyProfileRecursive(node["items"] as JObject, profile);
            ApplyProfileRecursive(node["additionalProperties"] as JObject, profile);

            var properties = node["properties"] as JObject;
            if (properties != null)
            {
                foreach (KeyValuePair<string, JToken> property in properties)
                {
                    ApplyProfileRecursive(property.Value as JObject, profile);
                }
            }
        }

        private static bool HasOpenEndedMap(JObject node)
        {
            if (node == null) return false;

            JToken additional = node["additionalProperties"];
            if (additional != null && additional.Type != JTokenType.Boolean)
            {
                return true;
            }

            if (HasOpenEndedMap(node["items"] as JObject)) return true;

            var properties = node["properties"] as JObject;
            if (properties != null)
            {
                foreach (KeyValuePair<string, JToken> property in properties)
                {
                    if (HasOpenEndedMap(property.Value as JObject)) return true;
                }
            }

            return false;
        }

        // ---------------------------------------------------------------------
        // 序列化設定
        // ---------------------------------------------------------------------

        /// <summary>
        /// exporter 用的序列化設定。刻意不用 static field initializer：
        /// 靜態建構式一旦拋例外，整個類別會被 <c>TypeInitializationException</c> 永久鎖死。
        /// </summary>
        private static JsonSerializerOptions EnsureSerializerOptions()
        {
            if (_serializerOptions != null) return _serializerOptions;

            lock (OptionsLock)
            {
                if (_serializerOptions != null) return _serializerOptions;

                var resolver = new DefaultJsonTypeInfoResolver();
                resolver.Modifiers.Add(ApplyNewtonsoftContract);

                var options = new JsonSerializerOptions
                {
                    // 生產路徑一律以 Newtonsoft 反序列化，schema 的成員契約必須跟著 Newtonsoft 走：
                    // Newtonsoft 預設序列化 public field，System.Text.Json 預設不會。
                    IncludeFields = true,
                    // MEAI 的預設設定是 camelCase，會產生 optionalCount / selfRef 這種與 CLR 成員名不一致的鍵。
                    PropertyNamingPolicy = null,
                    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                    TypeInfoResolver = resolver
                };
                options.Converters.Add(new JsonStringEnumConverter());

                _serializerOptions = options;
                return _serializerOptions;
            }
        }

        /// <summary>
        /// 把 System.Text.Json 的合約拉齊到 Newtonsoft 的行為。
        /// schema 由 STJ 產生、反序列化卻由 Newtonsoft 執行，兩邊的成員集合與鍵名必須一致，
        /// 否則模型會照 schema 填一個 Newtonsoft 收不到的欄位。
        ///
        /// 已知無法對齊的殘餘風險：Newtonsoft 的自訂 <c>[JsonConverter]</c> 會改變 wire 形狀，
        /// 而 STJ 的 exporter 完全看不到它。結構化輸出的型別請勿使用自訂 Newtonsoft converter。
        /// </summary>
        private static void ApplyNewtonsoftContract(JsonTypeInfo typeInfo)
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;

            for (int index = typeInfo.Properties.Count - 1; index >= 0; index--)
            {
                JsonPropertyInfo property = typeInfo.Properties[index];

                if (property.IsExtensionData)
                {
                    typeInfo.Properties.RemoveAt(index);
                    continue;
                }

                // 唯讀成員 Newtonsoft 反序列化不會寫入，等同舊實作的 CanWrite / !IsInitOnly 條件。
                if (property.Set == null)
                {
                    typeInfo.Properties.RemoveAt(index);
                    continue;
                }

                var member = property.AttributeProvider as MemberInfo;
                if (member == null) continue;

                if (member.IsDefined(typeof(Newtonsoft.Json.JsonIgnoreAttribute), true))
                {
                    typeInfo.Properties.RemoveAt(index);
                    continue;
                }

                var fieldInfo = member as FieldInfo;
                if (fieldInfo != null && fieldInfo.IsInitOnly)
                {
                    typeInfo.Properties.RemoveAt(index);
                    continue;
                }

                object[] jsonProperties = member.GetCustomAttributes(typeof(Newtonsoft.Json.JsonPropertyAttribute), true);
                if (jsonProperties.Length > 0)
                {
                    string propertyName = ((Newtonsoft.Json.JsonPropertyAttribute)jsonProperties[0]).PropertyName;
                    if (!string.IsNullOrEmpty(propertyName))
                    {
                        property.Name = propertyName;
                    }
                }
            }
        }

        private static JsonTypeInfo GetTypeInfo(Type type)
        {
            if (type == null) return null;

            try
            {
                return EnsureSerializerOptions().GetTypeInfo(type);
            }
            catch
            {
                // 拿不到型別資訊只會讓該子樹退化成純 JSON 正規化，不該讓整份 schema 失敗。
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // 降級路徑
        // ---------------------------------------------------------------------

        /// <summary>
        /// 純反射的降級實作（MEAI exporter 不可用時使用）。
        /// 產出的就是 canonical 形狀（單一 type、無 <c>$ref</c>），差別只在
        /// <c>Nullable&lt;T&gt;</c> 成員是以「不列入 required」表達，而不是 <see cref="OptionalMarker"/>。
        /// 因此 Stage C 對它等同 no-op，而 <c>StrictCompatible</c> 會被強制為 false。
        /// </summary>
        private static JObject BuildLegacyCanonical(Type type, int maxDepth)
        {
            return BuildLegacySchema(type, new HashSet<Type>(), maxDepth, 0) ?? CreateEmptyObjectSchema();
        }

        /// <summary>
        /// <paramref name="visited"/> 追蹤目前遞迴路徑上的型別，偵測到循環時回傳 null，
        /// 由父層略過該成員（與 <c>CreateDummyInstance</c> 把循環欄位截斷為 null 的行為一致）。
        /// </summary>
        private static JObject BuildLegacySchema(Type type, HashSet<Type> visited, int maxDepth, int depth)
        {
            if (type == null || depth > maxDepth)
            {
                return null;
            }

            // Nullable<T> 一律以底層型別產生 schema；父層負責不將其列入 required。
            Type underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType != null)
            {
                return BuildLegacySchema(underlyingType, visited, maxDepth, depth);
            }

            var schema = new JObject();

            if (type == typeof(string) || type == typeof(char))
            {
                schema["type"] = "string";
            }
            else if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
                     type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
            {
                schema["type"] = "integer";
            }
            else if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                schema["type"] = "number";
            }
            else if (type == typeof(bool))
            {
                schema["type"] = "boolean";
            }
            else if (type.IsEnum)
            {
                schema["type"] = "string";
                var names = new JArray();
                foreach (string name in Enum.GetNames(type))
                {
                    names.Add(name);
                }
                schema["enum"] = names;
            }
            // Dictionary 需以開放式 map 表示，否則反射會落入自訂物件分支而產生空 properties。
            // 必須排在集合分支之前：Dictionary<,> 同時也實作 ICollection<KeyValuePair<,>>。
            else if (IsSupportedDictionary(type, out Type keyType, out Type valueType))
            {
                schema["type"] = "object";

                // JSON 物件的鍵一律是字串，因此只有 string 或 enum 鍵能忠實表示成 map。
                if (keyType == typeof(string) || keyType.IsEnum)
                {
                    JObject valueSchema = BuildLegacySchema(valueType, visited, maxDepth, depth + 1);
                    if (valueSchema != null)
                    {
                        schema["additionalProperties"] = valueSchema;
                    }
                }
            }
            else if (GetSequenceElementType(type) != null)
            {
                schema["type"] = "array";
                JObject itemSchema = BuildLegacySchema(GetSequenceElementType(type), visited, maxDepth, depth + 1);
                if (itemSchema == null) return null;
                schema["items"] = itemSchema;
            }
            else
            {
                // 循環引用偵測：若該型別已在目前遞迴路徑上，回傳 null 讓父層略過此成員。
                if (!visited.Add(type))
                {
                    return null;
                }

                try
                {
                    schema["type"] = "object";
                    var properties = new JObject();
                    var required = new JArray();

                    foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (prop.CanRead && prop.CanWrite && prop.GetIndexParameters().Length == 0)
                        {
                            JObject propSchema = BuildLegacySchema(prop.PropertyType, visited, maxDepth, depth + 1);
                            if (propSchema == null) continue;

                            properties[prop.Name] = propSchema;
                            if (Nullable.GetUnderlyingType(prop.PropertyType) == null)
                            {
                                required.Add(prop.Name);
                            }
                        }
                    }

                    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!field.IsLiteral && !field.IsInitOnly)
                        {
                            JObject fieldSchema = BuildLegacySchema(field.FieldType, visited, maxDepth, depth + 1);
                            if (fieldSchema == null) continue;

                            properties[field.Name] = fieldSchema;
                            if (Nullable.GetUnderlyingType(field.FieldType) == null)
                            {
                                required.Add(field.Name);
                            }
                        }
                    }

                    schema["properties"] = properties;
                    schema["required"] = required;
                    schema["additionalProperties"] = false;
                }
                finally
                {
                    visited.Remove(type);
                }
            }

            return schema;
        }

        private static bool IsSupportedDictionary(Type type, out Type keyType, out Type valueType)
        {
            keyType = null;
            valueType = null;

            if (!type.IsGenericType) return false;

            Type definition = type.GetGenericTypeDefinition();
            if (definition != typeof(Dictionary<,>) &&
                definition != typeof(IDictionary<,>) &&
                definition != typeof(IReadOnlyDictionary<,>))
            {
                return false;
            }

            Type[] args = type.GetGenericArguments();
            keyType = args[0];
            valueType = args[1];
            return true;
        }

        private static Type GetSequenceElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();

            if (type.IsGenericType)
            {
                Type definition = type.GetGenericTypeDefinition();
                if (definition == typeof(List<>) ||
                    definition == typeof(IList<>) ||
                    definition == typeof(IReadOnlyList<>) ||
                    definition == typeof(ICollection<>))
                {
                    return type.GetGenericArguments()[0];
                }
            }

            return null;
        }

        private static JObject CreateEmptyObjectSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["required"] = new JArray(),
                ["additionalProperties"] = false
            };
        }
    }
}
