namespace RimLLM_Framework.Core;

/// <summary>
/// 飽和整數運算：溢位時夾在上限，而不是繞回負數。用於 token 估算與預算預留的累加。
/// </summary>
internal static class SaturatingMath
{
    /// <summary>回傳 <paramref name="left"/> + <paramref name="right"/>，結果超過 <see cref="long.MaxValue"/> 時回傳上限。</summary>
    internal static long Add(long left, long right)
    {
        return right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
    }
}
