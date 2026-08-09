using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 設定介面共用的樣式常數與繪製輔助。
    ///
    /// 顏色集中在此，是因為同一種語意（選中、次要文字、成功、危險）先前以字面值散落在各個
    /// drawer，任何調整都得逐檔搜尋，實際上也已經出現同語意不同值的情形。
    /// 這裡的值直接沿用原本的硬編值，收攏本身不改變外觀。
    /// </summary>
    public static class RimLLMUIStyle
    {
        /// <summary>選中項目的底色。</summary>
        public static readonly Color SelectionFill = new Color(1f, 1f, 1f, 0.08f);

        /// <summary>模型 chip 的底色。</summary>
        public static readonly Color ChipFill = new Color(1f, 1f, 1f, 0.05f);

        /// <summary>停用或次要狀態的文字色。</summary>
        public static readonly Color Muted = new Color(0.53f, 0.53f, 0.53f);

        /// <summary>啟用狀態的文字色。</summary>
        public static readonly Color Success = new Color(0.13f, 0.77f, 0.37f);

        /// <summary>進度條未填滿的底色。</summary>
        public static readonly Color BarTrack = new Color(0.2f, 0.2f, 0.2f, 0.6f);

        /// <summary>失敗比例的底色（成功率條的背景）。</summary>
        public static readonly Color BarTrackDanger = new Color(0.35f, 0.15f, 0.15f, 0.6f);

        /// <summary>成功率條的填色。</summary>
        public static readonly Color BarFillSuccess = new Color(0.18f, 0.48f, 0.18f, 0.8f);

        /// <summary>快取命中率條的填色。</summary>
        public static readonly Color BarFillCache = new Color(0.15f, 0.45f, 0.6f, 0.8f);

        /// <summary>
        /// 暫時改變 <see cref="Text"/> 的全域繪製狀態，離開作用域自動還原。
        ///
        /// RimWorld 的 Text.Anchor / Font / WordWrap 是全域可變狀態，各處手動設值再手動還原；
        /// 只要中途拋出例外，還原那行就不會執行，之後整個遊戲的 UI 都會沿用被改壞的狀態。
        /// 以 using 包住可確保還原一定發生。
        /// </summary>
        public static Scope With(TextAnchor? anchor = null, GameFont? font = null, bool? wordWrap = null)
        {
            return new Scope(anchor, font, wordWrap);
        }

        /// <summary>
        /// <see cref="With"/> 回傳的作用域。以 struct 實作避免每幀配置。
        /// </summary>
        public struct Scope : IDisposable
        {
            private readonly TextAnchor _anchor;
            private readonly GameFont _font;
            private readonly bool _wordWrap;

            internal Scope(TextAnchor? anchor, GameFont? font, bool? wordWrap)
            {
                _anchor = Text.Anchor;
                _font = Text.Font;
                _wordWrap = Text.WordWrap;

                if (anchor.HasValue) Text.Anchor = anchor.Value;
                if (font.HasValue) Text.Font = font.Value;
                if (wordWrap.HasValue) Text.WordWrap = wordWrap.Value;
            }

            public void Dispose()
            {
                Text.Anchor = _anchor;
                Text.Font = _font;
                Text.WordWrap = _wordWrap;
            }
        }

        /// <summary>API 金鑰遮罩時保留的頭尾字元數。</summary>
        private const int MaskedPrefixLength = 8;
        private const int MaskedSuffixLength = 4;

        /// <summary>
        /// 把 API 金鑰轉成可安全顯示於畫面的遮罩形式。
        ///
        /// 保留頭尾是為了讓使用者能辨認自己設定了哪一把金鑰（多把金鑰輪替時特別需要），
        /// 中段一律隱藏。長度不足以安全保留頭尾時整串隱藏 —— 短金鑰露出頭尾等同露出大半內容。
        /// 空字串維持空，否則畫面上會看起來像已經設定過。
        /// </summary>
        public static string MaskApiKey(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey)) return string.Empty;

            if (apiKey.Length <= MaskedPrefixLength + MaskedSuffixLength)
            {
                return new string('•', apiKey.Length);
            }

            return apiKey.Substring(0, MaskedPrefixLength) +
                   "…" +
                   apiKey.Substring(apiKey.Length - MaskedSuffixLength);
        }

        /// <summary>
        /// 以關鍵字過濾模型名稱。空白關鍵字代表不過濾。
        /// 供應商設定頁與 <see cref="Dialog_SelectModel"/> 共用同一份實作，
        /// 否則兩處的比對規則會各自漂移（例如一邊分大小寫、一邊不分）。
        /// </summary>
        public static List<string> FilterModels(IEnumerable<string> models, string filter)
        {
            var result = new List<string>();
            if (models == null) return result;

            bool matchAll = string.IsNullOrEmpty(filter) || filter.Trim().Length == 0;
            string needle = matchAll ? null : filter.Trim().ToLowerInvariant();

            foreach (string model in models)
            {
                if (string.IsNullOrEmpty(model)) continue;
                if (matchAll || model.ToLowerInvariant().IndexOf(needle, StringComparison.Ordinal) >= 0)
                {
                    result.Add(model);
                }
            }
            return result;
        }

        /// <summary>
        /// 依可用寬度算出模型 chip 的欄數與實際寬度。
        ///
        /// 欄數由偏好寬度決定，但寬度改由欄數反推，讓 chip 填滿整列 ——
        /// 先前使用固定寬度，右緣總是留下一條用不到的空隙，長模型名稱卻同時被截斷。
        /// </summary>
        public static void ComputeChipLayout(
            float contentWidth, float gap, float preferredWidth, out int columns, out float chipWidth)
        {
            columns = Mathf.Max(1, Mathf.FloorToInt((contentWidth - gap) / (preferredWidth + gap)));
            chipWidth = Mathf.Max(1f, (contentWidth - gap * (columns + 1)) / columns);
        }
    }
}
