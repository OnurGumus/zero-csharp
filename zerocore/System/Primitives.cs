// The primitive types. The compiler knows their sizes; they need no fields.
// Each gets Equals, GetHashCode and ToString, which records, anonymous types,
// EqualityComparer<T> and `"n=" + 5` call. The numbers also take .NET's
// standard format strings, ToString("X4") and TryFormat (Number.Formatting.cs),
// which string.Format and $"{x:F2}" use.

namespace System
{
    public struct Void { }

    public struct Boolean : IEquatable<bool>, IComparable<bool>, IComparable
    {
        public int CompareTo(object obj) => obj is bool other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type Boolean.");

        public bool Equals(bool other) => (bool)this == other;
        public int CompareTo(bool other) => (bool)this == other ? 0 : (bool)this ? 1 : -1;
        public override bool Equals(object obj) => obj is bool other && (bool)this == other;
        public override int GetHashCode() => (bool)this ? 1 : 0;
        public override string ToString() => (bool)this ? "True" : "False";
    }

    // Char is in Char.cs.

    public unsafe partial struct SByte : IEquatable<sbyte>, IComparable<sbyte>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is sbyte other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type SByte.");

        public const sbyte MaxValue = 0x7F;
        public const sbyte MinValue = -0x80;

        public bool Equals(sbyte other) => (sbyte)this == other;

        public int CompareTo(sbyte other) => (sbyte)this < other ? -1 : (sbyte)this > other ? 1 : 0;
        public override bool Equals(object obj) => obj is sbyte other && (sbyte)this == other;
        public override int GetHashCode() => unchecked((int)this);
        public override string ToString() => Number.FormatInt64((long)this);
        public string ToString(string format) => Number.IntegerToString((ulong)(long)(sbyte)this, true, 8, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatInteger((ulong)(long)(sbyte)this, true, 8, destination, out charsWritten, format);
    }

    public unsafe partial struct Byte : IEquatable<byte>, IComparable<byte>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is byte other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type Byte.");

        public const byte MaxValue = 0xFF;
        public const byte MinValue = 0;

        public bool Equals(byte other) => (byte)this == other;

        public int CompareTo(byte other) => (byte)this < other ? -1 : (byte)this > other ? 1 : 0;
        public override bool Equals(object obj) => obj is byte other && (byte)this == other;
        public override int GetHashCode() => unchecked((int)this);
        public override string ToString() => Number.FormatUInt64((ulong)this);
        public string ToString(string format) => Number.IntegerToString((ulong)(byte)this, false, 8, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatInteger((ulong)(byte)this, false, 8, destination, out charsWritten, format);
    }

    public unsafe partial struct Int16 : IEquatable<short>, IComparable<short>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is short other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type Int16.");

        public const short MaxValue = 0x7FFF;
        public const short MinValue = -0x8000;

        public bool Equals(short other) => (short)this == other;

        public int CompareTo(short other) => (short)this < other ? -1 : (short)this > other ? 1 : 0;
        public override bool Equals(object obj) => obj is short other && (short)this == other;
        public override int GetHashCode() => unchecked((int)this);
        public override string ToString() => Number.FormatInt64((long)this);
        public string ToString(string format) => Number.IntegerToString((ulong)(long)(short)this, true, 16, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatInteger((ulong)(long)(short)this, true, 16, destination, out charsWritten, format);
    }

    public unsafe partial struct UInt16 : IEquatable<ushort>, IComparable<ushort>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is ushort other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type UInt16.");

        public const ushort MaxValue = 0xFFFF;
        public const ushort MinValue = 0;

        public bool Equals(ushort other) => (ushort)this == other;

        public int CompareTo(ushort other) => (ushort)this < other ? -1 : (ushort)this > other ? 1 : 0;
        public override bool Equals(object obj) => obj is ushort other && (ushort)this == other;
        public override int GetHashCode() => unchecked((int)this);
        public override string ToString() => Number.FormatUInt64((ulong)this);
        public string ToString(string format) => Number.IntegerToString((ulong)(ushort)this, false, 16, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatInteger((ulong)(ushort)this, false, 16, destination, out charsWritten, format);
    }

    public unsafe partial struct Int32 : IEquatable<int>, IComparable<int>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is int other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type Int32.");

        public const int MaxValue = 0x7FFFFFFF;
        public const int MinValue = -0x80000000;

        public bool Equals(int other) => (int)this == other;

        public int CompareTo(int other) => (int)this < other ? -1 : (int)this > other ? 1 : 0;
        public override bool Equals(object obj) => obj is int other && (int)this == other;
        public override int GetHashCode() => unchecked((int)this);
        public override string ToString() => Number.FormatInt64((long)this);
        public string ToString(string format) => Number.IntegerToString((ulong)(long)(int)this, true, 32, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatInteger((ulong)(long)(int)this, true, 32, destination, out charsWritten, format);
    }

    public unsafe partial struct UInt32 : IEquatable<uint>, IComparable<uint>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is uint other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type UInt32.");

        public const uint MaxValue = 0xFFFFFFFF;
        public const uint MinValue = 0;

