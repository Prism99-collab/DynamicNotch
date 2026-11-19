using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The AI, honest about what it does on your PC:
///  • messages are sent with send_message, which opens the conversation, types in the real message box, sends, and
///    then reads the chat to see the message there; it says "sent" only when it saw it;
///  • it never writes to people unless your message asks it to send something, and never types through PowerShell
///    (that can land in any window);
///  • an answer that says "done" after typing or clicking that wasn't checked on screen says so;
///  • "what's on my screen" gets a real picture of the screen to look at;
///  • the battery and PC cards only show when the question is about them.
/// </summary>
public partial class NotchWindow
{
    static readonly Regex ChatApps = new(@"discord|whatsapp|teams|slack|telegram|messenger|signal|skype|instagram|wechat|viber|\bline\b|outlook|gmail|mail\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SendIntent = new(@"\b(send|sent|message|msg|dm|text|tell|say|write|reply|answer|post|ping|envoie|envoyer|envoi|écris|ecris|dis[- ]lui|dis|réponds|reponds|b3at|sift|seft|kteb|ktb|goul|gol|qol|ab3at|siftlo|b3atlo)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Did the message being answered ask to send something to someone?</summary>
    bool UserAskedToSend => SendIntent.IsMatch(_chatLastUser);

    /// <summary>Typing or clicking happened since the last look at the screen (so "done" isn't checked yet).</summary>
    bool _actUnchecked;

    // ───────────────────────────── sending a message, checked ─────────────────────────────

    static readonly Regex MessageBoxName = new(@"^(message\b|type a message|write a message|send a message|enter a message|new message|reply|écrire|ecrire|écris|tapez|taper un message|saisi(r|ssez) un message|envoyer un message|rédiger|escribe|escribir un mensaje|mensaje|nachricht|scrivi|messaggio)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SearchBoxName = new(@"^(search|rechercher|recherche|buscar|suchen|cerca|pesquisar|find|search or start)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    async Task<string> SendMessage(string app, string to, string text, CancellationToken cancel)
    {
        if (!UserAskedToSend) return "Error: the user didn't ask to send a message. Never write to people unless asked.";
        text = text.Trim();
        to = to.Trim();
        if (text.Length == 0) return "Error: what should the message say?";
        string a = app.Trim().ToLowerInvariant();
        var hwnd = FindUiWindow(a);
        if (hwnd == IntPtr.Zero) return $"Error: {app} isn't open. Open it (open), wait a few seconds, then send_message again.";
        _uiHwnd = hwnd;
        _uiSeen.Clear();
        if (!await Front(hwnd)) return "Error: couldn't bring it to the front, so nothing was sent.";

        bool quickSwitch = Regex.IsMatch(a, "discord|slack|teams");
        if (to.Length > 0)
        {
            if (quickSwitch)
            {
                // Discord, Slack, Teams: Ctrl+K jumps to a person or a channel
                PressCombo("ctrl+k");
                await Task.Delay(450, cancel);
                TypeText(to);
                await Task.Delay(1000, cancel);
                PressCombo("enter");
                await Task.Delay(1200, cancel);
            }
            else
            {
                // WhatsApp, Telegram, Messenger…: the search box, then the person in the list
                var box = await Limited(() => FindKind(hwnd, SearchBoxName, editable: true), null);
                if (box == null) return $"Error: couldn't find the search box to open the chat with {to}. Use use_app look, then click the search box.";
                await Limited(() => { try { box.SetFocus(); } catch { Press(box, "edit"); } return true; }, false);
                await Task.Delay(200, cancel);
                PressCombo("ctrl+a");
                TypeText(to);
                await Task.Delay(1600, cancel);
                var person = await Limited(() => FindPerson(hwnd, to), null);
                if (person == null) return $"Error: no chat called “{to}” showed up in the search. Nothing was sent.";
                await Limited(() => Press(person, "list item"), "");
                await Task.Delay(1300, cancel);
            }
        }
        if (!StillFront(hwnd)) return "Error: the app lost the front, so nothing was sent.";

        // where the message is written
        var input = await Limited(() => FindKind(hwnd, MessageBoxName, editable: true), null);
        if (input != null) await Limited(() => { try { input.SetFocus(); } catch { Press(input, "edit"); } return true; }, false);
        else if (!quickSwitch) return "Error: couldn't find where to type the message. Nothing was sent.";
        await Task.Delay(200, cancel);
        if (!StillFront(hwnd)) return "Error: the app lost the front, so nothing was sent.";
        TypeText(text);
        await Task.Delay(250, cancel);
        PressCombo("enter");
        await Task.Delay(1300, cancel);

        // checked: is it in the conversation now?
        bool seen = await Limited(() => MessageVisible(hwnd, text), false);
        _actUnchecked = false;
        return seen
            ? $"Done: sent{(to.Length > 0 ? " to " + to : "")}: “{text}” (it's in the chat now)."
            : $"Typed and sent with Enter{(to.Length > 0 ? " to " + to : "")}, but the message couldn't be seen in the chat afterwards. Tell the user it isn't confirmed and to check.";
    }

    /// <summary>A box (search, message) by its name, the last one on the page (the message box is at the bottom).</summary>
    static AutomationElement? FindKind(IntPtr hwnd, Regex name, bool editable)
    {
        AutomationElement? found = null;
        foreach (var (el, kind, n) in Elements(hwnd))
            if ((!editable || kind is "edit" or "document" or "combo box") && name.IsMatch(n)) found = el;
        return found;
    }

    /// <summary>The person's line in a list of chats (not the search box with their name in it).</summary>
    static AutomationElement? FindPerson(IntPtr hwnd, string who)
    {
        string w = who.ToLowerInvariant();
        (AutomationElement El, int Rank)? best = null;
        foreach (var (el, kind, n) in Elements(hwnd))
        {
            if (kind is "edit" or "document" or "combo box") continue;
            if (!n.Contains(w, StringComparison.OrdinalIgnoreCase)) continue;
            int rank = kind switch { "list item" => 0, "hyperlink" => 1, "button" => 2, "tree item" => 2, _ => 3 } + (n.StartsWith(who, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
            if (best == null || rank < best.Value.Rank) best = (el, rank);
        }
        return best?.El;
    }

    static bool MessageVisible(IntPtr hwnd, string text)
    {
        string probe = Regex.Replace(text, @"\s+", " ").Trim();
        if (probe.Length > 24) probe = probe[..24];
        foreach (var (_, kind, n) in Elements(hwnd))
            if (kind is not ("edit" or "combo box") && Regex.Replace(n, @"\s+", " ").Contains(probe, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ───────────────────────────── the screen, as a picture ─────────────────────────────

    static readonly Regex ScreenAsk = new(@"what('?s| is| do (you|u) see| u see| do u see|'?re you seeing| can (you|u) see)?\s+(on|in)\s+(my|the)\s+screen|what do (you|u) see|look at (my|the) screen|see my screen|check my screen|my screen now|regarde (mon )?(é|e)cran|qu'?est[- ]ce que tu vois|tu vois quoi|chouf (l'?)?(ecran|screen)|chno kayn f (l'?)?(ecran|screen)|describe (my|the) screen|(see|seeing|look|looking|vois|voir|regarde|chouf|show|shows|showing|on).{0,40}(screen|[ée]cran)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The whole screen as a picture to send (the notch steps aside for the shot).</summary>
    async Task<byte[]?> CaptureScreen()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return null;
        double was = Root.Opacity, popWas = _pop?.Opacity ?? 1;
        Root.Opacity = 0;
        if (_pop != null) _pop.Opacity = 0;
        await Task.Delay(160);
        try
        {
            var b = screen.Bounds;
            using var bmp = new System.Drawing.Bitmap(b.Width, b.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(b.Left, b.Top, 0, 0, bmp.Size);
            var hbmp = bmp.GetHbitmap();
            try { return Shrink(System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions())); }
            finally { DeleteObject(hbmp); }
        }
        catch { return null; }
        finally
        {
            Root.Opacity = was;
            if (_pop != null) _pop.Opacity = popWas;
        }
    }

    // ───────────────────────────── the answer, kept honest ─────────────────────────────

    static readonly Regex BatteryAsk = new(@"batter|charge|gear|setup|devices?|mouse|headset|casque|souris|earbuds|controller|bluetooth", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PcAsk = new(@"\bpc\b|computer|laptop|ordi|status|état|cpu|processor|ram|memory|mémoire|disk|disque|storage|slow|lent|performance|health|santé|system", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SaysDone = new(@"\b(sent|envoy|posted|played|playing|done|clicked|typed|opened|launched|started|closed|changed|set to|turned|lancé|ouvert|fait)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Cards only for questions about them; and a "done" that wasn't checked on screen says so.</summary>
    string Honest(string said)
    {
        if (said.Contains("[[card:batteries]]") && !BatteryAsk.IsMatch(_chatLastUser)) said = said.Replace("[[card:batteries]]", "");
        if (said.Contains("[[card:pc]]") && !PcAsk.IsMatch(_chatLastUser)) said = said.Replace("[[card:pc]]", "");
        said = Regex.Replace(said, @"\n{3,}", "\n\n").Trim();
        if (_actUnchecked && SaysDone.IsMatch(said) && !said.Contains("not checked", StringComparison.OrdinalIgnoreCase))
            said += "\n\n_Not checked on screen: look to be sure._";
        return said;
    }
}
