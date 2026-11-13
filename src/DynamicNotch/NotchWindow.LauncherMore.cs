using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The search's reach beyond its own list:
///  • files anywhere on the PC, from Windows' own index (by name, and by the words inside documents);
///  • the windows already open ("chrome" goes to the Chrome you have, a window's title finds it);
///  • what you copied and what's on the shelf;
///  • a second level for every line (→ or right-click): open with, show in folder, copy the path or the file,
///    run as administrator, a terminal there, close the window, pin a copied text…;
///  • pictures show themselves as a thumbnail.
/// Windows and files are read when the search opens or while you type, never in the background otherwise.
/// </summary>
public partial class NotchWindow
{
    ScrollViewer? _launchScroll;
    TextBlock? _launchGhost;

    // ───────────────────────────── Windows' own index ─────────────────────────────

    readonly WindowsSearch _ws = new();
    List<WindowsSearch.Item> _wsItems = new();
    string? _wsAsked, _wsAnswered;

    void WireWindowsSearch()
    {
        _ws.Results += (q, items) => Dispatcher.InvokeAsync(() =>
        {
            if (!_launcherOpen || _launchBox == null || !string.Equals(_wsAsked, q, StringComparison.Ordinal)) return;
            _wsItems = items;
            _wsAnswered = q;
            if (_actionsFor == null) Search(merge: true); // the list grows in place; the line you're on stays picked
        });
    }

    // ───────────────────────────── open windows ─────────────────────────────

    sealed record OpenWin(IntPtr Hwnd, string Title, string? Path, string App, string NormExe, string NormDesc);

    List<OpenWin> _openWins = new();
    int _winsVersion;

