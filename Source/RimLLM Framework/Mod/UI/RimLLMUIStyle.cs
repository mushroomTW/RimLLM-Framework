using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 設定介面共用的樣式常數與繪製輔助；同一種語意的顏色集中於此。
/// </summary>
public static class RimLLMUIStyle
{
    /// <summary>設定畫面的金色強調：選中項、段落標題；RimAgent 設定頁直接引用同一組色票。</summary>
    public static readonly Color Gold = new(0.94f, 0.78f, 0.45f);
    public static readonly Color SelectedFill = new(0.94f, 0.78f, 0.45f, 0.16f);
    public static readonly Color PanelFill = new(1f, 1f, 1f, 0.03f);
    public static readonly Color PanelBorder = new(0.4f, 0.41f, 0.43f);

    /// <summary>模型 chip 的底色。</summary>
    public static readonly Color ChipFill = new(1f, 1f, 1f, 0.05f);

    /// <summary>停用或次要狀態的文字色。</summary>
    public static readonly Color Muted = new(0.53f, 0.53f, 0.53f);

    /// <summary>啟用狀態的文字色。</summary>
    public static readonly Color Success = new(0.13f, 0.77f, 0.37f);

    /// <summary>進度條未填滿的底色。</summary>
    public static readonly Color BarTrack = new(0.2f, 0.2f, 0.2f, 0.6f);

    /// <summary>失敗比例的底色（成功率條的背景）。</summary>
    public static readonly Color BarTrackDanger = new(0.35f, 0.15f, 0.15f, 0.6f);

    /// <summary>成功率條的填色。</summary>
    public static readonly Color BarFillSuccess = new(0.18f, 0.48f, 0.18f, 0.8f);

    /// <summary>快取命中率條的填色。</summary>
    public static readonly Color BarFillCache = new(0.15f, 0.45f, 0.6f, 0.8f);

    /// <summary>警告狀態文字/圖示色。</summary>
    public static readonly Color Warning = new(0.96f, 0.62f, 0.04f);

    /// <summary>對話測試中使用者發言氣泡的底色。</summary>
    public static readonly Color BubbleUserFill = new(0.14f, 0.24f, 0.35f, 0.45f);

    /// <summary>對話測試中使用者發言氣泡的外框色。</summary>
    public static readonly Color BubbleUserBorder = new(0.28f, 0.48f, 0.70f, 0.65f);

    /// <summary>對話測試中 AI 回覆氣泡的底色。</summary>
    public static readonly Color BubbleAiFill = new(0.12f, 0.12f, 0.12f, 0.55f);

    /// <summary>對話測試中 AI 回覆氣泡的外框色。</summary>
    public static readonly Color BubbleAiBorder = new(0.32f, 0.32f, 0.32f, 0.65f);

    /// <summary>暫時改變 <see cref="Text"/> 的全域繪製狀態，離開作用域自動還原。Text.Anchor / Font / WordWrap 是全域可變狀態，手動還原遇到例外就不會執行，以 using 確保還原一定發生。</summary>
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

