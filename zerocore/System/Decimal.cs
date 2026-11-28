// decimal for zero mode: .NET's layout and results (a 96-bit integer, a sign
// and a power of ten from 0 to 28 that divides it), without .NET's code.
// Arithmetic works on 32-bit limbs in stack buffers, so nothing allocates and
// 32-bit targets need no 128-bit support. Results round half to even, as
// .NET's do; scales, signs of zero and trailing zeros follow .NET's rules,
// checked against .NET by fuzzing every operation (scripts/decimal-fuzz).
namespace System
{
    public readonly unsafe struct Decimal : IEquatable<Decimal>, IComparable<Decimal>, IComparable, ISpanFormattable
    {
        private const int SignMask = unchecked((int)0x80000000);
        private const int ScaleShift = 16;
        private const int MaxScale = 28;

        // .NET's field order: the flags (sign and scale), then the top 32 bits
        // of the integer, then the low 64.
        private readonly int _flags;
        private readonly uint _hi32;
        private readonly ulong _lo64;

        public static readonly Decimal Zero = default;
        public static readonly Decimal One = new Decimal(1);
        public static readonly Decimal MinusOne = new Decimal(-1);
        public static readonly Decimal MaxValue = new Decimal(-1, -1, -1, false, 0);
        public static readonly Decimal MinValue = new Decimal(-1, -1, -1, true, 0);

        public Decimal(int lo, int mid, int hi, bool isNegative, byte scale)
        {
            if (scale > MaxScale)
                throw new ArgumentOutOfRangeException(nameof(scale));
            _lo64 = (uint)lo | ((ulong)(uint)mid << 32);
            _hi32 = (uint)hi;
            _flags = (scale << ScaleShift) | (isNegative ? SignMask : 0);
        }

        public Decimal(int value) : this((long)value) { }

        public Decimal(uint value) : this((ulong)value) { }

        public Decimal(long value)
        {
            _hi32 = 0;
            if (value >= 0)
            {
                _lo64 = (ulong)value;
                _flags = 0;
            }
            else
            {
                _lo64 = (ulong)(-(value + 1)) + 1;
                _flags = SignMask;
            }
        }

        public Decimal(ulong value)
        {
            _lo64 = value;
            _hi32 = 0;
            _flags = 0;
        }

        public Decimal(double value) => this = FromDouble(value);

        public Decimal(float value) => this = FromSingle(value);

        private Decimal(ulong lo64, uint hi32, int scale, bool negative)
        {
            _lo64 = lo64;
            _hi32 = hi32;
            _flags = (scale << ScaleShift) | (negative ? SignMask : 0);
        }

        public byte Scale => (byte)(_flags >> ScaleShift);

        private bool IsNegative => _flags < 0;

        private bool IsZero => _lo64 == 0 && _hi32 == 0;

        // ---- limbs ---------------------------------------------------------

        private void Load(Span<uint> limbs)
        {
            limbs.Clear();
            limbs[0] = (uint)_lo64;
            limbs[1] = (uint)(_lo64 >> 32);
            limbs[2] = _hi32;
        }

        private static bool Fits96(ReadOnlySpan<uint> x)
        {
            for (int i = 3; i < x.Length; i++)
                if (x[i] != 0)
                    return false;
            return true;
        }

        private static bool IsZeroLimbs(ReadOnlySpan<uint> x)
        {
            for (int i = 0; i < x.Length; i++)
                if (x[i] != 0)
                    return false;
            return true;
        }

        // x = x * m + add; false when the result doesn't fit x.
        private static bool MulAdd(Span<uint> x, uint m, uint add)
        {
            ulong carry = add;
            for (int i = 0; i < x.Length; i++)
            {
                ulong t = (ulong)x[i] * m + carry;
                x[i] = (uint)t;
                carry = t >> 32;
            }
            return carry == 0;
        }

        // x = x / d, returning the remainder.
        private static uint DivSmall(Span<uint> x, uint d)
        {
            ulong rem = 0;
            for (int i = x.Length - 1; i >= 0; i--)
            {
                ulong t = (rem << 32) | x[i];
                x[i] = (uint)(t / d);
                rem = t % d;
            }
            return (uint)rem;
        }

        private static int Compare(ReadOnlySpan<uint> x, ReadOnlySpan<uint> y)
        {
            for (int i = x.Length - 1; i >= 0; i--)
            {
                if (x[i] != y[i])
                    return x[i] < y[i] ? -1 : 1;
            }
            return 0;
        }

        private static void Add(Span<uint> x, ReadOnlySpan<uint> y)
        {
            ulong carry = 0;
            for (int i = 0; i < x.Length; i++)
            {
                ulong t = (ulong)x[i] + y[i] + carry;
                x[i] = (uint)t;
                carry = t >> 32;
            }
        }

        // x = x - y, for x >= y.
        private static void Sub(Span<uint> x, ReadOnlySpan<uint> y)
        {
            long borrow = 0;
            for (int i = 0; i < x.Length; i++)
            {
                long t = (long)x[i] - y[i] - borrow;
                x[i] = (uint)t;
                borrow = t < 0 ? 1 : 0;
            }
        }

        private static void ShiftLeft1(Span<uint> x, uint bit)
        {
            for (int i = 0; i < x.Length; i++)
            {
                uint top = x[i] >> 31;
                x[i] = (x[i] << 1) | bit;
                bit = top;
            }
        }

        // Divides x by 10 `count` times, rounding the result half to even over
        // everything dropped; lowers scale by count.
        private static void DropDigits(Span<uint> x, int count)
        {
            bool sticky = false;
            uint last = 0;
            for (int i = 0; i < count; i++)
            {
                if (last != 0)
                    sticky = true;
                last = DivSmall(x, 10);
            }
            if (last > 5 || (last == 5 && (sticky || (x[0] & 1) != 0)))
                MulAdd(x, 1, 1);
        }

        // The result of an operation: rounded, once, to a scale of at most 28
        // and an integer that fits 96 bits; OverflowException when even a
        // scale of 0 doesn't fit.
        private static Decimal Result(Span<uint> x, int scale, bool negative)
        {
            if (!TryResult(x, scale, negative, out Decimal result))
                throw new OverflowException("Value was either too large or too small for a Decimal.");
            return result;
        }

        // False when it doesn't fit (nothing can be caught in zero mode, so
        // parsing asks rather than catches).
        private static bool TryResult(Span<uint> x, int scale, bool negative, out Decimal result)
        {
            result = Zero;
            if (scale > MaxScale + 80)
            {
                // Every digit goes (an integer here has at most 77 digits).
                result = new Decimal(0, 0, MaxScale, negative);
                return true;
            }
            int drop = scale > MaxScale ? scale - MaxScale : 0;
            if (drop > 0 || !Fits96(x))
            {
                Span<uint> t = stackalloc uint[x.Length];
                x.CopyTo(t);
                for (int i = 0; i < drop; i++)
                    DivSmall(t, 10);
                while (!Fits96(t))
                {
                    DivSmall(t, 10);
                    drop++;
                }
                if (drop > scale)
                    return false;
                DropDigits(x, drop);
                scale -= drop;
                // Rounding up can carry past 96 bits: 2^96 ends in 6, so one
                // more digit rounds the same as from the exact value.
                if (!Fits96(x))
                {
                    if (scale == 0)
                        return false;
                    DropDigits(x, 1);
                    scale--;
                }
            }
            result = new Decimal(x[0] | ((ulong)x[1] << 32), x[2], scale, negative);
            return true;
        }

        // ---- arithmetic ----------------------------------------------------

        public static Decimal Add(Decimal a, Decimal b) => AddSub(a, b, false);

        public static Decimal Subtract(Decimal a, Decimal b) => AddSub(a, b, true);

        private static Decimal AddSub(Decimal a, Decimal b, bool subtract)
        {
            bool aNeg = a.IsNegative, bNeg = b.IsNegative ^ subtract;
            int scale = Math.Max(a.Scale, b.Scale);
            Span<uint> x = stackalloc uint[7];
            Span<uint> y = stackalloc uint[7];
            a.Load(x);
            b.Load(y);
            for (int i = a.Scale; i < scale; i++)
                MulAdd(x, 10, 0);
            for (int i = b.Scale; i < scale; i++)
                MulAdd(y, 10, 0);
            bool negative;
            if (aNeg == bNeg)
            {
                Add(x, y);
                negative = aNeg;
            }
            else
            {
                int c = Compare(x, y);
                if (c > 0)
                {
                    Sub(x, y);
                    negative = aNeg;
                }
                else if (c < 0)
                {
                    Sub(y, x);
                    y.CopyTo(x);
                    negative = bNeg;
                }
                else
                {
                    // Equal and opposite: .NET's zero takes the sign of the
                    // operand with the larger scale (the first if they're even).
                    x.Clear();
                    negative = b.Scale > a.Scale ? bNeg : aNeg;
                }
            }
            return Result(x, scale, negative);
        }

        public static Decimal Multiply(Decimal a, Decimal b)
        {
            Span<uint> x = stackalloc uint[3];
            Span<uint> y = stackalloc uint[3];
            a.Load(x);
            b.Load(y);
            Span<uint> p = stackalloc uint[7];
            p.Clear();
            for (int i = 0; i < 3; i++)
            {
                ulong carry = 0;
                for (int j = 0; j < 3; j++)
                {
                    ulong t = (ulong)x[i] * y[j] + p[i + j] + carry;
                    p[i + j] = (uint)t;
                    carry = t >> 32;
                }
                p[i + 3] = (uint)carry;
            }
            // .NET's two cases for a zero: with both operands under 2^32, a
            // scale above 47 can only round to 0, which is plain 0; with a
            // larger operand, an exact zero product is plain 0. Other zeros
            // keep their sign and scale.
            int scale = a.Scale + b.Scale;
            bool small = a._hi32 == 0 && (a._lo64 >> 32) == 0 && b._hi32 == 0 && (b._lo64 >> 32) == 0;
            if (small ? scale > MaxScale + 19 : IsZeroLimbs(p))
                return Zero;
            return Result(p, scale, a.IsNegative != b.IsNegative);
        }

        // q = x / y and r = x % y, bit by bit (y is at most 96 bits).
        private static void DivRem(ReadOnlySpan<uint> x, ReadOnlySpan<uint> y, Span<uint> q, Span<uint> r)
        {
            q.Clear();
            r.Clear();
            for (int bit = x.Length * 32 - 1; bit >= 0; bit--)
            {
                ShiftLeft1(r, (x[bit >> 5] >> (bit & 31)) & 1);
                ShiftLeft1(q, 0);
                if (Compare(r, y) >= 0)
                {
                    Sub(r, y);
                    q[0] |= 1;
                }
            }
        }

        public static Decimal Divide(Decimal a, Decimal b)
        {
            if (b.IsZero)
                throw new DivideByZeroException("Attempted to divide by zero.");
            Span<uint> x = stackalloc uint[4];
            Span<uint> y = stackalloc uint[4];
            a.Load(x);
            b.Load(y);
            Span<uint> q = stackalloc uint[4];
            Span<uint> r = stackalloc uint[4];
            DivRem(x, y, q, r);
            int scale = a.Scale - b.Scale;
            // A quotient that isn't exact at the natural scale loses its
            // trailing zeros at the end, as .NET's does (10/4 is 2.5, while
            // 1.00/1 stays 1.00).
            bool unscale = !IsZeroLimbs(r);
            Span<uint> nq = stackalloc uint[4];
            Span<uint> t = stackalloc uint[4];
            Span<uint> digit = stackalloc uint[4];
            Span<uint> rem = stackalloc uint[4];
            // One more digit of the quotient: (q, r) for x * 10 / y.
            while (scale < 0 || (!IsZeroLimbs(r) && scale < MaxScale))
            {
                q.CopyTo(nq);
                if (!MulAdd(nq, 10, 0))
                    break;
                r.CopyTo(t);
                MulAdd(t, 10, 0);
                DivRem(t, y, digit, rem);
                Add(nq, digit);
                if (!Fits96(nq))
                    break;
                nq.CopyTo(q);
                rem.CopyTo(r);
                scale++;
            }
            if (scale < 0)
                throw new OverflowException("Value was either too large or too small for a Decimal.");
            if (!IsZeroLimbs(r))
            {
                // Round half to even on the remainder.
                r.CopyTo(t);
                ShiftLeft1(t, 0);
                int c = Compare(t, y);
                if (c > 0 || (c == 0 && (q[0] & 1) != 0))
                    MulAdd(q, 1, 1);
            }
            Decimal result = Result(q, scale, a.IsNegative != b.IsNegative);
            return unscale ? WithoutTrailingZeros(result) : result;
        }

        private static Decimal WithoutTrailingZeros(Decimal d)
        {
            Span<uint> x = stackalloc uint[3];
            Span<uint> t = stackalloc uint[3];
            d.Load(x);
            int scale = d.Scale;
            while (scale > 0)
            {
                x.CopyTo(t);
                if (DivSmall(t, 10) != 0)
                    break;
                t.CopyTo(x);
                scale--;
            }
            return new Decimal(x[0] | ((ulong)x[1] << 32), x[2], scale, d.IsNegative);
        }

        // The sign is the dividend's. As .NET's: 0 % x and |a| < |b| give a
        // unchanged, |a| = |b| gives zero at the larger scale.
        public static Decimal Remainder(Decimal a, Decimal b)
        {
            if (b.IsZero)
                throw new DivideByZeroException("Attempted to divide by zero.");
            if (a.IsZero)
                return a;
            int c = Compare(Abs(a), Abs(b));
            if (c < 0)
                return a;
            int scale = Math.Max(a.Scale, b.Scale);
            if (c == 0)
                return new Decimal(0, 0, scale, a.IsNegative);
            Span<uint> x = stackalloc uint[7];
            Span<uint> y = stackalloc uint[7];
            a.Load(x);
            b.Load(y);
            for (int i = a.Scale; i < scale; i++)
                MulAdd(x, 10, 0);
            for (int i = b.Scale; i < scale; i++)
                MulAdd(y, 10, 0);
            Span<uint> q = stackalloc uint[7];
            Span<uint> r = stackalloc uint[7];
            DivRem(x, y, q, r);
            return Result(r, scale, a.IsNegative);
        }

        public static Decimal Negate(Decimal d) => new Decimal(d._lo64, d._hi32, d.Scale, !d.IsNegative);

        public static Decimal Abs(Decimal d) => new Decimal(d._lo64, d._hi32, d.Scale, false);

        public static int Compare(Decimal a, Decimal b)
        {
            bool aZero = a.IsZero, bZero = b.IsZero;
            if (aZero && bZero)
                return 0;
            bool aNeg = a.IsNegative && !aZero, bNeg = b.IsNegative && !bZero;
            if (aNeg != bNeg)
                return aNeg ? -1 : 1;
            int scale = Math.Max(a.Scale, b.Scale);
            Span<uint> x = stackalloc uint[7];
            Span<uint> y = stackalloc uint[7];
            a.Load(x);
            b.Load(y);
            for (int i = a.Scale; i < scale; i++)
                MulAdd(x, 10, 0);
            for (int i = b.Scale; i < scale; i++)
                MulAdd(y, 10, 0);
            int c = Compare(x, y);
            return aNeg ? -c : c;
        }

        // ---- rounding ------------------------------------------------------

        // Drops the fractional digits, rounding as `mode` says: 0 toward zero,
        // 1 half to even, 2 half away from zero, 3 toward negative infinity,
        // 4 toward positive infinity.
        private static Decimal RoundTo(Decimal d, int decimals, int mode)
        {
            int scale = d.Scale;
            if (scale <= decimals)
                return d;
            Span<uint> x = stackalloc uint[3];
            d.Load(x);
            bool sticky = false;
            uint last = 0;
            for (int i = decimals; i < scale; i++)
            {
                if (last != 0)
                    sticky = true;
                last = DivSmall(x, 10);
            }
            bool inexact = last != 0 || sticky;
            bool up = mode switch
            {
                1 => last > 5 || (last == 5 && (sticky || (x[0] & 1) != 0)),
                2 => last >= 5,
                3 => inexact && d.IsNegative,
                4 => inexact && !d.IsNegative,
                _ => false,
            };
            if (up)
                MulAdd(x, 1, 1);
            return new Decimal(x[0] | ((ulong)x[1] << 32), x[2], decimals, d.IsNegative);
        }

        public static Decimal Truncate(Decimal d) => RoundTo(d, 0, 0);

        public static Decimal Floor(Decimal d) => RoundTo(d, 0, 3);

        public static Decimal Ceiling(Decimal d) => RoundTo(d, 0, 4);

        public static Decimal Round(Decimal d) => RoundTo(d, 0, 1);

        public static Decimal Round(Decimal d, int decimals)
        {
            if ((uint)decimals > MaxScale)
                throw new ArgumentOutOfRangeException(nameof(decimals));
            return RoundTo(d, decimals, 1);
        }

        // ---- conversions ---------------------------------------------------

        public static implicit operator Decimal(int value) => new Decimal(value);
        public static implicit operator Decimal(uint value) => new Decimal(value);
        public static implicit operator Decimal(long value) => new Decimal(value);
        public static implicit operator Decimal(ulong value) => new Decimal(value);
        public static implicit operator Decimal(short value) => new Decimal(value);
        public static implicit operator Decimal(ushort value) => new Decimal(value);
        public static implicit operator Decimal(sbyte value) => new Decimal(value);
        public static implicit operator Decimal(byte value) => new Decimal(value);
        public static implicit operator Decimal(char value) => new Decimal(value);
        public static explicit operator Decimal(double value) => new Decimal(value);
        public static explicit operator Decimal(float value) => new Decimal(value);

        // The integer part, or OverflowException if it doesn't fit.
        private static bool IntegerPart(Decimal d, out ulong magnitude)
        {
            Decimal t = Truncate(d);
            magnitude = t._lo64;
            return t._hi32 == 0;
        }

        public static explicit operator long(Decimal value)
        {
            if (!IntegerPart(value, out ulong m) || (value.IsNegative ? m > 0x8000000000000000UL : m > long.MaxValue))
                throw new OverflowException("Value was either too large or too small for an Int64.");
            return value.IsNegative ? (long)(0 - m) : (long)m;
        }

        public static explicit operator ulong(Decimal value)
        {
            if (!IntegerPart(value, out ulong m) || (value.IsNegative && m != 0))
                throw new OverflowException("Value was either too large or too small for a UInt64.");
            return m;
        }

        public static explicit operator int(Decimal value)
        {
            long v = ToInt64Checked(value, "Int32");
            if (v < int.MinValue || v > int.MaxValue)
                throw new OverflowException("Value was either too large or too small for an Int32.");
            return (int)v;
        }

        public static explicit operator uint(Decimal value)
        {
            long v = ToInt64Checked(value, "UInt32");
            if (v < 0 || v > uint.MaxValue)
                throw new OverflowException("Value was either too large or too small for a UInt32.");
            return (uint)v;
        }

        public static explicit operator short(Decimal value)
        {
            long v = ToInt64Checked(value, "Int16");
            if (v < short.MinValue || v > short.MaxValue)
                throw new OverflowException("Value was either too large or too small for an Int16.");
            return (short)v;
        }

        public static explicit operator ushort(Decimal value)
        {
            long v = ToInt64Checked(value, "UInt16");
            if (v < 0 || v > ushort.MaxValue)
                throw new OverflowException("Value was either too large or too small for a UInt16.");
            return (ushort)v;
        }

        public static explicit operator sbyte(Decimal value)
        {
            long v = ToInt64Checked(value, "SByte");
            if (v < sbyte.MinValue || v > sbyte.MaxValue)
                throw new OverflowException("Value was either too large or too small for a signed byte.");
            return (sbyte)v;
        }

        public static explicit operator byte(Decimal value)
        {
            long v = ToInt64Checked(value, "Byte");
            if (v < 0 || v > byte.MaxValue)
                throw new OverflowException("Value was either too large or too small for an unsigned byte.");
            return (byte)v;
        }

        public static explicit operator char(Decimal value)
        {
            long v = ToInt64Checked(value, "Char");
            if (v < 0 || v > char.MaxValue)
                throw new OverflowException("Value was either too large or too small for a character.");
            return (char)v;
        }

        private static long ToInt64Checked(Decimal value, string type)
        {
            if (!IntegerPart(value, out ulong m) || m > 0x8000000000000000UL)
                throw new OverflowException("Value was either too large or too small for an " + type + ".");
            return value.IsNegative ? (long)(0 - m) : (long)m;
        }

        public static explicit operator double(Decimal value)
        {
            double d = (double)value._lo64 + (double)value._hi32 * 18446744073709551616.0;
            d /= PowerOf10(value.Scale);
            return value.IsNegative ? -d : d;
        }

        public static explicit operator float(Decimal value) => (float)(double)value;

        // Exact for every power .NET's conversion uses (the nearest double, as
        // the compiler rounds each literal).
        private static double PowerOf10(int n) => n switch
        {
            0 => 1e0, 1 => 1e1, 2 => 1e2, 3 => 1e3, 4 => 1e4, 5 => 1e5, 6 => 1e6, 7 => 1e7,
            8 => 1e8, 9 => 1e9, 10 => 1e10, 11 => 1e11, 12 => 1e12, 13 => 1e13, 14 => 1e14,
            15 => 1e15, 16 => 1e16, 17 => 1e17, 18 => 1e18, 19 => 1e19, 20 => 1e20, 21 => 1e21,
            22 => 1e22, 23 => 1e23, 24 => 1e24, 25 => 1e25, 26 => 1e26, 27 => 1e27, 28 => 1e28,
            _ => Math.Pow(10, n),
        };

        // As .NET's: a double keeps 15 significant digits (its precision), a
        // float 7, at a scale of at most 28, without trailing zeros. `exp` is
        // the binary exponent, for a value in [2^(exp-1), 2^exp).
        private static Decimal FromDouble(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            return FromBinary(bits < 0 ? -value : value, (int)((bits >> 52) & 0x7FF) - 1022, bits < 0, 15);
        }

        private static Decimal FromSingle(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            return FromBinary(bits < 0 ? -(double)value : value, ((bits >> 23) & 0xFF) - 126, bits < 0, 7);
        }

        private static Decimal FromBinary(double dbl, int exp, bool negative, int digits)
        {
            // Below 2^-95 even 28 decimal places round to 0; above 2^96 it
            // doesn't fit (infinities and NaN included).
            if (exp < -94)
                return Zero;
            if (exp > 96)
                throw new OverflowException("Value was either too large or too small for a Decimal.");
            double top = PowerOf10(digits), bottom = PowerOf10(digits - 1);
            // The power of ten that brings it to `digits` digits, from
            // exp * log10(2) in 16-bit fixed point.
            int power = digits - 1 - ((exp * 19728) >> 16);
            if (power >= 0)
            {
                if (power > MaxScale)
                    power = MaxScale;
                dbl *= PowerOf10(power);
            }
            else if (power != -1 || dbl >= top)
                dbl /= PowerOf10(-power);
            else
                power = 0;
            if (dbl < bottom && power < MaxScale)
            {
                dbl *= 10;
                power++;
            }
            ulong mant = (ulong)(long)Math.Round(dbl);
            if (mant == 0)
                return Zero;
            Span<uint> x = stackalloc uint[4];
            x.Clear();
            int scale;
            if (power < 0)
            {
                // More digits than kept: the rest are zeros.
                x[0] = (uint)mant;
                x[1] = (uint)(mant >> 32);
                for (int i = power; i < 0; i++)
                    MulAdd(x, 10, 0);
                scale = 0;
            }
            else
            {
                for (int lmax = Math.Min(power, digits - 1); lmax > 0 && mant % 10 == 0; lmax--)
                {
                    mant /= 10;
                    power--;
                }
                x[0] = (uint)mant;
                x[1] = (uint)(mant >> 32);
                scale = power;
            }
            return Result(x, scale, negative);
        }

        // ---- text ----------------------------------------------------------

        // All the digits, trailing zeros too: 1.50m is "1.50". Formats round
        // half away from zero: 2.675m.ToString("F2") is "2.68".
        public override string ToString() => ToString((string)null);
        public string ToString(string format) => ToString(format, null);
        public string ToString(string format, IFormatProvider provider) => Number.DecimalToString(_lo64, _hi32, Scale, IsNegative, format, provider);
        public string ToString(IFormatProvider provider) => ToString(null, provider);
        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null) =>
            Number.TryFormatDecimal(_lo64, _hi32, Scale, IsNegative, destination, out charsWritten, format, provider);

