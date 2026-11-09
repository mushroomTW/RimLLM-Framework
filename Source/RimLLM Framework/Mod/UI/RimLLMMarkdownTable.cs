using System;
using System.Collections.Generic;
using System.Linq;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>Markdown 的一段：<see cref="Rows"/> 為 null 時是一般 Markdown 文字，否則是表格的原始儲存格（第一列為表頭）。</summary>
public readonly struct RimLLMMarkdownSegment
{
    public readonly string Markdown;
    public readonly IReadOnlyList<string[]> Rows;

    public RimLLMMarkdownSegment(string markdown, IReadOnlyList<string[]> rows)
    {
        Markdown = markdown;
        Rows = rows;
    }

    public bool IsTable => Rows != null;
}

/// <summary>
/// 逐格繪製的 Markdown 表格。<see cref="RimLLMMarkdown.ToRichText"/> 只能把表格攤平成一行一列，
/// 因為遊戲字型是比例字、無法靠空白對齊；呼叫端先用 <see cref="Split"/> 切出頂層表格，
/// 一般文字照常轉 rich text，表格則以 <see cref="Layout"/> 排版、<see cref="Draw"/> 畫出格線。
/// </summary>
public sealed class RimLLMMarkdownTable
{
    private const float CellPadX = 6f;
    private const float CellPadY = 2f;
    private static readonly Color HeaderColor = new(1f, 1f, 1f, 0.08f);
    private static readonly Color StripeColor = new(1f, 1f, 1f, 0.03f);
    private static readonly Color GridColor = new(0.45f, 0.52f, 0.60f, 0.45f);

    private static readonly MarkdownPipeline TablePipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();
    private static readonly GUIContent MeasureContent = new();

    /// <summary>已轉成 rich text 的儲存格（第一列為表頭），欄數補齊。</summary>
    public string[][] Cells { get; private set; }
    public float[] ColumnWidths { get; private set; }
    public float[] RowHeights { get; private set; }
    public float Width { get; private set; }
    public float Height { get; private set; }
    /// <summary>tab 分欄、換行分列的純文字，供複製到剪貼簿。</summary>
    public string PlainText { get; private set; }

    /// <summary>
    /// 只切出最外層的表格：引用或清單裡的表格留在文字段，交給 <see cref="RimLLMMarkdown.ToRichText"/> 攤平。
    /// 以 4 空格或 tab 縮排開頭的「表格」在 GFM 是程式碼，同樣留在文字段。
    /// </summary>
    public static List<RimLLMMarkdownSegment> Split(string markdown)
    {
        var segments = new List<RimLLMMarkdownSegment>();
        string source = markdown ?? string.Empty;
        int cursor = 0;
        foreach (Block block in Markdown.Parse(source, TablePipeline))
        {
            if (block is not Table table || StartsIndented(source, table.Span.Start)) continue;
            int start = LineStart(source, table.Span.Start);
            AddText(segments, source.Substring(cursor, start - cursor));
            segments.Add(new RimLLMMarkdownSegment(source.Substring(start, table.Span.End + 1 - start), Rows(table, source)));
            cursor = Math.Min(source.Length, table.Span.End + 1);
        }
        AddText(segments, source.Substring(cursor));
        return segments;
    }

