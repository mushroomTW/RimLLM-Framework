using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimWorld;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>本地模型端點的共用探測核心：逐一 GET 候選 URL，回傳第一個成功項目。</summary>
internal static class LocalEndpointProbe
{
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
        public required string Name;
        public required string BaseUrl;
/// <summary>探測用位址；與 BaseUrl 分開，因 Ollama 原生位址（/api/tags）與 OpenAI 相容位址（/v1）不同。</summary>
        public required string TestUrl;
    }

    // SDK 豁免：只探測本機服務是否在線（含 Ollama 原生 /api/tags），不是 LLM 請求。
#pragma warning disable RS0030
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(600)
    };
#pragma warning restore RS0030

    internal static async Task<Target?> FindReachableAsync(IEnumerable<Target> targets)
    {
        if (targets == null) return null;
        foreach (Target target in targets)
        {
            try
            {
                using HttpResponseMessage response = await SharedClient.GetAsync(
                    target.TestUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
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

    internal static void StartDetect(IEnumerable<Target> targets, Action<Target> onFound, Action onMissing)
    {
        _ = Task.Run(async () =>
        {
            Target? hit = await FindReachableAsync(targets).ConfigureAwait(false);
            RimLLMDispatcher.EnqueueOnMainThread(() =>
            {
                if (hit != null)
                {
                    onFound(hit);
                    Messages.Message("RimLLM_MsgDetectSuccess".Translate(hit.Name), MessageTypeDefOf.PositiveEvent, historical: false);
                }
                else
                {
                    onMissing();
                    Messages.Message("RimLLM_MsgDetectFailed".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                }
            });
        });
    }
}
