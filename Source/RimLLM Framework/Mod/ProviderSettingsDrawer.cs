using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimWorld;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 負責 API 供應商（Providers）分頁的 UI 渲染與交互狀態管理。
    /// </summary>
    public static class ProviderSettingsDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        // 供應商分頁專屬的 UI 暫存狀態
        public static string ActiveProviderSubTab { get; set; } = "Gemini";
        private static Vector2 _midScrollPosition = Vector2.zero;
        private static Vector2 _detailScrollPosition = Vector2.zero;
        private static readonly Dictionary<string, string> FetchStatus = new Dictionary<string, string>();
        private static readonly Dictionary<string, bool> Fetching = new Dictionary<string, bool>();
        private static readonly Dictionary<string, string> TestStatus = new Dictionary<string, string>();
        private static readonly Dictionary<string, bool> Testing = new Dictionary<string, bool>();
        private static readonly Dictionary<string, Vector2> ModelScrollPositions = new Dictionary<string, Vector2>();
        private static bool isDetectingLocal = false;
        private static string detectStatusMsg = "";

        /// <summary>
        /// 獲取供應商設定詳細內容的滾動高度。
        /// </summary>
        public static float GetHeight(float width)
        {
            bool enabled = Settings.IsProviderEnabled(ActiveProviderSubTab);
            if (!enabled) return 120f;

            int modelCount = Settings.GetModelList(ActiveProviderSubTab).Count;
            float modelSectionHeight = modelCount > 0 ? 280f : 60f;

            // 動態計算 API 金鑰列表的高度
            float keysHeight = 0f;
            if (ActiveProviderSubTab != "OpenAICompatible")
            {
                string rawApiKey = Settings.GetApiKey(ActiveProviderSubTab);
                var keys = rawApiKey.Split(new char[] { ',' }, StringSplitOptions.None);
                int keyCount = Mathf.Max(1, keys.Length);
                keysHeight = 30f + (keyCount * 32f) + 36f;
            }

            float extraHeight = 0f;
            if (ActiveProviderSubTab == "Kimi" || ActiveProviderSubTab == "MiniMax" || ActiveProviderSubTab == "Qwen")
            {
                extraHeight = 30f;
            }
            else if (ActiveProviderSubTab == "OpenAICompatible")
            {
                extraHeight = 60f;
            }
            return 250f + keysHeight + modelSectionHeight + 100f + extraHeight;
        }

        /// <summary>
        /// 繪製中欄的供應商選單。
        /// </summary>
        public static void DrawMiddleProviderMenu(Rect rect)
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

        private static void DrawProviderSubButton(Listing_Standard listing, string label, string providerId)
        {
            Rect btnRect = listing.GetRect(46f);

            if (ActiveProviderSubTab == providerId)
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
                ActiveProviderSubTab = providerId;
                _detailScrollPosition = Vector2.zero;
            }

            Rect nameRect = new Rect(btnRect.x + 8f, btnRect.y + 3f, btnRect.width - 16f, 22f);
            Rect statusRect = new Rect(btnRect.x + 8f, btnRect.y + 25f, btnRect.width - 16f, 18f);
            Text.Font = GameFont.Small;
            string nameText = ActiveProviderSubTab == providerId ? $"<color=white><b>{label}</b></color>" : $"<color=silver>{label}</color>";
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

        /// <summary>
        /// 根據目前的供應商，調度右側的詳細配置渲染。
        /// </summary>
        public static void DrawRightDetailContent(Listing_Standard listing)
        {
            if (ActiveProviderSubTab == "Gemini")
                DrawProviderSettings(listing, "Gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash");
            else if (ActiveProviderSubTab == "OpenAI")
                DrawProviderSettings(listing, "OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-4o-mini");
            else if (ActiveProviderSubTab == "DeepSeek")
                DrawProviderSettings(listing, "DeepSeek", "https://api.deepseek.com", "deepseek-chat");
            else if (ActiveProviderSubTab == "Groq")
                DrawProviderSettings(listing, "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile");
            else if (ActiveProviderSubTab == "Anthropic")
                DrawProviderSettings(listing, "Anthropic", "https://api.anthropic.com/v1/messages", "claude-3-5-sonnet-20241022");
            else if (ActiveProviderSubTab == "OpenRouter")
                DrawProviderSettings(listing, "OpenRouter", "https://openrouter.ai/api/v1", "google/gemini-2.5-flash");
            else if (ActiveProviderSubTab == "Kimi")
                DrawProviderSettings(listing, "Kimi", "https://api.moonshot.ai/v1", "moonshot-v1-8k");
            else if (ActiveProviderSubTab == "MiniMax")
                DrawProviderSettings(listing, "MiniMax", "https://api.minimax.io/v1", "abab6.5g-chat");
            else if (ActiveProviderSubTab == "Qwen")
                DrawProviderSettings(listing, "Qwen", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-plus");
            else if (ActiveProviderSubTab == "Nvidia")
                DrawProviderSettings(listing, "Nvidia", "https://integrate.api.nvidia.com/v1", "meta/llama-3.1-8b-instruct");
            else if (ActiveProviderSubTab == "OpenAICompatible")
                DrawProviderSettings(listing, "OpenAICompatible", "http://localhost:1234/v1", "default", isLocal: true);
        }

        private static void DrawProviderSettings(Listing_Standard listing, string providerId, string defaultEndpoint, string defaultModel, bool isLocal = false)
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

                // 3. Endpoint 設定
                if (isLocal)
                {
                    string endpoint = Settings.GetEndpoint(providerId, defaultEndpoint);
                    listing.Label("RimLLM_ApiEndpoint".Translate());
                    endpoint = listing.TextEntry(endpoint);
                    Settings.SetEndpoint(providerId, endpoint?.Trim());

                    if (providerId == "OpenAICompatible")
                    {
                        Rect detectRect = listing.GetRect(30f);
                        Rect detectBtnRect = new Rect(detectRect.x, detectRect.y, 250f, detectRect.height);
                        Rect detectStatusRect = new Rect(detectRect.x + 260f, detectRect.y, detectRect.width - 260f, detectRect.height);

                        if (isDetectingLocal)
                        {
                            GUI.color = Color.gray;
                            Widgets.ButtonText(detectBtnRect, "RimLLM_DetectingLocal".Translate());
                            GUI.color = Color.white;
                        }
                        else
                        {
                            if (Widgets.ButtonText(detectBtnRect, "RimLLM_DetectLocalBtn".Translate()))
                            {
                                StartDetectLocalEndpoint();
                            }
                        }

                        Text.Anchor = TextAnchor.MiddleLeft;
                        Widgets.Label(detectStatusRect, detectStatusMsg);
                        Text.Anchor = TextAnchor.UpperLeft;
                        listing.Gap(4f);
                    }
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

        private static void StartFetchModels(string providerId)
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

        private static void StartTest(string providerId)
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

        private static void StartDetectLocalEndpoint()
        {
            isDetectingLocal = true;
            detectStatusMsg = "RimLLM_DetectingLocal".Translate();

            Task.Run(async () =>
            {
                var targets = new (string Name, string BaseUrl, string TestUrl)[]
                {
                    ("LM Studio", "http://localhost:1234/v1", "http://localhost:1234/v1/models"),
                    ("Ollama", "http://localhost:11434/v1", "http://localhost:11434/v1/models"),
                    ("Ollama (Raw)", "http://localhost:11434", "http://localhost:11434/api/tags"),
                    ("LocalAI/vLLM (8080)", "http://localhost:8080/v1", "http://localhost:8080/v1/models"),
                    ("LocalAI/vLLM (8000)", "http://localhost:8000/v1", "http://localhost:8000/v1/models")
                };

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromMilliseconds(600);
                    foreach (var target in targets)
                    {
                        try
                        {
                            var response = await client.GetAsync(target.TestUrl).ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                string finalUrl = target.BaseUrl;
                                if (target.Name == "Ollama (Raw)")
                                {
                                    finalUrl = "http://localhost:11434/v1";
                                }

                                RimLLMDispatcher.Instance.Enqueue(() =>
                                {
                                    Settings.SetEndpoint("OpenAICompatible", finalUrl);
                                    Settings.Write();
                                    isDetectingLocal = false;
                                    detectStatusMsg = "RimLLM_DetectSuccess".Translate(target.Name, finalUrl);
                                    Messages.Message("RimLLM_MsgDetectSuccess".Translate(target.Name), MessageTypeDefOf.PositiveEvent, false);
                                });
                                return;
                            }
                        }
                        catch
                        {
                            // Ignore
                        }
                    }
                }

                RimLLMDispatcher.Instance.Enqueue(() =>
                {
                    isDetectingLocal = false;
                    detectStatusMsg = "RimLLM_DetectFailed".Translate();
                    Messages.Message("RimLLM_MsgDetectFailed".Translate(), MessageTypeDefOf.RejectInput, false);
                });
            });
        }
    }
}
