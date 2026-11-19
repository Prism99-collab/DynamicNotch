using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DynamicNotch.Services;

/// <summary>
/// Reads the documents people actually have: PDF (its text; a scanned page is read with Windows' own text
/// recognition), Excel (.xlsx, every sheet as rows), PowerPoint (.pptx, slide by slide) and pictures (the text in them,
/// read by Windows). Everything stays on this PC. Null: not one of these, the caller reads it as text.
/// </summary>
public static class DocReader
{
    public static readonly string[] Kinds = { ".pdf", ".xlsx", ".xlsm", ".pptx", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    const int MaxChars = 30000;

    public static string? Read(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".pdf" => Pdf(path),
                ".xlsx" or ".xlsm" => Excel(path),
                ".pptx" => Slides(path),
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" => Picture(path),
                ".xls" or ".doc" or ".ppt" => $"This is an old Office file ({ext}): it can't be read directly. Open it and save it as {ext}x, or read it with the app.",
                _ => null,
            };
        }
        catch (Exception ex) { return "Error: couldn't read it (" + ex.Message + ")"; }
    }

    static string Cut(string s) => s.Length > MaxChars ? s[..MaxChars] + "\n…(the document goes on; only the start was read)" : s;

    // ───────────────────────────── PDF ─────────────────────────────

    static string Pdf(string path)
    {
        var sb = new StringBuilder();
        var empty = new List<int>();
        int pages;
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            pages = doc.NumberOfPages;
            foreach (var page in doc.GetPages())
            {
                if (sb.Length > MaxChars) break;
                string text;
                try { text = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page); }
                catch { text = page.Text; }
                text = Regex.Replace(text, @"[ \t]+\n", "\n").Trim();
                if (text.Length < 20) { empty.Add(page.Number); sb.AppendLine($"[page {page.Number}: OCR]"); continue; }
                sb.AppendLine($"[page {page.Number}]");
                sb.AppendLine(text);
            }
        }
        // pages that are pictures (a scan): Windows reads their text, a few pages at most
        if (empty.Count > 0)
        {
            var read = Ocr.PdfPages(path, empty.Take(8).ToList());
            foreach (int n in empty)
                sb.Replace($"[page {n}: OCR]", read.TryGetValue(n, out var t) && t.Length > 0 ? $"[page {n}, scanned: read by text recognition]\n{t}" : $"[page {n}: a picture with no readable text]");
        }
        string all = sb.ToString().Trim();
        if (all.Length == 0) return "This PDF has no text that could be read.";
        return Cut($"PDF, {pages} page{(pages == 1 ? "" : "s")}:\n{all}");
    }

    // ───────────────────────────── Excel ─────────────────────────────

    static string Excel(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument? Load(string name) { var e = zip.GetEntry(name); if (e == null) return null; using var s = e.Open(); return XDocument.Load(s); }
        static IEnumerable<XElement> All(XContainer x, string local) => x.Descendants().Where(e => e.Name.LocalName == local);

        // the shared texts (most cells point into them)
        var shared = new List<string>();
        if (Load("xl/sharedStrings.xml") is { } ss)
            foreach (var si in All(ss, "si")) shared.Add(string.Concat(All(si, "t").Select(t => t.Value)));

        // the sheets, in the workbook's order, with their names
        var book = Load("xl/workbook.xml") ?? throw new InvalidDataException("not an Excel workbook");
        var rels = Load("xl/_rels/workbook.xml.rels");
        var targets = rels == null ? new Dictionary<string, string>() : All(rels, "Relationship").ToDictionary(r => (string?)r.Attribute("Id") ?? "", r => (string?)r.Attribute("Target") ?? "");
        var sb = new StringBuilder();
        int sheetNo = 0;
        foreach (var sheet in All(book, "sheet"))
        {
            sheetNo++;
            string name = (string?)sheet.Attribute("name") ?? $"Sheet{sheetNo}";
            string? rid = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
            string target = rid != null && targets.TryGetValue(rid, out var t) ? t : $"worksheets/sheet{sheetNo}.xml";
            target = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
            var xml = Load(target);
            if (xml == null) continue;
            sb.AppendLine($"## Sheet “{name}”");
            int rows = 0;
            foreach (var row in All(xml, "row"))
            {
                var cells = new SortedDictionary<int, string>();
                foreach (var c in All(row, "c"))
                {
                    string r = (string?)c.Attribute("r") ?? "";
                    int col = ColumnOf(r);
                    string type = (string?)c.Attribute("t") ?? "";
                    string v = All(c, "v").FirstOrDefault()?.Value ?? "";
                    string value = type switch
                    {
                        "s" => int.TryParse(v, out int i) && i < shared.Count ? shared[i] : v,
                        "inlineStr" => string.Concat(All(c, "t").Select(x => x.Value)),
                        "b" => v == "1" ? "TRUE" : "FALSE",
                        _ => v,
                    };
                    if (All(c, "f").FirstOrDefault() is { } f && f.Value.Length > 0) value += $" (={f.Value})";
                    if (value.Length > 0) cells[col < 0 ? cells.Count : col] = value.Replace('\n', ' ').Replace("|", "/");
                }
                if (cells.Count == 0) continue;
                int last = cells.Keys.Max();
                sb.AppendLine("| " + string.Join(" | ", Enumerable.Range(0, last + 1).Select(i => cells.TryGetValue(i, out var s) ? s : "")) + " |");
                if (++rows >= 400) { sb.AppendLine("…(more rows)"); break; }
                if (sb.Length > MaxChars) break;
            }
            if (rows == 0) sb.AppendLine("(empty)");
            sb.AppendLine();
        }
        return Cut(sb.ToString().Trim());
    }

    /// <summary>"C7" → 2 (A = 0).</summary>
    static int ColumnOf(string reference)
    {
        int n = 0, i = 0;
        for (; i < reference.Length && char.IsLetter(reference[i]); i++) n = n * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        return i == 0 ? -1 : n - 1;
    }

    // ───────────────────────────── PowerPoint ─────────────────────────────

    static string Slides(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var slides = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
            .OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value)).ToList();
        var sb = new StringBuilder($"Presentation, {slides.Count} slides:\n");
        int n = 0;
        foreach (var e in slides)
        {
            n++;
            using var s = e.Open();
            var x = XDocument.Load(s);
            var paras = x.Descendants().Where(p => p.Name.LocalName == "p")
                .Select(p => string.Concat(p.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)).Trim())
                .Where(p => p.Length > 0);
            sb.AppendLine($"[slide {n}]");
            foreach (var p in paras) sb.AppendLine(p);
            if (sb.Length > MaxChars) break;
        }
        return Cut(sb.ToString().Trim());
    }

    // ───────────────────────────── pictures ─────────────────────────────

    static string Picture(string path)
    {
        string text = Ocr.File(path);
        return text.Length == 0 ? "A picture with no readable text in it (to see what it shows, attach it to the chat)." : "Text in the picture (read by Windows' text recognition):\n" + Cut(text);
    }
}

