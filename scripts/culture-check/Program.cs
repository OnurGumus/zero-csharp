// Compares zerocore's numbers with a NumberFormatInfo of your own (namespace
// ZeroTest here) with .NET's: random NumberFormatInfos made the same way on
// both sides (bad values too, to compare the exceptions), then formatting
// and parsing integers, doubles and decimals with them.
// Usage: check-culture.sh [iterations] [seed]
using System;
using System.Text;
using G = System.Globalization;
using Z = ZeroTest;

static unsafe class Program
{
    static Random R = new Random(12345);
    static int failures;
    static long checks;
    static int shown;
    static readonly System.Collections.Generic.Dictionary<string, int> perLabel = new();

    static string Run(Func<string> f)
    {
        try { return f(); }
        catch (Exception e) { return "!" + e.GetType().Name + ": " + e.Message; }
    }

    static void Same(string label, string input, Func<string> zero, Func<string> net)
    {
        checks++;
        string a = Run(zero), b = Run(net);
        if (a == b)
            return;
        failures++;
        perLabel.TryGetValue(label, out int n);
        perLabel[label] = n + 1;
        if (n < 4 && shown < 200)
        {
            shown++;
            Console.WriteLine($"FAIL {label} [{Escape(input)}]\n  zero: {Escape(a)}\n  .NET: {Escape(b)}");
        }
    }

