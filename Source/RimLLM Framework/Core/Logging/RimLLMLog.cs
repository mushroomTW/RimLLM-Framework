using System;
using System.Text.RegularExpressions;
using Verse;

namespace RimLLM_Framework.Core;

/// <summary>
/// 日誌包裝器。提供安全且跨環境的日誌輸出。
/// 在 RimWorld 遊戲環境中，呼叫 Verse.Log 輸出至遊戲日誌與控制台；
/// 在無 Unity 引擎的單元測試環境中，自動 Fallback 使用 System.Console 輸出，防範 ECall 崩潰。
/// </summary>
public static partial class RimLLMLog
{
    /// <summary>是否啟用一般資訊（Message）等級的日誌輸出。</summary>
    public static bool Enabled { get; set; } = true;

    // 正則前快徑：每個指標必須是 SecretPattern 對應分支的必要子字串，否則該分支會被略過而漏遮。
    private static bool HasPotentialSecret(string value)
    {
        return value.Contains("api", StringComparison.OrdinalIgnoreCase)
            || value.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || value.Contains("key=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("bearer", StringComparison.OrdinalIgnoreCase)
            || value.Contains("sk-", StringComparison.OrdinalIgnoreCase)
            || value.Contains("AIza", StringComparison.OrdinalIgnoreCase)
            || value.Contains("gsk_", StringComparison.OrdinalIgnoreCase)
            || value.Contains("xai-", StringComparison.OrdinalIgnoreCase)
            || value.Contains("nvapi-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 對字串進行日誌安全清理：遮罩 API 金鑰與敏感憑證，轉義換行符號，並限制最大長度。
    /// </summary>
    /// <param name="value">原始字串。</param>
    /// <param name="maxLength">截斷上限字元數。</param>
    /// <returns>已清理並可安全記錄的字串。</returns>
    public static string? SanitizeForLog(string? value, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(value)) return value;

        string sanitized = HasPotentialSecret(value)
            ? SecretPattern.Replace(value, match =>
        {
            string text = match.Value;
            int idx = text.IndexOf('=', StringComparison.Ordinal);
            if (idx < 0) idx = text.IndexOf(':', StringComparison.Ordinal);
            if (idx > 0)
            {
                return string.Concat(text.AsSpan(0, idx + 1), "[redacted]");
            }
            return "[redacted-secret]";
        })
            : value;

        sanitized = sanitized.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        if (sanitized.Length > maxLength)
        {
            sanitized = string.Concat(sanitized.AsSpan(0, maxLength), "...");
        }
        return sanitized;
    }

    /// <summary>
    /// 輸出一般資訊日誌。僅在 <see cref="Enabled"/> 為 true 時輸出至遊戲主控台。
    /// </summary>
    /// <param name="msg">日誌訊息內容。</param>
    public static void Message(string msg)
    {
        if (!Enabled) return;
        try
        {
            Log.Message(msg);
        }
        catch
        {
            Console.WriteLine($"[INFO] {msg}");
        }
    }

    /// <summary>
    /// 輸出警告。不檢查 Enabled 旗標：警告多半是一次性的診斷線索（金鑰解不開、遙測寫檔失敗、
    /// schema exporter 降級），詳細日誌預設關閉時仍必須看得到。每次請求都會觸發的警告
    /// 由呼叫端自行以 <see cref="Enabled"/> 包住。
    /// </summary>
    public static void Warning(string msg)
    {
        try
        {
            Log.Warning(msg);
        }
        catch
        {
            WriteConsole(ConsoleColor.Yellow, "WARN", msg);
        }
    }

    /// <summary>
    /// 輸出錯誤日誌。故意不檢查 Enabled 旗標，因為嚴重錯誤與異常必須始終輸出並觸發遊戲控制台彈出，以利開發者與玩家發現問題。
    /// </summary>
    public static void Error(string msg)
    {
        try
        {
            Log.Error(msg);
        }
        catch
        {
            WriteConsole(ConsoleColor.Red, "ERROR", msg);
        }
    }

    private static void WriteConsole(ConsoleColor color, string level, string msg)
    {
        ConsoleColor original = Console.ForegroundColor;
        Console.ForegroundColor = color;
        try
        {
            Console.WriteLine($"[{level}] {msg}");
        }
        finally
        {
            Console.ForegroundColor = original;
        }
    }

    [GeneratedRegex(@"(?i)(" +
        @"(?:x-goog-)?api[_\- ]?key[""']?\s*[:=]\s*[""']?[^""'\s,;}]+" +
        @"|x-api-key[""']?\s*[:=]\s*[""']?[^""'\s,;}]+" +
        // 需一併吃掉 "Bearer " 前綴，否則只會遮到 scheme 而讓後面的 token 露出。
        @"|authorization[""']?\s*[:=]\s*[""']?(?:Bearer\s+)?[^""'\s,;}]+" +
        @"|key=[^&\s]+" +
        @"|Bearer\s+[A-Za-z0-9\-._~+/]{16,}={0,2}" +
        @"|sk-ant-[A-Za-z0-9\-_]{16,}" +
        @"|sk-[a-z0-9_\-]{8,}" +
        @"|AIza[0-9A-Za-z\-_]{20,}" +
        @"|gsk_[A-Za-z0-9]{20,}" +
        @"|xai-[A-Za-z0-9]{20,}" +
        @"|nvapi-[A-Za-z0-9\-_]{20,}" +
        @")", RegexOptions.Compiled | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretPattern { get; }
}