        public readonly void Dispose()
        {
            Text.Anchor = _anchor;
            Text.Font = _font;
            Text.WordWrap = _wordWrap;
        }
    }

    private const int MaskedPrefixLength = 8;
    private const int MaskedSuffixLength = 4;

    /// <summary>把 API 金鑰轉成可安全顯示的遮罩形式：保留頭尾以便辨認（多把輪替時），中段隱藏；長度不足以安全保留頭尾時整串隱藏；空字串維持空。</summary>
    public static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return string.Empty;

        if (apiKey.Length <= MaskedPrefixLength + MaskedSuffixLength)
        {
            return new string('•', apiKey.Length);
        }

        return string.Concat(apiKey.AsSpan(0, MaskedPrefixLength), "…", apiKey.AsSpan(apiKey.Length - MaskedSuffixLength));
    }

    /// <summary>
    /// 直連供應商 → 廠商名，取 OpenRouter 模型 ID 的前綴寫法（例如 Gemini 的模型在 OpenRouter 上是 google/…）。
    /// 只列模型全出自單一廠商的供應商；Groq、Nvidia、Player2 這類代管多家模型的服務沒有單一廠商，不列入。
    /// 獨立維護而不沿用 models.dev 的供應商鍵：那張表服務於上下文長度下載，鍵名可能為了下載而調整。
    /// </summary>
    private static readonly Dictionary<string, string> ModelVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProviderIds.OpenAI] = "openai",
        [ProviderIds.Gemini] = "google",
        [ProviderIds.DeepSeek] = "deepseek",
        [ProviderIds.Grok] = "x-ai",
        [ProviderIds.Kimi] = "moonshotai",
        [ProviderIds.MiniMax] = "minimax",
        [ProviderIds.Qwen] = "qwen",
        [ProviderIds.Zai] = "z-ai",
    };

    /// <summary>供應商的模型廠商名，供 <see cref="FilterModels(IEnumerable{string}, string, string)"/> 使用；沒有單一廠商時為 null。</summary>
    public static string? GetModelVendor(string? providerId)
    {
        return providerId != null && ModelVendors.TryGetValue(providerId, out string? vendor) ? vendor : null;
    }

    /// <summary>
    /// 不帶廠商鍵的 <see cref="FilterModels(IEnumerable{string}, string, string)"/>；保留原簽章以免破壞外部呼叫端。
    /// </summary>
    public static List<string> FilterModels(IEnumerable<string> models, string filter)
    {
        return FilterModels(models, filter, vendor: null);
    }

    /// <summary>
    /// 以關鍵字過濾模型名稱。空白關鍵字代表不過濾。
    /// </summary>
    /// <param name="vendor">
    /// 直連供應商的廠商名（見 <see cref="GetModelVendor"/>）。
    /// 直連供應商的模型 ID 不帶廠商前綴（Gemini 是 <c>gemini-2.5-flash</c>，OpenRouter 是 <c>google/gemini-2.5-flash</c>），
    /// 不帶前綴的 ID 會視同 <c>{vendor}/{id}</c> 比對，以廠商名搜尋才不會一筆都找不到。
    /// </param>
    public static List<string> FilterModels(IEnumerable<string> models, string filter, string? vendor)
    {
        var result = new List<string>();
        if (models == null) return result;

        bool matchAll = string.IsNullOrEmpty(filter) || filter.Trim().Length == 0;
        string needle = matchAll ? string.Empty : filter.Trim();

        // 以不分大小寫的比對取代逐筆 ToLowerInvariant：這段每幀對數百筆模型名跑一次，
        // 每筆各配置一個小寫副本只是白白製造 GC 壓力。
        foreach (string model in models)
        {
            if (string.IsNullOrEmpty(model)) continue;
            if (matchAll
                || model.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (vendor != null && !model.Contains('/', StringComparison.Ordinal) && MatchesVendorPrefixed(vendor, model, needle)))
            {
                result.Add(model);
            }
        }
        return result;
    }

    /// <summary>
    /// <c>{vendor}/{model}</c> 是否包含 <paramref name="needle"/>，但不實際串接字串（每次按鍵都對數百筆重算）。
    /// 呼叫前已確認 <paramref name="model"/> 本身不含 needle，因此只剩兩種可能：
    /// needle 落在廠商名內，或橫跨斜線（廠商名結尾 + "/" + 模型開頭）。
    /// </summary>
    private static bool MatchesVendorPrefixed(string vendor, string model, string needle)
    {
        if (vendor.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        int slash = needle.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0
               && vendor.AsSpan().EndsWith(needle.AsSpan(0, slash), StringComparison.OrdinalIgnoreCase)
               && model.AsSpan().StartsWith(needle.AsSpan(slash + 1), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 過濾結果的快取。模型清單與過濾字串都只在使用者操作時變動，但 OnGUI 每幀跑好幾次，
    /// 沒有這層的話每個 pass 都要重新走一遍數百筆比對。
    /// 以呼叫端提供的 <c>cacheKey</c>（通常是供應商 ID）與清單版本號區分項目。
    /// </summary>
    private sealed class FilteredModelsEntry
    {
        public int Version;
        public required string Filter;
        public required string? Vendor;
        public required List<string> Result;
    }

    private static readonly Dictionary<string, FilteredModelsEntry> FilteredModelsCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 不帶廠商鍵的 <see cref="FilterModelsCached(string, int, string, Func{IList{string}}, string)"/>；保留原簽章以免破壞外部呼叫端。
    /// </summary>
    public static List<string> FilterModelsCached(string cacheKey, int version, string filter, Func<IList<string>> sourceProvider)
    {
        return FilterModelsCached(cacheKey, version, filter, sourceProvider, vendor: null);
    }

    /// <summary>
    /// 帶快取的 <see cref="FilterModels(IEnumerable{string}, string, string)"/>。<paramref name="version"/> 或 <paramref name="filter"/>
    /// 與上次相同就直接回傳上次的結果（同一個 List 實例，呼叫端不得改動）。
    /// <paramref name="sourceProvider"/> 只在需要重算時才被呼叫，讓呼叫端連清單複製都能省下。
    /// </summary>
    public static List<string> FilterModelsCached(
        string cacheKey, int version, string filter, Func<IList<string>> sourceProvider, string? vendor)
    {
        ArgumentNullException.ThrowIfNull(sourceProvider);
        filter = filter ?? string.Empty;

        if (FilteredModelsCache.TryGetValue(cacheKey ?? string.Empty, out FilteredModelsEntry? entry) &&
            entry.Version == version &&
            string.Equals(entry.Filter, filter, StringComparison.Ordinal) &&
            string.Equals(entry.Vendor, vendor, StringComparison.Ordinal))
        {
            return entry.Result;
        }

        IList<string> source = sourceProvider();
        entry = new FilteredModelsEntry
        {
            Version = version,
            Filter = filter,
            Vendor = vendor,
            Result = FilterModels(source, filter, vendor)
        };
        FilteredModelsCache[cacheKey ?? string.Empty] = entry;
        return entry.Result;
    }

    /// <summary>算出捲動清單中落在可視範圍內的列區間 [firstRow, endRow)；每列固定高度，只畫可視列以免每幀對數百個晶片做文字量測，前後各多算一列，捲動時不會露出空白。</summary>
    public static void GetVisibleRowRange(float scrollY, float viewportHeight, float rowStride, float topPadding, int rowCount, out int firstRow, out int endRow)
    {
        if (rowCount <= 0 || rowStride <= 0f)
        {
            firstRow = 0;
            endRow = 0;
            return;
        }

        firstRow = Mathf.Clamp(Mathf.FloorToInt((scrollY - topPadding) / rowStride) - 1, 0, rowCount);
        endRow = Mathf.Clamp(Mathf.CeilToInt((scrollY - topPadding + viewportHeight) / rowStride) + 1, firstRow, rowCount);
    }

    /// <summary>
    /// 依字型與寬度分開的截斷快取。RimWorld 的 <c>Truncate</c> 不帶快取時會逐字元呼叫
    /// <c>Text.CalcSize</c> 直到塞得下為止，長模型名在窄晶片裡一次要量十幾回，
    /// 每幀對每個晶片重做是設定頁最貴的單一開銷。原生快取只以字串為鍵，
    /// 寬度或字型一變就會拿到錯的結果，因此這裡再包一層以 (字型, 寬度) 分桶。
    /// </summary>
    private static readonly Dictionary<long, Dictionary<string, string>> TruncateCaches =
        [];

    public static string TruncateCached(string text, float width)
    {
        if (string.IsNullOrEmpty(text)) return text;

        long bucket = ((long)Text.Font << 32) | (uint)Mathf.RoundToInt(width);
        if (!TruncateCaches.TryGetValue(bucket, out Dictionary<string, string>? cache))
        {
            cache = new Dictionary<string, string>(StringComparer.Ordinal);
            TruncateCaches[bucket] = cache;
        }
        return text.Truncate(width, cache);
    }

    /// <summary>依可用寬度算出模型 chip 的欄數與實際寬度：欄數由偏好寬度決定，寬度由欄數反推，讓 chip 填滿整列。</summary>
    public static void ComputeChipLayout(
        float contentWidth, float gap, float preferredWidth, out int columns, out float chipWidth)
    {
        columns = Mathf.Max(1, Mathf.FloorToInt((contentWidth - gap) / (preferredWidth + gap)));
        chipWidth = Mathf.Max(1f, (contentWidth - gap * (columns + 1)) / columns);
    }

    /// <summary>
    /// 清單項目的選取／滑過外框。左欄一級分頁與中欄供應商清單共用同一套視覺。
    /// </summary>
    public static void DrawSelectableFrame(Rect rect, bool selected)
    {
        if (selected)
        {
            Widgets.DrawBoxSolid(rect, SelectedFill);
            Color old = GUI.color;
            GUI.color = Gold;
            Widgets.DrawBox(rect, 1);
            GUI.color = old;
        }
        else if (Mouse.IsOver(rect))
        {
            Widgets.DrawHighlight(rect);
        }
    }

    /// <summary>導覽清單項目的選取／滑過底色（無外框），左欄一級分頁與中欄供應商清單共用。</summary>
    public static void DrawNavSelection(Rect rect, bool selected)
    {
        if (selected) Widgets.DrawBoxSolid(rect, SelectedFill);
        else if (Mouse.IsOver(rect)) Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.06f));
    }

    /// <summary>
    /// 帶「顯示／遮罩」切換鈕的 API 金鑰欄位，回傳欄位內的最新值。
    ///
    /// 遮罩時刻意不畫 TextField：TextField 會把畫面上的字串當成使用者輸入寫回，
    /// 那會讓遮罩字串直接覆蓋掉真正的金鑰。改畫唯讀外觀的標籤。
    /// 供應商分頁與 Embedding 分頁共用，否則兩處的遮罩規則會各自漂移。
    /// </summary>
    /// <param name="inputRect">金鑰輸入框或遮罩方塊繪製區域。</param>
    /// <param name="revealRect">明文/遮罩切換按鈕繪製區域。</param>
    /// <param name="value">金鑰原始字串。</param>
    /// <param name="revealed">傳入目前是否明文顯示；使用者按下切換鈕時就地翻轉。</param>
    /// <returns>編輯後的金鑰字串。</returns>
    public static string DrawMaskableKeyField(Rect inputRect, Rect revealRect, string value, ref bool revealed)
    {
        string result = value;
        if (revealed || string.IsNullOrEmpty(value))
        {
            // 欄位為空時沒有東西需要遮罩，直接給輸入框以便貼上；
            // 一旦有輸入就轉為明文狀態，避免輸入中途換成唯讀標籤而失去焦點。
            result = Widgets.TextField(inputRect, value ?? "");
            if (!revealed && !string.IsNullOrEmpty(result))
            {
                revealed = true;
            }
        }
        else
        {
            Widgets.DrawBoxSolid(inputRect, ChipFill);
            Widgets.DrawBox(inputRect, 1);
            using (With(TextAnchor.MiddleLeft, wordWrap: false))
            {
                Widgets.Label(inputRect.ContractedBy(4f), MaskApiKey(value));
            }
        }

        if (Widgets.ButtonText(revealRect, revealed ? "abc" : "•••"))
        {
            revealed = !revealed;
        }
        TooltipHandler.TipRegion(revealRect, (revealed ? "RimLLM_HideApiKey" : "RimLLM_RevealApiKey").Translate());
        return result;
    }

    /// <summary>狀態列（第三方整合、使用中的模組分頁）的列高與列距。</summary>
    public const float StatusRowHeight = 34f;
    public const float StatusRowGap = 4f;
    private const float StatusRowPadding = 10f;
    private const float StatusDotSize = 8f;
    private const float StatusBadgeWidth = 120f;

    /// <summary>狀態列色點：生效／已呼叫。</summary>
    public static readonly Color StatusGreen = new Color32(0x4a, 0xde, 0x80, 0xff);

    /// <summary>狀態列色點：可用但未生效／尚未呼叫。</summary>
    public static readonly Color StatusBlue = new Color32(0x60, 0xa5, 0xfa, 0xff);

    /// <summary>「還不能用」等錯誤狀態的紅色。</summary>
    public static readonly Color Danger = new Color32(0xe0, 0x60, 0x60, 0xff);

    /// <summary>
    /// 控制項下方的一行灰色小字說明：「這是做什麼的、什麼時候才要改」。
    /// </summary>
    public static void DrawHint(Listing_Standard listing, string text)
    {
        using (With(font: GameFont.Tiny))
        {
            Color old = GUI.color;
            GUI.color = Muted;
            listing.Label(text);
            GUI.color = old;
        }
        listing.Gap(4f);
    }

    /// <summary>
    /// 緊湊狀態列：底色、色點、標題與右側狀態標籤，兩個分頁共用同一套版面以免各自漂移。
    /// <paramref name="tint"/> 套用在色點、標題與標籤上（例如未安裝時淡化），底色不受影響；結束時 GUI.color 還原為白色。
    /// <paramref name="rightInset"/> 為標籤右側預留給額外控制項（如勾選框）的寬度。
    /// 回傳滑鼠是否停在列上，提示由呼叫端掛，才能只在懸停時組字串。
    /// </summary>
    public static bool DrawStatusRow(Rect rowRect, Color statusColor, string title, string badge, Color tint, float rightInset = 0f)
    {
        Widgets.DrawBoxSolid(rowRect, ChipFill);
        bool hovered = Mouse.IsOver(rowRect);
        if (hovered) Widgets.DrawHighlight(rowRect);

        Rect badgeRect = new(rowRect.xMax - StatusRowPadding - rightInset - StatusBadgeWidth, rowRect.y, StatusBadgeWidth, rowRect.height);
        Rect dotRect = new(
            rowRect.x + StatusRowPadding,
            rowRect.y + (rowRect.height - StatusDotSize) / 2f,
            StatusDotSize, StatusDotSize);
        Rect titleRect = new(dotRect.xMax + 8f, rowRect.y, badgeRect.x - dotRect.xMax - 16f, rowRect.height);

        Color oldColor = GUI.color;
        GUI.color = tint;
        Widgets.DrawBoxSolid(dotRect, statusColor * tint);
        using (With(TextAnchor.MiddleLeft, GameFont.Small, wordWrap: false))
        {
            Widgets.Label(titleRect, title);
        }
        using (With(TextAnchor.MiddleRight, GameFont.Tiny, wordWrap: false))
        {
            GUI.color = statusColor * tint;
            Widgets.Label(badgeRect, badge);
        }
        GUI.color = oldColor;
        return hovered;
    }

    /// <summary>
    /// 比例條：底色 + 依比例填滿 + 外框 + 置中百分比標籤。
    /// </summary>
    public static void DrawRatioBar(Rect rect, float fillPercent, Color track, Color fill, string label)
    {
        Widgets.DrawBoxSolid(rect, track);
        if (fillPercent > 0f)
        {
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width * fillPercent, rect.height), fill);
        }
        Widgets.DrawBox(rect, 1);

        using (With(TextAnchor.MiddleCenter, GameFont.Tiny))
        {
            Widgets.Label(rect, label);
        }
    }

    private static bool _pendingSave;

    /// <summary>
    /// 判斷當前 OnGUI 事件是否適合寫入設定至磁碟（跨拖曳事件累計變更，於滑鼠放開或無事件時觸發）。
    /// </summary>
    public static bool ShouldSaveSettings(bool changed)
    {
        if (changed) _pendingSave = true;
        if (!_pendingSave) return false;

        if (Event.current == null || Event.current.type == EventType.MouseUp || Event.current.rawType == EventType.MouseUp)
        {
            _pendingSave = false;
            return true;
        }
        return false;
    }
}
