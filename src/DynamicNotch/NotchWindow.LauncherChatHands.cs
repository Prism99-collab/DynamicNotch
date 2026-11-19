using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The AI's hands in any app: it looks at a window the way a screen reader does (Windows UI Automation: buttons, boxes,
/// lists, tabs, links, with their names), clicks, types and presses shortcuts, and can take a picture of the window to
/// check with its own eyes that it worked. Works in normal apps, browsers and web-based apps (Discord, Teams, Spotify,
/// WhatsApp...). Keys and clicks only ever go to that app: if it can't be brought to the front, nothing is typed.
/// Buttons that delete, pay, leave or sign out wait for your OK.
/// </summary>
public partial class NotchWindow
{
    IntPtr _uiHwnd;
    List<(AutomationElement El, string Kind, string Name)> _uiSeen = new();
    byte[]? _pendingSight;

    static readonly Regex RiskyButton = new(@"\b(delete|remove|uninstall|erase|wipe|format|reset|unfriend|block|ban|kick|leave|unsubscribe|pay|purchase|buy|checkout|order now|place order|send money|transfer|sign out|log ?out|deactivate|discard|empty (the )?(trash|bin))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    async Task<string> UseApp(string app, string action, string target, string text, CancellationToken cancel)
    {
        action = action.Trim().ToLowerInvariant();
        var hwnd = FindUiWindow(app);
        if (hwnd == IntPtr.Zero)
            return app.Trim().Length > 0 ? $"Error: {app} has no open window. Open it first (open), wait a moment, then look." : "Error: say which app.";
        if (hwnd != _uiHwnd) _uiSeen.Clear();
        _uiHwnd = hwnd;
        string title = WindowTitle(hwnd);
        // a chat app: nothing is written (or sent with Enter) unless the user asked to send something
        bool chat = ChatApps.IsMatch(app + " " + title);
        if (chat && !UserAskedToSend && (action is "type" or "write" || action is "keys" or "key" or "shortcut" && Regex.IsMatch(text + target, @"enter|return", RegexOptions.IgnoreCase)))
            return "Error: the user didn't ask to send a message, so nothing is typed in a chat app. Don't write to people unless asked.";
        if (chat && action is "type" or "write" && UserAskedToSend)
            return "Error: to send a message use send_message (it opens the chat, types in the message box, sends, and checks it arrived).";

        switch (action)
        {
            case "look":
            case "read":
                _actUnchecked = false;
                return await Limited(() => LookAt(hwnd, title), "Error: the app isn't responding (its window couldn't be read). Try 'see', or wait a moment.");

            case "see":
            {
                if (!await Front(hwnd)) return $"Error: couldn't bring {title} to the front to see it.";
                await Task.Delay(150, cancel);
                var jpeg = await Limited(() => Snap(hwnd), (byte[]?)null);
                if (jpeg == null) return "Error: couldn't take a picture of the window.";
                _pendingSight = jpeg;
                _actUnchecked = false;
                return $"A picture of {title} comes right after this, as an image: look at it to check.";
            }

            case "click":
            case "press":
            case "open":
            case "select":
            {
                var (el, kind, name) = await Limited(() => FindElement(hwnd, target), ((AutomationElement?)null, "", ""));
                if (el == null) return $"Error: nothing called “{target}” in {title}. Look again: it may have another name, or be further down (scroll).";
                if (RiskyButton.IsMatch(name) && _settings.AiAsk != "never" && await ConfirmInChat($"Press “{name}” in {AppOf(title)}", title, cancel) == 0)
                    return "The user declined: it wasn't pressed.";
                if (!await Front(hwnd)) return $"Error: couldn't bring {title} to the front.";
                string how = await Limited(() => Press(el, kind), "Error: the app didn't respond to the press.");
                await Task.Delay(450, cancel); // the app reacts
                if (!how.StartsWith("Error")) _actUnchecked = true;
                return how.StartsWith("Error") ? how : $"{how} {kind} “{name}”. Not checked yet: look or see before saying it worked.";
            }

            case "type":
            case "write":
            {
                if (text.Length == 0) return "Error: what should be typed?";
                if (!await Front(hwnd)) return $"Error: couldn't bring {title} to the front, so nothing was typed.";
                if (target.Trim().Length > 0)
                {
                    var (el, kind, _) = await Limited(() => FindElement(hwnd, target), ((AutomationElement?)null, "", ""));
                    if (el == null) return $"Error: no box called “{target}”. Look again.";
                    await Limited(() => { try { el.SetFocus(); } catch { Press(el, kind); } return true; }, false);
                    await Task.Delay(120, cancel);
                }
                if (!StillFront(hwnd)) return "Error: the app lost the front, so nothing was typed.";
                TypeText(text);
                await Task.Delay(200, cancel);
                _actUnchecked = true;
                return $"Typed “{(text.Length > 60 ? text[..60] + "…" : text)}”. It isn't sent: press enter (keys) if that's what the user wants.";
            }

            case "keys":
            case "key":
            case "shortcut":
            {
                string combo = (text.Length > 0 ? text : target).Trim();
                if (combo.Length == 0) return "Error: which keys?";
                if (Regex.IsMatch(combo, @"\b(win|windows)\s*\+\s*l\b|ctrl\s*\+\s*alt\s*\+\s*(del|delete)", RegexOptions.IgnoreCase)) return "Error: that shortcut isn't allowed.";
                if (!await Front(hwnd)) return $"Error: couldn't bring {title} to the front, so no keys were pressed.";
                foreach (var one in combo.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!StillFront(hwnd)) return "Error: the app lost the front; stopped.";
                    if (!PressCombo(one)) return $"Error: don't know the key “{one}”.";
                    await Task.Delay(260, cancel);
                }
                _actUnchecked = true;
                return $"Pressed {combo} in {AppOf(title)}. Not checked yet: look or see before saying it worked.";
            }

            case "scroll":
            {
                if (!await Front(hwnd)) return $"Error: couldn't bring {title} to the front.";
                bool up = Regex.IsMatch(text + " " + target, @"\bup\b", RegexOptions.IgnoreCase);
                GetWindowRect(hwnd, out var r);
                Wheel((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, up ? 5 : -5);
                await Task.Delay(300, cancel);
                _uiSeen.Clear();
                return $"Scrolled {(up ? "up" : "down")}. Look again for what's there now.";
            }

            case "focus":
                return await Front(hwnd) ? $"{title} is in front." : $"Error: couldn't bring {title} to the front.";
        }
        return "Error: action must be look, see, click, type, keys, scroll or focus.";
    }

    /// <summary>A UI Automation call with a time limit: an app that isn't responding doesn't hold the AI.</summary>
    static async Task<T> Limited<T>(Func<T> work, T ifHung, int seconds = 8)
    {
        var task = Task.Run(work);
        return await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) == task ? await task : ifHung;
    }

