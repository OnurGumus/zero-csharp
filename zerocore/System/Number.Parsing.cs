// Text to numbers, as .NET's Parse and TryParse read it in the invariant
// culture, for every NumberStyles: the same text is accepted and rejected,
// with the same answer, the same FormatException or OverflowException, and
// the same ArgumentException for a style .NET refuses (scripts/parse-check
// compares the two over millions of strings).
//
// What .NET does and this does too:
// - Integers: whitespace around the number is 0x09-0x0D and ' ' only; with
//   NumberStyles.Number or Float, "1,000", "1.00" and "1e3" are integers,
//   "1.5" overflows. Hex and binary digits take no sign and no "0x"; they are
//   the bits of the type, so sbyte.Parse("FF", NumberStyles.HexNumber) is -1.
// - double, float and Half are correctly rounded, ties to even, from all the
//   digits given (up to .NET's 768, 113 and 22 significant ones; past those
//   only whether a digit isn't zero counts). Too large is Infinity, too small
//   is 0, "-0" is -0.0. "Infinity", "-Infinity" and "NaN" are read in any
//   case, with surrounding whitespace.
// - Text ending in '\0' characters reads as if they weren't there.
//
// A double with at most 15 or so digits and a small exponent is one exact
// multiply or divide (Clinger's fast path); a whole number below 2^64 is its
// own bits. Anything else is divided out exactly in big integers, to 63 bits
// and whether anything remains, and rounded once: no double rounding, so
// float and Half are as exact as double.
//
// This file uses nothing but Span, NumberStyles, the exceptions, AsSpan
// and FromChars, so scripts/parse-check can build it into an ordinary .NET
// program.

using System.Globalization;
using System.Runtime.CompilerServices;

namespace System
{
    internal static unsafe partial class Number
    {
        internal const int Parsed = 0;
        internal const int NotANumber = 1;
        internal const int OutOfRange = 2;

        // ---- styles ----------------------------------------------------------

        private const NumberStyles AllStyles = NumberStyles.Any | NumberStyles.AllowHexSpecifier | NumberStyles.AllowBinarySpecifier;

        internal static void ValidateIntegerStyle(NumberStyles style)
        {
            if ((style & ~AllStyles) != 0)
                throw new ArgumentException("An undefined NumberStyles value is being used.", "style");
            if ((style & NumberStyles.AllowHexSpecifier) != 0 && (style & ~NumberStyles.HexNumber) != 0
                || (style & NumberStyles.AllowBinarySpecifier) != 0 && (style & ~NumberStyles.BinaryNumber) != 0)
                throw new ArgumentException("With the AllowHexSpecifier or AllowBinarySpecifier bit set in the enum bit field, the only other valid bits that can be combined into the enum value must be AllowLeadingWhite and AllowTrailingWhite.", "style");
        }

        internal static void ValidateFloatStyle(NumberStyles style)
        {
            if ((style & ~AllStyles) != 0)
                throw new ArgumentException("An undefined NumberStyles value is being used.", "style");
            if ((style & (NumberStyles.AllowHexSpecifier | NumberStyles.AllowBinarySpecifier)) != 0)
                throw new ArgumentException("The number styles AllowHexSpecifier and AllowBinarySpecifier are not supported on floating point data types.", "style");
        }

        internal static ReadOnlySpan<char> NotNull(string s)
        {
            if (s is null)
                throw new ArgumentNullException("s");
            return AsSpan(s);
        }

        internal static FormatException BadNumber(ReadOnlySpan<char> s)
        {
            fixed (char* chars = s)
                return new FormatException("The input string '" + FromChars(chars, s.Length) + "' was not in a correct format.");
        }

        // ---- integers --------------------------------------------------------

        // Parse for an integer type of `bits` bits (`maxDigits` decimal digits):
        // the value's bits, which the caller truncates to its type.
        internal static ulong ParseInteger(ReadOnlySpan<char> s, NumberStyles style, bool signed, int bits, int maxDigits, string overflowMessage)
        {
            ValidateIntegerStyle(style);
            int status = ParseIntegerCore(s, style, signed, bits, maxDigits, out ulong raw);
            if (status == NotANumber)
                throw BadNumber(s);
            if (status == OutOfRange)
                throw new OverflowException(overflowMessage);
            return raw;
        }