    static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            sb.Append(c >= 0x20 && c < 0x7F ? c.ToString() : $"\\u{(int)c:X4}");
        return sb.ToString();
    }

    static readonly string[] Separators = { ".", ",", "\u00A0", "\u202F", " ", "'", "\u066B", "::", "", "x", "\u2019" };
    static readonly string[] Signs = { "-", "+", "\u2212", "minus ", "\u2012", "--", "", "~" };
    static readonly string[] Symbols = { "$", "\u20AC", "kr", "\u00A4", "TL", "", "\u00A0\u20AC", "%", "pct", "\u2030" };
    static readonly string[] Words = { "NaN", "nan", "\u221E", "-\u221E", "Infinity", "-Infinity", "inf", "Ninguno", "" };
    static readonly int[][] Groups = { new[] { 3 }, new[] { 3, 2 }, new[] { 1, 2, 0 }, new int[0], new[] { 0 }, new[] { 4, 0 }, new[] { 2 }, new[] { 9, 1 }, new[] { 3, 0 } };

    static string Pick(string[] options) => options[R.Next(options.Length)];

    // A random setting applied to both, or a bad value (comparing the
    // exceptions); returns its description.
    static string Configure(Z.NumberFormatInfo z, G.NumberFormatInfo s)
    {
        var log = new StringBuilder();
        int settings = R.Next(1, 12);
        for (int k = 0; k < settings; k++)
        {
            int which = R.Next(25);
            bool bad = R.Next(12) == 0;
            string text = bad ? (R.Next(2) == 0 ? null : "") : null;
            int number = bad ? (R.Next(2) == 0 ? -1 : 100) : 0;
            int[] groups = bad ? (R.Next(2) == 0 ? new[] { 10 } : new[] { 0, 3 }) : Groups[R.Next(Groups.Length)];
            string label;
            string result;
            switch (which)
            {
                case 0: { string v = bad ? text : Pick(Signs); label = "NegativeSign=" + v; result = Both(() => z.NegativeSign = v, () => s.NegativeSign = v); break; }
                case 1: { string v = bad ? text : Pick(Signs); label = "PositiveSign=" + v; result = Both(() => z.PositiveSign = v, () => s.PositiveSign = v); break; }
                case 2: { string v = bad ? text : Pick(Separators); label = "NumberDecimalSeparator=" + v; result = Both(() => z.NumberDecimalSeparator = v, () => s.NumberDecimalSeparator = v); break; }
                case 3: { string v = bad ? text : Pick(Separators); label = "NumberGroupSeparator=" + v; result = Both(() => z.NumberGroupSeparator = v, () => s.NumberGroupSeparator = v); break; }
                case 4: { string v = bad ? text : Pick(Separators); label = "CurrencyDecimalSeparator=" + v; result = Both(() => z.CurrencyDecimalSeparator = v, () => s.CurrencyDecimalSeparator = v); break; }
                case 5: { string v = bad ? text : Pick(Separators); label = "CurrencyGroupSeparator=" + v; result = Both(() => z.CurrencyGroupSeparator = v, () => s.CurrencyGroupSeparator = v); break; }
                case 6: { string v = bad ? text : Pick(Separators); label = "PercentDecimalSeparator=" + v; result = Both(() => z.PercentDecimalSeparator = v, () => s.PercentDecimalSeparator = v); break; }
                case 7: { string v = bad ? text : Pick(Separators); label = "PercentGroupSeparator=" + v; result = Both(() => z.PercentGroupSeparator = v, () => s.PercentGroupSeparator = v); break; }
                case 8: { string v = bad ? text : Pick(Symbols); label = "CurrencySymbol=" + v; result = Both(() => z.CurrencySymbol = v, () => s.CurrencySymbol = v); break; }
                case 9: { string v = bad ? text : Pick(Symbols); label = "PercentSymbol=" + v; result = Both(() => z.PercentSymbol = v, () => s.PercentSymbol = v); break; }
                case 10: { string v = bad ? text : Pick(Words); label = "NaNSymbol=" + v; result = Both(() => z.NaNSymbol = v, () => s.NaNSymbol = v); break; }
                case 11: { string v = bad ? text : Pick(Words); label = "PositiveInfinitySymbol=" + v; result = Both(() => z.PositiveInfinitySymbol = v, () => s.PositiveInfinitySymbol = v); break; }
                case 12: { string v = bad ? text : Pick(Words); label = "NegativeInfinitySymbol=" + v; result = Both(() => z.NegativeInfinitySymbol = v, () => s.NegativeInfinitySymbol = v); break; }
                case 13: { int v = bad ? number : R.Next(0, 6); label = "NumberDecimalDigits=" + v; result = Both(() => z.NumberDecimalDigits = v, () => s.NumberDecimalDigits = v); break; }
                case 14: { int v = bad ? number : R.Next(0, 6); label = "CurrencyDecimalDigits=" + v; result = Both(() => z.CurrencyDecimalDigits = v, () => s.CurrencyDecimalDigits = v); break; }
                case 15: { int v = bad ? number : R.Next(0, 6); label = "PercentDecimalDigits=" + v; result = Both(() => z.PercentDecimalDigits = v, () => s.PercentDecimalDigits = v); break; }
                case 16: { int v = bad ? number : R.Next(0, 5); label = "NumberNegativePattern=" + v; result = Both(() => z.NumberNegativePattern = v, () => s.NumberNegativePattern = v); break; }
                case 17: { int v = bad ? number : R.Next(0, 4); label = "CurrencyPositivePattern=" + v; result = Both(() => z.CurrencyPositivePattern = v, () => s.CurrencyPositivePattern = v); break; }
                case 18: { int v = bad ? number : R.Next(0, 17); label = "CurrencyNegativePattern=" + v; result = Both(() => z.CurrencyNegativePattern = v, () => s.CurrencyNegativePattern = v); break; }
                case 19: { int v = bad ? number : R.Next(0, 4); label = "PercentPositivePattern=" + v; result = Both(() => z.PercentPositivePattern = v, () => s.PercentPositivePattern = v); break; }
                case 20: { int v = bad ? number : R.Next(0, 12); label = "PercentNegativePattern=" + v; result = Both(() => z.PercentNegativePattern = v, () => s.PercentNegativePattern = v); break; }
                case 21: { int[] v = groups; label = "NumberGroupSizes=" + string.Join(",", v); result = Both(() => z.NumberGroupSizes = v, () => s.NumberGroupSizes = v); break; }
                case 22: { int[] v = groups; label = "CurrencyGroupSizes=" + string.Join(",", v); result = Both(() => z.CurrencyGroupSizes = v, () => s.CurrencyGroupSizes = v); break; }
                case 23: { int[] v = groups; label = "PercentGroupSizes=" + string.Join(",", v); result = Both(() => z.PercentGroupSizes = v, () => s.PercentGroupSizes = v); break; }
                default: { string v = bad ? text : Pick(Symbols); label = "PerMilleSymbol=" + v; result = Both(() => z.PerMilleSymbol = v, () => s.PerMilleSymbol = v); break; }
            }
            if (result != null)
            {
                checks++;
                if (!result.StartsWith("same"))
                {
                    failures++;
                    perLabel.TryGetValue("setter", out int n);
                    perLabel["setter"] = n + 1;
                    if (n < 6)
                        Console.WriteLine($"FAIL setter [{Escape(label)}]\n  {Escape(result)}");
                }
            }
            log.Append(label).Append("; ");
        }
        return log.ToString();
    }

    static string Both(Action zero, Action net)
    {
        string a = Run(() => { zero(); return "ok"; }), b = Run(() => { net(); return "ok"; });
        return a == b ? "same" : $"zero {a} | .NET {b}";
    }

    delegate void Formatter(char format, int precision, ref Z.Number.Sink sink);

    static string Zero(Z.NumberFormatInfo info, char letter, int precision, Formatter f)
    {
        precision = Z.Number.s_culture.Precision(letter, precision, info);
        char[] buffer = new char[2000];
        fixed (char* chars = buffer)
        {
            var sink = new Z.Number.Sink(chars, buffer.Length) { Info = info };
            f(letter, precision, ref sink);
            return new string(chars, 0, sink.Length);
        }
    }

    static readonly char[] Letters = { 'F', 'N', 'C', 'P', 'E', 'G', 'f', 'n', 'c', 'p', 'e', 'g', 'D', 'R', 'X' };

    static string Format(char letter, int precision) => precision < 0 ? letter.ToString() : letter + precision.ToString();

    static double AnyDouble() => R.Next(6) switch
    {
        0 => (R.NextDouble() - 0.5) * Math.Pow(10, R.Next(-8, 20)),
        1 => R.Next(-100000, 100000) / 100.0,
        2 => R.Next(5) switch { 0 => double.NaN, 1 => double.PositiveInfinity, 2 => double.NegativeInfinity, 3 => -0.0, _ => 0 },
        3 => BitConverter.Int64BitsToDouble(R.NextInt64()),
        4 => Math.Round((R.NextDouble() - 0.5) * 2e7, R.Next(0, 4)),
        _ => (R.NextDouble() - 0.5) * 1e-3,
    };

    static long AnyLong() => R.Next(4) switch
    {
        0 => R.NextInt64(long.MinValue, long.MaxValue),
        1 => R.Next(-100000, 100000),
        2 => R.Next(-1000, 1000) * 1_000_000L,
        _ => R.Next(-10, 10),
    };

    // Text for parsing: a number .NET writes with these symbols, sometimes
    // changed, or pieces of the symbols and digits.
    static string ParseText(G.NumberFormatInfo s)
    {
        if (R.Next(3) > 0)
        {
            string text = R.Next(2) == 0
                ? AnyDouble().ToString(Format("FNCPEG"[R.Next(6)], R.Next(-1, 4)), s)
                : AnyLong().ToString(Format("DNCF"[R.Next(4)], R.Next(-1, 3)), s);
            switch (R.Next(5))
            {
                case 0: return " " + text + " ";
                case 1: return text.Length > 1 ? text.Remove(R.Next(text.Length), 1) : text;
                case 2: return text.Replace(" ", "");
                default: return text;
            }
        }
        var sb = new StringBuilder();
        int n = R.Next(1, 7);
        string[] pieces = { s.NegativeSign, s.PositiveSign, s.NumberDecimalSeparator, s.NumberGroupSeparator, s.CurrencySymbol,
            s.CurrencyDecimalSeparator, s.CurrencyGroupSeparator, "1", "23", "0", "456", "e", "E5", "(", ")", " ", "-", "+", "\u00A0", s.NaNSymbol, s.PositiveInfinitySymbol };
        for (int i = 0; i < n; i++)
            sb.Append(pieces[R.Next(pieces.Length)]);
        return sb.ToString();
    }

    static readonly G.NumberStyles[] Styles =
    {
        G.NumberStyles.Integer, G.NumberStyles.Number, G.NumberStyles.Float, G.NumberStyles.Currency, G.NumberStyles.Any,
        G.NumberStyles.Float | G.NumberStyles.AllowThousands, G.NumberStyles.AllowLeadingSign | G.NumberStyles.AllowParentheses,
        G.NumberStyles.AllowTrailingSign | G.NumberStyles.AllowDecimalPoint, G.NumberStyles.AllowCurrencySymbol | G.NumberStyles.AllowLeadingWhite,
    };

    public static int Main(string[] args)
    {
        int iterations = args.Length > 0 ? int.Parse(args[0]) : 100_000;
        R = new Random(args.Length > 1 ? int.Parse(args[1]) : 12345);
        var invariantZ = new Z.NumberFormatInfo();
        var invariantS = new G.NumberFormatInfo();
        Z.NumberFormatInfo z = null;
        G.NumberFormatInfo s = null;
        string config = "";
        for (int it = 0; it < iterations; it++)
        {
            if (it % 20 == 0)
            {
                // Sometimes the invariant values in a writable one: the same
                // text the invariant formatting writes, by the other path.
                z = new Z.NumberFormatInfo();
                s = new G.NumberFormatInfo();
                config = R.Next(8) == 0 ? "(defaults)" : Configure(z, s);
            }

            char letter = Letters[R.Next(Letters.Length)];
            int precision = R.Next(4) == 0 ? -1 : R.Next(0, 12);
            string format = Format(letter, precision);

            double d = AnyDouble();
            if (letter != 'D' && letter != 'X')
                Same("double", config + " " + d.ToString("R") + " " + format,
                    () => Zero(z, letter, precision, (char l, int p, ref Z.Number.Sink sk) => Z.Number.FormatDouble(d, l, p, ref sk)),
                    () => d.ToString(format, s));
            long v = AnyLong();
            Same("long", config + " " + v + " " + format,
                () => Zero(z, letter, precision, (char l, int p, ref Z.Number.Sink sk) => Z.Number.FormatInteger((ulong)v, true, 64, l, p, ref sk)),
                () => v.ToString(format, s));
            int iv = (int)v;
            Same("int", config + " " + iv + " " + format,
                () => Zero(z, letter, precision, (char l, int p, ref Z.Number.Sink sk) => Z.Number.FormatInteger((ulong)(long)iv, true, 32, l, p, ref sk)),
                () => iv.ToString(format, s));
            decimal m = (decimal)R.NextInt64(-100_000_000_000, 100_000_000_000) / (decimal)Math.Pow(10, R.Next(0, 9));
            if (letter != 'D' && letter != 'X')
            {
                int[] bits = decimal.GetBits(m);
                ulong lo = (uint)bits[0] | ((ulong)(uint)bits[1] << 32);
                uint hi = (uint)bits[2];
                int scale = (bits[3] >> 16) & 0xFF;
                bool negative = bits[3] < 0;
                Same("decimal", config + " " + m + " " + format,
                    () => Zero(z, letter, precision, (char l, int p, ref Z.Number.Sink sk) => Z.Number.FormatDecimal(lo, hi, scale, negative, l, p, ref sk)),
                    () => m.ToString(format, s));
            }

            // Parsing.
            string text = ParseText(s);
            G.NumberStyles style = Styles[R.Next(Styles.Length)];
            var zstyle = style;
            Same("parse int", config + " \"" + text + "\" " + style,
                () => ((int)Z.Number.ParseInteger(text, zstyle, true, 32, 10, "Value was either too large or too small for an Int32.", z)).ToString(),
                () => int.Parse(text, style, s).ToString());
            Same("parse long", config + " \"" + text + "\" " + style,
                () => ((long)Z.Number.ParseInteger(text, zstyle, true, 64, 19, "Value was either too large or too small for an Int64.", z)).ToString(),
                () => long.Parse(text, style, s).ToString());
            Same("parse double", config + " \"" + text + "\" " + style,
                () => BitConverter.UInt64BitsToDouble(Z.Number.ParseFloat(text, zstyle, Z.Number.DoubleFormat, z)).ToString("R"),
                () => double.Parse(text, style, s).ToString("R"));
            Same("TryParse double", config + " \"" + text + "\" " + style,
                () => Z.Number.TryParseFloat(text, zstyle, Z.Number.DoubleFormat, z, out ulong b) + " " + BitConverter.UInt64BitsToDouble(b).ToString("R"),
                () => double.TryParse(text, style, s, out double r) + " " + r.ToString("R"));
        }

        // Read-only ones refuse changes, Clone and ReadOnly behave as .NET's.
        Same("read-only", "", () => Run(() => { Z.NumberFormatInfo.InvariantInfo.NegativeSign = "x"; return "set"; }), () => Run(() => { G.NumberFormatInfo.InvariantInfo.NegativeSign = "x"; return "set"; }));
        Same("Clone", "", () => { var c = (Z.NumberFormatInfo)Z.NumberFormatInfo.InvariantInfo.Clone(); return c.IsReadOnly + " " + Z.NumberFormatInfo.ReadOnly(c).IsReadOnly; },
            () => { var c = (G.NumberFormatInfo)G.NumberFormatInfo.InvariantInfo.Clone(); return c.IsReadOnly + " " + G.NumberFormatInfo.ReadOnly(c).IsReadOnly; });

        foreach (var (label, n) in perLabel)
            Console.WriteLine($"  {label}: {n} failures");
        Console.WriteLine(failures == 0 ? $"OK: {checks} checks" : $"FAILED: {failures} of {checks} checks");
        return failures == 0 ? 0 : 1;
    }
}

namespace ZeroTest
{
    using System;

    static unsafe partial class Number
    {
        internal static string FromChars(char* chars, int length) => new string(chars, 0, length);
        internal static ReadOnlySpan<char> AsSpan(string text) => text;
        // Number.cs isn't copied; only a current culture with symbols of its own uses this.
        internal static string IntegerToString(ulong raw, bool signed, int bits, string format, IFormatProvider provider) => throw new NotSupportedException();
    }

    // What the copied files ask of CultureInfo: the invariant culture.
    public class CultureInfo : IFormatProvider
    {
        public static CultureInfo CurrentCulture { get; } = new CultureInfo();
        public NumberFormatInfo NumberFormat => NumberFormatInfo.InvariantInfo;
        public object GetFormat(Type formatType) => null;
    }
}
