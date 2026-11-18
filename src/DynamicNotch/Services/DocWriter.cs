using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Real documents from what the AI writes (markdown-ish text): Word (.docx) with headings, bold / italic, lists,
/// tables and code; Excel (.xlsx) from a markdown table or CSV rows, the first row bold; PDF with headings and
/// wrapped text on as many pages as it needs. Built by hand (the formats are plain zipped XML / PDF objects): no
/// Office, no extra libraries. Anything else is written as text (UTF-8).
/// </summary>
public static class DocWriter
{
    public static void Write(string path, string content)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx": File.WriteAllBytes(path, Docx(content)); break;
            case ".xlsx": File.WriteAllBytes(path, Xlsx(content)); break;
            case ".pdf": File.WriteAllBytes(path, Pdf(content)); break;
            default: File.WriteAllText(path, content, new UTF8Encoding(false)); break;
        }
    }

    public static bool IsDocument(string path) => Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pdf";

    // ───────────────────────────── markdown, read once ─────────────────────────────

    enum Kind { Heading, Paragraph, Bullet, Number, Code, Table, Rule }
    sealed record Block(Kind Kind, string Text, int Level = 0, List<string[]>? Rows = null);

    static List<Block> Parse(string md)
    {
        var blocks = new List<Block>();
        var lines = md.Replace("\r", "").Split('\n');
        var para = new StringBuilder();
        void Flush() { if (para.Length > 0) { blocks.Add(new Block(Kind.Paragraph, para.ToString().Trim())); para.Clear(); } }
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.TrimStart().StartsWith("```"))
            {
                Flush();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Append(lines[i]).Append('\n');
                blocks.Add(new Block(Kind.Code, code.ToString().TrimEnd('\n')));
                continue;
            }
            if (line.Trim().Length == 0) { Flush(); continue; }
            if (IsTableRow(line))
            {
                Flush();
                var rows = new List<string[]>();
                for (; i < lines.Length && IsTableRow(lines[i]); i++)
                {
                    if (Regex.IsMatch(lines[i], @"^\s*\|?\s*:?-{2,}")) continue; // the |---|---| line
                    rows.Add(Cells(lines[i]));
                }
                i--;
                blocks.Add(new Block(Kind.Table, "", Rows: rows));
                continue;
            }
            var h = Regex.Match(line, @"^\s*(#{1,6})\s+(.*)$");
            if (h.Success) { Flush(); blocks.Add(new Block(Kind.Heading, h.Groups[2].Value.Trim(), h.Groups[1].Value.Length)); continue; }
            if (Regex.IsMatch(line, @"^\s*(?:---+|\*\*\*+|___+)\s*$")) { Flush(); blocks.Add(new Block(Kind.Rule, "")); continue; }
            var li = Regex.Match(line, @"^(\s*)([-*•+])\s+(.*)$");
            if (li.Success) { Flush(); blocks.Add(new Block(Kind.Bullet, li.Groups[3].Value, Math.Min(3, li.Groups[1].Value.Length / 2))); continue; }
            var ni = Regex.Match(line, @"^(\s*)(\d+)[.)]\s+(.*)$");
            if (ni.Success) { Flush(); blocks.Add(new Block(Kind.Number, ni.Groups[2].Value + ". " + ni.Groups[3].Value, Math.Min(3, ni.Groups[1].Value.Length / 2))); continue; }
            if (para.Length > 0) para.Append(' ');
            para.Append(line.Trim());
        }
        Flush();
        return blocks;
    }

    static bool IsTableRow(string line) => Regex.IsMatch(line, @"^\s*\|.*\|\s*$");
    static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    /// <summary>A line of text as runs: (text, bold, italic, code).</summary>
    static List<(string Text, bool Bold, bool Italic, bool Code)> Runs(string s)
    {
        var list = new List<(string, bool, bool, bool)>();
        int at = 0;
        foreach (Match m in Regex.Matches(s, @"\*\*(.+?)\*\*|__(.+?)__|`([^`]+)`|(?<![\w*])\*([^*\s][^*]*?)\*(?!\w)|\[([^\]]+)\]\(([^)\s]+)\)"))
        {
            if (m.Index > at) list.Add((s[at..m.Index], false, false, false));
            if (m.Groups[1].Success || m.Groups[2].Success) list.Add((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, true, false, false));
            else if (m.Groups[3].Success) list.Add((m.Groups[3].Value, false, false, true));
            else if (m.Groups[4].Success) list.Add((m.Groups[4].Value, false, true, false));
            else list.Add(($"{m.Groups[5].Value} ({m.Groups[6].Value})", false, false, false));
            at = m.Index + m.Length;
        }
        if (at < s.Length) list.Add((s[at..], false, false, false));
        return list;
    }

    static string X(string s) => SecurityElement.Escape(Regex.Replace(s, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "")) ?? "";

    // ───────────────────────────── Word ─────────────────────────────

    static byte[] Docx(string md)
    {
        var body = new StringBuilder();
        string Para(string inner, string props = "") => $"<w:p><w:pPr>{props}</w:pPr>{inner}</w:p>";
        string RunsXml(string text, int size = 22, bool forceBold = false, string? font = null)
        {
            var sb = new StringBuilder();
            foreach (var (t, b, it, code) in Runs(text))
            {
                string rpr = (b || forceBold ? "<w:b/>" : "") + (it ? "<w:i/>" : "") + (code || font != null ? $"<w:rFonts w:ascii=\"{font ?? "Consolas"}\" w:hAnsi=\"{font ?? "Consolas"}\" w:cs=\"{font ?? "Consolas"}\"/>" : "")
                    + $"<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/>";
                sb.Append($"<w:r><w:rPr>{rpr}</w:rPr><w:t xml:space=\"preserve\">{X(t)}</w:t></w:r>");
            }
            return sb.ToString();
        }
        foreach (var b in Parse(md))
        {
            switch (b.Kind)
            {
                case Kind.Heading:
                    int size = b.Level switch { 1 => 36, 2 => 30, 3 => 26, _ => 24 };
                    body.Append(Para(RunsXml(b.Text, size, forceBold: true), "<w:spacing w:before=\"240\" w:after=\"120\"/><w:keepNext/>"));
                    break;
                case Kind.Paragraph:
                    body.Append(Para(RunsXml(b.Text), "<w:spacing w:after=\"160\" w:line=\"276\" w:lineRule=\"auto\"/>"));
                    break;
                case Kind.Bullet:
                case Kind.Number:
                    string mark = b.Kind == Kind.Bullet ? "•\t" : "";
                    int left = 360 + b.Level * 360;
                    body.Append(Para($"<w:r><w:t xml:space=\"preserve\">{X(mark)}</w:t></w:r>" + RunsXml(b.Text), $"<w:tabs><w:tab w:val=\"left\" w:pos=\"{left}\"/></w:tabs><w:spacing w:after=\"60\"/><w:ind w:left=\"{left}\" w:hanging=\"{(b.Kind == Kind.Bullet ? 240 : 0)}\"/>"));
                    break;
                case Kind.Code:
                    foreach (var line in b.Text.Split('\n'))
                        body.Append(Para($"<w:r><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:sz w:val=\"19\"/></w:rPr><w:t xml:space=\"preserve\">{X(line)}</w:t></w:r>",
                            "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/><w:spacing w:after=\"0\"/>"));
                    body.Append(Para(""));
                    break;
                case Kind.Rule:
                    body.Append(Para("", "<w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"BFBFBF\"/></w:pBdr>"));
                    break;
                case Kind.Table when b.Rows is { Count: > 0 } rows:
                    int cols = rows.Max(r => r.Length);
                    body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>");
                    foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" }) body.Append($"<w:{side} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>");
                    body.Append("</w:tblBorders></w:tblPr><w:tblGrid>");
                    for (int c = 0; c < cols; c++) body.Append("<w:gridCol/>");
                    body.Append("</w:tblGrid>");
                    for (int r = 0; r < rows.Count; r++)
                    {
                        body.Append("<w:tr>");
                        for (int c = 0; c < cols; c++)
                        {
                            string cell = c < rows[r].Length ? rows[r][c] : "";
                            body.Append($"<w:tc><w:tcPr>{(r == 0 ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"EDEDED\"/>" : "")}</w:tcPr>{Para(RunsXml(cell, 21, forceBold: r == 0), "<w:spacing w:after=\"40\"/>")}</w:tc>");
                        }
                        body.Append("</w:tr>");
                    }
                    body.Append("</w:tbl>");
                    body.Append(Para(""));
                    break;
            }
        }
        string document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>"
            + body
            + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1300\" w:right=\"1300\" w:bottom=\"1300\" w:left=\"1300\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";
        string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"259\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>"
            + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>"
            + "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:tblPr><w:tblCellMar><w:left w:w=\"108\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style></w:styles>";
        return Zip(new()
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>",
            ["_rels/.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/_rels/document.xml.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>",
            ["word/document.xml"] = document,
            ["word/styles.xml"] = styles,
        });
    }

    // ───────────────────────────── Excel ─────────────────────────────

    static byte[] Xlsx(string content)
    {
        // a markdown table if there is one, else CSV-like rows (comma, semicolon or tab)
        var blocks = Parse(content);
        var rows = blocks.FirstOrDefault(b => b.Kind == Kind.Table)?.Rows;
        if (rows == null)
        {
            var lines = content.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("```")).ToList();
            char sep = lines.Count == 0 ? ',' : new[] { '\t', ';', ',' }.OrderByDescending(c => lines.Take(5).Sum(l => l.Count(ch => ch == c))).First();
            rows = lines.Select(l => SplitCsv(l, sep)).ToList();
        }
        var sheet = new StringBuilder();
        int maxCols = rows.Count == 0 ? 1 : rows.Max(r => r.Length);
        sheet.Append("<cols>");
        for (int c = 0; c < maxCols; c++)
        {
            int width = Math.Clamp(rows.Max(r => c < r.Length ? r[c].Length : 0) + 2, 8, 60);
            sheet.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{width}\" customWidth=\"1\"/>");
        }
        sheet.Append("</cols><sheetData>");
        for (int r = 0; r < rows.Count; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\">");
            for (int c = 0; c < rows[r].Length; c++)
            {
                string v = Regex.Replace(rows[r][c].Trim(), @"^\*\*(.*)\*\*$", "$1");
                string refName = Col(c) + (r + 1);
                string style = r == 0 ? " s=\"1\"" : "";
                string num = v.Replace(" ", "");
                if (r > 0 && double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !num.StartsWith("0") | num == "0" | num.StartsWith("0."))
                    sheet.Append($"<c r=\"{refName}\"{style}><v>{d.ToString(CultureInfo.InvariantCulture)}</v></c>");
                else
                    sheet.Append($"<c r=\"{refName}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{X(v)}</t></is></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData>");
        string sheetXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + (rows.Count > 1 ? "<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>" : "")
            + sheet + "</worksheet>";
        return Zip(new()
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>",
            ["_rels/.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = sheetXml,
            ["xl/styles.xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEDEDED\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/></cellXfs></styleSheet>",
        });
    }

    static string Col(int c) { string s = ""; c++; while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; } return s; }

    static string[] SplitCsv(string line, char sep)
    {
        var cells = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else quoted = !quoted; }
            else if (ch == sep && !quoted) { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        cells.Add(cur.ToString());
        return cells.ToArray();
    }

    static byte[] Zip(Dictionary<string, string> parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, xml) in parts)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
        return ms.ToArray();
    }

    // ───────────────────────────── PDF ─────────────────────────────

    /// <summary>A4 pages, Helvetica (Latin text; other letters become ?), headings bold, lists indented, code in Courier.</summary>
    static byte[] Pdf(string md)
    {
        const double W = 595, H = 842, M = 56, Width = W - 2 * M;
        var pages = new List<StringBuilder>();
        StringBuilder page = new();
        double y = H - M;
        void NewPage() { page = new StringBuilder(); pages.Add(page); y = H - M; }
        NewPage();
        void Line(string text, string font, double size, double indent = 0, double after = 0)
        {
            double lead = size * 1.35;
            if (y - lead < M) NewPage();
            y -= lead;
            page.Append($"BT /{font} {size.ToString(CultureInfo.InvariantCulture)} Tf {(M + indent).ToString("0.##", CultureInfo.InvariantCulture)} {y.ToString("0.##", CultureInfo.InvariantCulture)} Td ({PdfText(text)}) Tj ET\n");
            y -= after;
        }
        void Wrapped(string text, string font, double size, double indent = 0, double after = 4, string? first = null)
        {
            double charW = size * (font == "F3" ? 0.6 : 0.5);
            int perLine = Math.Max(10, (int)((Width - indent) / charW));
            var words = text.Split(' ');
            var cur = new StringBuilder(first ?? "");
            bool firstLine = true;
            foreach (var w in words)
            {
                if (cur.Length + w.Length + 1 > perLine && cur.Length > 0) { Line(cur.ToString(), font, size, indent); cur.Clear(); firstLine = false; }
                if (cur.Length > 0 && !(firstLine && first != null && cur.ToString() == first)) cur.Append(' ');
                cur.Append(w);
            }
            if (cur.Length > 0) Line(cur.ToString(), font, size, indent);
            y -= after;
        }
        string Plain(string s) => string.Concat(Runs(s).Select(r => r.Text));
        foreach (var b in Parse(md))
        {
            switch (b.Kind)
            {
                case Kind.Heading: y -= 6; Wrapped(Plain(b.Text), "F2", b.Level == 1 ? 18 : b.Level == 2 ? 15 : 13, 0, 4); break;
                case Kind.Paragraph: Wrapped(Plain(b.Text), "F1", 11, 0, 6); break;
                case Kind.Bullet: Wrapped("-  " + Plain(b.Text), "F1", 11, 12 + b.Level * 14, 2); break;
                case Kind.Number: Wrapped(Plain(b.Text), "F1", 11, 12 + b.Level * 14, 2); break;
                case Kind.Code: foreach (var l in b.Text.Split('\n')) Line(l, "F3", 9.5, 8); y -= 6; break;
                case Kind.Rule: y -= 6; if (y < M + 10) NewPage(); page.Append($"{M} {y.ToString("0.##", CultureInfo.InvariantCulture)} m {W - M} {y.ToString("0.##", CultureInfo.InvariantCulture)} l 0.75 G S\n"); y -= 8; break;
                case Kind.Table when b.Rows != null:
                    foreach (var (row, i) in b.Rows.Select((r, i) => (r, i))) Wrapped(string.Join("   |   ", row.Select(Plain)), i == 0 ? "F2" : "F1", 10, 0, 2);
                    y -= 6;
                    break;
            }
        }
        // the file: catalog, pages, fonts, then each page's content
        var objects = new List<string>();
        int fontBase = 3, first = 6;
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{string.Join(" ", pages.Select((_, i) => $"{first + i * 2} 0 R"))}] /Count {pages.Count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        var enc = Encoding.Latin1;
        var contents = new List<byte[]>();
        for (int i = 0; i < pages.Count; i++)
        {
            byte[] stream = enc.GetBytes(pages[i].ToString());
            contents.Add(stream);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] /Resources << /Font << /F1 {fontBase} 0 R /F2 {fontBase + 1} 0 R /F3 {fontBase + 2} 0 R >> >> /Contents {first + i * 2 + 1} 0 R >>");
            objects.Add("STREAM" + i);
        }
        using var ms = new MemoryStream();
        void Put(string s) { var b = enc.GetBytes(s); ms.Write(b, 0, b.Length); }
        Put("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            Put($"{i + 1} 0 obj\n");
            if (objects[i].StartsWith("STREAM"))
            {
                var data = contents[int.Parse(objects[i][6..])];
                Put($"<< /Length {data.Length} >>\nstream\n");
                ms.Write(data, 0, data.Length);
                Put("\nendstream\n");
            }
            else Put(objects[i] + "\n");
            Put("endobj\n");
        }
        long xref = ms.Position;
        Put($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Put($"{o:0000000000} 00000 n \n");
        Put($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>Text for a PDF string: WinAnsi letters, the three special characters escaped, the rest as '?'.</summary>
    static string PdfText(string s)
    {
        var sb = new StringBuilder();
        foreach (char c0 in s)
        {
            char c = c0 switch { '‘' or '’' => '\'', '“' or '”' => '"', '–' or '—' => '-', '•' => '-', '…' => '.', '\t' => ' ', _ => c0 };
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c >= 32 && c <= 255) sb.Append(c);
            else sb.Append('?');
        }
        return sb.ToString();
    }
}
