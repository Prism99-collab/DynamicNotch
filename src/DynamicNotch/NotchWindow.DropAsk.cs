using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicNotch.Controls;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Drop a file on the notch to ask about it. The drop zone has two places: the shelf (keep it), and Pixo on the right
/// ("Ask Pixo about it"). Bring the file over Pixo: the face opens wide and watches it come; let go: it eats it, and
/// the chat opens on a new conversation with the file attached (a picture, a document, a PDF, code, a page of text),
/// a first question ready to send or to type over. Text dragged from a page works too.
/// Works with or without the AI set up: without a key, the chat asks for one first, as it always does.
/// </summary>
public partial class NotchWindow
{
    NotchFace? _dropFace;
    bool _dropAsk;

    /// <summary>The drop zone opens: Pixo is there on the right, curious.</summary>
    void DropAskShow()
    {
        DropAsk.Visibility = _settings.AiChat ? Visibility.Visible : Visibility.Collapsed;
        if (!_settings.AiChat) return;
        if (_dropFace == null)
        {
            _dropFace = new NotchFace(20) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            DropAskFace.Children.Add(_dropFace);
        }
        _dropFace.Visibility = _settings.NotchFace ? Visibility.Visible : Visibility.Collapsed;
        _dropFace.Neon = _cyber; _dropFace.Accent = PalAccent(); _dropFace.Eco = Eco;
        _dropFace.SetOutfit(FaceOutfitNow(), animate: false);
        _dropFace.SetMood(NotchFace.Mood.Idle);
        _dropAsk = false;
        DropAskPaint(false);
    }

    /// <summary>Over Pixo or not (the drag moves): its place lights up, the face opens wide and follows it.</summary>
    void DropAskOver(Point inPanel)
    {
        if (DropAsk.Visibility != Visibility.Visible) return;
        var at = DropAsk.TranslatePoint(new Point(0, 0), DropPanel);
        bool over = inPanel.X >= at.X - 10 && inPanel.X <= at.X + DropAsk.ActualWidth + 14 && inPanel.Y >= -6 && inPanel.Y <= DropPanel.ActualHeight + 8;
        _dropFace?.LookAt((inPanel.X - (at.X + 15)) / 160, -(inPanel.Y - 46) / 60);
        if (over == _dropAsk) return;
        _dropAsk = over;
        DropAskPaint(over);
        if (over)
        {
            if (_dropHot) SetDropHot(false); // one place at a time
            _dropFace?.Play(NotchFace.Emote.Excited, 1.4);
            FeelSound(Sfx.Cue.On);
        }
    }

    void DropAskPaint(bool on)
    {
        var acc = _cyber ? CyCyan : ChatAccent;
        DropAsk.Background = new SolidColorBrush(on ? A(acc, 0x2E) : Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        DropAsk.BorderBrush = new SolidColorBrush(on ? acc : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        DropAsk.CornerRadius = new CornerRadius(_cyber ? 0 : 14);
        DropAskTitle.Text = on ? "Let go" : "Ask Pixo";
        DropAskSub.Text = on ? "to ask about it" : "about it";
        if (!Eco)
        {
            var st = DropAsk.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
            DropAsk.RenderTransformOrigin = new Point(0.5, 0.5);
            DropAsk.RenderTransform = st;
            var k = new DoubleAnimation(on ? 1.06 : 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, k);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, k);
        }
        if (on) UpdateAura(acc);
    }

    /// <summary>Let go over Pixo: it eats it, then the chat opens with it attached.</summary>
    void DropAskTake(List<string> files, string? text)
    {
        _dropAsk = false;
        _dropFace?.Play(NotchFace.Emote.Gulp, 1.0);
        FeelSound(Sfx.Cue.Eat);
        LeaveDropZone();
        // after the drop's own work is over (Windows is still finishing it), and once the notch has settled
        After(260, async () =>
        {
            try
            {
                if (_pop != null) _pop.Activate();
                else
                {
                    OpenLauncher(null);
                    if (!_launcherOpen) return;
                    StartSession(null); // a conversation of its own
                    EnterChat(null);
                }
                int attached = 0;
                foreach (var path in files.Take(5))
                {
                    if (PictureExts.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) { if (AddPictureFile(path)) attached++; continue; }
                    string body = await Task.Run(() => ReadAnyText(path));
                    if (body.StartsWith("Error") || body.StartsWith("This is not a text file")) { Toast("Can't read " + System.IO.Path.GetFileName(path) + " as text"); continue; }
                    if (body.Length > 24000) body = body[..24000] + "\n…(cut)";
                    _chatAttach = (System.IO.Path.GetFileName(path), body);
                    ShowAttachments();
                    attached++;
                }
                if (files.Count == 0 && text is { Length: > 0 })
                {
                    _chatAttach = ("Dropped text", text.Length > 24000 ? text[..24000] + "\n…(cut)" : text);
                    ShowAttachments();
                    attached++;
                }
                if (_launchBox == null || attached == 0) return;
                // a first question, ready: Enter sends it, typing replaces it
                bool picture = files.Count > 0 && files.All(f => PictureExts.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()));
                _launchBox.Text = picture ? "What's in this picture?" : files.Count > 1 ? "What are these about, in a few lines?" : "What is this about, in a few lines?";
                _launchBox.SelectAll();
                _launchBox.Focus();
            }
            catch (Exception ex) { AppLog.Write("drop to ask: " + ex.Message); }
        });
    }
}
