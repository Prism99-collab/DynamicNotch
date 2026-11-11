using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DynamicNotch.Controls;

/// <summary>
/// The Neon theme's data rain, built to be smooth and nearly free. Three depth layers (far, middle, near: slower,
/// dimmer, smaller toward the back), each one a sheet of falling columns drawn once into a cached picture. The
/// graphics chip only slides each sheet down, timed to the screen's own refresh (no timer, no steps), and the sheet
/// holds its columns twice, one screen apart, so the slide loops without a seam. Per frame that's three numbers,
/// not one per character. Speed changes and pauses go through the animation's own clock: no restart, no jump.
/// The size is fixed (the shape around it clips it), so a growing or shrinking notch never rebuilds it.
/// </summary>
public sealed class DataRain : Canvas
{
    const string Glyphs = "ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789ABCDEF0123456789<>/*+=:";

    sealed class Layer
    {
        public Canvas Sheet = null!;
        public readonly TranslateTransform Shift = new();
        public AnimationClock? Clock;
        public double Speed;                 // px per second at SpeedFactor 1
        public readonly List<TextBlock[]> Columns = new(); // each column twice: one screen apart
    }

    readonly Layer[] _layers;
    readonly Random _rand = new();
    readonly int _fps;
    readonly DispatcherTimer _thaw, _mutate;
    Color _color;
    double _speed = 1;
    bool _running, _frozen;

    public DataRain(double width, double height, double cell, double fontSize, int fps, double minSpeed, double maxSpeed,
        int minLen, int maxLen, double density, Color color)
    {
        Width = width;
        Height = height;
        _fps = fps;
        _color = color;
        IsHitTestVisible = false;
        ClipToBounds = true;
        var font = new FontFamily("Consolas, MS Gothic, Yu Gothic, Segoe UI");
        var depths = new[] { (speed: 0.45, size: 0.8, alpha: 0.45), (speed: 0.7, size: 0.9, alpha: 0.7), (speed: 1.0, size: 1.0, alpha: 1.0) };
        _layers = new Layer[depths.Length];
        int slots = Math.Max(1, (int)(width / cell));
        for (int li = 0; li < depths.Length; li++)
        {
            var (sp, size, alpha) = depths[li];
            var layer = new Layer { Speed = (minSpeed + _rand.NextDouble() * (maxSpeed - minSpeed)) * sp };
            layer.Sheet = new Canvas { Width = width, Height = height * 2, Opacity = alpha, RenderTransform = layer.Shift, CacheMode = new BitmapCache(1.25) };
            double c = cell * size;
            for (int s = 0; s < slots; s++)
            {
                if (_rand.NextDouble() > density / depths.Length * 1.6) continue; // sparse: a third of the columns per layer, give or take
                int len = _rand.Next(minLen, maxLen + 1);
                string text = RandomText(len);
                double x = s * cell + (_rand.NextDouble() - 0.5) * cell * 0.4;
                double y = _rand.NextDouble() * height;
                var pair = new TextBlock[2];
                for (int k = 0; k < 2; k++)
                {
                    var tb = new TextBlock
                    {
                        FontFamily = font, FontSize = fontSize * size, LineHeight = c, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                        TextAlignment = TextAlignment.Center, Width = c, Text = text, Foreground = TailBrush(color),
                    };
                    SetLeft(tb, x);
                    SetTop(tb, y + k * height - len * c); // the copy one screen lower: the loop is seamless
                    layer.Sheet.Children.Add(tb);
                    pair[k] = tb;
                }
                layer.Columns.Add(pair);
            }
            layer.Shift.Y = -height;
            Children.Add(layer.Sheet);
            _layers[li] = layer;
        }
        _thaw = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _thaw.Tick += (_, _) => { _thaw.Stop(); _frozen = false; if (_running) foreach (var l in _layers) l.Clock?.Controller?.Resume(); };
        // now and then a character changes in one column (both copies): alive, not a loop. Rare: it redraws that sheet
        _mutate = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1400) };
        _mutate.Tick += (_, _) => Mutate();
    }

    /// <summary>Faint at the top of the tail, strong toward the bottom, the leading character almost white.</summary>
    static Brush TailBrush(Color c)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, c.R, c.G, c.B), 0.55));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, c.R, c.G, c.B), 0.9));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, (byte)(c.R + (255 - c.R) * 0.7), (byte)(c.G + (255 - c.G) * 0.7), (byte)(c.B + (255 - c.B) * 0.7)), 1));
        b.Freeze();
        return b;
    }

    string RandomText(int length)
    {
        var chars = new char[length * 2 - 1];
        for (int i = 0; i < length; i++)
        {
            chars[i * 2] = Glyphs[_rand.Next(Glyphs.Length)];
            if (i < length - 1) chars[i * 2 + 1] = '\n';
        }
        return new string(chars);
    }

    public bool Running => _running;

    /// <summary>Falls (from where it stopped).</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        foreach (var l in _layers)
        {
            if (l.Clock == null)
            {
                // one screen down, again and again: the sheet's second copy takes the first one's place exactly
                var slide = new DoubleAnimation(-Height, 0, TimeSpan.FromSeconds(Height / l.Speed)) { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(slide, _fps);
                l.Clock = slide.CreateClock();
                l.Shift.ApplyAnimationClock(TranslateTransform.YProperty, l.Clock);
            }
            else l.Clock.Controller?.Resume();
            if (l.Clock.Controller != null) l.Clock.Controller.SpeedRatio = _speed;
        }
        if (_frozen) foreach (var l in _layers) l.Clock?.Controller?.Pause();
        _mutate.Start();
    }

    /// <summary>Stops where it is: nothing moves, nothing is drawn again, no frames are asked for.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        foreach (var l in _layers) l.Clock?.Controller?.Pause();
        _mutate.Stop();
    }

    /// <summary>How fast it falls (1 normal); raised with the PC's load. Smoothly, from where it is.</summary>
    public double SpeedFactor
    {
        get => _speed;
        set
        {
            value = Math.Clamp(value, 0.2, 4);
            if (Math.Abs(value - _speed) < 0.05) return;
            _speed = value;
            foreach (var l in _layers) if (l.Clock?.Controller != null) l.Clock.Controller.SpeedRatio = value;
        }
    }

    /// <summary>Another colour (cyan normally, red under heavy load).</summary>
    public void SetColor(Color c)
    {
        if (c == _color) return;
        _color = c;
        var b = TailBrush(c);
        foreach (var l in _layers)
            foreach (var pair in l.Columns)
                foreach (var tb in pair) tb.Foreground = b;
    }

    /// <summary>Everything holds still for a moment (a notification arrived).</summary>
    public void Freeze(int ms)
    {
        if (!_running) return;
        _frozen = true;
        foreach (var l in _layers) l.Clock?.Controller?.Pause();
        _thaw.Interval = TimeSpan.FromMilliseconds(ms);
        _thaw.Stop();
        _thaw.Start();
    }

    void Mutate()
    {
        var layer = _layers[_rand.Next(_layers.Length)];
        if (layer.Columns.Count == 0) return;
        var pair = layer.Columns[_rand.Next(layer.Columns.Count)];
        var t = pair[0].Text.ToCharArray();
        int i = _rand.Next(t.Length / 2 + 1) * 2;
        if (i >= t.Length) return;
        t[i] = Glyphs[_rand.Next(Glyphs.Length)];
        string s = new(t);
        pair[0].Text = pair[1].Text = s;
    }
}
