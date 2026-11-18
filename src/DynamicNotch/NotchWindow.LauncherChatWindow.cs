using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace DynamicNotch;

/// <summary>
/// The chat, two more ways: popped out of the notch into its own window (moved and sized anywhere, kept on top if you
/// like, everything still works: the same box, keys, chats), and back in with Esc, the same button, or closing it;
/// or, in the notch, pulled taller by the handle on its bottom edge (remembered; the notch's window only grows while
/// the chat is taller).
/// </summary>
public partial class NotchWindow
{
    ChatPopWindow? _pop;
    double _chatExtra;
    bool _gripping;
    double _gripFrom, _gripStartExtra;
    Border? _chatGrip, _chatGripBar;
    TextBlock? _popButton, _pinButton;

    bool ChatPopped => _pop != null;

    /// <summary>How much taller the chat may be pulled: up to about 55 % of the screen.</summary>
    double MaxChatExtra
    {
        get
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            double h = screen != null && screen.Bounds.Height > 0 ? screen.Bounds.Height / Math.Max(1, _scale) : 900;
            return Math.Max(0, Math.Round(h * 0.6) - ExpandedSize.Height);
        }
    }

    // ───────────────────────────── taller, in the notch ─────────────────────────────

    FrameworkElement BuildChatGrip()
    {
        _chatGripBar = new Border { Width = 40, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _chatGrip = new Border
        {
            Child = _chatGripBar, Width = 90, Height = 12, Background = Brushes.Transparent, Cursor = Cursors.SizeNS, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -9), Visibility = Visibility.Collapsed,
            ToolTip = "Drag to make the chat taller  ·  double-click to reset",
        };
        Panel.SetZIndex(_chatGrip, 60);
        _chatGrip.MouseEnter += (_, _) => GripLit(true);
        _chatGrip.MouseLeave += (_, _) => { if (!_gripping) GripLit(false); };
        _chatGrip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2) { SetChatExtra(0, save: true); return; }
            _gripping = true;
            _gripFrom = e.GetPosition(this).Y;
            _gripStartExtra = _chatExtra;
            // room for the whole pull up front, so the window isn't resized on every move
            Height = FullHeight + MaxChatExtra;
            PositionWindow();
            _chatGrip.CaptureMouse();
        };
        _chatGrip.MouseMove += (_, e) =>
        {
            if (!_gripping) return;
            SetChatExtra(_gripStartExtra + e.GetPosition(this).Y - _gripFrom, save: false, resizeWindow: false);
        };
        _chatGrip.MouseLeftButtonUp += (_, _) =>
        {
            if (!_gripping) return;
            _gripping = false;
            _chatGrip.ReleaseMouseCapture();
            GripLit(_chatGrip.IsMouseOver);
            SetChatExtra(_chatExtra, save: true);
        };
        _chatGrip.LostMouseCapture += (_, _) => { if (_gripping) { _gripping = false; SetChatExtra(_chatExtra, save: true); } };
        Grid.SetRow(_chatGrip, 2);
        return _chatGrip;
    }

    void GripLit(bool on)
    {
        if (_chatGripBar == null) return;
        var acc = ChatAccent;
        _chatGripBar.Background = Frozen(A(acc, on ? (byte)0xD0 : (byte)0x55));
        _chatGripBar.BeginAnimation(WidthProperty, new DoubleAnimation(on ? 56 : 40, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void ShowGrip(bool on)
    {
        if (_chatGrip == null) return;
        _chatGrip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) GripLit(false);
    }

    /// <summary>The chat's extra height in the notch: the panel, the notch's shape and (unless dragging) its window follow.</summary>
    void SetChatExtra(double extra, bool save, bool resizeWindow = true)
    {
        extra = Math.Clamp(Math.Round(extra), 0, MaxChatExtra);
        _chatExtra = extra;
        Expanded.Height = ExpandedSize.Height + extra;
        if (_launchBody != null) { _launchBody.Width = double.NaN; _launchBody.Height = double.NaN; } // fills the new height
        if (resizeWindow)
        {
            Height = FullHeight + extra;
            PositionWindow();
        }
        _launchWantH = ExpandedSize.Height + extra;
        if (_mode == Mode.Expanded) ApplyShape();
        if (save && Math.Abs(_settings.AiChatExtra - extra) > 0.5) { _settings.AiChatExtra = extra; _settings.Save(); }
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => FollowChat());
    }

    /// <summary>The chat ends (or leaves the notch): the notch gets its own size back; the window shrinks once the shape has.</summary>
    void DropChatExtra()
    {
        if (_chatExtra <= 0 && Expanded.Height <= ExpandedSize.Height) return;
        _chatExtra = 0;
        Expanded.Height = ExpandedSize.Height;
        After(650, () =>
        {
            if (_chatExtra > 0 || _smallWindow) return;
            Height = FullHeight;
            PositionWindow();
        });
    }

    // ───────────────────────────── popped out ─────────────────────────────

    void TogglePop() { if (ChatPopped) PopIn(); else PopOut(); }

    void PopOut()
    {
        if (_pop != null || _launch == null || !_chatMode || _launchBox == null) return;
        // the notch lets go of the panel
        _launch.BeginAnimation(OpacityProperty, null);
        _launch.Opacity = 1;
        _launch.Visibility = Visibility.Visible;
        _launchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _launchScale.ScaleY = 1;
        Expanded.Children.Remove(_launch);
        if (_launchBody != null) { _launchBody.Width = double.NaN; _launchBody.Height = double.NaN; }
        ShowGrip(false);
        DropChatExtra();
        _launcherOpen = false;
        _launchWantH = 0;
        StopStartOrb();
        SetActivatable(false);
        UpdateRain(); UpdateAmbient();
        if (_mode == Mode.Expanded) Collapse();

        _pop = new ChatPopWindow(this, _launch, ChatAccent, _cyber, _settings.AiPopBounds, _settings.AiPopTopmost);
        foreach (var key in new[] { "LookTile", "LookEdge", "LookSheen", "LookLabel", "LookSwitchOff", "LookSwitchOn" })
            if (Resources.Contains(key)) _pop.Resources[key] = Resources[key];
        _pop.WantsBack += PopIn;
        _pop.Activated += (_, _) => { _launchBox?.Focus(); Keyboard.Focus(_launchBox); };
        _pop.Show();
        _pop.Activate();
        UpdatePopButtons();
        ChatKeys();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { _launchBox?.Focus(); ChatScrollTo(double.MaxValue, 0); });
    }

    void PopIn()
    {
        if (_pop == null || _launch == null) return;
        var w = _pop;
        _pop = null;
        _settings.AiPopBounds = w.BoundsText;
        _settings.AiPopTopmost = w.Topmost;
        _settings.Save();
        w.Release();
        w.Close();
        Expanded.Children.Add(_launch);
        // back in the notch, in the chat, as it was
        OpenLauncher(null);
        EnterChat(null);
        UpdatePopButtons();
    }

    void UpdatePopButtons()
    {
        if (_popButton != null)
        {
            _popButton.Text = ChatPopped ? "" : "";
            _popButton.ToolTip = ChatPopped ? "Back into the notch  ·  Esc" : "Pop out into its own window";
        }
        if (_pinButton != null)
        {
            _pinButton.Visibility = ChatPopped ? Visibility.Visible : Visibility.Collapsed;
            bool pinned = _pop?.Topmost == true;
            _pinButton.Text = pinned ? "" : "";
            _pinButton.ToolTip = pinned ? "Kept on top · click to let other windows cover it" : "Keep it on top of other windows";
        }
        ShowGrip(_chatMode && !ChatPopped && _launcherOpen);
    }

    void TogglePin()
    {
        if (_pop == null) return;
        _pop.Topmost = !_pop.Topmost;
        _settings.AiPopTopmost = _pop.Topmost;
        _settings.Save();
        UpdatePopButtons();
    }

    /// <summary>The header moves the popped window.</summary>
    void DragPop(MouseButtonEventArgs e)
    {
        if (_pop == null || e.ChangedButton != MouseButton.Left) return;
        try { _pop.DragMove(); } catch { }
    }
}

