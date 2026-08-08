using System.Collections.Generic;

namespace RimLLM_Framework.SDK
{
    /// <summary>
    /// 內建供應商識別碼常數。
    /// 集中定義以避免魔法字串散落各處；第三方 Mod 亦可引用。
    /// </summary>
    public static class ProviderIds
    {
        public const string OpenAI = "OpenAI";
        public const string Gemini = "Gemini";
        public const string OpenAICompatible = "OpenAICompatible";
        public const string DeepSeek = "DeepSeek";
        public const string Groq = "Groq";
        public const string Grok = "Grok";
        public const string OpenRouter = "OpenRouter";
        public const string Kimi = "Kimi";
        public const string MiniMax = "MiniMax";
        public const string Qwen = "Qwen";
        public const string Nvidia = "Nvidia";
        public const string Zai = "Z.ai";

        /// <summary>
        /// 全部內建供應商，順序即為設定 UI 與診斷輸出的顯示順序。
        /// SDK 尚未初始化時，UI 以此清單作為後備。
        /// </summary>
        public static readonly IReadOnlyList<string> BuiltIn = new List<string>
        {
            Gemini, OpenAI, DeepSeek, Groq, Grok, Zai,
            OpenRouter, Kimi, MiniMax, Qwen, Nvidia, OpenAICompatible
        };

        /// <summary>
        /// 需要「中國端點」切換的供應商（各自有中國／國際兩組網域）。
        /// </summary>
        public static bool HasChinaEndpoint(string providerId)
        {
            return providerId == MiniMax || providerId == Qwen || providerId == Kimi;
        }

        /// <summary>
        /// 從 Fallback Chain 條目取出供應商識別碼。
        /// 條目格式為 "ProviderId" 或 "ProviderId:ModelName"；空字串回傳 null。
        /// </summary>
        public static string ParseProviderId(string fallbackEntry)
        {
            if (string.IsNullOrEmpty(fallbackEntry)) return null;

            int colonIndex = fallbackEntry.IndexOf(':');
            return colonIndex > 0 ? fallbackEntry.Substring(0, colonIndex) : fallbackEntry;
        }
    }
}
