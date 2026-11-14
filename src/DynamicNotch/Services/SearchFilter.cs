namespace DynamicNotch.Services;

/// <summary>What a file must be to show: of these types, a folder, changed within these dates, at least this big.</summary>
public sealed record FileFilter(HashSet<string>? Exts, bool FoldersOnly, DateTime? After, DateTime? Before, long MinSize, string Label)
{
    public bool Allows(string name, bool folder, DateTime modified)
    {
        if (FoldersOnly != folder && (FoldersOnly || Exts != null)) return false;
        if (Exts != null && !folder)
        {
            int dot = name.LastIndexOf('.');
            if (dot < 0 || !Exts.Contains(name[(dot + 1)..].ToLowerInvariant())) return false;
        }
        if (After is { } a && modified < a) return false;
        if (Before is { } b && modified >= b) return false;
        return true;
    }

    public string Key => $"{(Exts == null ? "" : string.Join(",", Exts.Order()))}|{FoldersOnly}|{After:O}|{Before:O}|{MinSize}";
}

/// <summary>
/// Filters said in plain words, taken out of what's typed: "pdf invoice", "pictures today", "excel last week",
/// "big videos", "screenshots yesterday", "invoices march", "folders downloads", ".psd", "ext:svg".
/// Only clear words filter: a date, a size, ".ext", a plural type ("pictures", "videos") or "pdf". "video editor",
/// "word" or "music" stay normal searches (the app, the folder); a singular type joins in when something else filters
/// ("photo today").
/// </summary>
public static class SearchFilter
{
    static readonly string[] Images = { "jpg", "jpeg", "png", "gif", "webp", "bmp", "heic", "heif", "tif", "tiff", "raw", "cr2", "nef", "arw", "dng", "avif", "svg", "psd" };
    static readonly string[] Videos = { "mp4", "mkv", "mov", "avi", "webm", "wmv", "m4v", "flv", "mpg", "mpeg", "3gp" };
    static readonly string[] Audio = { "mp3", "wav", "flac", "m4a", "ogg", "aac", "wma", "opus", "aiff" };
    static readonly string[] Docs = { "pdf", "doc", "docx", "odt", "rtf", "txt", "md", "xls", "xlsx", "csv", "ods", "ppt", "pptx", "odp", "pages", "numbers", "key", "epub" };
    static readonly string[] Words = { "doc", "docx", "odt", "rtf" };
    static readonly string[] Sheets = { "xls", "xlsx", "xlsm", "csv", "ods", "numbers" };
    static readonly string[] Slides = { "ppt", "pptx", "odp", "key" };
    static readonly string[] Archives = { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso" };
    static readonly string[] Installers = { "exe", "msi", "msix", "appx" };
    static readonly string[] Code = { "cs", "js", "ts", "tsx", "jsx", "py", "java", "kt", "cpp", "c", "h", "hpp", "go", "rs", "php", "rb", "swift", "html", "css", "scss", "json", "xml", "yaml", "yml", "sql", "sh", "ps1", "xaml", "lua", "dart" };
    static readonly string[] Texts = { "txt", "md", "rtf", "log" };

    /// <summary>Each type word: its extensions, its name in the label, and whether it filters on its own.</summary>
    static readonly Dictionary<string, (string[] Exts, string Name, bool Alone)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = (new[] { "pdf" }, "PDF", true), ["pdfs"] = (new[] { "pdf" }, "PDF", true),
        ["picture"] = (Images, "Pictures", false), ["pictures"] = (Images, "Pictures", true), ["photo"] = (Images, "Photos", false), ["photos"] = (Images, "Photos", true),
        ["image"] = (Images, "Images", false), ["images"] = (Images, "Images", true), ["pics"] = (Images, "Pictures", true),
        ["screenshot"] = (Images, "Screenshots", false), ["screenshots"] = (Images, "Screenshots", true),
        ["video"] = (Videos, "Videos", false), ["videos"] = (Videos, "Videos", true), ["movie"] = (Videos, "Videos", false), ["movies"] = (Videos, "Videos", true),
        ["clip"] = (Videos, "Clips", false), ["clips"] = (Videos, "Clips", true), ["recording"] = (Videos, "Recordings", false), ["recordings"] = (Videos, "Recordings", true),
        ["music"] = (Audio, "Music", false), ["song"] = (Audio, "Songs", false), ["songs"] = (Audio, "Songs", true), ["audio"] = (Audio, "Audio", false), ["mp3"] = (new[] { "mp3" }, "MP3", false),
        ["document"] = (Docs, "Documents", false), ["documents"] = (Docs, "Documents", true), ["docs"] = (Docs, "Documents", true),
        ["word"] = (Words, "Word files", false), ["doc"] = (Words, "Word files", false), ["docx"] = (Words, "Word files", false),
        ["excel"] = (Sheets, "Spreadsheets", false), ["sheet"] = (Sheets, "Spreadsheets", false), ["sheets"] = (Sheets, "Spreadsheets", true),
        ["spreadsheet"] = (Sheets, "Spreadsheets", false), ["spreadsheets"] = (Sheets, "Spreadsheets", true), ["xlsx"] = (Sheets, "Spreadsheets", false),
        ["powerpoint"] = (Slides, "Presentations", false), ["slides"] = (Slides, "Presentations", true), ["presentation"] = (Slides, "Presentations", false),
        ["presentations"] = (Slides, "Presentations", true), ["pptx"] = (Slides, "Presentations", false),
        ["zip"] = (Archives, "Archives", false), ["zips"] = (Archives, "Archives", true), ["archive"] = (Archives, "Archives", false), ["archives"] = (Archives, "Archives", true),
        ["installer"] = (Installers, "Installers", false), ["installers"] = (Installers, "Installers", true), ["setup"] = (Installers, "Installers", false),
        ["code"] = (Code, "Code", false), ["text"] = (Texts, "Text files", false), ["notes"] = (Texts, "Notes", false),
    };

