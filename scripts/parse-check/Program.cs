// Compares zerocore's number parsing (System/Number.Parsing.cs, in namespace
// ZeroTest here) with .NET's in the invariant culture: random text, numbers
// written every way NumberStyles allows, integer limits, and strings exactly
// at (or a hair off) the halfway point between two doubles, floats or Halfs.
// Usage: check-parsing.sh [iterations] [seed]
using CultureInfo = System.Globalization.CultureInfo;
using NumberFormatInfo = System.Globalization.NumberFormatInfo;
using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using ZeroTest;

static class Program
{
    static Random R = new Random(12345);
    static int failures;
    static long checks;
    static int shown;
    static string lastFailed;

    sealed record IntegerKind(string Name, bool Signed, int Bits, int MaxDigits, string Overflow,
        Func<string, NumberStyles, ulong> Parse, Func<string, NumberStyles, (bool, ulong)> TryParse);

    sealed record FloatKind(string Name, Func<Number.FloatFormat> Format,
        Func<string, NumberStyles, ulong> Parse, Func<string, NumberStyles, (bool, ulong)> TryParse);

    static readonly CultureInfo I = CultureInfo.InvariantCulture;

    static readonly IntegerKind[] Integers =
    {
        new("sbyte", true, 8, 3, "Value was either too large or too small for a signed byte.",
            (s, st) => (byte)sbyte.Parse(s, st, I), (s, st) => (sbyte.TryParse(s, st, I, out var v), (byte)v)),
        new("byte", false, 8, 3, "Value was either too large or too small for an unsigned byte.",
            (s, st) => byte.Parse(s, st, I), (s, st) => (byte.TryParse(s, st, I, out var v), v)),
        new("short", true, 16, 5, "Value was either too large or too small for an Int16.",
            (s, st) => (ushort)short.Parse(s, st, I), (s, st) => (short.TryParse(s, st, I, out var v), (ushort)v)),
        new("ushort", false, 16, 5, "Value was either too large or too small for a UInt16.",
            (s, st) => ushort.Parse(s, st, I), (s, st) => (ushort.TryParse(s, st, I, out var v), v)),
        new("int", true, 32, 10, "Value was either too large or too small for an Int32.",
            (s, st) => (uint)int.Parse(s, st, I), (s, st) => (int.TryParse(s, st, I, out var v), (uint)v)),
        new("uint", false, 32, 10, "Value was either too large or too small for a UInt32.",
            (s, st) => uint.Parse(s, st, I), (s, st) => (uint.TryParse(s, st, I, out var v), v)),
        new("long", true, 64, 19, "Value was either too large or too small for an Int64.",
            (s, st) => (ulong)long.Parse(s, st, I), (s, st) => (long.TryParse(s, st, I, out var v), (ulong)v)),
        new("ulong", false, 64, 20, "Value was either too large or too small for a UInt64.",
            (s, st) => ulong.Parse(s, st, I), (s, st) => (ulong.TryParse(s, st, I, out var v), v)),
        new("nint", true, 64, 19, "Value was either too large or too small for an Int64.",
            (s, st) => (ulong)nint.Parse(s, st, I), (s, st) => (nint.TryParse(s, st, I, out var v), (ulong)v)),
        new("nuint", false, 64, 20, "Value was either too large or too small for a UInt64.",
            (s, st) => nuint.Parse(s, st, I), (s, st) => (nuint.TryParse(s, st, I, out var v), v)),
    };

    static readonly FloatKind[] Floats =
    {
        new("double", () => Number.DoubleFormat,
            (s, st) => BitConverter.DoubleToUInt64Bits(double.Parse(s, st, I)),
            (s, st) => (double.TryParse(s, st, I, out var v), BitConverter.DoubleToUInt64Bits(v))),
        new("float", () => Number.SingleFormat,
            (s, st) => BitConverter.SingleToUInt32Bits(float.Parse(s, st, I)),
            (s, st) => (float.TryParse(s, st, I, out var v), BitConverter.SingleToUInt32Bits(v))),
        new("Half", () => Number.HalfFormat,
            (s, st) => BitConverter.HalfToUInt16Bits(Half.Parse(s, st, I)),
            (s, st) => (Half.TryParse(s, st, I, out var v), BitConverter.HalfToUInt16Bits(v))),
    };

    static string Outcome(Func<ulong> parse)
    {
        try
        {
            return "ok " + parse().ToString("X");
        }
        catch (Exception e)
        {
            return "!" + e.GetType().Name + ": " + e.Message;
        }
    }

    static string TryOutcome(Func<(bool, ulong)> tryParse)
    {
        try
        {
            var (ok, value) = tryParse();
            return ok ? "ok " + value.ToString("X") : "false " + value.ToString("X");
        }
        catch (Exception e)
        {
            return "!" + e.GetType().Name + ": " + e.Message;
        }
    }

