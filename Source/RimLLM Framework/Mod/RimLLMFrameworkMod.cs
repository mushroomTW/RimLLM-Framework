using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using System.IO;
using System.Text;
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
        private static string activeMainCategory = "Providers";
        private static string activeProviderSubTab = "Gemini";
        private static Vector2 _detailScrollPosition = Vector2.zero;
        private static Vector2 _midScrollPosition = Vector2.zero;
        private static Vector2 debugScrollPosition = Vector2.zero;
        private static string chatInput = "";
        private static readonly List<string> chatHistory = new List<string>();
        private static Vector2 chatScrollPosition = Vector2.zero;
        private static bool chatLoading = false;
        private static LLMReasoningEffort chatReasoningEffort = LLMReasoningEffort.None;
        private static bool chatReasoningInitialized = false;
        private static readonly Dictionary<string, string> FetchStatus = new Dictionary<string, string>();
        private static readonly Dictionary<string, bool> Fetching = new Dictionary<string, bool>();
        private static readonly Dictionary<string, Vector2> ModelScrollPositions = new Dictionary<string, Vector2>();
        private static string addProviderId = "Gemini";
        private static string addModelName = "";
        public RimLLMFrameworkMod(ModContentPack content) : base(content)
        {
            // 1. 載入並儲存 Settings 實體
            Settings = GetSettings<RimLLMFrameworkSettings>();
            // 2. 註冊 SDK 服務管理器到 Provider 入口 (依賴注入 Settings)
            RimLLMProvider.Initialize(new RimLLMManager(Settings));
 
            // 3. 強制觸發 Unity 主線程派遣器 (RimLLMDispatcher) 單例建立
            var dispatcher = RimLLMDispatcher.Instance;
            // 從 Settings 還原對話歷史
            if (Settings.ChatHistory != null && Settings.ChatHistory.Count > 0)
            {
                chatHistory.Clear();
                chatHistory.AddRange(Settings.ChatHistory);
            }
            
            Log.Message("[RimLLM] RimLLM Framework 載入成功。");
        }
        public override string SettingsCategory()
        {
            return "RimLLM Framework";
        }
        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            // 動態尋找並調整 RimWorld 預設的 Mod 設定視窗大小
            var modSettingsWindow = Find.WindowStack.WindowOfType<Dialog_ModSettings>();
            if (modSettingsWindow != null)
            {
                float targetWidth = 1150f;
                float targetHeight = 780f;
                if (Math.Abs(modSettingsWindow.windowRect.width - targetWidth) > 1f || Math.Abs(modSettingsWindow.windowRect.height - targetHeight) > 1f)
                {
                    modSettingsWindow.windowRect.width = targetWidth;
                    modSettingsWindow.windowRect.height = targetHeight;
                    modSettingsWindow.windowRect.x = (UI.screenWidth - targetWidth) / 2f;
                    modSettingsWindow.windowRect.y = (UI.screenHeight - targetHeight) / 2f;
                }
            }
            // 直接在原地繪製完整設定內容，不再需要點擊第二層按鈕
            DrawSettingsWindowBody(inRect);
        }
        public void DrawSettingsWindowBody(Rect inRect)
        {
            float leftWidth = 135f;
            float midWidth = 190f;
            float gap = 8f;
            float height = inRect.height - 10f;
            // 1. 左側一級分類欄
            Rect leftColRect = new Rect(inRect.x, inRect.y, leftWidth, height);
            Widgets.DrawMenuSection(leftColRect);
            DrawLeftCategoryMenu(leftColRect);
            // 2. 根據選中項目決定中欄與右欄佈局
            if (activeMainCategory == "Providers")
            {
                Rect midColRect = new Rect(leftColRect.xMax + gap, inRect.y, midWidth, height);
                Widgets.DrawMenuSection(midColRect);
                DrawMiddleProviderMenu(midColRect);
                Rect rightColRect = new Rect(midColRect.xMax + gap, inRect.y, inRect.width - leftWidth - midWidth - gap * 2f, height);
                Widgets.DrawMenuSection(rightColRect);
                DrawRightDetailContent(rightColRect);
            }
            else
            {
                Rect rightColRect = new Rect(leftColRect.xMax + gap, inRect.y, inRect.width - leftWidth - gap, height);
                Widgets.DrawMenuSection(rightColRect);
                DrawRightDetailContent(rightColRect);
            }
        }
        private void DrawLeftCategoryMenu(Rect rect)
        {
            Rect contentRect = rect.ContractedBy(6f);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(contentRect);
            Text.Font = GameFont.Small;
            listing.Label("RimLLM_MainMenu".Translate());
            listing.Gap(6f);
            DrawCategoryButton(listing, "RimLLM_TabProviders".Translate(), "Providers");
            listing.Gap(4f);
            DrawCategoryButton(listing, "RimLLM_TabFallback".Translate(), "Fallback");
            listing.Gap(4f);
            DrawCategoryButton(listing, "RimLLM_TabGlobalConfig".Translate(), "GlobalConfig");
            listing.Gap(4f);
            DrawCategoryButton(listing, "RimLLM_TabChatTest".Translate(), "ChatTest");
            listing.Gap(4f);
            DrawCategoryButton(listing, "RimLLM_TabDebug".Translate(), "Debug");
            listing.End();
        }
        private void DrawCategoryButton(Listing_Standard listing, string label, string categoryId)
        {
            Rect btnRect = listing.GetRect(32f);
            
            if (activeMainCategory == categoryId)
            {
                Widgets.DrawBoxSolid(btnRect, new Color(1f, 1f, 1f, 0.08f));
                Widgets.DrawBox(btnRect, 1);
            }
            else
            {
                if (Mouse.IsOver(btnRect))
                {
                    Widgets.DrawHighlight(btnRect);
                }
            }
            if (Widgets.ButtonInvisible(btnRect))
            {
                activeMainCategory = categoryId;
                _detailScrollPosition = Vector2.zero;
            }
            Text.Anchor = TextAnchor.MiddleCenter;
            string text = activeMainCategory == categoryId ? $"<color=white><b>{label}</b></color>" : $"<color=silver>{label}</color>";
            Widgets.Label(btnRect, text);
            Text.Anchor = TextAnchor.UpperLeft;
        }
        private void DrawMiddleProviderMenu(Rect rect)
        {
            Rect contentRect = rect.ContractedBy(6f);
            
            Rect titleRect = new Rect(contentRect.x, contentRect.y, contentRect.width, 20f);
            Text.Font = GameFont.Small;
            Widgets.Label(titleRect, "RimLLM_ApiProviders".Translate());
            Rect listRect = new Rect(contentRect.x, titleRect.yMax + 4f, contentRect.width, contentRect.height - 24f);
            // 11 個供應商，每個按鈕高度 46f + 4f gap = 50f
            Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, 11 * 50f + 10f);
            Widgets.BeginScrollView(listRect, ref _midScrollPosition, viewRect);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);
            DrawProviderSubButton(listing, "Google Gemini", "Gemini");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "OpenAI", "OpenAI");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "DeepSeek", "DeepSeek");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "Groq", "Groq");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "Anthropic Claude", "Anthropic");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "OpenRouter", "OpenRouter");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "Kimi", "Kimi");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "MiniMax", "MiniMax");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "Qwen", "Qwen");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "NVIDIA", "Nvidia");
            listing.Gap(4f);
            DrawProviderSubButton(listing, "OpenAI Compatible", "OpenAICompatible");
            listing.End();
            Widgets.EndScrollView();
        }
        private void DrawProviderSubButton(Listing_Standard listing, string label, string providerId)
        {
            Rect btnRect = listing.GetRect(46f);
            
            if (activeProviderSubTab == providerId)
            {
                Widgets.DrawBoxSolid(btnRect, new Color(1f, 1f, 1f, 0.08f));
                Widgets.DrawBox(btnRect, 1);
            }
            else
            {
                if (Mouse.IsOver(btnRect))
                {
                    Widgets.DrawHighlight(btnRect);
                }
            }
            if (Widgets.ButtonInvisible(btnRect))
            {
                activeProviderSubTab = providerId;
                _detailScrollPosition = Vector2.zero;
            }
            Rect nameRect = new Rect(btnRect.x + 8f, btnRect.y + 3f, btnRect.width - 16f, 22f);
            Rect statusRect = new Rect(btnRect.x + 8f, btnRect.y + 25f, btnRect.width - 16f, 18f);
            Text.Font = GameFont.Small;
            string nameText = activeProviderSubTab == providerId ? $"<color=white><b>{label}</b></color>" : $"<color=silver>{label}</color>";
            Widgets.Label(nameRect, nameText);
            Text.Font = GameFont.Tiny;
            bool enabled = Settings.IsProviderEnabled(providerId);
            Color oldColor = GUI.color;
            GUI.color = enabled ? new Color(0.13f, 0.77f, 0.37f) : new Color(0.53f, 0.53f, 0.53f);
            string statusText = enabled ? "RimLLM_StatusEnabled".Translate() : "RimLLM_StatusDisabled".Translate();
            if (enabled)
            {
                int modelCount = Settings.GetModelList(providerId).Count;
                statusText += " | " + "RimLLM_ModelsCount".Translate(modelCount);
            }
            Widgets.Label(statusRect, statusText);
            GUI.color = oldColor;
            
            Text.Font = GameFont.Small;
        }
        private void DrawRightDetailContent(Rect rect)
        {
            Rect contentRect = rect.ContractedBy(8f);
            Rect titleRect = new Rect(contentRect.x, contentRect.y, contentRect.width, 24f);
            string titleText = "";
            if (activeMainCategory == "Providers")
            {
                string tabName = activeProviderSubTab;
                if (activeProviderSubTab == "OpenAICompatible")
                {
                    tabName += "RimLLM_LocalCompatibleMode".Translate();
                }
                titleText = "RimLLM_TitleProviderSettings".Translate(tabName);
            }
            else if (activeMainCategory == "Fallback")
            {
                titleText = "RimLLM_TitleFallback".Translate();
            }
            else if (activeMainCategory == "GlobalConfig")
            {
                titleText = "RimLLM_TitleGlobalConfig".Translate();
            }
            else if (activeMainCategory == "ChatTest")
            {
                titleText = "RimLLM_ChatTitle".Translate();
            }
            else if (activeMainCategory == "Debug")
            {
                titleText = "RimLLM_TitleDebug".Translate();
            }
            Widgets.Label(titleRect, $"<size=14><b>{titleText}</b></size>");
            Widgets.DrawLineHorizontal(contentRect.x, titleRect.yMax + 4f, contentRect.width);
            Rect detailRect = new Rect(contentRect.x, titleRect.yMax + 8f, contentRect.width, contentRect.height - 36f);
            Rect viewRect = new Rect(0f, 0f, detailRect.width, GetDetailViewHeight(detailRect.width));
            Widgets.BeginScrollView(detailRect, ref _detailScrollPosition, viewRect, false);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);
            if (activeMainCategory == "Providers")
            {
                if (activeProviderSubTab == "Gemini")
                    DrawProviderSettings(listing, "Gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash");
                else if (activeProviderSubTab == "OpenAI")
                    DrawProviderSettings(listing, "OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-4o-mini");
                else if (activeProviderSubTab == "DeepSeek")
                    DrawProviderSettings(listing, "DeepSeek", "https://api.deepseek.com", "deepseek-chat");
                else if (activeProviderSubTab == "Groq")
                    DrawProviderSettings(listing, "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile");
                else if (activeProviderSubTab == "Anthropic")
                    DrawProviderSettings(listing, "Anthropic", "https://api.anthropic.com/v1/messages", "claude-3-5-sonnet-20241022");
                else if (activeProviderSubTab == "OpenRouter")
                    DrawProviderSettings(listing, "OpenRouter", "https://openrouter.ai/api/v1", "google/gemini-2.5-flash");
                else if (activeProviderSubTab == "Kimi")
                    DrawProviderSettings(listing, "Kimi", "https://api.moonshot.ai/v1", "moonshot-v1-8k");
                else if (activeProviderSubTab == "MiniMax")
                    DrawProviderSettings(listing, "MiniMax", "https://api.minimax.io/v1", "abab6.5g-chat");
                else if (activeProviderSubTab == "Qwen")
                    DrawProviderSettings(listing, "Qwen", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-plus");
                else if (activeProviderSubTab == "Nvidia")
                    DrawProviderSettings(listing, "Nvidia", "https://integrate.api.nvidia.com/v1", "meta/llama-3.1-8b-instruct");
                else if (activeProviderSubTab == "OpenAICompatible")
                    DrawProviderSettings(listing, "OpenAICompatible", "http://localhost:1234/v1", "default", isLocal: true);
            }
            else if (activeMainCategory == "Fallback")
            {
                DrawFallbackSettings(listing);
            }
            else if (activeMainCategory == "GlobalConfig")
            {
                DrawGlobalConfigSettings(listing);
            }
            else if (activeMainCategory == "ChatTest")
            {
                DrawChatTestSettings(listing);
            }
            else if (activeMainCategory == "Debug")
            {
                DrawDebugSettings(listing);
            }
            listing.End();
            Widgets.EndScrollView();
        }
        private float GetDetailViewHeight(float width)
        {
            if (activeMainCategory == "Providers")
            {
                bool enabled = Settings.IsProviderEnabled(activeProviderSubTab);
                if (!enabled) return 120f;
                
                int modelCount = Settings.GetModelList(activeProviderSubTab).Count;
                float modelSectionHeight = modelCount > 0 ? 280f : 60f;
                
                // 動態計算 API 金鑰列表的高度 (每個 key 32f，新增按鈕 36f，標題與間隙 30f)
                float keysHeight = 0f;
                if (activeProviderSubTab != "OpenAICompatible")
                {
                    string rawApiKey = Settings.GetApiKey(activeProviderSubTab);
                    var keys = rawApiKey.Split(new char[] { ',' }, StringSplitOptions.None);
                    int keyCount = Mathf.Max(1, keys.Length);
                    keysHeight = 30f + (keyCount * 32f) + 36f;
                }

                float extraHeight = 0f;
                if (activeProviderSubTab == "Kimi" || activeProviderSubTab == "MiniMax" || activeProviderSubTab == "Qwen")
                {
                    extraHeight = 30f;
                }
                else if (activeProviderSubTab == "OpenAICompatible")
                {
                    extraHeight = 60f;
                }
                return 250f + keysHeight + modelSectionHeight + 100f + extraHeight; 
            }
            else if (activeMainCategory == "Fallback")
            {
                int chainCount = Settings.FallbackChain.Count;
                return 150f + (chainCount * 36f) + 180f;
            }
            else if (activeMainCategory == "ChatTest")
            {
                return 650f;
            }
            else if (activeMainCategory == "Debug")
            {
                return 620f;
            }
            else
            {
                return 280f;
            }
        }
        private void DrawProviderSettings(Listing_Standard listing, string providerId, string defaultEndpoint, string defaultModel, bool isLocal = false)
        {
            // 1. 啟用 / 停用
            bool enabled = Settings.IsProviderEnabled(providerId);
            listing.CheckboxLabeled("RimLLM_EnableProvider".Translate(), ref enabled);
            Settings.SetProviderEnabled(providerId, enabled);
            if (enabled)
            {
                // 2. API 金鑰列表
                if (!isLocal || providerId == "OpenAICompatible")
                {
                    string rawApiKey = Settings.GetApiKey(providerId);
                    var keys = new List<string>(rawApiKey.Split(new char[] { ',' }, StringSplitOptions.None));
                    if (keys.Count == 0 || (keys.Count == 1 && string.IsNullOrEmpty(keys[0])))
                    {
                        keys = new List<string> { "" };
                    }

                    listing.Label("RimLLM_ApiKey".Translate());
                    
                    int keyToDelete = -1;
                    for (int i = 0; i < keys.Count; i++)
                    {
                        Rect rowRect = listing.GetRect(28f);
                        Rect inputRect = new Rect(rowRect.x, rowRect.y, rowRect.width - 40f, rowRect.height);
                        Rect deleteRect = new Rect(rowRect.x + rowRect.width - 32f, rowRect.y, 32f, rowRect.height);
                        
                        string oldVal = keys[i];
                        string newVal = Widgets.TextField(inputRect, oldVal);
                        if (newVal != oldVal)
                        {
                            keys[i] = newVal;
                        }
                        
                        if (keys.Count > 1 || !string.IsNullOrEmpty(keys[0]))
                        {
                            if (Widgets.ButtonText(deleteRect, "-"))
                            {
                                keyToDelete = i;
                            }
                        }
                        listing.Gap(4f);
                    }

                    if (keyToDelete != -1)
                    {
                        keys.RemoveAt(keyToDelete);
                        if (keys.Count == 0) keys.Add("");
                    }

                    Rect addKeyRowRect = listing.GetRect(28f);
                    Rect addKeyBtnRect = new Rect(addKeyRowRect.x, addKeyRowRect.y, 140f, addKeyRowRect.height);
                    if (Widgets.ButtonText(addKeyBtnRect, "RimLLM_AddApiKeyBtn".Translate()))
                    {
                        keys.Add("");
                    }
                    listing.Gap(8f);

                    string newRawKey = string.Join(",", keys);
                    Settings.SetApiKey(providerId, newRawKey);
                }
                // 3. Endpoint 設定 (僅本地相容接口展示，官方雲端服務自動在背景配置)
                if (isLocal)
                {
                    string endpoint = Settings.GetEndpoint(providerId, defaultEndpoint);
                    listing.Label("RimLLM_ApiEndpoint".Translate());
                    endpoint = listing.TextEntry(endpoint);
                    Settings.SetEndpoint(providerId, endpoint?.Trim());
                }
                else
                {
                    Settings.SetEndpoint(providerId, null);
                }
                // 3.1 支援中國端點切換 (僅 Kimi, MiniMax, Qwen 且非 Local)
                if (!isLocal && (providerId == "Kimi" || providerId == "MiniMax" || providerId == "Qwen"))
                {
                    bool isChina = Settings.IsChinaMode(providerId);
                    bool oldIsChina = isChina;
                    listing.CheckboxLabeled("RimLLM_ChinaEndpointToggle".Translate(), ref isChina);
                    if (isChina != oldIsChina)
                    {
                        Settings.SetChinaMode(providerId, isChina);
                        Settings.Write();
                    }
                }
                listing.Gap(8f);
                // 4. 動態獲取模型列表與展示
                listing.Label("RimLLM_AvailableModelsTitle".Translate());
                
                var currentModels = Settings.GetModelList(providerId);
                if (currentModels.Count == 0)
                {
                    listing.Label("RimLLM_NoCachedModels".Translate());
                }
                else
                {
                    Rect scrollRect = listing.GetRect(220f);
                    Widgets.DrawMenuSection(scrollRect);
                    
                    float contentWidth = scrollRect.width - 16f; // 扣除滾動條
                    float chipWidth = 220f;
                    float chipHeight = 28f;
                    float gap = 8f;
                    
                    int cols = Mathf.Max(1, Mathf.FloorToInt((contentWidth + gap) / (chipWidth + gap)));
                    int rows = Mathf.CeilToInt((float)currentModels.Count / cols);
                    float viewHeight = Mathf.Max(220f, rows * (chipHeight + gap) + gap);
                    
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
                string fetchMsg = FetchStatus.TryGetValue(providerId, out string m) ? m : "RimLLM_FetchStatusNotRun".Translate().ToString();
                Rect fetchRect = listing.GetRect(60f);
                Rect fetchBtnRect = new Rect(fetchRect.x, fetchRect.y + 15f, 180f, 30f);
                Rect fetchMsgRect = new Rect(fetchRect.x + 190f, fetchRect.y, fetchRect.width - 190f, 60f);
                if (isFetching)
                {
                    Widgets.Label(fetchBtnRect, "RimLLM_Fetching".Translate());
                }
                else
                {
                    if (Widgets.ButtonText(fetchBtnRect, "RimLLM_FetchModelsBtn".Translate()))
                    {
                        StartFetchModels(providerId);
                    }
                }
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(fetchMsgRect, fetchMsg);
                Text.Anchor = TextAnchor.UpperLeft;
                
                listing.Gap(12f);
                // 5. 連線測試
                listing.Label("RimLLM_ConnectionTestTitle".Translate());
                bool isTesting = Testing.TryGetValue(providerId, out bool val) && val;
                string status = TestStatus.TryGetValue(providerId, out string s) ? s : "RimLLM_TestStatusNotRun".Translate().ToString();
                Rect btnRect = listing.GetRect(60f);
                Rect leftRect = new Rect(btnRect.x, btnRect.y + 15f, 180f, 30f);
                Rect rightRect = new Rect(btnRect.x + 190f, btnRect.y, btnRect.width - 190f, 60f);
                if (isTesting)
                {
                    Widgets.Label(leftRect, "RimLLM_Testing".Translate());
                }
                else
                {
                    if (Widgets.ButtonText(leftRect, "RimLLM_TestConnectionBtn".Translate()))
                    {
                        StartTest(providerId);
                    }
                }
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(rightRect, "RimLLM_TestResult".Translate(status));
                Text.Anchor = TextAnchor.UpperLeft;
            }
            listing.GapLine(4f);
        }
        private void SetDefaultAddModelName(string providerId)
        {
            addProviderId = providerId;
            if (providerId == "OpenRouter")
            {
                addModelName = "openrouter/auto";
                return;
            }
            var models = Settings.GetModelList(providerId);
            if (models != null && models.Count > 0)
            {
                addModelName = models[0];
            }
            else
            {
                addModelName = Settings.GetDefaultModel(providerId, "default");
            }
        }
        private void DrawFallbackSettings(Listing_Standard listing)
        {
            listing.Label("RimLLM_FallbackExplanation".Translate());
            listing.Gap(8f);
            var chain = Settings.FallbackChain;
            chain.RemoveAll(entry => string.IsNullOrEmpty(entry));
            // 確保 addModelName 已經初始化
            if (string.IsNullOrEmpty(addModelName))
            {
                SetDefaultAddModelName(addProviderId);
            }
            // 1. 繪製 Fallback 鏈列表
            if (chain.Count == 0)
            {
                listing.Label("RimLLM_FallbackEmptyWarning".Translate());
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
            listing.Label("RimLLM_AddToFallbackTitle".Translate());
            // 2.1 選擇供應商
            Rect addRect = listing.GetRect(30f);
            Rect addProvBtn = new Rect(addRect.x, addRect.y, 150f, addRect.height);
            Rect addModBtn = new Rect(addRect.x + 160f, addRect.y, 250f, addRect.height);
            Rect addSubmitBtn = new Rect(addRect.x + 420f, addRect.y, 100f, addRect.height);
            if (Widgets.ButtonText(addProvBtn, "RimLLM_SelectProviderBtn".Translate(addProviderId)))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("Gemini", () => SetDefaultAddModelName("Gemini")),
                    new FloatMenuOption("OpenAI", () => SetDefaultAddModelName("OpenAI")),
                    new FloatMenuOption("DeepSeek", () => SetDefaultAddModelName("DeepSeek")),
                    new FloatMenuOption("Groq", () => SetDefaultAddModelName("Groq")),
                    new FloatMenuOption("Anthropic", () => SetDefaultAddModelName("Anthropic")),
                    new FloatMenuOption("OpenRouter", () => SetDefaultAddModelName("OpenRouter")),
                    new FloatMenuOption("Kimi", () => SetDefaultAddModelName("Kimi")),
                    new FloatMenuOption("MiniMax", () => SetDefaultAddModelName("MiniMax")),
                    new FloatMenuOption("Qwen", () => SetDefaultAddModelName("Qwen")),
                    new FloatMenuOption("Nvidia", () => SetDefaultAddModelName("Nvidia")),
                    new FloatMenuOption("OpenAICompatible", () => SetDefaultAddModelName("OpenAICompatible"))
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
            // 2.2 選擇該供應商底下的快取模型
            var models = Settings.GetModelList(addProviderId);
            string modelBtnLabel = string.IsNullOrEmpty(addModelName) ? "default" : addModelName;
            
            if (Widgets.ButtonText(addModBtn, "RimLLM_SelectModelBtn".Translate(modelBtnLabel)))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                if (addProviderId == "OpenRouter")
                {
                    options.Add(new FloatMenuOption("openrouter/auto", () => addModelName = "openrouter/auto"));
                }
                
                foreach (var model in models)
                {
                    string currentM = model;
                    options.Add(new FloatMenuOption(currentM, () => addModelName = currentM));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            // 2.3 點擊新增
            if (Widgets.ButtonText(addSubmitBtn, "RimLLM_AddBtn".Translate()))
            {
                string entry = $"{addProviderId}:{addModelName}";
                if (chain.Contains(entry))
                {
                    Messages.Message("RimLLM_MsgModelExists".Translate(), MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    chain.Add(entry);
                    Settings.Write();
                    Messages.Message("RimLLM_MsgModelAdded".Translate(entry), MessageTypeDefOf.PositiveEvent, false);
                }
            }
            listing.GapLine(10f);
        }
        private void DrawGlobalConfigSettings(Listing_Standard listing)
        {
            float prevTimeout = Settings.ApiTimeout;
            int prevRetries = Settings.MaxRetries;
            float prevDelay = Settings.RetryDelay;
            int prevMaxConcurrent = Settings.MaxConcurrentRequests;
            // 1. API 逾時時間
            listing.Label("RimLLM_ApiTimeoutLabel".Translate(Mathf.RoundToInt(Settings.ApiTimeout)));
            Settings.ApiTimeout = listing.Slider(Settings.ApiTimeout, 5f, 120f);
            // 2. 單模型最多重試次數
            listing.Label("RimLLM_MaxRetriesLabel".Translate(Settings.MaxRetries));
            float retriesVal = listing.Slider((float)Settings.MaxRetries, 0f, 10f);
            Settings.MaxRetries = Mathf.RoundToInt(retriesVal);
            // 3. 重試間隔
            listing.Label("RimLLM_RetryDelayLabel".Translate(Mathf.RoundToInt(Settings.RetryDelay)));
            Settings.RetryDelay = listing.Slider(Settings.RetryDelay, 0f, 10f);
            // 4. 最大並行數
            listing.Label("RimLLM_MaxConcurrentRequestsLabel".Translate(Settings.MaxConcurrentRequests));
            float maxConcurrentVal = listing.Slider((float)Settings.MaxConcurrentRequests, 1f, 10f);
            Settings.MaxConcurrentRequests = Mathf.RoundToInt(maxConcurrentVal);
            listing.Gap(6f);
            // 5. 預設思考強度
            Rect effortRect = listing.GetRect(30f);
            Rect labelRect = new Rect(effortRect.x, effortRect.y, 250f, effortRect.height);
            Rect btnRect = new Rect(effortRect.x + 260f, effortRect.y, 200f, effortRect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(labelRect, "RimLLM_ReasoningEffortLabel".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            string currentEffortLabel = $"RimLLM_ReasoningEffort_{Settings.DefaultReasoningEffort}".Translate();
            if (Widgets.ButtonText(btnRect, currentEffortLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RimLLM_ReasoningEffort_None".Translate(), () => { Settings.DefaultReasoningEffort = LLMReasoningEffort.None; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_Low".Translate(), () => { Settings.DefaultReasoningEffort = LLMReasoningEffort.Low; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_Medium".Translate(), () => { Settings.DefaultReasoningEffort = LLMReasoningEffort.Medium; Settings.Write(); }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_High".Translate(), () => { Settings.DefaultReasoningEffort = LLMReasoningEffort.High; Settings.Write(); })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }
        private void DrawDebugSettings(Listing_Standard listing)
        {
            listing.Label("RimLLM_TitleDebugExplanation".Translate());
            listing.Gap(8f);
            bool prevDetailedLogging = Settings.DetailedLogging;
            // 詳細日誌
            bool detailedLogging = Settings.DetailedLogging;
            listing.CheckboxLabeled("RimLLM_DetailedLogging".Translate(), ref detailedLogging, "RimLLM_DetailedLoggingExplanation".Translate());
            Settings.DetailedLogging = detailedLogging;
            RimLLMLog.Enabled = detailedLogging;
            if (prevDetailedLogging != Settings.DetailedLogging)
            {
                Settings.Write();
            }
            listing.Gap(8f);
            // 匯出診斷按鈕
            Rect exportRect = listing.GetRect(30f);
            if (Widgets.ButtonText(exportRect, "RimLLM_ExportDiag".Translate()))
            {
                ExportDiagnostics();
            }
            listing.Gap(12f);
            // 最近請求紀錄日誌
            listing.Label("<b>" + "RimLLM_RecentRequests".Translate(30) + "</b>");
            listing.Gap(4f);
            if (RimLLMProvider.Instance is RimLLMManager manager)
            {
                var logs = new List<RimLLMManager.RequestLogEntry>(manager.RequestLogs);
                logs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                
                if (logs.Count == 0)
                {
                    listing.Label("RimLLM_NoRequests".Translate());
                }
                else
                {
                    Rect logScrollRect = listing.GetRect(420f);
                    Widgets.DrawMenuSection(logScrollRect);
                    
                    float contentWidth = logScrollRect.width - 16f;
                    float logHeight = 24f;
                    float viewHeight = Math.Max(420f, logs.Count * logHeight + 10f);
                    Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);
                    Widgets.BeginScrollView(logScrollRect, ref debugScrollPosition, viewRect);
                    
                    for (int i = 0; i < logs.Count; i++)
                    {
                        var log = logs[i];
                        Rect lineRect = new Rect(4f, i * logHeight + 4f, contentWidth - 8f, logHeight - 2f);
                        
                        string timeStr = log.Timestamp.ToString("HH:mm:ss");
                        string statusText = log.Success 
                            ? $"<color=#22c55e>SUCCESS</color> ({log.LatencyMs}ms)" 
                            : $"<color=#ef4444>FAILED</color> (Err: {log.ErrorMessage})";
                        
                        string logLine = $"[{timeStr}] Mod: {log.ModId} | {log.Provider} ({log.Model}) | {statusText}";
                        
                        Text.Font = GameFont.Tiny;
                        Widgets.Label(lineRect, logLine);
                        Text.Font = GameFont.Small;
                    }
                    Widgets.EndScrollView();
                }
            }
        }
        private void DrawChatTestSettings(Listing_Standard listing)
        {
            if (!chatReasoningInitialized)
            {
                chatReasoningEffort = Settings.DefaultReasoningEffort;
                chatReasoningInitialized = true;
            }
            
            // 滾動顯示對話歷史
            Rect chatRect = listing.GetRect(480f);
            Widgets.DrawMenuSection(chatRect);
            
            float chatContentWidth = chatRect.width - 16f;
            
            StringBuilder chatBuilder = new StringBuilder();
            for (int i = 0; i < chatHistory.Count; i++)
            {
                chatBuilder.Append(chatHistory[i]);
                if (i < chatHistory.Count - 1)
                {
                    chatBuilder.AppendLine();
                    chatBuilder.AppendLine();
                }
            }
            string allChatText = chatBuilder.ToString();
            
            float chatViewHeight = Math.Max(480f, Text.CalcHeight(allChatText, chatContentWidth) + 12f);
            Rect chatViewRect = new Rect(0f, 0f, chatContentWidth, chatViewHeight);
            
            Widgets.BeginScrollView(chatRect, ref chatScrollPosition, chatViewRect);
            Rect allChatRect = new Rect(4f, 4f, chatContentWidth - 8f, chatViewHeight - 8f);
            GUIStyle richLabelStyle = new GUIStyle(Text.CurFontStyle);
            richLabelStyle.richText = true;
            richLabelStyle.wordWrap = true;
            GUI.Label(allChatRect, allChatText, richLabelStyle);
            Widgets.EndScrollView();
            
            listing.Gap(6f);
            // 思考強度設定列
            Rect effortRowRect = listing.GetRect(30f);
            Rect effortLabelRect = new Rect(effortRowRect.x, effortRowRect.y, 250f, effortRowRect.height);
            Rect effortBtnRect = new Rect(effortRowRect.x + 260f, effortRowRect.y, 200f, effortRowRect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(effortLabelRect, "RimLLM_ReasoningEffortLabel".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            string chatEffortLabel = $"RimLLM_ReasoningEffort_{chatReasoningEffort}".Translate();
            if (Widgets.ButtonText(effortBtnRect, chatEffortLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RimLLM_ReasoningEffort_None".Translate(), () => { chatReasoningEffort = LLMReasoningEffort.None; }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_Low".Translate(), () => { chatReasoningEffort = LLMReasoningEffort.Low; }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_Medium".Translate(), () => { chatReasoningEffort = LLMReasoningEffort.Medium; }),
                    new FloatMenuOption("RimLLM_ReasoningEffort_High".Translate(), () => { chatReasoningEffort = LLMReasoningEffort.High; })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
            listing.Gap(6f);
            
            // 輸入框、清空按鈕與發送按鈕
            Rect inputRowRect = listing.GetRect(30f);
            Rect textInputRect = new Rect(inputRowRect.x, inputRowRect.y, inputRowRect.width - 180f, inputRowRect.height);
            Rect clearBtnRect = new Rect(inputRowRect.x + inputRowRect.width - 170f, inputRowRect.y, 80f, inputRowRect.height);
            Rect sendBtnRect = new Rect(inputRowRect.x + inputRowRect.width - 80f, inputRowRect.y, 80f, inputRowRect.height);
            
            if (chatLoading)
            {
                int dotCount = 1 + (int)(Time.realtimeSinceStartup * 2.5) % 3;
                string dots = new string('.', dotCount);
                Widgets.Label(textInputRect, $"<color=silver>{"RimLLM_AiThinking".Translate()}{dots}</color>");
            }
            else
            {
                chatInput = Widgets.TextField(textInputRect, chatInput);
            }
            
            // 點擊清空按鈕邏輯
            if (!chatLoading && Widgets.ButtonText(clearBtnRect, "RimLLM_ClearBtn".Translate()))
            {
                chatHistory.Clear();
                Settings.ChatHistory.Clear();
                Settings.Write();
                chatInput = "";
            }
            
            bool pressEnter = (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return);
            
            if (!chatLoading && (Widgets.ButtonText(sendBtnRect, "RimLLM_Send".Translate()) || pressEnter))
            {
                if (!string.IsNullOrEmpty(chatInput))
                {
                    string trimmedInput = chatInput.Trim();
                    if (trimmedInput == "/clear")
                    {
                        chatHistory.Clear();
                        Settings.ChatHistory.Clear();
                        Settings.Write();
                        chatInput = "";
                    }
                    else
                    {
                        string userPrompt = trimmedInput;
                        chatHistory.Add("RimLLM_ChatUser".Translate() + " " + userPrompt);
                        
                        // 先新增一個 AI 回覆的佔位項目，以利後續串流更新
                        chatHistory.Add("RimLLM_ChatAi".Translate() + " ");
                        int aiHistoryIndex = chatHistory.Count - 1;
                        
                        Settings.ChatHistory = new List<string>(chatHistory);
                        Settings.Write();
                        chatLoading = true;
                        chatInput = "";
                        
                        object replyLock = new object();
                        string accumulatedReply = "";
                        
                        Task.Run(async () =>
                        {
                            try
                            {
                                var request = new LLMRequest
                                {
                                    ModId = "RimLLM.DebugChat",
                                    Prompt = userPrompt,
                                    MaxTokens = 4096,
                                    Temperature = 0.7f,
                                    ReasoningEffort = chatReasoningEffort,
                                    EnableStreaming = true,
                                    OnChunkReceived = chunk =>
                                    {
                                        string localReply;
                                        lock (replyLock)
                                        {
                                            accumulatedReply += chunk;
                                            localReply = FormatThinkProcess(accumulatedReply);
                                        }
                                        RimLLMDispatcher.Instance.Enqueue(() =>
                                        {
                                            if (aiHistoryIndex < chatHistory.Count)
                                            {
                                                chatHistory[aiHistoryIndex] = "RimLLM_ChatAi".Translate() + " " + localReply;
                                                chatScrollPosition.y = 999999f;
                                            }
                                        });
                                    }
                                };
                                string finalReply = await RimLLMProvider.Instance.GenerateAsync(request).ConfigureAwait(false);
                                string formattedFinal = FormatThinkProcess(finalReply);
                                RimLLMDispatcher.Instance.Enqueue(() =>
                                {
                                    if (aiHistoryIndex < chatHistory.Count)
                                    {
                                        chatHistory[aiHistoryIndex] = "RimLLM_ChatAi".Translate() + " " + formattedFinal;
                                    }
                                    Settings.ChatHistory = new List<string>(chatHistory);
                                    Settings.Write();
                                    chatLoading = false;
                                    chatScrollPosition.y = 999999f;
                                });
                            }
                            catch (Exception ex)
                            {
                                RimLLMDispatcher.Instance.Enqueue(() =>
                                {
                                    if (aiHistoryIndex < chatHistory.Count)
                                    {
                                        chatHistory[aiHistoryIndex] = "RimLLM_ChatAiError".Translate() + " <color=#ef4444>" + ex.Message + "</color>";
                                    }
                                    else
                                    {
                                        chatHistory.Add("RimLLM_ChatAiError".Translate() + " <color=#ef4444>" + ex.Message + "</color>");
                                    }
                                    Settings.ChatHistory = new List<string>(chatHistory);
                                    Settings.Write();
                                    chatLoading = false;
                                    chatScrollPosition.y = 999999f;
                                });
                            }
                        });
                    }
                }
            }
        }
        private static string FormatThinkProcess(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (Settings.DetailedLogging)
            {
                RimLLMLog.Message($"[RimLLM-DEBUG] FormatThinkProcess Input: {text}");
            }
            // 1. 處理已閉合的 <think>...</think> 或 <thought>...</thought> -> 標記為灰色
            string result = System.Text.RegularExpressions.Regex.Replace(text, @"<(think|thought)>([\s\S]*?)</\1>", m =>
            {
                string thinkContent = m.Groups[2].Value.Trim();
                return string.IsNullOrEmpty(thinkContent) ? "" : $"\n<color=silver>[思考過程]\n{thinkContent}\n[/思考過程]</color>\n\n";
            });
            // 2. 處理未閉合的 <think> 或 <thought>（串流中常遇到） -> 將後續全部標記為灰色
            var matchUnclosedXml = System.Text.RegularExpressions.Regex.Match(result, @"<(think|thought)>(?![\s\S]*</\1>)");
            if (matchUnclosedXml.Success)
            {
                int index = matchUnclosedXml.Index;
                string before = result.Substring(0, index);
                string after = result.Substring(index + matchUnclosedXml.Length);
                result = before + $"\n<color=silver>[思考中...]\n{after} ...</color>";
            }
            // 3. 處理已閉合的 markdown 思考區塊 ```thought...``` -> 標記為灰色
            result = System.Text.RegularExpressions.Regex.Replace(result, @"```thought([\s\S]*?)```", m =>
            {
                string thinkContent = m.Groups[1].Value.Trim();
                return string.IsNullOrEmpty(thinkContent) ? "" : $"\n<color=silver>[思考過程]\n{thinkContent}\n[/思考過程]</color>\n\n";
            });
            // 4. 處理未閉合的 markdown 思考區塊 ```thought （串流中常遇到）-> 將後續全部標記為灰色
            if (result.Contains("```thought"))
            {
                int index = result.IndexOf("```thought");
                string before = result.Substring(0, index);
                string after = result.Substring(index + 10);
                result = before + $"\n<color=silver>[思考中...]\n{after} ...</color>";
            }
            return result.Trim();
        }
        private void ExportDiagnostics()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== RimLLM Framework Diagnostics ===");
                sb.AppendLine($"Export Time: {DateTime.Now}");
                sb.AppendLine($"OS: {SystemInfo.operatingSystem}");
                string maskedDeviceId = "Masked_";
                try
                {
                    string rawId = SystemInfo.deviceUniqueIdentifier;
                    if (!string.IsNullOrEmpty(rawId) && rawId.Length > 8)
                    {
                        maskedDeviceId = rawId.Substring(0, 4) + "****" + rawId.Substring(rawId.Length - 4);
                    }
                    else
                    {
                        maskedDeviceId = rawId;
                    }
                }
                catch
                {
                    maskedDeviceId = "Unknown";
                }
                sb.AppendLine($"Device ID: {maskedDeviceId}");
                sb.AppendLine($"Max Concurrent Requests Setting: {Settings.MaxConcurrentRequests}");
                sb.AppendLine($"Timeout Setting: {Settings.ApiTimeout}s");
                sb.AppendLine($"Max Retries Setting: {Settings.MaxRetries}");
                sb.AppendLine($"Retry Delay Setting: {Settings.RetryDelay}s");
                sb.AppendLine($"Detailed Logging: {Settings.DetailedLogging}");
                sb.AppendLine();
                
                sb.AppendLine("=== Fallback Chain ===");
                for (int i = 0; i < Settings.FallbackChain.Count; i++)
                {
                    sb.AppendLine($"  {i+1}. {Settings.FallbackChain[i]}");
                }
                sb.AppendLine();
                sb.AppendLine("=== Provider Setup ===");
                var providers = new[] { "Gemini", "OpenAI", "DeepSeek", "Groq", "Anthropic", "OpenRouter", "Kimi", "MiniMax", "Qwen", "Nvidia", "OpenAICompatible" };
                foreach (var prov in providers)
                {
                    bool enabled = Settings.IsProviderEnabled(prov);
                    bool hasKey = !string.IsNullOrEmpty(Settings.GetApiKey(prov));
                    string endpoint = Settings.GetEndpoint(prov, "default");
                    sb.AppendLine($"  {prov}: Enabled={enabled}, HasKey={hasKey}, Endpoint={endpoint}");
                    var models = Settings.GetModelList(prov);
                    sb.AppendLine($"    Cached Models ({models.Count}): {string.Join(", ", models)}");
                }
                sb.AppendLine();
                sb.AppendLine("=== Recent Request Logs ===");
                if (RimLLMProvider.Instance is RimLLMManager manager)
                {
                    var logs = manager.RequestLogs.ToArray();
                    if (logs.Length == 0)
                    {
                        sb.AppendLine("  No requests recorded in this session.");
                    }
                    else
                    {
                        foreach (var log in logs)
                        {
                            string status = log.Success ? "SUCCESS" : $"FAILED ({log.ErrorMessage})";
                            sb.AppendLine($"  [{log.Timestamp:yyyy-MM-dd HH:mm:ss}] Mod: {log.ModId} | Provider: {log.Provider} ({log.Model}) | {status} | Latency: {log.LatencyMs}ms");
                        }
                    }
                }
                string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimLLM_Diagnostic.txt");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Messages.Message("RimLLM_ExportDiagSuccess".Translate(path), MessageTypeDefOf.PositiveEvent, false);
            }
            catch (Exception ex)
            {
                Messages.Message("RimLLM_ExportDiagFailed".Translate(ex.Message), MessageTypeDefOf.RejectInput, false);
            }
        }
        private void StartFetchModels(string providerId)
        {
            if (providerId != "OpenAICompatible")
            {
                string apiKey = Settings.GetApiKey(providerId);
                if (string.IsNullOrEmpty(apiKey))
                {
                    FetchStatus[providerId] = "RimLLM_EnterApiKey".Translate();
                    return;
                }
            }
            Fetching[providerId] = true;
            FetchStatus[providerId] = "RimLLM_Fetching".Translate();
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
                            FetchStatus[providerId] = "RimLLM_FetchSuccessCount".Translate(models.Count);
                        }
                        else
                        {
                            FetchStatus[providerId] = "RimLLM_FetchSuccessEmpty".Translate();
                        }
                    });
                }
                catch (Exception ex)
                {
                    RimLLMDispatcher.Instance.Enqueue(() =>
                    {
                        Fetching[providerId] = false;
                        FetchStatus[providerId] = "RimLLM_FetchFailed".Translate() + " (" + ex.Message + ")";
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
                    TestStatus[providerId] = "RimLLM_EnterApiKey".Translate();
                    return;
                }
            }
 
            Testing[providerId] = true;
            TestStatus[providerId] = "RimLLM_Testing".Translate();
 
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
                            TestStatus[providerId] = "RimLLM_TestStatusSuccess".Translate(result.LatencyMs, result.Model);
                        }
                        else
                        {
                            TestStatus[providerId] = "RimLLM_TestStatusFailed".Translate(result.ErrorMessage);
                        }
                    });
                }
                catch (Exception ex)
                {
                    RimLLMDispatcher.Instance.Enqueue(() =>
                    {
                        Testing[providerId] = false;
                        TestStatus[providerId] = "RimLLM_TestStatusError".Translate(ex.Message);
                    });
                }
            });
        }
    }
}
