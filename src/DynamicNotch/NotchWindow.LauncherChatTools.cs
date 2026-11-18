using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using DynamicNotch.Services;
using Do = DynamicNotch.Services.LauncherIntent.Do;

namespace DynamicNotch;

/// <summary>
/// What the AI chat can do on the PC: the notch's own commands, files (find, read, list, write), opening things,
/// the clipboard, the web (search and read pages) and the PC's state. Each one is offered to the model only when
/// Preferences allow it (acting on the PC, the web).
/// </summary>
public partial class NotchWindow
{
    // ───────────────────────────── the AI's tools ─────────────────────────────

    static JsonArray? _chatTools;

    static readonly string[] WebToolNames = { "web_search", "read_webpage", "research", "news" };
    static readonly string[] MemoryTools = { "remember", "forget" };

    /// <summary>The tools Preferences allow: acting on the PC, the web, both, or none (then it only talks).</summary>
    JsonArray? ChatToolsFor()
    {
        var list = new JsonArray();
        foreach (var t in ChatTools())
        {
            string name = t?["function"]?["name"]?.ToString() ?? "";
            // reading the notch's own data is always allowed; acting needs "Let it act on your PC"
            bool allowed = name == "notch_data" || (WebToolNames.Contains(name) ? _settings.AiWeb : MemoryTools.Contains(name) ? _settings.AiMemory : _settings.AiTools);
            if (allowed) list.Add(t!.DeepClone());
        }
        return list.Count > 0 ? list : null;
    }