    /// <summary>The visible app windows, read once as the search opens (off the UI thread).</summary>
    void LoadOpenWindows()
    {
        int v = ++_winsVersion;
        uint me = (uint)Environment.ProcessId;
        Task.Run(() =>
        {
            var list = new List<OpenWin>();
            var info = new Dictionary<uint, (string? Path, string Exe, string Desc, string Name)>();
            try
            {
                EnumWindows((h, _) =>
                {
                    if (!IsWindowVisible(h) || Interop.Native.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                    if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
                    if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true; // a suspended app's hidden window
                    string title = WindowTitle(h);
                    if (title.Length == 0 || ClassName(h) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid == me) return true;
                    if (!info.TryGetValue(pid, out var i))
                    {
                        string? path = ProcessPathById(pid);
                        string exe = path != null ? System.IO.Path.GetFileNameWithoutExtension(path) : "";
                        string desc = "";
                        try { if (path != null) desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; } catch { }
                        info[pid] = i = (path, Norm(exe), Norm(desc), desc.Length > 0 ? desc : exe);
                    }
                    bool frame = i.Exe == "applicationframehost"; // a Store app: its name is the title
                    list.Add(new OpenWin(h, title, frame ? null : i.Path, frame ? title : i.Name, frame ? Norm(title) : i.Exe, frame ? Norm(title) : i.Desc));
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list;
        }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (v != _winsVersion || !_launcherOpen) return;
            _openWins = t.Result;
            if (_launchBox is { Text.Length: > 0 } && _actionsFor == null) Search(merge: true);
        }), TaskScheduler.Default);
    }

    /// <summary>The open window of an app from the list (by its file, its description, or a Store app's title).</summary>
    OpenWin? WindowFor(LauncherIndex.Entry app)
    {
        if (_openWins.Count == 0) return null;
        string name = Norm(app.Name);
        string? exe = DesktopAppExe(app) is { } p ? Norm(System.IO.Path.GetFileNameWithoutExtension(p)) : null;
        return _openWins.FirstOrDefault(w => exe != null && w.Path != null && w.NormExe == exe)
            ?? _openWins.FirstOrDefault(w => name.Length >= 3 && (w.NormDesc == name || w.NormExe == name));
    }

    void OfferWindows(string find, IEnumerable<LauncherIndex.Entry?> apps, Action<Hit, double> offer)
    {
        if (_openWins.Count == 0) return;
        var covered = new HashSet<IntPtr>(apps.Where(a => a != null).Select(a => WindowFor(a!)?.Hwnd ?? IntPtr.Zero));
        foreach (var w in _openWins)
        {
            double byTitle = LauncherIndex.Score(find, new LauncherIndex.Entry(w.Title, "", LauncherIndex.Kind.File, null));
            double byApp = LauncherIndex.Score(find, new LauncherIndex.Entry(w.App, "", LauncherIndex.Kind.File, null));
            if (byTitle <= 0 && (byApp <= 0 || covered.Contains(w.Hwnd))) continue; // the app's own line already goes there
            double s = Math.Max(byTitle, byApp - 60) + 20;
            var win = w;
            offer(new Hit(w.Title, w.App == w.Title ? "Open window" : "Open window · " + w.App, w.Path == null ? "" : null, w.Path,
                () => { CloseLauncher(giveBack: false); FocusWindow(win.Hwnd); },
                Section: "Open windows", Highlight: find, Actions: () => WindowActions(win), Key: "win|" + w.Hwnd), s);
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // ───────────────────────────── copied and kept ─────────────────────────────

    void OfferKept(string find, Action<Hit, double> offer)
    {
        var words = find.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;
        foreach (var c in _clips)
        {
            if (c.Text is not { Length: > 0 } text) continue;
            string low = text.ToLowerInvariant();
            if (!words.All(w => low.Contains(w))) continue;
            string line = OneLine(text);
            var clip = c;
            offer(new Hit(Trim(line, 90), (c.Pinned ? "Pinned · " : "Copied · ") + Ago(c.At), "", null, () => CopyClipAgain(clip),
                Section: "Clipboard & shelf", Highlight: find, Actions: () => ClipActions(clip), Key: "clip|" + text.GetHashCode()),
                (low.StartsWith(words[0]) ? 300 : 260) + (c.Pinned ? 20 : 0));
        }
        foreach (var item in _shelf)
        {
            string name = item.Name;
            string low = name.ToLowerInvariant();
            if (!words.All(w => low.Contains(w))) continue;
            var it = item;
            offer(new Hit(Trim(name, 90), "On the shelf · Enter copies it", item.Path == null ? "" : null, item.Path, () =>
            {
                _ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(800);
                try { ShelfTransfer.CopyToClipboard(it.Path, it.Text); } catch { }
                CloseLauncher(giveBack: true);
                After(200, () => ShowOsd("copied", "", null, "Copied", it.Path != null ? $"{it.Name} · Ctrl+V to paste" : "Ctrl+V to paste", null, glyphBrush: Blue, seconds: 1.6, prio: Prio.Low));
            }, Section: "Clipboard & shelf", Highlight: find, Actions: it.Path != null ? () => PathActions(it.Path!, false) : null, Key: "shelf|" + (it.Path ?? it.Text)), 250);
        }
    }

    static string OneLine(string t) => Regex.Replace(t, @"\s+", " ").Trim();

    /// <summary>A copied text from the search: on the clipboard again, at the top of the history, with a nod.</summary>
    void CopyClipAgain(ClipEntry c)
    {
        _ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(600);
        try { if (c.Text != null) Clipboard.SetText(c.Text); } catch { }
        if (!c.Pinned) { c.At = DateTime.Now; _clips.Remove(c); _clips.Insert(0, c); ClipsChanged(); }
        CloseLauncher(giveBack: true);
        After(200, () => ShowOsd("copied", "", null, "Copied", Trim(OneLine(c.Text ?? ""), 60), null, glyphBrush: Blue, seconds: 1.4, prio: Prio.Low));
    }

    // ───────────────────────────── the second level: → on a line ─────────────────────────────

    sealed record HitAction(string Label, string Glyph, Action Run, string Detail = "");

    Hit? _actionsFor;
    List<Hit>? _mainHits;
    int _mainSel;

    void EnterActions()
    {
        if (_launchSel < 0 || _launchSel >= _hits.Count || _hits[_launchSel].Actions is not { } make) return;
        var list = make();
        if (list.Count == 0) return;
        _actionsFor = _hits[_launchSel];
        _mainHits = _hits.ToList();
        _mainSel = _launchSel;
        _hits.Clear();
        foreach (var a in list) _hits.Add(new Hit(a.Label, a.Detail, a.Glyph, null, a.Run));
        _launchSel = 0;
        SetGhost(null);
        RenderHits(animate: true);
    }

    void LeaveActions()
    {
        if (_actionsFor == null || _mainHits == null) return;
        _actionsFor = null;
        _hits.Clear();
        _hits.AddRange(_mainHits);
        _launchSel = Math.Min(_mainSel, _hits.Count - 1);
        _mainHits = null;
        RenderHits(animate: false);
        if (_launchSel == 0) UpdateGhost();
    }

    List<HitAction> EntryActions(LauncherIndex.Entry e, OpenWin? win)
    {
        var list = new List<HitAction>();
        if (e.Kind == LauncherIndex.Kind.App)
        {
            if (win != null)
            {
                list.Add(new("Switch to it", "", () => { _index.Used(e); CloseLauncher(giveBack: false); FocusWindow(win.Hwnd); }, Trim(win.Title, 50)));
                list.Add(new("Open a new one", "", () => { _index.Used(e); OpenTarget(e.Target); }));
            }
            else list.Add(new("Open", "", () => { _index.Used(e); OpenTarget(e.Target); }));
            if (DesktopAppExe(e) is { } exe)
            {
                list.Add(new("Run as administrator", "", () => { _index.Used(e); RunAsAdmin(exe); }, "Windows asks first"));
                list.Add(new("Show in folder", "", () => { CloseLauncher(giveBack: false); RevealInFolder(exe); }, ParentName(exe)));
            }
            // Frequent, from here too
            bool hidden = _settings.SearchHiddenApps.Contains(e.Target);
            list.Add(new(hidden ? "Show in Frequent" : "Keep out of Frequent", hidden ? "" : "", () =>
            {
                if (hidden) _settings.SearchHiddenApps.Remove(e.Target);
                else if (!_settings.SearchHiddenApps.Contains(e.Target)) _settings.SearchHiddenApps.Add(e.Target);
                _settings.Save();
                LeaveActions();
                SetFoot(hidden ? $"{Trim(e.Name, 30)} can show in Frequent again" : $"{Trim(e.Name, 30)} won't show in Frequent");
            }, hidden ? "It's kept out now" : "The row of usual apps in the empty search"));
            if (win != null)
                list.Add(new("Close it", "", () => { CloseLauncher(giveBack: true); foreach (var w in _openWins.Where(w => w.NormExe == win.NormExe && w.Path == win.Path)) PostMessage(w.Hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); },
                    "It asks first if there's unsaved work"));
            return list;
        }
        list.AddRange(PathActions(e.Target, e.Kind == LauncherIndex.Kind.Folder, e));
        return list;
    }

    List<HitAction> PathActions(string path, bool folder, LauncherIndex.Entry? e = null)
    {
        var list = new List<HitAction>
        {
            new("Open", "", () => { if (e != null) _index.Used(e); OpenTarget(path); }),
        };
        bool lnk = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        if (!folder && !lnk) list.Add(new("Open with…", "", () => { CloseLauncher(giveBack: false); Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}"); }, "Pick the app"));
        if (!lnk) list.Add(new("Show in folder", "", () => { CloseLauncher(giveBack: false); RevealInFolder(path); }, ParentName(path)));
        list.Add(new("Copy the path", "", () => CopyAnswer(path), Trim(path, 50)));
        if (!lnk) list.Add(new(folder ? "Copy the folder" : "Copy the file", "", () =>
        {
            try { Clipboard.SetFileDropList(new StringCollection { path }); } catch { }
            CloseLauncher(giveBack: true);
            After(200, () => ShowOsd("copied", "", null, "Copied", $"{System.IO.Path.GetFileName(path.TrimEnd('\\'))} · Ctrl+V to paste", null, glyphBrush: Blue, seconds: 1.6, prio: Prio.Low));
        }, "Paste it in a folder or a chat"));
        if (folder) list.Add(new("Open a terminal here", "", () => { CloseLauncher(giveBack: false); Terminal(path); }));
        return list;
    }

    List<HitAction> WindowActions(OpenWin w) => new()
    {
        new("Switch to it", "", () => { CloseLauncher(giveBack: false); FocusWindow(w.Hwnd); }, w.App),
        new("Close the window", "", () => { CloseLauncher(giveBack: true); PostMessage(w.Hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); }, "It asks first if there's unsaved work"),
    };

    List<HitAction> ClipActions(ClipEntry c) => new()
    {
        new("Copy", "", () => CopyClipAgain(c)),
        new(c.Pinned ? "Unpin" : "Pin", "", () => { c.Pinned = !c.Pinned; ClipsChanged(); LeaveActions(); Search(); }, c.Pinned ? "Back to the normal list" : "Stays at the top, through Clear"),
        new("Remove from the history", "", () => { _clips.Remove(c); ClipsChanged(); LeaveActions(); Search(); }),
    };

    static void Start(string file, string args)
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); } catch (Exception ex) { AppLog.Write("launcher: " + ex.Message); }
    }

    static void Terminal(string folder)
    {
        try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder.TrimEnd('\\')}\\.\"") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true }); } catch { }
        }
    }

    void RunAsAdmin(string exe)
    {
        CloseLauncher(giveBack: false);
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? "" }); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // you said no to Windows' question
        catch (Exception ex) { AppLog.Write("run as admin: " + ex.Message); }
    }

    /// <summary>
    /// A desktop app's own .exe, from its Start menu id ("{known folder}\Google\Chrome\Application\chrome.exe" or a full
    /// path). Null for Store apps and anything that isn't an .exe on disk.
    /// </summary>
    static string? DesktopAppExe(LauncherIndex.Entry e)
    {
        if (e.Kind != LauncherIndex.Kind.App) return null;
        lock (ExeOf)
        {
            if (ExeOf.TryGetValue(e.Target, out var known)) return known;
            return ExeOf[e.Target] = FindExe(e);
        }
    }

    static readonly Dictionary<string, string?> ExeOf = new(StringComparer.OrdinalIgnoreCase);

    static string? FindExe(LauncherIndex.Entry e)
    {
        const string prefix = "shell:AppsFolder\\";
        if (!e.Target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string id = e.Target[prefix.Length..];
        if (id.Contains('!') || !id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Regex.Match(id, @"^\{([0-9A-Fa-f-]{36})\}\\(.+)$");
        string? path = id;
        if (m.Success)
        {
            path = null;
            var guid = Guid.Parse(m.Groups[1].Value);
            if (SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out IntPtr p) == 0)
            {
                try { path = System.IO.Path.Combine(Marshal.PtrToStringUni(p) ?? "", m.Groups[2].Value); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        try { return path != null && System.IO.Path.IsPathRooted(path) && File.Exists(path) ? path : null; } catch { return null; }
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    // ───────────────────────────── pictures show themselves ─────────────────────────────

    readonly Dictionary<string, ImageSource?> _thumbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A small thumbnail in place of the file icon, decoded off the UI thread and kept for the session.</summary>
    void ShowThumb(Border box, string path)
    {
        if (_thumbs.TryGetValue(path, out var known)) { if (known != null) Apply(known); return; }
        _thumbs[path] = null;
        Task.Run(() =>
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > 60L * 1024 * 1024) return null;
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(path);
                img.DecodePixelWidth = 48;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                img.EndInit();
                img.Freeze();
                return (ImageSource?)img;
            }
            catch { return null; }
        }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            if (t.Result is not { } img) return;
            _thumbs[path] = img;
            Apply(img);
        }), TaskScheduler.Default);

        void Apply(ImageSource img)
        {
            box.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            box.Width = box.Height = 22;
            box.BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            box.BorderThickness = new Thickness(0.5);
            box.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(160)));
        }
    }
}
