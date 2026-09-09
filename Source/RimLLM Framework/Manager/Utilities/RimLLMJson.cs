using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// 全框架共用的 JSON 序列化設定與薄包裝。
    /// Newtonsoft 移除後唯一的 JSON 引擎是 System.Text.Json，所有生產路徑
    /// （結構化輸出反序列化、設定／遙測 DTO、sample JSON）都經由此處，寬容度只定義一次。
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    internal static class RimLLMJson
    {
        private static readonly JsonSerializerOptions SharedOptions = CreateSharedOptions();

        private static JsonSerializerOptions CreateSharedOptions()
        {
            var options = new JsonSerializerOptions
            {
                // DTO 大量使用 public field，STJ 預設只看 property。
                IncludeFields = true,
                // 對齊 Newtonsoft 預設：反序列化時成員名大小寫不敏感。
                PropertyNameCaseInsensitive = true,
                // 對齊 Newtonsoft 的寬容：數字可由字串讀入、尾隨逗號與註解略過（LLM 輸出常見）。
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            };
            // schema 產生器以字串名稱表達列舉，反序列化必須接受名稱（與 Newtonsoft 預設一致）。
            // 否則 LLM 照 schema 回傳 "Kind":"Alpha" 會拋 JsonException，且該例外被歸類為不可重試。
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        /// <summary>共用設定。JsonSerializerOptions 建構後即凍結語意，可多執行緒共用。</summary>
        public static JsonSerializerOptions Options => SharedOptions;

        /// <summary>以執行期型別序列化（DTO 可能是 object 宣告，泛型推斷會丟失成員）。</summary>
        public static string Serialize(object value)
        {
            if (value == null) return "null";
            return JsonSerializer.Serialize(value, value.GetType(), SharedOptions);
        }

        public static string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value, SharedOptions);
        }

        public static T Deserialize<T>(string json)
        {
            return JsonSerializer.Deserialize<T>(json, SharedOptions);
        }
    }
#pragma warning restore S101
}