    static string AppOf(string title) => title.Contains(" - ") ? title[(title.LastIndexOf(" - ") + 3)..] : title;

    IntPtr FindUiWindow(string app)
    {
        if (app.Trim().Length == 0) return _uiHwnd != IntPtr.Zero && IsWindow(_uiHwnd) ? _uiHwnd : IntPtr.Zero;
        var wins = RunningWindows(app);
        if (wins.Count == 0 && System.Text.RegularExpressions.Regex.IsMatch(app, @"^(browser|web|navigateur|chrome|google chrome|edge|msedge|brave|firefox|opera)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && DefaultBrowser() is { } own)
            wins = RunningWindows(own);
        if (wins.Count == 0) return IntPtr.Zero;
        // the one you used last, else the biggest (the main window, not a small helper)
        if (wins.Any(w => w.Hwnd == _uiHwnd)) return _uiHwnd;
        return wins.OrderByDescending(w => { GetWindowRect(w.Hwnd, out var r); return (long)(r.Right - r.Left) * (r.Bottom - r.Top); }).First().Hwnd;
    }

    async Task<bool> Front(IntPtr hwnd)
    {
        if (StillFront(hwnd)) return true;
        for (int i = 0; i < 3; i++)
        {
            FocusWindow(hwnd);
            SetForegroundWindow(hwnd);
            await Task.Delay(220 + i * 150);
            if (StillFront(hwnd)) return true;
        }
        return false;
    }

    static bool StillFront(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == hwnd) return true;
        GetWindowThreadProcessId(fg, out uint a);
        GetWindowThreadProcessId(hwnd, out uint b);
        return a == b && a != 0 && a != (uint)Environment.ProcessId;
    }

    // ───────────────────────────── seeing ─────────────────────────────

    static readonly HashSet<string> Interactive = new(StringComparer.OrdinalIgnoreCase)
    { "button", "edit", "combo box", "list item", "menu item", "tab item", "hyperlink", "check box", "radio button", "tree item", "split button", "slider", "data item", "document" };

    static List<(AutomationElement El, string Kind, string Name)> Elements(IntPtr hwnd)
    {
        var list = new List<(AutomationElement, string, string)>();
        var root = AutomationElement.FromHandle(hwnd);
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        var cond = new AndCondition(new PropertyCondition(AutomationElement.IsControlElementProperty, true), new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
        AutomationElementCollection all;
        using (cache.Activate()) all = root.FindAll(TreeScope.Descendants, cond);
        foreach (AutomationElement el in all)
        {
            try
            {
                // the element's type in English whatever Windows' language ("ControlType.ListItem" → "list item"):
                // the localized name would be "bouton", "élément de liste"… on a French Windows and nothing would match
                string kind = Regex.Replace(el.Cached.ControlType.ProgrammaticName.Replace("ControlType.", ""), @"(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
                string name = Regex.Replace(el.Cached.Name ?? "", @"\s+", " ").Trim();
                if (name.Length > 90) name = name[..90] + "…";
                if (el.Cached.BoundingRectangle.IsEmpty || el.Cached.BoundingRectangle.Width < 2) continue;
                if (name.Length == 0 && kind is not ("edit" or "document" or "combo box")) continue;
                list.Add((el, kind, name));
            }
            catch { }
        }
        return list;
    }

    string LookAt(IntPtr hwnd, string title)
    {
        try
        {
            var found = Elements(hwnd);
            // web-based apps build their tree when first asked: ask once more
            if (found.Count < 6) { Thread.Sleep(700); found = Elements(hwnd); }
            var shown = found.Where(f => Interactive.Contains(f.Kind)).Take(150).ToList();
            var labels = found.Where(f => !Interactive.Contains(f.Kind) && f.Name.Length > 1).Select(f => f.Name).Distinct().Take(60).ToList();
            _uiSeen = shown;
            var sb = new StringBuilder();
            sb.AppendLine($"Window: {title}. What's on screen (use the number to click or type). Only what's listed here is readable: if what you need isn't in this list, use 'see' (a picture) and never guess.");
            for (int i = 0; i < shown.Count; i++)
            {
                var (el, kind, name) = shown[i];
                string value = "";
                if (kind is "edit" or "combo box" or "document")
                    try { if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) { string v = ((ValuePattern)vp).Current.Value ?? ""; value = v.Length == 0 ? " (empty)" : $" = “{(v.Length > 50 ? v[..50] + "…" : v)}”"; } } catch { }
                sb.AppendLine($"[{i + 1}] {kind} “{name}”{value}");
            }
            if (labels.Count > 0) sb.AppendLine("Text on screen: " + string.Join(" · ", labels));
            if (shown.Count == 0) sb.AppendLine("(this app shows nothing readable: use 'see' to look at it, and keys to work it)");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return "Error: couldn't read the window (" + ex.Message + "). Use 'see' instead."; }
    }

    /// <summary>By the number from the last look, or by its name (the closest match, buttons first).</summary>
    (AutomationElement? El, string Kind, string Name) FindElement(IntPtr hwnd, string target)
    {
        target = target.Trim().Trim('"', '“', '”', '[', ']');
        if (int.TryParse(target, out int n) && n >= 1 && n <= _uiSeen.Count) return _uiSeen[n - 1];
        if (target.Length == 0) return (null, "", "");
        IEnumerable<(AutomationElement El, string Kind, string Name)> pool = _uiSeen;
        for (int pass = 0; pass < 2; pass++)
        {
            var hit = pool.Where(f => f.Name.Equals(target, StringComparison.OrdinalIgnoreCase)).OrderBy(f => Interactive.Contains(f.Kind) ? 0 : 1).FirstOrDefault();
            if (hit.El == null) hit = pool.Where(f => f.Name.Contains(target, StringComparison.OrdinalIgnoreCase)).OrderBy(f => Interactive.Contains(f.Kind) ? 0 : 1).ThenBy(f => f.Name.Length).FirstOrDefault();
            if (hit.El != null) return hit;
            try { pool = Elements(hwnd); } catch { break; }
        }
        return (null, "", "");
    }

    /// <summary>Pressed the way the app understands best: its own action, else selecting or toggling it, else a real click.</summary>
    static string Press(AutomationElement el, string kind)
    {
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) { ((InvokePattern)inv).Invoke(); return "Pressed"; }
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel)) { ((SelectionItemPattern)sel).Select(); return "Selected"; }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tog)) { ((TogglePattern)tog).Toggle(); return "Toggled"; }
            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var exp)) { ((ExpandCollapsePattern)exp).Expand(); return "Opened"; }
        }
        catch { }
        try
        {
            var r = el.Current.BoundingRectangle;
            if (r.IsEmpty) return "Error: it can't be pressed (it has no place on screen).";
            Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
            return "Clicked";
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    /// <summary>The window as a picture (even a browser or game-engine window), small enough to send.</summary>
    static byte[]? Snap(IntPtr hwnd)
    {
        try
        {
            GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 20 || h < 20) return null;
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                var dc = g.GetHdc();
                bool ok = PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT */);
                g.ReleaseHdc(dc);
                if (!ok) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
            }
            double k = Math.Min(1, 1280.0 / Math.Max(w, h));
            using var small = k < 1 ? new System.Drawing.Bitmap(bmp, (int)(w * k), (int)(h * k)) : new System.Drawing.Bitmap(bmp);
            using var ms = new System.IO.MemoryStream();
            var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            var ps = new System.Drawing.Imaging.EncoderParameters(1) { Param = { [0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 78L) } };
            small.Save(ms, jpeg, ps);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // ───────────────────────────── hands ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)] struct MouseIn { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyIn2 { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] struct InUnion { [FieldOffset(0)] public MouseIn M; [FieldOffset(0)] public KeyIn2 K; }
    [StructLayout(LayoutKind.Sequential)] struct In { public uint Type; public InUnion U; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, In[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] static extern bool CursorAt(out System.Drawing.Point p);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    static In InKey(ushort vk, bool up, bool unicode = false, ushort ch = 0) => new()
    {
        Type = 1,
        U = new InUnion { K = new KeyIn2 { Vk = unicode ? (ushort)0 : vk, Scan = ch, Flags = (unicode ? 0x0004u : 0u) | (up ? 0x0002u : 0u) | (!unicode && vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E ? 0x0001u : 0u) } },
    };

    static void Send(params In[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<In>());

    static void TypeText(string text)
    {
        foreach (char c in text.Replace("\r\n", "\n"))
        {
            if (c == '\n') { Send(InKey(0x10, false), InKey(0x0D, false), InKey(0x0D, true), InKey(0x10, true)); continue; } // a new line, not "send"
            Send(InKey(0, false, true, c), InKey(0, true, true, c));
        }
    }

    static ushort? VkOf(string k) => k.ToLowerInvariant() switch
    {
        "ctrl" or "control" => 0x11, "shift" => 0x10, "alt" => 0x12, "win" or "windows" => 0x5B,
        "enter" or "return" => 0x0D, "esc" or "escape" => 0x1B, "tab" => 0x09, "space" => 0x20, "backspace" => 0x08,
        "delete" or "del" => 0x2E, "insert" => 0x2D, "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
        "home" => 0x24, "end" => 0x23, "pageup" or "pgup" => 0x21, "pagedown" or "pgdn" => 0x22,
        "plus" => 0xBB, "minus" => 0xBD, "comma" => 0xBC, "period" or "dot" => 0xBE, "slash" => 0xBF,
        var f when Regex.IsMatch(f, @"^f([1-9]|1[0-2])$") => (ushort)(0x6F + int.Parse(f[1..])),
        var c when c.Length == 1 && char.IsLetterOrDigit(c[0]) => char.ToUpperInvariant(c[0]),
        _ => null,
    };

    static bool PressCombo(string combo)
    {
        var parts = combo.Replace(" ", "").Split('+', StringSplitOptions.RemoveEmptyEntries);
        var vks = new List<ushort>();
        foreach (var p in parts) { if (VkOf(p) is not { } vk) return false; vks.Add(vk); }
        if (vks.Count == 0) return false;
        var seq = vks.Select(v => InKey(v, false)).Concat(Enumerable.Reverse(vks).Select(v => InKey(v, true))).ToArray();
        Send(seq);
        return true;
    }

    static void Click(int x, int y)
    {
        CursorAt(out var was);
        SetCursorPos(x, y);
        Thread.Sleep(40);
        Send(new In { Type = 0, U = new InUnion { M = new MouseIn { Flags = 0x0002 } } }, new In { Type = 0, U = new InUnion { M = new MouseIn { Flags = 0x0004 } } });
        Thread.Sleep(60);
        SetCursorPos(was.X, was.Y);
    }

    static void Wheel(int x, int y, int notches)
    {
        CursorAt(out var was);
        SetCursorPos(x, y);
        Thread.Sleep(30);
        Send(new In { Type = 0, U = new InUnion { M = new MouseIn { Flags = 0x0800, Data = unchecked((uint)(notches * 120)) } } });
        Thread.Sleep(40);
        SetCursorPos(was.X, was.Y);
    }
}
