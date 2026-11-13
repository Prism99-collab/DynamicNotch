using System.Globalization;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services;

/// <summary>
/// Answers the notch's search gives on its own, before any app or file: a calculation ("340*12%", "sqrt(2)^3"),
/// a conversion ("5 km in miles", "72 f to c", "3.5 gb in mb"), a colour ("#ff2e88"), a web address ("github.com").
/// All worked out on the PC, instantly, nothing sent anywhere.
/// </summary>
public static class LauncherAnswers
{
    public sealed record Answer(string Title, string Detail, string Copy, string? Color = null, string? Url = null, bool Secret = false);

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Answer? For(string query)
    {
        // "what is 15% of 80", "calculate 2^10", "convert 5 km to miles": the words around the sum don't matter
        string q = Regex.Replace(query.Trim(), @"^(?:what(?:'s| is)|whats|calculate|calc|compute|how much is|convert|solve)\s+", "", RegexOptions.IgnoreCase).TrimEnd('?', ' ');
        if (q.Length == 0) return null;
        return Colour(q) ?? Url(q) ?? Convert(q) ?? Calculate(q);
    }

    // ───── colours

    static Answer? Colour(string q)
    {
        var m = Regex.Match(q, @"^#?([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$");
        if (!m.Success || q[0] != '#' && !Regex.IsMatch(q, "[a-fA-F]")) return null;
        string h = m.Groups[1].Value;
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        int r = System.Convert.ToInt32(h[..2], 16), g = System.Convert.ToInt32(h[2..4], 16), b = System.Convert.ToInt32(h[4..], 16);
        string hex = "#" + h.ToUpperInvariant();
        return new Answer(hex, $"rgb({r}, {g}, {b}) · Enter copies", hex, Color: hex);
    }

    // ───── web addresses

    static Answer? Url(string q)
    {
        if (q.Contains(' ') || !Regex.IsMatch(q, @"^(https?://)?([a-z0-9-]+\.)+[a-z]{2,}(/\S*)?$", RegexOptions.IgnoreCase)) return null;
        if (Regex.IsMatch(q, @"^\d+(\.\d+)+$")) return null; // a number like 1.5.2
        string url = q.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? q : "https://" + q;
        return new Answer(q, "Open in your browser", url, Url: url);
    }

    // ───── conversions

    sealed record Unit(string[] Names, string Group, double Factor, string Label);

    static readonly Unit[] Units =
    {
        new(new[] { "mm", "millimeter", "millimeters", "millimetre", "millimetres" }, "len", 0.001, "mm"),
        new(new[] { "cm", "centimeter", "centimeters", "centimetre", "centimetres" }, "len", 0.01, "cm"),
        new(new[] { "m", "meter", "meters", "metre", "metres" }, "len", 1, "m"),
        new(new[] { "km", "kilometer", "kilometers", "kilometre", "kilometres" }, "len", 1000, "km"),
        new(new[] { "in", "inch", "inches", "\"" }, "len", 0.0254, "in"),
        new(new[] { "ft", "foot", "feet", "'" }, "len", 0.3048, "ft"),
        new(new[] { "yd", "yard", "yards" }, "len", 0.9144, "yd"),
        new(new[] { "mi", "mile", "miles" }, "len", 1609.344, "mi"),
        new(new[] { "mg", "milligram", "milligrams" }, "mass", 0.000001, "mg"),
        new(new[] { "g", "gram", "grams" }, "mass", 0.001, "g"),
        new(new[] { "kg", "kilo", "kilos", "kilogram", "kilograms" }, "mass", 1, "kg"),
        new(new[] { "t", "ton", "tons", "tonne", "tonnes" }, "mass", 1000, "t"),
        new(new[] { "oz", "ounce", "ounces" }, "mass", 0.028349523125, "oz"),
        new(new[] { "lb", "lbs", "pound", "pounds" }, "mass", 0.45359237, "lb"),
        new(new[] { "ml", "milliliter", "milliliters", "millilitre" }, "vol", 0.001, "ml"),
        new(new[] { "l", "liter", "liters", "litre", "litres" }, "vol", 1, "L"),
        new(new[] { "gal", "gallon", "gallons" }, "vol", 3.785411784, "gal"),
        new(new[] { "cup", "cups" }, "vol", 0.2365882365, "cups"),
        new(new[] { "b", "byte", "bytes" }, "data", 1, "B"),
        new(new[] { "kb", "kilobyte", "kilobytes" }, "data", 1024, "KB"),
        new(new[] { "mb", "megabyte", "megabytes" }, "data", 1024d * 1024, "MB"),
        new(new[] { "gb", "gigabyte", "gigabytes" }, "data", 1024d * 1024 * 1024, "GB"),
        new(new[] { "tb", "terabyte", "terabytes" }, "data", 1024d * 1024 * 1024 * 1024, "TB"),
        new(new[] { "ms", "millisecond", "milliseconds" }, "time", 0.001, "ms"),
        new(new[] { "s", "sec", "secs", "second", "seconds" }, "time", 1, "s"),
        new(new[] { "min", "mins", "minute", "minutes" }, "time", 60, "min"),
        new(new[] { "h", "hr", "hrs", "hour", "hours" }, "time", 3600, "h"),
        new(new[] { "day", "days" }, "time", 86400, "days"),
        new(new[] { "week", "weeks" }, "time", 604800, "weeks"),
        new(new[] { "kmh", "km/h", "kph" }, "speed", 1 / 3.6, "km/h"),
        new(new[] { "mph" }, "speed", 0.44704, "mph"),
        new(new[] { "m/s", "mps" }, "speed", 1, "m/s"),
        new(new[] { "c", "°c", "celsius" }, "temp", 0, "°C"),
        new(new[] { "f", "°f", "fahrenheit" }, "temp", 0, "°F"),
        new(new[] { "k", "kelvin" }, "temp", 0, "K"),
    };

