using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 處理 OpenAICompatible / 自訂 Endpoint 繪製邏輯。
    /// </summary>
    public static class OpenAICompatibleSubTabDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        public static void DrawOpenAICompatibleSettings(Listing_Standard listing, string providerId, string defaultEndpoint, string defaultModel)
        {
            // 1. 啟用 / 停用
            bool enabled = Settings.IsProviderEnabled(providerId);
            listing.CheckboxLabeled("RimLLM_EnableProvider".Translate(), ref enabled);
            Settings.SetProviderEnabled(providerId, enabled);
            if (!enabled) return;

            // 2. API 金鑰列表
            GenericProviderSubTabDrawer.DrawApiKeyList(listing, providerId);

            // 3. Endpoint 設定
            string endpoint = Settings.GetEndpoint(providerId, defaultEndpoint);
            listing.Label("RimLLM_ApiEndpoint".Translate());
            endpoint = listing.TextEntry(endpoint);
            Settings.SetEndpoint(providerId, endpoint?.Trim());

            // 3.1 本地端點自動偵測控制項
            LocalProviderSubTabDrawer.DrawLocalDetectionControls(listing, providerId);

            listing.Gap(8f);

            // 4. 動態獲取模型列表與展示
            GenericProviderSubTabDrawer.DrawModelListSection(listing, providerId);

            // Fetch Models
            GenericProviderSubTabDrawer.DrawFetchModelsButton(listing, providerId);

            listing.Gap(12f);

            // 4.9 呼叫統計與成功率及 API Cache Rate
            GenericProviderSubTabDrawer.DrawProviderCallStats(listing, providerId);

            // 5. 連線測試
            GenericProviderSubTabDrawer.DrawConnectionTest(listing, providerId);

            listing.GapLine(4f);
        }
    }
}
