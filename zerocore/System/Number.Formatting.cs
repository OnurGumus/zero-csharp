// Numbers as text with .NET's standard format strings, in the invariant
// culture: "D8", "X4", "B8", "F2", "N0", "E3", "G5", "P1", "C", "R" and the
// default. The text is .NET's, character for character (scripts/format-check
// compares the two over millions of values). A NumberFormatInfo of your own
// (in Sink.Info) hands the text over to Number.Culture.cs.
//
// What .NET does and this does too:
// - double, float and Half print their exact binary value, rounded to the
//   digits the format asks for, ties to even: 0.125.ToString("F2") is "0.12",
//   0.1.ToString("F20") is "0.10000000000000000555". Without a precision
//   ("G", "R" or none) they print the shortest text that reads back.
// - integers and decimal round ties away from zero: 2.675m.ToString("F2") is
//   "2.68", 25.ToString("G1") is "3E+01".
// - binary floating point keeps the sign of zero ("-0.00"); integers and
//   decimal never print "-0".
//
// Custom formats such as "0.00" or "#,##0" aren't supported: they throw
// FormatException (which ends the program, see Exceptions.cs).
//
// This file uses nothing but Span and FormatException, so scripts/
// format-check compiles it against real .NET as well.

using System.Globalization;

namespace System
{
    internal static unsafe partial class Number
    {
        // ---- where the text goes -------------------------------------------

        // Characters past the capacity are counted, not written, so a caller
        // learns the length it needs and can run the format again.
        internal struct Sink
        {
            private readonly char* _chars;
            private readonly int _capacity;
            public int Length;
            // Symbols of your own, or null for the invariant culture's.
            public NumberFormatInfo Info;

            public Sink(char* chars, int capacity)
            {
                _chars = chars;
                _capacity = capacity;
                Length = 0;
                Info = null;
            }

            public bool Fits => Length <= _capacity;

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public void Add(char c)
            {
                if (Length < _capacity)
                    _chars[Length] = c;
                Length++;
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public void Add(char c, int count)
            {
                while (count > 0 && Length < _capacity)
                {
                    _chars[Length++] = c;
                    count--;
                }
                if (count > 0)
                    Length += count;
            }

            public void Add(string text)
            {
                for (int i = 0; i < text.Length; i++)
                    Add(text[i]);
            }
        }

        // ---- format strings ------------------------------------------------

        // A standard format is a letter and an optional precision of up to
        // 999,999,999: "X", "F2", "D08". Returns the letter, or '\0' for an
        // empty format; precision is -1 when none is given.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static char ParseFormat(ReadOnlySpan<char> format, out int precision)
        {
            precision = -1;
            if (format.Length == 0)
                return '\0';
            char letter = format[0];
            if ((uint)((letter | 0x20) - 'a') <= 'z' - 'a')
            {
                int value = 0;
                int i = 1;
                for (; i < format.Length; i++)
                {
                    int digit = format[i] - '0';
                    if ((uint)digit > 9)
                        break;
                    if (value > 99_999_999)
                        throw new FormatException("Format specifier precision is too large; the maximum is 999,999,999.");
                    value = value * 10 + digit;
                }
                if (i == format.Length)
                {
                    if (format.Length > 1)
                        precision = value;
                    return letter;
                }
            }
            throw new FormatException("Custom numeric formats such as \"0.00\" aren't supported in zero mode; use a standard one such as \"F2\".");
        }

        private static FormatException BadFormat() => new FormatException("Format specifier was invalid.");

        private static char Upper(char letter) => letter >= 'a' && letter <= 'z' ? (char)(letter - 32) : letter;

        // ---- a number as decimal digits ------------------------------------

        // 0.d[0]d[1]...d[Count-1] × 10^Scale, each digit 0-9. Zero has no
        // digits. Binary floating point keeps the sign of zero.
        internal struct Digits
        {
            public byte* D;
            public int Count;
            public int Scale;
            public bool Negative;
            public bool Binary;
        }

