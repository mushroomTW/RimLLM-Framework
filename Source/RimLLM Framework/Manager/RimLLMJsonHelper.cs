using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// JSON 的靜態處理與格式修復輔助工具。
    /// 包含結構化資料的 JSON 補齊、Regex 修復以及 Dummy 物件生成（用於產生 schema 快取）。
    /// </summary>
    public static class RimLLMJsonHelper
    {
        private static readonly Regex TrailingCommaRegex = new Regex(@",\s*([\]}])", RegexOptions.Compiled);
        private static readonly Regex JsonBlockRegex = new Regex(@"(\{.*\}|\[.*\])", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex ThinkTagRegex = new Regex(@"<think>.*?</think>", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly ConcurrentDictionary<Type, string> SampleJsonCache = new ConcurrentDictionary<Type, string>();
        private static readonly ConcurrentDictionary<string, JObject> SchemaCache = new ConcurrentDictionary<string, JObject>();

        /// <summary>
        /// Schema 遞迴的最大深度。超過此深度的巢狀成員會被略過，避免病態型別造成堆疊耗盡。
        /// </summary>
        private const int MaxSchemaDepth = 8;

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
            if (SampleJsonCache.TryGetValue(type, out string json))
            {
                return json;
            }

            try
            {
                object instance = CreateDummyInstance(type);
                string generatedJson = JsonConvert.SerializeObject(instance, Formatting.None);
                SampleJsonCache[type] = generatedJson;
                return generatedJson;
            }
            catch
            {
                return "{}";
            }
        }

        /// <summary>
        /// 遞迴使用反射將 C# 型別轉換為 JObject 代表的 JSON Schema。
        /// 結果會被快取，回傳的一律是深拷貝，呼叫端可自由修改而不影響快取。
        /// </summary>
        public static JObject GenerateJsonSchema(Type type, bool uppercaseTypes = false)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            string cacheKey = type.AssemblyQualifiedName + "|" + uppercaseTypes;
            if (!SchemaCache.TryGetValue(cacheKey, out JObject cached))
            {
                cached = BuildSchema(type, uppercaseTypes, new HashSet<Type>(), 0)
                         ?? CreateEmptyObjectSchema(uppercaseTypes);
                SchemaCache[cacheKey] = cached;
            }

            return (JObject)cached.DeepClone();
        }

        /// <summary>
        /// 判斷型別樹中是否含有開放式 map（由 Dictionary 產生的 additionalProperties schema）。
        /// OpenAI 的 strict structured output 不接受這種形狀，呼叫端需據此關閉 strict 或改走提示式 JSON。
        /// </summary>
        public static bool ContainsOpenEndedMap(Type type)
        {
            if (type == null) return false;
            return ContainsOpenEndedMapCore(type, new HashSet<Type>(), 0);
        }

        private static bool ContainsOpenEndedMapCore(Type type, HashSet<Type> visited, int depth)
        {
            if (type == null || depth > MaxSchemaDepth) return false;

            Type underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return ContainsOpenEndedMapCore(underlying, visited, depth);

            if (IsSupportedDictionary(type, out _, out _)) return true;

            Type elementType = GetSequenceElementType(type);
            if (elementType != null) return ContainsOpenEndedMapCore(elementType, visited, depth + 1);

            if (IsScalarType(type)) return false;

            if (!visited.Add(type)) return false;
            try
            {
                foreach (Type memberType in EnumerateSerializableMemberTypes(type))
                {
                    if (ContainsOpenEndedMapCore(memberType, visited, depth + 1)) return true;
                }
            }
            finally
            {
                visited.Remove(type);
            }

            return false;
        }

        /// <summary>
        /// Schema 產生的遞迴核心。
        /// <paramref name="visited"/> 追蹤目前遞迴路徑上的型別，偵測到循環時回傳 null，
        /// 由父層略過該成員（與 CreateDummyInstance 把循環欄位截斷為 null 的行為一致）。
        /// </summary>
        private static JObject BuildSchema(Type type, bool uppercaseTypes, HashSet<Type> visited, int depth)
        {
            if (type == null || depth > MaxSchemaDepth)
            {
                return null;
            }

            // Nullable<T> 一律以底層型別產生 schema；父層負責不將其列入 required。
            Type underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType != null)
            {
                return BuildSchema(underlyingType, uppercaseTypes, visited, depth);
            }

            var schema = new JObject();
            string typeStr;

            if (type == typeof(string) || type == typeof(char))
            {
                typeStr = uppercaseTypes ? "STRING" : "string";
                schema["type"] = typeStr;
            }
            else if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
                     type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
            {
                typeStr = uppercaseTypes ? "INTEGER" : "integer";
                schema["type"] = typeStr;
            }
            else if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                typeStr = uppercaseTypes ? "NUMBER" : "number";
                schema["type"] = typeStr;
            }
            else if (type == typeof(bool))
            {
                typeStr = uppercaseTypes ? "BOOLEAN" : "boolean";
                schema["type"] = typeStr;
            }
            else if (type.IsEnum)
            {
                typeStr = uppercaseTypes ? "STRING" : "string";
                schema["type"] = typeStr;
                var names = new JArray();
                foreach (var name in Enum.GetNames(type))
                {
                    names.Add(name);
                }
                schema["enum"] = names;
            }
            // Dictionary 需以開放式 map 表示，否則反射會落入自訂物件分支而產生空 properties。
            // 必須排在集合分支之前：Dictionary<,> 同時也實作 ICollection<KeyValuePair<,>>。
            else if (IsSupportedDictionary(type, out Type keyType, out Type valueType))
            {
                typeStr = uppercaseTypes ? "OBJECT" : "object";
                schema["type"] = typeStr;

                // JSON 物件的鍵一律是字串，因此只有 string 或 enum 鍵能忠實表示成 map。
                if (keyType == typeof(string) || keyType.IsEnum)
                {
                    JObject valueSchema = BuildSchema(valueType, uppercaseTypes, visited, depth + 1);
                    if (valueSchema != null)
                    {
                        schema["additionalProperties"] = valueSchema;
                    }
                }
            }
            else if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>) ||
                                            type.GetGenericTypeDefinition() == typeof(IList<>) ||
                                            type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ||
                                            type.GetGenericTypeDefinition() == typeof(ICollection<>)))
            {
                typeStr = uppercaseTypes ? "ARRAY" : "array";
                schema["type"] = typeStr;
                Type itemType = type.GetGenericArguments()[0];
                JObject itemSchema = BuildSchema(itemType, uppercaseTypes, visited, depth + 1);
                if (itemSchema == null) return null;
                schema["items"] = itemSchema;
            }
            else if (type.IsArray)
            {
                typeStr = uppercaseTypes ? "ARRAY" : "array";
                schema["type"] = typeStr;
                Type itemType = type.GetElementType();
                JObject itemSchema = BuildSchema(itemType, uppercaseTypes, visited, depth + 1);
                if (itemSchema == null) return null;
                schema["items"] = itemSchema;
            }
            else // 自定義物件
            {
                // 循環引用偵測：若該型別已在目前遞迴路徑上，回傳 null 讓父層略過此成員。
                if (!visited.Add(type))
                {
                    return null;
                }

                try
                {
                    typeStr = uppercaseTypes ? "OBJECT" : "object";
                    schema["type"] = typeStr;
                    var properties = new JObject();
                    var required = new JArray();

                    // 獲取所有公開屬性
                    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (prop.CanRead && prop.CanWrite && prop.GetIndexParameters().Length == 0)
                        {
                            JObject propSchema = BuildSchema(prop.PropertyType, uppercaseTypes, visited, depth + 1);
                            if (propSchema == null) continue;

                            properties[prop.Name] = propSchema;
                            if (!IsOptionalMember(prop.PropertyType))
                            {
                                required.Add(prop.Name);
                            }
                        }
                    }

                    // 獲取所有公開欄位
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!field.IsLiteral && !field.IsInitOnly)
                        {
                            JObject fieldSchema = BuildSchema(field.FieldType, uppercaseTypes, visited, depth + 1);
                            if (fieldSchema == null) continue;

                            properties[field.Name] = fieldSchema;
                            if (!IsOptionalMember(field.FieldType))
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

        private static JObject CreateEmptyObjectSchema(bool uppercaseTypes)
        {
            return new JObject
            {
                ["type"] = uppercaseTypes ? "OBJECT" : "object",
                ["properties"] = new JObject(),
                ["required"] = new JArray(),
                ["additionalProperties"] = false
            };
        }

        /// <summary>
        /// Nullable&lt;T&gt; 成員視為選填，不列入 required。
        /// </summary>
        private static bool IsOptionalMember(Type memberType)
        {
            return Nullable.GetUnderlyingType(memberType) != null;
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

        private static bool IsScalarType(Type type)
        {
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);
        }

        private static IEnumerable<Type> EnumerateSerializableMemberTypes(Type type)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.CanRead && prop.CanWrite && prop.GetIndexParameters().Length == 0)
                {
                    yield return prop.PropertyType;
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!field.IsLiteral && !field.IsInitOnly)
                {
                    yield return field.FieldType;
                }
            }
        }

        /// <summary>
        /// 修復不完整的 JSON 字串，例如移除 `<think>` 標籤、Markdown 語法、多餘的尾隨逗號以及補齊括號。
        /// </summary>
        public static string RepairJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return json;

            json = json.Trim();

            // 0. 剝離 <think>...</think> 標籤及其內容，以避免結構化 JSON 解析失敗
            json = ThinkTagRegex.Replace(json, "").Trim();

            // 1. 移除 Markdown 標記
            if (json.StartsWith("```"))
            {
                int startIndex = json.IndexOf('\n');
                if (startIndex != -1)
                {
                    json = json.Substring(startIndex + 1);
                }
                else
                {
                    json = json.Substring(3);
                }
            }
            if (json.EndsWith("```"))
            {
                json = json.Substring(0, json.Length - 3);
            }
            json = json.Trim();

            // 2. 移除尾隨逗號 (使用編譯後的靜態 Regex 提效)
            json = TrailingCommaRegex.Replace(json, "$1");

            // 3. 補齊缺失括號 (跳過雙引號字串內部的字元)。
            //    以堆疊記錄尚待閉合的符號，才能對交錯巢狀（如 {"a":[1) 產生正確的閉合順序；
            //    僅用計數會固定先補 } 再補 ]，對巢狀結構會產生無法解析的結果。
            //    此處刻意用 List<char> 而非 Stack<char>：RimWorld 的 Mono 執行環境無法從
            //    mscorlib facade 載入 Stack<T>，會擲出 TypeLoadException。
            var expectedClosers = new List<char>();
            bool inString = false;
            bool escapeNext = false;
            foreach (char c in json)
            {
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }
                if (c == '\\')
                {
                    escapeNext = true;
                    continue;
                }
                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }
                if (inString) continue;

                if (c == '{') expectedClosers.Add('}');
                else if (c == '[') expectedClosers.Add(']');
                else if (c == '}' || c == ']')
                {
                    // 閉合符號與堆疊頂端不符，代表結構本身已損毀而非單純截斷。
                    // 此時任何補齊都只會讓結果更糟，直接交給 ExtractJsonBlock 與 double repair 處理。
                    if (expectedClosers.Count == 0 || expectedClosers[expectedClosers.Count - 1] != c)
                    {
                        return json;
                    }
                    expectedClosers.RemoveAt(expectedClosers.Count - 1);
                }
            }

            // 3a. 字串在結尾處未閉合時先補上引號，否則後續補的括號會落在字串內部。
            if (inString)
            {
                json += "\"";
            }

            // 3b. 清掉截斷造成的懸空 token，避免補完括號後仍無法解析。
            //     明確列出空白字元：net472／Mono 沒有無參數的 String.TrimEnd() 多載。
            json = json.TrimEnd(' ', '\t', '\r', '\n');
            if (json.EndsWith(":"))
            {
                json += "null";
            }
            else if (json.EndsWith(","))
            {
                json = json.Substring(0, json.Length - 1);
            }

            // 3c. 依 LIFO 順序補齊
            for (int i = expectedClosers.Count - 1; i >= 0; i--)
            {
                json += expectedClosers[i];
            }

            return json;
        }

        /// <summary>
        /// 提取字串中的 JSON 區塊（第一個匹配的 { ... } 或 [ ... ]）。
        /// </summary>
        public static string ExtractJsonBlock(string input)
        {
            var match = JsonBlockRegex.Match(input);
            if (match.Success)
            {
                return match.Value;
            }
            return input;
        }

        private static object CreateDummyInstance(Type type)
        {
            return CreateDummyInstance(type, new HashSet<Type>());
        }

        private static object CreateDummyInstance(Type type, HashSet<Type> visitedTypes)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return 0;
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return 0.0;
            if (type == typeof(bool)) return false;
            if (type.IsEnum)
            {
                var values = Enum.GetValues(type);
                return values.Length > 0 ? values.GetValue(0) : 0;
            }

            // 避免循環引用導致 StackOverflow
            if (visitedTypes.Contains(type))
            {
                return null;
            }
            visitedTypes.Add(type);

            try
            {
                if (type.IsArray)
                {
                    var elementType = type.GetElementType();
                    var array = Array.CreateInstance(elementType, 1);
                    array.SetValue(CreateDummyInstance(elementType, new HashSet<Type>(visitedTypes)), 0);
                    return array;
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
                    return list;
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
                    return dict;
                }

                object instance = null;
                try
                {
                    instance = Activator.CreateInstance(type);
                }
                catch
                {
                    // 若無無參數建構子，使用 FormatterServices 進行安全實例化
                    instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
                }

                if (instance != null)
                {
                    // 遞迴填充公開欄位與屬性
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        try
                        {
                            field.SetValue(instance, CreateDummyInstance(field.FieldType, new HashSet<Type>(visitedTypes)));
                        }
                        catch { }
                    }
                    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (prop.CanWrite)
                        {
                            try
                            {
                                prop.SetValue(instance, CreateDummyInstance(prop.PropertyType, new HashSet<Type>(visitedTypes)), null);
                            }
                            catch { }
                        }
                    }
                }
                return instance;
            }
            catch
            {
                return null;
            }
        }
    }
}
