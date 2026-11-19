using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimWorld;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 本地模型端點探測的共用核心：依序 GET 各候選的測試 URL，回傳第一個成功的項目。
/// 供應商分頁與 Embedding 分頁的按鈕狀態與成功回寫各不相同，因此 UI 狀態仍留在各 drawer，
/// 這裡收斂「逐一探測」的迴圈、HttpClient，以及背景探測後回主線程回報的流程。
/// </summary>
internal static class LocalEndpointProbe
{
    // 本地供應商的預設連接位址；供應商分頁與 Embedding 分頁共用，改版時只需改一處。
#pragma warning disable S1075 // 本地服務的產品預設位址，不在執行期變動。
    private const string ModelsSuffix = "/models";
    internal const string OllamaBaseUrl = "http://localhost:11434/v1";
    internal const string OllamaV1ModelsUrl = OllamaBaseUrl + ModelsSuffix;
    internal const string OllamaApiTagsUrl = "http://localhost:11434/api/tags";
    internal const string LmStudioBaseUrl = "http://localhost:1234/v1";
    internal const string LmStudioV1ModelsUrl = LmStudioBaseUrl + ModelsSuffix;
    internal const string LocalAI8080BaseUrl = "http://localhost:8080/v1";
    internal const string LocalAI8080V1ModelsUrl = LocalAI8080BaseUrl + ModelsSuffix;
    internal const string LocalAI8000BaseUrl = "http://localhost:8000/v1";
    internal const string LocalAI8000V1ModelsUrl = LocalAI8000BaseUrl + ModelsSuffix;
#pragma warning restore S1075

    internal sealed class Target
    {
        /// <summary>顯示名稱（探測成功訊息用）。</summary>
        public string Name;
        /// <summary>探測成功後寫入設定的端點。</summary>
        public string BaseUrl;
        /// <summary>實際送出探測請求的位址。與 BaseUrl 分開是因為 Ollama 原生位址（/api/tags）
        /// 與 OpenAI 相容位址（/v1）不同：探測打前者，寫入後者，呼叫端傳入時就填好兩邊。</summary>
        public string TestUrl;
    }

    // SDK 豁免：只探測本機服務是否在線（含 Ollama 原生 /api/tags），不是 LLM 請求。
#pragma warning disable RS0030
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(600)
    };
#pragma warning restore RS0030

    /// <summary>回傳第一個可連通的候選（原樣回傳，不改寫任何欄位），全部失敗則回傳 null。</summary>
    /// <param name="targets">候選端點。</param>
    internal static async Task<Target> FindReachableAsync(IEnumerable<Target> targets)
    {
        if (targets == null) return null;
        foreach (Target target in targets)
        {
            try
            {
                using HttpResponseMessage response = await SharedClient.GetAsync(target.TestUrl).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return target;
                }
            }
            catch
            {
                // 探測失敗屬正常情形（服務未啟動），繼續試下一個候選端點。
            }
        }
        return null;
    }

    /// <summary>
    /// 在背景探測，結果回到主線程後先交給呼叫端回寫設定與 UI 狀態，再彈出成功或失敗訊息。
    /// </summary>
    /// <param name="targets">候選端點。</param>
    /// <param name="onFound">探測成功時在主線程執行：寫入端點、更新該頁的狀態文字。</param>
    /// <param name="onMissing">全部失敗時在主線程執行：更新該頁的狀態文字。</param>
    internal static void StartDetect(IEnumerable<Target> targets, Action<Target> onFound, Action onMissing)
    {
        Task.Run(async () =>
        {
            Target hit = await FindReachableAsync(targets).ConfigureAwait(false);
            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                if (hit != null)
                {
                    onFound(hit);
                    Messages.Message("RimLLM_MsgDetectSuccess".Translate(hit.Name), MessageTypeDefOf.PositiveEvent, false);
                }
                else
                {
                    onMissing();
                    Messages.Message("RimLLM_MsgDetectFailed".Translate(), MessageTypeDefOf.RejectInput, false);
                }
            });
        });
    }
}