    /// <summary>
    /// 依目前字型排版：欄寬取該欄最寬的內容，放不下時以 <see cref="FitColumns"/> 壓縮長欄，長的格子換行、整列跟著變高。
    /// <paramref name="renderCell"/> 把原始儲存格轉成 rich text，預設為 <see cref="RimLLMMarkdown.CellToRichText"/>；
    /// 呼叫端可藉此套上自己的文字顏色。
    /// </summary>
    public static RimLLMMarkdownTable Layout(IReadOnlyList<string[]> rows, float width, Func<string, string> renderCell = null)
    {
        renderCell ??= RimLLMMarkdown.CellToRichText;
        int columns = rows.Count == 0 ? 0 : rows.Max(row => row.Length);
        var cells = new string[rows.Count][];
        var natural = new float[columns];
        for (int r = 0; r < rows.Count; r++)
        {
            cells[r] = new string[columns];
            for (int c = 0; c < columns; c++)
            {
                string rich = renderCell(c < rows[r].Length ? rows[r][c] : string.Empty);
                cells[r][c] = rich;
                natural[c] = Mathf.Max(natural[c], Mathf.Ceil(MeasureWidth(rich)) + 1f + CellPadX * 2f);
            }
        }
        float[] widths = FitColumns(natural, width);
        var heights = new float[rows.Count];
        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                float inner = Mathf.Max(1f, widths[c] - CellPadX * 2f);
                heights[r] = Mathf.Max(heights[r], MeasureHeight(cells[r][c], inner) + CellPadY * 2f);
            }
        }
        return new RimLLMMarkdownTable
        {
            Cells = cells,
            ColumnWidths = widths,
            RowHeights = heights,
            Width = widths.Sum(),
            Height = heights.Sum(),
            PlainText = string.Join("\n", cells.Select(row => string.Join("\t", row.Select(cell => cell.StripTags()))))
        };
    }

    /// <summary>
    /// 放不下時只壓縮長欄：比平分寬度窄的欄照原寬，剩下的空間再平分給其餘欄，反覆到穩定；
    /// 等比例壓縮會連短欄一起壓，把「colony-progression」這種名稱拆成好幾行。
    /// </summary>
    public static float[] FitColumns(float[] natural, float width)
    {
        var widths = new float[natural.Length];
        if (natural.Sum() <= width)
        {
            Array.Copy(natural, widths, natural.Length);
            return widths;
        }
        var open = new List<int>(Enumerable.Range(0, natural.Length));
        float remaining = width;
        bool settled = false;
        while (!settled && open.Count > 0)
        {
            settled = true;
            float share = remaining / open.Count;
            foreach (int c in open.Where(c => natural[c] <= share).ToList())
            {
                widths[c] = natural[c];
                remaining -= natural[c];
                open.Remove(c);
                settled = false;
            }
        }
        foreach (int c in open) widths[c] = Mathf.Floor(remaining / open.Count);
        return widths;
    }

    /// <summary>以 <paramref name="rect"/> 左上角為起點畫出表格；字型須與 <see cref="Layout"/> 時相同。</summary>
    public void Draw(Rect rect)
    {
        float y = rect.y;
        for (int r = 0; r < Cells.Length; r++)
        {
            var rowRect = new Rect(rect.x, y, Width, RowHeights[r]);
            if (r == 0) Widgets.DrawBoxSolid(rowRect, HeaderColor);
            else if (r % 2 == 0) Widgets.DrawBoxSolid(rowRect, StripeColor);
            float x = rect.x;
            for (int c = 0; c < ColumnWidths.Length; c++)
            {
                Widgets.Label(new Rect(x + CellPadX, y + CellPadY, ColumnWidths[c] - CellPadX * 2f, RowHeights[r] - CellPadY * 2f), Cells[r][c]);
                x += ColumnWidths[c];
            }
            y += RowHeights[r];
        }
        var tableRect = new Rect(rect.x, rect.y, Width, Height);
        Color oldColor = GUI.color;
        GUI.color = GridColor;
        Widgets.DrawBox(tableRect, 1);
        GUI.color = oldColor;
        float lineX = rect.x;
        for (int c = 0; c < ColumnWidths.Length - 1; c++)
        {
            lineX += ColumnWidths[c];
            Widgets.DrawBoxSolid(new Rect(lineX, rect.y, 1f, Height), GridColor);
        }
        float lineY = rect.y;
        for (int r = 0; r < RowHeights.Length - 1; r++)
        {
            lineY += RowHeights[r];
            Widgets.DrawBoxSolid(new Rect(rect.x, lineY, Width, 1f), GridColor);
        }
    }

    // Text.CalcSize／CalcHeight 會先去掉標籤，量不到變寬的粗體與放大的字；這裡連標籤一起量。
    private static float MeasureWidth(string richText)
    {
        MeasureContent.text = richText;
        return Text.CurFontStyle.CalcSize(MeasureContent).x;
    }

    private static float MeasureHeight(string richText, float width)
    {
        MeasureContent.text = richText;
        return Text.CurFontStyle.CalcHeight(MeasureContent, width);
    }

    private static void AddText(List<RimLLMMarkdownSegment> segments, string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) segments.Add(new RimLLMMarkdownSegment(text.Trim('\n', '\r'), null));
    }

    private static List<string[]> Rows(Table table, string source)
    {
        var rows = new List<string[]>();
        foreach (Block rowBlock in table)
        {
            if (rowBlock is not TableRow row) continue;
            var cells = new List<string>();
            foreach (Block cellBlock in row)
            {
                // 取原始切片而非 inline 樹；跳脫的直線要自己還原。
                cells.Add(source.Substring(cellBlock.Span.Start, cellBlock.Span.Length).Trim().Replace("\\|", "|"));
            }
            if (cells.Count > 0) rows.Add([.. cells]);
        }
        return rows;
    }

    private static int LineStart(string source, int index)
    {
        int newline = index > 0 ? source.LastIndexOf('\n', index - 1) : -1;
        return newline + 1;
    }

    private static bool StartsIndented(string source, int index)
    {
        if (index < source.Length && source[index] == '\t') return true;
        int spaces = 0;
        for (int k = LineStart(source, index); k < index && source[k] == ' '; k++) spaces++;
        return spaces >= 4;
    }
}
