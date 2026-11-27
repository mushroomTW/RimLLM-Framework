namespace RimLLM_Framework.Core;

/// <summary>
/// 飽和整數運算：溢位時夾在上限，而不是繞回負數。用於 token 估算與預算預留的累加。
/// </summary>
internal static class SaturatingMath
{
    internal static long Add(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right) return long.MaxValue;
        if (right < 0 && left < long.MinValue - right) return long.MinValue;
        return left + right;
    }
}
