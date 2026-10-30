using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicNotch.Services;

/// <summary>
/// The real size of a download in progress, from the browser's own download list. A ".crdownload" file only says
/// how much has arrived; Chromium browsers (Brave, Chrome, Edge, Opera, Vivaldi) write the total into their
/// History database as soon as the download starts. That database is copied (the browser keeps it open) and read
/// with Windows' built-in SQLite.
/// </summary>
public static class BrowserDownloads
{
    public sealed record Info(long Total, string? Target, string? Url);

    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    static readonly string[] UserDataDirs =
    {
        Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"),
        Path.Combine(Local, @"Google\Chrome\User Data"),
        Path.Combine(Local, @"Microsoft\Edge\User Data"),
        Path.Combine(Local, @"Vivaldi\User Data"),
        Path.Combine(Local, @"Chromium\User Data"),
        Path.Combine(Roaming, @"Opera Software\Opera Stable"),
        Path.Combine(Roaming, @"Opera Software\Opera GX Stable"),
    };

    /// <summary>Downloads in progress in any Chromium browser, by the partial file's full path (case-insensitive).</summary>
    public static Dictionary<string, Info> InProgress()
    {
        var result = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        foreach (var history in HistoryFiles())
        {
            try { Read(history, result); }
            catch (Exception ex) { AppLog.Write($"[debug] browser downloads: {Path.GetFileName(Path.GetDirectoryName(history))}: {ex.Message}"); }
        }
        return result;
    }

    /// <summary>Only profiles that wrote to their history recently: a download in progress always does.</summary>
    static IEnumerable<string> HistoryFiles()
    {
        foreach (var root in UserDataDirs)
        {
            if (!Directory.Exists(root)) continue;
            var candidates = new List<string> { Path.Combine(root, "History") }; // Opera keeps it at the root
            try { candidates.AddRange(Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "History"))); } catch { }
            foreach (var h in candidates)
                if (Recent(h)) yield return h;
        }
    }

    static bool Recent(string history)
    {
        try
        {
            var fi = new FileInfo(history);
            return fi.Exists && fi.Length < 300_000_000 && DateTime.UtcNow - fi.LastWriteTimeUtc < TimeSpan.FromHours(6);
        }
        catch { return false; }
    }

    static void Read(string history, Dictionary<string, Info> into)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "DynamicNotch");
        Directory.CreateDirectory(tmp);
        string copy = Path.Combine(tmp, "history-" + Environment.CurrentManagedThreadId + ".db");
        try
        {
            // the browser holds the file open; it shares reading, so a plain copy works
            using (var src = new FileStream(history, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(copy, FileMode.Create, FileAccess.Write))
                src.CopyTo(dst);
            if (File.Exists(history + "-wal"))
                using (var src = new FileStream(history + "-wal", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = new FileStream(copy + "-wal", FileMode.Create, FileAccess.Write))
                    src.CopyTo(dst);

            if (sqlite3_open_v2(Utf8(copy), out var db, 0x2 /* READWRITE */, IntPtr.Zero) != 0) { sqlite3_close(db); return; }
            try
            {
                // state 0 = in progress
                if (sqlite3_prepare_v2(db, Utf8("SELECT current_path, target_path, total_bytes, tab_url FROM downloads WHERE state = 0"), -1, out var stmt, IntPtr.Zero) != 0) return;
                try
                {
                    while (sqlite3_step(stmt) == 100 /* ROW */)
                    {
                        string? current = Text(stmt, 0), target = Text(stmt, 1), url = Text(stmt, 3);
                        long total = sqlite3_column_int64(stmt, 2);
                        if (string.IsNullOrEmpty(current)) continue;
                        into[current] = new Info(total, string.IsNullOrEmpty(target) ? null : target, url);
                    }
                }
                finally { sqlite3_finalize(stmt); }
            }
            finally { sqlite3_close(db); }
        }
        finally
        {
            try { File.Delete(copy); } catch { }
            try { File.Delete(copy + "-wal"); } catch { }
            try { File.Delete(copy + "-shm"); } catch { }
            try { File.Delete(copy + "-journal"); } catch { }
        }
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    static string? Text(IntPtr stmt, int col)
    {
        IntPtr p = sqlite3_column_text(stmt, col);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    }

    const string Sqlite = "winsqlite3.dll"; // part of Windows 10 and 11

    [DllImport(Sqlite)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Sqlite)] static extern int sqlite3_close(IntPtr db);
    [DllImport(Sqlite)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Sqlite)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Sqlite)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Sqlite)] static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Sqlite)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
}
