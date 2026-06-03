using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimWorld;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// RimLLM Framework Mod 本體進入點。
    /// 初始化 SDK、掛載 Dispatcher 並渲染玩家設定 GUI。
    /// </summary>
    public class RimLLMFrameworkMod : Verse.Mod
    {
        /// <summary>
        /// 全域設定檔實例。
        /// </summary>
        public static RimLLMFrameworkSettings Settings { get; private set; }

        private static readonly Dictionary<string, string> TestStatus = new Dictionary<string, string>();
        private static readonly Dictionary<string, bool> Testing = new Dictionary<string, bool>();

        private static string selectedTab = "Gemini";
        private static readonly Dictionary<string, string> FetchStatus = new Dictionary<string, string>();
        private static readonly Dictionary<string, bool> Fetching = new Dictionary<string, bool>();
        private static readonly Dictionary<string, Vector2> ModelScrollPositions = new Dictionary<string, Vector2>();
        private static string addProviderId = "Gemini";
        private static string addModelName = "";

        private Vector2 _scrollPosition = Vector2.zero;

        public RimLLMFrameworkMod(ModContentPack content) : base(content)
        {
            // 1. 載入並儲存 Settings 實體
            Settings = GetSettings<RimLLMFrameworkSettings>();

            // 2. 註冊 SDK 服務管理器到 Provider 入口
            RimLLMProvider.Initialize(new RimLLMManager());
 
            // 3. 強制觸發 Unity 主線程派遣器 (RimLLMDispatcher) 單例建立
            var dispatcher = RimLLMDispatcher.Instance;
            
            Log.Message("[RimLLM] RimLLM Framework 載入成功。");
        }

        public override string SettingsCategory()
        {
            return "RimLLM Framework";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);

            // 限制 Scroll 區域，保留底部確定空間
            Rect outRect = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - 10f);
            Rect viewRect = new Rect(0f, 0f, inRect.width - 25f, 850f);

            Widgets.BeginScrollView(outRect, ref _scrollPosition, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            listing.Label("<size=18><b>RimLLM Framework - 基礎框架配置</b></size>");
            listing.Gap(8f);

            // 1. 供應商與功能切換選單
            Rect selectRect = listing.GetRect(35f);
            string tabLabel = selectedTab == "Fallback" ? "全域 Fallback 設置" : $"供應商設定: {selectedTab}";
            if (Widgets.ButtonText(selectRect, $"<b>{tabLabel}</b> (點擊切換項目)"))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("Google Gemini", () => selectedTab = "Gemini"),
                    new FloatMenuOption("OpenAI", () => selectedTab = "OpenAI"),
                    new FloatMenuOption("OpenAI Compatible (本地/相容)", () => selectedTab = "OpenAICompatible"),
                    new FloatMenuOption("全域 Fallback 設置", () => selectedTab = "Fallback")
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
            listing.Gap(12f);

            // 2. 根據選擇渲染內容
            if (selectedTab == "Fallback")
            {
                DrawFallbackSettings(listing);
            }
            else
            {
                if (selectedTab == "Gemini")
                    DrawProviderSettings(listing, "Gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash");
                else if (selectedTab == "OpenAI")
                    DrawProviderSettings(listing, "OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-4o-mini");
                else if (selectedTab == "OpenAICompatible")
                    DrawProviderSettings(listing, "OpenAICompatible", "http://localhost:1234/v1", "default", isLocal: true);
            }

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
                // 2. API 金鑰
                if (!isLocal)
                {
                    string apiKey = Settings.GetApiKey(providerId);
                    listing.Label("API 金鑰 (Key):");
                    apiKey = listing.TextEntry(apiKey);
                    Settings.SetApiKey(providerId, apiKey?.Trim());
                }

                // 3. Endpoint 設定 (僅本地相容接口展示，官方雲端服務自動在背景配置)
                if (isLocal)
                {
                    string endpoint = Settings.GetEndpoint(providerId, defaultEndpoint);
                    listing.Label("API Endpoint:");
                    endpoint = listing.TextEntry(endpoint);
                    Settings.SetEndpoint(providerId, endpoint?.Trim());
                }
                else
                {
                    Settings.SetEndpoint(providerId, defaultEndpoint);
                }

                listing.Gap(8f);

                // 4. 動態獲取模型列表與展示
                listing.Label("<b>可用模型列表配置</b>");
                
                var currentModels = Settings.GetModelList(providerId);
                if (currentModels.Count == 0)
                {
                    listing.Label("<color=gray>無快取的模型清單（請先獲取）</color>");
                }
                else
                {
                    Rect scrollRect = listing.GetRect(120f);
                    Widgets.DrawMenuSection(scrollRect);
                    
                    float contentWidth = scrollRect.width - 16f; // 扣除滾動條
                    float chipWidth = 220f;
                    float chipHeight = 28f;
                    float gap = 8f;
                    
                    int cols = Mathf.Max(1, Mathf.FloorToInt((contentWidth + gap) / (chipWidth + gap)));
                    int rows = Mathf.CeilToInt((float)currentModels.Count / cols);
                    float viewHeight = Mathf.Max(120f, rows * (chipHeight + gap) + gap);
                    
                    Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);
                    
                    if (!ModelScrollPositions.ContainsKey(providerId))
                    {
                        ModelScrollPositions[providerId] = Vector2.zero;
                    }
                    Vector2 scrollPos = ModelScrollPositions[providerId];
                    
                    Widgets.BeginScrollView(scrollRect, ref scrollPos, viewRect);
                    ModelScrollPositions[providerId] = scrollPos;
                    
                    for (int i = 0; i < currentModels.Count; i++)
                    {
                        string model = currentModels[i];
                        int col = i % cols;
                        int row = i / cols;
                        
                        Rect chipRect = new Rect(
                            col * (chipWidth + gap) + gap, 
                            row * (chipHeight + gap) + gap, 
                            chipWidth, 
                            chipHeight
                        );
                        
                        Widgets.DrawBoxSolid(chipRect, new Color(1f, 1f, 1f, 0.05f));
                        Widgets.DrawBox(chipRect, 1);
                        
                        if (Mouse.IsOver(chipRect))
                        {
                            Widgets.DrawHighlight(chipRect);
                        }
                        TooltipHandler.TipRegion(chipRect, model);
                        
                        Text.Font = GameFont.Tiny;
                        Text.Anchor = TextAnchor.MiddleCenter;
                        bool originalWordWrap = Text.WordWrap;
                        Text.WordWrap = false;
                        
                        Rect textRect = chipRect.ContractedBy(2f);
                        Widgets.Label(textRect, $"<color=silver>{model}</color>");
                        
                        Text.WordWrap = originalWordWrap;
                        Text.Anchor = TextAnchor.UpperLeft;
                        Text.Font = GameFont.Small;
                    }
                    
                    Widgets.EndScrollView();
                }

                bool isFetching = Fetching.TryGetValue(providerId, out bool f) && f;
                string fetchMsg = FetchStatus.TryGetValue(providerId, out string m) ? m : "未執行獲取";

                Rect fetchRect = listing.GetRect(30f);
                Rect fetchBtnRect = new Rect(fetchRect.x, fetchRect.y, 180f, fetchRect.height);
                Rect fetchMsgRect = new Rect(fetchRect.x + 190f, fetchRect.y, fetchRect.width - 190f, fetchRect.height);

                if (isFetching)
                {
                    Widgets.Label(fetchBtnRect, "獲取中...");
                }
                else
                {
                    if (Widgets.ButtonText(fetchBtnRect, "獲取可用模型清單"))
                    {
                        StartFetchModels(providerId);
                    }
                }

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(fetchMsgRect, fetchMsg);
                Text.Anchor = TextAnchor.UpperLeft;
                
                listing.Gap(12f);

                // 5. 連線測試
                listing.Label("<b>供應商連線測試</b>");
                bool isTesting = Testing.TryGetValue(providerId, out bool val) && val;
                string status = TestStatus.TryGetValue(providerId, out string s) ? s : "未測試";

                Rect btnRect = listing.GetRect(30f);
                Rect leftRect = new Rect(btnRect.x, btnRect.y, 180f, btnRect.height);
                Rect rightRect = new Rect(btnRect.x + 190f, btnRect.y, btnRect.width - 190f, btnRect.height);

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

        private void DrawFallbackSettings(Listing_Standard listing)
        {
            listing.Label("<size=14><b>全域模型級 Fallback 機制 (Model-level Fallback)</b></size>");
            listing.Label("當呼叫 SDK 時，框架將依此清單順序嘗試，前一項失敗則嘗試下一項。");
            listing.Gap(8f);

            var chain = Settings.FallbackChain;
            chain.RemoveAll(entry => string.IsNullOrEmpty(entry) || !entry.Contains(":"));

            // 1. 繪製 Fallback 鏈列表
            if (chain.Count == 0)
            {
                listing.Label("<color=yellow>目前 Fallback 鏈為空，請從下方選擇模型新增！</color>");
            }
            else
            {
                for (int i = 0; i < chain.Count; i++)
                {
                    string entry = chain[i];
                    Rect itemRect = listing.GetRect(30f);
                    
                    // 左右劃分
                    Rect labelRect = new Rect(itemRect.x, itemRect.y, itemRect.width - 120f, itemRect.height);
                    Rect upRect = new Rect(itemRect.x + itemRect.width - 110f, itemRect.y, 30f, itemRect.height);
                    Rect downRect = new Rect(itemRect.x + itemRect.width - 75f, itemRect.y, 30f, itemRect.height);
                    Rect deleteRect = new Rect(itemRect.x + itemRect.width - 40f, itemRect.y, 30f, itemRect.height);

                    // 繪製順序標記與名稱
                    Widgets.Label(labelRect, $" {i + 1}. <color=cyan>{entry}</color>");

                    // 上移按鈕
                    if (i > 0)
                    {
                        if (Widgets.ButtonText(upRect, "▲"))
                        {
                            string temp = chain[i];
                            chain[i] = chain[i - 1];
                            chain[i - 1] = temp;
                            Settings.Write();
                            break;
                        }
                    }
                    else
                    {
                        GUI.color = Color.gray;
                        Widgets.ButtonText(upRect, "▲");
                        GUI.color = Color.white;
                    }

                    // 下移按鈕
                    if (i < chain.Count - 1)
                    {
                        if (Widgets.ButtonText(downRect, "▼"))
                        {
                            string temp = chain[i];
                            chain[i] = chain[i + 1];
                            chain[i + 1] = temp;
                            Settings.Write();
                            break;
                        }
                    }
                    else
                    {
                        GUI.color = Color.gray;
                        Widgets.ButtonText(downRect, "▼");
                        GUI.color = Color.white;
                    }

                    // 刪除按鈕
                    if (Widgets.ButtonText(deleteRect, "X"))
                    {
                        chain.RemoveAt(i);
                        Settings.Write();
                        break;
                    }
                }
            }

            listing.GapLine(10f);

            // 2. 新增項目區域
            listing.Label("<b>新增模型至 Fallback 鏈</b>");

            // 2.1 選擇供應商
            Rect addRect = listing.GetRect(30f);
            Rect addProvBtn = new Rect(addRect.x, addRect.y, 150f, addRect.height);
            Rect addModBtn = new Rect(addRect.x + 160f, addRect.y, 250f, addRect.height);
            Rect addSubmitBtn = new Rect(addRect.x + 420f, addRect.y, 100f, addRect.height);

            if (Widgets.ButtonText(addProvBtn, $"供應商: {addProviderId}"))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("Gemini", () => { addProviderId = "Gemini"; addModelName = ""; }),
                    new FloatMenuOption("OpenAI", () => { addProviderId = "OpenAI"; addModelName = ""; }),
                    new FloatMenuOption("OpenAICompatible", () => { addProviderId = "OpenAICompatible"; addModelName = ""; })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // 2.2 選擇該供應商底下的快取模型
            var models = Settings.GetModelList(addProviderId);
            string modelBtnLabel = string.IsNullOrEmpty(addModelName) ? "請選擇模型" : addModelName;
            
            if (Widgets.ButtonText(addModBtn, $"模型: {modelBtnLabel}"))
            {
                if (models.Count == 0)
                {
                    Messages.Message($"該供應商 {addProviderId} 尚無快取的模型清單，請先至該供應商設定頁面點選「獲取可用模型清單」。", MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (var model in models)
                    {
                        string currentM = model;
                        options.Add(new FloatMenuOption(currentM, () => addModelName = currentM));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }

            // 2.3 點擊新增
            if (Widgets.ButtonText(addSubmitBtn, "新增"))
            {
                if (string.IsNullOrEmpty(addModelName))
                {
                    Messages.Message("請先選擇要新增的模型名稱！", MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    string entry = $"{addProviderId}:{addModelName}";
                    if (chain.Contains(entry))
                    {
                        Messages.Message("該模型組合已存在於 Fallback 鏈中！", MessageTypeDefOf.RejectInput, false);
                    }
                    else
                    {
                        chain.Add(entry);
                        Settings.Write();
                        Messages.Message($"已新增 {entry} 至 Fallback 鏈！", MessageTypeDefOf.PositiveEvent, false);
                    }
                }
            }
        }

        private void StartFetchModels(string providerId)
        {
            if (providerId != "OpenAICompatible")
            {
                string apiKey = Settings.GetApiKey(providerId);
                if (string.IsNullOrEmpty(apiKey))
                {
                    FetchStatus[providerId] = "<color=red>請先輸入 API 金鑰 (Key)</color>";
                    return;
                }
            }

            Fetching[providerId] = true;
            FetchStatus[providerId] = "正在從伺服器獲取模型清單...";

            Task.Run(async () =>
            {
                try
                {
                    var models = await RimLLMProvider.Instance.FetchProviderModelsAsync(providerId).ConfigureAwait(false);
                    
                    RimLLMDispatcher.Instance.Enqueue(() =>
                    {
                        Fetching[providerId] = false;
                        if (models != null && models.Count > 0)
                        {
                            Settings.SetModelList(providerId, models);
                            Settings.Write();
                            FetchStatus[providerId] = $"<color=green>獲取成功</color> (共獲取到 {models.Count} 個模型)";
                        }
                        else
                        {
                            FetchStatus[providerId] = "<color=yellow>獲取成功，但回傳清單為空</color>";
                        }
                    });
                }
                catch (Exception ex)
                {
                    RimLLMDispatcher.Instance.Enqueue(() =>
                    {
                        Fetching[providerId] = false;
                        FetchStatus[providerId] = $"<color=red>獲取失敗</color> ({ex.Message})";
                    });
                }
            });
        }
 
        private void StartTest(string providerId)
        {
            if (providerId != "OpenAICompatible")
            {
                string apiKey = Settings.GetApiKey(providerId);
                if (string.IsNullOrEmpty(apiKey))
                {
                    TestStatus[providerId] = "<color=red>請先輸入 API 金鑰 (Key)</color>";
                    return;
                }
            }
 
            Testing[providerId] = true;
            TestStatus[providerId] = "發送測試請求...";
 
            Task.Run(async () =>
            {
                try
                {
                    TestResult result = await RimLLMProvider.Instance.TestProviderAsync(providerId).ConfigureAwait(false);
 
                    // 確保非同步 Callback 分發回 Unity 主線程安全執行
                    RimLLMDispatcher.Instance.Enqueue(() =>
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
                    RimLLMDispatcher.Instance.Enqueue(() =>
                    {
                        Testing[providerId] = false;
                        TestStatus[providerId] = $"<color=red>異常</color> ({ex.Message})";
                    });
                }
            });
        }
    }
}
