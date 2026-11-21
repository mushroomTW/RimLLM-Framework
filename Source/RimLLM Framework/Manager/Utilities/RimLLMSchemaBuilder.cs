using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 一次 schema 產生的完整結果。不可變，因此可直接由快取共用而不需複製。
/// </summary>
public sealed class RimLLMSchemaResult
{
    internal RimLLMSchemaResult(string json, bool containsOpenEndedMap, bool strictCompatible)
    {
        Json = json;
        ContainsOpenEndedMap = containsOpenEndedMap;
        StrictCompatible = strictCompatible;
    }

    /// <summary>已套用 OpenAI 相容方言的 schema JSON。</summary>
    public string Json { get; }

    /// <summary>schema 中是否含開放式 map（由 Dictionary 產生的 <c>additionalProperties</c> 物件）。</summary>
    public bool ContainsOpenEndedMap { get; }

    /// <summary>是否可安全地以 OpenAI strict structured output 送出。</summary>
    public bool StrictCompatible { get; }

    // 以參考型別裝箱：參考的寫入是原子的；JsonElement? 是多欄位結構，並行首次存取可能讀到寫一半的值。
    private volatile StrongBox<JsonElement>? _element;

    /// <summary>
    /// <see cref="Json"/> 解析後的獨立 <see cref="JsonElement"/>，第一次存取時解析一次後重用。
    /// 多執行緒同時首次存取只會各解析一次、結果相同。
    /// </summary>
    internal JsonElement Element
    {
        get
        {
            var cached = _element;
            if (cached != null) return cached.Value;

            using JsonDocument document = JsonDocument.Parse(Json);
            JsonElement element = document.RootElement.Clone();
            _element = new StrongBox<JsonElement>(element);
            return element;
        }
    }
}

/// <summary>
/// 結構化輸出的 JSON Schema 產生器。
///
/// 管線分三段：
/// <list type="number">
/// <item>Stage A：<c>System.Text.Json.Schema.JsonSchemaExporter</c> 產生完整的 JSON Schema。</item>
/// <item>Stage B：正規化成 provider 吃得下的受限子集 —— 展開 <c>$ref</c>、截斷循環與過深巢狀、
/// 把可為 null 的聯集收斂成單一 <c>type</c>、補上 <c>[Description]</c>、只保留關鍵字白名單。</item>
/// <item>Stage C：套用唯一的 OpenAI 相容方言（選填成員寫成 ["T","null"] 聯集）。</item>
/// </list>
///
/// 為什麼不能直接送 exporter 的原始輸出：它把可為 null 的成員寫成 <c>"type": ["string","null"]</c>，
/// 送出前必須先收斂；<c>$ref</c> 指標也必須先解析。
/// 全供應商皆走 OpenAI 相容端點，統一使用聯集寫法。
/// </summary>
public static class RimLLMSchemaBuilder
{
    /// <summary>
    /// Schema 遞迴的最大深度。超過此深度的巢狀成員會被略過，避免病態型別造成堆疊耗盡。
    /// OpenAI 的 strict structured output 明訂 schema 最多 5 層巢狀（另有全域 100 個 property 的上限）。
    /// 超過就會被服務端拒絕，接著被 <c>IsNativeSchemaRejected</c> 靜默降級成提示式 JSON ——
    /// 與其送出已知會被拒的 schema，不如在此先截斷。
    /// </summary>
    public const int OpenAIMaxSchemaDepth = 5;

    /// <summary>Stage B 用來標記 <c>Nullable&lt;T&gt;</c> 節點的私有關鍵字，Stage C 會翻譯並移除它。</summary>
    private const string OptionalMarker = "x-rimllm-optional";

    /// <summary>Stage C 之前允許存在的關鍵字。其餘一律剝除。</summary>
    private const string SchemaType = "type";
    private const string SchemaEnum = "enum";
    private const string SchemaProperties = "properties";
    private const string SchemaRequired = "required";
    private const string SchemaItems = "items";
    private const string SchemaAdditionalProperties = "additionalProperties";
    private const string SchemaDescription = "description";

    private static readonly HashSet<string> AllowedKeywords = new(StringComparer.Ordinal)
    {
        SchemaType, SchemaEnum, SchemaProperties, SchemaRequired, SchemaItems, SchemaAdditionalProperties, SchemaDescription, OptionalMarker
    };