    static JsonArray ChatTools() => _chatTools ??= JsonNode.Parse("""
    [
      {"type":"function","function":{"name":"notch_data","description":"Read what the notch itself knows: 'devices' (battery of the wireless mouse, headset, Bluetooth devices and the PC; what sound plays on), 'calls' (each call's app, who/where, when, how long, and a call happening now), 'reminders', 'timer', 'focus', 'media' (what's playing), 'notifications', 'clipboard' (copy history), 'shelf', 'downloads', 'system' (the PC now and its recent heavy moments), or 'today' (a summary of all of it). Use it for any question about the user's calls, reminders, copies, notifications, music or PC health: never tell the user to go and check.","parameters":{"type":"object","properties":{"what":{"type":"string","description":"today, devices, calls, reminders, timer, focus, media, notifications, clipboard, shelf, downloads, system"},"filter":{"type":"string","description":"Optional words to look for (an app, a name, some text)"}},"required":["what"]}}},
      {"type":"function","function":{"name":"do_on_pc","description":"Do something on this PC, said in plain words the way the notch understands it. Examples: 'volume 30', 'mute', 'brightness 60', 'pause music', 'next song', 'wifi off', 'bluetooth on', 'airplane mode on', 'dark mode on', 'focus for 45 minutes', 'timer 10 min', 'remind me to call mum at 18:00', 'cancel reminders', 'open spotify', 'open spotify and discord', 'close discord', 'youtube lofi beats', 'take a screenshot', 'lock', 'empty recycle bin', 'hide desktop icons', 'sleep', 'shut down in 1 hour'. Several can be joined with 'and'.","parameters":{"type":"object","properties":{"command":{"type":"string","description":"The command in plain English"}},"required":["command"]}}},
      {"type":"function","function":{"name":"find_files","description":"Search the files, folders and apps on this PC by name. Understands filters like 'pdf last week', 'photos from yesterday', 'big files', 'invoice 2025'. Returns paths.","parameters":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}}},
      {"type":"function","function":{"name":"read_file","description":"Read a text file or a Word document (.docx) on this PC. Returns its text (long files are cut).","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Full path"}},"required":["path"]}}},
      {"type":"function","function":{"name":"list_folder","description":"List what's in a folder: newest first, with sizes. Accepts a full path or 'downloads', 'desktop', 'documents', 'pictures', 'videos', 'music'.","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}},
      {"type":"function","function":{"name":"write_file","description":"Create a file: .docx (real Word document), .xlsx (real Excel sheet), .pdf, or any text file (.txt, .md, .csv, .html, code...). For .docx and .pdf write the content as markdown (# headings, **bold**, lists, | tables |, code blocks). For .xlsx give a markdown table or CSV rows, first row = headers. A bare name goes to Documents. mode: create (default, never replaces), append (text files), replace (overwrites; the user is asked).","parameters":{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"mode":{"type":"string","enum":["create","append","replace"]}},"required":["path","content"]}}},
      {"type":"function","function":{"name":"create_image","description":"Make a picture from a description (saved in Pictures\\Dynamic Notch and shown in the chat). Set from_attached to edit or restyle the picture the user attached.","parameters":{"type":"object","properties":{"prompt":{"type":"string","description":"A detailed description: subject, style, colours, framing"},"from_attached":{"type":"boolean"}},"required":["prompt"]}}},
      {"type":"function","function":{"name":"undo_last","description":"Take back the last thing you did on the PC that can be undone (a file written, a picture made, volume, apps opened or closed, reminders, settings switched, the clipboard...).","parameters":{"type":"object","properties":{"count":{"type":"integer","description":"How many steps back (default 1)"}}}}},
      {"type":"function","function":{"name":"open","description":"Open a file, a folder or a web address with its usual app or the browser.","parameters":{"type":"object","properties":{"target":{"type":"string","description":"A full path or a URL"}},"required":["target"]}}},
      {"type":"function","function":{"name":"clipboard","description":"Read what's on the clipboard, or put text on it.","parameters":{"type":"object","properties":{"action":{"type":"string","enum":["read","write"]},"text":{"type":"string"}},"required":["action"]}}},
      {"type":"function","function":{"name":"research","description":"Search the web AND read the best pages, in one step: the way to answer any question about facts, current events, prices, people, products, how-tos. Returns numbered sources with their text.","parameters":{"type":"object","properties":{"query":{"type":"string","description":"A precise search query, in the language most likely to find good sources"}},"required":["query"]}}},
      {"type":"function","function":{"name":"news","description":"The latest headlines with their outlet and time: on a subject, or the top stories when the query is empty.","parameters":{"type":"object","properties":{"query":{"type":"string"}}}}},
      {"type":"function","function":{"name":"web_search","description":"A quick web search: titles, links and snippets only (prefer research to actually answer).","parameters":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}}},
      {"type":"function","function":{"name":"read_webpage","description":"Read the text of a web page.","parameters":{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}}},
      {"type":"function","function":{"name":"run_powershell","description":"Run a PowerShell script on this PC and get its output. For anything Windows can do that the other tools don't cover: settings, system info, processes, services, files in bulk, network, apps (winget), registry reads. Prefer one clear script. Risky scripts are shown to the user to approve first.","parameters":{"type":"object","properties":{"script":{"type":"string","description":"PowerShell code"},"why":{"type":"string","description":"In a few words, what it does (shown to the user)"}},"required":["script","why"]}}},
      {"type":"function","function":{"name":"manage_window","description":"Focus, minimise, maximise, restore or close an app's windows.","parameters":{"type":"object","properties":{"app":{"type":"string","description":"The app's name, e.g. 'chrome', 'word', 'discord'"},"action":{"type":"string","enum":["focus","minimize","maximize","restore","close"]}},"required":["app","action"]}}},
      {"type":"function","function":{"name":"notify","description":"Show a short message on the notch (a pop-up at the top of the screen), e.g. when a long task is done or the user asked to be told something.","parameters":{"type":"object","properties":{"title":{"type":"string","description":"A few words"},"text":{"type":"string","description":"One short line"}},"required":["title"]}}},
      {"type":"function","function":{"name":"recent_notifications","description":"The notifications the PC received lately (app, title, text, time), newest first. For 'what did I miss?' or finding a message.","parameters":{"type":"object","properties":{}}}},
      {"type":"function","function":{"name":"browser_history","description":"What the user downloaded or visited in their browsers (Brave, Chrome, Edge, Vivaldi, Opera; every profile). Use it for 'what did I download', 'that site I visited yesterday', 'find the page about X'.","parameters":{"type":"object","properties":{"kind":{"type":"string","enum":["downloads","visits"]},"browser":{"type":"string","description":"brave, chrome, edge, vivaldi, opera, or any"},"search":{"type":"string","description":"Words to look for in titles, addresses or file names (optional)"},"days":{"type":"integer","description":"How far back, in days (default 7)"}},"required":["kind"]}}},
      {"type":"function","function":{"name":"notch_page","description":"Open the notch on one of its pages: home (music), system (CPU, memory, apps), tools, clipboard (clipboard and files), colors, settings, calls.","parameters":{"type":"object","properties":{"page":{"type":"string"}},"required":["page"]}}},
      {"type":"function","function":{"name":"remember","description":"Save a lasting fact about the user (a preference, their work, a place, how they like things done). Not passwords or secrets.","parameters":{"type":"object","properties":{"fact":{"type":"string","description":"One short sentence, e.g. 'Prefers answers in French'"}},"required":["fact"]}}},
      {"type":"function","function":{"name":"forget","description":"Forget saved facts about the user that contain these words.","parameters":{"type":"object","properties":{"about":{"type":"string"}},"required":["about"]}}},
      {"type":"function","function":{"name":"pc_status","description":"What's going on on the PC right now: the time, battery, volume, the song playing, the open windows, memory and free disk space, reminders.","parameters":{"type":"object","properties":{}}}}
    ]
    """)!.AsArray();

