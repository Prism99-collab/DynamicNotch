using System.Windows.Automation;

namespace DynamicNotch.Services;

/// <summary>
/// Leaves a call in any app by pressing the app's own leave button (Disconnect, Leave, End call, Hang up…), found
/// through Windows' accessibility tree: no keys typed, no window brought forward. Only buttons whose name is exactly
/// one of those words (in a few languages) are pressed, and only in the call's own windows. Apps that ask "leave
/// the meeting?" get that confirmed too.
/// </summary>
public static class CallLeave
{
    static readonly HashSet<string> Leave = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "Disconnect", "Leave", "Leave call", "Leave meeting", "Hang up", "End call", "Leave the call",
        // French
        "Déconnecter", "Se déconnecter", "Quitter", "Quitter l'appel", "Quitter l’appel", "Quitter la réunion", "Raccrocher", "Terminer l'appel", "Terminer l’appel", "Mettre fin à l'appel", "Mettre fin à l’appel",
        // Spanish, Portuguese, Italian, German
        "Desconectar", "Salir", "Salir de la llamada", "Colgar", "Finalizar llamada", "Abandonar la llamada",
        "Sair", "Sair da chamada", "Desligar", "Encerrar chamada",
        "Disconnetti", "Esci", "Abbandona chiamata", "Riaggancia", "Termina chiamata",
        "Trennen", "Verlassen", "Anruf verlassen", "Auflegen", "Anruf beenden", "Besprechung verlassen",
        // Arabic
        "قطع الاتصال", "مغادرة", "مغادرة المكالمة", "إنهاء المكالمة", "مغادرة الاجتماع",
    };

    /// <summary>The "are you sure?" step some apps add (Zoom: Leave → Leave meeting).</summary>
    static readonly HashSet<string> Confirm = new(StringComparer.OrdinalIgnoreCase)
    {
        "Leave meeting", "Leave Meeting", "Leave call", "End call", "Quitter la réunion", "Quitter l'appel", "Quitter l’appel",
        "Salir de la reunión", "Sair da reunião", "Abbandona riunione", "Besprechung verlassen", "مغادرة الاجتماع",
    };

    /// <summary>Presses the leave button in one of the windows; its name, or null when none was found.</summary>
    public static string? Press(IReadOnlyList<IntPtr> windows)
    {
        // the first look can come back empty in Chromium-based apps (Discord, Teams, browsers): their accessibility
        // tree is only built once someone asks for it, so a second look a moment later finds it
        for (int attempt = 0; attempt < 3; attempt++)
        {
            foreach (var w in windows)
                if (Find(w, Leave) is { } button && Invoke(button) is { } name)
                {
                    ConfirmIfAsked(windows);
                    return name;
                }
            Thread.Sleep(350);
        }
        return null;
    }

    static void ConfirmIfAsked(IReadOnlyList<IntPtr> windows)
    {
        for (int i = 0; i < 4; i++)
        {
            Thread.Sleep(300);
            foreach (var w in windows)
                if (Find(w, Confirm) is { } button) { Invoke(button); return; }
        }
    }

    static AutomationElement? Find(IntPtr window, HashSet<string> names)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var buttons = root.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true)));
            AutomationElement? hidden = null;
            foreach (AutomationElement b in buttons)
            {
                if (!names.Contains(Clean(b.Current.Name))) continue;
                if (!b.Current.IsOffscreen) return b;
                hidden ??= b;
            }
            return hidden;
        }
        catch { return null; }
    }

    static string? Invoke(AutomationElement b)
    {
        try
        {
            string name = b.Current.Name;
            if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) { ((InvokePattern)p).Invoke(); return name; }
            if (b.TryGetCurrentPattern(TogglePattern.Pattern, out var t)) { ((TogglePattern)t).Toggle(); return name; }
        }
        catch { }
        return null;
    }

    /// <summary>"Leave (Ctrl+Shift+H)" → "Leave"; stray spaces and dots trimmed.</summary>
    static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int cut = s.IndexOf(" (", StringComparison.Ordinal);
        if (cut > 0) s = s[..cut];
        return s.Replace(' ', ' ').Trim().TrimEnd('.', '…').Trim();
    }
}
