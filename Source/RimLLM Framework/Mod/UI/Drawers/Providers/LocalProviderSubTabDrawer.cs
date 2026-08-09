using System;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimWorld;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 處理 Ollama / LM Studio 等本地模型自動偵測與繪製邏輯。
    /// </summary>
    public static class LocalProviderSubTabDrawer
    {
        private static RimLLMFrameworkSettings Settings => RimLLMFrameworkMod.Settings;

        /// <summary>
        /// 偵測用的共用 HttpClient。每次偵測 new 一個會累積未釋放的連線並讓 DNS 結果失效，
        /// 因此改為靜態重用；逾時對本機探測固定為 600ms。
        /// </summary>
        private static readonly System.Net.Http.HttpClient DetectClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(600)
        };

        public static bool IsDetectingLocal { get; private set; } = false;
        public static string DetectStatusMsg { get; private set; } = "";

        public static void DrawLocalDetectionControls(Listing_Standard listing, string providerId)
        {
            Rect detectRect = listing.GetRect(30f);
            Rect detectBtnRect = new Rect(detectRect.x, detectRect.y, 250f, detectRect.height);
            Rect detectStatusRect = new Rect(detectRect.x + 260f, detectRect.y, detectRect.width - 260f, detectRect.height);

            if (IsDetectingLocal)
            {
                GUI.color = Color.gray;
                Widgets.ButtonText(detectBtnRect, "RimLLM_DetectingLocal".Translate());
                GUI.color = Color.white;
            }
            else
            {
                if (Widgets.ButtonText(detectBtnRect, "RimLLM_DetectLocalBtn".Translate()))
                {
                    StartDetectLocalEndpoint(providerId);
                }
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(detectStatusRect, DetectStatusMsg);
            Text.Anchor = TextAnchor.UpperLeft;

            listing.Gap(4f);
        }

        public static void StartDetectLocalEndpoint(string providerId = "OpenAICompatible")
        {
            IsDetectingLocal = true;
            DetectStatusMsg = "RimLLM_DetectingLocal".Translate();

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

                foreach (var target in targets)
                {
                    try
                    {
                        var response = await DetectClient.GetAsync(target.TestUrl).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            string finalUrl = target.BaseUrl;
                            if (target.Name == "Ollama (Raw)")
                            {
                                finalUrl = "http://localhost:11434/v1";
                            }

                            RimLLMDispatcher.EnqueueOnMainThread(() =>
                            {
                                Settings.SetEndpoint(providerId, finalUrl);
                                Settings.Write();
                                IsDetectingLocal = false;
                                DetectStatusMsg = "RimLLM_DetectSuccess".Translate(target.Name, finalUrl);
                                Messages.Message("RimLLM_MsgDetectSuccess".Translate(target.Name), MessageTypeDefOf.PositiveEvent, false);
                            });
                            return;
                        }
                    }
                    catch
                    {
                        // 探測失敗屬正常情形（服務未啟動），繼續試下一個候選端點。
                    }
                }

                RimLLMDispatcher.EnqueueOnMainThread(() =>
                {
                    IsDetectingLocal = false;
                    DetectStatusMsg = "RimLLM_DetectFailed".Translate();
                    Messages.Message("RimLLM_MsgDetectFailed".Translate(), MessageTypeDefOf.RejectInput, false);
                });
            });
        }
    }
}
