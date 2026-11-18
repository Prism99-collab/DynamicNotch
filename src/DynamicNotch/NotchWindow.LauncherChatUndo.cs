using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicNotch.Services;
using Do = DynamicNotch.Services.LauncherIntent.Do;

namespace DynamicNotch;

/// <summary>
/// Taking back what the AI did: every step that can be undone (a file it wrote or changed, a picture it made, the
/// volume, apps it opened or closed, reminders, dark mode, Wi-Fi..., the clipboard, a minimised window, a fact it
/// remembered) gets an Undo on its line; Ctrl+Z in the empty box, /undo, or "undo that" takes back the latest.
/// Deleted files go to the Recycle Bin. Also here: the AI's files (real Word / Excel / PDF), and pictures it makes.
/// </summary>
public partial class NotchWindow
{
    sealed record UndoStep(string Label, Func<string> Undo);
    readonly Dictionary<string, List<UndoStep>> _undo = new();
    UndoStep? _pendingUndo;   // set by the tool that just ran; picked up for its line

    void Undoable(string label, Func<string> undo) => _pendingUndo = new UndoStep(label, undo);

    List<UndoStep> UndoLog => _undo.TryGetValue((_session ??= NewSession()).Id, out var l) ? l : _undo[_session.Id] = new();

    /// <summary>Takes back the latest undoable step (the line it's on says so).</summary>
    string UndoLast()
    {
        var log = UndoLog;
        if (log.Count == 0) return "Nothing to undo.";
        var step = log[^1];
        log.RemoveAt(log.Count - 1);
        string said;
        try { said = step.Undo(); } catch (Exception ex) { said = "Couldn't undo: " + ex.Message; }
        _undoLinks.TryGetValue(step, out var link);
        if (link != null) { link.Text = "undone"; link.Cursor = null; link.IsHitTestVisible = false; }
        return said;
    }

    readonly Dictionary<UndoStep, TextBlock> _undoLinks = new();

