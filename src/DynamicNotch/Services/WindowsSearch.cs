using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Files anywhere on the PC, from the index Windows already keeps (the one File Explorer's search box uses): by name,
/// and by the words inside documents. Nothing is scanned here; Windows has done the work. Asked on a thread of its own,
/// a moment after you stop typing, and only the newest question is answered. If indexing is off on this PC the search
/// simply keeps to what it knew before.
/// </summary>
public sealed class WindowsSearch
{
    public sealed record Item(string Path, string Name, bool Folder, DateTime Modified, long Size, bool ByContent);

    /// <summary>Answers arrive on the search's thread: (the question, what was found).</summary>
    public event Action<string, List<Item>>? Results;

    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    string? _pending;
    Thread? _worker;
    volatile bool _dead;
    dynamic? _conn;
    int _failures;

    public bool Available => !_dead;

    /// <summary>Also look for the words inside documents (not only in file names).</summary>
    public volatile bool Inside = true;

    /// <summary>Ask (replacing any question not answered yet).</summary>
    public void Query(string query)
    {
        if (_dead) return;
        lock (_gate) _pending = query;
        if (_worker == null)
        {
            _worker = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Windows Search" };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
        _wake.Set();
    }

    void Loop()
    {
        while (!_dead)
        {
            _wake.WaitOne();
            // wait for a short pause in the typing: one question per pause, not one per key
            string? q;
            while (true)
            {
                lock (_gate) q = _pending;
                Thread.Sleep(110);
                bool changed;
                lock (_gate) changed = !ReferenceEquals(q, _pending);
                if (!changed) break;
            }
            lock (_gate) _pending = null;
            if (q == null) continue;
            try
            {
                var found = Ask(q);
                bool newer;
                lock (_gate) newer = _pending != null;
                if (!newer) Results?.Invoke(q, found); // else: something newer was asked meanwhile
                _failures = 0;
            }
            catch (Exception ex)
            {
                _conn = null;
                if (++_failures >= 3) { _dead = true; AppLog.Write("windows search off for this session: " + ex.Message); }
            }
        }
    }

    List<Item> Ask(string query)
    {
        var words = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}._+-]*").Select(m => m.Value.Trim('.', '-', '_')).Where(w => w.Length > 0).Take(5).ToList();
        if (words.Count == 0) return new();
        if (_conn == null)
        {
            var type = Type.GetTypeFromProgID("ADODB.Connection");
            if (type == null) { _dead = true; return new(); }
            try
            {
                _conn = Activator.CreateInstance(type)!;
                _conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            }
            catch (Exception ex)
            {
                // the Windows Search service is off or missing on this PC
                _conn = null;
                _dead = true;
                AppLog.Write("windows search unavailable: " + ex.Message);
                return new();
            }
        }
        var found = new List<Item>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // by name: every word starts a word of the file's name
        string byName = string.Join(" AND ", words.Select(w => $"CONTAINS(System.FileName, '\"{Esc(w)}*\"')"));
        Read($"SELECT TOP 24 System.ItemUrl, System.ItemNameDisplay, System.ItemType, System.DateModified, System.Size FROM SystemIndex " +
             $"WHERE SCOPE='file:' AND {byName} AND {Skip} ORDER BY System.DateModified DESC", false, found, seen);

        // by what's inside, for three letters or more: the documents that best fit (not code or data files)
        if (Inside && words.Sum(w => w.Length) >= 3 && found.Count < 20)
        {
            string inside = string.Join(" AND ", words.Select(w => $"\"{Esc(w)}*\""));
            Read($"SELECT TOP 10 System.ItemUrl, System.ItemNameDisplay, System.ItemType, System.DateModified, System.Size FROM SystemIndex " +
                 $"WHERE SCOPE='file:' AND CONTAINS(*, '{inside}') AND System.Kind = 'document' AND System.ItemType <> 'Directory' AND {Skip} ORDER BY System.Search.Rank DESC", true, found, seen);
        }
        return found;
    }

    /// <summary>Places people never mean: app data, caches, code dependencies, the Recycle Bin.</summary>
    const string Skip = "System.ItemUrl NOT LIKE '%/AppData/%' AND System.ItemUrl NOT LIKE '%/node_modules/%' " +
                        "AND System.ItemUrl NOT LIKE '%/.git/%' AND System.ItemUrl NOT LIKE '%$Recycle.Bin%' " +
                        "AND System.ItemUrl NOT LIKE '%/.%' " + // hidden tool folders: .cargo, .vscode…
                        "AND System.ItemUrl NOT LIKE '%/obj/%' AND System.ItemUrl NOT LIKE '%/bin/Debug/%' AND System.ItemUrl NOT LIKE '%/bin/Release/%'";

    /// <summary>"file:C:/Users/…" to a real path (the display path is translated, C:\Usuarios\…, and can't be opened).</summary>
    static string? FromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        string p = Uri.UnescapeDataString(url[5..]).TrimStart('/').Replace('/', '\\');
        if (p.Length >= 2 && p[1] == ':') return p;           // C:\…
        return url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? @"\\" + p : null; // a network share
    }

    static string Esc(string w)
    {
        var sb = new StringBuilder(w.Length);
        foreach (char c in w) if (char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+') sb.Append(c);
        return sb.ToString();
    }

    void Read(string sql, bool byContent, List<Item> into, HashSet<string> seen)
    {
        dynamic rs = _conn!.Execute(sql);
        try
        {
            while (!(bool)rs.EOF)
            {
                string? path = FromUrl(rs.Fields.Item(0).Value as string);
                if (!string.IsNullOrEmpty(path) && seen.Add(path))
                {
                    bool folder = (rs.Fields.Item(2).Value as string) == "Directory";
                    DateTime modified = rs.Fields.Item(3).Value is DateTime d ? d.ToLocalTime() : DateTime.MinValue;
                    long size = 0;
                    try { object? s = rs.Fields.Item(4).Value; if (s != null && s is not DBNull) size = Convert.ToInt64(s); } catch { }
                    string name = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path; // the real name, with its extension
                    into.Add(new Item(path, name, folder, modified, size, byContent));
                }
                rs.MoveNext();
            }
        }
        finally { try { rs.Close(); } catch { } }
    }
}
