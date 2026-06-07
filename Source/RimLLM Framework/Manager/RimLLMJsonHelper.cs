using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
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
        /// 修復不完整的 JSON 字串，例如移除 `<think>` 標籤、Markdown 語法、多餘的尾隨逗號以及補齊括號。
        /// </summary>
        public static string RepairJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return json;

            json = json.Trim();

            // 0. 剝離 <think>...</think> 標籤及其內容，以避免結構化 JSON 解析失敗
            json = Regex.Replace(json, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();

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

            // 3. 補齊缺失括號 (跳過雙引號字串內部的字元)
            int braceCount = 0;
            int bracketCount = 0;
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
                if (!inString)
                {
                    if (c == '{') braceCount++;
                    else if (c == '}') braceCount--;
                    else if (c == '[') bracketCount++;
                    else if (c == ']') bracketCount--;
                }
            }

            if (braceCount > 0)
            {
                json += new string('}', braceCount);
            }
            if (bracketCount > 0)
            {
                json += new string(']', bracketCount);
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