    /// <summary>
    /// 最終結果快取。Stage C 只剩唯一的 OpenAI 相容方言，結果完全由型別決定，
    /// 因此直接以 <c>Type</c> 為鍵。
    /// </summary>
    private static readonly ConcurrentDictionary<Type, RimLLMSchemaResult> ResultCache = new();

    private static readonly System.Threading.Lock OptionsLock = new();
    private static JsonSerializerOptions? _serializerOptions;

    /// <summary>
    /// 產生指定型別的 schema。結果不可變，可直接共用。
    /// </summary>
    public static RimLLMSchemaResult Build(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (ResultCache.TryGetValue(type, out RimLLMSchemaResult? cached))
        {
            return cached;
        }

        // 不另外快取 canonical：Build 本身已由 ResultCache 快取，每個型別只會走到這裡一次。
        JsonObject canonical = BuildCanonical(type, OpenAIMaxSchemaDepth);
        JsonObject shaped = ApplyOpenAiDialect(canonical);

        bool containsOpenEndedMap = HasOpenEndedMap(shaped);
        bool strictCompatible = !containsOpenEndedMap;

        var result = new RimLLMSchemaResult(shaped.ToJsonString(), containsOpenEndedMap, strictCompatible);
        ResultCache[type] = result;
        return result;
    }

    /// <summary>產生 schema 的 JSON 字串。</summary>
    public static string BuildJson(Type type)
    {
        return Build(type).Json;
    }

    /// <summary>
    /// 判斷型別產生的 schema 是否含開放式 map。OpenAI 的 strict structured output 不接受這種形狀。
    /// 由實際產生的 schema 推導，而非型別樹反射 —— 被深度截斷掉的 Dictionary 不該再關閉 strict。
    /// </summary>
    public static bool ContainsOpenEndedMap(Type type)
    {
        if (type == null) return false;
        return Build(type).ContainsOpenEndedMap;
    }


    private static JsonObject BuildCanonical(Type type, int maxDepth)
    {
        JsonObject raw = ExportRaw(type);
        JsonObject? normalized = Normalize(raw, GetTypeInfo(type), new NormalizeContext(raw, maxDepth), 0);
        return normalized ?? CreateEmptyObjectSchema();
    }

    /// <summary>
    /// Stage A：由 System.Text.Json 的 <see cref="JsonSchemaExporter"/> 產生完整 JSON Schema。
    ///
    /// 刻意直接呼叫 exporter，而不是 MEAI 的 <c>AIJsonUtilities.CreateJsonSchema</c> 包裝：
    /// exporter 正是 MEAI 內部使用的同一個引擎，直呼它讓 Stage B/C 拿到未經包裝層改寫的
    /// 原始輸出，正規化只需要對付一種形狀。MEAI 包裝層多做而我們仍需要的只有
    /// <c>[Description]</c>，由 Stage B 自行讀取補上。
    ///
    /// </summary>
    internal static JsonObject ExportRaw(Type type)
    {
        JsonNode node = JsonSchemaExporter.GetJsonSchemaAsNode(EnsureSerializerOptions(), type);
        // exporter 的根一律是物件 schema；若不是，轉型失敗直接拋出，不會靜默產出錯誤形狀。
        return (JsonObject)(JsonNode.Parse(node.ToJsonString()) ?? throw new JsonException("Schema node could not be re-parsed."));
    }

    private sealed class NormalizeContext
    {
        public NormalizeContext(JsonObject rawRoot, int maxDepth)
        {
            RawRoot = rawRoot;
            MaxDepth = maxDepth;
        }

        /// <summary>exporter 原始輸出的根節點，<c>$ref</c> 的 JSON pointer 以它為基準。</summary>
        public JsonObject RawRoot { get; }

        /// <summary>本次產生適用的巢狀深度上限（固定為 OpenAI strict 上限）。</summary>
        public int MaxDepth { get; }

        /// <summary>目前展開路徑上已解析過的 pointer。</summary>
        public List<string> PointerPath { get; } = [];

        /// <summary>目前展開路徑上的 CLR 物件型別，用來在型別層截斷循環。</summary>
        public List<Type> TypePath { get; } = [];
    }