        public bool Equals(uint other) => (uint)this == other;

        public int CompareTo(uint other) => (uint)this < other ? -1 : (uint)this > other ? 1 : 0;
        public override bool Equals(object obj) => obj is uint other && (uint)this == other;
        public override int GetHashCode() => unchecked((int)this);
        public override string ToString() => Number.FormatUInt64((ulong)this);
        public string ToString(string format) => Number.IntegerToString((ulong)(uint)this, false, 32, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatInteger((ulong)(uint)this, false, 32, destination, out charsWritten, format);
    }

    public unsafe partial struct Int64 : IEquatable<long>, IComparable<long>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is long other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type Int64.");

        public const long MaxValue = 0x7FFFFFFFFFFFFFFF;
        public const long MinValue = -0x8000000000000000;

        public bool Equals(long other) => (long)this == other;

        public int CompareTo(long other) => (long)this < other ? -1 : (long)this > other ? 1 : 0;
        public override bool Equals(object obj) => obj is long other && (long)this == other;
        public override int GetHashCode() => unchecked((int)this ^ (int)((long)this >> 32));
        public override string ToString() => Number.FormatInt64((long)this);
        public string ToString(string format) => Number.IntegerToString((ulong)(long)this, true, 64, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatInteger((ulong)(long)this, true, 64, destination, out charsWritten, format);
    }

    public unsafe partial struct UInt64 : IEquatable<ulong>, IComparable<ulong>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is ulong other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type UInt64.");

        public const ulong MaxValue = 0xFFFFFFFFFFFFFFFF;
        public const ulong MinValue = 0;

        public bool Equals(ulong other) => (ulong)this == other;

        public int CompareTo(ulong other) => (ulong)this < other ? -1 : (ulong)this > other ? 1 : 0;
        public override bool Equals(object obj) => obj is ulong other && (ulong)this == other;
        public override int GetHashCode() => unchecked((int)this ^ (int)((ulong)this >> 32));
        public override string ToString() => Number.FormatUInt64((ulong)this);
        public string ToString(string format) => Number.IntegerToString((ulong)this, false, 64, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatInteger((ulong)this, false, 64, destination, out charsWritten, format);
    }

    public unsafe partial struct IntPtr : IEquatable<nint>, IComparable<nint>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is nint other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type IntPtr.");

        public bool Equals(nint other) => (nint)this == other;
        public int CompareTo(nint other) => (nint)this < other ? -1 : (nint)this > other ? 1 : 0;
        public static readonly nint Zero;

        public static int Size
        {
            get => sizeof(nint);
        }

        public static nint MaxValue => sizeof(nint) == 8 ? (nint)long.MaxValue : (nint)int.MaxValue;
        public static nint MinValue => sizeof(nint) == 8 ? (nint)long.MinValue : (nint)int.MinValue;

        public static nint Min(nint x, nint y) => x <= y ? x : y;
        public static nint Max(nint x, nint y) => x >= y ? x : y;

        public static (nint Quotient, nint Remainder) DivRem(nint left, nint right)
        {
            nint quotient = left / right;
            return (quotient, left - quotient * right);
        }

        public override bool Equals(object obj) => obj is nint other && (nint)this == other;
        public override int GetHashCode() => unchecked((int)(long)(nint)this ^ (int)((long)(nint)this >> 32));
        public override string ToString() => Number.FormatInt64((nint)this);
        public string ToString(string format) => Number.IntegerToString((ulong)(long)(nint)this, true, sizeof(nint) * 8, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatInteger((ulong)(long)(nint)this, true, sizeof(nint) * 8, destination, out charsWritten, format);
    }

    public unsafe partial struct UIntPtr : IEquatable<nuint>, IComparable<nuint>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is nuint other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type UIntPtr.");

        public bool Equals(nuint other) => (nuint)this == other;
        public int CompareTo(nuint other) => (nuint)this < other ? -1 : (nuint)this > other ? 1 : 0;
        public static readonly nuint Zero;

        public static int Size
        {
            get => sizeof(nuint);
        }

        public override bool Equals(object obj) => obj is nuint other && (nuint)this == other;
        public override int GetHashCode() => unchecked((int)(ulong)(nuint)this ^ (int)((ulong)(nuint)this >> 32));
        public override string ToString() => Number.FormatUInt64((nuint)this);
        public string ToString(string format) => Number.IntegerToString((ulong)(nuint)this, false, sizeof(nuint) * 8, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatInteger((ulong)(nuint)this, false, sizeof(nuint) * 8, destination, out charsWritten, format);
    }

