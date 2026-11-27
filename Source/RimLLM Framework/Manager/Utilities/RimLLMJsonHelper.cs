using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager;

/// <summary>
/// JSON 的靜態處理與格式修復輔助工具。
/// 包含結構化資料的 JSON 補齊、Regex 修復以及 Dummy 物件生成（用於產生 schema 快取）。
/// </summary>
public static partial class RimLLMJsonHelper
{
    private static readonly ConcurrentDictionary<Type, string> SampleJsonCache = new();

    // 全框架共用的 JSON 引擎與寬容設定。
    // DTO 大量使用 public field（STJ 預設只看 property），反序列化大小寫不敏感，
    // 並對齊舊 Newtonsoft 寬容度：數字可由字串讀入、尾隨逗號與註解略過、列舉接受名稱。
    private static readonly JsonSerializerOptions SharedOptions = CreateSharedOptions();
    private static readonly DefaultJsonTypeInfoResolver SharedTypeInfoResolver = new();

    private static JsonSerializerOptions CreateSharedOptions()
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>共用設定。首次序列化後 System.Text.Json 即鎖定選項，不可再修改；可多執行緒共用。</summary>
    public static JsonSerializerOptions Options => SharedOptions;

    /// <summary>以執行期型別序列化（DTO 可能是 object 宣告，泛型推斷會丟失成員）。</summary>
    public static string Serialize(object? value)
    {
        if (value == null) return "null";
        return JsonSerializer.Serialize(value, value.GetType(), SharedOptions);
    }

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, SharedOptions);
    }

    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, SharedOptions);
    }

    /// <summary>
    /// 獲取指定型別的 Sample JSON 字串。
    /// </summary>
    public static string GetSampleJson<T>()
    {
        return GetSampleJson(typeof(T));
    }

    /// <summary>
    /// 獲取指定型別的 Sample JSON 字串。
    /// </summary>
    public static string GetSampleJson(Type type)
    {
        if (SampleJsonCache.TryGetValue(type, out string? json))
        {
            return json;
        }

        try
        {
            object? instance = CreateDummyInstance(type);
            string generatedJson = Serialize(instance);
            SampleJsonCache[type] = generatedJson;
            return generatedJson;
        }
        catch
        {
            return "{}";
        }
    }



    /// <summary>
    /// 結構化輸出的核心流程：直接解析 → 靜態 JSON repair → 抽出 JSON 區塊後第二次解析。
    /// 純本地修復，不會再向模型發第二次請求。
    /// </summary>
    public static T DeserializeStructured<T>(string rawResponse, IRimLLMSettings settings)
    {
        try
        {
            return DeserializeAndValidate<T>(rawResponse);
        }
        catch (Exception ex)
        {
            if (settings?.EnableJsonRepair != true)
            {
                throw new RimLLMException(
                    LLMError.InvalidResponse,
                    $"Unable to parse LLM response to target object {typeof(T).Name} (JSON Repair is disabled). Raw Response: {RimLLMLog.SanitizeForLog(rawResponse, 300)}. Parse error: {RimLLMLog.SanitizeForLog(ex.Message, 200)}",
                    ex);
            }

            string repairedJson = RepairJson(rawResponse);
            if (RimLLMLog.Enabled)
            {
                RimLLMLog.Warning($"[RimLLM] First JSON parse failed, attempting static repair. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}\nRepaired preview: {RimLLMLog.SanitizeForLog(repairedJson, 300)}\nError: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
            }
            try
            {
                string fallbackExtracted = ExtractJsonBlock(repairedJson);
                return DeserializeAndValidate<T>(fallbackExtracted);
            }
            catch (Exception repairEx)
            {
                throw new RimLLMException(
                    LLMError.InvalidResponse,
                    $"Unable to parse LLM response to target object {typeof(T).Name}. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}. Parse error: {RimLLMLog.SanitizeForLog(ex.Message, 200)}. Static repair error: {RimLLMLog.SanitizeForLog(repairEx.Message, 200)}",
                    repairEx);
            }
        }
    }

    /// <summary>
    /// 修復不完整的 JSON 字串，例如移除 <c>&lt;think&gt;</c> 標籤、Markdown 語法、多餘的尾隨逗號以及補齊括號。
    /// </summary>
    public static string RepairJson(string json)
    {
        if (string.IsNullOrEmpty(json))
            return json;

        json = json.Trim();
        // 只移除 JSON 字串外的思考區塊，避免改寫模型真正輸出的文字。
        json = RemoveThinkBlocksOutsideStrings(json).Trim();
        json = StripMarkdownCodeBlocks(json);
        json = RemoveTrailingCommasOutsideStrings(json);

        if (!TryCollectExpectedClosers(json, out var expectedClosers, out bool inString, out bool escapeNext))
        {
            return json;
        }

        return CompleteTruncatedJson(json, expectedClosers, inString, escapeNext);
    }

    /// <summary>移除開頭與結尾的 Markdown 程式碼區塊標記。</summary>
    private static string StripMarkdownCodeBlocks(string json)
    {
        if (!json.StartsWith("```", StringComparison.Ordinal) &&
            !json.StartsWith('{') &&
            !json.StartsWith('['))
        {
            int fenceStart = json.IndexOf("```", StringComparison.Ordinal);
            if (fenceStart >= 0)
            {
                int lineEnd = json.IndexOf('\n', fenceStart);
                int contentStart = lineEnd >= 0 ? lineEnd + 1 : fenceStart + 3;
                int fenceEnd = json.IndexOf("```", contentStart, StringComparison.Ordinal);
                json = fenceEnd >= 0 ? json.Substring(contentStart, fenceEnd - contentStart) : json.Substring(contentStart);
                return json.Trim();
            }
        }

        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            int startIndex = json.IndexOf('\n', StringComparison.Ordinal);
            json = startIndex != -1 ? json.Substring(startIndex + 1) : json.Substring(3);
        }
        if (json.EndsWith("```", StringComparison.Ordinal))
        {
            json = json.Substring(0, json.Length - 3);
        }
        return json.Trim();
    }

    /// <summary>掃描 JSON 括號與引號狀態，回傳待閉合的右括號堆疊。若結構損毀（括號錯配）回傳 false。</summary>
    private static bool TryCollectExpectedClosers(
        string json,
        out List<char> expectedClosers,
        out bool inString,
        out bool escapeNext)
    {
        expectedClosers = [];
        inString = false;
        escapeNext = false;

        foreach (char c in json)
        {
            if (inString)
            {
                StepStringState(c, ref inString, ref escapeNext);
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (!HandleCloserChar(c, expectedClosers))
            {
                return false;
            }
        }

        return true;
    }

    private static void StepStringState(char c, ref bool inString, ref bool escapeNext)
    {
        if (escapeNext)
        {
            escapeNext = false;
        }
        else if (c == '\\')
        {
            escapeNext = true;
        }
        else if (c == '"')
        {
            inString = false;
        }
    }

    private static bool HandleCloserChar(char c, List<char> expectedClosers)
    {
        if (c == '{')
        {
            expectedClosers.Add('}');
        }
        else if (c == '[')
        {
            expectedClosers.Add(']');
        }
        else if (c == '}' || c == ']')
        {
            if (expectedClosers.Count == 0 || expectedClosers[expectedClosers.Count - 1] != c)
            {
                return false;
            }
            expectedClosers.RemoveAt(expectedClosers.Count - 1);
        }
        return true;
    }

    /// <summary>補齊未閉合的字串引號、清理懸空 token 並按 LIFO 順序補齊右括號。</summary>
    private static string CompleteTruncatedJson(
        string json,
        List<char> expectedClosers,
        bool inString,
        bool escapeNext)
    {
        if (inString)
        {
            // 未配對的跳脫符號會吃掉新補的引號，先補一個反斜線讓字串正確閉合。
            json += escapeNext ? "\\\"" : "\"";
        }

        // 清掉截斷造成的懸空 token，避免補完括號後仍無法解析。
        json = json.TrimEnd(' ', '\t', '\r', '\n');
        if (json.EndsWith(':'))
        {
            json += "null";
        }
        else if (json.EndsWith(','))
        {
            json = json.Substring(0, json.Length - 1);
        }

        var sb = new StringBuilder(json);
        for (int i = expectedClosers.Count - 1; i >= 0; i--)
        {
            sb.Append(expectedClosers[i]);
        }
        return sb.ToString();
    }

    private static string RemoveThinkBlocksOutsideStrings(string json)
    {
        var result = new StringBuilder(json.Length);
        bool inString = false;
        bool escapeNext = false;

        for (int index = 0; index < json.Length;)
        {
            char current = json[index];
            if (inString)
            {
                index = AppendStringChar(result, index, current, ref escapeNext, ref inString);
                continue;
            }

            if (current == '"')
            {
                inString = true;
                result.Append(current);
                index++;
                continue;
            }

            if (TrySkipThinkBlock(json, index, out int nextIndex))
            {
                index = nextIndex;
                continue;
            }

            result.Append(current);
            index++;
        }

        return result.ToString();
    }

    private static int AppendStringChar(StringBuilder result, int index, char current, ref bool escapeNext, ref bool inString)
    {
        result.Append(current);
        if (escapeNext)
        {
            escapeNext = false;
        }
        else if (current == '\\')
        {
            escapeNext = true;
        }
        else if (current == '"')
        {
            // 字串結束：下一個字元起回到非字串分支，區塊標記與逗號清理才不會誤判。
            inString = false;
        }
        return index + 1;
    }

    private static bool TrySkipThinkBlock(string json, int index, out int nextIndex)
    {
        const string thinkStart = "<think>";
        const string thinkEnd = "</think>";
        if (index + thinkStart.Length <= json.Length &&
            string.CompareOrdinal(json, index, thinkStart, 0, thinkStart.Length) == 0)
        {
            int end = json.IndexOf(thinkEnd, index + thinkStart.Length, StringComparison.Ordinal);
            if (end >= 0)
            {
                nextIndex = end + thinkEnd.Length;
                return true;
            }
        }
        nextIndex = index;
        return false;
    }

    private static bool IsTrailingComma(string json, int index)
    {
        int next = index + 1;
        while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
        return next < json.Length && (json[next] == '}' || json[next] == ']');
    }

    private static string RemoveTrailingCommasOutsideStrings(string json)
    {
        var result = new StringBuilder(json.Length);
        bool inString = false;
        bool escapeNext = false;

        for (int index = 0; index < json.Length;)
        {
            char current = json[index];
            if (inString)
            {
                index = AppendStringChar(result, index, current, ref escapeNext, ref inString);
                continue;
            }

            if (current == '"')
            {
                inString = true;
                result.Append(current);
                index++;
                continue;
            }

            if (current == ',' && IsTrailingComma(json, index))
            {
                index++;
                continue;
            }

            result.Append(current);
            index++;
        }

        return result.ToString();
    }

    /// <summary>
    /// 提取字串中的 JSON 區塊（第一個完整配對的 { ... } 或 [ ... ]）。
    /// </summary>
    public static string ExtractJsonBlock(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        int start = -1;
        char open = '\0';
        char close = '\0';
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '{')
            {
                start = i;
                open = '{';
                close = '}';
                break;
            }
            if (input[i] == '[')
            {
                start = i;
                open = '[';
                close = ']';
                break;
            }
        }
        if (start < 0) return input;

        int depth = 0;
        bool inString = false;
        bool escapeNext = false;
        for (int i = start; i < input.Length; i++)
        {
            char c = input[i];
            if (inString)
            {
                StepStringState(c, ref inString, ref escapeNext);
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return input.Substring(start, i - start + 1);
                }
            }
        }

        return input.Substring(start);
    }

    private static object? CreateDummyInstance(Type type)
    {
        return CreateDummyInstance(type, []);
    }

    private static object? CreateDummyInstance(Type type, HashSet<Type> visitedTypes)
    {
        if (TryCreateScalarDummy(type, out object? scalar)) return scalar;

        // 避免循環引用導致 StackOverflow
        if (visitedTypes.Contains(type))
        {
            return null;
        }
        visitedTypes.Add(type);

        try
        {
            if (TryCreateCollectionDummy(type, visitedTypes, out object? collection)) return collection;

            object? instance;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch
            {
                // 若無無參數建構子，建立不執行建構子的預設實例。
                instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            }

            if (instance != null)
            {
                PopulateDummyMembers(instance, type, visitedTypes);
            }
            return instance;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryCreateScalarDummy(Type type, out object? dummy)
    {
        if (type == typeof(string)) { dummy = "string"; return true; }
        if (type == typeof(int)) { dummy = 0; return true; }
        if (type == typeof(long)) { dummy = 0L; return true; }
        if (type == typeof(short)) { dummy = (short)0; return true; }
        if (type == typeof(byte)) { dummy = (byte)0; return true; }
        if (type == typeof(sbyte)) { dummy = (sbyte)0; return true; }
        if (type == typeof(ushort)) { dummy = (ushort)0; return true; }
        if (type == typeof(uint)) { dummy = 0u; return true; }
        if (type == typeof(ulong)) { dummy = 0UL; return true; }
        if (type == typeof(float)) { dummy = 0f; return true; }
        if (type == typeof(double)) { dummy = 0.0; return true; }
        if (type == typeof(decimal)) { dummy = 0m; return true; }
        if (type == typeof(bool)) { dummy = false; return true; }
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            dummy = values.Length > 0 ? values.GetValue(0) : 0;
            return true;
        }
        dummy = null;
        return false;
    }

    private static bool TryCreateCollectionDummy(Type type, HashSet<Type> visitedTypes, out object? dummy)
    {
        if (type.IsArray)
        {
            var elementType = type.GetElementType() ?? typeof(object);
            var array = Array.CreateInstance(elementType, 1);
            array.SetValue(CreateDummyInstance(elementType, new HashSet<Type>(visitedTypes)), 0);
            dummy = array;
            return true;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = type.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = Activator.CreateInstance(listType) as System.Collections.IList;
            if (list != null)
            {
                list.Add(CreateDummyInstance(elementType, new HashSet<Type>(visitedTypes)));
            }
            dummy = list;
            return true;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var keyType = type.GetGenericArguments()[0];
            var valueType = type.GetGenericArguments()[1];
            var dictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
            var dict = Activator.CreateInstance(dictType) as System.Collections.IDictionary;
            if (dict != null)
            {
                var dummyKey = CreateDummyInstance(keyType, new HashSet<Type>(visitedTypes));
                var dummyVal = CreateDummyInstance(valueType, new HashSet<Type>(visitedTypes));
                if (dummyKey != null)
                {
                    dict.Add(dummyKey, dummyVal);
                }
            }
            dummy = dict;
            return true;
        }
        dummy = null;
        return false;
    }

    private static void PopulateDummyMembers(object instance, Type type, HashSet<Type> visitedTypes)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            try
            {
                field.SetValue(instance, CreateDummyInstance(field.FieldType, new HashSet<Type>(visitedTypes)));
            }
            catch { RimLLMLog.Warning($"[RimLLM] Dummy instance field skipped: {field.Name}"); }
        }
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.CanWrite)
            {
                try
                {
                    prop.SetValue(instance, CreateDummyInstance(prop.PropertyType, new HashSet<Type>(visitedTypes)), index: null);
                }
                catch { RimLLMLog.Warning($"[RimLLM] Dummy instance property skipped: {prop.Name}"); }
            }
        }
    }

    /// <summary>
    /// 反序列化 JSON 字串為指定型別並驗證所有必填欄位。
    /// </summary>
    public static T DeserializeAndValidate<T>(string json)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        ValidateRequiredJsonMembers(document.RootElement, RimLLMSchemaBuilder.Build(typeof(T)).Element);

        T? result = Deserialize<T>(json);
        if (result is null)
        {
            throw new InvalidOperationException("Structured response deserialized to null.");
        }
        return result;
    }

    /// <summary>
    /// 驗證反序列化後的物件及其子屬性/欄位皆不可為 null。
    /// </summary>
    public static void ValidateStructuredObject<T>(T result)
    {
        if (result is null)
        {
            throw new InvalidOperationException("Structured response deserialized to null.");
        }

        ValidateRequiredMembers(result, typeof(T), []);
    }

    /// <summary>
    /// 遞迴驗證結構化物件。以「目前遞迴路徑」追蹤型別，而非「整次驗證已看過」：
    /// <paramref name="visitedTypes"/> 只包含目前正在下潛的祖先型別，離開某型別時移除。
    /// 這樣同一型別的多個實例（例如 <c>List&lt;Item&gt;</c> 的第二筆資料）都會完整驗證，
    /// 同時仍能防止循環引用造成 StackOverflow。
    /// </summary>
    public static void ValidateRequiredMembers(object value, Type type, HashSet<Type> visitedTypes)
    {
        if (value == null || type == null) return;
        visitedTypes ??= new HashSet<Type>();

        if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal)) return;
        if (!visitedTypes.Add(type)) return;

        try
        {
            JsonTypeInfo typeInfo = SharedTypeInfoResolver.GetTypeInfo(type, SharedOptions);
            if (typeInfo.Kind == JsonTypeInfoKind.Dictionary)
            {
                ValidateDictionaryMembers(value, typeInfo, visitedTypes, type);
                return;
            }

            if (typeInfo.Kind == JsonTypeInfoKind.Enumerable)
            {
                ValidateEnumerableMembers(value, typeInfo, visitedTypes, type);
                return;
            }

            if (typeInfo.Kind == JsonTypeInfoKind.Object)
            {
                ValidateObjectProperties(value, typeInfo, visitedTypes);
            }
        }
        finally
        {
            visitedTypes.Remove(type);
        }
    }

    private static void ValidateDictionaryMembers(object value, JsonTypeInfo typeInfo, HashSet<Type> visitedTypes, Type type)
    {
        foreach (object? item in EnumerateDictionaryValues(value))
        {
            ValidateRuntimeMember(
                item,
                typeInfo.ElementType,
                visitedTypes,
                $"Required structured response dictionary value is null (dictionary type '{type.Name}').");
        }
    }

    private static void ValidateEnumerableMembers(object value, JsonTypeInfo typeInfo, HashSet<Type> visitedTypes, Type type)
    {
        if (value is IEnumerable collection)
        {
            foreach (object item in collection)
            {
                ValidateRuntimeMember(
                    item,
                    typeInfo.ElementType,
                    visitedTypes,
                    $"Required structured response collection element is null (collection type '{type.Name}').");
            }
        }
    }

    private static void ValidateObjectProperties(object value, JsonTypeInfo typeInfo, HashSet<Type> visitedTypes)
    {
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (property.IsExtensionData || property.Set == null || property.Get == null) continue;
            if (property.AttributeProvider is FieldInfo field && field.IsInitOnly) continue;

            ValidateRuntimeMember(
                property.Get(value),
                property.PropertyType,
                visitedTypes,
                $"Required structured response member '{property.Name}' is null.");
        }
    }

    #pragma warning disable S3267 // JsonElement.ObjectEnumerator 不實現 IEnumerable，無法直接套用 LINQ Where/FirstOrDefault。
    private static void ValidateRequiredJsonMembers(JsonElement value, JsonElement schema)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!SchemaAllowsNull(schema))
            {
                throw new InvalidOperationException("Required structured response value is null.");
            }
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object) return;

        if (ValidateObjectSchema(value, schema)) return;
        if (ValidateArraySchema(value, schema)) return;
        ValidateDictionarySchema(value, schema);
    }

    private static bool ValidateObjectSchema(JsonElement value, JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out JsonElement properties) ||
            properties.ValueKind != JsonValueKind.Object ||
            value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (schema.TryGetProperty("required", out JsonElement required) && required.ValueKind == JsonValueKind.Array)
        {
            ValidateRequiredObjectProperties(value, properties, required);
        }

        if (schema.TryGetProperty("additionalProperties", out JsonElement additionalProperties) &&
            additionalProperties.ValueKind == JsonValueKind.Object)
        {
            ValidateAdditionalProperties(value, properties, additionalProperties);
        }
        return true;
    }

    private static bool ValidateArraySchema(JsonElement value, JsonElement schema)
    {
        if (!schema.TryGetProperty("items", out JsonElement items) || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            ValidateRequiredJsonMembers(item, items);
        }
        return true;
    }

    private static void ValidateDictionarySchema(JsonElement value, JsonElement schema)
    {
        if (!schema.TryGetProperty("additionalProperties", out JsonElement valueSchema) ||
            valueSchema.ValueKind != JsonValueKind.Object ||
            value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty entry in value.EnumerateObject())
        {
            ValidateRequiredJsonMembers(entry.Value, valueSchema);
        }
    }

    private static bool TryFindProperty(JsonElement value, string name, out JsonElement memberValue)
    {
        bool found = false;
        memberValue = default;
        foreach (JsonProperty inputProperty in value.EnumerateObject())
        {
            if (string.Equals(inputProperty.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                memberValue = inputProperty.Value;
            }
        }
        return found;
    }

    private static void ValidateRequiredObjectProperties(JsonElement value, JsonElement properties, JsonElement required)
    {
        foreach (JsonElement requiredName in required.EnumerateArray())
        {
            string name = requiredName.GetString() ?? string.Empty;
            if (!TryFindProperty(value, name, out JsonElement memberValue))
            {
                throw new InvalidOperationException(
                    $"Required structured response member '{name}' is missing.");
            }

            if (properties.TryGetProperty(name, out JsonElement memberSchema))
            {
                ValidateRequiredJsonMembers(memberValue, memberSchema);
            }
        }
    }

    private static void ValidateAdditionalProperties(JsonElement value, JsonElement properties, JsonElement additionalProperties)
    {
        foreach (JsonProperty inputProperty in value.EnumerateObject())
        {
            if (!properties.TryGetProperty(inputProperty.Name, out _))
            {
                ValidateRequiredJsonMembers(inputProperty.Value, additionalProperties);
            }
        }
    }
    #pragma warning restore S3267

    private static void ValidateRuntimeMember(
        object? value,
        Type? type,
        HashSet<Type> visitedTypes,
        string nullErrorMessage)
    {
        if (value == null)
        {
            if (type != null && Nullable.GetUnderlyingType(type) != null) return;
            throw new InvalidOperationException(nullErrorMessage);
        }

        ValidateRequiredMembers(value, type ?? value.GetType(), visitedTypes);
    }

    private static IEnumerable<object?> EnumerateDictionaryValues(object value)
    {
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary) yield return entry.Value;
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (object entry in enumerable)
            {
                PropertyInfo? valueProperty = entry?.GetType().GetProperty("Value");
                if (valueProperty != null) yield return valueProperty.GetValue(entry, index: null);
            }
        }
    }

    private static bool SchemaAllowsNull(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type)) return false;
        if (type.ValueKind == JsonValueKind.String)
        {
            return string.Equals(type.GetString(), "null", StringComparison.Ordinal);
        }
        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement candidate in type.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String &&
                    string.Equals(candidate.GetString(), "null", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
