using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The mouse and headset batteries, as two tiny rings beside the date on Home: the ring fills with the level
/// (green, yellow under 50 %, red under 20 %), the device's icon sits inside, a little bolt shows while it charges.
/// A reading too old to trust (a sleeping mouse) or a headset that's off is drawn grey. Hover for the details.
/// Low battery (20 / 10 / 5 %) pops up once per threshold, re-armed after charging; old readings never alert.
/// </summary>
public partial class NotchWindow
{
    readonly DeviceBatteryService _devices;
    readonly Dictionary<DeviceKind, int> _lowAlerted = new(); // the lowest threshold already announced

    static readonly Color RingGreen = Color.FromRgb(0x30, 0xD1, 0x58), RingYellow = Color.FromRgb(0xFF, 0xD6, 0x0A), RingRed = Color.FromRgb(0xFF, 0x45, 0x3A);

    void WireDevices()
    {
        _devices.Changed += OnDevicesChanged;
        _devices.Start();
    }

    string _devicesLogged = "";
    int _devicesCount;

    void OnDevicesChanged()
    {
        string summary = string.Join(", ", _devices.Devices.Select(d => $"{d.Name} {(d.Level is { } l ? l + "%" : "--")} {d.State}{(d.Charging ? " charging" : "")}"));
        if (summary != _devicesLogged) { _devicesLogged = summary; AppLog.Write("device batteries: " + (summary.Length > 0 ? summary : "none")); CyberPulse(); }
        int count = _devices.Devices.Count;
        if (count != _devicesCount)
        {
            // rings appeared or went away: the switcher re-fits its chips to the header space left
            _devicesCount = count;
            _switchKey = "";
            RenderSwitch();
        }
        RenderDeviceRings();
        foreach (var d in _devices.Devices) CheckDeviceLow(d);
    }

    string _ringsKey = "";

    /// <summary>
    /// Rings on Home. When the "what to show" switcher also needs the header, they share it: the rings go compact
    /// (the percentage moves into their hint) and the date steps aside, so nothing overlaps.
    /// </summary>
    void RenderDeviceRings()
    {
        var devices = _devices.Devices;
        bool show = devices.Count > 0 && _page == 0;
        bool compact = show && LiveSwitch.Visibility == Visibility.Visible;
        DateText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (!show) { DeviceRings.Visibility = Visibility.Collapsed; return; }
        DeviceRings.Visibility = Visibility.Visible;
        // only rebuilt when something on them changes (a ring under the pointer keeps its hint)
        string key = (compact ? "c|" : "f|") + string.Join("|", devices.Select(d => $"{d.Kind}{d.Level}{d.State}{d.Charging}"));
        if (key == _ringsKey) return;
        _ringsKey = key;
        DeviceRings.Children.Clear();
        foreach (var d in devices) DeviceRings.Children.Add(DeviceRing(d, compact));
    }

    FrameworkElement DeviceRing(DeviceBattery d, bool compact = false)
    {
        const double size = 18, stroke = 1.8;
        bool live = d.State == BatteryState.Live && d.Level != null;
        double level = Math.Clamp((d.Level ?? 0) / 100.0, 0, 1);
        var color = !live ? Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)
            : d.Charging ? RingGreen : level < 0.2 ? RingRed : level < 0.5 ? RingYellow : RingGreen;

        var grid = new Grid { Width = size, Height = size, Background = Brushes.Transparent };
        grid.Children.Add(new Ellipse { Stroke = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), StrokeThickness = stroke });
        if (d.Level != null && level > 0)
            grid.Children.Add(new Path
            {
                Data = Arc(size, stroke, level), Stroke = Frozen(color), StrokeThickness = stroke,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        grid.Children.Add(new TextBlock
        {
            Text = d.Kind == DeviceKind.Mouse ? "" : "",
            FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8.5,
            Foreground = live ? Brushes.White : DimBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        if (live && d.Charging)
        {
            // a small bolt on the ring's corner, breathing while it charges
            var bolt = new Border
            {
                Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Frozen(RingGreen),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -2),
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 5.5, Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            if (!Eco)
                bolt.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(900))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            bolt.Unloaded += (_, _) => bolt.BeginAnimation(OpacityProperty, null); // a replaced ring stops breathing
            grid.Children.Add(bolt);
        }
        string state = d.State switch
        {
            BatteryState.Off => "off",
            BatteryState.Stale => d.Level is { } last ? $"no recent report (last {last}%)" : "waiting for the first report",
            _ => d.Charging ? "charging" : "on battery",
        };
        string hint = live ? $"{d.Name} · {d.Level}% · {state}" : $"{d.Name} · {state}";

        // the percentage right beside the ring, so it reads without hovering
        var pct = new TextBlock
        {
            Text = d.Level is { } l ? $"{l}%" : "--", FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            Foreground = live ? Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)) : DimBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
        };
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, compact ? 6 : 9, 0), Background = Brushes.Transparent };
        item.Children.Add(grid);
        if (!compact) item.Children.Add(pct);
        // the hint opens the moment the pointer arrives (no tooltip delay), in front of everything
        item.MouseEnter += (_, _) => ShowTip(item, hint);
        return item;
    }

    /// <summary>A ring arc from the top, clockwise, covering <paramref name="share"/> of the circle.</summary>
    static Geometry Arc(double size, double stroke, double share)
    {
        double r = (size - stroke) / 2, c = size / 2;
        if (share >= 0.999)
        {
            var full = new EllipseGeometry(new Point(c, c), r, r);
            full.Freeze();
            return full;
        }
        double a = share * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, share > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>20 %, 10 %, 5 %: each announced once on the way down; charging or getting back to 25 % re-arms them.</summary>
    void CheckDeviceLow(DeviceBattery d)
    {
        if (d.State != BatteryState.Live || d.Level is not { } level) return; // old readings never alert
        if (d.Charging || level >= 25) { _lowAlerted.Remove(d.Kind); return; }
        int threshold = level <= 5 ? 5 : level <= 10 ? 10 : level <= 20 ? 20 : 0;
        if (threshold == 0) return;
        if (_lowAlerted.TryGetValue(d.Kind, out int done) && done <= threshold) return;
        _lowAlerted[d.Kind] = threshold;
        string what = d.Kind == DeviceKind.Mouse ? "Mouse" : "Headset";
        ShowOsd("battery", d.Kind == DeviceKind.Mouse ? "" : "", level / 100.0, $"{what} battery low · {level}%", d.Name, null,
            glyphBrush: level <= 10 ? Red : Orange, seconds: 3.5, force: true, motion: Motion.Shake);
    }
}
