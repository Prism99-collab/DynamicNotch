using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch;

/// <summary>
/// Getting the message right before answering it:
///  • a short reply to the last answer ("no", "it's Brave not Chrome", "I meant…") is read as a correction of that
///    answer, not as an order: a name in it is never a reason to open or close something;
///  • a correction and "what's on my screen" go to the smart model;
///  • "what's on my screen" comes with the real names of the apps on screen, read from Windows, so the answer never
///    has to guess an app from how it looks (Brave and Chrome look alike).
/// </summary>
public partial class NotchWindow
{
    /// <summary>How every answer sounds: a person, not a search engine.</summary>
    const string SoundHuman = "Sound like a smart, friendly person texting, not a search engine or a dictionary: plain natural sentences, in the user's language, alphabet and tone (Darija or Arabic typed in Latin letters gets Latin letters back; casual if they're casual, slang if they use it). Lists, bold and headings only when they make a real answer clearer. Never answer a greeting, a feeling or chit-chat with facts, definitions or sources.";

    // small talk: every word is one of these (and at least one is a greeting or a "how are you")
    static readonly HashSet<string> TalkCore = new(StringComparer.OrdinalIgnoreCase)
    {
        "hey", "heyy", "heyyy", "hi", "hii", "hello", "helo", "yo", "yoo", "sup", "wassup", "wsp", "wsg", "hru", "wyd", "gm", "gn", "morning", "thanks", "thank", "thx",
        "ty", "tysm", "lol", "lmao", "haha", "hahaha", "salut", "cv", "bonjour", "bonsoir", "merci", "coucou", "cc", "salam", "slm", "labas", "lbas", "kidayr", "kidayra",
        "wach", "bkhir", "hamdullah", "lhamdullah", "sava", "howdy", "hiya", "greetings", "goodnight", "cheers", "nice", "cool",
    };
    static readonly HashSet<string> TalkFill = new(StringComparer.OrdinalIgnoreCase)
    {
        "how", "are", "r", "you", "u", "ya", "doing", "is", "it", "going", "was", "your", "ur", "day", "what's", "whats", "what", "up", "good", "evening", "night",
        "afternoon", "today", "tonight", "bro", "bruh", "man", "dude", "fam", "mate", "buddy", "again", "tho", "though", "very", "much", "so", "a", "lot", "and",
        "too", "im", "i'm", "i", "am", "fine", "well", "ok", "okay", "great", "all", "there", "my", "friend", "ça", "ca", "va", "toi", "et", "tu", "bien", "très", "tres", "ai", "notch",
        "the", "to", "be", "sir", "doin", "everything", "life", "things", "hows", "how's", "nm", "nothing", "much", "same", "yes", "yeah", "yep", "no", "nah",
    };

