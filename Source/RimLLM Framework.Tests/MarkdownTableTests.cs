using NUnit.Framework;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

/// <summary>逐格繪製表格的切段與欄寬分配（繪製本身要在遊戲內驗證）。</summary>
[TestFixture]
public class MarkdownTableTests
{
    [Test]
    public void TopLevelTableIsSplitIntoRawCells()
    {
        var segments = RimLLMMarkdownTable.Split("前言\n\n| 殖民者 | 工作 |\n|---|---|\n| **Cole** | 建造、射擊 12** |\n| Toon | a \\| b |\n\n結語");

        Assert.That(segments, Has.Count.EqualTo(3));
        Assert.That(segments[0].IsTable, Is.False);
        Assert.That(segments[0].Markdown, Is.EqualTo("前言"));
        Assert.That(segments[1].Rows, Has.Count.EqualTo(3));
        Assert.That(segments[1].Rows[0], Is.EqualTo(new[] { "殖民者", "工作" }));
        Assert.That(segments[1].Rows[1], Is.EqualTo(new[] { "**Cole**", "建造、射擊 12**" }));
        Assert.That(segments[1].Rows[2], Is.EqualTo(new[] { "Toon", "a | b" }));
        Assert.That(segments[2].Markdown, Is.EqualTo("結語"));
    }

    [Test]
    public void SplitTableMarkdownParsesBackToSameRows()
    {
        var table = RimLLMMarkdownTable.Split("a\n| x | y |\n|---|---|\n| 1 | 2 |")[1];
        Assert.That(RimLLMMarkdownTable.Split(table.Markdown)[0].Rows, Is.EqualTo(table.Rows));
    }

    [Test]
    public void TablesInsideCodeQuotesOrIndentStayText()
    {
        foreach (string markdown in new[]
        {
            "```\n| x | y |\n|---|---|\n| 1 | 2 |\n```",
            "> | x | y |\n> |---|---|\n> | 1 | 2 |",
            "    | x | y |\n    |---|---|\n    | 1 | 2 |",
            "純文字，沒有表格"
        })
        {
            var segments = RimLLMMarkdownTable.Split(markdown);
            Assert.That(segments, Has.Count.EqualTo(1), markdown);
            Assert.That(segments[0].IsTable, Is.False, markdown);
        }
    }

    [Test]
    public void FitColumnsKeepsNarrowColumnsAndShrinksWideOnes()
    {
        Assert.That(RimLLMMarkdownTable.FitColumns([50f, 100f], 300f), Is.EqualTo(new[] { 50f, 100f }));
        Assert.That(RimLLMMarkdownTable.FitColumns([120f, 2000f], 600f), Is.EqualTo(new[] { 120f, 480f }));
        Assert.That(RimLLMMarkdownTable.FitColumns([60f, 900f, 900f], 600f), Is.EqualTo(new[] { 60f, 270f, 270f }));
    }
}
