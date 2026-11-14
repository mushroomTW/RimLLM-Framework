namespace RimLLM_Framework;

/// <summary>
/// 圖片輸入支援查詢。與 <see cref="IContextWindowLookup"/> 同理，刻意不併入公開的 <see cref="IRimLLMSettings"/>，
/// 避免依舊版編譯的外部 Mod 在載入時擲出 TypeLoadException。
/// </summary>
internal interface IVisionLookup
{
    /// <summary>
    /// 查詢模型是否支援圖片輸入：重新整理模型清單時記下的結果；沒有資料則回傳 null。
    /// </summary>
    bool? GetVisionSupport(string providerId, string modelName);
}