    /// <summary>"hey hru today", "thanks bro", "good morning", "ça va?": a chat, not a question to research.</summary>
    static bool IsSmallTalk(string text)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}']+").Select(m => m.Value.Trim('\'')).Where(w => w.Length > 0).ToList();
        if (words.Count == 0 || words.Count > 9) return false;
        bool core = words.Any(TalkCore.Contains) || Regex.IsMatch(text, @"\bhow (are|r) (you|u)\b|\bhow'?s it going\b|\bwhat'?s up\b|\bça va\b|\bca va\b|\bgood (morning|evening|night|afternoon)\b", RegexOptions.IgnoreCase);
        return core && words.All(w => TalkCore.Contains(w) || TalkFill.Contains(w));
    }

    /// <summary>Moroccan Darija words, as typed in Latin letters.</summary>
    static readonly Regex DarijaWords = new(@"\b(salam|slm|labas|lbas|wach|wesh|kidayr|kidayra|kidayrin|chno|chnou|achno|bghit|bghiti|dyal|dyali|dyalk|mzyan|mezyan|walo|safi|wakha|3afak|zwin|khoya|sahbi|hamdullah|lhamdullah|bzaf|daba|ghir|fin|m3ak|m3a|nta|nti|ana|3lach|kifach|imta|chhal|bslama|tbarkallah|inchallah)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Every letter in the message is a Latin one (English, French, Darija typed in Latin…).</summary>
    static bool LatinOnly(string text) => Regex.IsMatch(text, @"\p{L}") && !Regex.IsMatch(text, @"[^\u0000-ɏḀ-ỿ\s\p{P}\p{S}\p{N}]");

    /// <summary>Told to the AI with every message.</summary>
    const string ReadTheConversation = "Read each message with the conversation before it: a short reply right after your answer (no, wrong, it's X not Y, I meant…, thanks) is about that answer. Never take an app or a name in it as an order to open, close or play something. Do something on the PC only when the message asks for it. When you're unsure what's meant, ask one short question instead of guessing.";

    static readonly Regex CorrectionWords = new(
        @"^\s*(no+|nope|nah|wrong|incorrect|not really|not quite|not that|that'?s (not|wrong|incorrect)|thats (not|wrong)|you'?re wrong|ur wrong|youre wrong|actually|i meant|i mean\b|i said|i didn'?t (say|ask|mean)|not what i (asked|meant|said)|non\b|c'?est pas|pas [çc]a|machi|mashi)\b"
        + @"|\b(it'?s|its|it is|that'?s|thats|this is|c'?est|rah)\s+(a |an |the )?[\w.'-]+(\s[\w.'-]+){0,3}\s*,?\s+(not|pas|machi|mashi)\s+(a |an |the )?\w+"
        + @"|^\s*(it'?s|its|that'?s|thats)\s+not\s+(a |an |the )?\w+\s*$"
        + @"|\bnot\s+\w+\s*,\s*(it'?s|its)\b|\bnot what i (asked|meant|said)\b|\byou (got it|are|r) wrong\b|\bthat'?s wrong\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Does this message correct the last answer (rather than ask something new)?</summary>
    static bool IsCorrection(string text) => text.Length <= 160 && CorrectionWords.IsMatch(text);

    [DllImport("user32.dll", EntryPoint = "IsIconic")] static extern bool IsMinimisedWindow(IntPtr hwnd);

    static readonly Dictionary<string, string> _realAppNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An app's real name from its file ("Brave Browser", "Discord"), not a guess from how it looks.</summary>
    static string AppNameOf(string path)
    {
        lock (_realAppNames)
        {
            if (_realAppNames.TryGetValue(path, out var known)) return known;
            string name = "";
            try { name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; } catch { }
            if (name.Length < 2) name = System.IO.Path.GetFileNameWithoutExtension(path);
            return _realAppNames[path] = name;
        }
    }

    /// <summary>
    /// What's on screen, read from Windows: the window in front (under the notch) and the others open, front to back,
    /// with their app's real name and title. Null when nothing could be read.
    /// </summary>
    string? ScreenFacts()
    {
        try
        {
            uint me = (uint)Environment.ProcessId;
            var seen = new List<string>();
            var pathOf = new Dictionary<uint, string?>();
            Interop.Native.EnumWindows((h, _) =>
            {
                if (seen.Count >= 7) return false;
                if (!Interop.Native.IsWindowVisible(h) || IsMinimisedWindow(h) || Interop.Native.GetWindow(h, 4) != IntPtr.Zero) return true;
                string title = Interop.Native.WindowTitle(h);
                if (title.Length == 0 || title is "Program Manager" or "Windows Input Experience" || title.Contains("Overlay", StringComparison.OrdinalIgnoreCase)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == me) return true;
                if (!pathOf.TryGetValue(pid, out var path)) pathOf[pid] = path = Interop.Native.ProcessPathById(pid);
                if (path == null) return true;
                string app = AppNameOf(path);
                if (app.Length == 0 || Regex.IsMatch(app, @"^(Windows Explorer|ApplicationFrameHost|TextInputHost|ShellExperienceHost)$", RegexOptions.IgnoreCase) && title.Length < 3) return true;
                if (title.Length > 90) title = title[..90] + "…";
                seen.Add($"{app} — “{title}”");
                return true;
            }, IntPtr.Zero);
            if (seen.Count == 0) return null;
            var sb = new StringBuilder("[On screen, read from Windows (exact app names: trust these over how things look in the picture). In front: ");
            sb.Append(seen[0]);
            if (seen.Count > 1) sb.Append(". Also open: ").Append(string.Join("; ", seen.Skip(1)));
            sb.Append(".]");
            return sb.ToString();
        }
        catch { return null; }
    }
}
