using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 對話紀錄的字串格式：中繼資料標記、推理與答案的分界、思考區塊標灰、前綴解析與剪貼簿淨化。純文字處理，不涉及繪製。
/// </summary>
public static partial class ChatTestDrawer
{
    /// <summary>中繼資料標記的前後綴，用於在持久化歷史中保存應答模型、耗時與 Token 數。</summary>
    internal const string MetaPrefix = "\n<!--rimllm-meta:";
    internal const string MetaSuffix = "-->";

    /// <summary>
    /// AI 應答的微型中繼資料。
    /// </summary>
    internal sealed class ChatEntryMeta
    {
        public string ModelId { get; set; } = string.Empty;
        public long ElapsedMs { get; set; }
        public int TotalTokens { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public bool IsEstimatedTokens { get; set; }

        public string Serialize()
        {
            return string.Create(CultureInfo.InvariantCulture, $"model={ModelId ?? ""}|ms={ElapsedMs}|tok={TotalTokens}|p={PromptTokens}|c={CompletionTokens}|est={(IsEstimatedTokens ? 1 : 0)}");
        }

        private static readonly char[] PipeSeparator = ['|'];

        public static ChatEntryMeta? Deserialize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var meta = new ChatEntryMeta();
            var parts = raw.Split(PipeSeparator);
            foreach (var part in parts)
            {
                int eq = part.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).Trim();
                string val = part.Substring(eq + 1).Trim();
                ParseMetaField(meta, key, val);
            }
            return meta;
        }

