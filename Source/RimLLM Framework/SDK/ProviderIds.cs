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
        public const string Anthropic = "Anthropic";
        public const string OpenRouter = "OpenRouter";
        public const string Kimi = "Kimi";
        public const string MiniMax = "MiniMax";
        public const string Qwen = "Qwen";
        public const string Nvidia = "Nvidia";
    }
}
