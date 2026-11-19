using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DynamicNotch;

/// <summary>
/// Markdown tables in answers, drawn as real tables: a header in the theme's colour, rows that alternate softly,
/// numbers right-aligned and tinted so values read at a glance, columns sized to what's in them, all selectable like
/// the rest of the answer. A table the model squeezed onto one line is put back into rows first.
/// </summary>
public partial class NotchWindow
{
    static bool IsTableRow(string line)
    {
        string t = line.Trim();
        return t.Length > 2 && t.StartsWith('|') && t.EndsWith('|') && t.Count(c => c == '|') >= 3;
    }

    static bool IsTableRule(string line) => Regex.IsMatch(line.Trim(), @"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$");

    /// <summary>"| a | b | |---|---| | 1 | 2 |" on one line: back into rows.</summary>
    static string Untangle(string md) => Regex.Replace(md, @"^(?<l>.*\|\s*:?-{2,}.*)$", m =>
    {
        string line = m.Groups["l"].Value;
        if (Regex.Matches(line, @"\|\s+\|").Count < 2) return line;
        return Regex.Replace(line, @"\|\s+\|", "|\n|");
    }, RegexOptions.Multiline);

    static string[] Cells(string row)
    {
        string t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToArray();
    }

    static readonly Regex Numberish = new(@"^[-+~≈]?\s*[\d][\d\s.,']*\s*(%|[A-Za-z°µ/€$£]{1,6}(/[A-Za-z]{1,4})?)?$|^[\$€£]\s?[\d][\d\s.,]*$", RegexOptions.Compiled);

    /// <summary>The table, from its markdown rows (the |---| line marks the header and each column's alignment).</summary>
    Block MarkdownTable(List<string> rows)
    {
        var acc = ChatAccent;
        int ruleAt = rows.FindIndex(IsTableRule);
        var data = rows.Where((r, i) => i != ruleAt).Select(Cells).ToList();
        bool header = ruleAt == 1;
        int cols = data.Max(r => r.Length);
        var align = new TextAlignment?[cols];
        if (ruleAt >= 0)
        {
            var rule = Cells(rows[ruleAt]);
            for (int c = 0; c < Math.Min(cols, rule.Length); c++)
                align[c] = rule[c].StartsWith(':') && rule[c].EndsWith(':') ? TextAlignment.Center : rule[c].EndsWith(':') ? TextAlignment.Right : rule[c].StartsWith(':') ? TextAlignment.Left : null;
        }
        // a column of numbers reads best right-aligned
        for (int c = 0; c < cols; c++)
        {
            var values = data.Skip(header ? 1 : 0).Select(r => c < r.Length ? Regex.Replace(r[c], @"\*|`", "") : "").Where(v => v.Length > 0).ToList();
            if (align[c] == null && values.Count > 0 && values.All(v => Numberish.IsMatch(v))) align[c] = TextAlignment.Right;
        }

        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8), BorderBrush = Frozen(A(acc, _cyber ? (byte)0x60 : (byte)0x38)), BorderThickness = new Thickness(1) };
        for (int c = 0; c < cols; c++)
        {
            int width = data.Max(r => c < r.Length ? Math.Min(40, r[c].Length) : 0);
            table.Columns.Add(new TableColumn { Width = new GridLength(Math.Max(4, width + 2), GridUnitType.Star) });
        }
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var line = Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        for (int r = 0; r < data.Count; r++)
        {
            bool head = header && r == 0;
            var row = new TableRow
            {
                Background = head ? Frozen(new LinearGradientBrush(A(acc, _cyber ? (byte)0x38 : (byte)0x40), A(AiSecond, _cyber ? (byte)0x20 : (byte)0x24), 0))
                    : (r - (header ? 1 : 0)) % 2 == 1 ? Frozen(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
            };
            for (int c = 0; c < cols; c++)
            {
                string text = c < data[r].Length ? data[r][c] : "";
                var p = new Paragraph { Margin = new Thickness(0), FontSize = head ? 11 : 12, LineHeight = head ? 15 : 17, TextAlignment = align[c] ?? TextAlignment.Left };
                if (head)
                {
                    p.FontWeight = FontWeights.SemiBold;
                    p.Foreground = Frozen(_cyber ? CyYellow : Color.FromRgb(0xFF, 0xFF, 0xFF));
                    if (_cyber) { p.FontFamily = CyFont; text = text.ToUpperInvariant(); }
                }
                else if (align[c] == TextAlignment.Right) p.Foreground = Frozen(CodeTone);
                Inline(p.Inlines, text);
                row.Cells.Add(new TableCell(p) { Padding = new Thickness(8, head ? 4 : 3, 8, head ? 4 : 3), BorderBrush = line, BorderThickness = new Thickness(0, 0, c < cols - 1 ? 1 : 0, r < data.Count - 1 ? 1 : 0) });
            }
            group.Rows.Add(row);
        }
        MakeSortable(table, group, header, i => i % 2 == 1 ? Frozen(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent); // NotchWindow.LauncherChatRich
        return table;
    }
}
