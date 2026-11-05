using System;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using RimWorld;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 設定載入期間要告訴玩家的一次性通知。
/// </summary>
/// <remarks>
/// 設定在 Mod 建構子就載入，此時派遣器還沒建立，Defs 與語言資料也還不能用；
/// 這時送出的通知會同步執行、因翻譯失敗而遺失。因此先記下來，等遊戲資料就緒再送。
/// </remarks>
internal static class RimLLMStartupNotices
{
    private static readonly object Gate = new();
    private static readonly List<(string Key, string Argument)> Pending = [];
    private static bool _ready;

    /// <summary>送出通知；遊戲資料尚未就緒時先保留。相同內容只保留一筆。</summary>
    /// <param name="translationKey">翻譯鍵。</param>
    /// <param name="argument">翻譯參數 {0}；沒有時為 null。</param>
    public static void Post(string translationKey, string argument = null)
    {
        lock (Gate)
        {
            if (!_ready)
            {
                if (!Pending.Contains((translationKey, argument)))
                {
                    Pending.Add((translationKey, argument));
                }
                return;
            }
        }

        Show(translationKey, argument);
    }

    /// <summary>遊戲資料已就緒：送出保留中的通知，之後的通知直接送出。</summary>
    public static void Flush()
    {
        List<(string Key, string Argument)> notices;
        lock (Gate)
        {
            _ready = true;
            notices = [.. Pending];
            Pending.Clear();
        }

        foreach ((string key, string argument) in notices)
        {
            Show(key, argument);
        }
    }

    private static void Show(string translationKey, string argument)
    {
        RimLLMDispatcher.EnqueueOnMainThread(() =>
        {
            try
            {
                TaggedString text = argument == null ? translationKey.Translate() : translationKey.Translate(argument);
                Messages.Message(text, MessageTypeDefOf.CautionInput, false);
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"[RimLLM] Notice '{translationKey}' could not be shown: {ex.Message}");
            }
        });
    }

    /// <summary>測試用：目前保留中的通知數。</summary>
    internal static int PendingCountForTests
    {
        get
        {
            lock (Gate)
            {
                return Pending.Count;
            }
        }
    }

    /// <summary>測試用：還原為遊戲資料尚未就緒的狀態。</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _ready = false;
            Pending.Clear();
        }
    }
}

/// <summary>
/// 在遊戲資料就緒後觸發 <see cref="RimLLMStartupNotices.Flush"/>。必須與通知類別分開：
/// 靜態建構子放在通知類別上的話，載入設定時第一次 Post 就會提前觸發它。
/// </summary>
[StaticConstructorOnStartup]
internal static class RimLLMStartupNoticeFlusher
{
    static RimLLMStartupNoticeFlusher()
    {
        RimLLMStartupNotices.Flush();
    }
}
