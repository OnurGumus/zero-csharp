// Compares zerocore's number formatting (System/Number.Formatting.cs, in
// namespace ZeroTest here) with .NET's in the invariant culture, for random
// and edge values of every numeric type and every standard format.
// Usage: check-formats.sh [iterations] [seed]
using CultureInfo = System.Globalization.CultureInfo;
using NumberFormatInfo = System.Globalization.NumberFormatInfo;
using System;
using System.Globalization;
using ZeroTest;

static unsafe class Program
{
    static Random R = new Random(12345);
    static int failures;
    static long checks;
    static readonly char[] Buffer = new char[1 << 16];

    static readonly string[] FloatFormats =
    {
        "", "G", "g", "R", "R4", "r3", "R0", "G0", "G1", "G2", "G3", "G5", "G7", "G9", "G15", "G16", "G17", "G20", "G25", "g4",
        "F", "F0", "F1", "F2", "F3", "F5", "F10", "F17", "F25", "f2",
        "E", "E0", "E1", "E2", "E5", "E10", "E16", "E20", "e3",
        "N", "N0", "N1", "N3", "n2", "P", "P0", "P1", "P3", "p2", "C", "C0", "C3", "c1",
    };

    static readonly string[] IntegerFormats =
    {
        "", "G", "g", "R", "R4", "r3", "R0", "G0", "G1", "G2", "G3", "G5", "G10", "G25", "g3",
        "D", "D0", "D1", "D5", "D10", "D25", "d3", "X", "X0", "X2", "X8", "X16", "X20", "x", "x4",
        "B", "B8", "B70", "b3",
        "F", "F0", "F2", "F5", "E", "E0", "E2", "E5", "E20", "e1", "N", "N0", "N2", "P", "P0", "P1", "C", "C0",
    };

    static readonly string[] DecimalFormats =
    {
        "", "G", "g", "R", "R4", "r3", "R0", "G0", "G1", "G2", "G3", "G5", "G10", "G28", "G29", "G30", "g4",
        "F", "F0", "F1", "F2", "F5", "F30", "E", "E0", "E2", "E5", "E30", "e3",
        "N", "N0", "N2", "N4", "P", "P0", "P1", "C", "C0", "C3",
    };

    delegate void Formatter(char format, int precision, ref Number.Sink sink);

    static string Zero(string format, Formatter formatter)
    {
        try
        {
            char letter = Number.ParseFormat(format, out int precision);
            fixed (char* chars = Buffer)
            {
                var sink = new Number.Sink(chars, Buffer.Length);
                formatter(letter, precision, ref sink);
                if (!sink.Fits)
                    return "!too long";
                return new string(chars, 0, sink.Length);
            }
        }
        catch (Exception e)
        {
            return "!" + e.GetType().Name;
        }
    }

    static string Net(Func<string> format)
    {
        try { return format(); }
        catch (Exception e) { return "!" + e.GetType().Name; }
    }

    static void Compare(string what, string format, string expected, string actual)
    {
        checks++;
        if (expected == actual)
            return;
        failures++;
        if (failures <= 40)
            Console.WriteLine($"{what} \"{format}\": .NET [{expected}], zerocore [{actual}]");
    }

    // .NET's shortest text for a few powers of two doesn't read back as the
    // same double (2^-25 is "2.980232238769531E-08", which parses as the
    // double below it). There zerocore must give text that does read back.
    static int netRoundTripBugs;

    static void CheckDouble(double v)
    {
        foreach (string f in FloatFormats)
        {
            string expected = Net(() => v.ToString(f, CultureInfo.InvariantCulture));
            string actual = Zero(f, (char l, int p, ref Number.Sink s) => Number.FormatDouble(v, l, p, ref s));
            bool shortest = f is "" or "G" or "g" or "R" or "R4" or "r3" or "R0" or "G0";
            if (shortest && double.IsFinite(v) && double.Parse(expected, CultureInfo.InvariantCulture) != v)
            {
                netRoundTripBugs++;
                expected = double.Parse(actual, CultureInfo.InvariantCulture) == v ? actual : "text that reads back";
            }
            Compare($"double {v:R} (0x{BitConverter.DoubleToUInt64Bits(v):X16})", f, expected, actual);
        }
    }