    /// <summary>The "Undo" on a step's line.</summary>
    void AttachUndo(StepView step, UndoStep undo)
    {
        UndoLog.Add(undo);
        var link = new TextBlock { Text = "Undo", FontSize = 10, Foreground = DimBrush, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), ToolTip = "Take back: " + undo.Label };
        link.MouseEnter += (_, _) => link.Foreground = Frozen(ChatAccent);
        link.MouseLeave += (_, _) => link.Foreground = DimBrush;
        link.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var log = UndoLog;
            if (!log.Contains(undo)) return;
            log.Remove(undo);
            string said;
            try { said = undo.Undo(); } catch (Exception ex) { said = "Couldn't undo: " + ex.Message; }
            link.Text = "undone";
            link.IsHitTestVisible = false;
            Toast(said);
            _launchBox?.Focus();
        };
        _undoLinks[undo] = link;
        step.AddExtra(link);
    }

    // ───────────────────────────── the Recycle Bin ─────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    static bool Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT { wFunc = 3 /* delete */, pFrom = path + "\0\0", fFlags = 0x40 | 0x10 | 0x4 | 0x400 /* undo, no confirm, silent, no error ui */ };
        return SHFileOperation(ref op) == 0 && !File.Exists(path);
    }

    // ───────────────────────────── files it writes ─────────────────────────────

    /// <summary>
    /// A file from the AI: real Word / Excel / PDF for those types, text for the rest. create never replaces;
    /// append adds; replace overwrites after your OK (the old one is kept for Undo).
    /// </summary>
    async Task<string> WriteFileTool(string path, string content, string mode, CancellationToken cancel)
    {
        try
        {
            path = ExpandPath(path);
            if (!Path.IsPathRooted(path)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), path);
            if (Path.GetExtension(path).Length == 0) path += ".txt";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool exists = File.Exists(path);
            if (mode == "append")
            {
                if (DocWriter.IsDocument(path)) return "Error: can't append to a " + Path.GetExtension(path) + " file; write a new one, or use mode 'replace' with the whole content.";
                byte[]? before = exists ? File.ReadAllBytes(path) : null;
                File.AppendAllText(path, content);
                Undoable("the text added to " + Path.GetFileName(path), () => { if (before == null) Recycle(path); else File.WriteAllBytes(path, before); return "Took back the text added to " + Path.GetFileName(path); });
                return "Added to " + path;
            }
            if (exists && mode != "replace") return "Error: " + path + " already exists. Pick another name, or use mode 'replace' (the user is asked first).";
            byte[]? old = null;
            if (exists)
            {
                if (_settings.AiAsk != "never" && await ConfirmInChat("Replace " + Path.GetFileName(path), path, cancel) == 0) return "The user declined: the file was not replaced.";
                old = File.ReadAllBytes(path);
            }
            DocWriter.Write(path, content);
            if (old != null) Undoable("the new " + Path.GetFileName(path), () => { File.WriteAllBytes(path, old); return "Put back the old " + Path.GetFileName(path); });
            else Undoable("the new file " + Path.GetFileName(path), () => Recycle(path) ? "Moved " + Path.GetFileName(path) + " to the Recycle Bin" : "Couldn't remove " + Path.GetFileName(path));
            return (exists ? "Replaced " : "Saved ") + path + (DocWriter.IsDocument(path) ? " (a real " + Path.GetExtension(path).TrimStart('.').ToUpperInvariant() + " file)" : "");
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    // ───────────────────────────── pictures it makes ─────────────────────────────

    async Task<string> MakePictureTool(string prompt, bool fromAttached, CancellationToken cancel)
    {
        if (prompt.Trim().Length == 0) return "Error: describe the picture.";
        string? key = OpenRouter.LoadOpenRouterKey();
        // a new picture: free, no key needed (Pollinations); OpenRouter only to change an attached picture, or if that fails
        if (!fromAttached || key == null)
        {
            var (free, freeError) = await FreePicture(prompt, cancel);
            if (free != null) return SavePictures(new List<byte[]> { free }, "a free picture model");
            if (key == null) return fromAttached
                ? "Error: changing a picture needs an OpenRouter key with a little credit (Preferences → AI chat); a new picture from a description works for free."
                : "Error: the free picture service didn't answer (" + freeError + "). Try again in a minute.";
        }
        var models = await OpenRouter.ImageModels();
        if (models.Count == 0) return "Error: no picture-making model is available on OpenRouter right now.";
        var model = models.FirstOrDefault(m => m.Id == _settings.AiImageModel) ?? models[0];
        byte[]? from = null;
        if (fromAttached)
            foreach (var m in (_session?.Messages ?? new JsonArray()).Reverse())
                if (m?["content"] is JsonArray parts && parts.FirstOrDefault(p => p?["type"]?.ToString() == "image_url")?["image_url"]?["url"]?.ToString() is { } url && url.Contains(','))
                { from = Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]); break; }
        var (images, _, error) = await OpenRouter.MakeImage(key!, model.Id, prompt, from, cancel);
        if (error != null)
            return error.Contains("credit", StringComparison.OrdinalIgnoreCase)
                ? $"Error: making pictures needs OpenRouter credit: no picture model is free right now (cheapest: {models.FirstOrDefault(m => !m.Free)?.Id ?? model.Id}, about a cent a picture). The user can add credit at https://openrouter.ai/settings/credits."
                : "Error: " + error;
        return SavePictures(images, model.Id);
    }

    static readonly System.Net.Http.HttpClient PictureHttp = new() { Timeout = TimeSpan.FromSeconds(100) };

    /// <summary>A picture from a description, free and without a key; its shape follows the words (wallpaper, phone...).</summary>
    static async Task<(byte[]? Bytes, string? Error)> FreePicture(string prompt, CancellationToken cancel)
    {
        var (w, h) = System.Text.RegularExpressions.Regex.IsMatch(prompt, @"wallpaper|landscape|wide|banner|cinematic|desktop|16:9|fond d.[ée]cran|paysage", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? (1536, 864)
            : System.Text.RegularExpressions.Regex.IsMatch(prompt, @"portrait|phone|vertical|story|poster|9:16|affiche", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? (864, 1536) : (1024, 1024);
        string words = prompt.Trim();
        if (words.Length > 800) words = words[..800];
        string url = $"https://image.pollinations.ai/prompt/{Uri.EscapeDataString(words)}?width={w}&height={h}&nologo=true&seed={Random.Shared.Next(1, 1_000_000)}";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var res = await PictureHttp.GetAsync(url, cancel);
                string type = res.Content.Headers.ContentType?.MediaType ?? "";
                if (res.IsSuccessStatusCode && type.StartsWith("image/"))
                {
                    var bytes = await res.Content.ReadAsByteArrayAsync(cancel);
                    if (bytes.Length > 2000) return (bytes, null);
                }
                if ((int)res.StatusCode is not (429 or >= 500)) return (null, $"it said {(int)res.StatusCode}");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return (null, "stopped"); }
            catch (Exception ex) { if (attempt == 1) return (null, ex.Message); }
            await Task.Delay(2500, cancel);
        }
        return (null, "busy");
    }

    string SavePictures(List<byte[]> images, string madeWith)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Dynamic Notch");
        Directory.CreateDirectory(folder);
        var saved = new List<string>();
        foreach (var bytes in images)
        {
            string ext = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? ".jpg" : ".png";
            string file = Path.Combine(folder, $"AI {DateTime.Now:yyyy-MM-dd HH.mm.ss}{(saved.Count > 0 ? " " + (saved.Count + 1) : "")}{ext}");
            File.WriteAllBytes(file, bytes);
            saved.Add(file);
            ChatPicture(file);
        }
        Undoable(saved.Count == 1 ? "the picture" : "the pictures", () => { foreach (var f in saved) Recycle(f); return saved.Count == 1 ? "Moved the picture to the Recycle Bin" : "Moved the pictures to the Recycle Bin"; });
        return "Made with " + madeWith + " and saved: " + string.Join(", ", saved) + ". It's shown in the chat already: don't repeat it as a markdown image or list the path again; say one short line about it.";
    }

    /// <summary>A picture on this PC, shown in the chat: a click opens it.</summary>
    void ChatPicture(string file)
    {
        if (_chatStack == null) return;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 720;
            img.UriSource = new Uri(file);
            img.EndInit();
            img.Freeze();
            var acc = ChatAccent;
            var frame = new Border
            {
                Child = new Image { Source = img, MaxHeight = 240, Stretch = Stretch.Uniform }, CornerRadius = new CornerRadius(_cyber ? 0 : 10), ClipToBounds = true,
                BorderBrush = Frozen(A(acc, 0x70)), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, ToolTip = file + "  ·  click to open, Ctrl+click to show in its folder",
            };
            frame.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenLink(file); };
            var row = new Grid { Margin = new Thickness(12, 8, 18, 2) };
            row.Children.Add(frame);
            _chatStack.Children.Add(row);
            Arrive(row);
            FollowChat(force: true);
        }
        catch { }
    }

    // ───────────────────────────── the notch's commands, taken back ─────────────────────────────

    /// <summary>Before a command runs: how to put things back after it (null when there's nothing sensible to undo).</summary>
    Func<string>? UndoFor(LauncherIntent.Intent t, List<ReminderItem> remindersBefore)
    {
        switch (t.Kind)
        {
            case Do.Volume:
            {
                float vol = _audio.Volume;
                bool muted = _audio.Muted;
                return () => { if (_audio.Muted != muted) _audio.ToggleMute(); _audio.SetVolume(vol); RefreshVolume(); return $"Volume back to {(int)Math.Round(vol * 100)}%{(muted ? " (muted)" : "")}"; };
            }
            case Do.Remind:
                return () =>
                {
                    var added = _reminders.Where(r => !remindersBefore.Contains(r)).ToList();
                    foreach (var r in added) _reminders.Remove(r);
                    SaveReminders();
                    ScheduleReminders();
                    return added.Count == 1 ? "Reminder removed" : $"{added.Count} reminders removed";
                };
            case Do.Open when t.Op != "switch" && t.Text is { } names:
                return () =>
                {
                    int n = 0;
                    foreach (var name in OpenNames(names))
                        foreach (var w in RunningWindows(name)) { PostMessage(w.Hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); n++; }
                    return n > 0 ? "Closed what it opened" : "It's already closed";
                };
            case Do.Close when t.Op != "force" && t.Text is { } app:
                return () => { _ = Execute(new LauncherIntent.Intent(Do.Open, "open", app)); return "Opening " + app + " again"; };
            case Do.DarkMode or Do.Wifi or Do.Bluetooth or Do.Airplane or Do.DesktopIcons:
                return () => { _ = Execute(t.On is bool on ? t with { On = !on } : t); return "Switched back"; };
        }
        return null;
    }
}