        // Digits with an optional sign, decimal point and exponent ("e5",
        // "E-3"), as .NET's Parse takes with NumberStyles.Float; digits beyond
        // what fits are rounded.
        public static Decimal Parse(string s)
        {
            int status = ParseCore(s, out Decimal result);
            if (status == 1)
                throw new FormatException("The input string was not in a correct format.");
            if (status == 2)
                throw new OverflowException("Value was either too large or too small for a Decimal.");
            return result;
        }

        public static bool TryParse(string s, out Decimal result) => ParseCore(s, out result) == 0;

        // 0: parsed; 1: not a number; 2: too large.
        private static int ParseCore(string s, out Decimal result)
        {
            result = Zero;
            if (s == null)
                return 1;
            int i = 0, n = s.Length;
            while (i < n && s[i] == ' ') i++;
            while (n > i && s[n - 1] == ' ') n--;
            bool negative = false;
            if (i < n && (s[i] == '-' || s[i] == '+'))
            {
                negative = s[i] == '-';
                i++;
            }
            // Up to 60 significant digits are kept (more than can matter);
            // a nonzero digit past them only breaks a tie when rounding.
            Span<uint> x = stackalloc uint[8];
            x.Clear();
            int scale = 0, digits = 0;
            bool any = false, point = false, sticky = false;
            for (; i < n; i++)
            {
                char c = s[i];
                if (c == '.' && !point)
                {
                    point = true;
                    continue;
                }
                if (c < '0' || c > '9')
                    break;
                any = true;
                uint digit = (uint)(c - '0');
                if (digits == 0 && digit == 0)
                {
                    if (point)
                        scale++;
                    continue;
                }
                if (digits < 60)
                {
                    MulAdd(x, 10, digit);
                    digits++;
                    if (point)
                        scale++;
                }
                else
                {
                    if (digit != 0)
                        sticky = true;
                    if (!point)
                        scale--;
                }
            }
            if (!any)
                return 1;
            if (i < n && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                bool expNeg = false;
                if (i < n && (s[i] == '-' || s[i] == '+'))
                {
                    expNeg = s[i] == '-';
                    i++;
                }
                if (i == n || s[i] < '0' || s[i] > '9')
                    return 1;
                int exp = 0;
                for (; i < n && s[i] >= '0' && s[i] <= '9'; i++)
                {
                    if (exp < 100000)
                        exp = exp * 10 + (s[i] - '0');
                }
                scale += expNeg ? exp : -exp;
            }
            if (i != n)
                return 1;
            if (sticky)
            {
                // A trailing 1 stands for the nonzero digits dropped.
                MulAdd(x, 10, 1);
                scale++;
            }
            if (IsZeroLimbs(x))
            {
                result = new Decimal(0, 0, Math.Max(0, Math.Min(scale, MaxScale)), negative);
                return 0;
            }
            for (; scale < 0; scale++)
            {
                if (!MulAdd(x, 10, 0) || !Fits96(x))
                    return 2;
            }
            return TryResult(x, scale, negative, out result) ? 0 : 2;
        }