    async Task<string> RunTool(string name, string argsJson, CancellationToken cancel = default)
    {
        JsonNode? args;
        try { args = JsonNode.Parse(argsJson.Length > 0 ? argsJson : "{}"); } catch { return "Error: the arguments weren't valid JSON."; }
        string Arg(string k) => args?[k]?.ToString() ?? "";
        switch (name)
        {
            case "do_on_pc": return await ToolDo(Arg("command"));
            case "find_files": return await ToolFind(Arg("query"));
            case "read_file": return await Task.Run(() => ReadAnyText(ExpandPath(Arg("path"))));
            case "list_folder":
            {
                string dir = ExpandPath(Arg("path"));
                string listing = await Task.Run(() => ListFolder(dir));
                if (Directory.Exists(dir))
                    try
                    {
                        Found(new DirectoryInfo(dir).EnumerateFileSystemInfos().Where(i => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                            .OrderByDescending(i => i.LastWriteTime).Take(6)
                            .Select(i => new ResultCard(i is DirectoryInfo ? "folder" : "file", i.Name, i is FileInfo f ? $"{SystemMonitor.Bytes(f.Length)} · {f.LastWriteTime:d MMM HH:mm}" : $"Folder · {i.LastWriteTime:d MMM}", i.FullName)));
                    }
                    catch { }
                return listing;
            }
            case "write_file": return await WriteFileTool(Arg("path"), Arg("content"), Arg("mode"), cancel);
            case "create_image": return await MakePictureTool(Arg("prompt"), Arg("from_attached").Equals("true", StringComparison.OrdinalIgnoreCase), cancel);
            case "undo_last":
            {
                int n = Math.Clamp(args?["count"] is JsonValue cv && cv.TryGetValue(out int c) ? c : 1, 1, 10);
                var said = new List<string>();
                for (int i = 0; i < n; i++) { string r = UndoLast(); said.Add(r); if (r == "Nothing to undo.") break; }
                return string.Join("\n", said);
            }
            case "open":
            {
                string t = Arg("target").Trim();
                if (t.Length == 0) return "Error: nothing to open.";
                bool url = Regex.IsMatch(t, @"^(https?|mailto):", RegexOptions.IgnoreCase);
                string path = url ? t : ExpandPath(t);
                if (!url && !File.Exists(path) && !Directory.Exists(path)) return "Error: not found: " + path;
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return "Opened " + path; }
                catch (Exception ex) { return "Error: " + ex.Message; }
            }
            case "clipboard":
                if (Arg("action") == "write")
                {
                    try
                    {
                        string? before = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                        Clipboard.SetText(Arg("text"));
                        if (before != null) Undoable("the clipboard", () => { Clipboard.SetText(before); return "The clipboard is back as it was"; });
                        return "Copied to the clipboard.";
                    }
                    catch (Exception ex) { return "Error: " + ex.Message; }
                }
                try { return Clipboard.ContainsText() ? Clipboard.GetText() : "The clipboard has no text."; } catch (Exception ex) { return "Error: " + ex.Message; }
            case "web_search":
            {
                var hits = await WebTools.Search(Arg("query"), 8, cancel);
                Found(WebCards(hits.Take(5)));
                return hits.Count == 0 ? "No results. Try other words, or research." : string.Join("\n\n", hits.Select((h, i) => $"[{i + 1}] {h.Title}\n{h.Url}\n{h.Snippet}"));
            }
            case "research":
            {
                var (hits, report) = await WebTools.Research(Arg("query"), cancel);
                Found(WebCards(hits.Take(5)));
                return report;
            }
            case "news":
            {
                var hits = await WebTools.News(Arg("query"), 12, cancel);
                Found(WebCards(hits.Take(6), "news"));
                return hits.Count == 0 ? "No news found." : string.Join("\n", hits.Select((h, i) => $"[{i + 1}] {h.Title} — {h.Snippet}"));
            }
            case "read_webpage":
            {
                string url = Arg("url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return "Error: not a web address.";
                try
                {
                    var (title, text) = await WebTools.Read(url, 14000, cancel);
                    Found(new[] { new ResultCard("web", title.Length > 0 ? title : WebTools.Domain(url), WebTools.Domain(url), url) });
                    return (title.Length > 0 ? "# " + title + "\n" : "") + text;
                }
                catch (Exception ex) { return "Error: " + ex.Message; }
            }
            case "pc_status": return PcStatus();
            case "notch_data": return NotchData(Arg("what"), Arg("filter"));
            case "run_powershell": return await RunPowerShell(Arg("script"), Arg("why"), cancel);
            case "manage_window": return ManageWindow(Arg("app"), Arg("action"));
            case "notify":
            {
                string title = Arg("title").Trim(), text = Arg("text").Trim();
                if (title.Length == 0) return "Error: a title is needed.";
                ShowOsd("ai", "\uE8BD", null, title.Length > 40 ? title[..40] : title, text.Length > 80 ? text[..80] : text, null, glyphBrush: Frozen(ChatAccent), seconds: 4, force: true, prio: Prio.Normal);
                return "Shown on the notch.";
            }
            case "recent_notifications": return RecentToastsText();
            case "browser_history":
            {
                int days = args?["days"] is JsonValue dv && dv.TryGetValue(out int d) ? d : 7;
                string kind = Arg("kind") == "visits" ? "visits" : "downloads", browser = Arg("browser"), search = Arg("search");
                return await Task.Run(() => BrowserHistory.Query(browser, kind, search, days));
            }
            case "notch_page": return OpenNotchPage(Arg("page"));
            case "remember":
            {
                string kept = AiMemory.Remember(Arg("fact"));
                string fact = kept.StartsWith("Remembered: ") ? kept[12..] : "";
                if (fact.Length > 0) Undoable("what it remembered", () => { AiMemory.Remove(fact); return "Forgot it again"; });
                return kept;
            }
            case "forget": return AiMemory.Forget(Arg("about"));
        }
        return "Error: there's no tool called " + name;
    }

    /// <summary>The notch's own commands, the same ones the search understands, done at once.</summary>
    async Task<string> ToolDo(string command)
    {
        var list = LauncherIntent.Parse(command, DateTime.Now);
        if (list.Count == 0) return "Not understood as a command. Use the plain forms from the tool's examples, one step at a time.";
        bool asked = Regex.IsMatch(_chatLastUser, @"\b(shut ?down|turn off|power off|restart|reboot|sleep|hibernate|log ?off|sign out)\b", RegexOptions.IgnoreCase);
        var said = new List<string>();
        var undos = new List<Func<string>>();
        foreach (var t in list)
        {
            if (t.Kind is Do.Power or Do.Sleep && !asked) { said.Add("Skipped (the user didn't ask to " + (t.Kind == Do.Sleep ? "sleep" : "power off") + ")"); continue; }
            if (t.Kind == Do.Reminders)
            {
                said.Add(_reminders.Count == 0 ? "No reminders" : string.Join("; ", _reminders.Select(r => $"{r.Text} at {r.At:ddd d MMM HH:mm}")));
                continue;
            }
            var v = Describe(t);
            if (v.Done) { said.Add(v.Title + ": already so (" + v.Detail + ")"); continue; }
            if (!v.Ready) { said.Add(v.Title + ": couldn't (" + v.Detail + ")"); continue; }
            var undo = UndoFor(t, _reminders.ToList());
            string? r = await Execute(t);
            if (undo != null) undos.Add(undo);
            if (t.Kind == Do.Volume) RefreshVolume();
            said.Add("Done: " + (r is { Length: > 0 } ? r : v.Title));
        }
        if (undos.Count > 0) Undoable(command, () => string.Join(" · ", Enumerable.Reverse(undos).Select(u => u())));
        return string.Join("\n", said);
    }

    async Task<string> ToolFind(string query)
    {
        if (query.Trim().Length == 0) return "Error: empty query.";
        EnsureFileIndex();
        var plan = await Task.Run(() => ComputePlan(query, -1));
        var entries = plan.Entries.Select(x => (x.E, x.S)).Concat(plan.Files.Select(x => (x.E, x.S)))
            .OrderByDescending(x => x.S).Select(x => x.E).DistinctBy(e => e.Target, StringComparer.OrdinalIgnoreCase).Take(15).ToList();
        Found(entries.Where(e => e.Kind is LauncherIndex.Kind.File or LauncherIndex.Kind.Folder or LauncherIndex.Kind.App && !e.Target.StartsWith("shell:") || e.Kind == LauncherIndex.Kind.App)
            .Take(6).Select(e => new ResultCard(e.Kind == LauncherIndex.Kind.Folder ? "folder" : "file", e.Name, e.Kind == LauncherIndex.Kind.App ? "App" : ParentName(e.Target), e.Target)));
        var lines = entries
            .Select(e =>
            {
                string kind = e.Kind switch { LauncherIndex.Kind.App => "app", LauncherIndex.Kind.Folder => "folder", LauncherIndex.Kind.Setting => "setting", _ => "file" };
                string when = "";
                if (e.Kind is LauncherIndex.Kind.File or LauncherIndex.Kind.Folder && !e.Target.StartsWith("shell:"))
                {
                    var f = FileFacts(e.Target, e.Kind == LauncherIndex.Kind.Folder);
                    if (f.When > DateTime.MinValue.AddYears(1)) when = $" · changed {f.When:yyyy-MM-dd HH:mm}";
                    if (f.Size >= 0 && e.Kind == LauncherIndex.Kind.File) when += " · " + SystemMonitor.Bytes(f.Size);
                }
                return $"[{kind}] {e.Name} — {e.Target}{when}";
            }).ToList();
        if (!_fileIndex.Ready) lines.Add("(The notch is still reading the drives: more files may appear later.)");
        return lines.Count == 0 ? "Nothing found." : string.Join("\n", lines);
    }

    /// <summary>
    /// A PowerShell script, run hidden with a time limit; its output (and errors) come back. One that changes things
    /// (deletes, installs, settings, downloads, starting programs...) waits for your OK first, unless Preferences say not to.
    /// </summary>
    async Task<string> RunPowerShell(string script, string why, CancellationToken cancel)
    {
        if (script.Trim().Length == 0) return "Error: empty script.";
        if (NeedsOk(script, out string kinds))
        {
            int ok = await ConfirmInChat(why.Trim().Length > 0 ? why.Trim() : "Run a command", script, cancel);
            if (ok == 0) return "The user declined: it was not run. Don't retry it another way; say what you'd need instead.";
            if (ok == 2) Trust(kinds);
        }
        if (cancel.IsCancellationRequested) return "Cancelled by the user.";
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -")
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi)!;
                p.StandardInput.WriteLine("[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'");
                p.StandardInput.WriteLine(script);
                p.StandardInput.Close();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                bool done = p.WaitForExit(45000);
                if (!done) { try { p.Kill(entireProcessTree: true); } catch { } }
                string output = outTask.Wait(2000) ? outTask.Result : "";
                string errors = errTask.Wait(2000) ? errTask.Result : "";
                var sb = new StringBuilder();
                if (!done) sb.AppendLine("(stopped after 45 s)");
                sb.AppendLine("Exit code: " + (done ? p.ExitCode.ToString() : "-"));
                if (output.Trim().Length > 0) sb.AppendLine(Cut(output.Trim(), 8000));
                if (errors.Trim().Length > 0) sb.AppendLine("Errors:\n" + Cut(errors.Trim(), 3000));
                if (output.Trim().Length == 0 && errors.Trim().Length == 0) sb.AppendLine("(no output)");
                return (errors.Trim().Length > 0 && output.Trim().Length == 0 ? "Error: " : "") + sb.ToString().TrimEnd();
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        });
    }

    string ManageWindow(string app, string action)
    {
        if (app.Trim().Length == 0) return "Error: which app?";
        var wins = RunningWindows(app);
        if (wins.Count == 0) return $"No open window for “{app}”.";
        foreach (var w in wins)
        {
            switch (action)
            {
                case "focus": FocusWindow(w.Hwnd); break;
                case "minimize": Interop.Native.ShowWindow(w.Hwnd, 6); break;
                case "maximize": Interop.Native.ShowWindow(w.Hwnd, 3); break;
                case "restore": Interop.Native.ShowWindow(w.Hwnd, 9); break;
                case "close": PostMessage(w.Hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); break;
                default: return "Error: unknown action " + action;
            }
            if (action == "focus") break;
        }
        if (action is "minimize" or "maximize")
        {
            var hwnds = wins.Select(w => w.Hwnd).ToList();
            Undoable("the window", () => { foreach (var h in hwnds) Interop.Native.ShowWindow(h, 9); return "Window restored"; });
        }
        return $"Done: {action} {wins[0].Name} ({wins.Count} window{(wins.Count == 1 ? "" : "s")})";
    }

    static string ExpandPath(string p)
    {
        p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
        string? special = p.ToLowerInvariant() switch
        {
            "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            _ => null,
        };
        if (special != null) return special;
        if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
        return p;
    }

    static string ReadAnyText(string path)
    {
        if (!File.Exists(path)) return "Error: no such file: " + path;
        try
        {
            if (path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                var doc = zip.GetEntry("word/document.xml");
                if (doc == null) return "Error: not a readable Word document.";
                using var r = new StreamReader(doc.Open());
                string xml = r.ReadToEnd();
                xml = Regex.Replace(xml, @"</w:p>", "\n");
                xml = Regex.Replace(xml, @"<w:tab/>", "\t");
                string txt = System.Net.WebUtility.HtmlDecode(Regex.Replace(xml, @"<[^>]+>", ""));
                return Cut(txt, 30000);
            }
            using var fs = File.OpenRead(path);
            var head = new byte[Math.Min(4096, fs.Length)];
            int n = fs.Read(head, 0, head.Length);
            if (head.Take(n).Contains((byte)0) && !(n > 1 && (head[0] == 0xFF && head[1] == 0xFE || head[0] == 0xFE && head[1] == 0xFF)))
                return "This is not a text file (" + Path.GetExtension(path) + ", " + SystemMonitor.Bytes(fs.Length) + "): it can't be read as text.";
            fs.Position = 0;
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buf = new char[30000];
            int got = reader.ReadBlock(buf, 0, buf.Length);
            string text = new(buf, 0, got);
            return reader.EndOfStream ? text : text + "\n…(the file goes on; only the start was read)";
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    static string Cut(string s, int max) => s.Length > max ? s[..max] + "\n…(cut)" : s;

    static string ListFolder(string path)
    {
        if (!Directory.Exists(path)) return "Error: no such folder: " + path;
        try
        {
            var dir = new DirectoryInfo(path);
            var items = dir.EnumerateFileSystemInfos().Where(i => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderByDescending(i => i.LastWriteTime).Take(60)
                .Select(i => i is DirectoryInfo ? $"[folder] {i.Name} · {i.LastWriteTime:yyyy-MM-dd}" : $"{i.Name} · {SystemMonitor.Bytes(((FileInfo)i).Length)} · {i.LastWriteTime:yyyy-MM-dd HH:mm}")
                .ToList();
            return items.Count == 0 ? "The folder is empty." : path + "\n" + string.Join("\n", items);
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    static string Plain(string html) => System.Net.WebUtility.HtmlDecode(Regex.Replace(html, @"<[^>]+>", "")).Trim();

    string PcStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Time: {DateTime.Now:dddd d MMMM yyyy HH:mm}");
        if (_power.HasBattery) sb.AppendLine($"Battery: {_power.Percent}%{(_power.PluggedIn ? ", plugged in" : ", on battery")}{(_power.Saver ? ", saver on" : "")}");
        else sb.AppendLine("Power: mains (no battery)");
        sb.AppendLine($"Volume: {(int)Math.Round(_audio.Volume * 100)}%{(_audio.Muted ? " (muted)" : "")} on {_audio.OutputName}");
        if (_media.Title.Length > 0) sb.AppendLine($"Music: {_media.Title}{(_media.Artist.Length > 0 ? " — " + _media.Artist : "")} ({(_media.IsPlaying ? "playing" : "paused")})");
        var mem = new MemStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemStatus>() };
        if (GlobalMemoryStatusEx(ref mem)) sb.AppendLine($"Memory: {mem.Load}% used of {mem.Total / 1073741824.0:0.#} GB");
        try
        {
            var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            sb.AppendLine($"Disk {sys.Name.TrimEnd('\\')}: {SystemMonitor.Bytes(sys.AvailableFreeSpace)} free of {SystemMonitor.Bytes(sys.TotalSize)}");
        }
        catch { }
        if (_openWins.Count > 0) sb.AppendLine("Open windows: " + string.Join("; ", _openWins.Take(20).Select(w => $"{w.App}: {w.Title}")));
        if (_reminders.Count > 0) sb.AppendLine("Reminders: " + string.Join("; ", _reminders.Select(r => $"{r.Text} at {r.At:ddd HH:mm}")));
        return sb.ToString().TrimEnd();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MemStatus { public uint Length, Load; public ulong Total, Avail, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemStatus m);

    static string ToolGlyph(string name) => name switch
    {
        "browser_history" => "\uE774", "notch_page" => "\uE7F4", "notch_data" => "\uE81C", "create_image" => "\uEB9F", "undo_last" => "\uE7A7",
        "run_powershell" => "\uE756", "manage_window" => "\uE737", "notify" => "\uEA8F", "recent_notifications" => "\uEA8F", "remember" => "\uE8F1", "forget" => "\uE8F1",
        "do_on_pc" => "\uE945", "find_files" => "", "read_file" => "", "list_folder" => "", "write_file" => "",
        "open" => "\uE8A7", "clipboard" => "\uE77F", "web_search" => "\uE774", "read_webpage" => "\uE774", "research" => "\uE721", "news" => "\uE789", _ => "",
    };

    static string ToolLabel(string name, string argsJson)
    {
        string a = "";
        try
        {
            var n = JsonNode.Parse(argsJson.Length > 0 ? argsJson : "{}");
            a = (n?["command"] ?? n?["what"] ?? n?["page"] ?? n?["query"] ?? n?["path"] ?? n?["target"] ?? n?["url"] ?? n?["fact"] ?? n?["about"] ?? n?["app"] ?? n?["action"])?.ToString() ?? "";
            if (n?["app"] != null && n?["action"] != null) a = $"{n["action"]} {n["app"]}";
        }
        catch { }
        if (a.Length > 60) a = a[..57] + "…";
        return name switch
        {
            "do_on_pc" => a,
            "find_files" => $"Looking for “{a}”",
            "read_file" => "Reading " + Path.GetFileName(a),
            "list_folder" => "Looking in " + a,
            "write_file" => "Writing " + Path.GetFileName(a),
            "open" => "Opening " + a,
            "clipboard" => a == "write" ? "Copying to the clipboard" : "Reading the clipboard",
            "web_search" => $"Searching the web for “{a}”",
            "research" => $"Researching “{a}”",
            "news" => a.Length > 0 ? $"Reading the news on “{a}”" : "Reading today's headlines",
            "read_webpage" => "Reading " + (Uri.TryCreate(a, UriKind.Absolute, out var u) ? u.Host : a),
            "pc_status" => "Checking the PC",
            "browser_history" => "Reading your browser's history",
            "notch_data" => "Reading the notch: " + a,
            "create_image" => "Making a picture",
            "undo_last" => "Undoing",
            "notch_page" => "Opening the notch: " + a,
            "run_powershell" => Why(argsJson),
            "manage_window" => "Window: " + a,
            "notify" => "Notifying on the notch",
            "recent_notifications" => "Reading your notifications",
            "remember" => "Remembering: " + a,
            "forget" => "Forgetting: " + a,
            _ => name,
        };
    }

    static string Why(string argsJson)
    {
        try
        {
            var n = JsonNode.Parse(argsJson.Length > 0 ? argsJson : "{}");
            string why = n?["why"]?.ToString() ?? "";
            if (why.Length > 0) return why.Length > 60 ? why[..57] + "…" : why;
            string first = (n?["script"]?.ToString() ?? "").Split('\n')[0];
            return "PowerShell: " + (first.Length > 48 ? first[..45] + "…" : first);
        }
        catch { return "PowerShell"; }
    }

    static string ToolOutcome(string result)
    {
        if (result.StartsWith("Error")) return "failed";
        if (result.StartsWith("The user declined")) return "not run";
        string first = result.Split('\n')[0];
        if (first.StartsWith("Done: ")) return first[6..];
        return first.StartsWith("Not understood") || first.Contains("couldn't") ? "didn't work" : "done";
    }
}
