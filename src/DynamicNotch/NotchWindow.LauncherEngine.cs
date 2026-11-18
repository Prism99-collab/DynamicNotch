using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using DynamicNotch.Services;
using Do = DynamicNotch.Services.LauncherIntent.Do;
using Intent = DynamicNotch.Services.LauncherIntent.Intent;

namespace DynamicNotch;

/// <summary>
/// The search's thinking, done off the screen's thread so typing never waits for it:
///  • every key hands what's typed to one worker thread; the worker works out the commands it understood, the answer,
///    the apps and settings that fit, and the files from the notch's own list (a few milliseconds, every key, no pause
///    needed), then the screen only draws the result, on the very next frame;
///  • only the newest question counts: an answer that's out of date by the time it's ready is dropped;
///  • Enter, Tab and the arrows never act on an old list: if the newest answer isn't drawn yet, it's worked out on
///    the spot first.
/// </summary>
public partial class NotchWindow
{
    /// <summary>Everything the search needs to draw its results, worked out away from the screen.</summary>
    sealed record SearchPlan(int Gen, string Query, string Terms, FileFilter? Filter, IReadOnlyList<Intent> Intents, LauncherAnswers.Answer? Fact,
        List<(LauncherIndex.Entry E, double S)> Entries, List<(LauncherIndex.Entry E, double S, WindowsSearch.Item Item)> Files, double Ms);

    readonly object _planGate = new();
    (int Gen, string Query)? _planWanted;
    readonly AutoResetEvent _planWake = new(false);
    Thread? _planThread;
    SearchPlan? _lastPlan;