        private static void ParseMetaField(ChatEntryMeta meta, string key, string val)
        {
            switch (key)
            {
                case "model":
                    meta.ModelId = val;
                    break;
                case "ms":
                    if (long.TryParse(val, CultureInfo.InvariantCulture, out long ms)) { meta.ElapsedMs = ms; }
                    break;
                case "tok":
                    if (int.TryParse(val, CultureInfo.InvariantCulture, out int tok)) { meta.TotalTokens = tok; }
                    break;
                case "p":
                    if (int.TryParse(val, CultureInfo.InvariantCulture, out int p)) { meta.PromptTokens = p; }
                    break;
                case "c":
                    if (int.TryParse(val, CultureInfo.InvariantCulture, out int c)) { meta.CompletionTokens = c; }
                    break;
                case "est":
                    meta.IsEstimatedTokens = val == "1";
                    break;
                default:
                    break;
            }
        }
    }

    private static string ThinkStartLabel => "RimLLM_ThinkProcessLabel".Translate();
    private static string ThinkEndLabel => "RimLLM_ThinkProcessEndLabel".Translate();
    private static string ThinkingLabel => "RimLLM_ThinkingLabel".Translate();

    /// <summary>
    /// 新紀錄的 AI 內文存原始 Markdown 並以此標記開頭，繪製時才轉換，表格才能逐格畫。
    /// 沒有標記的舊紀錄已是轉好的 rich text，照原樣顯示。
    /// </summary>
    internal const string MarkdownBodyMarker = "<!--rimllm-md-->";

    /// <summary>
    /// 推理欄位的標記，後接推理內容的字元數：<c>&lt;!--rimllm-reasoning:123--&gt;</c>。
    /// 對話紀錄是整條字串持久化，推理與答案需要分界；以長度而非結束標記分界，
    /// 推理內容不論含什麼字元都不必跳脫，也不會和模型自己寫在正文裡的 &lt;think&gt; 混淆。
    /// </summary>
    private const string ReasoningMarkerPrefix = "<!--rimllm-reasoning:";
    private const string ReasoningMarkerSuffix = "-->";

    /// <summary>組出 Markdown 本文：推理內容（若有）以長度標記存放在答案之前。</summary>
    internal static string BuildMarkdownBody(string reasoning, string answer)
    {
        if (string.IsNullOrEmpty(reasoning)) return MarkdownBodyMarker + answer;
        return MarkdownBodyMarker + ReasoningMarkerPrefix + reasoning.Length.ToString(CultureInfo.InvariantCulture) + ReasoningMarkerSuffix + reasoning + answer;
    }

    /// <summary><see cref="BuildMarkdownBody"/> 的反向：拆出推理內容與答案。標記不完整時整段視為答案。</summary>
    internal static void SplitMarkdownBody(string body, out string? reasoning, out string? answer)
    {
        reasoning = string.Empty;
        answer = body ?? string.Empty;
        if (!answer.StartsWith(ReasoningMarkerPrefix, StringComparison.Ordinal)) return;

        int lengthStart = ReasoningMarkerPrefix.Length;
        int suffixStart = answer.IndexOf(ReasoningMarkerSuffix, lengthStart, StringComparison.Ordinal);
        if (suffixStart < 0) return;
        if (!int.TryParse(answer.AsSpan(lengthStart, suffixStart - lengthStart), System.Globalization.NumberStyles.None,
                CultureInfo.InvariantCulture, out int length))
        {
            return;
        }

        int contentStart = suffixStart + ReasoningMarkerSuffix.Length;
        if (length > answer.Length - contentStart) return;
        reasoning = answer.Substring(contentStart, length);
        answer = answer.Substring(contentStart + length);
    }

    /// <summary>
    /// 供應商原生推理直接以欄位呈現：還沒有答案時顯示「思考中」，否則顯示已閉合的思考過程。
    /// 答案部分仍要經過 <see cref="FormatThinkProcess"/>，因為部分模型（如本機 Ollama/Qwen）
    /// 把 &lt;think&gt; 直接寫在正文裡，那是一般文字，MEAI 不會拆成推理內容。
    /// </summary>
    private static string FormatReasoning(string? reasoning, bool hasAnswer)
    {
        if (string.IsNullOrWhiteSpace(reasoning)) return string.Empty;
        string escaped = RimLLMMarkdown.EscapeUntrustedUnityTags(reasoning);
        return hasAnswer
            ? WrapClosedThought(escaped)
            : WrapTrailingThought(escaped, 0, 0);
    }

    private static string? FormatThinkProcess(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 先跳脫供應商內容中的 Unity 標籤，再加入框架自行產生的灰色 thinking wrapper；
        // 否則供應商可以用同名的 </color> 提早關閉框架標籤。
        text = RimLLMMarkdown.EscapeUntrustedUnityTags(text);

        // 註：此處原本在 DetailedLogging 下記錄完整的模型輸出。
        // 該日誌未經 SanitizeForLog、未截斷，且因為由 OnChunkReceived 觸發，
        // 會對每個 chunk 重複輸出整段累積內容，使 PII、prompt 或意外出現的金鑰
        // 長期留存於 Player.log。已移除（稽核 SEC-002）。

        // 1. 處理已閉合的 <think>...</think> 或 <thought>...</thought> -> 標記為灰色
        string result = ClosedThoughtTag.Replace(text, m => WrapClosedThought(m.Groups["body"].Value));

        // 2. 處理未閉合的 <think> 或 <thought>（串流中常遇到） -> 將後續全部標記為灰色
        var matchUnclosedXml = UnclosedThoughtTag.Match(result);
        if (matchUnclosedXml.Success)
        {
            result = WrapTrailingThought(result, matchUnclosedXml.Index, matchUnclosedXml.Length);
        }

        // 3. 處理已閉合的 markdown 思考區塊 ```thought...``` -> 標記為灰色
        result = ThoughtFence.Replace(result, m => WrapClosedThought(m.Groups["body"].Value));

        // 4. 處理未閉合的 markdown 思考區塊 ```thought （串流中常遇到）-> 將後續全部標記為灰色
        int markdownMarker = result.IndexOf(MarkdownThoughtMarker, StringComparison.Ordinal);
        if (markdownMarker >= 0)
        {
            result = WrapTrailingThought(result, markdownMarker, MarkdownThoughtMarker.Length);
        }

        return result.Trim();
    }

    private const string MarkdownThoughtMarker = "```thought";

    /// <summary>已閉合的思考區塊：內容為空則整段捨棄，否則以灰色包起來。</summary>
    private static string WrapClosedThought(string content)
    {
        string thinkContent = content.Trim();
        return string.IsNullOrEmpty(thinkContent)
            ? ""
            : $"\n<color=silver>{ThinkStartLabel}\n{thinkContent}\n{ThinkEndLabel}</color>\n\n";
    }

    /// <summary>未閉合的思考區塊：把標記之後的所有內容都視為思考中並標灰。</summary>
    private static string WrapTrailingThought(string text, int markerIndex, int markerLength)
    {
        string before = text.Substring(0, markerIndex);
        string after = text.Substring(markerIndex + markerLength);
        return before + $"\n<color=silver>{ThinkingLabel}\n{after} ...</color>";
    }

    /// <summary>
    /// 對話紀錄項目的前綴（例如 "&lt;b&gt;[AI]:&lt;/b&gt; "）。
    /// 一定要以 ToString() 取 RawText 再串接：TaggedString 隱含轉成 string 時會呼叫 StripTags()，
    /// 把整條訊息裡所有 &lt;...&gt; 標籤（包含 Markdown 轉出的 color/size/b）一併剝掉，
    /// 畫面上就只剩沒有任何格式的純文字。
    /// </summary>
    private static string EntryPrefix(string key)
    {
        try
        {
            if (LanguageDatabase.activeLanguage != null)
            {
                return key.Translate().ToString() + " ";
            }
        }
        catch
        {
            // 單元測試或環境未載入語系時安全退回
        }

        return key switch
        {
            ChatUserPrefixKey => "<b>[Me]:</b> ",
            ChatAiTag => "<b>[AI]:</b> ",
            "RimLLM_ChatAiError" => "<b>[AI Error]:</b> ",
            _ => key + " ",
        };
    }

    /// <summary>
    /// 嘗試剝掉 <paramref name="key"/> 對應的前綴。
    /// 修正前持久化的舊紀錄前綴已被 StripTags 剝成純文字（"[AI]: "），兩種形式都要認得。
    /// </summary>
    private static bool TryStripPrefix(string entry, string key, out string? label, out string? body)
    {
        string raw = EntryPrefix(key);
        string plain = raw.StripTags();
        label = plain.Trim();
        if (entry.StartsWith(raw, StringComparison.Ordinal))
        {
            body = entry.Substring(raw.Length);
            return true;
        }
        if (entry.StartsWith(plain, StringComparison.Ordinal))
        {
            body = entry.Substring(plain.Length);
            return true;
        }
        body = string.Empty;
        return false;
    }

    internal static bool ParseMessage(string entry, out string? label, out string? body, out ChatEntryMeta? meta)
    {
        meta = null;
        if (string.IsNullOrEmpty(entry))
        {
            label = string.Empty;
            body = string.Empty;
            return false;
        }

        // 抽離並淨化中繼資料標記，避免污染本文顯示與剪貼簿
        int metaStart = entry.LastIndexOf(MetaPrefix, StringComparison.Ordinal);
        if (metaStart >= 0)
        {
            int metaEnd = entry.IndexOf(MetaSuffix, metaStart + MetaPrefix.Length, StringComparison.Ordinal);
            if (metaEnd >= 0)
            {
                string rawMeta = entry.Substring(metaStart + MetaPrefix.Length, metaEnd - (metaStart + MetaPrefix.Length));
                meta = ChatEntryMeta.Deserialize(rawMeta);
                entry = entry.Substring(0, metaStart);
            }
        }

        if (TryStripPrefix(entry, ChatUserPrefixKey, out label, out body)) return true;
        if (TryStripPrefix(entry, "RimLLM_ChatAiError", out label, out body)) return false;
        if (TryStripPrefix(entry, ChatAiTag, out label, out body)) return false;

        // 舊格式或手動拼接的格式相容
        if (entry.StartsWith("<b>[我]:</b>", StringComparison.Ordinal) || entry.StartsWith("<b>[Me]:</b>", StringComparison.Ordinal))
        {
            int endTag = entry.IndexOf("</b>", StringComparison.Ordinal);
            label = entry.Substring(0, endTag + 4).Replace("<b>", "", StringComparison.Ordinal).Replace("</b>", "", StringComparison.Ordinal).Trim();
            body = entry.Substring(endTag + 4).TrimStart(SpaceTrimChars);
            return true;
        }

        if (entry.StartsWith("<b>[AI]:</b>", StringComparison.Ordinal))
        {
            int endTag = entry.IndexOf("</b>", StringComparison.Ordinal);
            label = entry.Substring(0, endTag + 4).Replace("<b>", "", StringComparison.Ordinal).Replace("</b>", "", StringComparison.Ordinal).Trim();
            body = entry.Substring(endTag + 4).TrimStart(SpaceTrimChars);
            return false;
        }

        label = EntryPrefix(ChatAiTag).StripTags().Trim();
        body = entry;
        return false;
    }

    internal static bool ParseMessage(string entry, out string? label, out string? body)
    {
        return ParseMessage(entry, out label, out body, out _);
    }

    /// <summary>
    /// 格式化模型名稱供微型標籤顯示。若為 "供應商:模型" 複合識別碼，轉為易讀的 "供應商 • 模型" 格式。
    /// </summary>
    internal static string FormatModelDisplayName(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return string.Empty;
        return ProviderIds.TrySplitModelId(modelId, out string? provider, out string? model)
            ? $"{provider} • {model}"
            : modelId;
    }

    /// <summary>
    /// 複製到剪貼簿前先移除真正的 rich text 標籤與可能存在的中繼標記，再把 <see cref="RimLLMMarkdown.EscapeCode"/> 為了不被 Unity 解讀
    /// 而改成全形的程式碼標籤（＜b＞、＜/color＞…）還原成 ASCII。只還原那六種標籤，
    /// 中文回覆裡本來就是全形的「＜注意＞」不能被改掉。順序不能顛倒，否則還原後的程式碼標籤會被當成格式一起剝掉。
    /// </summary>
    private static string StripRichTextForClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var timeout = TimeSpan.FromSeconds(1);
        string cleaned = Regex.Replace(text, @"<!--rimllm-meta:[\s\S]*?-->", "", RegexOptions.IgnoreCase, timeout);
        cleaned = Regex.Replace(cleaned, @"</?(?:b|i|size|color|material|quad)(?:=[^>]*)?>", "", RegexOptions.IgnoreCase, timeout);
        return Regex.Replace(cleaned, @"＜(?<tag>/?(?:b|i|size|color|material|quad)(?:=[^＞]*)?)＞", "<${tag}>", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, timeout);
    }

    [GeneratedRegex(@"```thought(?<body>[\s\S]*?)```", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ThoughtFence { get; }
    [GeneratedRegex(@"<(?<tag>think|thought)>(?<body>[\s\S]*?)</\k<tag>>", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ClosedThoughtTag { get; }
    [GeneratedRegex(@"<(?<tag>think|thought)>(?![\s\S]*</\k<tag>>)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnclosedThoughtTag { get; }
}