        // Keeps `keep` digits (which may be 0 or fewer), rounding half away
        // from zero, and drops trailing zeros: .NET's RoundNumber. Digits that
        // are already rounded (binary floating point) just lose their zeros.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Round(ref Digits n, int keep)
        {
            int i = 0;
            while (i < keep && i < n.Count)
                i++;
            if (i == keep && i < n.Count && n.D[i] >= 5)
            {
                while (i > 0 && n.D[i - 1] == 9)
                    i--;
                if (i > 0)
                {
                    n.D[i - 1]++;
                }
                else
                {
                    n.Scale++;
                    n.D[0] = 1;
                    i = 1;
                }
            }
            else
            {
                while (i > 0 && n.D[i - 1] == 0)
                    i--;
            }
            if (i == 0)
            {
                n.Scale = 0;
                if (!n.Binary)
                    n.Negative = false;
            }
            n.Count = i;
        }

        // The digits the format keeps: `cutoff` significant digits, or
        // `cutoff` digits after the decimal point. -1: all of them.
        private static int Cutoff(char upper, int precision, out bool significant)
        {
            significant = false;
            switch (upper)
            {
                case 'F':
                case 'N':
                case 'C':
                    return precision < 0 ? 2 : precision;
                case 'P':
                    // Two more: the value is multiplied by 100.
                    return (precision < 0 ? 2 : precision) + 2;
                case 'E':
                    significant = true;
                    return (precision < 0 ? 6 : precision) + 1;
                case 'G':
                    significant = true;
                    return precision <= 0 ? -1 : precision;
                default:
                    return -1;
            }
        }

        // ---- digits as text ------------------------------------------------

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static char DigitAt(ref Digits n, int i) => i < n.Count ? (char)('0' + n.D[i]) : '0';