    static void CheckSingle(float v)
    {
        foreach (string f in FloatFormats)
            Compare($"float {v:R} (0x{BitConverter.SingleToUInt32Bits(v):X8})", f,
                Net(() => v.ToString(f, CultureInfo.InvariantCulture)),
                Zero(f, (char l, int p, ref Number.Sink s) => Number.FormatSingle(v, l, p, ref s)));
    }

    static void CheckHalf(Half v)
    {
        foreach (string f in FloatFormats)
            Compare($"Half {v} (0x{BitConverter.HalfToUInt16Bits(v):X4})", f,
                Net(() => v.ToString(f, CultureInfo.InvariantCulture)),
                Zero(f, (char l, int p, ref Number.Sink s) => Number.FormatHalf(BitConverter.HalfToUInt16Bits(v), l, p, ref s)));
    }

    static void CheckInteger<T>(T v, ulong raw, bool signed, int bits) where T : IFormattable
    {
        foreach (string f in IntegerFormats)
            Compare($"{typeof(T).Name} {v}", f,
                Net(() => v.ToString(f, CultureInfo.InvariantCulture)),
                Zero(f, (char l, int p, ref Number.Sink s) => Number.FormatInteger(raw, signed, bits, l, p, ref s)));
    }

    static void CheckDecimal(decimal v)
    {
        int[] parts = decimal.GetBits(v);
        ulong lo = (uint)parts[0] | ((ulong)(uint)parts[1] << 32);
        uint hi = (uint)parts[2];
        int scale = (parts[3] >> 16) & 0xFF;
        bool negative = parts[3] < 0;
        foreach (string f in DecimalFormats)
            Compare($"decimal {v}", f,
                Net(() => v.ToString(f, CultureInfo.InvariantCulture)),
                Zero(f, (char l, int p, ref Number.Sink s) => Number.FormatDecimal(lo, hi, scale, negative, l, p, ref s)));
    }

    static void CheckAllIntegers(long value)
    {
        CheckInteger((sbyte)value, (ulong)(long)(sbyte)value, true, 8);
        CheckInteger((byte)value, (byte)value, false, 8);
        CheckInteger((short)value, (ulong)(long)(short)value, true, 16);
        CheckInteger((ushort)value, (ushort)value, false, 16);
        CheckInteger((int)value, (ulong)(long)(int)value, true, 32);
        CheckInteger((uint)value, (uint)value, false, 32);
        CheckInteger(value, (ulong)value, true, 64);
        CheckInteger((ulong)value, (ulong)value, false, 64);
    }

    static double RandomDouble()
    {
        switch (R.Next(8))
        {
            case 0: return BitConverter.UInt64BitsToDouble((ulong)R.NextInt64() ^ ((ulong)R.Next(2) << 63));
            case 1: return R.Next(-100000, 100000) / Math.Pow(10, R.Next(0, 8));
            case 2: return R.Next(-1000, 1000) + R.Next(0, 8) / 8.0;
            case 3: return Math.Pow(10, R.Next(-325, 309)) * (R.Next(2) == 0 ? 1 : -1);
            case 4: return Math.Pow(2, R.Next(-1074, 1024));
            case 5: return BitConverter.UInt64BitsToDouble((ulong)R.Next(0, 1 << 20));
            case 6: return R.NextDouble() * Math.Pow(10, R.Next(-30, 30));
            default: return R.NextInt64() / 7.0;
        }
    }

    static long RandomLong() => R.Next(6) switch
    {
        0 => R.Next(-1000, 1000),
        1 => R.NextInt64(),
        2 => -R.NextInt64(),
        3 => R.Next(int.MinValue, int.MaxValue),
        4 => (long)Math.Pow(10, R.Next(0, 19)) * (R.Next(2) == 0 ? 1 : -1) + R.Next(-2, 3),
        _ => R.NextInt64() >> R.Next(0, 63),
    };