/// <summary>The chat's own window: dark, framed in the AI's colour, rounded (square in Neon), sized and moved freely.</summary>
sealed class ChatPopWindow : Window
{
    readonly Border _frame;
    bool _letGo;
    public event Action? WantsBack;

    public ChatPopWindow(NotchWindow owner, FrameworkElement content, Color accent, bool neon, string bounds, bool topmost)
    {
        Title = "AI chat · Dynamic Notch";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Topmost = topmost;
        MinWidth = 380;
        MinHeight = 300;
        Width = 520;
        Height = 560;
        Background = new SolidColorBrush(neon ? Color.FromRgb(0x05, 0x0C, 0x12) : Color.FromRgb(0x14, 0x14, 0x18));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 12;
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        if (!Place(bounds))
        {
            // first time: near the top of the screen, under where the notch is
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + 60;
        }
        content.Margin = new Thickness(0);
        var wash = new LinearGradientBrush(Color.FromArgb(neon ? (byte)0x1A : (byte)0x14, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), new Point(0, 0), new Point(0, 0.35));
        var inner = new Grid { Background = wash };
        inner.Children.Add(content);
        // a handle on top says it can be moved (anywhere that isn't text or a button moves it too)
        var handle = new Border { Width = 36, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x60, accent.R, accent.G, accent.B)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -9, 0, 0), IsHitTestVisible = false };
        inner.Children.Add(handle);
        _frame = new Border
        {
            Child = inner, BorderThickness = new Thickness(1), Padding = new Thickness(0, 12, 0, 6), Background = Brushes.Transparent,
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B), Color.FromArgb(0x30, accent.R, accent.G, accent.B), new Point(0, 0), new Point(1, 1)),
        };
        Content = _frame;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1, corner = neon ? 1 /* square */ : 2 /* round */;
            DwmSetWindowAttribute(hwnd, 20, ref dark, 4);     // dark frame
            DwmSetWindowAttribute(hwnd, 33, ref corner, 4);   // Windows 11 corners
        };
        // it arrives: a quick rise and fade
        Opacity = 0;
        var rise = new TranslateTransform(0, 10);
        _frame.RenderTransform = rise;
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        // closing it docks the chat back in the notch (unless the whole app is quitting)
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1 || Interactive(e.OriginalSource as DependencyObject)) return;
            try { DragMove(); e.Handled = true; } catch { }
        };
        Closing += (_, e) => { if (!_letGo && owner.IsVisible && !Dispatcher.HasShutdownStarted) { e.Cancel = true; WantsBack?.Invoke(); } };
    }

    /// <summary>Text, boxes, buttons, links and the like keep their clicks; everything else is a handle.</summary>
    static bool Interactive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Documents.Hyperlink) return true;
            if (d is FrameworkElement fe && (fe.Cursor == Cursors.Hand || fe.Cursor == Cursors.IBeam || fe.Cursor == Cursors.SizeNS)) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>"x,y,w,h" of where it was last time, when that's still on a screen.</summary>
    bool Place(string bounds)
    {
        var p = bounds.Split(',');
        if (p.Length != 4 || !double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y)
            || !double.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w)
            || !double.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h)) return false;
        var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!virt.IntersectsWith(new Rect(x + 40, y + 10, 80, 30))) return false; // off every screen now
        Left = x; Top = y; Width = Math.Max(MinWidth, w); Height = Math.Max(MinHeight, h);
        return true;
    }

    public string BoundsText => string.Join(",", new[] { Left, Top, ActualWidth, ActualHeight }.Select(v => Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Gives the chat back (before closing) so it can return to the notch.</summary>
    public void Release()
    {
        _letGo = true;
        if (_frame.Child is Grid g) g.Children.Clear();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
