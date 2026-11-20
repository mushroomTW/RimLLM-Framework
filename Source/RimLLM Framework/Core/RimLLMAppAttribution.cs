using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Verse;

namespace RimLLM_Framework.Core;

/// <summary>
/// 送給 OpenRouter 的應用歸屬身分：<c>HTTP-Referer</c> 網址與 App 顯示名稱。
/// </summary>
/// <remarks>
/// OpenRouter 以網址作為 App 的唯一識別，名稱只是該 App 的顯示名稱。
/// 因此沒有專屬網址的 Mod 一律歸屬到框架本身，而不是把它的名稱掛在別人的網址底下：
/// 那樣多個 Mod 會共用同一個 App，顯示名稱隨最後一個請求來回跳動。
/// </remarks>
internal sealed record RimLLMAppAttribution(string Title, string Url)
{
    /// <summary>框架本身的身分；呼叫端無法歸屬或未設定時使用。</summary>
    public static readonly RimLLMAppAttribution Framework =
        new("RimLLM Framework", "https://github.com/mushroomTW/RimLLM-Framework");

    private static readonly AsyncLocal<RimLLMAppAttribution?> CurrentValue = new();

    /// <summary>
    /// 目前請求的歸屬，沿非同步呼叫鏈傳到 SDK pipeline policy。未設定時為 <see cref="Framework"/>。
    /// </summary>
    /// <remarks>
    /// 以 AsyncLocal 傳遞而非經由參數：<see cref="Providers.ILLMProvider.CreateChatClient"/> 是公開介面，
    /// 只帶 model，加參數會破壞外部實作。請只在 async 方法內設定，值才會在該次呼叫結束後還原。
    /// </remarks>
    public static RimLLMAppAttribution? Current
    {
        get => CurrentValue.Value ?? Framework;
        set => CurrentValue.Value = value;
    }

    /// <summary>
    /// 以目前啟用的 Mod 建立 packageId → 歸屬的唯讀對照表，並回傳查詢函式。
    /// </summary>
    /// <remarks>
    /// 啟動時在主執行緒建一次快照：啟用清單在遊戲執行期間不會變動，
    /// 而 <see cref="ModLister"/> 的字典沒有同步保護，不能在背景執行緒建立 client 時才查。
    /// 共用同一網址的 Mod（或網址與框架相同）會互相覆寫 OpenRouter 上的顯示名稱，因此一律歸屬框架。
    /// </remarks>
    /// <param name="mods">啟用中的 Mod：packageId（不含 _steam 後綴）、名稱與 About.xml 的 url。</param>
    public static Func<string?, RimLLMAppAttribution> CreateResolver(
        IEnumerable<(string PackageId, string Name, string Url)> mods)
    {
        var candidates = mods
            .Where(m => !string.IsNullOrWhiteSpace(m.PackageId))
            .Select(m => (PackageId: m.PackageId.Trim(), Attribution: FromMetadata(m.Name, m.PackageId, m.Url)))
            .ToList();

        var sharedUrls = candidates
            .GroupBy(c => UrlKey(c.Attribution.Url), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Skip(1).Any())
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        sharedUrls.Add(UrlKey(Framework.Url));

        var byPackageId = new Dictionary<string, RimLLMAppAttribution>(StringComparer.OrdinalIgnoreCase);
        foreach ((string packageId, RimLLMAppAttribution attribution) in candidates)
        {
            byPackageId[packageId] = sharedUrls.Contains(UrlKey(attribution.Url)) ? Framework : attribution;
        }

        return modId => modId != null && byPackageId.TryGetValue(modId.Trim(), out RimLLMAppAttribution? found)
            ? found
            : Framework;
    }

    /// <summary>
    /// 由 Mod 中繼資料組出歸屬。沒有 http(s) 網址時回傳 <see cref="Framework"/>；
    /// 名稱會先去掉 RimWorld 富文字標籤，仍含非 ASCII 字元時改用 packageId。
    /// </summary>
    /// <remarks>
    /// .NET 的 SocketsHttpHandler 預設拒絕非 ASCII 標頭值並擲出例外，
    /// 中文 Mod 名稱若原樣送出，整個請求都會失敗。
    /// </remarks>
    public static RimLLMAppAttribution FromMetadata(string? name, string? packageId, string? url)
    {
        url = url?.Trim();
        if (!IsHeaderSafe(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Framework;
        }

        string? strippedName = name?.StripTags().Trim();
        string? title = IsHeaderSafe(strippedName) ? strippedName : packageId?.Trim();
        return IsHeaderSafe(title) ? new RimLLMAppAttribution(title, url) : Framework;
    }

    /// <summary>非空白且只含可列印 ASCII 字元。</summary>
    private static bool IsHeaderSafe([NotNullWhen(true)] string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.All(c => c >= 0x20 && c <= 0x7E);
    }

    /// <summary>比對網址是否相同時忽略結尾斜線。</summary>
    private static string UrlKey(string url) => url.TrimEnd('/');
}