        // ---- equality ------------------------------------------------------

        public bool Equals(Decimal other) => Compare(this, other) == 0;

        public override bool Equals(object obj) => obj is Decimal other && Compare(this, other) == 0;

        // Equal values hash alike: 1.0 and 1.00 drop their trailing zeros first.
        public override int GetHashCode()
        {
            if (IsZero)
                return 0;
            Span<uint> x = stackalloc uint[3];
            Load(x);
            int scale = Scale;
            Span<uint> t = stackalloc uint[3];
            while (scale > 0)
            {
                x.CopyTo(t);
                if (DivSmall(t, 10) != 0)
                    break;
                t.CopyTo(x);
                scale--;
            }
            int h = (int)x[0] ^ (int)(x[1] * 31) ^ (int)(x[2] * 961) ^ (scale << 24);
            return IsNegative ? ~h : h;
        }

        public int CompareTo(Decimal other) => Compare(this, other);

        public int CompareTo(object obj)
            => obj is Decimal other ? Compare(this, other) : obj == null ? 1 : throw new ArgumentException("Object must be of type Decimal.");

        // ---- operators -----------------------------------------------------

        public static Decimal operator +(Decimal d) => d;
        public static Decimal operator -(Decimal d) => Negate(d);
        public static Decimal operator ++(Decimal d) => Add(d, One);
        public static Decimal operator --(Decimal d) => Subtract(d, One);
        public static Decimal operator +(Decimal a, Decimal b) => Add(a, b);
        public static Decimal operator -(Decimal a, Decimal b) => Subtract(a, b);
        public static Decimal operator *(Decimal a, Decimal b) => Multiply(a, b);
        public static Decimal operator /(Decimal a, Decimal b) => Divide(a, b);
        public static Decimal operator %(Decimal a, Decimal b) => Remainder(a, b);
        public static bool operator ==(Decimal a, Decimal b) => Compare(a, b) == 0;
        public static bool operator !=(Decimal a, Decimal b) => Compare(a, b) != 0;
        public static bool operator <(Decimal a, Decimal b) => Compare(a, b) < 0;
        public static bool operator <=(Decimal a, Decimal b) => Compare(a, b) <= 0;
        public static bool operator >(Decimal a, Decimal b) => Compare(a, b) > 0;
        public static bool operator >=(Decimal a, Decimal b) => Compare(a, b) >= 0;

        public static Decimal Min(Decimal a, Decimal b) => Compare(a, b) <= 0 ? a : b;
        public static Decimal Max(Decimal a, Decimal b) => Compare(a, b) >= 0 ? a : b;
        public static int Sign(Decimal d) => d.IsZero ? 0 : d.IsNegative ? -1 : 1;

        // The four ints .NET's GetBits gives: low, middle and high 32 bits,
        // then the sign and scale.
        public static int GetBits(decimal d, Span<int> destination)
        {
            destination[0] = (int)(uint)d._lo64;
            destination[1] = (int)(uint)(d._lo64 >> 32);
            destination[2] = (int)d._hi32;
            destination[3] = d._flags;
            return 4;
        }
    }
}
