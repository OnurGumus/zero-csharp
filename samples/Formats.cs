// Test: numbers as text with .NET's format strings, against .NET's answers.
// ToString("X4"), ToString("F2") and the rest on every numeric type,
// string.Format's "{0,8:N1}", $"{x:F2}", TryFormat and StringBuilder's
// AppendFormat. Every expected string here is what .NET 10 prints in the
// invariant culture; scripts/format-check compares the formatting code with
// .NET's over millions of values, and this checks it compiled into a
// program, on each target.
// Exit code = number of failed checks.
// Build: ./build.sh Formats

using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace FormatsSample;

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

    static void Is(string actual, string expected)
    {
        s_sectionChecks++;
        if (actual != expected)
        {
            s_failures++;
            s_sectionFailures++;
            Console.WriteLine($"  FAIL: expected [{expected}], got [{actual}]");
        }
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

    // Hide a value from the optimizer. (Always false: see SmallIntegers.cs.)
    static bool s_never;
    [MethodImpl(MethodImplOptions.NoInlining)] static int I(int value) => s_never ? 0 : value;
    [MethodImpl(MethodImplOptions.NoInlining)] static long L(long value) => s_never ? 0 : value;
    [MethodImpl(MethodImplOptions.NoInlining)] static double D(double value) => s_never ? 0 : value;
    [MethodImpl(MethodImplOptions.NoInlining)] static float F(float value) => s_never ? 0 : value;
    [MethodImpl(MethodImplOptions.NoInlining)] static decimal M(decimal value) => s_never ? 0 : value;

    record Point(double X, double Y);

    public static int Main()
    {
        s_never = false;

        // docs:integers
        int count = 1234567;
        Is(count.ToString("N0"), "1,234,567");
        Is(count.ToString("D9"), "001234567");
        Is(255.ToString("X4"), "00FF");
        Is((-1).ToString("x"), "ffffffff");
        Is(5.ToString("B8"), "00000101");
        // docs:end
        Is(I(-42).ToString("D8"), "-00000042");
        Is(I(int.MinValue).ToString("D"), "-2147483648");
        Is(((sbyte)I(-1)).ToString("X"), "FF");
        Is(((short)I(-1)).ToString("x8"), "0000ffff");
        Is(((byte)I(10)).ToString("X4"), "000A");
        Is(L(-1).ToString("X"), "FFFFFFFFFFFFFFFF");
        Is(((sbyte)I(-2)).ToString("B"), "11111110");
        Is(((ulong)L(-1)).ToString("N0"), "18,446,744,073,709,551,615");
        Is(L(long.MinValue).ToString("N0"), "-9,223,372,036,854,775,808");
        Is(L(long.MinValue).ToString("E3"), "-9.223E+018");
        Is(I(1234567).ToString("N"), "1,234,567.00");
        Is(I(-5).ToString("F3"), "-5.000");
        Is(I(12345).ToString("E"), "1.234500E+004");
        Is(I(12355).ToString("E2"), "1.24E+004");
        Is(I(99950).ToString("E2"), "1.00E+005");
        Is(I(12345).ToString("G3"), "1.23E+04");
        Is(I(25).ToString("G1"), "3E+01");
        Is(I(12345).ToString("G10"), "12345");
        Is(I(1).ToString("P"), "100.00 %");
        Is(I(-1).ToString("P0"), "-100 %");
        Is(I(-5).ToString("C"), "(¤5.00)");
        Is(I(0).ToString("E"), "0.000000E+000");
        Is(I(5).ToString("R"), "5");
        Is(I(7).ToString(""), "7");
        Is(((uint)I(-1)).ToString(), "4294967295");
        Section("integers");

        // docs:doubles
        double sum = 0.1 + 0.2;
        Is(sum.ToString(), "0.30000000000000004");
        Is(sum.ToString("F2"), "0.30");
        Is(1234.5678.ToString("N2"), "1,234.57");
        Is(0.1234.ToString("P1"), "12.3 %");
        Is(1234.56.ToString("E2"), "1.23E+003");
        // 0.125 is exact in binary: a tie, so the even digit.
        Is(0.125.ToString("F2"), "0.12");
        // docs:end
        Is(D(1.0 / 3).ToString(), "0.3333333333333333");
        Is(D(100).ToString(), "100");
        Is(D(1e16).ToString(), "10000000000000000");
        Is(D(1e17).ToString(), "1E+17");
        Is(D(123456789012345678).ToString(), "1.2345678901234568E+17");
        Is(D(1e-5).ToString(), "1E-05");
        Is(D(0.0001).ToString(), "0.0001");
        Is(D(double.MaxValue).ToString(), "1.7976931348623157E+308");
        Is(D(double.Epsilon).ToString(), "5E-324");
        Is(D(2.2250738585072014E-308).ToString(), "2.2250738585072014E-308");
        Is((-D(0)).ToString(), "-0");
        Is((-D(0)).ToString("F2"), "-0.00");
        Is((-D(0.001)).ToString("F2"), "-0.00");
        Is((-D(0)).ToString("C"), "(¤0.00)");
        Is((D(0) / D(0)).ToString(), "NaN");
        Is((D(1) / D(0)).ToString("N"), "Infinity");
        Is((D(-1) / D(0)).ToString(), "-Infinity");
        Is(D(2.675).ToString("F2"), "2.67");
        Is(D(0.375).ToString("F2"), "0.38");
        Is(D(2.5).ToString("F0"), "2");
        Is(D(3.5).ToString("F0"), "4");
        Is(D(9.995).ToString("F2"), "9.99");
        Is(D(9.9951).ToString("F2"), "10.00");
        Is(D(0.1).ToString("F20"), "0.10000000000000000555");
        Is(D(0.1).ToString("G17"), "0.10000000000000001");
        Is(D(1e23).ToString("F0"), "99999999999999991611392");
        Is(D(1e300).ToString("E3"), "1.000E+300");
        Is(D(1234.5678).ToString("G3"), "1.23E+03");
        Is(D(1234.5678).ToString("g3"), "1.23e+03");
        Is(D(0.0001234).ToString("G3"), "0.000123");
        Is(D(0.00001234).ToString("G3"), "1.23E-05");
        Is(D(1234.56).ToString("e2"), "1.23e+003");
        Is(D(1234.56).ToString("E0"), "1E+003");
        Is(D(-1234.5).ToString("C"), "(¤1,234.50)");
        Is(D(1234.5).ToString("C0"), "¤1,234");
        Is(D(999.999).ToString("N2"), "1,000.00");
        Is(D(0.5).ToString("N0"), "0");
        Is(D(5e-324).ToString("E5"), "4.94066E-324");
        Is(D(1).ToString("F100").Length.ToString(), "102");
        Is(D(Math.Pow(2, -25)).ToString(), "2.9802322387695312E-08");
        Section("double");

        Is(F(0.1f).ToString(), "0.1");
        Is((F(1f) / F(3f)).ToString(), "0.33333334");
        Is(F(16777217f).ToString(), "16777216");
        Is(F(123456789f).ToString(), "123456790");
        Is(F(1e9f).ToString(), "1E+09");
        Is(F(float.MaxValue).ToString(), "3.4028235E+38");
        Is(F(float.Epsilon).ToString(), "1E-45");
        Is(F(0.1f).ToString("F10"), "0.1000000015");
        Is(F(0.1f).ToString("G9"), "0.100000001");
        Is(F(3.14159f).ToString("E"), "3.141590E+000");
        Is(F(0.125f).ToString("F2"), "0.12");
        Is(((Half)F(0.1f)).ToString(), "0.1");
        Is(((Half)F(0.1f)).ToString("F2"), "0.10");
        Is(((Half)F(0.1f)).ToString("E"), "9.997559E-002");
        Is(((Half)F(65504f)).ToString("N"), "65,504.00");
        Is(Half.PositiveInfinity.ToString(), "Infinity");
        Section("float and Half");

        // docs:decimal
        decimal price = 2.675m;
        Is(price.ToString("F2"), "2.68");
        Is(1.50m.ToString(), "1.50");
        Is(1234567.891m.ToString("N"), "1,234,567.89");
        Is((-0.001m).ToString("F2"), "0.00");
        // docs:end
        Is(M(-2.665m).ToString("F2"), "-2.67");
        Is(M(0.005m).ToString("F2"), "0.01");
        Is(M(-0.5m).ToString("F0"), "-1");
        Is(M(1234.5m).ToString("E"), "1.234500E+003");
        Is(M(1234.5m).ToString("G2"), "1.2E+03");
        Is(M(1.50m).ToString("G0"), "1.5");
        Is(M(0.00001m).ToString(), "0.00001");
        Is(M(0.00001m).ToString("G5"), "1E-05");
        Is(M(0.125m).ToString("P1"), "12.5 %");
        Is(M(-3.5m).ToString("C"), "(¤3.50)");
        Is(M(25m).ToString("G1"), "3E+01");
        Is(decimal.MaxValue.ToString("N"), "79,228,162,514,264,337,593,543,950,335.00");
        Is(new decimal(0, 0, 0, true, 1).ToString(), "0.0");
        Section("decimal");

        // docs:format
        string row = string.Format("[{0,5}][{0,-5}][{1,8:F2}]", 42, 3.14159);
        Is(row, "[   42][42   ][    3.14]");
        double celsius = 21.456;
        Is($"{celsius:F1} °C", "21.5 °C");
        Is($"|{255,-6:x}|{5,6:B4}|", "|ff    |  0101|");
        // docs:end
        Is(string.Format("{1:N1}|{1,-6:F2}|", I(42), D(3.14159)), "3.1|3.14  |");
        Is(string.Format("{{{0}}}", I(1)), "{1}");
        Is(string.Format("{0 , 5 }", I(1)), "    1");
        Is(string.Format("{0:}", D(1.5)), "1.5");
        Is(string.Format("{0:F2}}}", D(1.5)), "1.50}");
        Is(string.Format("{0,4:X}", "ab"), "  ab");
        Is(string.Format("{0:X}", true), "True");
        Is(string.Format("{0,3}", 'c'), "  c");
        Is(string.Format("{2}{1}{0}", "a", "b", "c"), "cba");
        Is(string.Format("[{0}]", (object)null), "[]");
        Is(string.Format("{0,3:D2}|{1:P0}|{2:E1}", (byte)7, M(0.5m), F(1.5f)), " 07|50 %|1.5E+000");
        Is($"{(sbyte)-5} {(byte)200} {(short)-300} {(ushort)60000} {int.MinValue} {uint.MaxValue} {long.MinValue} {ulong.MaxValue}",
            "-5 200 -300 60000 -2147483648 4294967295 -9223372036854775808 18446744073709551615");
        Is(new StringBuilder().Append('[').AppendFormat("{0:X2}-{1:X2}", I(10), I(171)).Append(']').ToString(), "[0A-AB]");
        Is(new Point(D(0.1), D(1e20)).ToString(), "Point { X = 0.1, Y = 1E+20 }");
        Section("string.Format and $\"...\"");

        // docs:tryformat
        Span<char> buffer = stackalloc char[16];
        bool fits = 1234.5.TryFormat(buffer, out int written, "N1");
        Check("TryFormat writes into the span",
            fits && buffer.Slice(0, written).SequenceEqual("1,234.5"));
        Check("TryFormat that doesn't fit writes nothing",
            !long.MinValue.TryFormat(buffer.Slice(0, 8), out written, "N0")
            && written == 0);
        // docs:end
        Check("TryFormat, exactly the size", I(1234).TryFormat(buffer.Slice(0, 4), out written, default) && written == 4);
        Check("decimal TryFormat", M(1.5m).TryFormat(buffer, out written, "F3") && buffer.Slice(0, written).SequenceEqual("1.500"));
        Check("Half TryFormat", ((Half)F(0.5f)).TryFormat(buffer, out written, "P0") && buffer.Slice(0, written).SequenceEqual("50 %"));
        IFormattable formattable = D(0.5);
        Is(formattable.ToString("P0", null), "50 %");
        Is(I(255).ToString("X", null), "FF");
        Is(D(1.5).ToString((IFormatProvider)null), "1.5");
        Section("TryFormat and IFormattable");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine($"{s_failures} checks FAILED.");
        return s_failures;
    }
}
