namespace RimLLM_Framework;

internal static class ApiTimeouts
{
    // 非串流是整次請求的總時間，串流是閒置時間；推理模型首個 token 就要 10–17 秒，30 秒會誤殺長回覆。
    internal const float DefaultSeconds = 100f;

    internal const float MinSeconds = 5f;

    internal const float MaxSeconds = 120f;

    internal static float Resolve(float configuredSeconds)
    {
        return configuredSeconds > 0f ? configuredSeconds : DefaultSeconds;
    }
}
