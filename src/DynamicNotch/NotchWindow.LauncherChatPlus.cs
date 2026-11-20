using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicNotch.Services;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The chat, further: pictures (attached, pasted with Ctrl+V, or the screen itself) sent to a model that can see,
/// pictures and videos in answers, what's sent to the model kept within its limits, the notch telling you when an
/// answer is ready while the chat isn't on screen, and how risky a command is (read / change / danger).
/// </summary>
public partial class NotchWindow
{
    readonly List<byte[]> _chatPictures = new();       // JPEGs to send with the next message
    readonly Queue<string> _chatQueue = new();         // typed while it was answering: sent next
    readonly Dictionary<string, BitmapImage> _mediaCache = new();
    readonly Dictionary<string, HashSet<string>> _trusted = new();   // per chat: kinds of risky command you allowed
    GoogleSearch? _google;

    /// <summary>The AI's search engine: Google through Edge's engine (on the window's thread), the others as they are.</summary>
    void WireWebSearch()
    {
        WebTools.Engine = _settings.AiSearchEngine;
        if (WebTools.Google != null) return;
        WebTools.Google = (q, max, cancel) =>
        {
            if (_hwnd == IntPtr.Zero || !GoogleSearch.Available) return Task.FromResult(new List<WebTools.Hit>());
            return Dispatcher.InvokeAsync(() => (_google ??= new GoogleSearch(_hwnd, Dispatcher)).Search(q, max, cancel)).Task.Unwrap();
        };
    }

    // ───────────────────────────── pictures in ─────────────────────────────

    static readonly string[] PictureExts = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    /// <summary>A picture, made small enough to send (1280 px at most, JPEG).</summary>
    static byte[]? Shrink(BitmapSource src)
    {
        try
        {
            double scale = Math.Min(1, 1280.0 / Math.Max(src.PixelWidth, src.PixelHeight));
            BitmapSource bmp = scale < 1 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
            if (bmp.Format != PixelFormats.Bgr24 && bmp.Format != PixelFormats.Bgra32) bmp = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    bool AddPicture(BitmapSource src, string label)
    {
        if (_chatPictures.Count >= 4) { Toast("Up to 4 pictures per message"); return false; }
        if (Shrink(src) is not { } jpeg) { Toast("Couldn't read that picture"); return false; }
        _chatPictures.Add(jpeg);
        ShowAttachments();
        Toast(label);
        return true;
    }

    bool AddPictureFile(string path)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            img.EndInit();
            return AddPicture(img, "Picture attached");
        }
        catch { Toast("Couldn't read that picture"); return false; }
    }