        internal static bool TryParseInteger(ReadOnlySpan<char> s, NumberStyles style, bool signed, int bits, int maxDigits, out ulong raw)
        {
            ValidateIntegerStyle(style);
            if (ParseIntegerCore(s, style, signed, bits, maxDigits, out raw) == Parsed)
                return true;
            raw = 0;
            return false;
        }

        private static int ParseIntegerCore(ReadOnlySpan<char> s, NumberStyles style, bool signed, int bits, int maxDigits, out ulong raw)
        {
            raw = 0;
            if ((style & NumberStyles.AllowHexSpecifier) != 0)
                return ParseBits(s, style, bits, 4, out raw);
            if ((style & NumberStyles.AllowBinarySpecifier) != 0)
                return ParseBits(s, style, bits, 1, out raw);

            byte* digits = stackalloc byte[maxDigits];
            NumberText n = default;
            n.Digits = digits;
            n.Capacity = maxDigits;
            n.Integer = true;
            if (!Scan(s, style, ref n))
                return NotANumber;

            // The digits, then Scale - Count zeros; nothing may follow the point.
            if (n.Scale > maxDigits || n.Scale < n.Count || (!signed && n.Negative) || n.NonZeroTail)
                return OutOfRange;
            ulong limit = signed
                ? (n.Negative ? 1UL << (bits - 1) : (1UL << (bits - 1)) - 1)
                : (bits == 64 ? 0xFFFFFFFFFFFFFFFFUL : (1UL << bits) - 1);
            ulong value = 0;
            for (int i = 0; i < n.Scale; i++)
            {
                uint digit = i < n.Count ? digits[i] : 0u;
                if (value > (limit - digit) / 10)
                    return OutOfRange;
                value = value * 10 + digit;
            }
            raw = n.Negative ? 0 - value : value;
            return Parsed;
        }

        // Hex (4 bits a digit) or binary (1): no sign, no prefix, at most
        // `bits` bits once leading zeros are dropped.
        private static int ParseBits(ReadOnlySpan<char> s, NumberStyles style, int bits, int digitBits, out ulong raw)
        {
            raw = 0;
            int p = 0;
            if ((style & NumberStyles.AllowLeadingWhite) != 0)
            {
                while (p < s.Length && IsWhite(s[p]))
                    p++;
            }
            if (p == s.Length || BitsDigit(s[p], digitBits) < 0)
                return NotANumber;

            ulong value = 0;
            int significant = 0;
            bool overflow = false;
            for (; p < s.Length; p++)
            {
                int digit = BitsDigit(s[p], digitBits);
                if (digit < 0)
                    break;
                if (value == 0 && digit == 0)
                    continue;
                if (significant == bits / digitBits)
                    overflow = true;
                else
                {
                    value = (value << digitBits) | (uint)digit;
                    significant++;
                }
            }
            if ((style & NumberStyles.AllowTrailingWhite) != 0)
            {
                while (p < s.Length && IsWhite(s[p]))
                    p++;
            }
            if (!OnlyNulls(s, p))
                return NotANumber;
            if (overflow)
                return OutOfRange;
            raw = value;
            return Parsed;
        }