    static readonly string[] Months = { "january", "february", "march", "april", "june", "july", "august", "september", "october", "november", "december" }; // ("may" is a word too)

    /// <summary>The words that are still the name, and the filter (null when nothing typed is a filter).</summary>
    public static (string Terms, FileFilter? Filter) Parse(string query, DateTime now)
    {
        var tokens = query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count == 0) return (query, null);
        HashSet<string>? exts = null;
        string? typeName = null;
        bool folders = false, dated = false, sized = false, explicitType = false, aloneType = false;
        DateTime? after = null, before = null;
        string? when = null;
        long minSize = 0;
        var keep = new List<string>();
        var extraTerms = new List<string>();
        DateTime today = now.Date;
        DateTime monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i], next = i + 1 < tokens.Count ? tokens[i + 1] : "";
            // explicit types: ".psd", "ext:svg"
            if (t.StartsWith("ext:") && t.Length > 4 || t.Length is > 1 and <= 6 && t[0] == '.' && t[1..].All(char.IsLetterOrDigit))
            {
                (exts ??= new()).Add(t.StartsWith("ext:") ? t[4..].TrimStart('.') : t[1..]);
                typeName ??= "." + (t.StartsWith("ext:") ? t[4..].TrimStart('.') : t[1..]).ToUpperInvariant();
                explicitType = true;
                continue;
            }
            if (t is "folder" or "folders") { folders = true; typeName ??= "Folders"; continue; }
            if (Types.TryGetValue(t, out var ty))
            {
                (exts ??= new()).UnionWith(ty.Exts);
                typeName ??= ty.Name;
                aloneType |= ty.Alone;
                if (t.StartsWith("screenshot")) extraTerms.Add("screenshot");
                continue;
            }
            // dates
            if (t is "today") { after = today; before = null; when = "today"; dated = true; continue; }
            if (t is "yesterday") { after = today.AddDays(-1); before = today; when = "yesterday"; dated = true; continue; }
            if (t is "recent" or "recently" or "latest") { after = today.AddDays(-7); when = "recent"; dated = true; continue; }
            if (t is "this" or "last" or "past" && next is "week" or "month" or "year")
            {
                bool thisOne = t == "this";
                (after, before, when) = next switch
                {
                    "week" => thisOne ? (monday, (DateTime?)null, "this week") : t == "past" ? (today.AddDays(-7), null, "past week") : (monday.AddDays(-7), monday, "last week"),
                    "month" => thisOne ? (new DateTime(today.Year, today.Month, 1), (DateTime?)null, "this month") : t == "past" ? (today.AddDays(-30), null, "past month")
                        : (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1), "last month"),
                    _ => thisOne ? (new DateTime(today.Year, 1, 1), (DateTime?)null, "this year") : t == "past" ? (today.AddDays(-365), null, "past year")
                        : (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year, 1, 1), "last year"),
                };
                dated = true;
                i++;
                continue;
            }
            if (t is "last" or "past" && int.TryParse(next, out int n) && n is > 0 and < 1000 && i + 2 < tokens.Count && tokens[i + 2] is "days" or "day" or "weeks" or "week")
            {
                int days = tokens[i + 2].StartsWith("week") ? n * 7 : n;
                after = today.AddDays(-days + 1); when = $"last {n} {tokens[i + 2]}"; dated = true;
                i += 2;
                continue;
            }
            int month = Array.IndexOf(Months, t);
            if (month >= 0)
            {
                int m = month < 4 ? month + 1 : month + 2; // the list skips May
                int year = m > today.Month ? today.Year - 1 : today.Year;
                after = new DateTime(year, m, 1); before = after.Value.AddMonths(1); when = char.ToUpperInvariant(t[0]) + t[1..]; dated = true;
                continue;
            }
            // sizes
            if (t is "big" or "large" or "biggest" or "largest") { minSize = 100L << 20; sized = true; continue; }
            if (t is "huge") { minSize = 1L << 30; sized = true; continue; }
            // little words around a filter
            if (t is "files" or "file" or "my" or "from" or "of" or "the" or "in" or "on" or "all" or "show" or "find") { keep.Add(t); continue; }
            keep.Add(t);
        }

        // only filter when the words clearly mean it
        var names = keep.Where(w => w is not ("files" or "file" or "my" or "from" or "of" or "the" or "in" or "on" or "all" or "show" or "find")).ToList();
        // a singular type word ("video editor") is a name, not a filter, unless something else says filter ("photo today")
        bool active = dated || sized || explicitType || aloneType || folders && names.Count > 0;
        if (!active) return (query, null);

        names.AddRange(extraTerms);
        string label = string.Join(" · ", new[] { sized ? (minSize >= 1L << 30 ? "Huge" : "Big") + (typeName != null ? " " + typeName.ToLowerInvariant() : " files") : typeName ?? "Files", when }
            .Where(x => !string.IsNullOrEmpty(x)));
        if (label.Length == 0) label = "Files";
        return (string.Join(' ', names), new FileFilter(exts, folders, after, before, minSize, label));
    }
}