/// <summary>Windows' own text recognition (no download, nothing leaves the PC), in the languages the PC has.</summary>
public static class Ocr
{
    static Windows.Media.Ocr.OcrEngine? Engine() =>
        Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

    public static string File(string path) => Run(async () =>
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        return await Read(stream);
    });

    /// <summary>The text in a picture held in memory (PNG / JPEG bytes).</summary>
    public static string Bytes(byte[] picture) => Run(async () =>
    {
        using var stream = new MemoryStream(picture).AsRandomAccessStream();
        return await Read(stream);
    });

    /// <summary>The text of some PDF pages (1-based numbers), each drawn as a picture first.</summary>
    public static Dictionary<int, string> PdfPages(string path, List<int> pages)
    {
        var found = new Dictionary<int, string>();
        Run(async () =>
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            foreach (int n in pages)
            {
                if (n < 1 || n > pdf.PageCount) continue;
                using var page = pdf.GetPage((uint)(n - 1));
                using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(ms, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1700 });
                ms.Seek(0);
                found[n] = await Read(ms);
            }
            return "";
        });
        return found;
    }

    static async Task<string> Read(Windows.Storage.Streams.IRandomAccessStream stream)
    {
        var engine = Engine();
        if (engine == null) return "";
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        uint max = Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        var transform = new Windows.Graphics.Imaging.BitmapTransform();
        if (decoder.PixelWidth > max || decoder.PixelHeight > max)
        {
            double k = Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight);
            transform.ScaledWidth = (uint)(decoder.PixelWidth * k);
            transform.ScaledHeight = (uint)(decoder.PixelHeight * k);
        }
        using var bmp = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            transform, Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bmp);
        return string.Join("\n", result.Lines.Select(l => l.Text)).Trim();
    }

    /// <summary>Waits for Windows' async work on a pool thread (never on the window's thread), 25 s at most.</summary>
    static string Run(Func<Task<string>> work)
    {
        try
        {
            var t = Task.Run(work);
            return t.Wait(TimeSpan.FromSeconds(25)) ? t.Result : "";
        }
        catch { return ""; }
    }
}