        private static int BitsDigit(char c, int digitBits)
        {
            if (digitBits == 1)
                return c == '0' ? 0 : c == '1' ? 1 : -1;
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        // ---- the text of a number --------------------------------------------

        // The significant digits as .NET's NumberBuffer keeps them: no leading
        // zeros, no trailing ones, and the value is 0.D1D2D3... * 10^Scale.
        private struct NumberText
        {
            public byte* Digits;
            public int Capacity;
            public int Count;
            public int Scale;
            public bool Negative;
            public bool NonZeroTail; // a digit past Capacity wasn't zero
            public bool Integer;
        }

        private const int StateSign = 1;
        private const int StateParens = 2;
        private const int StateDigits = 4;
        private const int StateNonZero = 8;
        private const int StateDecimal = 16;
        private const int StateCurrency = 32;

        private const char CurrencySymbol = '\u00A4';

        private static bool IsWhite(char c) => c == ' ' || (uint)(c - '\t') <= '\r' - '\t';
        private static bool IsDigit(char c) => (uint)(c - '0') <= 9;
        private static char At(ReadOnlySpan<char> s, int i) => i < s.Length ? s[i] : '\0';

        private static bool OnlyNulls(ReadOnlySpan<char> s, int p)
        {
            for (; p < s.Length; p++)
            {
                if (s[p] != '\0')
                    return false;
            }
            return true;
        }

        // The steps of .NET's TryParseNumber, in the invariant culture.
        private static bool Scan(ReadOnlySpan<char> s, NumberStyles style, ref NumberText n)
        {
            int state = 0;
            bool currency = (style & NumberStyles.AllowCurrencySymbol) != 0;
            int p = 0;
            char ch = At(s, 0);

            // Whitespace, a sign or '(' and the currency symbol, in any order;
            // no whitespace between a sign and the digits ("- 5"), unless the
            // currency symbol came first.
            while (true)
            {
                if (!IsWhite(ch) || (style & NumberStyles.AllowLeadingWhite) == 0
                    || ((state & StateSign) != 0 && (state & StateCurrency) == 0))
                {
                    if ((style & NumberStyles.AllowLeadingSign) != 0 && (state & StateSign) == 0 && (ch == '+' || ch == '-'))
                    {
                        state |= StateSign;
                        if (ch == '-')
                            n.Negative = true;
                    }
                    else if (ch == '(' && (style & NumberStyles.AllowParentheses) != 0 && (state & StateSign) == 0)
                    {
                        state |= StateSign | StateParens;
                        n.Negative = true;
                    }
                    else if (currency && ch == CurrencySymbol)
                    {
                        state |= StateCurrency;
                        currency = false;
                    }
                    else
                        break;
                }
                ch = At(s, ++p);
            }

            // Digits, the point and group separators.
            int count = 0;
            int end = 0;
            while (true)
            {
                if (IsDigit(ch))
                {
                    state |= StateDigits;
                    if (ch != '0' || (state & StateNonZero) != 0)
                    {
                        if (count < n.Capacity)
                        {
                            n.Digits[count] = (byte)(ch - '0');
                            if (ch != '0')
                                end = count + 1;
                        }
                        else if (ch != '0')
                            n.NonZeroTail = true;
                        if ((state & StateDecimal) == 0)
                            n.Scale++;
                        count++;
                        state |= StateNonZero;
                    }
                    else if ((state & StateDecimal) != 0)
                        n.Scale--;
                }
                else if ((style & NumberStyles.AllowDecimalPoint) != 0 && (state & StateDecimal) == 0 && ch == '.')
                    state |= StateDecimal;
                else if ((style & NumberStyles.AllowThousands) != 0 && (state & StateDigits) != 0
                    && (state & StateDecimal) == 0 && ch == ',')
                {
                }
                else
                    break;
                ch = At(s, ++p);
            }
            n.Count = end;
            if ((state & StateDigits) == 0)
                return false;

            // The exponent; an 'e' without digits after it isn't one.
            if ((ch == 'E' || ch == 'e') && (style & NumberStyles.AllowExponent) != 0)
            {
                int before = p;
                ch = At(s, ++p);
                bool negative = false;
                if (ch == '+')
                    ch = At(s, ++p);
                else if (ch == '-')
                {
                    negative = true;
                    ch = At(s, ++p);
                }
                if (IsDigit(ch))
                {
                    int exponent = 0;
                    do
                    {
                        if (exponent >= 100_000_000)
                        {
                            // Far past any result but 0 or Infinity.
                            exponent = int.MaxValue;
                            n.Scale = 0;
                            while (IsDigit(ch))
                                ch = At(s, ++p);
                            break;
                        }
                        exponent = exponent * 10 + (ch - '0');
                        ch = At(s, ++p);
                    }
                    while (IsDigit(ch));
                    n.Scale += negative ? -exponent : exponent;
                }
                else
                {
                    p = before;
                    ch = At(s, p);
                }
            }

            // Whitespace, a trailing sign, ')' and the currency symbol.
            while (true)
            {
                if (!IsWhite(ch) || (style & NumberStyles.AllowTrailingWhite) == 0)
                {
                    if ((style & NumberStyles.AllowTrailingSign) != 0 && (state & StateSign) == 0 && (ch == '+' || ch == '-'))
                    {
                        state |= StateSign;
                        if (ch == '-')
                            n.Negative = true;
                    }
                    else if (ch == ')' && (state & StateParens) != 0)
                        state &= ~StateParens;
                    else if (currency && ch == CurrencySymbol)
                        currency = false;
                    else
                        break;
                }
                ch = At(s, ++p);
            }
            if ((state & StateParens) != 0)
                return false;
            if ((state & StateNonZero) == 0)
            {
                n.Scale = 0;
                // An integer zero has no sign, unless it had a point ("-0.0").
                if (n.Integer && (state & StateDecimal) == 0)
                    n.Negative = false;
            }
            return OnlyNulls(s, p);
        }

        // ---- double, float and Half ------------------------------------------

        // A binary floating-point format: `precision` significand bits (with
        // the implicit one), `exponentBits`, and NaN's bits.
        internal struct FloatFormat
        {
            public int Precision;
            public int ExponentBits;
            public int MaxDigits;  // .NET's: past these only a nonzero tail counts
            public int MaxScale;   // 10^MaxScale and above is Infinity
            public int MinScale;   // below 10^MinScale is 0
            public ulong NaN;
        }

        internal static FloatFormat DoubleFormat => new FloatFormat
        {
            Precision = 53, ExponentBits = 11, MaxDigits = 768, MaxScale = 309, MinScale = -324, NaN = 0xFFF8000000000000UL,
        };

        internal static FloatFormat SingleFormat => new FloatFormat
        {
            Precision = 24, ExponentBits = 8, MaxDigits = 113, MaxScale = 39, MinScale = -46, NaN = 0xFFC00000UL,
        };

        internal static FloatFormat HalfFormat => new FloatFormat
        {
            Precision = 11, ExponentBits = 5, MaxDigits = 22, MaxScale = 5, MinScale = -8, NaN = 0xFE00UL,
        };

        internal static ulong ParseFloat(ReadOnlySpan<char> s, NumberStyles style, FloatFormat format)
        {
            ValidateFloatStyle(style);
            if (!ParseFloatCore(s, style, format, out ulong bits))
                throw BadNumber(s);
            return bits;
        }

        internal static bool TryParseFloat(ReadOnlySpan<char> s, NumberStyles style, FloatFormat format, out ulong bits)
        {
            ValidateFloatStyle(style);
            return ParseFloatCore(s, style, format, out bits);
        }

        private static bool ParseFloatCore(ReadOnlySpan<char> s, NumberStyles style, FloatFormat format, out ulong bits)
        {
            byte* digits = stackalloc byte[format.MaxDigits];
            NumberText n = default;
            n.Digits = digits;
            n.Capacity = format.MaxDigits;
            if (Scan(s, style, ref n))
            {
                bits = ToFloat(ref n, format);
                return true;
            }
            return ParseSymbol(s, format, out bits);
        }

        // "Infinity", "-Infinity", "NaN", "+Infinity", "+NaN" and "-NaN", in
        // any case, with whitespace around them, whatever the style.
        private static bool ParseSymbol(ReadOnlySpan<char> s, out bool negative, out bool infinity)
        {
            negative = false;
            infinity = false;
            int start = 0, end = s.Length;
            while (start < end && IsWhiteSpace(s[start]))
                start++;
            while (end > start && IsWhiteSpace(s[end - 1]))
                end--;
            ReadOnlySpan<char> text = s.Slice(start, end - start);
            if (text.Length > 0 && (text[0] == '+' || text[0] == '-'))
            {
                negative = text[0] == '-';
                text = text.Slice(1);
            }
            if (EqualsIgnoreCase(text, "infinity"))
                return infinity = true;
            if (EqualsIgnoreCase(text, "nan"))
                return true;
            return false;
        }

        private static bool ParseSymbol(ReadOnlySpan<char> s, FloatFormat format, out ulong bits)
        {
            bits = 0;
            if (!ParseSymbol(s, out bool negative, out bool infinity))
                return false;
            if (!infinity)
            {
                bits = format.NaN;
                return true;
            }
            int fraction = format.Precision - 1;
            bits = ((1UL << format.ExponentBits) - 1) << fraction;
            if (negative)
                bits |= 1UL << (fraction + format.ExponentBits);
            return true;
        }

        // char.IsWhiteSpace's set, without the Unicode tables.
        private static bool IsWhiteSpace(char c)
            => c == ' ' || (c >= '\t' && c <= '\r') || c == '\u0085' || c == '\u00A0' || c == '\u1680'
            || (c >= '\u2000' && c <= '\u200A') || c == '\u2028' || c == '\u2029' || c == '\u202F'
            || c == '\u205F' || c == '\u3000';

        private static bool EqualsIgnoreCase(ReadOnlySpan<char> text, string lowercase)
        {
            if (text.Length != lowercase.Length)
                return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 'A' && c <= 'Z')
                    c = (char)(c + 32);
                if (c != lowercase[i])
                    return false;
            }
            return true;
        }

