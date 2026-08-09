using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        /// 將 C# 型別轉換為 JObject 代表的 JSON Schema。
        /// </summary>
        [Obsolete("改用 RimLLMSchemaBuilder.Build(type, profile)。此多載將於下一版移除。", false)]
        public static JObject GenerateJsonSchema(Type type, bool uppercaseTypes = false)
        {
            return JObject.Parse(GenerateJsonSchemaString(type, uppercaseTypes));
        }

        /// <summary>
        /// 取得 JSON Schema 的字串形式。
        /// </summary>
        [Obsolete("改用 RimLLMSchemaBuilder.BuildJson(type, profile)。此多載將於下一版移除。", false)]
        public static string GenerateJsonSchemaString(Type type, bool uppercaseTypes = false)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            // uppercaseTypes 原本是為了 Gemini REST 的大寫 type 關鍵字而存在，但生產路徑從未使用它
            // （小寫同樣被 Schema.FromJson 接受）。這裡仍對映到 Gemini 方言以維持對外相容。
            RimLLMSchemaProfile profile = uppercaseTypes ? RimLLMSchemaProfile.Gemini : RimLLMSchemaProfile.OpenAI;
            string json = RimLLMSchemaBuilder.BuildJson(type, profile);
            if (!uppercaseTypes)
            {
                return json;
            }

            JObject schema = JObject.Parse(json);
            UppercaseTypeKeywords(schema);
            return schema.ToString();
        }

        private static void UppercaseTypeKeywords(JObject node)
        {
            if (node == null) return;

            JToken type = node["type"];
            if (type != null && type.Type == JTokenType.String)
            {
                node["type"] = type.Value<string>().ToUpperInvariant();
            }

            UppercaseTypeKeywords(node["items"] as JObject);
            UppercaseTypeKeywords(node["additionalProperties"] as JObject);

            var properties = node["properties"] as JObject;
            if (properties == null) return;

            foreach (KeyValuePair<string, JToken> property in properties)
            {
                UppercaseTypeKeywords(property.Value as JObject);
            }
        }

        /// <summary>
        /// 判斷型別是否會產生開放式 map（由 Dictionary 產生的 additionalProperties schema）。
        /// OpenAI 的 strict structured output 不接受這種形狀。
        /// </summary>
        [Obsolete("改用 RimLLMSchemaBuilder.ContainsOpenEndedMap(type)。此多載將於下一版移除。", false)]
        public static bool ContainsOpenEndedMap(Type type)
        {
            return RimLLMSchemaBuilder.ContainsOpenEndedMap(type);
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
