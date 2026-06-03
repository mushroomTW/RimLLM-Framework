using System;
using Verse;

namespace RimLLM_Framework.Core
{
    /// <summary>
    /// 日誌包裝器。提供安全且跨環境的日誌輸出。
    /// 在 RimWorld 遊戲環境中，呼叫 Verse.Log 輸出至遊戲日誌與控制台；
    /// 在無 Unity 引擎的單元測試環境中，自動 Fallback 使用 System.Console 輸出，防範 ECall 崩潰。
    /// </summary>
    public static class ArchotechLog
    {
        public static void Message(string msg)
        {
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