        private static ulong ToFloat(ref NumberText n, FloatFormat format)
        {
            ulong sign = n.Negative ? 1UL << (format.Precision - 1 + format.ExponentBits) : 0;
            if (n.Count == 0 || n.Scale < format.MinScale)
                return sign;
            if (n.Scale > format.MaxScale)
                return sign | Infinity(format);

            // The value is digits * 10^exponent.
            int exponent = n.Scale - n.Count;
            if (n.Count <= 19)
            {
                ulong digits = 0;
                for (int i = 0; i < n.Count; i++)
                    digits = digits * 10 + n.Digits[i];

                // A whole number below 2^64: its bits as they are.
                ulong whole = digits;
                int e = exponent;
                while (e > 0 && whole <= 0xFFFFFFFFFFFFFFFFUL / 10)
                {
                    whole *= 10;
                    e--;
                }
                if (e == 0)
                    return sign | Round(whole, 0, n.NonZeroTail, format);

                // Both exact as doubles, so one multiply or divide rounds
                // correctly (Clinger's fast path); only for double, as for
                // float or Half that would round twice.
                if (format.Precision == 53 && !n.NonZeroTail && digits <= 1UL << 53 && exponent >= -22 && exponent <= 22)
                {
                    double value = exponent >= 0 ? digits * Pow10(exponent) : digits / Pow10(-exponent);
                    return sign | *(ulong*)&value;
                }
            }
            return sign | Divide(ref n, exponent, format);
        }