    /// <summary>A key was typed: the letter is on screen already; the results follow as soon as they're worked out.</summary>
    void QueueSearch()
    {
        if (_chatMode) { OnChatTyped(); return; } // typing a message (or filtering your chats), not a search
        int v = ++_searchAsked;
        TypePulse();
        string q = _launchBox?.Text ?? "";
        // nothing to think about, or ↑ going back through earlier searches (it needs the answer at once to keep its place)
        if (_recalling || q.Trim().Length == 0 || q.TrimStart().StartsWith("desktop:", StringComparison.OrdinalIgnoreCase)) { Search(); return; }
        lock (_planGate) _planWanted = (v, q);
        if (_planThread == null)
        {
            _planThread = new Thread(PlanLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Search" };
            _planThread.Start();
        }
        _planWake.Set();
    }

    void PlanLoop()
    {
        while (true)
        {
            _planWake.WaitOne();
            while (true)
            {
                (int Gen, string Query)? job;
                lock (_planGate) { job = _planWanted; _planWanted = null; }
                if (job == null) break;
                SearchPlan plan;
                try { plan = ComputePlan(job.Value.Query, job.Value.Gen); }
                catch (Exception ex) { AppLog.Write("search: " + ex.Message); continue; }
                bool newer;
                lock (_planGate) newer = _planWanted != null;
                if (newer) continue; // already out of date: work out the newest instead
                // before the next frame is drawn (the screen's own work comes first, keys after)
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    if (!_launcherOpen || plan.Gen != _searchAsked || _launchBox?.Text != plan.Query) return;
                    ApplyPlanTimed(plan, merge: false);
                });
            }
        }
    }

    /// <summary>Something the search draws from changed (the list of apps was refreshed): worked out again, the line you're on kept.</summary>
    void RefreshPlan()
    {
        if (!_launcherOpen || _actionsFor != null || _launchBox == null) return;
        ApplyPlanTimed(ComputePlan(_launchBox.Text, _searchAsked), merge: true);
    }

    void SearchNowIfBehind() { if (_searchDone != _searchAsked) Search(); }

    /// <summary>
    /// What's typed, worked out: the commands and the answer, the apps, settings and places that fit (with your habits),
    /// and the files on every drive. Safe on any thread: it only reads.
    /// </summary>
    SearchPlan ComputePlan(string q, int gen)
    {
        var sw = Stopwatch.StartNew();
        var now = DateTime.Now;
        string trimmed = q.Trim();
        var noEntries = new List<(LauncherIndex.Entry, double)>();
        var noFiles = new List<(LauncherIndex.Entry, double, WindowsSearch.Item)>();
        if (trimmed.Length == 0 || trimmed.StartsWith("desktop:", StringComparison.OrdinalIgnoreCase))
            return new SearchPlan(gen, q, q, null, Array.Empty<Intent>(), null, noEntries, noFiles, 0);

        IReadOnlyList<Intent> intents = Array.Empty<Intent>();
        LauncherAnswers.Answer? fact = null;
        if (_settings.SearchCommands)
        {
            intents = LauncherIntent.Parse(q, now);
            fact = LauncherIntent.Fact(q, now) ?? LauncherAnswers.For(q);
        }
        var (find, understood) = FindFor(q, intents);
        understood |= fact != null;
        var (terms, filter) = understood ? (find, (FileFilter?)null) : SearchFilter.Parse(find, now);
        bool learn = _settings.SearchLearn;

        // apps, settings, places and the files the notch knows by heart
        var entries = new List<(LauncherIndex.Entry E, double S)>();
        foreach (var e in _index.Entries)
        {
            double s;
            if (filter != null)
            {
                // a filter: only files and folders that pass it (dates read once, then kept)
                if (e.Kind is not (LauncherIndex.Kind.File or LauncherIndex.Kind.Folder) || e.Target.StartsWith("shell:") || IsShortcut(e.Target)) continue;
                var facts = FileFacts(e.Target, e.Kind == LauncherIndex.Kind.Folder);
                if (facts.When == DateTime.MinValue || !filter.Allows(e.Name, e.Kind == LauncherIndex.Kind.Folder, facts.When)) continue;
                if (filter.MinSize > 0 && facts.Size < filter.MinSize) continue;
                s = terms.Length == 0 ? 300 : LauncherIndex.Score(terms, e);
            }
            else s = LauncherIndex.Score(terms, e);
            if (s <= 0) continue;
            entries.Add((e, s + DesktopLift(e) + _index.Habit(e) + (learn ? _index.PickBoost(q, e) : 0)));
        }
        entries = entries.OrderByDescending(x => x.S).Take(48).ToList();

        // files on every drive (a few milliseconds: the list narrows as you type)
        var files = new List<(LauncherIndex.Entry, double, WindowsSearch.Item)>();
        if (!understood && _settings.SearchWholePc && _fileIndex.Ready && (terms.Trim().Length >= 2 || filter != null))
        {
            var found = _fileIndex.Find(terms, filter, 40);
            for (int i = 0; i < found.Count; i++)
            {
                var f = found[i];
                if (IsShortcut(f.Path) && !OnDesktop(f.Path)) continue; // shortcuts are the apps' own lines
                var e = new LauncherIndex.Entry(f.Name, f.Path, f.Folder ? LauncherIndex.Kind.Folder : LauncherIndex.Kind.File, ParentName(f.Path));
                double s = terms.Trim().Length == 0 ? 300 + (40 - i) : LauncherIndex.Score(terms, e);
                if (s <= 0) s = 160; // found by the list's own rules (a word's initials, a word inside)
                if (!f.Folder && now - f.Modified < TimeSpan.FromDays(14)) s += 30;
                if (i < 12 && !f.Folder) FileFacts(f.Path, false); // sizes for the lines you'll see, read here, not while drawing
                files.Add((e, s + DesktopLift(e) + _index.Habit(e) + (learn ? _index.PickBoost(q, e) : 0), new WindowsSearch.Item(f.Path, f.Name, f.Folder, f.Modified, -1, false)));
            }
        }
        return new SearchPlan(gen, q, terms, filter, intents, fact, entries, files, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>What the list should look for, given the commands understood: for "open spotify", "spotify".</summary>
    static (string Find, bool Understood) FindFor(string q, IReadOnlyList<Intent> list)
    {
        if (list.Count == 0) return (q, false);
        if (list.Count == 1 && list[0] is { Kind: Do.Open, Op: "open" } t && !Regex.IsMatch(t.Text!, @"\s+and\s+|,"))
        {
            if (LauncherAnswers.For(t.Text!) is { Url: not null }) return (q, true);
            string name = Regex.Replace(t.Text!, @"\s*\b(?:settings?|app|folder)$", "").Trim();
            return (name.Length > 0 ? name : q, false);
        }
        return (q, true);
    }

    void ApplyPlanTimed(SearchPlan plan, bool merge)
    {
        if (!_measureSearch) { ApplyPlan(plan, merge); return; }
        var sw = Stopwatch.StartNew();
        ApplyPlan(plan, merge);
        AppLog.Write($"search '{plan.Query}' {(merge ? "merge " : "")}worked out in {plan.Ms:0.0} ms (off screen), drawn in {sw.Elapsed.TotalMilliseconds:0.0} ms, {_hits.Count} lines");
    }

    /// <summary>Warm-up, a little after start: the panel, the search's code paths and your usual icons, so the first Alt+Space is instant.</summary>
    void WarmLauncher()
    {
        After(9000, () =>
        {
            if (_launcherOpen) return;
            try
            {
                BuildLauncher();
                StyleLauncher();
                foreach (var e in _index.Favourites(10, _settings.SearchHiddenApps)) WarmIcon(e.Target);
                if (_desktopHidden || DesktopIcons.Shown == false) foreach (var e in DesktopItems().Take(8)) WarmIcon(e.Target);
                _desktopItems = null;
                Task.Run(() => { try { ComputePlan("a", 0); ComputePlan("notch", 0); } catch { } });
            }
            catch (Exception ex) { AppLog.Write("search warm-up: " + ex.Message); }
        });
    }
}
