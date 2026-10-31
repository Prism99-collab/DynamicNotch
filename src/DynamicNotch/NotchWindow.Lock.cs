using System.Windows.Media;

namespace DynamicNotch;

/// <summary>
/// Locked: the notch keeps showing what's happening (music, calls, alerts, the FPS pill) but reacts to nothing:
/// no opening at the top edge, no file drops, no clicks, no touch gestures; everything under it works as if it
/// weren't there. Locked from the gold button in the control center (or Settings / the tray menu), unlocked from
/// the tray icon (a click on it) or Win+Alt+N. It stays locked across restarts.
/// </summary>
public partial class NotchWindow
{
    static readonly Brush LockGold = Frozen(Color.FromRgb(0xFF, 0xC4, 0x0A));

    public bool IsLocked => _settings.Locked;

    /// <summary>Raised when the notch is locked or unlocked (the tray shows it).</summary>
    public event Action? LockChanged;

    void WireLock()
    {
        LockBtn.Click += (_, _) => SetLocked(true);
    }

    public void SetLocked(bool on)
    {
        if (on == _settings.Locked) return;
        _settings.Locked = on;
        _settings.Save();
        Services.AppLog.Write(on ? "notch locked" : "notch unlocked");
        if (on)
        {
            if (_mode == Mode.Expanded) Collapse();
            if (_dropArmed) SetDropArmed(false);
            if (_mode == Mode.Drop) LeaveDropZone();
            CancelTouchGesture();
            _peek = false;
            _dwell = 0;
            ApplyShape();
        }
        RefreshClickThrough();
        SyncSettingsPage();
        LockChanged?.Invoke();
        ShowOsd("lock", "", null, on ? "Notch locked" : "Notch unlocked",
            on ? "Still shows things, won't react · unlock from its tray icon" : "Back to normal", null,
            glyphBrush: LockGold, seconds: on ? 3.4 : 1.8, force: true, motion: on ? Motion.Auto : Motion.Pulse);
    }
}
