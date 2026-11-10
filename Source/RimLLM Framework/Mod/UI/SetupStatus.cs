using System;
using System.Collections.Generic;

namespace RimLLM_Framework.Mod;

/// <summary>首頁與左側選單顯示的整體設定狀態。</summary>
internal enum SetupState
{
    /// <summary>備援鏈是空的：還沒連接任何 AI。</summary>
    NotConnected,
    /// <summary>備援鏈有條目，但沒有任何一個的供應商已啟用且具備金鑰。</summary>
    Broken,
    /// <summary>至少有一個條目能真的送出請求。</summary>
    Ready
}

/// <summary>
/// 判斷「AI 現在能不能用」。路由層只走備援鏈，鏈上沒有可用條目時任何請求都會失敗，
/// 因此這裡的判斷必須與 <see cref="RimLLM_Framework.Manager.Services.RimLLMFallbackPipeline"/>
/// 的候選資格一致：供應商已啟用，且需要金鑰時已填金鑰。
/// </summary>
internal static class SetupStatus
{
    /// <summary>
    /// 依備援鏈判斷設定狀態。<paramref name="firstUsableEntry"/> 為第一個可用的條目（供首頁顯示「目前使用」）。
    /// </summary>
    internal static SetupState Evaluate(IReadOnlyList<string> chain, Func<string, bool> isProviderUsable, out string firstUsableEntry)
    {
        firstUsableEntry = null;
        bool hasEntry = false;
        if (chain != null)
        {
            foreach (string entry in chain)
            {
                string providerId = ProviderIds.ParseProviderId(entry);
                if (providerId == null) continue;
                hasEntry = true;
                if (isProviderUsable(providerId))
                {
                    firstUsableEntry = entry;
                    return SetupState.Ready;
                }
            }
        }
        return hasEntry ? SetupState.Broken : SetupState.NotConnected;
    }

    /// <summary>以目前的設定判斷狀態。</summary>
    internal static SetupState Current(out string firstUsableEntry)
    {
        return Evaluate(RimLLMFrameworkMod.Settings.FallbackChain, IsProviderUsable, out firstUsableEntry);
    }

    /// <summary>
    /// 供應商是否可用：已啟用，且需要金鑰時已填金鑰。
    /// 外部 Mod 註冊的供應商註冊即啟用，金鑰由該 Mod 自行管理，視為可用。
    /// </summary>
    internal static bool IsProviderUsable(string providerId)
    {
        RimLLMFrameworkSettings settings = RimLLMFrameworkMod.Settings;
        bool builtIn = false;
        foreach (string id in ProviderIds.BuiltIn)
        {
            if (id == providerId) { builtIn = true; break; }
        }

        if (!builtIn)
        {
            return RimLLMProvider.TryGetManager(out var manager) && manager.IsProviderEnabled(providerId);
        }
        if (!settings.IsProviderEnabled(providerId)) return false;
        return !ProviderIds.RequiresApiKey(providerId, settings.GetEndpoint(providerId, null))
            || !string.IsNullOrEmpty(settings.GetApiKey(providerId));
    }

    /// <summary>
    /// 把連線測試的錯誤碼轉成給玩家看的白話說明翻譯鍵。
    /// </summary>
    internal static string DescribeFailureKey(LLMError error)
    {
        switch (error)
        {
            case LLMError.InvalidKey:
                return "RimLLM_SetupFail_Key";
            case LLMError.RateLimit:
            case LLMError.QuotaExceeded:
                return "RimLLM_SetupFail_Quota";
            case LLMError.Timeout:
            case LLMError.ProviderOffline:
            case LLMError.NetworkError:
                return "RimLLM_SetupFail_Offline";
            default:
                return "RimLLM_SetupFail_Other";
        }
    }
}