        private static ulong Infinity(FloatFormat format) =>
            ((1UL << format.ExponentBits) - 1) << (format.Precision - 1);

        // 10^n, exact for n <= 22: every power and product is a representable
        // integer.
        private static double Pow10(int n)
        {
            double result = 1;
            double power = 10;
            while (n > 0)
            {
                if ((n & 1) != 0)
                    result *= power;
                power *= power;
                n >>= 1;
            }
            return result;
        }

        // The value (q + a little, if `sticky`) * 2^e2, rounded once to the
        // format, ties to even: its bits, without the sign.
        private static ulong Round(ulong q, int e2, bool sticky, FloatFormat format)
        {
            if (q == 0)
                return 0;
            int fractionBits = format.Precision - 1;
            int maxExponent = (1 << (format.ExponentBits - 1)) - 1;
            int minExponent = 1 - maxExponent;
            int length = 64;
            while ((q >> (length - 1)) == 0)
                length--;
            int top = length - 1 + e2; // the power of two of the leading bit
            if (top > maxExponent)
                return Infinity(format);

            // The power of two of the last bit kept: fixed for denormals.
            int unit = (top > minExponent ? top : minExponent) - fractionBits;
            int drop = unit - e2;
            ulong whole;
            if (drop <= 0)
                whole = q << -drop; // fits: length - drop <= precision
            else if (drop > 64)
                return 0; // below half the smallest denormal
            else
            {
                whole = drop == 64 ? 0 : q >> drop;
                ulong rest = drop == 64 ? q : q & ((1UL << drop) - 1);
                ulong half = 1UL << (drop - 1);
                if (rest > half || (rest == half && (sticky || (whole & 1) != 0)))
                    whole++;
                if (whole == 1UL << format.Precision)
                {
                    whole >>= 1;
                    unit++;
                }
            }

            if (whole < 1UL << fractionBits)
                return whole; // denormal, or 0
            int biased = unit + fractionBits + maxExponent;
            if (biased >= (1 << format.ExponentBits) - 1)
                return Infinity(format);
            return ((ulong)biased << fractionBits) | (whole & ((1UL << fractionBits) - 1));
        }

