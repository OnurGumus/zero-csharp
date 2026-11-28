// Parse and TryParse for the numbers, with .NET's overloads; the parsing
// itself is in Number.Parsing.cs. A null string is an ArgumentNullException
// for Parse and false for TryParse. The provider's NumberFormatInfo (or the
// current culture's) gives the symbols; the invariant culture's by default.

using System.Globalization;

namespace System
{
    public unsafe partial struct SByte
    {
        public static sbyte Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Integer, null);
        public static sbyte Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static sbyte Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Integer, provider);
        public static sbyte Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static sbyte Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(string s, out sbyte result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out sbyte result) => TryParse(s, NumberStyles.Integer, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out sbyte result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out sbyte result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out sbyte result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static sbyte Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider provider = null) =>
            unchecked((sbyte)Number.ParseInteger(s, style, true, 8, 3, "Value was either too large or too small for a signed byte.", provider));

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out sbyte result)
        {
            bool parsed = Number.TryParseInteger(s, style, true, 8, 3, provider, out ulong raw);
            result = unchecked((sbyte)raw);
            return parsed;
        }
    }

    public unsafe partial struct Byte
    {
        public static byte Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Integer, null);
        public static byte Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static byte Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Integer, provider);
        public static byte Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static byte Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(string s, out byte result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out byte result) => TryParse(s, NumberStyles.Integer, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out byte result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out byte result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out byte result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static byte Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider provider = null) =>
            unchecked((byte)Number.ParseInteger(s, style, false, 8, 3, "Value was either too large or too small for an unsigned byte.", provider));

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out byte result)
        {
            bool parsed = Number.TryParseInteger(s, style, false, 8, 3, provider, out ulong raw);
            result = unchecked((byte)raw);
            return parsed;
        }
    }

    public unsafe partial struct Int16
    {
        public static short Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Integer, null);
        public static short Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static short Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Integer, provider);
        public static short Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static short Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(string s, out short result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out short result) => TryParse(s, NumberStyles.Integer, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out short result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out short result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out short result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static short Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider provider = null) =>
            unchecked((short)Number.ParseInteger(s, style, true, 16, 5, "Value was either too large or too small for an Int16.", provider));

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out short result)
        {
            bool parsed = Number.TryParseInteger(s, style, true, 16, 5, provider, out ulong raw);
            result = unchecked((short)raw);
            return parsed;
        }
    }

    public unsafe partial struct UInt16
    {
        public static ushort Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Integer, null);
        public static ushort Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static ushort Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Integer, provider);
        public static ushort Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static ushort Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(string s, out ushort result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out ushort result) => TryParse(s, NumberStyles.Integer, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out ushort result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out ushort result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out ushort result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static ushort Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider provider = null) =>
            unchecked((ushort)Number.ParseInteger(s, style, false, 16, 5, "Value was either too large or too small for a UInt16.", provider));

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out ushort result)
        {
            bool parsed = Number.TryParseInteger(s, style, false, 16, 5, provider, out ulong raw);
            result = unchecked((ushort)raw);
            return parsed;
        }
    }

    public unsafe partial struct Int32
    {
        public static int Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Integer, null);
        public static int Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static int Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Integer, provider);
        public static int Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static int Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(string s, out int result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out int result) => TryParse(s, NumberStyles.Integer, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out int result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out int result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out int result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static int Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider provider = null) =>
            unchecked((int)Number.ParseInteger(s, style, true, 32, 10, "Value was either too large or too small for an Int32.", provider));

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out int result)
        {
            bool parsed = Number.TryParseInteger(s, style, true, 32, 10, provider, out ulong raw);
            result = unchecked((int)raw);
            return parsed;
        }
    }

    public unsafe partial struct UInt32
    {
        public static uint Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Integer, null);
        public static uint Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static uint Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Integer, provider);
        public static uint Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static uint Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(string s, out uint result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out uint result) => TryParse(s, NumberStyles.Integer, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out uint result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out uint result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out uint result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static uint Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider provider = null) =>
            unchecked((uint)Number.ParseInteger(s, style, false, 32, 10, "Value was either too large or too small for a UInt32.", provider));

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out uint result)
        {
            bool parsed = Number.TryParseInteger(s, style, false, 32, 10, provider, out ulong raw);
            result = unchecked((uint)raw);
            return parsed;
        }
    }

    public unsafe partial struct Int64
    {
        public static long Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Integer, null);
        public static long Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static long Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Integer, provider);
        public static long Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static long Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(string s, out long result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out long result) => TryParse(s, NumberStyles.Integer, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out long result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out long result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out long result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static long Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider provider = null) =>
            unchecked((long)Number.ParseInteger(s, style, true, 64, 19, "Value was either too large or too small for an Int64.", provider));

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out long result)
        {
            bool parsed = Number.TryParseInteger(s, style, true, 64, 19, provider, out ulong raw);
            result = unchecked((long)raw);
            return parsed;
        }
    }

    public unsafe partial struct UInt64
    {
        public static ulong Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Integer, null);
        public static ulong Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static ulong Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Integer, provider);
        public static ulong Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static ulong Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(string s, out ulong result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out ulong result) => TryParse(s, NumberStyles.Integer, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out ulong result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out ulong result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out ulong result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static ulong Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider provider = null) =>
            unchecked((ulong)Number.ParseInteger(s, style, false, 64, 20, "Value was either too large or too small for a UInt64.", provider));

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out ulong result)
        {
            bool parsed = Number.TryParseInteger(s, style, false, 64, 20, provider, out ulong raw);
            result = unchecked((ulong)raw);
            return parsed;
        }
    }

    public unsafe partial struct IntPtr
    {
        public static nint Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Integer, null);
        public static nint Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static nint Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Integer, provider);
        public static nint Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static nint Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(string s, out nint result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out nint result) => TryParse(s, NumberStyles.Integer, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out nint result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out nint result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out nint result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static nint Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider provider = null) =>
            unchecked((nint)Number.ParseInteger(s, style, true, sizeof(nint) * 8, sizeof(nint) == 8 ? 19 : 10, sizeof(nint) == 8 ? "Value was either too large or too small for an Int64." : "Value was either too large or too small for an Int32.", provider));

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out nint result)
        {
            bool parsed = Number.TryParseInteger(s, style, true, sizeof(nint) * 8, sizeof(nint) == 8 ? 19 : 10, provider, out ulong raw);
            result = unchecked((nint)raw);
            return parsed;
        }
    }

    public unsafe partial struct UIntPtr
    {
        public static nuint Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Integer, null);
        public static nuint Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static nuint Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Integer, provider);
        public static nuint Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static nuint Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(string s, out nuint result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out nuint result) => TryParse(s, NumberStyles.Integer, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out nuint result) => TryParse(Number.AsSpan(s), NumberStyles.Integer, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out nuint result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out nuint result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static nuint Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider provider = null) =>
            unchecked((nuint)Number.ParseInteger(s, style, false, sizeof(nuint) * 8, sizeof(nuint) == 8 ? 20 : 10, sizeof(nuint) == 8 ? "Value was either too large or too small for a UInt64." : "Value was either too large or too small for a UInt32.", provider));

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out nuint result)
        {
            bool parsed = Number.TryParseInteger(s, style, false, sizeof(nuint) * 8, sizeof(nuint) == 8 ? 20 : 10, provider, out ulong raw);
            result = unchecked((nuint)raw);
            return parsed;
        }
    }

    public unsafe partial struct Single
    {
        public static float Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Float | NumberStyles.AllowThousands, null);
        public static float Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static float Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Float | NumberStyles.AllowThousands, provider);
        public static float Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static float Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider);
        public static bool TryParse(string s, out float result) => TryParse(Number.AsSpan(s), NumberStyles.Float | NumberStyles.AllowThousands, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out float result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out float result) => TryParse(Number.AsSpan(s), NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out float result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out float result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static float Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Float | NumberStyles.AllowThousands, IFormatProvider provider = null)
        {
            ulong bits = Number.ParseFloat(s, style, Number.SingleFormat, provider);
            return BitConverter.UInt32BitsToSingle((uint)bits);
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out float result)
        {
            bool parsed = Number.TryParseFloat(s, style, Number.SingleFormat, provider, out ulong bits);
            result = BitConverter.UInt32BitsToSingle((uint)bits);
            return parsed;
        }
    }

    public unsafe partial struct Double
    {
        public static double Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Float | NumberStyles.AllowThousands, null);
        public static double Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static double Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Float | NumberStyles.AllowThousands, provider);
        public static double Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static double Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider);
        public static bool TryParse(string s, out double result) => TryParse(Number.AsSpan(s), NumberStyles.Float | NumberStyles.AllowThousands, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out double result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out double result) => TryParse(Number.AsSpan(s), NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out double result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out double result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static double Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Float | NumberStyles.AllowThousands, IFormatProvider provider = null)
        {
            ulong bits = Number.ParseFloat(s, style, Number.DoubleFormat, provider);
            return BitConverter.UInt64BitsToDouble(bits);
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out double result)
        {
            bool parsed = Number.TryParseFloat(s, style, Number.DoubleFormat, provider, out ulong bits);
            result = BitConverter.UInt64BitsToDouble(bits);
            return parsed;
        }
    }

    public readonly unsafe partial struct Half
    {
        public static Half Parse(string s) => Parse(Number.NotNull(s), NumberStyles.Float | NumberStyles.AllowThousands, null);
        public static Half Parse(string s, NumberStyles style) => Parse(Number.NotNull(s), style, null);
        public static Half Parse(string s, IFormatProvider provider) => Parse(Number.NotNull(s), NumberStyles.Float | NumberStyles.AllowThousands, provider);
        public static Half Parse(string s, NumberStyles style, IFormatProvider provider) => Parse(Number.NotNull(s), style, provider);
        public static Half Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider);
        public static bool TryParse(string s, out Half result) => TryParse(Number.AsSpan(s), NumberStyles.Float | NumberStyles.AllowThousands, null, out result);
        public static bool TryParse(ReadOnlySpan<char> s, out Half result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, null, out result);
        public static bool TryParse(string s, IFormatProvider provider, out Half result) => TryParse(Number.AsSpan(s), NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out Half result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);
        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out Half result) => TryParse(Number.AsSpan(s), style, provider, out result);

        public static Half Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Float | NumberStyles.AllowThousands, IFormatProvider provider = null)
        {
            ulong bits = Number.ParseFloat(s, style, Number.HalfFormat, provider);
            return BitConverter.UInt16BitsToHalf((ushort)bits);
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out Half result)
        {
            bool parsed = Number.TryParseFloat(s, style, Number.HalfFormat, provider, out ulong bits);
            result = BitConverter.UInt16BitsToHalf((ushort)bits);
            return parsed;
        }
    }
}