        // The format's text for digits rounded as Cutoff says. maxDigits: the
        // decimal exponent above which "G" switches to scientific notation.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Emit(ref Digits n, char format, int precision, int maxDigits, bool noScientific, ref Sink s)
        {
            if (s.Info == null)
                InvariantEmit(ref n, format, precision, maxDigits, noScientific, ref s);
            else
                CultureEmit(ref n, format, precision, maxDigits, noScientific, ref s);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void InvariantEmit(ref Digits n, char format, int precision, int maxDigits, bool noScientific, ref Sink s)
        {
            char upper = Upper(format);
            int decimals = precision < 0 ? 2 : precision;
            switch (upper)
            {
                case 'F':
                    if (n.Negative)
                        s.Add('-');
                    Fixed(ref n, decimals, false, ref s);
                    return;
                case 'N':
                    if (n.Negative)
                        s.Add('-');
                    Fixed(ref n, decimals, true, ref s);
                    return;
                case 'C':
                    // The invariant culture's currency: ¤1,234.50 and (¤1,234.50).
                    if (n.Negative)
                        s.Add('(');
                    s.Add('¤');
                    Fixed(ref n, decimals, true, ref s);
                    if (n.Negative)
                        s.Add(')');
                    return;
                case 'P':
                    if (n.Count > 0)
                        n.Scale += 2;
                    if (n.Negative)
                        s.Add('-');
                    Fixed(ref n, decimals, true, ref s);
                    s.Add(' ');
                    s.Add('%');
                    return;
                case 'E':
                    if (n.Negative)
                        s.Add('-');
                    Scientific(ref n, precision < 0 ? 6 : precision, format == 'e' ? 'e' : 'E', ref s);
                    return;
                default:
                    if (n.Negative)
                        s.Add('-');
                    General(ref n, precision > 0 ? precision : maxDigits, format == 'g' ? 'e' : 'E', noScientific, ref s);
                    return;
            }
        }

        // 1,234.50: the whole part (grouped by thousands with `group`), then
        // `decimals` digits.
        private static void Fixed(ref Digits n, int decimals, bool group, ref Sink s)
        {
            int whole = n.Scale;
            int i = 0;
            if (whole > 0)
            {
                for (int position = whole; position > 0; position--)
                {
                    if (group && position != whole && position % 3 == 0)
                        s.Add(',');
                    s.Add(DigitAt(ref n, i++));
                }
            }
            else
            {
                s.Add('0');
            }
            if (decimals > 0)
            {
                s.Add('.');
                int zeros = whole < 0 ? (-whole < decimals ? -whole : decimals) : 0;
                s.Add('0', zeros);
                decimals -= zeros;
                while (decimals > 0 && i < n.Count)
                {
                    s.Add(DigitAt(ref n, i++));
                    decimals--;
                }
                s.Add('0', decimals);
            }
        }

        // 1.234500E+003: one digit, `decimals` more, and an exponent of at
        // least three digits.
        private static void Scientific(ref Digits n, int decimals, char e, ref Sink s)
        {
            s.Add(DigitAt(ref n, 0));
            if (decimals > 0)
            {
                s.Add('.');
                int i = 1;
                while (decimals > 0 && i < n.Count)
                {
                    s.Add(DigitAt(ref n, i++));
                    decimals--;
                }
                s.Add('0', decimals);
            }
            Exponent(n.Count == 0 ? 0 : n.Scale - 1, e, 3, ref s);
        }

        // "G": the digits there are, as 1234.5 or as 1.2345E+03 when the
        // exponent passes maxDigits or the number is below 0.0001.
        private static void General(ref Digits n, int maxDigits, char e, bool noScientific, ref Sink s)
        {
            int position = n.Scale;
            bool scientific = false;
            if (!noScientific && (position > maxDigits || position < -3))
            {
                position = 1;
                scientific = true;
            }
            int i = 0;
            if (position > 0)
            {
                do
                {
                    s.Add(DigitAt(ref n, i++));
                }
                while (--position > 0);
            }
            else
            {
                s.Add('0');
            }
            if (i < n.Count || position < 0)
            {
                s.Add('.');
                s.Add('0', -position);
                while (i < n.Count)
                    s.Add(DigitAt(ref n, i++));
            }
            if (scientific)
                Exponent(n.Scale - 1, e, 2, ref s);
        }

        private static void Exponent(int exponent, char e, int minDigits, ref Sink s)
        {
            s.Add(e);
            s.Add(exponent < 0 ? '-' : '+');
            uint value = (uint)(exponent < 0 ? -exponent : exponent);
            char* digits = stackalloc char[10];
            int count = 0;
            do
            {
                digits[count++] = (char)('0' + value % 10);
                value /= 10;
            }
            while (value != 0);
            s.Add('0', minDigits - count);
            while (count > 0)
                s.Add(digits[--count]);
        }

        // ---- integers ------------------------------------------------------

        // raw: the value, sign-extended to 64 bits when signed; bits: the
        // type's width, for "X" and "B", which print the two's complement.
        internal static void FormatInteger(ulong raw, bool signed, int bits, char format, int precision, ref Sink s)
        {
            bool negative = signed && (long)raw < 0;
            ulong magnitude = negative ? 0 - raw : raw;
            char upper = Upper(format);
            if (upper == 'X' || upper == 'B')
            {
                if (bits < 64)
                    raw &= (1UL << bits) - 1;
                int shift = upper == 'X' ? 4 : 1;
                char* text = stackalloc char[64];
                int count = 0;
                char ten = format == 'x' ? 'a' : 'A';
                do
                {
                    int digit = (int)(raw & (ulong)((1 << shift) - 1));
                    text[count++] = (char)(digit < 10 ? '0' + digit : ten + digit - 10);
                    raw >>= shift;
                }
                while (raw != 0);
                s.Add('0', precision - count);
                while (count > 0)
                    s.Add(text[--count]);
                return;
            }

            byte* d = stackalloc byte[20];
            Digits n = default;
            n.D = d;
            n.Negative = negative;
            int length = 0;
            byte* reversed = stackalloc byte[20];
            do
            {
                reversed[length++] = (byte)(magnitude % 10);
                magnitude /= 10;
            }
            while (magnitude != 0);
            for (int i = 0; i < length; i++)
                d[i] = reversed[length - 1 - i];

            // "R" on an integer is "G": "R4" rounds to four digits, as in .NET.
            if (upper == 'D' || upper == '\0' || ((upper == 'G' || upper == 'R') && precision <= 0))
            {
                if (negative)
                    AddNegative(ref s);
                if (upper == 'D')
                    s.Add('0', precision - length);
                for (int i = 0; i < length; i++)
                    s.Add((char)('0' + d[i]));
                return;
            }
            // Zero has no digits here.
            n.Count = d[0] == 0 ? 0 : length;
            n.Scale = n.Count;
            if (upper == 'R')
            {
                format = format == 'r' ? 'g' : 'G';
                upper = 'G';
            }
            if (upper != 'G' && upper != 'F' && upper != 'N' && upper != 'E' && upper != 'P' && upper != 'C')
                throw BadFormat();
            int cutoff = Cutoff(upper, precision, out bool significant);
            Round(ref n, significant ? cutoff : n.Scale + cutoff);
            Emit(ref n, format, precision, 0, false, ref s);
        }

        // ---- decimal -------------------------------------------------------

        // A decimal's parts: the 96-bit integer, and the power of ten (0-28)
        // that divides it.
        internal static void FormatDecimal(ulong lo64, uint hi32, int scale, bool negative, char format, int precision, ref Sink s)
        {
            char upper = Upper(format);
            // "R" on a decimal is "G", as in .NET.
            if (upper == 'R')
            {
                format = format == 'r' ? 'g' : 'G';
                upper = 'G';
            }
            if (upper != '\0' && upper != 'G' && upper != 'R' && upper != 'F' && upper != 'N' && upper != 'E' && upper != 'P' && upper != 'C')
                throw BadFormat();

            uint* x = stackalloc uint[3];
            x[0] = (uint)lo64;
            x[1] = (uint)(lo64 >> 32);
            x[2] = hi32;
            byte* reversed = stackalloc byte[30];
            int length = 0;
            while ((x[0] | x[1] | x[2]) != 0)
            {
                ulong remainder = 0;
                for (int i = 2; i >= 0; i--)
                {
                    ulong part = (remainder << 32) | x[i];
                    x[i] = (uint)(part / 10);
                    remainder = part % 10;
                }
                reversed[length++] = (byte)remainder;
            }
            byte* d = stackalloc byte[30];
            for (int i = 0; i < length; i++)
                d[i] = reversed[length - 1 - i];
            Digits n = default;
            n.D = d;
            n.Count = length;
            n.Scale = length - scale;
            n.Negative = negative && length > 0;

            if (upper == '\0' || upper == 'R' || (upper == 'G' && precision < 0))
            {
                // All the digits, trailing zeros too (1.50m is "1.50"), never
                // in scientific notation.
                Emit(ref n, 'G', -1, 0, true, ref s);
                return;
            }
            if (upper == 'G' && precision == 0)
            {
                // "G0": without trailing zeros, and scientific past as many
                // digits as the decimal has.
                int digits = n.Count;
                Round(ref n, digits);
                Emit(ref n, format, 0, digits, false, ref s);
                return;
            }
            int cutoff = Cutoff(upper, precision, out bool significant);
            Round(ref n, significant ? cutoff : n.Scale + cutoff);
            Emit(ref n, format, precision, 0, false, ref s);
        }

        // ---- binary floating point -----------------------------------------

        internal static void FormatDouble(double value, char format, int precision, ref Sink s)
        {
            ulong bits = *(ulong*)&value;
            FormatIeee(bits, 52, 11, 17, format, precision, ref s);
        }

        internal static void FormatSingle(float value, char format, int precision, ref Sink s)
        {
            uint bits = *(uint*)&value;
            FormatIeee(bits, 23, 8, 9, format, precision, ref s);
        }

        internal static void FormatHalf(ushort bits, char format, int precision, ref Sink s) =>
            FormatIeee(bits, 10, 5, 5, format, precision, ref s);

        // An IEEE 754 value's bits: sign, exponent and fraction.
        private static void FormatIeee(ulong bits, int fractionBits, int exponentBits, int roundTripDigits, char format, int precision, ref Sink s)
        {
            bool negative = (bits >> (fractionBits + exponentBits)) != 0;
            int maxExponent = (1 << exponentBits) - 1;
            int biased = (int)(bits >> fractionBits) & maxExponent;
            ulong fraction = bits & ((1UL << fractionBits) - 1);
            if (biased == maxExponent)
            {
                FormatNonFinite(fraction != 0, negative, ref s);
                return;
            }
            // value = mantissa × 2^exponent
            int bias = maxExponent / 2 + fractionBits;
            ulong mantissa = biased == 0 ? fraction : fraction | (1UL << fractionBits);
            int exponent = biased == 0 ? 1 - bias : biased - bias;
            FormatBinary(mantissa, exponent, biased > 1 && fraction == 0, negative, roundTripDigits, format, precision, ref s);
        }

        // The value is mantissa × 2^exponent. unequalMargins: the value is a
        // power of two above the smallest normal one, so the gap below it is
        // half the gap above. roundTripDigits: 17 for double, 9 for float, 5
        // for Half, the exponent above which "G" without a precision goes
        // scientific.
        private static void FormatBinary(ulong mantissa, int exponent, bool unequalMargins, bool negative, int roundTripDigits, char format, int precision, ref Sink s)
        {
            char upper = Upper(format);
            bool shortest = upper == '\0' || upper == 'R' || (upper == 'G' && precision <= 0);
            if (!shortest && upper != 'G' && upper != 'F' && upper != 'N' && upper != 'E' && upper != 'P' && upper != 'C')
                throw BadFormat();

            Digits n = default;
            n.Negative = negative;
            n.Binary = true;
            int cutoff = -1;
            bool significant = true;
            int capacity = 20;
            if (!shortest)
            {
                cutoff = Cutoff(upper, precision, out significant);
                // At most the digits the format asks for, and at most the 767
                // significant digits a double's exact value can have.
                long estimate = significant ? cutoff : (long)EstimateScale(mantissa, exponent) + 1 + cutoff;
                capacity = estimate < 1 ? 1 : estimate > MaxExactDigits ? MaxExactDigits : (int)estimate;
            }
            // A stackalloc of a size known only at run time needs __alloca on
            // DOS, which it doesn't have: so one of two fixed sizes.
            if (capacity <= SmallDigits)
                FormatBinarySmall(mantissa, exponent, unequalMargins, cutoff, significant, capacity, shortest, roundTripDigits, format, precision, ref n, ref s);
            else
                FormatBinaryLarge(mantissa, exponent, unequalMargins, cutoff, significant, capacity, shortest, roundTripDigits, format, precision, ref n, ref s);
        }

        private const int SmallDigits = 24;

        private static void FormatBinarySmall(ulong mantissa, int exponent, bool unequalMargins, int cutoff, bool significant, int capacity, bool shortest, int roundTripDigits, char format, int precision, ref Digits n, ref Sink s)
        {
            byte* d = stackalloc byte[SmallDigits];
            n.D = d;
            FormatBinaryDigits(mantissa, exponent, unequalMargins, cutoff, significant, capacity, shortest, roundTripDigits, format, precision, ref n, ref s);
        }

        private static void FormatBinaryLarge(ulong mantissa, int exponent, bool unequalMargins, int cutoff, bool significant, int capacity, bool shortest, int roundTripDigits, char format, int precision, ref Digits n, ref Sink s)
        {
            byte* d = stackalloc byte[MaxExactDigits];
            n.D = d;
            FormatBinaryDigits(mantissa, exponent, unequalMargins, cutoff, significant, capacity, shortest, roundTripDigits, format, precision, ref n, ref s);
        }

        private static void FormatBinaryDigits(ulong mantissa, int exponent, bool unequalMargins, int cutoff, bool significant, int capacity, bool shortest, int roundTripDigits, char format, int precision, ref Digits n, ref Sink s)
        {
            if (mantissa != 0)
                Dragon4(mantissa, exponent, unequalMargins, cutoff, significant, capacity, ref n);
            if (shortest)
            {
                // "g" and "r" write a lowercase e, as in .NET.
                Emit(ref n, format == 'g' || format == 'r' ? 'g' : 'G', -1, roundTripDigits, false, ref s);
                return;
            }
            Round(ref n, n.Count);
            Emit(ref n, format, precision, 0, false, ref s);
        }

        // NaN, Infinity and -Infinity, whatever the format.
        private static void FormatNonFinite(bool nan, bool negative, ref Sink s)
        {
            if (s.Info != null)
            {
                s_culture.NonFinite(nan, negative, ref s);
                return;
            }
            if (nan)
                s.Add("NaN");
            else
                s.Add(negative ? "-Infinity" : "Infinity");
        }

        private const int MaxExactDigits = 770;

        // floor(log10(value)) + 1, or one less.
        private static int EstimateScale(ulong mantissa, int exponent)
        {
            int bits = 64;
            while (bits > 0 && (mantissa >> (bits - 1)) == 0)
                bits--;
            int log2 = exponent + bits - 1;
            // floor(log2 × log10(2)), exact for |log2| < 1650.
            return ((log2 * 78913) >> 18) + 1;
        }

        // ---- Dragon4 -------------------------------------------------------

        // Steele and White's digit generation (as .NET's Dragon4 and Burger
        // and Dybvig have it) on integers wide enough for a double's exact
        // value. With cutoff < 0 it writes the shortest digits that read back
        // as the same value; otherwise the exact value rounded to `cutoff`
        // significant digits, or to `cutoff` digits after the point, ties to
        // even.
        private const int BigLimbs = 40;

        private struct Big
        {
            public int Length;
            public fixed uint L[BigLimbs];
        }

        private static void Dragon4(ulong mantissa, int exponent, bool unequalMargins, int cutoff, bool significant, int capacity, ref Digits n)
        {
            Big value, scale, high, lowStorage, sum;
            Big* low = unequalMargins ? &lowStorage : &high;
            bool shortest = cutoff < 0;

            // value / scale is the number; high / scale and low / scale are
            // half the gaps to its neighbors.
            Set(&value, mantissa);
            if (exponent >= 0)
            {
                ShiftLeft(&value, exponent + (unequalMargins ? 2 : 1));
                Set(&scale, unequalMargins ? 4UL : 2UL);
                Set(&high, 1);
                ShiftLeft(&high, exponent + (unequalMargins ? 1 : 0));
                if (unequalMargins)
                {
                    Set(low, 1);
                    ShiftLeft(low, exponent);
                }
            }
            else
            {
                ShiftLeft(&value, unequalMargins ? 2 : 1);
                Set(&scale, 1);
                ShiftLeft(&scale, -exponent + (unequalMargins ? 2 : 1));
                Set(&high, unequalMargins ? 2UL : 1UL);
                if (unequalMargins)
                    Set(low, 1);
            }

            // Divide by 10^k so the number is in [0.1, 1).
            int k = EstimateScale(mantissa, exponent);
            if (k >= 0)
            {
                MulPow10(&scale, k);
            }
            else
            {
                MulPow10(&value, -k);
                if (shortest)
                {
                    MulPow10(&high, -k);
                    if (unequalMargins)
                        MulPow10(low, -k);
                }
            }
            bool even = (mantissa & 1) == 0;
            if (shortest)
            {
                Add(&sum, &value, &high);
                int c = Compare(&sum, &scale);
                if (even ? c >= 0 : c > 0)
                {
                    MulSmall(&scale, 10);
                    k++;
                }
            }
            else if (Compare(&value, &scale) >= 0)
            {
                MulSmall(&scale, 10);
                k++;
            }
            n.Scale = k;

            int count = 0;
            if (shortest)
            {
                while (true)
                {
                    MulSmall(&value, 10);
                    MulSmall(&high, 10);
                    if (unequalMargins)
                        MulSmall(low, 10);
                    int digit = DivideStep(&value, &scale);
                    Add(&sum, &value, &high);
                    int cLow = Compare(&value, low);
                    int cHigh = Compare(&sum, &scale);
                    bool isLow = even ? cLow <= 0 : cLow < 0;
                    bool isHigh = even ? cHigh >= 0 : cHigh > 0;
                    if (!isLow && !isHigh && count < capacity - 1)
                    {
                        n.D[count++] = (byte)digit;
                        continue;
                    }
                    bool up = isHigh && !isLow;
                    if (isLow == isHigh)
                    {
                        ShiftLeft(&value, 1);
                        int c = Compare(&value, &scale);
                        up = c > 0 || (c == 0 && (digit & 1) != 0);
                    }
                    n.D[count++] = (byte)digit;
                    if (up)
                        RoundUp(ref n, ref count);
                    break;
                }
            }
            else
            {
                int wanted = significant ? cutoff : k + cutoff;
                if (wanted > capacity)
                    wanted = capacity;
                if (wanted > 0)
                {
                    while (true)
                    {
                        MulSmall(&value, 10);
                        int digit = DivideStep(&value, &scale);
                        n.D[count++] = (byte)digit;
                        if (value.Length == 0)
                            break;
                        if (count == wanted)
                        {
                            ShiftLeft(&value, 1);
                            int c = Compare(&value, &scale);
                            if (c > 0 || (c == 0 && (digit & 1) != 0))
                                RoundUp(ref n, ref count);
                            break;
                        }
                    }
                }
                else if (wanted == 0)
                {
                    // Rounds to 0 or to one unit of the last place kept; a tie
                    // goes to 0, the even one.
                    ShiftLeft(&value, 1);
                    if (Compare(&value, &scale) > 0)
                    {
                        n.D[count++] = 1;
                        n.Scale++;
                    }
                }
            }
            n.Count = count;
        }

        // Adds one to the last digit, carrying.
        private static void RoundUp(ref Digits n, ref int count)
        {
            while (count > 0 && n.D[count - 1] == 9)
                count--;
            if (count == 0)
            {
                n.D[0] = 1;
                count = 1;
                n.Scale++;
            }
            else
            {
                n.D[count - 1]++;
            }
        }

        // value / scale, which is below 10; value becomes the remainder.
        private static int DivideStep(Big* value, Big* scale)
        {
            int digit = 0;
            while (Compare(value, scale) >= 0)
            {
                Sub(value, scale);
                digit++;
            }
            return digit;
        }

        private static void Set(Big* x, ulong value)
        {
            x->L[0] = (uint)value;
            x->L[1] = (uint)(value >> 32);
            x->Length = x->L[1] != 0 ? 2 : x->L[0] != 0 ? 1 : 0;
        }

        private static void MulSmall(Big* x, uint factor)
        {
            ulong carry = 0;
            for (int i = 0; i < x->Length; i++)
            {
                ulong product = (ulong)x->L[i] * factor + carry;
                x->L[i] = (uint)product;
                carry = product >> 32;
            }
            if (carry != 0)
                x->L[x->Length++] = (uint)carry;
        }

        private static void MulPow10(Big* x, int power)
        {
            while (power >= 9)
            {
                MulSmall(x, 1_000_000_000);
                power -= 9;
            }
            uint factor = 1;
            while (power-- > 0)
                factor *= 10;
            if (factor != 1)
                MulSmall(x, factor);
        }

        private static void ShiftLeft(Big* x, int shift)
        {
            if (x->Length == 0)
                return;
            int words = shift / 32;
            int bits = shift % 32;
            int length = x->Length;
            if (bits == 0)
            {
                for (int i = length - 1; i >= 0; i--)
                    x->L[i + words] = x->L[i];
            }
            else
            {
                x->L[length + words] = 0;
                for (int i = length - 1; i >= 0; i--)
                {
                    x->L[i + words + 1] |= x->L[i] >> (32 - bits);
                    x->L[i + words] = x->L[i] << bits;
                }
                length++;
            }
            for (int i = 0; i < words; i++)
                x->L[i] = 0;
            length += words;
            while (length > 0 && x->L[length - 1] == 0)
                length--;
            x->Length = length;
        }

        private static int Compare(Big* a, Big* b)
        {
            if (a->Length != b->Length)
                return a->Length < b->Length ? -1 : 1;
            for (int i = a->Length - 1; i >= 0; i--)
            {
                if (a->L[i] != b->L[i])
                    return a->L[i] < b->L[i] ? -1 : 1;
            }
            return 0;
        }

        // a -= b, for a >= b.
        private static void Sub(Big* a, Big* b)
        {
            long borrow = 0;
            for (int i = 0; i < a->Length; i++)
            {
                long difference = (long)a->L[i] - (i < b->Length ? b->L[i] : 0) + borrow;
                a->L[i] = (uint)difference;
                borrow = difference >> 32;
            }
            int length = a->Length;
            while (length > 0 && a->L[length - 1] == 0)
                length--;
            a->Length = length;
        }

        private static void Add(Big* result, Big* a, Big* b)
        {
            int length = a->Length > b->Length ? a->Length : b->Length;
            ulong carry = 0;
            for (int i = 0; i < length; i++)
            {
                ulong total = (ulong)(i < a->Length ? a->L[i] : 0) + (i < b->Length ? b->L[i] : 0) + carry;
                result->L[i] = (uint)total;
                carry = total >> 32;
            }
            if (carry != 0)
                result->L[length++] = (uint)carry;
            result->Length = length;
        }
    }
}