        // ---- exact division ----------------------------------------------------

        // Big enough for 768 digits over 5^1092, shifted to 63 bits more.
        private const int ParseLimbs = 90;

        private struct ParseBig
        {
            public int Length;
            public fixed uint L[ParseLimbs];
        }

        // digits * 10^exponent = (digits * 5^exponent) * 2^exponent, as a
        // quotient of big integers, divided out to 62 or 63 bits; the remainder
        // (or a digit past the 768th) only says the value is a little more.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong Divide(ref NumberText n, int exponent, FloatFormat format)
        {
            ParseBig top, bottom;
            top.Length = 0;
            bottom.Length = 0;
            for (int i = 0; i < n.Count; i += 9)
            {
                int take = n.Count - i < 9 ? n.Count - i : 9;
                uint chunk = 0, factor = 1;
                for (int j = 0; j < take; j++)
                {
                    chunk = chunk * 10 + n.Digits[i + j];
                    factor *= 10;
                }
                BigMulAdd(&top, factor, chunk);
            }
            BigMulAdd(&bottom, 1, 1);
            if (exponent >= 0)
                BigMulPow5(&top, exponent);
            else
                BigMulPow5(&bottom, -exponent);
            int e2 = exponent;

            // Shift so that top / bottom is in [2^61, 2^63).
            int shift = 62 - (BigBitLength(&top) - BigBitLength(&bottom));
            if (shift > 0)
                BigShiftLeft(&top, shift);
            else
                BigShiftLeft(&bottom, -shift);
            e2 -= shift;

            // Long division, a bit at a time, from 2^62 down.
            BigShiftLeft(&bottom, 62);
            ulong q = 0;
            for (int bit = 62; bit >= 0; bit--)
            {
                q <<= 1;
                if (BigCompare(&top, &bottom) >= 0)
                {
                    BigSub(&top, &bottom);
                    q |= 1;
                }
                BigShiftRight1(&bottom);
            }
            return Round(q, e2, top.Length != 0 || n.NonZeroTail, format);
        }

        // x = x * factor + add.
        private static void BigMulAdd(ParseBig* x, uint factor, uint add)
        {
            ulong carry = add;
            for (int i = 0; i < x->Length; i++)
            {
                ulong product = (ulong)x->L[i] * factor + carry;
                x->L[i] = (uint)product;
                carry = product >> 32;
            }
            if (carry != 0)
                x->L[x->Length++] = (uint)carry;
        }

        private static void BigMulPow5(ParseBig* x, int power)
        {
            while (power >= 13)
            {
                BigMulAdd(x, 1220703125, 0); // 5^13
                power -= 13;
            }
            uint factor = 1;
            while (power-- > 0)
                factor *= 5;
            if (factor != 1)
                BigMulAdd(x, factor, 0);
        }

        private static int BigBitLength(ParseBig* x)
        {
            if (x->Length == 0)
                return 0;
            uint high = x->L[x->Length - 1];
            int bits = 32;
            while ((high >> (bits - 1)) == 0)
                bits--;
            return (x->Length - 1) * 32 + bits;
        }

        private static void BigShiftLeft(ParseBig* x, int shift)
        {
            if (x->Length == 0 || shift == 0)
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

        private static void BigShiftRight1(ParseBig* x)
        {
            int length = x->Length;
            for (int i = 0; i < length; i++)
                x->L[i] = (x->L[i] >> 1) | (i + 1 < length ? x->L[i + 1] << 31 : 0);
            if (length > 0 && x->L[length - 1] == 0)
                x->Length = length - 1;
        }

        private static int BigCompare(ParseBig* a, ParseBig* b)
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
        private static void BigSub(ParseBig* a, ParseBig* b)
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
    }
}
