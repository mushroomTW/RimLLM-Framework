using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace RimLLM_Framework.Tests;

/// <summary>
/// lint 測試：<c>RimLLMLog</c> 的訊息一律英文（AGENTS.md「代碼風格」），
/// 讓玩家回報的 Player.log 不受語系影響，也方便搜尋。
/// </summary>
[TestFixture]
public class RimLLMLogLanguageTests
{
    private static readonly Regex LogCall = new(@"\bRimLLMLog\.(Message|Warning|Error)\(", RegexOptions.CultureInvariant);
    private static readonly Regex Cjk = new(@"[぀-ヿ㐀-䶿一-鿿가-힯＀-￯]", RegexOptions.CultureInvariant);

    [Test]
    public void LogMessagesAreEnglish()
    {
        string root = FindSourceRoot();
        var violations = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar) || relative.StartsWith("bin" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            foreach (Match call in LogCall.Matches(source))
            {
                string arguments = ReadArgumentsIgnoringComments(source, call.Index + call.Length);
                if (Cjk.IsMatch(arguments))
                {
                    int line = 1 + source.Substring(0, call.Index).Split('\n').Length - 1;
                    violations.Add(relative + ":" + line);
                }
            }
        }

        Assert.That(violations, Is.Empty,
            "RimLLMLog 訊息要用英文；給玩家看的文字改用 Languages/ 的翻譯鍵，日誌只記英文的技術訊息。");
    }

    [Test]
    public void ArgumentReaderIgnoresCommentsAndBalancesParentheses()
    {
        const string Source = "RimLLMLog.Warning(\"a (b)\" + F(x) // 中文註解\n); tail";

        string arguments = ReadArgumentsIgnoringComments(Source, "RimLLMLog.Warning(".Length);

        Assert.That(arguments, Does.Contain("\"a (b)\" + F(x)"));
        Assert.That(Cjk.IsMatch(arguments), Is.False);
        Assert.That(arguments, Does.Not.Contain("tail"));
    }

    /// <summary>從左括號後讀到對應的右括號，略過註解；字串內的括號不計入深度。</summary>
    private static string ReadArgumentsIgnoringComments(string source, int start)
    {
        var text = new System.Text.StringBuilder();
        int depth = 1;
        bool inString = false;
        int i = start;
        while (i < source.Length)
        {
            char c = source[i];
            if (!inString && c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                int lineEnd = source.IndexOf('\n', i);
                i = lineEnd < 0 ? source.Length : lineEnd;
                continue;
            }
            if (c == '"' && (i == 0 || source[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString && c == '(')
            {
                depth++;
            }
            else if (!inString && c == ')' && --depth == 0)
            {
                break;
            }
            text.Append(c);
            i++;
        }
        return text.ToString();
    }

    /// <summary>從本測試檔的編譯期路徑往上找，測試輸出到其他目錄（例如 <c>dotnet test -o</c>）時也找得到。</summary>
    private static string FindSourceRoot([CallerFilePath] string testFile = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(testFile)); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Source", "RimLLM Framework");
            if (File.Exists(Path.Combine(candidate, "RimLLM Framework.csproj")))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("找不到 Source/RimLLM Framework");
    }
}
