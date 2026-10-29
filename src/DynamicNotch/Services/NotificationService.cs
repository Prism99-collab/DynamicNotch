using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicNotch.Services;

public sealed record ToastInfo(string App, string? Aumid, string Title, string Body, ImageSource? Icon);

/// <summary>
/// Mirrors Windows toast notifications (Teams, Outlook, WhatsApp, Discord...).
/// Uses the instant change event when Windows delivers it, otherwise a light poll; it detects
/// which one actually works on this PC and adapts.
/// </summary>
public sealed class NotificationService
{
    readonly Dispatcher _ui;
    UserNotificationListener? _listener;
    readonly HashSet<uint> _seen = new();
    DispatcherTimer? _poll;
    TimeSpan _interval = TimeSpan.FromSeconds(2);
    bool _primed, _eventMode, _checking, _again;
    DateTime _lastEvent;

    public bool Available { get; private set; }
    public string Status { get; private set; } = "not started";
    public event Action<ToastInfo>? Received;

    public NotificationService(Dispatcher ui) => _ui = ui;

    /// <summary>How often to look for new notifications when events aren't available (slower on battery).</summary>
    public void SetInterval(TimeSpan interval)
    {
        _interval = interval;
        ApplyInterval();
    }

    void ApplyInterval()
    {
        // with live events the poll is only a safety net
        if (_poll != null) _poll.Interval = _eventMode ? TimeSpan.FromSeconds(Math.Max(10, _interval.TotalSeconds * 3)) : _interval;
    }

    public async Task InitAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            // unpackaged apps can wait forever for this consent; don't hang on it
            var request = _listener.RequestAccessAsync().AsTask();
            if (await Task.WhenAny(request, Task.Delay(4000)) != request) { Status = "no answer from Windows"; return; }
            var access = request.Result;
            Status = access.ToString();
            if (access != UserNotificationListenerAccessStatus.Allowed) return;
            Available = true;
            await CheckAsync(fromEvent: false);
            _primed = true;

            try
            {
                _listener.NotificationChanged += (_, _) => _ui.InvokeAsync(async () =>
                {
                    _lastEvent = DateTime.UtcNow;
                    await CheckAsync(fromEvent: true);
                });
                _eventMode = true;
            }
            catch { _eventMode = false; }
            Status += _eventMode ? " · live events" : " · polling";

            _poll = new DispatcherTimer(DispatcherPriority.Background);
            _poll.Tick += async (_, _) => await CheckAsync(fromEvent: false);
            ApplyInterval();
            _poll.Start();
        }
        catch (Exception ex)
        {
            Status = ex.GetType().Name + ": " + ex.Message;
            Available = false;
        }
    }

    async Task CheckAsync(bool fromEvent)
    {
        if (_listener == null) return;
        if (_checking) { _again = true; return; }
        _checking = true;
        try
        {
            do
            {
                _again = false;
                IReadOnlyList<UserNotification> list;
                try { list = await _listener.GetNotificationsAsync(NotificationKinds.Toast); }
                catch { return; }

                var current = new HashSet<uint>();
                foreach (var n in list)
                {
                    current.Add(n.Id);
                    if (!_seen.Add(n.Id) || !_primed) continue;
                    if ((DateTimeOffset.Now - n.CreationTime).TotalSeconds > 30) continue;

                    // the safety-net poll found something the event never announced: events don't work here
                    if (!fromEvent && _eventMode && (DateTime.UtcNow - _lastEvent).TotalSeconds > 3)
                    {
                        _eventMode = false;
                        Status = Status.Replace("live events", "polling");
                        ApplyInterval();
                    }

                    var info = await ReadAsync(n);
                    if (info != null) Received?.Invoke(info);
                }
                _seen.IntersectWith(current);
            } while (_again);
        }
        finally { _checking = false; }
    }

    static async Task<ToastInfo?> ReadAsync(UserNotification n)
    {
        try
        {
            string app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Notification";
            string? aumid = null;
            try { aumid = n.AppInfo?.AppUserModelId; } catch { }
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new();
            string title = texts.Count > 0 ? texts[0] : app;
            string body = string.Join(" · ", texts.Skip(1));
            ImageSource? icon = null;
            try
            {
                var logo = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
                if (logo != null)
                {
                    using var ras = await logo.OpenReadAsync();
                    using var s = ras.AsStreamForRead();
                    var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 64;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    icon = bmp;
                }
            }
            catch { }
            return new ToastInfo(app, aumid, title, body, icon);
        }
        catch { return null; }
    }
}
