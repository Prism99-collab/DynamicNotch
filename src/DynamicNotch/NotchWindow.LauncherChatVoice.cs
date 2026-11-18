using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Talking to the AI: the mic beside the message box (or Ctrl+M) listens; your words appear as you speak (Windows'
/// dictation, free) or once you stop (Gemini: free with its key, any language; or OpenRouter, with credit); a pause ends it, and what you said is
/// sent. Answers to what you said are read aloud (or always, or never: Preferences), with a Windows voice that
/// speaks the answer's language; Esc, the speaker button, or talking again stops the voice.
/// </summary>
public partial class NotchWindow
{
    TextBlock? _micButton, _speakButton;
    StackPanel? _levelBars;
    VoiceRecorder? _recorder;
    Windows.Foundation.IAsyncOperation<Windows.Media.SpeechRecognition.SpeechRecognitionResult>? _dictation;
    Windows.Media.SpeechRecognition.SpeechRecognizer? _recognizer;
    bool _listening, _transcribing, _spokenTurn;
    string _beforeVoice = "", _lastHypothesis = "";

    // ───────────────────────────── the mic and the level ─────────────────────────────

    FrameworkElement BuildMic()
    {
        _micButton = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Width = 26, Height = 26, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand, ToolTip = "Talk  ·  Ctrl+M", VerticalAlignment = VerticalAlignment.Center, Foreground = DimBrush, Background = Brushes.Transparent, Margin = new Thickness(4, 0, 0, 0),
        };
        _micButton.MouseEnter += (_, _) => { if (!_listening) _micButton.Foreground = Frozen(ChatAccent); };
        _micButton.MouseLeave += (_, _) => { if (!_listening) _micButton.Foreground = DimBrush; };
        _micButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _micButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleListening(); };
        VoiceOut.Changed += speaking => Dispatcher.BeginInvoke(() => PaintSpeaker(speaking));
        return _micButton;
    }

    /// <summary>Five bars that dance with your voice, in the prompt's place while listening.</summary>
    FrameworkElement BuildLevelBars()
    {
        _levelBars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        for (int i = 0; i < 5; i++)
            _levelBars.Children.Add(new Rectangle { Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center, Fill = Brushes.White });
        return _levelBars;
    }

    void ShowLevel(double level)
    {
        if (_levelBars == null) return;
        var acc = ChatAccent;
        double[] shape = { 0.55, 0.85, 1, 0.8, 0.5 };
        for (int i = 0; i < _levelBars.Children.Count; i++)
        {
            var bar = (Rectangle)_levelBars.Children[i];
            double h = 4 + 16 * Math.Clamp(level * shape[i] * (0.8 + 0.4 * Random.Shared.NextDouble()), 0, 1);
            bar.Fill = Frozen(acc);
            bar.BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(70)));
        }
    }

    void PaintMic()
    {
        if (_micButton == null) return;
        _micButton.Visibility = _settings.AiChat && _settings.AiVoice ? Visibility.Visible : Visibility.Collapsed;
        _micButton.Text = _listening ? "" : "";
        _micButton.Foreground = _listening ? Frozen(Color.FromRgb(0xFF, 0x45, 0x3A)) : DimBrush;
        _micButton.ToolTip = _listening ? "Stop  ·  Ctrl+M" : _transcribing ? "Writing down what you said…" : "Talk  ·  Ctrl+M";
        if (_listening && !Eco)
        {
            var pulse = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(pulse, 30);
            _micButton.BeginAnimation(OpacityProperty, pulse);
        }
        else _micButton.BeginAnimation(OpacityProperty, null);
        if (_levelBars != null && _launchPrompt != null)
        {
            _levelBars.Visibility = _listening ? Visibility.Visible : Visibility.Collapsed;
            _launchPrompt.Visibility = _listening ? Visibility.Hidden : Visibility.Visible;
        }
    }

    // ───────────────────────────── listening ─────────────────────────────

    void ToggleListening()
    {
        if (_listening) { FinishListening(cancel: false); return; }
        StartListening();
    }

    async void StartListening()
    {
        if (_listening || _transcribing || _launchBox == null) return;
        VoiceOut.Stop();
        if (!_chatMode && _pop == null) EnterChat(null, resume: true); // talking is for the AI
        if (_chatView == ChatView.History) ShowChatView(ChatView.Chat);
        _listening = true;
        _spokenTurn = true;
        _beforeVoice = _launchBox.Text.Length > 0 ? _launchBox.Text.TrimEnd() + " " : "";
        if (_launchHint != null) _launchHint.Text = _cyber ? "LISTENING…" : "Listening…";
        PaintMic();
        ChatKeys();
        try
        {
            // the level, from our own recording (and, for OpenRouter, what's sent)
            _recorder = new VoiceRecorder();
            _recorder.Level += l => Dispatcher.BeginInvoke(() => ShowLevel(l));
            bool cloud = UseCloudVoice;
            if (cloud) _recorder.Ended += heard => Dispatcher.BeginInvoke(() => { if (_listening) FinishListening(cancel: !heard); });
            _recorder.Start();
            if (!cloud) await ListenWithWindows();
        }
        catch (Exception ex)
        {
            AppLog.Write("voice in: " + ex.Message);
            StopRecorder();
            _listening = false;
            PaintMic();
            ChatNote("", "The microphone isn't available: " + ex.Message, tone: Warn);
            StyleChatLine();
            ChatKeys();
        }
    }

    bool UseCloudVoice => _settings.AiVoiceEngine is "ai" or "openrouter";

    /// <summary>Windows' dictation: free; your words appear as you say them; it ends on a pause.</summary>
    async Task ListenWithWindows()
    {
        var lang = _settings.AiVoiceLanguage is { Length: > 0 } tag && tag != "auto"
            ? new Windows.Globalization.Language(tag) : Windows.Media.SpeechRecognition.SpeechRecognizer.SystemSpeechLanguage;
        _recognizer = new Windows.Media.SpeechRecognition.SpeechRecognizer(lang);
        _recognizer.Constraints.Add(new Windows.Media.SpeechRecognition.SpeechRecognitionTopicConstraint(Windows.Media.SpeechRecognition.SpeechRecognitionScenario.Dictation, "dictation"));
        _recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(7);
        _recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(1.3);
        _lastHypothesis = "";
        _recognizer.HypothesisGenerated += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (!_listening || _launchBox == null) return;
            _lastHypothesis = e.Hypothesis.Text;
            _launchBox.Text = _beforeVoice + e.Hypothesis.Text + "…";
            _launchBox.CaretIndex = _launchBox.Text.Length;
        });
        var compiled = await _recognizer.CompileConstraintsAsync();
        if (compiled.Status != Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success)
            throw new InvalidOperationException("dictation isn't available for " + lang.DisplayName + " (" + compiled.Status + ")");
        try
        {
            _dictation = _recognizer.RecognizeAsync();
            var result = await _dictation;
            bool ok = result.Status == Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success;
            // stopped early: what it had already shown counts
            string text = ok && result.Text.Trim().Length > 0 ? result.Text.Trim() : _lastHypothesis.Trim();
            Heard(text, ok || text.Length > 0 ? null : result.Status.ToString());
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80045509))
        {
            EndListeningUi();
            ChatNote("", "Windows needs “Online speech recognition” on to take dictation.", tone: Warn,
                action: ("Open the setting", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-speech") { UseShellExecute = true }); } catch { } }));
        }
        catch (TaskCanceledException) { EndListeningUi(); }
        catch (OperationCanceledException) { EndListeningUi(); }
    }

    /// <summary>Stop now (and use what was said), or cancel (Esc).</summary>
    async void FinishListening(bool cancel)
    {
        if (!_listening) return;
        if (_recognizer != null)
        {
            // Windows: stopping early still gives what it heard so far
            if (cancel) { try { _dictation?.Cancel(); } catch { } EndListeningUi(); if (_launchBox != null) _launchBox.Text = _beforeVoice.TrimEnd(); }
            else
            {
                try { await _recognizer.StopRecognitionAsync(); }
                catch { string heard = _lastHypothesis; try { _dictation?.Cancel(); } catch { } if (heard.Length > 0) Heard(heard, null); }
            }
            return;
        }
        // OpenRouter: the recording goes out to be written down
        var rec = _recorder;
        _listening = false;
        rec?.Stop();
        await Task.Delay(120);
        PaintMic();
        if (cancel || rec == null || !rec.Heard || rec.Length < TimeSpan.FromMilliseconds(400)) { StopRecorder(); StyleChatLine(); ChatKeys(); if (!cancel) Toast("Didn't hear anything"); return; }
        byte[] wav = rec.Wav();
        StopRecorder();
        _transcribing = true;
        if (_launchHint != null && _launchBox != null) { _launchBox.Text = ""; _launchHint.Text = _cyber ? "DECODING SPEECH…" : "Writing down what you said…"; }
        PaintMic();
        try
        {
            if (OpenRouter.LoadKey() is not { } key) { Heard("", "no key"); return; }
            string? model = await OpenRouter.AudioModel(ChatModel);
            if (model == null) { Heard("", "no model can hear speech right now"); return; }
            var (text, error) = await OpenRouter.Transcribe(key, model, wav, CancellationToken.None);
            if (error != null && error.Contains("credits", StringComparison.OrdinalIgnoreCase))
                error = OpenRouter.IsGemini ? error : "OpenRouter needs at least $0.50 of credit to hear audio. Switch voice to Windows or Gemini in Preferences → AI chat.";
            Heard(text, error);
        }
        finally { _transcribing = false; PaintMic(); }
    }

    /// <summary>What you said: into the box, and sent (unless Preferences say to check it first).</summary>
    void Heard(string text, string? error)
    {
        EndListeningUi();
        if (_launchBox == null) return;
        if (error != null && text.Length == 0)
        {
            _launchBox.Text = _beforeVoice.TrimEnd();
            if (error is "NoRecognition" or "TimeoutExceeded" or "UserCanceled") Toast("Didn't catch that");
            else ChatNote("", "Voice: " + error, tone: Warn);
            return;
        }
        string said = (_beforeVoice + text).Trim();
        _launchBox.Text = said;
        _launchBox.CaretIndex = said.Length;
        _launchBox.Focus();
        if (said.Length > 0 && _settings.AiVoiceSend) SendTyped();
    }

    void EndListeningUi()
    {
        _listening = false;
        try { _recognizer?.Dispose(); } catch { }
        _recognizer = null;
        _dictation = null;
        StopRecorder();
        PaintMic();
        StyleChatLine();
        ChatKeys();
    }

    void StopRecorder()
    {
        try { _recorder?.Stop(); } catch { }
        var r = _recorder;
        _recorder = null;
        if (r != null) _ = Task.Delay(300).ContinueWith(_ => r.Dispose());
    }

    // ───────────────────────────── speaking ─────────────────────────────

    /// <summary>Read the answer aloud when you spoke the question (or always / never, as you chose).</summary>
    void MaybeSpeak(string answer)
    {
        bool spoken = _spokenTurn;
        _spokenTurn = false;
        if (answer.Length == 0 || !_settings.AiVoice) return;
        if (_settings.AiSpeak == "always" || _settings.AiSpeak == "voice" && spoken) Speak(answer);
    }

    void Speak(string text) => _ = VoiceOut.Speak(text, _settings.AiVoiceId, _settings.AiVoiceRate);

    TextBlock BuildSpeakerButton()
    {
        _speakButton = HeadButton("", "Read answers aloud", () =>
        {
            if (VoiceOut.Speaking) { VoiceOut.Stop(); return; }
            _settings.AiSpeak = _settings.AiSpeak == "always" ? "voice" : "always";
            _settings.Save();
            PaintSpeaker(false);
            Toast(_settings.AiSpeak == "always" ? "Answers are read aloud" : "Read aloud only when you talk");
        });
        PaintSpeaker(false);
        return _speakButton;
    }

    void PaintSpeaker(bool speaking)
    {
        if (_speakButton == null) return;
        _speakButton.Visibility = _settings.AiVoice ? Visibility.Visible : Visibility.Collapsed;
        bool always = _settings.AiSpeak == "always";
        _speakButton.Text = speaking ? "" : always ? "" : "";
        _speakButton.ToolTip = speaking ? "Stop reading  ·  Esc" : always ? "Answers are read aloud · click to read them only when you talk" : "Answers are read aloud when you talk · click to always read them";
        _speakButton.Foreground = speaking || always ? Frozen(ChatAccent) : DimBrush;
        if (speaking && !Eco)
        {
            var pulse = new DoubleAnimation(1, 0.5, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(pulse, 30);
            _speakButton.BeginAnimation(OpacityProperty, pulse);
        }
        else _speakButton.BeginAnimation(OpacityProperty, null);
    }
}