    /// <summary>
    /// 正規化遞迴核心。
    ///
    /// 回傳 <see langword="null"/> 的語意是「這個節點無法表達，父層必須刪掉對應成員並從
    /// <c>required</c> 移除」。
    /// </summary>
#pragma warning disable S1168 // null 表示不可表達節點，與空集合語意不同，呼叫端需區分。
    private static JsonObject? Normalize(JsonObject? node, JsonTypeInfo? typeInfo, NormalizeContext context, int depth)
    {
        if (node == null || depth > context.MaxDepth)
        {
            return null;
        }

        // exporter 的 $ref 是指向樹內既有節點的 JSON pointer，而且不只用於循環，也用於去重。
        if (TryNormalizeRef(node, typeInfo, context, depth, out JsonObject? refResult))
        {
            return refResult;
        }

        JsonObject? collapsed = CollapseCompositeKeywords(node);
        if (collapsed == null)
        {
            return null;
        }

        string? typeName = ExtractTypeName(collapsed);
        if (typeName == null)
        {
            return null;
        }

        var result = new JsonObject
        {
            ["type"] = typeName
        };

        // Nullable<T> 可能是成員、陣列元素或字典值；標記型別節點本身，讓後續方言轉換一致保留 null。
        if (typeInfo != null && Nullable.GetUnderlyingType(typeInfo.Type) != null)
        {
            result[OptionalMarker] = true;
        }

        JsonNode? description = collapsed[SchemaDescription];
        if (description != null && description.GetValueKind() == JsonValueKind.String)
        {
            result[SchemaDescription] = description.GetValue<string>();
        }

        if (typeName == "array")
        {
            return NormalizeArray(collapsed, typeInfo, context, depth, result);
        }

        if (typeName == "object")
        {
            return NormalizeObject(collapsed, result, typeInfo, context, depth);
        }

        if (collapsed["enum"] is JsonArray enumValues)
        {
            result["enum"] = enumValues.DeepClone();
        }

        return result;
    }
#pragma warning restore S1168

#pragma warning disable S1168 // null 表示陣列元素無法表達，調用端會將整個成員捨棄，不應轉為空集合。
    /// <summary>正規化陣列型別節點；元素無法表達時整個陣列成員一併捨棄（回傳 null 表示無法表達）。</summary>
    private static JsonObject? NormalizeArray(JsonObject collapsed, JsonTypeInfo? typeInfo, NormalizeContext context, int depth, JsonObject result)
    {
        JsonObject? itemSchema = Normalize(
            collapsed[SchemaItems] as JsonObject,
            typeInfo?.ElementType is { } elementType ? GetTypeInfo(elementType) : null,
            context,
            depth + 1);

        if (itemSchema == null) return null;
        result[SchemaItems] = itemSchema;
        return result;
    }
#pragma warning restore S1168

