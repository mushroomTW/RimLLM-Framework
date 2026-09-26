namespace RimLLM_Framework
{
    /// <summary>
    /// 每日預算超支判斷的唯一入口：token 帳本優先，未實作的外來設定退回舊版美元門檻。
    /// 用量追蹤、中介層與備援管線一律走這裡，避免三處各自重寫同一組條件。
    /// </summary>
    internal static class DailyTokenBudget
    {
        public static bool IsOverBudget(IRimLLMSettings settings)
        {
            if (settings is IDailyTokenBudget tokenLedger)
            {
                return tokenLedger.DailyTokenBudgetLimit > 0 &&
                    tokenLedger.DailyAccumulatedTokens >= tokenLedger.DailyTokenBudgetLimit;
            }

            return settings.DailyBudgetLimit > 0f &&
                settings.DailyAccumulatedCost >= settings.DailyBudgetLimit;
        }
    }
}