    static decimal RandomDecimal()
    {
        int lo = 0, mid = 0, hi = 0;
        int limbs = R.Next(4);
        if (limbs >= 1) lo = R.Next(int.MinValue, int.MaxValue);
        if (limbs >= 2) mid = R.Next(int.MinValue, int.MaxValue);
        if (limbs >= 3) hi = R.Next(int.MinValue, int.MaxValue) >> R.Next(0, 32);
        if (R.Next(8) == 0) lo = R.Next(0, 1000);
        return new decimal(lo, mid, hi, R.Next(2) == 0, (byte)R.Next(0, 29));
    }

    static int Main(string[] args)
    {
        int iterations = args.Length > 0 ? int.Parse(args[0]) : 100000;
        R = new Random(args.Length > 1 ? int.Parse(args[1]) : 12345);

        double[] doubles =
        {
            0.0, -0.0, 1, -1, 0.1, 0.2, 0.3, 0.1 + 0.2, 1.0 / 3, 2.0 / 3, 0.125, 0.375, 2.5, 3.5, 0.5, 1.5, 2.675, 1.005, 0.045,
            1e15, 1e16, 1e17, 1e21, 1e22, 1e23, 1e-5, 1e-4, 123456789012345678, 9007199254740993,
            double.MaxValue, double.MinValue, double.Epsilon, 2.2250738585072014E-308, 2.2250738585072009E-308,
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, 5e-324, 9.995, 9.9951, 0.05, 0.25, 0.35, 999.999,
            Math.PI, Math.E, 1e300, -1e-300, 4.35, 0.15, 1234.5678, 0.0001234, 0.00001234,
            Math.Pow(2, -25), Math.Pow(2, -957),
        };
        for (int e = -1074; e <= 1023; e++)
        {
            CheckDouble(Math.Pow(2, e));
            CheckSingle(MathF.Pow(2, e));
        }
        foreach (double d in doubles)
        {
            CheckDouble(d);
            CheckSingle((float)d);
            CheckHalf((Half)d);
        }
        long[] longs = { 0, 1, -1, 5, 15, 25, 99950, 12345, 12355, long.MinValue, long.MaxValue, int.MinValue, int.MaxValue, 255, 256, -128, 1000000 };
        foreach (long l in longs)
            CheckAllIntegers(l);
        CheckInteger(ulong.MaxValue, ulong.MaxValue, false, 64);
        decimal[] decimals = { 0m, -0m, 0.00m, new decimal(0, 0, 0, true, 3), 1.50m, 2.675m, -2.665m, 0.005m, -0.5m, -0.001m, 0.00001m, decimal.MaxValue, decimal.MinValue, 0.0000000000000000000000000001m, 79228162514264337593543950335m, 25m, 15m, 1234.5m };
        foreach (decimal m in decimals)
            CheckDecimal(m);
        for (ushort h = 0; h < ushort.MaxValue; h++)
            if (h % 7 == 0 || h < 1100)
                CheckHalf(BitConverter.UInt16BitsToHalf(h));

        for (int i = 0; i < iterations; i++)
        {
            double d = RandomDouble();
            CheckDouble(d);
            if (i % 3 == 0)
                CheckSingle(BitConverter.UInt32BitsToSingle((uint)R.Next() ^ ((uint)R.Next(2) << 31)));
            CheckSingle((float)d);
            if (i % 4 == 0)
                CheckAllIntegers(RandomLong());
            if (i % 4 == 1)
                CheckDecimal(RandomDecimal());
        }

        if (netRoundTripBugs > 0)
            Console.WriteLine($"{netRoundTripBugs} checks where .NET's shortest text doesn't read back (zerocore's must).");
        Console.WriteLine(failures == 0 ? $"All {checks} checks passed." : $"{failures} of {checks} checks FAILED.");
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
        public ZeroTest.NumberFormatInfo NumberFormat => ZeroTest.NumberFormatInfo.InvariantInfo;
        public object GetFormat(Type formatType) => null;
    }
}