    /// <summary>Ctrl+V with a picture on the clipboard (a screenshot, a copied image): it's attached.</summary>
    bool PastePicture()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) return AddPicture(img, "Picture pasted");
            if (Clipboard.ContainsFileDropList())
            {
                bool any = false;
                foreach (string? f in Clipboard.GetFileDropList())
                    if (f != null && PictureExts.Contains(Path.GetExtension(f).ToLowerInvariant())) any |= AddPictureFile(f);
                return any;
            }
        }
        catch { }
        return false;
    }

    /// <summary>The screen as it is now (the chat steps aside for the shot), attached to your next message.</summary>
    async void AttachScreen()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        // out of the picture for a moment
        double was = Root.Opacity, popWas = _pop?.Opacity ?? 1;
        Root.Opacity = 0;
        if (_pop != null) _pop.Opacity = 0;
        await Task.Delay(140);
        try
        {
            var b = screen.Bounds;
            using var bmp = new System.Drawing.Bitmap(b.Width, b.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(b.Left, b.Top, 0, 0, bmp.Size);
            var hbmp = bmp.GetHbitmap();
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                AddPicture(src, "Screen attached");
            }
            finally { DeleteObject(hbmp); }
        }
        catch (Exception ex) { Toast("Couldn't take the screen: " + ex.Message); }
        finally
        {
            Root.Opacity = was;
            if (_pop != null) _pop.Opacity = popWas;
            _launchBox?.Focus();
        }
    }

    FrameworkElement Thumb(byte[] jpeg, double height)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelHeight = (int)(height * 2);
        img.StreamSource = new MemoryStream(jpeg);
        img.EndInit();
        img.Freeze();
        return new Border
        {
            Height = height, Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(_cyber ? 0 : 5), ClipToBounds = true,
            Child = new Image { Source = img, Height = height, Stretch = Stretch.Uniform }, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    static string DataUri(byte[] jpeg) => "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);

    /// <summary>Can this model see pictures? (A model that can't is swapped for the free one, which can, for that message.)</summary>
    async Task<string> ModelForPictures(string model)
    {
        if (OpenRouter.IsGemini) return model; // Gemini and Gemma both see pictures
        var vision = await OpenRouter.VisionModels();
        if (vision == null || model == OpenRouter.DefaultModel || vision.Contains(model)) return model;
        ChatNote("", $"{ModelName(model)} can't see pictures: a free model that can answers this one.", tone: ChatAccent);
        return OpenRouter.DefaultModel;
    }

    // ───────────────────────────── what's sent ─────────────────────────────

    /// <summary>
    /// The conversation as the model gets it: the system prompt first; only the last two messages keep their
    /// pictures; old tool results shortened; the oldest exchanges left out when it grows past what models take.
    /// </summary>
    JsonArray BuildRequest(ChatSession session, JsonArray? tools)
    {
        var msgs = session.Messages;
        int lastPictures = 0;
        var keepPictures = new HashSet<int>();
        for (int i = msgs.Count - 1; i >= 0 && lastPictures < 2; i--)
            if (msgs[i]?["content"] is JsonArray) { keepPictures.Add(i); lastPictures++; }
        var list = new List<JsonObject>();
        for (int i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (m == null) continue;
            string role = m["role"]?.ToString() ?? "";
            // the last few messages go in full; older ones shortened, so a long chat stays as quick as a new one
            bool old = i < msgs.Count - 6;
            if (tools == null && role == "tool") { list.Add(new JsonObject { ["role"] = "user", ["content"] = "(result of an earlier step) " + Cut(m["content"]?.ToString() ?? "", old ? 700 : 4000) }); continue; }
            var copy = m.DeepClone().AsObject();
            copy.Remove("shown");
            copy.Remove("auto");
            copy.Remove("at");
            copy.Remove("file");
            copy.Remove("pictures");
            if (copy["content"] is JsonArray parts && !keepPictures.Contains(i))
                copy["content"] = (parts.FirstOrDefault(p => p?["type"]?.ToString() == "text")?["text"]?.ToString() ?? "") + " [picture]";
            if (role == "tool" && copy["content"]?.ToString() is { } result && result.Length > (old ? 700 : 4000)) copy["content"] = result[..(old ? 700 : 4000)] + "\n…(shortened)";
            // an older answer: its start is enough to follow the conversation
            if (role == "assistant" && old && copy["content"] is JsonValue && copy["content"]?.ToString() is { Length: > 900 } said) copy["content"] = said[..900] + " …(shortened)";
            // an older question with a file attached: the question, not the whole file again
            if (role == "user" && old && copy["content"] is JsonValue && copy["content"]?.ToString() is { Length: > 1500 } asked) copy["content"] = asked[..1500] + " …(shortened)";
            if (tools == null && copy.ContainsKey("tool_calls"))
            {
                copy.Remove("tool_calls");
                if (copy["content"] == null) copy["content"] = "(I took a step on the PC.)";
            }
            list.Add(copy);
        }
        // too long for the model: the oldest exchanges go (a whole exchange at a time, so no step loses its answer)
        static int Size(JsonObject o) => o.ToJsonString().Length;
        int total = list.Sum(Size);
        while (total > 36000) // about 9,000 tokens: quick to answer, and small enough for the fast engine
        {
            int next = list.FindIndex(1, o => o["role"]?.ToString() == "user");
            if (next <= 0) break;
            total -= list.Take(next).Sum(Size);
            list.RemoveRange(0, next);
        }
        while (list.Count > 0 && list[0]["role"]?.ToString() != "user") list.RemoveAt(0); // never start mid-exchange
        var request = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() } };
        foreach (var o in list) request.Add(o);
        return request;
    }

    /// <summary>A copy for the disk: pictures left out (they'd make every kept chat huge), their count kept.</summary>
    static JsonArray ForDisk(JsonArray msgs)
    {
        var copy = msgs.DeepClone().AsArray();
        foreach (var m in copy)
            if (m?["content"] is JsonArray parts)
                m["content"] = (parts.FirstOrDefault(p => p?["type"]?.ToString() == "text")?["text"]?.ToString() ?? "") + " [picture]";
        return copy;
    }

    static bool Retryable(string error) => !error.StartsWith("Today's") && (
        error.StartsWith("Too many") || error.StartsWith("The free model is busy") || error.StartsWith("No answer") || error.StartsWith("The answer stopped") || Regex.IsMatch(error, @"\(5\d\d\)|Provider returned error|overloaded|timed out", RegexOptions.IgnoreCase));

    // ───────────────────────────── the notch tells you ─────────────────────────────

    /// <summary>The answer came while the chat wasn't on screen: the notch says so; a click brings the chat back.</summary>
    string? _lastProblem;                 // why the last answer couldn't finish (set by ChatProblem)
    DateTime _askedAt = DateTime.Now;     // when the last question was sent
    System.Windows.Threading.DispatcherTimer? _noticeLater;

    /// <summary>
    /// The AI finished while you were somewhere else (another app, the notch closed, the chat window behind): the notch
    /// says so, with a sound: done, with the answer's first line, or couldn't finish, and why. A click brings the
    /// chat back. If the notch can't speak right now (a game, a presentation, a meeting), it says it as soon as it can.
    /// </summary>
    void AnswerReady(string answer, bool acted)
    {
        if (ChatInFront()) { _lastProblem = null; return; }
        bool failed = answer.Length == 0 && _lastProblem != null;
        if (answer.Length == 0 && !acted && !failed) return;
        string line = failed ? _lastProblem! : Regex.Replace(answer.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "Done", @"[*`#>_\[\]]", "").Trim();
        if (line.Length > 70) line = line[..69] + "…";
        _lastProblem = null;
        var took = DateTime.Now - _askedAt;
        string right = took.TotalSeconds < 60 ? $"{took.TotalSeconds:0}s" : $"{(int)took.TotalMinutes}m";
        string title = failed ? (_cyber ? "AI // COULDN'T FINISH" : "AI · couldn't finish") : acted ? (_cyber ? "AI // TASK DONE" : "AI · task done") : (_cyber ? "AI // ANSWER READY" : "AI · answer ready");
        void Tell()
        {
            ShowOsd("ai", failed ? "\uE7BA" : "\uE73E", null, title, line, right, glyphBrush: Frozen(failed ? Warn : Ok), seconds: 8, force: true, prio: Prio.High, onClick: ReopenChat);
            try { _notificationSound.Play(); } catch { }
            if (!failed) PalReact(Controls.Companion.Act.Pump, 2.4);
        }
        // in a game, presenting, or in a meeting: said as soon as that's over (for up to 20 minutes)
        _noticeLater?.Stop();
        if (InGame || _presenter || SilencedByMeeting("ai", force: false))
        {
            var until = DateTime.Now.AddMinutes(20);
            _noticeLater = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _noticeLater.Tick += (_, _) =>
            {
                if (DateTime.Now > until || ChatInFront()) { _noticeLater?.Stop(); return; }
                if (InGame || _presenter || SilencedByMeeting("ai", force: false)) return;
                _noticeLater?.Stop();
                Tell();
            };
            _noticeLater.Start();
            return;
        }
        Tell();
    }

    /// <summary>Is the chat actually in front of you (not just open behind another app)?</summary>
    bool ChatInFront()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (_pop is { IsVisible: true, WindowState: not WindowState.Minimized } && new System.Windows.Interop.WindowInteropHelper(_pop).Handle == fg) return true;
            return _launcherOpen && _chatMode && fg == _hwnd;
        }
        catch { return false; }
    }

    void ReopenChat()
    {
        if (_pop != null)
        {
            if (_pop.WindowState == WindowState.Minimized) _pop.WindowState = WindowState.Normal;
            _pop.Activate();
            return;
        }
        OpenLauncher(null);
        EnterChat(null, resume: true);
    }

    /// <summary>The notch opens on one of its pages (the chat makes way).</summary>
    string OpenNotchPage(string page)
    {
        int index = page.Trim().ToLowerInvariant() switch
        {
            "home" or "music" or "media" => 0, "system" or "stats" or "vitals" => 1, "tools" => 2, "clipboard" or "files" or "clipboard & files" => 3,
            "colors" or "colours" => 4, "settings" => 5, "calls" => 6, _ => -1,
        };
        if (index < 0) return "Error: no page called " + page + " (home, system, tools, clipboard, colors, settings, calls).";
        if (_pop == null) CloseLauncherTo(index); else After(50, () => ExpandTo(index));
        return "Opened the notch on " + page + ".";
    }

    // ───────────────────────────── pictures and videos in answers ─────────────────────────────

    static readonly Regex YouTube = new(@"(?:youtube\.com/watch\?v=|youtu\.be/|youtube\.com/shorts/)([\w-]{11})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A picture from the web (a markdown image), or a video's card (YouTube): loaded once, then kept.</summary>
    Block? MediaBlock(string url, string? caption, bool video)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        string src = url;
        if (video && YouTube.Match(url) is { Success: true } yt) src = $"https://img.youtube.com/vi/{yt.Groups[1].Value}/hqdefault.jpg";
        if (!_mediaCache.TryGetValue(src, out var img))
        {
            try
            {
                img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(src);
                img.DecodePixelWidth = 640;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                _mediaCache[src] = img;
                if (_mediaCache.Count > 40) _mediaCache.Remove(_mediaCache.Keys.First());
            }
            catch { return null; }
        }
        var acc = ChatAccent;
        var picture = new Image { Source = img, MaxHeight = video ? 150 : 200, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var grid = new Grid();
        grid.Children.Add(picture);
        if (video)
            grid.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = Frozen(Color.FromArgb(0xC0, 0x00, 0x00, 0x00)), BorderBrush = Frozen(A(acc, 0xC0)), BorderThickness = new Thickness(1.5),
                Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) },
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
        var stack = new StackPanel();
        stack.Children.Add(new Border { Child = grid, CornerRadius = new CornerRadius(_cyber ? 0 : 8), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, BorderBrush = Frozen(A(acc, 0x50)), BorderThickness = new Thickness(1) });
        if (caption is { Length: > 0 }) stack.Children.Add(new TextBlock { Text = (video ? "▶  " : "") + caption, FontSize = 10.5, Foreground = DimBrush, Margin = new Thickness(1, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        var host = new Border { Child = stack, Cursor = Cursors.Hand, ToolTip = url, Background = Brushes.Transparent };
        host.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenLink(url); };
        return new BlockUIContainer(host) { Margin = new Thickness(0, 3, 0, 7) };
    }

    // ───────────────────────────── how risky a command is ─────────────────────────────

    enum Risk { Read, Change, Danger }

    /// <summary>
    /// Read: looks only (runs at once). Change: creates, copies, opens, moves things in your own files (runs, shown as a step).
    /// Danger: deletes, kills, installs, downloads and runs, touches the registry, security, disks or power (waits for your OK).
    /// Harmless plumbing like 2>&1, > $null or | Out-Null isn't mistaken for writing a file.
    /// </summary>
    static (Risk Level, string Kinds) RiskOf(string script)
    {
        string s = Regex.Replace(script, @"\d?>&\d|\*>&\d|[12*]?>\s*\$null|\|\s*Out-(?:Null|String|Host)\b", " ", RegexOptions.IgnoreCase);
        // harmless names that look like the dangerous ones: clearing the screen, a variable, a job of its own
        s = Regex.Replace(s, @"\b(Clear-Host|Clear-Variable|Remove-Variable|Remove-Job|Remove-PSDrive|Remove-TypeData|Disable-PSBreakpoint|Enable-PSBreakpoint)\b", " ", RegexOptions.IgnoreCase);
        // what can't be read here (a command inside cmd or another PowerShell, encoded, run as admin, the registry) always asks
        // a command whose name is built while it runs, or a shell / script host started indirectly: what it does can't be
        // read here, so it always asks (a page the AI read could try to slip one in)
        var indirect = Regex.Matches(s, @"&\s*[\$\(\[]|\.Invoke(?:Script|Command)?\s*\(|\bInvoke-(?:Command|Item)\b|\[scriptblock\]::Create|\bNew-Object\s+-ComObject\b|\bStart-Process\b[^\n;|]*\b(?:cmd|powershell|pwsh|wscript|cscript|mshta|rundll32|regsvr32|msiexec|bash|wsl)\b|\bStart-Process\b[^\n;|]*\.(?:bat|cmd|ps1|vbs|js|hta|msi|scr)\b|\bStart-Process\b[^\n;|]*-Verb\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (indirect.Count > 0) return (Risk.Danger, "indirect");
        var hidden = Regex.Matches(s, @"\bcmd(?:\.exe)?\s+/[ck]\b|\b(?:powershell|pwsh)(?:\.exe)?\b[^\n;|]*\s-(?:e|ec|enc|encodedcommand|c|command|f|file)\b|-Verb\s+'?""?RunAs|\b(?:New-Item|Copy-Item|Move-Item|Rename-Item)\b(?=[^\n;|]*(?:\bHK(?:LM|CU|CR|U|CC):|Registry::))|\b(?:Set-Item|Set-Date|Set-TimeZone|Rename-Computer|Add-Type|Start-Job|Add-LocalGroupMember|New-LocalUser|Set-LocalUser|Add-AppxPackage|Remove-AppxPackage)\b|(?:^|[;|&({]|\n)\s*&?\s*(?:kill|spps|spsv|ri|rni|certutil|bitsadmin|mshta|rundll32|regsvr32|cscript|wscript)(?=[\s.]|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (hidden.Count > 0) return (Risk.Danger, string.Join(",", hidden.Select(m => Regex.Replace(m.Value, @"^[\s;|&({]+", "").Trim().ToLowerInvariant()).Distinct()));
        var danger = Regex.Matches(s,
            @"\b(Remove-\w+|Stop-Process|Stop-Service|Stop-Computer|Restart-Computer|Restart-Service|Set-Service|Uninstall-\w+|Install-\w+|Clear-\w+|Format-Volume|Initialize-Disk|Set-Partition|Disable-\w+|Enable-\w+|Set-MpPreference|Add-MpPreference|Set-ExecutionPolicy|Set-ItemProperty|New-ItemProperty|Remove-ItemProperty|Set-Acl|Register-ScheduledTask|Unregister-ScheduledTask|Invoke-Expression|Invoke-WebRequest|Invoke-RestMethod|Start-BitsTransfer|Set-NetFirewall\w*|New-NetFirewall\w*|Reset-\w+|Suspend-\w+|Dismount-\w+|Mount-\w+)\b|"
            // Windows' own tools count only where they're run as a command (start of a line or after ; | & ( ), not
            // inside a word: "Format-Table" lays out text, it formats no drive
            + @"(?:^|[;|&({]|\n)\s*&?\s*(del|erase|rd|rmdir|rm|reg|regedit|sc|net|netsh|shutdown|logoff|bcdedit|diskpart|format|takeown|icacls|cacls|winget|choco|scoop|msiexec|wmic|schtasks|taskkill|curl|wget|iwr|irm|iex|powercfg|cipher|vssadmin|wevtutil|sfc|dism|attrib)(?=[\s.]|$)|(?:\.|::)(Delete|Kill|Encrypt|Decrypt)\w*\(",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (danger.Count > 0) return (Risk.Danger, string.Join(",", danger.Select(m => Regex.Replace(m.Value, @"^[\s;|&({]+", "").Trim().ToLowerInvariant()).Distinct()));
        if (Regex.IsMatch(s, @"\b(New-Item|Set-Content|Add-Content|Out-File|Copy-Item|Move-Item|Rename-Item|Start-Process|Expand-Archive|Compress-Archive|Export-\w+|Set-Clipboard|Set-Location)\b|(?:^|[;|&({]|\n)\s*(mkdir|md|copy|xcopy|robocopy|move|ren|start)(?=\s|$)|[^-\d]>\s*[^&\s$]|(?:\.|::)(Move|Copy|Create|Write|Replace|Set|Start)\w*\(", RegexOptions.IgnoreCase | RegexOptions.Multiline))
            return (Risk.Change, "");
        return (Risk.Read, "");
    }

    /// <summary>Should this run without asking? (What you allowed "for this chat" isn't asked again.)</summary>
    bool NeedsOk(string script, out string kinds)
    {
        var (level, k) = RiskOf(script);
        kinds = k;
        if (_settings.AiAsk == "never" || !_settings.AiConfirm) return false;
        if (level == Risk.Read) return false;
        if (level == Risk.Change) return _settings.AiAsk == "changes";
        if (_session != null && _trusted.TryGetValue(_session.Id, out var ok) && k.Split(',').All(ok.Contains)) return false;
        return true;
    }

    void Trust(string kinds)
    {
        if (_session == null || kinds.Length == 0) return;
        if (!_trusted.TryGetValue(_session.Id, out var set)) _trusted[_session.Id] = set = new HashSet<string>();
        foreach (var k in kinds.Split(',')) set.Add(k);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
}