    static string TryOf(string parseOutcome) =>
        parseOutcome.StartsWith("!FormatException") || parseOutcome.StartsWith("!OverflowException") ? "false 0" : parseOutcome;

    static void Compare(string type, string text, NumberStyles style, string expected, string actual)
    {
        checks++;
        if (expected == actual)
            return;
        // One line for each string that fails, however many types it fails as.
        if (++failures <= 400 && text != lastFailed && ++shown <= 40)
            Console.WriteLine($"FAIL {type}.Parse({Show(text)}, {style}): .NET [{expected}] zero [{actual}]");
        lastFailed = text;
    }

    static string Show(string s)
    {
        var b = new StringBuilder("\"");
        foreach (char c in s.Length > 120 ? s.Substring(0, 60) + "\u2026" + s.Substring(s.Length - 50) : s)
        {
            if (c < ' ' || c > '~')
                b.Append($"\\u{(int)c:X4}");
            else
                b.Append(c);
        }
        return b.Append('"').Append(s.Length > 120 ? $" ({s.Length} chars)" : "").ToString();
    }

    static void CheckInteger(IntegerKind k, string s, NumberStyles style)
    {
        ulong mask = k.Bits == 64 ? ulong.MaxValue : (1UL << k.Bits) - 1;
        string expected = Outcome(() => k.Parse(s, style));
        string actual = Outcome(() => Number.ParseInteger(s, style, k.Signed, k.Bits, k.MaxDigits, k.Overflow, null) & mask);
        Compare(k.Name, s, style, expected, actual);
        string expectedTry = TryOutcome(() => k.TryParse(s, style));
        string actualTry = TryOutcome(() => { bool ok = Number.TryParseInteger(s, style, k.Signed, k.Bits, k.MaxDigits, null, out ulong raw); return (ok, raw & mask); });
        Compare(k.Name + ".Try", s, style, expectedTry, actualTry);
        Compare(k.Name + ".Try/Parse", s, style, TryOf(expected), expectedTry);
    }

    static void CheckFloat(FloatKind k, string s, NumberStyles style)
    {
        string expected = Outcome(() => k.Parse(s, style));
        string actual = Outcome(() => Number.ParseFloat(s, style, k.Format(), null));
        Compare(k.Name, s, style, expected, actual);
        string expectedTry = TryOutcome(() => k.TryParse(s, style));
        string actualTry = TryOutcome(() => { bool ok = Number.TryParseFloat(s, style, k.Format(), null, out ulong bits); return (ok, ok ? bits : 0); });
        Compare(k.Name + ".Try", s, style, expectedTry, actualTry);
    }

    static void CheckAll(string s, NumberStyles integerStyle, NumberStyles floatStyle)
    {
        foreach (var k in Integers)
            CheckInteger(k, s, integerStyle);
        foreach (var k in Floats)
            CheckFloat(k, s, floatStyle);
    }

    // ---- styles -------------------------------------------------------------

    static readonly NumberStyles[] CommonStyles =
    {
        NumberStyles.Integer, NumberStyles.Number, NumberStyles.Float, NumberStyles.Float | NumberStyles.AllowThousands,
        NumberStyles.Any, NumberStyles.Currency, NumberStyles.None, NumberStyles.AllowLeadingSign,
        NumberStyles.AllowDecimalPoint, NumberStyles.AllowExponent | NumberStyles.AllowLeadingSign,
        NumberStyles.AllowTrailingSign | NumberStyles.AllowParentheses, NumberStyles.AllowThousands,
    };

    static NumberStyles IntegerStyle()
    {
        int roll = R.Next(20);
        if (roll < 3) return NumberStyles.HexNumber;
        if (roll < 4) return (NumberStyles)(R.Next(4) | 0x200);
        if (roll < 5) return NumberStyles.BinaryNumber;
        if (roll < 6) return (NumberStyles)R.Next(0x1000); // anything, invalid too
        if (roll < 9) return (NumberStyles)R.Next(0x200);
        return CommonStyles[R.Next(CommonStyles.Length)];
    }

    static NumberStyles FloatStyle()
    {
        int roll = R.Next(20);
        if (roll < 1) return (NumberStyles)R.Next(0x1000);
        if (roll < 5) return (NumberStyles)R.Next(0x200);
        return CommonStyles[R.Next(CommonStyles.Length)];
    }

    // ---- text ---------------------------------------------------------------

    static readonly string Alphabet = "0123456789012345678900000.,+-eE \t\n()\u00A4\0abcdefABCDEFxX\u00A0\u2028Ii";

