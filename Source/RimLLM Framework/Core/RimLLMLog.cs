using System;
using System.Text.RegularExpressions;
using Verse;

namespace RimLLM_Framework.Core
{
    /// <summary>
    /// 日誌包裝器。提供安全且跨環境的日誌輸出。
    /// 在 RimWorld 遊戲環境中，呼叫 Verse.Log 輸出至遊戲日誌與控制台；
    /// 在無 Unity 引擎的單元測試環境中，自動 Fallback 使用 System.Console 輸出，防範 ECall 崩潰。
    /// </summary>
    public static class RimLLMLog
    {
        public static bool Enabled { get; set; } = true;
        /// <summary>
        /// 敏感字串遮罩樣式。除了通用的 api_key／authorization 賦值形式外，
        /// 另涵蓋本框架 12 家供應商實際使用的金鑰前綴，避免漏遮。
        /// </summary>
        private static readonly Regex SecretPattern = new Regex(
            @"(?i)(" +
            // 賦值形式（含 x-api-key、x-goog-api-key 等 header 名稱）
            @"(?:x-goog-)?api[_\- ]?key\s*[:=]\s*[""']?[^""'\s,;}]+" +
            @"|x-api-key\s*[:=]\s*[""']?[^""'\s,;}]+" +
            // 需一併吃掉 "Bearer " 前綴，否則只會遮到 scheme 而讓後面的 token 露出。
            @"|authorization\s*[:=]\s*[""']?(?:Bearer\s+)?[^""'\s,;}]+" +
            @"|key=[^&\s]+" +
            // Bearer token
            @"|Bearer\s+[A-Za-z0-9\-._~+/]{16,}={0,2}" +
            // 各供應商的金鑰前綴
            @"|sk-ant-[A-Za-z0-9\-_]{16,}" +
            @"|sk-[a-z0-9_\-]{8,}" +
            @"|AIza[0-9A-Za-z\-_]{20,}" +
            @"|gsk_[A-Za-z0-9]{20,}" +
            @"|xai-[A-Za-z0-9]{20,}" +
            @"|nvapi-[A-Za-z0-9\-_]{20,}" +
            @")",
            RegexOptions.Compiled);

        public static string SanitizeForLog(string value, int maxLength = 500)
        {
            if (string.IsNullOrEmpty(value)) return value;

            string sanitized = SecretPattern.Replace(value, match =>
            {
                string text = match.Value;
                int idx = text.IndexOf('=');
                if (idx < 0) idx = text.IndexOf(':');
                if (idx > 0)
                {
                    return text.Substring(0, idx + 1) + "[redacted]";
                }
                return "[redacted-secret]";
            });

            sanitized = sanitized.Replace("\r", "\\r").Replace("\n", "\\n");
            if (sanitized.Length > maxLength)
            {
                sanitized = sanitized.Substring(0, maxLength) + "...";
            }
            return sanitized;
        }

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

        public static void Warning(string msg)
        {
            if (!Enabled) return;
            try
            {
                Log.Warning(msg);
            }
            catch
            {
                var orig = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[WARN] {msg}");
                Console.ForegroundColor = orig;
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
                var orig = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] {msg}");
                Console.ForegroundColor = orig;
            }
        }
    }
}
