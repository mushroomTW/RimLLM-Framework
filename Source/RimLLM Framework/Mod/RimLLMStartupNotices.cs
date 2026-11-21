using System;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using RimWorld;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 設定在 Mod 建構子載入時，派遣器、Defs 與語言資料都還不能用，此時送出的通知會因翻譯失敗而遺失，
/// 因此先保留，等遊戲資料就緒再送。
/// </summary>
internal static class RimLLMStartupNotices
{
    private static readonly System.Threading.Lock Gate = new();
    private static readonly List<(string Key, string? Argument)> Pending = [];
    private static bool _ready;

    /// <summary>送出通知；遊戲資料尚未就緒時先保留，相同內容只保留一筆。</summary>
    public static void Post(string translationKey, string? argument = null)
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

    public static void Flush()
    {
        List<(string Key, string? Argument)> notices;
        lock (Gate)
        {
            _ready = true;
            notices = [.. Pending];
            Pending.Clear();
        }

        foreach ((string key, string? argument) in notices)
        {
            Show(key, argument);
        }
    }

    private static void Show(string translationKey, string? argument)
    {
        RimLLMDispatcher.EnqueueOnMainThread(() =>
        {
            try
            {
                TaggedString text = argument == null ? translationKey.Translate() : translationKey.Translate(argument);
                Messages.Message(text, MessageTypeDefOf.CautionInput, historical: false);
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"[RimLLM] Notice '{translationKey}' could not be shown: {ex.Message}");
            }
        });
    }

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
/// 在遊戲資料就緒後觸發 <see cref="RimLLMStartupNotices.Flush"/>。
/// 必須與通知類別分開：靜態建構子放在通知類別上，載入設定時第一次 Post 就會提前觸發。
/// </summary>
[StaticConstructorOnStartup]
[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
internal static class RimLLMStartupNoticeFlusher
{
    static RimLLMStartupNoticeFlusher()
    {
        RimLLMStartupNotices.Flush();
    }
}