    static Unit? FindUnit(string s) => Units.FirstOrDefault(u => u.Names.Contains(s.Trim().ToLowerInvariant()));

    static Answer? Convert(string q)
    {
        var m = Regex.Match(q, @"^\s*(-?[\d.,]+)\s*([a-z°/""']+)\s+(?:to|in|into|as|en|=)\s+([a-z°/""']+)\s*$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, Inv, out double v)) return null;
        if (FindUnit(m.Groups[2].Value) is not { } from || FindUnit(m.Groups[3].Value) is not { } to || from.Group != to.Group) return null;
        double result = from.Group == "temp" ? Temp(v, from.Label, to.Label) : v * from.Factor / to.Factor;
        string text = Nice(result), raw = Raw(result);
        return new Answer($"{text} {to.Label}", $"{Nice(v)} {from.Label} · Enter copies {raw}", raw);
    }

    static double Temp(double v, string from, string to)
    {
        double c = from switch { "°F" => (v - 32) * 5 / 9, "K" => v - 273.15, _ => v };
        return to switch { "°F" => c * 9 / 5 + 32, "K" => c + 273.15, _ => c };
    }

    // ───── calculations

    static Answer? Calculate(string q)
    {
        string e = q.Replace('×', '*').Replace('÷', '/').Replace(',', '.').Replace(" ", "");
        if (e.StartsWith('=')) e = e[1..];
        if (Regex.IsMatch(e, @"^\d{4}-\d{1,2}-\d{1,2}$")) return null; // a date, not a sum
        // "15% of 80" → 15/100*80
        e = Regex.Replace(e, @"([\d.]+)%of", "($1/100)*", RegexOptions.IgnoreCase);
        if (!Regex.IsMatch(e, @"^[\d.+\-*/^%()a-z]+$", RegexOptions.IgnoreCase)) return null;
        if (!Regex.IsMatch(e.TrimStart('-', '+'), @"[+\-*/^%(]|sqrt|pi|sin|cos|tan|log|ln|abs")) return null; // a lone number isn't a sum
        if (!Regex.IsMatch(e, @"\d|pi")) return null;
        try
        {
            var p = new Parser(e);
            double r = p.Expression();
            if (!p.Done || double.IsNaN(r) || double.IsInfinity(r)) return null;
            return new Answer($"= {Nice(r)}", "Enter copies the result", Raw(r));
        }
        catch { return null; }
    }

    /// <summary>For the clipboard: no thousands separators, so it pastes as a number anywhere.</summary>
    static string Raw(double v) => Math.Abs(v) >= 1e15 || Math.Abs(v) < 1e-9 && v != 0 ? v.ToString("G6", Inv) : Math.Round(v, 6).ToString("0.######", Inv);

    static string Nice(double v)
    {
        if (Math.Abs(v) >= 1e15 || Math.Abs(v) < 1e-9 && v != 0) return v.ToString("G6", Inv);
        return Math.Round(v, 6).ToString("#,0.######", Inv);
    }

    /// <summary>+ − × ÷, ^ powers, % (of what's before it: 340*12% = 40.8), brackets, sqrt sin cos tan log ln abs, pi.</summary>
    sealed class Parser
    {
        readonly string _s;
        int _i;
        public Parser(string s) => _s = s.ToLowerInvariant();
        public bool Done => _i >= _s.Length;
        char Peek => _i < _s.Length ? _s[_i] : '\0';

        public double Expression()
        {
            double v = Term();
            while (Peek is '+' or '-')
            {
                char op = _s[_i++];
                double t = Term();
                // "200 + 10%" means 200 plus 10 % of 200
                if (_lastWasPercent) { t = v * t; _lastWasPercent = false; }
                v = op == '+' ? v + t : v - t;
            }
            return v;
        }

        bool _lastWasPercent;

        double Term()
        {
            double v = Power();
            while (Peek is '*' or '/')
            {
                char op = _s[_i++];
                double f = Power();
                _lastWasPercent = false;
                v = op == '*' ? v * f : v / f;
            }
            return v;
        }

        double Power()
        {
            double b = Unary();
            if (Peek == '^') { _i++; return Math.Pow(b, Power()); }
            return b;
        }

        double Unary()
        {
            if (Peek == '-') { _i++; return -Unary(); }
            if (Peek == '+') { _i++; return Unary(); }
            double v = Atom();
            _lastWasPercent = false;
            if (Peek == '%') { _i++; v /= 100; _lastWasPercent = true; }
            return v;
        }

        double Atom()
        {
            if (Peek == '(')
            {
                _i++;
                double v = Expression();
                if (Peek != ')') throw new FormatException();
                _i++;
                return v;
            }
            if (char.IsLetter(Peek))
            {
                int start = _i;
                while (char.IsLetter(Peek)) _i++;
                string name = _s[start.._i];
                if (name == "pi") return Math.PI;
                if (name == "e") return Math.E;
                double arg = Atom();
                return name switch
                {
                    "sqrt" => Math.Sqrt(arg), "sin" => Math.Sin(arg * Math.PI / 180), "cos" => Math.Cos(arg * Math.PI / 180),
                    "tan" => Math.Tan(arg * Math.PI / 180), "log" => Math.Log10(arg), "ln" => Math.Log(arg), "abs" => Math.Abs(arg),
                    _ => throw new FormatException(),
                };
            }
            int s0 = _i;
            while (char.IsDigit(Peek) || Peek == '.') _i++;
            if (s0 == _i) throw new FormatException();
            return double.Parse(_s[s0.._i], Inv);
        }
    }
}
