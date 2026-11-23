namespace RimLLM_Framework;

internal static class ApiTimeouts
{
    // 非串流是整次請求的總時間，串流是閒置時間；推理模型首個 token 就要 10–17 秒，30 秒會誤殺長回覆。
    internal const float DefaultSeconds = 100f;

    /// <summary>舊版預設值；存檔裡剛好是這個值的視為從未調整過，載入時改用新預設。</summary>
    internal const float LegacyDefaultSeconds = 30f;

    internal static float Resolve(float configuredSeconds)
    {
        return configuredSeconds > 0f ? configuredSeconds : DefaultSeconds;
    }
}
