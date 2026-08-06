using System;
using Verse;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 預算超限詢問對話框。
    /// 相較於直接使用 <see cref="Dialog_MessageBox"/>，此類別額外保證：
    /// 視窗以任何方式關閉（含 ESC、切換場景）時都會執行收尾回呼，
    /// 使等待中的請求不會永久阻塞在一個永遠不會完成的 TaskCompletionSource 上。
    /// </summary>
    public class Dialog_BudgetPrompt : Dialog_MessageBox
    {
        private readonly Action _onDismissed;
        private bool _resolved;

        public Dialog_BudgetPrompt(
            string text,
            string approveLabel,
            Action approveAction,
            string declineLabel,
            Action declineAction,
            Action onDismissed)
            : base(text, approveLabel, null, declineLabel, null, null, false, null, null)
        {
            _onDismissed = onDismissed;

            buttonAAction = () =>
            {
                _resolved = true;
                approveAction?.Invoke();
            };
            buttonBAction = () =>
            {
                _resolved = true;
                declineAction?.Invoke();
            };
        }

        public override void PostClose()
        {
            base.PostClose();

            // 使用者沒有按任何按鈕就關閉視窗（例如按下 ESC）。
            if (!_resolved)
            {
                _resolved = true;
                _onDismissed?.Invoke();
            }
        }
    }
}
