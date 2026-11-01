using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The FPS pill's extras, each one switched on in Preferences (off by default): CPU and GPU load in %, CPU and GPU
/// temperature in °C. They sit after the frame rate in small type (the label a step dimmer than the number, a
/// hairline between groups), each group in its own colour (yours to choose), and change once a second, not 20
/// times, so nothing flickers in the corner of your eye. The numbers keep a fixed width and the pill only resizes in
/// 8 px steps, so it doesn't twitch either. One reading a second, off the UI thread, only while a game is in
/// front with at least one extra on; a temperature this PC doesn't report simply isn't shown.
/// </summary>
public partial class NotchWindow
{
    StackPanel? _statsRow;
    TextBlock? _cpuValue, _cpuTemp, _gpuValue, _gpuTemp;
    FrameworkElement? _cpuGroup, _gpuGroup, _statsSep;
    double _gameWidth = 54;
    bool _statsBusy;

    internal static readonly (string Name, string Hex)[] StatColors =
    {
        ("White", "#E6FFFFFF"), ("Cyan", "#54C1E6"), ("Green", "#30D158"), ("Yellow", "#FEE801"),
        ("Orange", "#FF9F0A"), ("Pink", "#FF2E88"), ("Purple", "#BF5AF2"), ("Red", "#FF453A"),
    };

    bool AnyGameStat => _settings.FpsCpu || _settings.FpsGpu || _settings.FpsCpuTemp || _settings.FpsGpuTemp;

    Color StatColor(string? hex) => Hex(hex) ?? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);

    void BuildGameStats()
    {
        if (_statsRow != null) return;
        if (VisualTreeHelper.GetChildrenCount(GamePanel) == 0 || GamePanel.Children[0] is not StackPanel fpsRow) return;
        _statsRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed };
        FrameworkElement Group(string label, out TextBlock value, out TextBlock temp)
        {
            var g = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(new TextBlock { Text = label, FontSize = 7.5, FontWeight = FontWeights.SemiBold, Opacity = 0.62, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 3, 0) });
            value = new TextBlock { FontSize = 9.5, FontWeight = FontWeights.SemiBold, MinWidth = 21, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
            temp = new TextBlock { FontSize = 9.5, MinWidth = 20, TextAlignment = TextAlignment.Right, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
            g.Children.Add(value);
            g.Children.Add(temp);
            return g;
        }
        Rectangle Hairline() => new() { Width = 1, Height = 8, Fill = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        _statsRow.Children.Add(Hairline());
        _cpuGroup = Group("CPU", out _cpuValue, out _cpuTemp);
        _statsRow.Children.Add(_cpuGroup);
        _statsSep = Hairline();
        _statsRow.Children.Add(_statsSep);
        _gpuGroup = Group("GPU", out _gpuValue, out _gpuTemp);
        _statsRow.Children.Add(_gpuGroup);
        fpsRow.Children.Add(_statsRow);
    }

    /// <summary>Once a second while a game is in front: read, then show (or hide) the extras.</summary>
    async Task GameStatsTick()
    {
        if (!AnyGameStat || _game == null || _statsBusy) { ShowGameStats(null); return; }
        _statsBusy = true;
        try
        {
            _strain ??= new StrainService(); // the strain watch rests in games: its reader is free
            var service = _strain;
            var r = await Task.Run(service.Sample);
            if (_game != null) ShowGameStats(r);
        }
        catch (Exception ex) { AppLog.Write("game stats: " + ex.Message); }
        finally { _statsBusy = false; }
    }

    void ShowGameStats(StrainService.Reading? r)
    {
        BuildGameStats();
        if (_statsRow == null) return;
        if (r == null || !AnyGameStat)
        {
            if (_statsRow.Visibility != Visibility.Collapsed) { _statsRow.Visibility = Visibility.Collapsed; FitGamePill(); }
            return;
        }
        bool cpu = _settings.FpsCpu || (_settings.FpsCpuTemp && r.CpuTemp != null);
        bool gpu = _settings.FpsGpu || (_settings.FpsGpuTemp && r.GpuTemp != null);
        var cpuBrush = Frozen(StatColor(_settings.FpsCpuColor));
        var gpuBrush = Frozen(StatColor(_settings.FpsGpuColor));
        Fill(_cpuGroup!, _cpuValue!, _cpuTemp!, cpu, _settings.FpsCpu ? $"{r.Cpu:0}%" : null, _settings.FpsCpuTemp && r.CpuTemp is { } ct ? $"{ct:0}°" : null, cpuBrush);
        Fill(_gpuGroup!, _gpuValue!, _gpuTemp!, gpu, _settings.FpsGpu ? $"{r.Gpu:0}%" : null, _settings.FpsGpuTemp && r.GpuTemp is { } gt ? $"{gt:0}°" : null, gpuBrush);
        _statsSep!.Visibility = cpu && gpu ? Visibility.Visible : Visibility.Collapsed;
        var vis = cpu || gpu ? Visibility.Visible : Visibility.Collapsed;
        if (_statsRow.Visibility != vis) _statsRow.Visibility = vis;
        FitGamePill();

        static void Fill(FrameworkElement group, TextBlock value, TextBlock temp, bool on, string? v, string? t, Brush brush)
        {
            group.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;
            ((TextBlock)((Panel)group).Children[0]).Foreground = brush;
            value.Foreground = temp.Foreground = brush;
            value.Visibility = v != null ? Visibility.Visible : Visibility.Collapsed;
            temp.Visibility = t != null ? Visibility.Visible : Visibility.Collapsed;
            if (v != null && value.Text != v) value.Text = v;
            if (t != null && temp.Text != t) temp.Text = t;
            temp.Margin = new Thickness(v != null ? 3 : 0, 0, 0, 0);
        }
    }

    /// <summary>The pill's width follows what it shows, in 8 px steps (so it never twitches with a digit).</summary>
    void FitGamePill()
    {
        if (GamePanel.Children.Count == 0 || GamePanel.Children[0] is not FrameworkElement row) return;
        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double min = _cyber ? 46 : 54;
        double w = Math.Max(min, Math.Ceiling((row.DesiredSize.Width + 16) / 8) * 8);
        if (Math.Abs(w - _gameWidth) < 1) return;
        _gameWidth = w;
        GamePanel.Width = w;
        if (_mode == Mode.Game)
        {
            ApplyShape();
            if (_smallWindow) { _smallWindow = false; SetSmallWindow(true); } // the small window grows with it
        }
    }

    /// <summary>The pill's width in a game (the frame rate alone, or with its extras).</summary>
    double GamePillWidth => Math.Max(_cyber ? 46 : 54, _gameWidth);
}
