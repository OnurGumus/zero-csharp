using System.Runtime.CompilerServices;

// System.Half: an IEEE 754 binary16 number (1 sign bit, 5 exponent bits, 10
// fraction bits), as in .NET. The compiler has no half-precision type, so it's
// a struct holding the bits. Arithmetic converts to float and rounds back,
// which gives the correctly rounded result: a float holds more than twice
// Half's precision. Conversions round to nearest, ties to even.
namespace System
{
    public readonly unsafe partial struct Half : IEquatable<Half>, IComparable<Half>, ISpanFormattable
    {
        private const ushort SignMask = 0x8000;
        private const ushort ExponentMask = 0x7C00;
        private const ushort FractionMask = 0x03FF;

        internal readonly ushort _value;

        internal Half(ushort value) => _value = value;

        public static Half Epsilon => new Half(0x0001);
        public static Half PositiveInfinity => new Half(0x7C00);
        public static Half NegativeInfinity => new Half(0xFC00);
        public static Half NaN => new Half(0xFE00);
        public static Half MinValue => new Half(0xFBFF);
        public static Half MaxValue => new Half(0x7BFF);
        public static Half Zero => new Half(0x0000);
        public static Half NegativeZero => new Half(0x8000);
        public static Half One => new Half(0x3C00);
        public static Half NegativeOne => new Half(0xBC00);
        public static Half E => new Half(0x4170);
        public static Half Pi => new Half(0x4248);
        public static Half Tau => new Half(0x4648);

        public static bool IsNaN(Half value) => (value._value & ~SignMask) > ExponentMask;
        public static bool IsInfinity(Half value) => (value._value & ~SignMask) == ExponentMask;
        public static bool IsPositiveInfinity(Half value) => value._value == 0x7C00;
        public static bool IsNegativeInfinity(Half value) => value._value == 0xFC00;
        public static bool IsFinite(Half value) => (value._value & ExponentMask) != ExponentMask;
        public static bool IsNegative(Half value) => (short)value._value < 0;
        internal static bool IsZero(Half value) => (value._value & ~SignMask) == 0;

        public static bool IsNormal(Half value)
        {
            int exponent = value._value & ExponentMask;
            return exponent != 0 && exponent != ExponentMask;
        }

        public static bool IsSubnormal(Half value) =>
            (value._value & ExponentMask) == 0 && (value._value & FractionMask) != 0;

        public static Half Abs(Half value) => new Half((ushort)(value._value & ~SignMask));

        // NaN propagates, as in .NET.
        public static Half Min(Half x, Half y) => (Half)MathF.Min((float)x, (float)y);
        public static Half Max(Half x, Half y) => (Half)MathF.Max((float)x, (float)y);

        // To float and double: exact.
        public static explicit operator float(Half value)
        {
            uint bits = value._value;
            uint sign = (bits & SignMask) << 16;
            uint exponent = (bits >> 10) & 0x1F;
            uint fraction = bits & FractionMask;
            uint result;
            if (exponent == 0x1F)
                result = fraction == 0 ? sign | 0x7F800000 : sign | 0x7FC00000 | (fraction << 13);
            else if (exponent != 0)
                result = sign | ((exponent + 112) << 23) | (fraction << 13);
            else
            {
                // Zero or subnormal: fraction * 2^-24, which a float holds exactly.
                float magnitude = fraction * (1f / 16777216f);
                return sign != 0 ? -magnitude : magnitude;
            }
            return *(float*)&result;
        }

        public static explicit operator double(Half value)
        {
            if (IsNaN(value))
            {
                // Built from the bits: converting a NaN float to double may lose
                // the payload on some targets (WebAssembly).
                ulong sign = (ulong)(value._value & SignMask) << 48;
                ulong bits = sign | 0x7FF8000000000000ul | ((ulong)(value._value & FractionMask) << 42);
                return *(double*)&bits;
            }
            return (float)value;
        }

        // From float and double: rounded once, straight from the source bits.
        public static explicit operator Half(float value)
        {
            uint bits = *(uint*)&value;
            uint sign = (bits >> 16) & SignMask;
            int exponent = (int)((bits >> 23) & 0xFF);
            uint fraction = bits & 0x7FFFFF;
            if (exponent == 0xFF)
                return new Half((ushort)(fraction == 0 ? sign | ExponentMask : sign | 0x7E00 | (fraction >> 13)));
            return new Half((ushort)(sign | Round(exponent - 112, exponent == 0 ? fraction : fraction | 0x800000, 13)));
        }

        public static explicit operator Half(double value)
        {
            ulong bits = *(ulong*)&value;
            uint sign = (uint)(bits >> 48) & SignMask;
            int exponent = (int)((bits >> 52) & 0x7FF);
            ulong fraction = bits & 0xFFFFFFFFFFFFFul;
            if (exponent == 0x7FF)
                return new Half((ushort)(fraction == 0 ? sign | ExponentMask : sign | 0x7E00 | (uint)(fraction >> 42)));
            return new Half((ushort)(sign | Round(exponent - 1008, exponent == 0 ? fraction : fraction | 0x10000000000000ul, 42)));
        }

        // The magnitude bits of significand * 2^(exponent - 15 - 10 - extraBits)
        // rounded to nearest, ties to even. significand has its leading 1 at bit
        // 10 + extraBits; exponent is Half's biased exponent, below 1 for
        // subnormals. A carry out of the fraction moves into the exponent, so
        // rounding up from MaxValue gives infinity.
        private static uint Round(int exponent, ulong significand, int extraBits)
        {
            if (exponent >= 0x1F)
                return ExponentMask;
            int shift = extraBits;
            uint biased = 0;
            if (exponent > 0)
                biased = (uint)(exponent - 1) << 10;
            else
            {
                // Below 2^-25 everything rounds to zero.
                if (exponent < -10)
                    return 0;
                shift += 1 - exponent;
            }
            ulong result = significand >> shift;
            ulong remainder = significand & ((1ul << shift) - 1);
            ulong halfway = 1ul << (shift - 1);
            if (remainder > halfway || (remainder == halfway && (result & 1) != 0))
                result++;
            return biased + (uint)result;
        }

        public static implicit operator Half(byte value) => (Half)(float)value;
        public static implicit operator Half(sbyte value) => (Half)(float)value;
        public static explicit operator Half(short value) => (Half)(float)value;
        public static explicit operator Half(ushort value) => (Half)(float)value;
        public static explicit operator Half(int value) => (Half)(float)value;
        public static explicit operator Half(long value) => (Half)(double)value;

        public static explicit operator int(Half value) => (int)(float)value;
        public static explicit operator long(Half value) => (long)(float)value;
        public static explicit operator short(Half value) => (short)(float)value;
        public static explicit operator ushort(Half value) => (ushort)(float)value;
        public static explicit operator byte(Half value) => (byte)(float)value;
        public static explicit operator sbyte(Half value) => (sbyte)(float)value;

        public static Half operator +(Half value) => value;
        public static Half operator -(Half value) => new Half((ushort)(value._value ^ SignMask));
        public static Half operator ++(Half value) => (Half)((float)value + 1f);
        public static Half operator --(Half value) => (Half)((float)value - 1f);
        public static Half operator +(Half left, Half right) => (Half)((float)left + (float)right);
        public static Half operator -(Half left, Half right) => (Half)((float)left - (float)right);
        public static Half operator *(Half left, Half right) => (Half)((float)left * (float)right);
        public static Half operator /(Half left, Half right) => (Half)((float)left / (float)right);
        public static Half operator %(Half left, Half right) => (Half)((float)left % (float)right);

        // As for float: NaN compares false with everything, and 0 == -0.
        public static bool operator ==(Half left, Half right) => (float)left == (float)right;
        public static bool operator !=(Half left, Half right) => (float)left != (float)right;
        public static bool operator <(Half left, Half right) => (float)left < (float)right;
        public static bool operator >(Half left, Half right) => (float)left > (float)right;
        public static bool operator <=(Half left, Half right) => (float)left <= (float)right;
        public static bool operator >=(Half left, Half right) => (float)left >= (float)right;

        // Unlike ==, NaN equals NaN here, as in .NET.
        public bool Equals(Half other) =>
            _value == other._value || (IsZero(this) && IsZero(other)) || (IsNaN(this) && IsNaN(other));

        public override bool Equals(object obj) => obj is Half other && Equals(other);

        public override int GetHashCode() =>
            IsNaN(this) || IsZero(this) ? _value & ExponentMask : _value;

        // NaN sorts first, as in .NET.
        public int CompareTo(Half value)
        {
            float left = (float)this, right = (float)value;
            if (left < right)
                return -1;
            if (left > right)
                return 1;
            if (left == right)
                return 0;
            return IsNaN(this) ? (IsNaN(value) ? 0 : -1) : 1;
        }

        // The shortest text that converts back to this value, as .NET writes it:
        // 0.1, 65500 (MaxValue, 65504), 6E-08 (Epsilon). Formats as for double.
        public override string ToString() => ToString((string)null);
        public string ToString(string format) => Number.IeeeToString(_value, 10, 5, 5, format);
        public string ToString(IFormatProvider provider) => ToString();
        public string ToString(string format, IFormatProvider provider) => ToString(format);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatIeee(_value, 10, 5, 5, destination, out charsWritten, format);
    }

    public static unsafe class BitConverter
    {
        // Every target zero mode builds for (x64, arm64, WebAssembly, i386, the
        // 6502) stores the low byte first; BinaryPrimitives relies on it.
        public const bool IsLittleEndian = true;

        public static ushort HalfToUInt16Bits(Half value) => value._value;
        public static Half UInt16BitsToHalf(ushort value) => new Half(value);
        public static short HalfToInt16Bits(Half value) => (short)value._value;
        public static Half Int16BitsToHalf(short value) => new Half((ushort)value);

        public static uint SingleToUInt32Bits(float value) => *(uint*)&value;
        public static float UInt32BitsToSingle(uint value) => *(float*)&value;
        public static int SingleToInt32Bits(float value) => *(int*)&value;
        public static float Int32BitsToSingle(int value) => *(float*)&value;

        public static ulong DoubleToUInt64Bits(double value) => *(ulong*)&value;
        public static double UInt64BitsToDouble(ulong value) => *(double*)&value;
        public static long DoubleToInt64Bits(double value) => *(long*)&value;
        public static double Int64BitsToDouble(long value) => *(double*)&value;
    }
}
