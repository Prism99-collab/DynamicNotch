using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicNotch.Services;

/// <summary>
/// What the browsers on this PC remember: downloads and visited pages, from the History database of Chromium
/// browsers (Brave, Chrome, Edge, Vivaldi, Opera), every profile. Read from a copy (the browser keeps the file
/// open), with Windows' own SQLite (winsqlite3.dll): nothing to install. Only read when the AI is asked to.
/// </summary>
public static class BrowserHistory
{
    static readonly (string Name, string Folder)[] Browsers =
    {
        ("brave", @"BraveSoftware\Brave-Browser\User Data"),
        ("chrome", @"Google\Chrome\User Data"),
        ("edge", @"Microsoft\Edge\User Data"),
        ("vivaldi", @"Vivaldi\User Data"),
        ("opera", @"..\Roaming\Opera Software\Opera Stable"),
    };

    public static string Query(string browser, string kind, string text, int days, int limit = 40)
    {
        browser = browser.Trim().ToLowerInvariant();
        days = Math.Clamp(days <= 0 ? 7 : days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);
        long sinceChrome = (since - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10;
        var rows = new List<(DateTime At, string Line)>();
        var found = new List<string>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (name, folder) in Browsers)
        {
            if (browser.Length > 0 && browser != "any" && browser != "all" && !name.StartsWith(browser) && !browser.StartsWith(name)) continue;
            string root = Path.GetFullPath(Path.Combine(local, folder));
            if (!Directory.Exists(root)) continue;
            var files = new List<string>();
            if (File.Exists(Path.Combine(root, "History"))) files.Add(Path.Combine(root, "History")); // Opera: no profiles
            foreach (var dir in Directory.EnumerateDirectories(root).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ")))
                if (File.Exists(Path.Combine(dir, "History"))) files.Add(Path.Combine(dir, "History"));
            if (files.Count > 0) found.Add(name);
            foreach (var file in files)
            {
                string? copy = null;
                try
                {
                    copy = Path.Combine(Path.GetTempPath(), "dn-history-" + Guid.NewGuid().ToString("N")[..8]);
                    using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var dst = File.Create(copy)) src.CopyTo(dst);
                    string profile = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                    string tag = name + (profile is "Default" or "" || profile.StartsWith("Opera") ? "" : " · " + profile);
                    if (kind == "downloads")
                        foreach (var r in Read(copy, "SELECT target_path, tab_url, start_time, total_bytes, state FROM downloads WHERE start_time > ?1 ORDER BY start_time DESC LIMIT 200", sinceChrome))
                        {
                            string path = r[0], url = r[1];
                            if (text.Length > 0 && !path.Contains(text, StringComparison.OrdinalIgnoreCase) && !url.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                            var at = FromChrome(r[2]);
                            long.TryParse(r[3], out long size);
                            string state = r[4] == "1" ? "" : r[4] == "2" ? " (cancelled)" : r[4] == "4" ? " (interrupted)" : "";
                            bool still = path.Length > 0 && File.Exists(path);
                            rows.Add((at, $"{at.ToLocalTime():ddd d MMM HH:mm} · {tag} · {Path.GetFileName(path)} · {SystemMonitor.Bytes(size)}{state} · {(still ? path : "(file no longer there) " + path)} · from {Short(url)}"));
                        }
                    else
                    {
                        string sql = "SELECT u.url, u.title, v.visit_time FROM visits v JOIN urls u ON u.id = v.url WHERE v.visit_time > ?1 ORDER BY v.visit_time DESC LIMIT 800";
                        var seen = new HashSet<string>();
                        foreach (var r in Read(copy, sql, sinceChrome))
                        {
                            string url = r[0], title = r[1];
                            if (text.Length > 0 && !url.Contains(text, StringComparison.OrdinalIgnoreCase) && !title.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!seen.Add(url)) continue;
                            var at = FromChrome(r[2]);
                            rows.Add((at, $"{at.ToLocalTime():ddd d MMM HH:mm} · {tag} · {(title.Length > 0 ? title : "(no title)")} · {url}"));
                        }
                    }
                }
                catch (Exception ex) { rows.Add((DateTime.MinValue, $"({name}: couldn't read its history: {ex.Message})")); }
                finally { if (copy != null) try { File.Delete(copy); } catch { } }
            }
        }
        if (found.Count == 0) return browser is "" or "any" or "all" ? "No Chromium browser history found on this PC (Brave, Chrome, Edge, Vivaldi, Opera)." : $"No history found for “{browser}” on this PC.";
        if (rows.Count == 0) return $"Nothing {(kind == "downloads" ? "downloaded" : "visited")}{(text.Length > 0 ? $" matching “{text}”" : "")} in the last {days} day{(days == 1 ? "" : "s")} ({string.Join(", ", found)}).";
        var list = rows.OrderByDescending(r => r.At).Take(Math.Clamp(limit, 1, 100)).Select(r => r.Line);
        return $"{(kind == "downloads" ? "Downloads" : "Visits")}, last {days} days, newest first ({string.Join(", ", found)}):\n" + string.Join("\n", list);
    }

    static string Short(string url) => url.Length > 90 ? url[..87] + "…" : url;

    static DateTime FromChrome(string micro) => long.TryParse(micro, out long v) && v > 0
        ? new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(v * 10) : DateTime.MinValue;

    // ── Windows' own SQLite

    static IEnumerable<string[]> Read(string file, string sql, long arg)
    {
        var result = new List<string[]>();
        if (sqlite3_open_v2(Encoding.UTF8.GetBytes(file + "\0"), out IntPtr db, 1 /* read only */, IntPtr.Zero) != 0) { sqlite3_close(db); throw new IOException("can't open the database"); }
        try
        {
            if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out IntPtr st, IntPtr.Zero) != 0) throw new IOException(Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "bad query");
            try
            {
                sqlite3_bind_int64(st, 1, arg);
                int cols = sqlite3_column_count(st);
                while (sqlite3_step(st) == 100 /* a row */)
                {
                    var row = new string[cols];
                    for (int i = 0; i < cols; i++) row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(st, i)) ?? "";
                    result.Add(row);
                }
            }
            finally { sqlite3_finalize(st); }
        }
        finally { sqlite3_close(db); }
        return result;
    }

    const string Lib = "winsqlite3.dll";
    [DllImport(Lib)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] static extern int sqlite3_close(IntPtr db);
    [DllImport(Lib)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib)] static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Lib)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] static extern IntPtr sqlite3_errmsg(IntPtr db);
}
