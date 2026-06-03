using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// Archotech Nexus (RimLLM Framework) Mod 本體進入點。
    /// 初始化 SDK、掛載 Dispatcher 並渲染玩家設定 GUI。
    /// </summary>
    public class ArchotechNexusMod : Verse.Mod
    {
        /// <summary>
        /// 全域設定檔實例。
        /// </summary>
        public static ArchotechNexusSettings Settings { get; private set; }

        private static readonly Dictionary<string, string> TestStatus = new Dictionary<string, string>();
        private static readonly Dictionary<string, bool> Testing = new Dictionary<string, bool>();

        private Vector2 _scrollPosition = Vector2.zero;

        public ArchotechNexusMod(ModContentPack content) : base(content)
        {
            // 1. 載入並儲存 Settings 實體
            Settings = GetSettings<ArchotechNexusSettings>();

            // 2. 註冊 SDK 服務管理器到 Provider 入口
            ArchotechLLMProvider.Initialize(new ArchotechLLMManager());

            // 3. 強制觸發 Unity 主線程派遣器 (ArchotechDispatcher) 單例建立
            var dispatcher = ArchotechDispatcher.Instance;
            
            Log.Message("[ArchotechNexus] Archotech Nexus (RimLLM Framework) 基礎框架載入成功。");
        }

        public override string SettingsCategory()
        {
            return "Archotech Nexus (RimLLM)";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);

            // 限制 Scroll 區域，保留底部確定空間
            Rect outRect = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - 10f);
            Rect viewRect = new Rect(0f, 0f, inRect.width - 25f, 750f);

            Widgets.BeginScrollView(outRect, ref _scrollPosition, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            listing.Label("<size=18><b>Archotech Nexus - 基礎框架配置</b></size>");
            listing.Gap(8f);

            // 逐一繪製各供應商設定介面
            DrawProviderSettings(listing, "Gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash");
            listing.Gap(12f);

            DrawProviderSettings(listing, "DeepSeek", "https://api.deepseek.com/chat/completions", "deepseek-chat");
            listing.Gap(12f);

            DrawProviderSettings(listing, "OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-4o-mini");
            listing.Gap(12f);

            DrawProviderSettings(listing, "OpenAICompatible", "http://localhost:11434/v1/chat/completions", "default", isLocal: true);
            listing.Gap(12f);

            // 顯示 Fallback 資訊
            listing.Label("<b>全域 API Fallback Chain 順序</b>");
            string chainStr = string.Join(" -> ", Settings.FallbackChain.ToArray());
            listing.Label($"輪詢路徑: <color=cyan>{chainStr}</color> (框架將按順序嘗試已啟用的服務)");

            listing.End();
            Widgets.EndScrollView();
        }

        private void DrawProviderSettings(Listing_Standard listing, string providerId, string defaultEndpoint, string defaultModel, bool isLocal = false)
        {
            string label = providerId;
            if (isLocal) label += " (本地模型 / 相容介面)";

            listing.Label($"<size=14><b>{label}</b></size>");
            
            // 1. 啟用 / 停用
            bool enabled = Settings.IsProviderEnabled(providerId);
            listing.CheckboxLabeled("啟用此 API 供應商", ref enabled);
            Settings.SetProviderEnabled(providerId, enabled);

            if (enabled)
            {
                // 2. API 金鑰 (本地相容通常免密鑰)
                if (!isLocal)
                {
                    string apiKey = Settings.GetApiKey(providerId);
                    listing.Label("API 金鑰 (Key):");
                    apiKey = listing.TextEntry(apiKey);
                    Settings.SetApiKey(providerId, apiKey?.Trim());
                }

                // 3. Endpoint 設定
                string endpoint = Settings.GetEndpoint(providerId, defaultEndpoint);
                listing.Label("API Endpoint:");
                endpoint = listing.TextEntry(endpoint);
                Settings.SetEndpoint(providerId, endpoint?.Trim());

                // 4. 連線測試
                bool isTesting = Testing.TryGetValue(providerId, out bool val) && val;
                string status = TestStatus.TryGetValue(providerId, out string s) ? s : "未測試";

                Rect btnRect = listing.GetRect(30f);
                Rect leftRect = new Rect(btnRect.x, btnRect.y, 140f, btnRect.height);
                Rect rightRect = new Rect(btnRect.x + 150f, btnRect.y, btnRect.width - 150f, btnRect.height);

                if (isTesting)
                {
                    Widgets.Label(leftRect, "連線測試中...");
                }
                else
                {
                    if (Widgets.ButtonText(leftRect, "測試 API 連線"))
                    {
                        StartTest(providerId);
                    }
                }

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(rightRect, $"測試結果: {status}");
                Text.Anchor = TextAnchor.UpperLeft;
            }

            listing.GapLine(4f);
        }

        private void StartTest(string providerId)
        {
            Testing[providerId] = true;
            TestStatus[providerId] = "發送測試請求...";

            Task.Run(async () =>
            {
                try
                {
                    TestResult result = await ArchotechLLMProvider.Instance.TestProviderAsync(providerId).ConfigureAwait(false);

                    // 確保非同步 Callback 分發回 Unity 主線程安全執行
                    ArchotechDispatcher.Instance.Enqueue(() =>
                    {
                        Testing[providerId] = false;
                        if (result.Success)
                        {
                            TestStatus[providerId] = $"<color=green>成功</color> (延遲: {result.LatencyMs}ms, 模組: {result.Model})";
                        }
                        else
                        {
                            TestStatus[providerId] = $"<color=red>失敗</color> ({result.ErrorMessage})";
                        }
                    });
                }
                catch (Exception ex)
                {
                    ArchotechDispatcher.Instance.Enqueue(() =>
                    {
                        Testing[providerId] = false;
                        TestStatus[providerId] = $"<color=red>異常</color> ({ex.Message})";
                    });
                }
            });
        }
    }
}