    /// <summary>
    /// 處理 $ref 指標：若節點是指向既有節點的 pointer，遞迴正規化目標並回傳 true；
    /// 若 pointer 指向目前路徑上的祖先（循環）或目標不存在，回傳 null 並 true；
    /// 若此節點不是 $ref，回傳 false。
    /// </summary>
    private static bool TryNormalizeRef(JsonObject node, JsonTypeInfo? typeInfo, NormalizeContext context, int depth, out JsonObject? result)
    {
        result = null;
        JsonNode? refToken = node["$ref"];
        if (refToken == null || refToken.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        string pointer = refToken.GetValue<string>();
        if (context.PointerPath.Contains(pointer))
        {
            return true;
        }

        JsonObject? target = ResolvePointer(context.RawRoot, pointer);
        if (target == null)
        {
            return true;
        }

        context.PointerPath.Add(pointer);
        try
        {
            result = Normalize(target, typeInfo, context, depth);
        }
        finally
        {
            context.PointerPath.RemoveAt(context.PointerPath.Count - 1);
        }
        return true;
    }

    #pragma warning disable S1168 // 回傳 JsonObject? 而非集合，null 代表截斷遞迴，呼叫端已處理。
    private static JsonObject? NormalizeObject(
        JsonObject node, JsonObject result, JsonTypeInfo? typeInfo, NormalizeContext context, int depth)
    {
        // Dictionary 會產生開放式 map（additionalProperties 是一份 value schema）；
        // 自訂類別則沒有 additionalProperties，由我們補上 false。
        if (node[SchemaAdditionalProperties] is JsonObject valueSchema)
        {
            JsonObject? normalizedValue = Normalize(
                valueSchema,
                typeInfo?.ElementType is { } elementType ? GetTypeInfo(elementType) : null,
                context,
                depth + 1);

            if (normalizedValue != null)
            {
                result[SchemaAdditionalProperties] = normalizedValue;
            }

            return result;
        }

        // 循環在 CLR 型別層截斷，而不是等到 JSON pointer 重現才截斷。
        // exporter 會把遞迴成員先完整展開一輪、其中才出現指回祖先的 $ref，
        // 若只靠 pointer 偵測就會多送一整層 —— 實測 ComplexTestDataStructure 的 schema
        // 從 789 字元漲到 3119 字元，而那是每次結構化請求都要付的 prompt token。
        Type? clrType = typeInfo?.Type;
        if (clrType != null)
        {
            if (context.TypePath.Contains(clrType)) return null;
            context.TypePath.Add(clrType);
        }

        try
        {
            ApplyDescription(result, clrType);
            NormalizeProperties(node, result, typeInfo, context, depth);
            result[SchemaAdditionalProperties] = false;
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
    #pragma warning restore S1168

    /// <summary>
    /// 正規化物件的 <c>properties</c> 與 <c>required</c> 關鍵字。每個成員個別正規化，
    /// 無法表達的成員會被略過。
    /// </summary>
    private static void NormalizeProperties(
        JsonObject node, JsonObject result, JsonTypeInfo? typeInfo, NormalizeContext context, int depth)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        if (node[SchemaProperties] is JsonObject rawProperties)
        {
            Dictionary<string, JsonPropertyInfo> memberLookup = BuildMemberLookup(typeInfo);

            foreach (var property in rawProperties)
            {
                memberLookup.TryGetValue(property.Key, out JsonPropertyInfo? memberInfo);

                JsonObject? memberSchema = Normalize(
                    property.Value as JsonObject,
                    memberInfo != null ? GetTypeInfo(memberInfo.PropertyType) : null,
                    context,
                    depth + 1);

                if (memberSchema == null) continue;

                ApplyMemberDescription(memberSchema, memberInfo);

                properties[property.Key] = memberSchema;
                required.Add(property.Key);
            }
        }

        result[SchemaProperties] = properties;
        result["required"] = required;
    }

    /// <summary>
    /// 把成員上的 <see cref="DescriptionAttribute"/> 寫進 schema。
    ///
    /// System.Text.Json 的 exporter 沒有 description 的概念 —— 這是 MEAI 包裝層多做的事，
    /// 而框架不經過那層（見 <see cref="ExportRaw"/>）。
    /// 只讀 <c>System.ComponentModel.DescriptionAttribute</c>。
    /// </summary>
    private static void ApplyMemberDescription(JsonObject memberSchema, JsonPropertyInfo? memberInfo)
    {
        ApplyDescription(memberSchema, memberInfo?.AttributeProvider as MemberInfo);
    }

    private static void ApplyDescription(JsonObject schema, MemberInfo? attributeSource)
    {
        if (schema == null || attributeSource == null) return;
        if (schema[SchemaDescription] != null) return;

        object[] attributes = attributeSource.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true);
        if (attributes.Length == 0) return;

        string description = ((DescriptionAttribute)attributes[0]).Description;
        if (!string.IsNullOrEmpty(description))
        {
            schema[SchemaDescription] = description;
        }
    }

    private static Dictionary<string, JsonPropertyInfo> BuildMemberLookup(JsonTypeInfo? typeInfo)
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
    #pragma warning disable S1168 // 回傳 JsonObject 而非集合，null 表示無法攤平，呼叫端已處理。
    private static JsonObject? CollapseCompositeKeywords(JsonObject node)
    {
        JsonArray? composite = (node["allOf"] as JsonArray) ?? (node["anyOf"] as JsonArray) ?? (node["oneOf"] as JsonArray);
        if (composite == null)
        {
            return node;
        }

        JsonObject? candidate = null;
        foreach (JsonNode? branch in composite)
        {
            if (branch is not JsonObject branchObject) return null;

            if (IsNullOnlySchema(branchObject)) continue;

            if (candidate != null) return null;
            candidate = branchObject;
        }

        if (candidate == null) return null;

        var merged = (JsonObject)candidate.DeepClone();
        foreach (var sibling in node)
        {
            if (sibling.Key == "allOf" || sibling.Key == "anyOf" || sibling.Key == "oneOf") continue;
            if (merged[sibling.Key] == null)
            {
                merged[sibling.Key] = sibling.Value?.DeepClone();
            }
        }

        return merged;
    }
    #pragma warning restore S1168

    private static bool IsNullOnlySchema(JsonObject node)
    {
        JsonNode? type = node["type"];
        return type != null && type.GetValueKind() == JsonValueKind.String && type.GetValue<string>() == "null";
    }

    /// <summary>
    /// 取出單一 type 名稱。可為 null 的聯集在此收斂 —— 選填語意改由 <see cref="OptionalMarker"/> 攜帶，
    /// 由 Stage C 依 provider 方言還原。
    /// </summary>
    private static string? ExtractTypeName(JsonObject node)
    {
        JsonNode? typeToken = node["type"];

        // exporter 對列舉只輸出 {"enum":[...]}，不帶 type；此管線直接使用 exporter，
        // 沒有 type 的節點會被視為無法表達而丟棄，
        // 所以在此由列舉值反推 —— 否則所有列舉成員都會從 schema 中消失。
        if (typeToken == null) return InferTypeFromEnum(node["enum"] as JsonArray);

        if (typeToken.GetValueKind() == JsonValueKind.String)
        {
            string single = typeToken.GetValue<string>();
            return single == "null" ? null : single;
        }

        if (typeToken is not JsonArray candidates) return null;

        foreach (JsonNode? candidate in candidates)
        {
            if (candidate?.GetValueKind() != JsonValueKind.String) continue;
            string name = candidate.GetValue<string>();
            if (name != "null") return name;
        }

        return null;
    }

    /// <summary>
    /// 由列舉值反推 <c>type</c>。<c>JsonStringEnumConverter</c> 會產出字串值，
    /// 未套用該轉換器的列舉則是整數值。
    /// </summary>
    private static string? InferTypeFromEnum(JsonArray? enumValues)
    {
        if (enumValues == null || enumValues.Count == 0) return null;

        foreach (JsonNode? value in enumValues)
        {
            if (value?.GetValueKind() == JsonValueKind.String) return "string";
            if (value?.GetValueKind() == JsonValueKind.Number) return "integer";
        }

        return null;
    }

    /// <summary>
    /// 解析 exporter 產生的 JSON pointer（形如 <c>#/properties/Nested/properties/Child</c>）。
    /// MEAI 不使用 <c>$defs</c>，pointer 一律指向輸出樹內的既有路徑。
    /// </summary>
    #pragma warning disable S1168 // 回傳 JsonObject? 而非集合，null 表示指針無效，呼叫端已處理。
    private static JsonObject? ResolvePointer(JsonObject rawRoot, string pointer)
    {
        if (string.IsNullOrEmpty(pointer)) return null;
        if (pointer == "#") return rawRoot;
        if (!pointer.StartsWith("#/", StringComparison.Ordinal)) return null;

        JsonNode? current = rawRoot;
        foreach (string rawSegment in pointer.Substring(2).Split('/'))
        {
            if (current is not JsonObject container) return null;

            // RFC 6901 的轉義：~1 代表 '/'，~0 代表 '~'。順序不可顛倒。
            string segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = container[segment];
            if (current == null) return null;
        }

        return current as JsonObject;
    }
    #pragma warning restore S1168


    /// <summary>就地改寫：canonical 每次都是新產生、沒有其他持有者，不需要先複製。</summary>
    private static JsonObject ApplyOpenAiDialect(JsonObject canonical)
    {
        ApplyOpenAiDialectRecursive(canonical);
        return canonical;
    }

    private static void ApplyOpenAiDialectRecursive(JsonObject? node)
    {
        if (node == null) return;

        bool optional = ApplyOptionalMarker(node);

        if (optional)
        {
            JsonNode? type = node["type"];
            if (type != null && type.GetValueKind() == JsonValueKind.String)
            {
                node["type"] = new JsonArray(type.GetValue<string>(), "null");
            }
            else if (type is JsonArray typeNames && !ContainsTypeName(typeNames, "null"))
            {
                typeNames.Add("null");
            }

            if (node["enum"] is JsonArray enumValues && !ContainsNull(enumValues))
            {
                enumValues.Add((JsonNode?)null);
            }
        }

        var removable = node.Where(member => !AllowedKeywords.Contains(member.Key)).Select(member => member.Key).ToList();
        foreach (string key in removable)
        {
            node.Remove(key);
        }

        ApplyOpenAiDialectRecursive(node[SchemaItems] as JsonObject);
        ApplyOpenAiDialectRecursive(node[SchemaAdditionalProperties] as JsonObject);

        if (node[SchemaProperties] is JsonObject properties)
        {
            foreach (var property in properties)
            {
                ApplyOpenAiDialectRecursive(property.Value as JsonObject);
            }
        }
    }

    private static bool ApplyOptionalMarker(JsonObject node)
    {
        JsonNode? optionalMarker = node[OptionalMarker];
        bool optional = optionalMarker != null && optionalMarker.GetValue<bool>();
        node.Remove(OptionalMarker);
        return optional;
    }

    private static bool ContainsTypeName(JsonArray values, string name)
    {
        foreach (JsonNode? value in values)
        {
            if (value != null && value.GetValueKind() == JsonValueKind.String &&
                string.Equals(value.GetValue<string>(), name, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsNull(JsonArray values)
    {
        foreach (JsonNode? value in values)
        {
            if (value == null || value.GetValueKind() == JsonValueKind.Null) return true;
        }
        return false;
    }

    private static bool HasOpenEndedMap(JsonObject? node)
    {
        if (node == null) return false;

        JsonNode? additional = node[SchemaAdditionalProperties];
        // additionalProperties 為 false（自訂類別）或物件（Dictionary 的 value schema）才是合法形狀；
        if (additional != null && additional.GetValueKind() != JsonValueKind.True && additional.GetValueKind() != JsonValueKind.False)
        {
            return true;
        }

        if (HasOpenEndedMap(node[SchemaItems] as JsonObject)) return true;

        if (node[SchemaProperties] is JsonObject properties &&
            properties.Any(p => HasOpenEndedMap(p.Value as JsonObject)))
        {
            return true;
        }

        return false;
    }


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
            resolver.Modifiers.Add(ApplySerializationContract);

            var options = new JsonSerializerOptions
            {
                // 生產路徑以 System.Text.Json 反序列化，schema 的成員契約必須跟著它走：
                // STJ 預設不序列化 public field，而 DTO 大量使用 field。
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
    /// 把 exporter 合約收斂到「反序列化能寫入」的成員集合。
    /// schema 由 STJ 產生、反序列化也由 STJ 執行，兩邊的成員集合與鍵名必須一致，
    /// 否則模型會照 schema 填一個反序列化收不到的欄位。
    ///
    /// <c>[JsonIgnore]</c> 與 <c>[JsonPropertyName]</c> 由 exporter 原生支援，不需在此手動處理；
    /// 只有「反序列化寫不進去」的成員需要剔除。
    ///
    /// 已知殘餘風險：自訂 <c>[JsonConverter]</c> 會改變 wire 形狀，
    /// 而 exporter 完全看不到它。結構化輸出的型別請勿使用自訂 converter。
    /// </summary>
    private static void ApplySerializationContract(JsonTypeInfo typeInfo)
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

            // 唯讀成員反序列化不會寫入。
            if (property.Set == null)
            {
                typeInfo.Properties.RemoveAt(index);
                continue;
            }

            var member = property.AttributeProvider as MemberInfo;
            if (member == null) continue;

            var fieldInfo = member as FieldInfo;
            if (fieldInfo != null && fieldInfo.IsInitOnly)
            {
                typeInfo.Properties.RemoveAt(index);
            }
        }
    }

    private static JsonTypeInfo? GetTypeInfo(Type type)
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

    private static JsonObject CreateEmptyObjectSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            [SchemaProperties] = new JsonObject(),
            ["required"] = new JsonArray(),
            [SchemaAdditionalProperties] = false
        };
    }
}
