// Compares zerocore's casing and comparing (namespace ZeroTest here) with
// .NET's in invariant mode: every code point's case, then random strings for
// ToUpper/ToLower, ToTitleCase, string's StringComparison overloads,
// CompareInfo and Rune. Each check compares the result's text, or the
// exception's type and message. Usage: check-text.sh [iterations] [seed]
using System;
using System.Text;
using G = System.Globalization;
using Z = ZeroTest;

static class Program
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

    // Characters worth mixing: ASCII, Latin-1, Turkish, Greek, Cyrillic,
    // digraphs, Deseret and Osage pairs, lone surrogates, apostrophes,
    // separators and marks.
    static readonly string[] Pieces =
    {
        "a", "b", "z", "A", "B", "Z", "0", "9", " ", "'", "-", "_", ".", ",", "!", "\t",
        "\u00E9", "\u00C9", "\u00DF", "\u00FF", "\u0178", "\u0130", "\u0131", "\u015F", "\u015E", "\u00E7",
        "\u03C3", "\u03A3", "\u03C2", "\u0436", "\u0416", "\u01C4", "\u01C5", "\u01C6", "\u01F1", "\u0149",
        "\uD801\uDC00", "\uD801\uDC28", "\uD801\uDCB0", "\uD801\uDCD8", "\uD83D\uDE00", "\uD800", "\uDC00",
        "\u0301", "\u2028", "\u00A0", "\u3042", "\uFF21", "\u2160", "\u24B6", "\u1E9E", "\u0345",
    };

    static string Text(int max = 8)
    {
        var sb = new StringBuilder();
        int n = R.Next(max + 1);
        for (int i = 0; i < n; i++)
            sb.Append(Pieces[R.Next(Pieces.Length)]);
        return sb.ToString();
    }

    // Something related to a: itself, its case changed, a piece of it.
    static string Related(string a)
    {
        switch (R.Next(6))
        {
            case 0: return a.ToUpperInvariant();
            case 1: return a.ToLowerInvariant();
            case 2: return a.Length == 0 ? a : a.Substring(R.Next(a.Length));
            case 3: return a.Length == 0 ? a : a.Substring(0, R.Next(a.Length + 1));
            case 4: return R.Next(2) == 0 ? null : "";
            default: return Text();
        }
    }

    static int Index(string s) => R.Next(5) switch
    {
        0 => -1,
        1 => (s?.Length ?? 0) + R.Next(3),
        2 => 0,
        _ => R.Next((s?.Length ?? 0) + 1),
    };

    static readonly int[] OptionChoices = { 0, 1, 2, 4, 8, 16, 32, 0x10000000, 0x20000000, 0x40000000, 0x40000001, 0x1000, 3, 0x21, 0x10000001 };

    static string Hex(string s) => s == null ? "null" : string.Join(",", Array.ConvertAll(s.ToCharArray(), c => ((int)c).ToString("X4"))) + (s.Length == 0 ? "empty" : "");

    public static int Main(string[] args)
    {
        int iterations = args.Length > 0 ? int.Parse(args[0]) : 200_000;
        R = new Random(args.Length > 1 ? int.Parse(args[1]) : 12345);
        var zi = Z.CompareInfo.Invariant;
        var si = G.CultureInfo.InvariantCulture.CompareInfo;
        var zt = Z.TextInfo.Invariant;
        var st = G.CultureInfo.InvariantCulture.TextInfo;
        var zc = new Z.CultureInfo();
        var sc = G.CultureInfo.InvariantCulture;

        // Every code point's case.
        for (int cp = 0; cp < 0x110000; cp++)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF)
            {
                string lone = ((char)cp).ToString();
                Same("lone surrogate case", lone, () => Hex(Z.StringOps.ToUpper(lone)) + Hex(Z.StringOps.ToLower(lone)), () => Hex(lone.ToUpperInvariant()) + Hex(lone.ToLowerInvariant()));
                continue;
            }
            var rune = new Rune(cp);
            int c = cp;
            Same("code point case", cp.ToString("X"), () => $"{Z.TextCasing.Change(c, true):X} {Z.TextCasing.Change(c, false):X}",
                () => $"{Rune.ToUpperInvariant(rune).Value:X} {Rune.ToLowerInvariant(rune).Value:X}");
            if (cp % 7 == 0)
            {
                var zr = new Z.Rune(c);
                Same("Rune classes", cp.ToString("X"),
                    () => $"{(int)Z.Rune.GetUnicodeCategory(zr)} {Z.Rune.IsLetter(zr)} {Z.Rune.IsDigit(zr)} {Z.Rune.IsNumber(zr)} {Z.Rune.IsUpper(zr)} {Z.Rune.IsLower(zr)} {Z.Rune.IsPunctuation(zr)} {Z.Rune.IsSymbol(zr)} {Z.Rune.IsSeparator(zr)} {Z.Rune.IsWhiteSpace(zr)} {Z.Rune.IsControl(zr)} {Z.Rune.IsLetterOrDigit(zr)} {Z.Rune.GetNumericValue(zr):R} {zr.Plane} {zr.IsBmp} {zr.IsAscii} {zr.Utf16SequenceLength} {Hex(zr.ToString())}",
                    () => $"{(int)Rune.GetUnicodeCategory(rune)} {Rune.IsLetter(rune)} {Rune.IsDigit(rune)} {Rune.IsNumber(rune)} {Rune.IsUpper(rune)} {Rune.IsLower(rune)} {Rune.IsPunctuation(rune)} {Rune.IsSymbol(rune)} {Rune.IsSeparator(rune)} {Rune.IsWhiteSpace(rune)} {Rune.IsControl(rune)} {Rune.IsLetterOrDigit(rune)} {Rune.GetNumericValue(rune):R} {rune.Plane} {rune.IsBmp} {rune.IsAscii} {rune.Utf16SequenceLength} {Hex(rune.ToString())}");
            }
        }

        for (int it = 0; it < iterations; it++)
        {
            string a = Text();
            string b = Related(a);
            string input = $"{Hex(a)} | {Hex(b)}";

            // Casing.
            Same("ToUpper", Hex(a), () => Hex(Z.StringOps.ToUpper(a)) + ReferenceEquals(Z.StringOps.ToUpper(a), a), () => Hex(a.ToUpperInvariant()) + ReferenceEquals(a.ToUpperInvariant(), a));
            Same("ToLower", Hex(a), () => Hex(Z.StringOps.ToLower(a)) + ReferenceEquals(Z.StringOps.ToLower(a), a), () => Hex(a.ToLowerInvariant()) + ReferenceEquals(a.ToLowerInvariant(), a));
            string words = Text(12);
            Same("ToTitleCase", Hex(words), () => Hex(zt.ToTitleCase(words)), () => Hex(st.ToTitleCase(words)));
            Same("TextInfo char", Hex(a), () => a.Length == 0 ? "" : $"{(int)zt.ToUpper(a[0])} {(int)zt.ToLower(a[0])}", () => a.Length == 0 ? "" : $"{(int)st.ToUpper(a[0])} {(int)st.ToLower(a[0])}");

            // string's StringComparison overloads.
            int type = R.Next(8) == 0 ? R.Next(-1, 8) : R.Next(6);
            var zType = (Z.StringComparison)type;
            var sType = (StringComparison)type;
            string ti = input + " " + type;
            Same("Equals", ti, () => Z.StringOps.Equals(a, b, zType).ToString(), () => string.Equals(a, b, sType).ToString());
            Same("Compare", ti, () => Z.StringOps.Compare(a, b, zType).ToString(), () => string.Compare(a, b, sType).ToString());
            Same("CompareOrdinal", input, () => Z.StringOps.CompareOrdinal(a, b).ToString(), () => string.CompareOrdinal(a, b).ToString());
            string longA = a + Text(14), longB = R.Next(2) == 0 ? longA.Substring(0, R.Next(longA.Length + 1)) : longA + Text(3);
            Same("CompareOrdinal (long)", Hex(longA) + " | " + Hex(longB), () => Z.StringOps.CompareOrdinal(longA, longB).ToString(), () => string.CompareOrdinal(longA, longB).ToString());
            Same("Compare (long)", Hex(longA) + " | " + Hex(longB) + " " + type, () => Z.StringOps.Compare(longA, longB, zType).ToString(), () => string.Compare(longA, longB, sType).ToString());
            if (b != null && a != null)
            {
                Same("StartsWith", ti, () => Z.StringOps.StartsWith(a, b, zType).ToString(), () => a.StartsWith(b, sType).ToString());
                Same("EndsWith", ti, () => Z.StringOps.EndsWith(a, b, zType).ToString(), () => a.EndsWith(b, sType).ToString());
                int start = Index(a), count = R.Next(3) == 0 ? R.Next(-1, a.Length + 2) : a.Length - Math.Max(0, Math.Min(start, a.Length));
                string si2 = ti + $" start {start} count {count}";
                Same("IndexOf(string, start, count)", si2, () => Z.StringOps.IndexOf(a, b, start, count, zType).ToString(), () => a.IndexOf(b, start, count, sType).ToString());
                int lastStart = R.Next(3) == 0 ? R.Next(-2, a.Length + 2) : a.Length - 1;
                int lastCount = R.Next(3) == 0 ? R.Next(-1, a.Length + 2) : lastStart + 1;
                string li = ti + $" start {lastStart} count {lastCount}";
                Same("LastIndexOf(string, start, count)", li, () => Z.StringOps.LastIndexOf(a, b, lastStart, lastCount, zType).ToString(), () => a.LastIndexOf(b, lastStart, lastCount, sType).ToString());
                Same("LastIndexOf(string)", ti, () => Z.StringOps.LastIndexOf(a, b, a.Length - 1, a.Length, zType).ToString(), () => a.LastIndexOf(b, sType).ToString());
                if (b.Length > 0)
                {
                    char ch = b[0];
                    Same("IndexOf(char)", ti, () => Z.StringOps.IndexOf(a, ch, zType).ToString(), () => a.IndexOf(ch, sType).ToString());
                }
                if (Z.StringOps.Equals(a, b, Z.StringComparison.OrdinalIgnoreCase))
                    Same("hash ignoring case", input, () => (Z.StringOps.GetHashCode(a, Z.StringComparison.OrdinalIgnoreCase) == Z.StringOps.GetHashCode(b, Z.StringComparison.OrdinalIgnoreCase)).ToString(), () => "True");
            }
            int ia = Index(a), ib = Index(b), len = R.Next(4) == 0 ? R.Next(-1, 3) : R.Next(10);
            string ci = ti + $" {ia} {ib} {len}";
            Same("Compare(index, length, type)", ci, () => Z.StringOps.Compare(a, ia, b, ib, len, zType).ToString(), () => string.Compare(a, ia, b, ib, len, sType).ToString());
            int options = OptionChoices[R.Next(OptionChoices.Length)];
            string oi = input + " " + options.ToString("X");
            Same("Compare(index, length, culture)", ci + " " + options.ToString("X"), () => Z.StringOps.Compare(a, ia, b, ib, len, zc, (Z.CompareOptions)options).ToString(), () => string.Compare(a, ia, b, ib, len, sc, (G.CompareOptions)options).ToString());

            // CompareInfo.
            Same("CompareInfo.Compare", oi, () => zi.Compare(a, b, (Z.CompareOptions)options).ToString(), () => si.Compare(a, b, (G.CompareOptions)options).ToString());
            Same("CompareInfo.Compare(span)", oi, () => zi.Compare(a.AsSpan(), b.AsSpan(), (Z.CompareOptions)options).ToString(), () => si.Compare(a.AsSpan(), b.AsSpan(), (G.CompareOptions)options).ToString());
            int o1 = Index(a), l1 = R.Next(-1, 5), o2 = Index(b), l2 = R.Next(-1, 5);
            Same("CompareInfo.Compare(offsets)", oi + $" {o1} {l1} {o2} {l2}", () => zi.Compare(a, o1, l1, b, o2, l2, (Z.CompareOptions)options).ToString(), () => si.Compare(a, o1, l1, b, o2, l2, (G.CompareOptions)options).ToString());
            Same("CompareInfo.Compare(offsets to end)", oi + $" {o1} {o2}", () => zi.Compare(a, o1, b, o2, (Z.CompareOptions)options).ToString(), () => si.Compare(a, o1, b, o2, (G.CompareOptions)options).ToString());
            Same("IsPrefix/IsSuffix", oi, () => $"{zi.IsPrefix(a, b, (Z.CompareOptions)options)} {zi.IsSuffix(a, b, (Z.CompareOptions)options)}", () => $"{si.IsPrefix(a, b, (G.CompareOptions)options)} {si.IsSuffix(a, b, (G.CompareOptions)options)}");
            int cs = Index(a), cc = R.Next(3) == 0 ? R.Next(-1, 6) : (a.Length - Math.Max(0, cs));
            Same("CompareInfo.IndexOf", oi + $" {cs} {cc}", () => zi.IndexOf(a, b, cs, cc, (Z.CompareOptions)options).ToString(), () => si.IndexOf(a, b, cs, cc, (G.CompareOptions)options).ToString());
            Same("CompareInfo.IndexOf(span)", oi, () => zi.IndexOf(a.AsSpan(), b.AsSpan(), (Z.CompareOptions)options).ToString(), () => si.IndexOf(a.AsSpan(), b.AsSpan(), (G.CompareOptions)options).ToString());
            char cch = (b == null || b.Length == 0) ? 'a' : b[0];
            Same("CompareInfo.IndexOf(char)", oi, () => zi.IndexOf(a, cch, (Z.CompareOptions)options).ToString(), () => si.IndexOf(a, cch, (G.CompareOptions)options).ToString());
            int ls = R.Next(3) == 0 ? R.Next(-2, (a?.Length ?? 0) + 2) : (a?.Length ?? 0) - 1, lc = R.Next(3) == 0 ? R.Next(-1, 6) : ls + 1;
            Same("CompareInfo.LastIndexOf", oi + $" {ls} {lc}", () => zi.LastIndexOf(a, b, ls, lc, (Z.CompareOptions)options).ToString(), () => si.LastIndexOf(a, b, ls, lc, (G.CompareOptions)options).ToString());
            Same("CompareInfo.LastIndexOf(char)", oi + $" {ls} {lc}", () => zi.LastIndexOf(a, cch, ls, lc, (Z.CompareOptions)options).ToString(), () => si.LastIndexOf(a, cch, ls, lc, (G.CompareOptions)options).ToString());
            Same("CompareInfo.LastIndexOf(span)", oi, () => zi.LastIndexOf(a.AsSpan(), b.AsSpan(), (Z.CompareOptions)options).ToString(), () => si.LastIndexOf(a.AsSpan(), b.AsSpan(), (G.CompareOptions)options).ToString());
            Same("IsSortable", Hex(a), () => Z.CompareInfo.IsSortable(a).ToString(), () => G.CompareInfo.IsSortable(a).ToString());
            Same("GetHashCode(options) checks", oi, () => { zi.GetHashCode(a, (Z.CompareOptions)options); return "ok"; }, () => { si.GetHashCode(a, (G.CompareOptions)options); return "ok"; });

            // Rune.
            int value = R.Next(4) switch { 0 => R.Next(-5, 0x110005), 1 => R.Next(0xD7F0, 0xE010), 2 => R.Next(int.MinValue, int.MaxValue), _ => R.Next(0, 0x80) };
            Same("Rune(int)", value.ToString("X"), () => new Z.Rune(value).Value.ToString("X"), () => new Rune(value).Value.ToString("X"));
            Same("Rune.TryCreate", value.ToString("X"), () => $"{Z.Rune.TryCreate(value, out var r)} {r.Value}", () => $"{Rune.TryCreate(value, out var r)} {r.Value}");
            string units = Text(3);
            Same("DecodeFromUtf16", Hex(units), () => $"{Z.Rune.DecodeFromUtf16(units, out var r, out int n)} {r.Value:X} {n}", () => $"{Rune.DecodeFromUtf16(units, out var r, out int n)} {r.Value:X} {n}");
            Same("DecodeLastFromUtf16", Hex(units), () => $"{Z.Rune.DecodeLastFromUtf16(units, out var r, out int n)} {r.Value:X} {n}", () => $"{Rune.DecodeLastFromUtf16(units, out var r, out int n)} {r.Value:X} {n}");
            if (units.Length > 0)
            {
                int at = R.Next(-1, units.Length + 1);
                Same("GetRuneAt", Hex(units) + " " + at, () => Z.Rune.GetRuneAt(units, at).Value.ToString("X"), () => Rune.GetRuneAt(units, at).Value.ToString("X"));
                char h = units[0], l = units.Length > 1 ? units[1] : 'x';
                Same("Rune(char, char)", Hex(units), () => new Z.Rune(h, l).Value.ToString("X"), () => new Rune(h, l).Value.ToString("X"));
                Same("Rune(char)", Hex(units), () => new Z.Rune(h).Value.ToString("X"), () => new Rune(h).Value.ToString("X"));
            }
        }

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
    }

    // What the copied files ask of CultureInfo: the invariant one.
    public class CultureInfo
    {
        internal const int LocaleInvariant = 127;
        public static CultureInfo GetCultureInfo(string name) => new CultureInfo();
        public static CultureInfo GetCultureInfo(int culture) => new CultureInfo();
        public CompareInfo CompareInfo => CompareInfo.Invariant;
        public TextInfo TextInfo => TextInfo.Invariant;
    }
}