    // float and double have .NET's constants (NaN, PositiveInfinity, MaxValue,
    // Epsilon, ...) and checks (IsNaN, IsFinite, IsSubnormal, ...).
    //
    // Equals here is bit equality, so NaN equals NaN, as .NET's Equals has
    // it (unlike ==); 0.0 and -0.0 are equal, as in .NET.
    public unsafe partial struct Single : IEquatable<float>, IComparable<float>, IComparable, ISpanFormattable,
        System.Numerics.Tensors.IFloatTensorElement
    {
        public int CompareTo(object obj) => obj is float other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type Single.");

        public const float MinValue = -3.40282347E+38f;
        public const float MaxValue = 3.40282347E+38f;
        public const float Epsilon = 1.401298E-45f;
        public const float NaN = 0.0f / 0.0f;
        public const float PositiveInfinity = 1.0f / 0.0f;
        public const float NegativeInfinity = -1.0f / 0.0f;
        public const float NegativeZero = -0.0f;
        public const float E = 2.71828183f;
        public const float Pi = 3.14159265f;
        public const float Tau = 6.28318531f;

        public static bool IsNaN(float f) => f != f;
        public static bool IsInfinity(float f) => (Bits(f) & 0x7FFFFFFF) == 0x7F800000;
        public static bool IsPositiveInfinity(float f) => f == PositiveInfinity;
        public static bool IsNegativeInfinity(float f) => f == NegativeInfinity;
        public static bool IsFinite(float f) => (Bits(f) & 0x7FFFFFFF) < 0x7F800000;
        public static bool IsNegative(float f) => (int)Bits(f) < 0;
        public static bool IsNormal(float f) => (Bits(f) & 0x7FFFFFFF) - 0x00800000 < 0x7F000000;
        public static bool IsSubnormal(float f) => (Bits(f) & 0x7FFFFFFF) - 1 < 0x007FFFFF;

        private static uint Bits(float f) => *(uint*)&f;

        public bool Equals(float other) => (float)this == other || ((float)this != (float)this && other != other);

        // As in .NET: NaN goes before every number and equals itself.

        public int CompareTo(float other)

        {

            float value = (float)this;

            if (value < other) return -1;

            if (value > other) return 1;

            if (value == other) return 0;

            if (value != value) return other != other ? 0 : -1;

            return 1;

        }
        public override bool Equals(object obj) => obj is float other && Equals(other);
        public override int GetHashCode()
        {
            float value = (float)this == 0 ? 0 : (float)this;
            return *(int*)&value;
        }
        public override string ToString() => ToString((string)null);
        public string ToString(string format)
        {
            float value = (float)this;
            return Number.IeeeToString(*(uint*)&value, 23, 8, 9, format);
        }
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null)
        {
            float value = (float)this;
            return Number.TryFormatIeee(*(uint*)&value, 23, 8, 9, destination, out charsWritten, format);
        }
    }

    public unsafe partial struct Double : IEquatable<double>, IComparable<double>, IComparable, ISpanFormattable
    {
        public int CompareTo(object obj) => obj is double other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type Double.");

        public const double MinValue = -1.7976931348623157E+308;
        public const double MaxValue = 1.7976931348623157E+308;
        public const double Epsilon = 4.9406564584124654E-324;
        public const double NaN = 0.0 / 0.0;
        public const double PositiveInfinity = 1.0 / 0.0;
        public const double NegativeInfinity = -1.0 / 0.0;
        public const double NegativeZero = -0.0;
        public const double E = 2.7182818284590452354;
        public const double Pi = 3.14159265358979323846;
        public const double Tau = 6.283185307179586476925;

        public static bool IsNaN(double d) => d != d;
        public static bool IsInfinity(double d) => (Bits(d) & 0x7FFFFFFFFFFFFFFF) == 0x7FF0000000000000;
        public static bool IsPositiveInfinity(double d) => d == PositiveInfinity;
        public static bool IsNegativeInfinity(double d) => d == NegativeInfinity;
        public static bool IsFinite(double d) => (Bits(d) & 0x7FFFFFFFFFFFFFFF) < 0x7FF0000000000000;
        public static bool IsNegative(double d) => (long)Bits(d) < 0;
        public static bool IsNormal(double d) => (Bits(d) & 0x7FFFFFFFFFFFFFFF) - 0x0010000000000000 < 0x7FE0000000000000;
        public static bool IsSubnormal(double d) => (Bits(d) & 0x7FFFFFFFFFFFFFFF) - 1 < 0x000FFFFFFFFFFFFF;

        private static ulong Bits(double d) => *(ulong*)&d;

        public bool Equals(double other) => (double)this == other || ((double)this != (double)this && other != other);

        // As in .NET: NaN goes before every number and equals itself.

        public int CompareTo(double other)

        {

            double value = (double)this;

            if (value < other) return -1;

            if (value > other) return 1;

            if (value == other) return 0;

            if (value != value) return other != other ? 0 : -1;

            return 1;

        }
        public override bool Equals(object obj) => obj is double other && Equals(other);
        public override int GetHashCode()
        {
            double value = (double)this == 0 ? 0 : (double)this;
            long bits = *(long*)&value;
            return unchecked((int)bits ^ (int)(bits >> 32));
        }
        public override string ToString() => ToString((string)null);
        public string ToString(string format)
        {
            double value = (double)this;
            return Number.IeeeToString(*(ulong*)&value, 52, 11, 17, format);
        }
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null)
        {
            double value = (double)this;
            return Number.TryFormatIeee(*(ulong*)&value, 52, 11, 17, destination, out charsWritten, format);
        }
    }
}
