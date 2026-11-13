using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RimLLM_Framework.Manager;

/// <summary>
/// Embedding 與決策服務共用的備援鏈規則：候選供應商的排序，以及哪些失敗值得換下一家。
/// 對話有自己的備援管線（<see cref="RimLLMFallbackPipeline"/>，含重試、熔斷與路由策略）；
/// 這兩個服務沒有逐模型的分流需求，只需要「主供應商失敗就依序換下一家」。
/// </summary>
internal static class ServiceProviderFailover
{
    /// <summary>
    /// 組出依序嘗試的供應商清單：啟用中的供應商永遠排第一，之後是備援鏈（去空、去重、
    /// 排除與主供應商相同者）。備援鏈只在主供應商已啟用時才有意義，因此主供應商為空或停用時回空清單。
    /// </summary>
    internal static List<string> BuildCandidates(string primary, IEnumerable<string> chain, string disabledId)
    {
        var candidates = new List<string>();
        if (string.IsNullOrEmpty(primary) || string.Equals(primary, disabledId, StringComparison.Ordinal))
        {
            return candidates;
        }

        candidates.Add(primary);
        if (chain == null)
        {
            return candidates;
        }

        foreach (string entry in chain)
        {
            if (string.IsNullOrWhiteSpace(entry) ||
                string.Equals(entry, disabledId, StringComparison.Ordinal) ||
                candidates.Contains(entry))
            {
                continue;
            }
            candidates.Add(entry);
        }
        return candidates;
    }

    /// <summary>
    /// 依序嘗試候選，前面的失敗（<see cref="ShouldFailOver"/>）就換下一個；最後一個候選的例外原樣往外丟，
    /// 保留它自己的堆疊。呼叫端必須先把用不了的備援濾掉：候選不可為空，且最後一個失敗就是最終答案。
    /// 兩個服務共用這一個迴圈，換家的規則才不會各寫一份而漂移。
    /// </summary>
    /// <param name="attempt">以候選與「是否為最後一個」執行一次嘗試。</param>
    /// <param name="onFailover">某個候選失敗而要換下一個時的通知（記日誌用）。</param>
    internal static async Task<TResult> RunAsync<TCandidate, TResult>(
        IReadOnlyList<TCandidate> candidates,
        Func<TCandidate, bool, Task<TResult>> attempt,
        Action<TCandidate, Exception> onFailover)
    {
        for (int i = 0; i < candidates.Count - 1; i++)
        {
            try
            {
                return await attempt(candidates[i], false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldFailOver(ex))
            {
                onFailover?.Invoke(candidates[i], ex);
            }
        }
        return await attempt(candidates[candidates.Count - 1], true).ConfigureAwait(false);
    }

    /// <summary>
    /// 這個失敗是否該換下一個供應商。換家能解決的：供應商端的暫時性問題、金鑰或額度問題、
    /// 模型不存在（備援供應商各有自己的模型設定）。換家無法解決的：呼叫端取消、
    /// 內容被過濾與輸入超過上下文上限，這些換一家送同樣內容多半一樣失敗，直接把錯誤還給呼叫端。
    /// </summary>
    internal static bool ShouldFailOver(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return false;
        }

        if (ex is RimLLMException rimEx)
        {
            // 請求本身被拒（400／413／422）是內容的問題，換一家送同樣的內容只會多花一份額度。
            if (rimEx.HttpStatusCode is 400 or 413 or 422)
            {
                return false;
            }

            // Unknown 是「無從分類」（含呼叫端參數錯誤），沒有證據顯示換一家會好，不列入。
            return rimEx.Error switch
            {
                LLMError.Timeout or LLMError.RateLimit or LLMError.InvalidKey or LLMError.ProviderOffline
                    or LLMError.InvalidResponse or LLMError.NetworkError or LLMError.ModelNotFound
                    or LLMError.QuotaExceeded => true,
                _ => false,
            };
        }

        return false;
    }
}
