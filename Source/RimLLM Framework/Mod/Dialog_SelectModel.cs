using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 自訂模型選擇視窗，支援關鍵字過濾搜尋與滾動列表，以解決模型選項過多導致 UI 混亂的問題。
    /// </summary>
    public class Dialog_SelectModel : Window
    {
        private readonly List<string> _allModels;
        private readonly Action<string> _onSelected;
        private string _filter = "";
        private Vector2 _scrollPosition = Vector2.zero;

        public override Vector2 InitialSize => new Vector2(550f, 650f);

        public Dialog_SelectModel(List<string> models, Action<string> onSelected)
        {
            this._allModels = models ?? new List<string>();
            this._onSelected = onSelected;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 1. 標題
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "RimLLM_SelectModelTitle".Translate());
            Text.Font = GameFont.Small;

            // 2. 搜尋框
            Rect searchLabelRect = new Rect(0f, 40f, 70f, 30f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(searchLabelRect, "RimLLM_Search".Translate() + ": ");
            Text.Anchor = TextAnchor.UpperLeft;

            Rect filterRect = new Rect(75f, 40f, inRect.width - 75f, 30f);
            _filter = Widgets.TextField(filterRect, _filter);

            // 3. 過濾模型清單
            List<string> filteredModels = new List<string>();
            string filterLower = _filter.ToLower();
            foreach (var m in _allModels)
            {
                if (string.IsNullOrEmpty(_filter) || m.ToLower().Contains(filterLower))
                {
                    filteredModels.Add(m);
                }
            }

            // 4. 滾動清單區
            float topOffset = 80f;
            float bottomOffset = 55f; // 為關閉按鈕留空間
            Rect listRect = new Rect(0f, topOffset, inRect.width, inRect.height - topOffset - bottomOffset);
            Widgets.DrawMenuSection(listRect);

            if (_allModels.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(listRect, "<color=gray>" + "RimLLM_NoCachedModels".Translate() + "</color>");
                Text.Anchor = TextAnchor.UpperLeft;
            }
            else if (filteredModels.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(listRect, "<color=gray>" + "RimLLM_NoMatchingModels".Translate() + "</color>");
                Text.Anchor = TextAnchor.UpperLeft;
            }
            else
            {
                float contentWidth = listRect.width - 16f;
                float rowHeight = 36f;
                float viewHeight = Math.Max(listRect.height, filteredModels.Count * rowHeight + 10f);
                Rect viewRect = new Rect(0f, 0f, contentWidth, viewHeight);

                Widgets.BeginScrollView(listRect, ref _scrollPosition, viewRect);

                for (int i = 0; i < filteredModels.Count; i++)
                {
                    string model = filteredModels[i];
                    Rect rowRect = new Rect(4f, i * rowHeight + 4f, contentWidth - 8f, rowHeight - 2f);

                    if (Widgets.ButtonText(rowRect, model, true, true, true))
                    {
                        _onSelected?.Invoke(model);
                        Close();
                        break;
                    }
                }

                Widgets.EndScrollView();
            }
        }
    }
}
