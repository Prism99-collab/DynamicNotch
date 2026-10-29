using System.Windows.Threading;

namespace DynamicNotch.Services;

/// <summary>
/// The notch's own Focus mode. Windows locks its Do Not Disturb switch to Microsoft apps
/// ("Access is denied" for the Focus API), so the notch does the job itself: while on, it holds
/// notifications and minor alerts, then hands you a summary of what you missed.
/// </summary>
public sealed class FocusService
{
    readonly List<string> _held = new();

    public bool IsOn { get; private set; }
    public DateTime StartedAt { get; private set; }
    public IReadOnlyList<string> Held => _held;
    public TimeSpan Elapsed => IsOn ? DateTime.Now - StartedAt : TimeSpan.Zero;

    public event Action? Changed;

    public FocusService(Dispatcher ui) { }

    public void Init() { }

    public void Toggle() => Set(!IsOn);

    public void Set(bool on)
    {
        if (on == IsOn) return;
        IsOn = on;
        if (on) { StartedAt = DateTime.Now; _held.Clear(); }
        Changed?.Invoke();
    }

    /// <summary>Remembers that something from <paramref name="source"/> was held back.</summary>
    public void Hold(string source) { if (IsOn) _held.Add(source); }

    /// <summary>"Teams ×3, WhatsApp, Outlook"</summary>
    public string HeldSummary(int maxApps = 3)
    {
        var groups = _held.GroupBy(h => h).OrderByDescending(g => g.Count()).ToList();
        var parts = groups.Take(maxApps).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToList();
        if (groups.Count > maxApps) parts.Add($"+{groups.Count - maxApps} more");
        return string.Join(", ", parts);
    }
}
