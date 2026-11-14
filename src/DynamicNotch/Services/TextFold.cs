using System.Globalization;
using System.Text;

namespace DynamicNotch.Services;

/// <summary>
/// How the search compares text: lower case, accents gone ("Résumé" is "resume"), one character for one character
/// (so the letters you typed can still be lit up in the original name). And the initials of a name, word starts and
/// CamelCase humps alike ("InvoiceCalc 2026.xlsx" → "ic2x"), so a few capitals find it.
/// </summary>
public static class TextFold
{
    static readonly char[] Table = new char[65536]; // filled as characters are met; writes are idempotent, so threads may share it

    public static char Fold(char c)
    {
        if (c < 128) return c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        char f = Table[c];
        if (f != '\0') return f;
        char lower = char.ToLowerInvariant(c);
        string d = lower.ToString().Normalize(NormalizationForm.FormD);
        f = d.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark ? d[0] : lower;
        Table[c] = f;
        return f;
    }

    /// <summary>Folded text, the same length as the original.</summary>
    public static string Fold(string s)
    {
        int i = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 128 || c is >= 'A' and <= 'Z') break;
        }
        if (i == s.Length) return s; // already plain lower case: nothing to copy
        return string.Create(s.Length, s, (span, src) => { for (int k = 0; k < src.Length; k++) span[k] = Fold(src[k]); });
    }

    /// <summary>The first letter of every word and CamelCase hump, folded: "Visual Studio Code" → "vsc", "InvoiceCalc" → "ic".</summary>
    public static string Initials(string name)
    {
        var sb = new StringBuilder(8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c)) continue;
            bool start = i == 0 || !char.IsLetterOrDigit(name[i - 1])
                || char.IsUpper(c) && char.IsLower(name[i - 1])                    // camelCase
                || char.IsDigit(c) != char.IsDigit(name[i - 1]);                    // "file2026": the number starts a word
            if (start) sb.Append(Fold(c));
        }
        return sb.ToString();
    }
}
