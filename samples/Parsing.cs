// Test: text to numbers with .NET's Parse and TryParse, against .NET's
// answers. Every integer type, double, float and Half, with the default
// styles and with NumberStyles (hex, binary, thousands, currency,
// parentheses), out-of-range and malformed text, and the hard cases for
// rounding: values exactly halfway between two doubles, and the smallest
// denormals. Every expected value here is what .NET 10 gives in the invariant
// culture; scripts/parse-check compares the parsing code with .NET's over
// millions of strings, and this checks it compiled into a program, on each
// target.
// Exit code = number of failed checks.
// Build: ./build.sh Parsing

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ParsingSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void Section(string section)
    {
        Console.WriteLine($"{(s_sectionFailures == 0 ? "PASS" : "FAIL")} {section} ({s_sectionChecks} checks)");
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_failures++;
            s_sectionFailures++;
            Console.WriteLine($"  FAIL: {name}");
        }
    }

    static void Bits(string text, double value, ulong expected) =>
        Check($"{text} is 0x{expected:X16}, got 0x{BitConverter.DoubleToUInt64Bits(value):X16}", BitConverter.DoubleToUInt64Bits(value) == expected);

    static void Bits(string text, float value, uint expected) =>
        Check($"{text} is 0x{expected:X8}, got 0x{BitConverter.SingleToUInt32Bits(value):X8}", BitConverter.SingleToUInt32Bits(value) == expected);

    static void Bits(string text, Half value, ushort expected) =>
        Check($"{text} is 0x{expected:X4}, got 0x{BitConverter.HalfToUInt16Bits(value):X4}", BitConverter.HalfToUInt16Bits(value) == expected);

    // Text the optimizer can't see through.
    static bool s_never;
    [MethodImpl(MethodImplOptions.NoInlining)] static string T(string text) => s_never ? "" : text;

    public static int Main()
    {
        s_never = false;

        // docs:integers
        int answer = int.Parse("42");
        Check("int.Parse", answer == 42);
        Check("whitespace and a sign", int.Parse(" -17 ") == -17);
        Check("thousands", int.Parse("1,234,567", NumberStyles.Number) == 1234567);
        Check("hex", int.Parse("ff", NumberStyles.HexNumber) == 255);
        Check("hex is the bits", sbyte.Parse("FF", NumberStyles.HexNumber) == -1);

        Check("TryParse", int.TryParse("12345", out int parsed) && parsed == 12345);
        Check("not a number", !int.TryParse("12a", out parsed) && parsed == 0);
        Check("too large", !int.TryParse("2147483648", out parsed));
        Check("null", !int.TryParse((string)null, out parsed));
        // docs:end

        Check("long.MinValue", long.Parse(T("-9223372036854775808")) == long.MinValue);
        Check("long too small", !long.TryParse(T("-9223372036854775809"), out _));
        Check("ulong.MaxValue", ulong.Parse(T("18446744073709551615")) == ulong.MaxValue);
        Check("ulong too large", !ulong.TryParse(T("18446744073709551616"), out _));
        Check("unsigned -0", uint.Parse(T("-0")) == 0);
        Check("unsigned -1", !uint.TryParse(T("-1"), out _));
        Check("byte", byte.Parse(T("255")) == 255 && !byte.TryParse(T("256"), out _));
        Check("sbyte", sbyte.Parse(T("-128")) == -128 && !sbyte.TryParse(T("128"), out _));
        Check("short", short.Parse(T("-32768")) == short.MinValue && !short.TryParse(T("32768"), out _));
        Check("ushort", ushort.Parse(T("65535")) == ushort.MaxValue);
        Check("nint", nint.Parse(T("-5")) == -5 && nuint.Parse(T("5")) == 5);
        Check("leading zeros", int.Parse(T("0000000000000000000042")) == 42);
        Check("no space after the sign", !int.TryParse(T("- 5"), out _));
        Check("no point by default", !int.TryParse(T("1.0"), out _));
        Check("1.0 with a point", int.Parse(T("1.0"), NumberStyles.Number) == 1);
        Check("1.5 doesn't fit an int", !int.TryParse(T("1.5"), NumberStyles.Number, null, out _));
        Check("1e3 as a float style", int.Parse(T("1e3"), NumberStyles.Float) == 1000);
        Check("parentheses", int.Parse(T("(42)"), NumberStyles.AllowParentheses) == -42);
        Check("trailing sign", int.Parse(T("42-"), NumberStyles.Number) == -42);
        Check("currency", int.Parse(T("\u00A4 1,000"), NumberStyles.Currency) == 1000);
        Check("hex, every type", ushort.Parse(T("FFFF"), NumberStyles.HexNumber) == 65535
            && short.Parse(T("8000"), NumberStyles.HexNumber) == short.MinValue
            && long.Parse(T("FFFFFFFFFFFFFFFF"), NumberStyles.HexNumber) == -1);
        Check("hex too long", !int.TryParse(T("1FFFFFFFF"), NumberStyles.HexNumber, null, out _));
        Check("hex with leading zeros", int.Parse(T("000000000FF"), NumberStyles.HexNumber) == 255);
        Check("hex takes no 0x", !int.TryParse(T("0xFF"), NumberStyles.HexNumber, null, out _));
        Check("binary", byte.Parse(T("1010"), NumberStyles.BinaryNumber) == 10
            && sbyte.Parse(T("10000000"), NumberStyles.BinaryNumber) == sbyte.MinValue);
        Check("trailing nulls", int.Parse(T("7\0\0")) == 7);
        Check("a span", int.Parse(T("x=99").AsSpan(2)) == 99);
        Section("integers");

        // docs:doubles
        double pi = double.Parse("3.14159");
        Check("double.Parse", pi == 3.14159);
        Check("exponent", double.Parse("6.02e23") == 6.02e23);
        Check("thousands by default", double.Parse("1,000.5") == 1000.5);
        Check("too large is Infinity",
            double.Parse("1e400") == double.PositiveInfinity);
        Check("too small is 0", double.Parse("1e-400") == 0);
        Check("symbols", double.IsNaN(double.Parse("NaN"))
            && double.Parse("-Infinity") == double.NegativeInfinity);
        Check("TryParse", double.TryParse("2.5", out double half) && half == 2.5);
        Check("not a number", !double.TryParse("2.5.1", out half));
        // docs:end

        Bits("0.1", double.Parse(T("0.1")), 0x3FB999999999999AUL);
        Bits("-0", double.Parse(T("-0")), 0x8000000000000000UL);
        Bits("MaxValue", double.Parse(T("1.7976931348623157e308")), 0x7FEFFFFFFFFFFFFFUL);
        Bits("past MaxValue, rounding down", double.Parse(T("1.7976931348623158e308")), 0x7FEFFFFFFFFFFFFFUL);
        Bits("halfway past MaxValue", double.Parse(T("179769313486231580793728971405303415079934132710037826936173778980444968292764750946649017977587207096330286416692887910946555547851940402630657488671505820681908902000708383676273854845817711531764475730270069855571366959622842914819860834936475292719074168444365510704342711559699508093042880177904174497792")), 0x7FF0000000000000UL);
        Bits("Epsilon", double.Parse(T("4.9406564584124654e-324")), 0x0000000000000001UL);
        Bits("just above half of Epsilon", double.Parse(T("2.4703282292062328e-324")), 0x0000000000000001UL);
        Bits("just below half of Epsilon", double.Parse(T("2.4703282292062327e-324")), 0);
        Bits("smallest normal", double.Parse(T("2.2250738585072014e-308")), 0x0010000000000000UL);
        Bits("largest denormal", double.Parse(T("2.2250738585072009e-308")), 0x000FFFFFFFFFFFFFUL);
        // 2^53 + 1 is halfway between 2^53 and 2^53 + 2: ties go to even.
        Bits("2^53 + 1", double.Parse(T("9007199254740993")), 0x4340000000000000UL);
        Bits("a hair above 2^53 + 1", double.Parse(T("9007199254740993.000000000000000000000000000001")), 0x4340000000000001UL);
        Bits("2^53 + 3", double.Parse(T("9007199254740995")), 0x4340000000000002UL);
        // The exact halfway point between 1 and the next double, then 760
        // zeros and a 1: only the last digit decides.
        string halfway = "1.00000000000000011102230246251565404236316680908203125";
        Bits("halfway above 1", double.Parse(T(halfway)), 0x3FF0000000000000UL);
        Bits("a hair above halfway", double.Parse(T(new StringBuilder(halfway).Append('0', 760).Append('1').ToString())), 0x3FF0000000000001UL);
        Bits("NaN", double.Parse(T("nan")), 0xFFF8000000000000UL);
        Bits("+Infinity", double.Parse(T(" +infinity ")), 0x7FF0000000000000UL);
        Bits("parentheses and currency", double.Parse(T("(\u00A41,234.50)"), NumberStyles.Currency), 0xC0934A0000000000UL);
        Check("no thousands in Float", !double.TryParse(T("1,000"), NumberStyles.Float, null, out _));
        Check("e without digits", !double.TryParse(T("1e"), out _));
        Check("a span", double.Parse(T("[0.25]").AsSpan(1, 4)) == 0.25);
        Section("double");

        Bits("float 0.1", float.Parse(T("0.1")), 0x3DCCCCCDu);
        Bits("float MaxValue", float.Parse(T("3.4028235e38")), 0x7F7FFFFFu);
        Bits("float past MaxValue, rounding down", float.Parse(T("3.4028235677e38")), 0x7F7FFFFFu);
        Bits("float past halfway", float.Parse(T("3.4028236e38")), 0x7F800000u);
        Bits("float halfway past MaxValue", float.Parse(T("340282356779733661637539395458142568448")), 0x7F800000u);
        Bits("float Epsilon", float.Parse(T("1.401298464324817e-45")), 0x00000001u);
        Bits("float 2^24 + 1", float.Parse(T("16777217")), 0x4B800000u);
        Bits("float a hair above 2^24 + 1", float.Parse(T("16777217.000000001")), 0x4B800001u);
        // Rounded once, not to a double first: this is just above halfway
        // between two floats, but would round to the halfway point as a double.
        Bits("float, not double rounding", float.Parse(T("1.00000005960464477550")), 0x3F800001u);
        Bits("Half MaxValue", Half.Parse(T("65504")), 0x7BFF);
        Bits("Half below halfway past MaxValue", Half.Parse(T("65519.99")), 0x7BFF);
        Bits("Half halfway past MaxValue", Half.Parse(T("65520")), 0x7C00);
        Bits("Half 0.1", Half.Parse(T("0.1")), 0x2E66);
        Bits("Half Epsilon", Half.Parse(T("6e-8")), 0x0001);
        Bits("Half NaN", Half.Parse(T("NaN")), 0xFE00);
        Check("float TryParse", float.TryParse(T("-2.5"), out float f) && f == -2.5f);
        Check("Half TryParse", !Half.TryParse(T("x"), out Half h) && BitConverter.HalfToUInt16Bits(h) == 0);
        Section("float and Half");

        // What ToString writes, Parse reads back to the same bits. TryFormat
        // writes into a buffer on the stack, so nothing is allocated.
        Span<char> text = stackalloc char[64];
        ulong state = 0x9E3779B97F4A7C15UL;
        int mismatches = 0;
        for (int i = 0; i < 2000; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            double d = BitConverter.UInt64BitsToDouble(state);
            if (double.IsFinite(d) && (!d.TryFormat(text, out int length)
                || BitConverter.DoubleToUInt64Bits(double.Parse(text.Slice(0, length))) != state))
                mismatches++;
            float single = BitConverter.UInt32BitsToSingle((uint)state);
            if (float.IsFinite(single) && (!single.TryFormat(text, out length)
                || BitConverter.SingleToUInt32Bits(float.Parse(text.Slice(0, length))) != (uint)state))
                mismatches++;
            Half small = BitConverter.UInt16BitsToHalf((ushort)state);
            if (Half.IsFinite(small) && (!small.TryFormat(text, out length)
                || BitConverter.HalfToUInt16Bits(Half.Parse(text.Slice(0, length))) != (ushort)state))
                mismatches++;
            long whole = (long)state;
            if (!whole.TryFormat(text, out length, "N0")
                || long.Parse(text.Slice(0, length), NumberStyles.Number) != whole)
                mismatches++;
            if (!state.TryFormat(text, out length, "X")
                || ulong.Parse(text.Slice(0, length), NumberStyles.HexNumber) != state)
                mismatches++;
        }
        Check($"round trips ({mismatches} differ)", mismatches == 0);
        Section("round trips");

        Console.WriteLine(s_failures == 0 ? "\nAll checks passed." : $"\n{s_failures} checks failed.");
        return s_failures;
    }
}
