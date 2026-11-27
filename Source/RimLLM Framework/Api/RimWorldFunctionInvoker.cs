using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Api;

/// <summary>
/// 提供 RimWorld / Unity 環境專用的主執行緒安全工具呼叫擴充方法。
/// </summary>
public static class RimWorldFunctionInvoker
{
    /// <summary>
    /// 將 IChatClient 包裝為支援自動多輪工具呼叫（Function Invoking）的客戶端。
    /// 所有工具（AIFunction）的叫用委派都會自動透過 RimLLMDispatcher 排入 Unity 主執行緒執行，
    /// 防止背景執行緒存取遊戲狀態時引發 Unity 跨執行緒崩潰。
    /// </summary>
    /// <param name="innerClient">底層聊天客戶端。</param>
    /// <param name="maxIterations">單次請求允許工具自動執行的最大迴圈次數（預設 10）。</param>
    /// <returns>具備自動主執行緒工具執行能力的 IChatClient。</returns>
    public static IChatClient AsMainThreadFunctionInvokingClient(
        this IChatClient innerClient,
        int maxIterations = 10)
    {
        ArgumentNullException.ThrowIfNull(innerClient);

        return new FunctionInvokingChatClient(innerClient)
        {
            MaximumIterationsPerRequest = maxIterations > 0 ? maxIterations : 10,
            AllowConcurrentInvocation = false, // Unity 為單執行緒環境，禁止工具並行搶佔
            // 工具擲出的例外訊息回給模型，讓它能修正引數重試；預設只回「Error: Function failed.」，
            // 模型無從得知錯在哪裡。
            IncludeDetailedErrors = true,
            FunctionInvoker = async (FunctionInvocationContext context, CancellationToken cancellationToken) =>
            {
                if (context?.Function == null)
                {
                    return null;
                }

                // 內層刻意不加 ConfigureAwait(false)：工具若含 await，續行必須回到主執行緒，
                // 否則後半段仍會在執行緒池碰遊戲狀態。
                // 必須 AsTask()：少了轉換，多載解析會選到 EnqueueOnMainThreadAsync<T>(Func<T>)
                // 且 T = ValueTask<object>，整顆 ValueTask 會被序列化成工具結果。
                try
                {
                    return await RimLLMDispatcher.EnqueueOnMainThreadAsync(
                        () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return context.Function.InvokeAsync(context.Arguments, cancellationToken).AsTask();
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 刻意不附加原始例外，避免外層記錄未清洗的內部訊息。
#pragma warning disable MA0054
                    throw new InvalidOperationException(RimLLMLog.SanitizeForLog(ex.Message, 300) ?? "Function invocation failed.");
#pragma warning restore MA0054
                }
            }
        };
    }
}
