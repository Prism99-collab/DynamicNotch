using System.IO;

namespace DynamicNotch.Services;

public static partial class BrowserHistory
{
    /// <summary>
    /// The address of each page title (the page last visited with that title), from the browsers' history: for a tab the
    /// Work page saw by its title and now has to open again. Only read when asked (Reopen all, open a tab, ask a project).
    /// </summary>
    public static Dictionary<string, string> UrlsFor(IEnumerable<string> titles, int days = 14)
    {
        var want = titles.Where(t => t.Trim().Length > 1).Select(t => t.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, (string Url, DateTime At)>(StringComparer.OrdinalIgnoreCase);
        if (want.Count == 0) return new();
        long since = (DateTime.UtcNow.AddDays(-days) - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (_, folder) in Browsers)
        {
            string root = Path.GetFullPath(Path.Combine(local, folder));
            if (!Directory.Exists(root)) continue;
            var files = new List<string>();
            if (File.Exists(Path.Combine(root, "History"))) files.Add(Path.Combine(root, "History"));
            foreach (var dir in Directory.EnumerateDirectories(root).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ")))
                if (File.Exists(Path.Combine(dir, "History"))) files.Add(Path.Combine(dir, "History"));
            foreach (var file in files)
            {
                string? copy = null;
                try
                {
                    copy = Path.Combine(Path.GetTempPath(), "dn-history-" + Guid.NewGuid().ToString("N")[..8]);
                    using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var dst = File.Create(copy)) src.CopyTo(dst);
                    foreach (var r in Read(copy, "SELECT url, title, last_visit_time FROM urls WHERE last_visit_time > ?1 ORDER BY last_visit_time DESC LIMIT 3000", since))
                    {
                        string url = r[0], title = r[1].Trim();
                        if (!want.Contains(title) || !url.StartsWith("http")) continue;
                        var at = FromChrome(r[2]);
                        if (!found.TryGetValue(title, out var was) || at > was.At) found[title] = (url, at);
                    }
                }
                catch { }
                finally { if (copy != null) try { File.Delete(copy); } catch { } }
            }
        }
        return found.ToDictionary(f => f.Key, f => f.Value.Url, StringComparer.OrdinalIgnoreCase);
    }
}
