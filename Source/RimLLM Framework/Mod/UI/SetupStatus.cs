using System;
using System.Collections.Generic;
using System.Linq;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Manager.Services;

namespace RimLLM_Framework.Mod;

internal enum SetupState
{
    NotConnected,
    /// <summary>備援鏈有條目，但沒有任何一個的供應商已啟用且具備金鑰。</summary>
    Broken,
    Ready
}

/// <summary>判斷「AI 現在能不能用」；候選資格須與 <see cref="RimLLM_Framework.Manager.RimLLMFallbackPipeline"/> 一致：供應商已啟用，且需要金鑰時已填金鑰。</summary>
internal static class SetupStatus
{
    /// <summary>依備援鏈判斷設定狀態；<paramref name="firstUsableEntry"/> 為第一個可用的條目。</summary>
    internal static SetupState Evaluate(IReadOnlyList<string> chain, Func<string, bool> isProviderUsable, out string? firstUsableEntry)
    {
        firstUsableEntry = null;
        bool hasEntry = false;
        if (chain != null)
        {
            foreach (string entry in chain)
            {
                string? providerId = ProviderIds.ParseProviderId(entry);
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

    internal static SetupState Current(out string? firstUsableEntry)
    {
        return Evaluate(RimLLMFrameworkMod.Settings.FallbackChain, IsProviderUsable, out firstUsableEntry);
    }

    /// <summary>供應商是否可用。外部 Mod 註冊的供應商註冊即啟用，金鑰由該 Mod 自行管理，視為可用。</summary>
    internal static bool IsProviderUsable(string providerId)
    {
        RimLLMFrameworkSettings settings = RimLLMFrameworkMod.Settings;
        bool builtIn = ProviderIds.BuiltIn.Contains(providerId, StringComparer.Ordinal);

        if (!builtIn)
        {
            return RimLLMProvider.TryGetManager(out var manager) && manager.IsProviderEnabled(providerId);
        }
        if (!settings.IsProviderEnabled(providerId)) return false;
        return !ProviderIds.RequiresApiKey(providerId, settings.GetEndpoint(providerId, defaultVal: null))
            || !string.IsNullOrEmpty(settings.GetApiKey(providerId));
    }

    /// <summary>對話供應商對應的雲端 Embedding 供應商（金鑰留空時沿用對話金鑰）；其他供應商回傳 null。</summary>
    internal static string? OneClickEmbeddingProviderFor(string chatProviderId)
    {
        if (chatProviderId == ProviderIds.Gemini) return "Google";
        if (chatProviderId == ProviderIds.OpenAI) return "OpenAI";
        if (chatProviderId == ProviderIds.OpenRouter) return RimLLMEmbeddingService.OpenRouterProvider;
        return null;
    }

    /// <summary>對話供應商對應、可沿用對話金鑰的決策供應商（見 <see cref="RimLLMDecisionProvider.ChatKeyProviderId"/>）；其他回傳 null。</summary>
    internal static string? OneClickDecisionProviderFor(string chatProviderId)
    {
        foreach (RimLLMDecisionProvider provider in RimLLMDecisionProviderCatalog.All)
        {
            if (provider.ChatKeyProviderId != null && provider.ChatKeyProviderId == chatProviderId)
            {
                return provider.Id;
            }
        }
        return null;
    }

    internal static string DescribeFailureKey(LLMError error)
    {
        return error switch
        {
            LLMError.InvalidKey => "RimLLM_SetupFail_Key",
            LLMError.RateLimit or LLMError.QuotaExceeded => "RimLLM_SetupFail_Quota",
            LLMError.Timeout or LLMError.ProviderOffline or LLMError.NetworkError => "RimLLM_SetupFail_Offline",
            _ => "RimLLM_SetupFail_Other",
        };
    }
}