    static string RandomText()
    {
        var b = new StringBuilder();
        int n = R.Next(14);
        for (int i = 0; i < n; i++)
            b.Append(Alphabet[R.Next(Alphabet.Length)]);
        return b.ToString();
    }

    static string Digits(int n, bool leadingZeros = true)
    {
        var b = new StringBuilder();
        for (int i = 0; i < n; i++)
            b.Append((char)('0' + (leadingZeros || i > 0 ? R.Next(10) : 1 + R.Next(9))));
        return b.ToString();
    }

    static string Pick(params string[] options) => options[R.Next(options.Length)];

    // A number put together the ways NumberStyles allows, with now and then
    // something out of place.
    static string Composed()
    {
        var b = new StringBuilder();
        if (R.Next(4) == 0) b.Append(Pick(" ", "  ", "\t", "\n ", "\u00A0"));
        if (R.Next(3) == 0) b.Append(Pick("-", "+", "(", "\u00A4", "-\u00A4", "\u00A4-", "\u00A4 -", "- ", "--"));
        if (R.Next(6) == 0) b.Append(Pick(" ", "\u00A4", "\u00A4 "));
        int whole = R.Next(4) == 0 ? R.Next(25) : R.Next(1, 8);
        string digits = R.Next(5) == 0 ? new string('0', R.Next(1, 30)) + Digits(whole) : Digits(whole);
        if (R.Next(5) == 0 && digits.Length > 3)
        {
            var grouped = new StringBuilder();
            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0) grouped.Append(Pick(",", ",", ",,", ""));
                grouped.Append(digits[i]);
            }
            digits = grouped.ToString();
        }
        b.Append(digits);
        if (R.Next(3) == 0)
        {
            b.Append('.');
            int fraction = R.Next(4) == 0 ? R.Next(30) : R.Next(4);
            b.Append(R.Next(3) == 0 ? new string('0', fraction) : Digits(fraction));
            if (R.Next(10) == 0) b.Append(Pick(".", ",", ".5"));
        }
        if (R.Next(4) == 0)
        {
            b.Append(Pick("e", "E", "e", "E"));
            b.Append(Pick("", "+", "-", "", "--", " "));
            int e = R.Next(6) switch
            {
                0 => R.Next(400),
                1 => R.Next(5000),
                2 => 99999999 + R.Next(3),
                3 => R.Next(10),
                4 => 0,
                _ => R.Next(50),
            };
            if (R.Next(10) != 0) b.Append(e);
            if (R.Next(12) == 0) b.Append("0000000000000");
        }
        if (R.Next(3) == 0) b.Append(Pick(")", "-", "+", "\u00A4", " ", "\t", "- ", " -", "\u00A4)", ")\u00A4", "  ", " \u00A4"));
        if (R.Next(10) == 0) b.Append(Pick("\0", "\0\0", "\0 ", " \0"));
        if (R.Next(25) == 0) b.Insert(R.Next(b.Length + 1), Alphabet[R.Next(Alphabet.Length)]);
        return b.ToString();
    }

    static readonly string[] Symbols =
    {
        "Infinity", "-Infinity", "+Infinity", "NaN", "-NaN", "+NaN", "infinity", "INFINITY", "-iNfInItY", "nan",
        "Inf", "-inf", "\u221E", "-\u221E", " Infinity ", "\u00A0NaN\u2028", "\tNaN\0", "Infinity\0", "- Infinity", "++NaN",
        "(Infinity)", "Infinity-", "NaN ", " -NaN", "Infinityx", "1Infinity", "\u0130nfinity", "\u0131nfinity", "",
    };

    static string Symbol() => Symbols[R.Next(Symbols.Length)];

    // Integer limits and values beside them, every way.
    static string IntegerEdge()
    {
        var k = Integers[R.Next(Integers.Length)];
        BigInteger max = k.Signed ? (BigInteger.One << (k.Bits - 1)) - 1 : (BigInteger.One << k.Bits) - 1;
        BigInteger min = k.Signed ? -(BigInteger.One << (k.Bits - 1)) : 0;
        BigInteger v = (R.Next(2) == 0 ? max : min) + R.Next(-3, 4);
        if (R.Next(6) == 0) v *= 10;
        string text = R.Next(5) switch
        {
            0 => (v < 0 ? (v + (BigInteger.One << k.Bits)) : v).ToString("X").TrimStart('0'),
            1 => v.ToString() + Pick(".0", ".00", ".000000000000000000000000000001", "e0", "0e-1", ".5", ".", ",000"),
            2 => v.ToString().Insert(v.Sign < 0 ? 1 : 0, Pick("0", "000000000000000000000000")),
            _ => v.ToString(),
        };
        return text.Length == 0 ? "0" : text;
    }

    // The exact decimal value of num * 2^twos.
    static string Exact(BigInteger num, int twos)
    {
        if (twos >= 0)
            return (num << twos).ToString();
        string digits = (num * BigInteger.Pow(5, -twos)).ToString().PadLeft(-twos + 1, '0');
        return digits.Substring(0, digits.Length + twos) + "." + digits.Substring(digits.Length + twos);
    }

    // The midpoint between a value and the next one up, written exactly, and
    // then exactly, a hair above (a 1 far down) or a hair below.
    static string Halfway()
    {
        int format = R.Next(3);
        int fractionBits = format == 0 ? 52 : format == 1 ? 23 : 10;
        int exponentBits = format == 0 ? 11 : format == 1 ? 8 : 5;
        int bias = (1 << (exponentBits - 1)) - 1;
        int biased = R.Next(4) == 0 ? R.Next(3) : R.Next((1 << exponentBits) - 1);
        ulong fraction = R.Next(3) == 0 ? (ulong)R.Next(3) : (ulong)R.NextInt64() & ((1UL << fractionBits) - 1);
        BigInteger m = biased == 0 ? fraction : fraction | (1UL << fractionBits);
        int e = (biased == 0 ? 1 : biased) - bias - fractionBits;
        string text = Exact(2 * m + 1, e - 1);
        switch (R.Next(5))
        {
            case 0:
                break;
            case 1:
                text = (text.Contains('.') ? text : text + ".") + new string('0', R.Next(800)) + "1";
                break;
            case 2:
                text = text + (text.Contains('.') ? "" : ".") + new string('0', R.Next(40));
                break;
            case 3:
                // One less in the last digit: just below.
                text = LessInLastDigit(text);
                break;
            default:
                // Written in scientific notation.
                text = Scientific(text);
                break;
        }
        return (R.Next(4) == 0 ? "-" : "") + text;
    }

    static string LessInLastDigit(string text)
    {
        char[] c = text.ToCharArray();
        for (int i = c.Length - 1; i >= 0; i--)
        {
            if (c[i] == '.') continue;
            if (c[i] != '0') { c[i]--; return new string(c); }
            c[i] = '9';
        }
        return text;
    }

    static string Scientific(string text)
    {
        int point = text.IndexOf('.');
        string digits = point < 0 ? text : text.Remove(point, 1);
        int scale = point < 0 ? text.Length : point;
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits.Substring(lead);
        scale -= lead;
        return digits.Substring(0, 1) + "." + digits.Substring(1) + "e" + (scale - 1);
    }

    // A random double, float or Half written as .NET writes it.
    static string Written()
    {
        double d = R.Next(3) switch
        {
            0 => BitConverter.Int64BitsToDouble(R.NextInt64()),
            1 => (double)BitConverter.Int32BitsToSingle(R.Next() * (R.Next(2) == 0 ? 1 : -1)),
            _ => (R.NextDouble() - 0.5) * Math.Pow(10, R.Next(-30, 30)),
        };
        string format = Pick("R", "E16", "E8", "G", "G5", "F2", "F20", "N3", "E30", "G17", "E3");
        return d.ToString(format, I);
    }

    public static int Main(string[] args)
    {
        int iterations = args.Length > 0 ? int.Parse(args[0]) : 200000;
        R = new Random(args.Length > 1 ? int.Parse(args[1]) : 12345);

        foreach (string s in Symbols)
            CheckAll(s, NumberStyles.Integer, NumberStyles.Float);
        foreach (var style in CommonStyles)
            foreach (string s in new[] { "", " ", "0", "-0", "+0", "(0)", "-0.0", "0e5", "-", ".", "e5", "1e", "1e+", "1 ", " 1", "1\0", "1\0x", "\0", "1,", ",1", "1.", ".1", "\u00A41", "1\u00A4", "\u00A4\u00A41", "(1", "1)", "( 1 )", "- 1", "\u00A4 -1", "-\u00A4 1", "1-", "1 -", "1e1000", "1e-1000", "-1e-1000" })
                CheckAll(s, style, style);

        for (int i = 0; i < iterations; i++)
        {
            string s = R.Next(8) switch
            {
                0 => RandomText(),
                1 => Symbol(),
                2 => IntegerEdge(),
                3 => Halfway(),
                4 => Written(),
                _ => Composed(),
            };
            CheckAll(s, IntegerStyle(), FloatStyle());
        }

        Console.WriteLine(failures == 0 ? $"OK: {checks} checks" : $"{failures} of {checks} checks differ");
        return failures == 0 ? 0 : 1;
    }
}

namespace ZeroTest
{
    // What Number.Parsing.cs takes from the rest of zerocore.
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
        public ZeroTest.NumberFormatInfo NumberFormat => ZeroTest.NumberFormatInfo.InvariantInfo;
        public object GetFormat(Type formatType) => null;
    }
}
