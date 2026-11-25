// String support for zero mode.
//
// The core library's string (zerocore/System/String.cs) has Length, an
// indexer, ==, !=, + (string operands), Equals, IsNullOrEmpty and string
// switch; $"..." with non-string holes, Substring and parsing are missing.
// This file fills the gap without a garbage collector:
//
// - System.MemoryExtensions: read-only operations on ReadOnlySpan<char>
//   (SequenceEqual, StartsWith, IndexOf, Trim, Split, ...), with the same
//   names as .NET. Defining AsSpan(string) also switches on C# 14's implicit
//   string -> ReadOnlySpan<char> conversion, so strings can be passed to any
//   span parameter. None of these allocate.
// - Str: parsing, ordinal comparison, and new strings built in an Arena
//   (Str.Format(arena, $"..."), Str.Concat). Arena strings are ordinary
//   strings, freed in bulk when their arena is reset.
// - $"..." works through custom interpolated string handlers:
//   Console.WriteLine($"...") writes straight to stdout,
//   StrBuilder.Append($"...") into the arena.
//
// a + b mallocs a string that is never freed; in a loop, prefer Str.Concat or
// Str.Format, which use the arena. Instead of s.Substring(i, n) use
// s.AsSpan(i, n). A null string acts as an empty one everywhere here.

using System;
using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices
{
    // The compiler finds these by name; the core library doesn't define them.
    public sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }

    public sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
    {
        public InterpolatedStringHandlerArgumentAttribute(string argument) => Argument = argument;
        public string Argument { get; }
    }
}

namespace System
{
    public static partial class MemoryExtensions
    {
        // ---- string -> span ----------------------------------------------

        /// <summary>The string's characters, without copying. Null gives an empty span.</summary>
        public static ReadOnlySpan<char> AsSpan(this string text)
        {
            if (text is null)
                return default;
            unsafe
            {
                // SAFETY: zero mode has no GC, so a string never moves and the
                // pointer outlives the fixed block. An arena string is valid until
                // its arena is reset, like any other arena memory.
                fixed (char* p = text)
                    return new ReadOnlySpan<char>(p, text.Length);
            }
        }

        public static ReadOnlySpan<char> AsSpan(this string text, int start)
        {
            ReadOnlySpan<char> s = text.AsSpan();
            if ((uint)start > (uint)s.Length)
                Environment.FailFast("AsSpan: start out of range");
            return s.Slice(start);
        }

        public static ReadOnlySpan<char> AsSpan(this string text, int start, int length)
        {
            ReadOnlySpan<char> s = text.AsSpan();
            if ((uint)start > (uint)s.Length || (uint)length > (uint)(s.Length - start))
                Environment.FailFast("AsSpan: range out of range");
            return s.Slice(start, length);
        }

        // ---- comparison ---------------------------------------------------

        /// <summary>Ordinal equality: the replacement for a == b.</summary>
        public static bool SequenceEqual(this ReadOnlySpan<char> span, ReadOnlySpan<char> other)
        {
            if (span.Length != other.Length)
                return false;
            for (int i = 0; i < span.Length; i++)
                if (span[i] != other[i])
                    return false;
            return true;
        }

        public static bool StartsWith(this ReadOnlySpan<char> span, ReadOnlySpan<char> value)
            => value.Length <= span.Length && span.Slice(0, value.Length).SequenceEqual(value);

        public static bool EndsWith(this ReadOnlySpan<char> span, ReadOnlySpan<char> value)
            => value.Length <= span.Length && span.Slice(span.Length - value.Length).SequenceEqual(value);

        // ---- search -------------------------------------------------------

        public static int IndexOf(this ReadOnlySpan<char> span, char value)
        {
            for (int i = 0; i < span.Length; i++)
                if (span[i] == value)
                    return i;
            return -1;
        }

        /// <summary>First index of <paramref name="value"/>, or -1. An empty value is found at 0.</summary>
        public static int IndexOf(this ReadOnlySpan<char> span, ReadOnlySpan<char> value)
        {
            for (int i = 0; i + value.Length <= span.Length; i++)
                if (span.Slice(i, value.Length).SequenceEqual(value))
                    return i;
            return -1;
        }

        public static int LastIndexOf(this ReadOnlySpan<char> span, char value)
        {
            for (int i = span.Length - 1; i >= 0; i--)
                if (span[i] == value)
                    return i;
            return -1;
        }

        public static bool Contains(this ReadOnlySpan<char> span, char value) => span.IndexOf(value) >= 0;

        public static bool Contains(this ReadOnlySpan<char> span, ReadOnlySpan<char> value) => span.IndexOf(value) >= 0;

        // ---- trimming (returns a slice, never copies) -----------------------

        public static ReadOnlySpan<char> Trim(this ReadOnlySpan<char> span) => span.TrimStart().TrimEnd();

        public static ReadOnlySpan<char> TrimStart(this ReadOnlySpan<char> span)
        {
            int i = 0;
            while (i < span.Length && Str.IsWhiteSpace(span[i]))
                i++;
            return span.Slice(i);
        }

        public static ReadOnlySpan<char> TrimEnd(this ReadOnlySpan<char> span)
        {
            int end = span.Length;
            while (end > 0 && Str.IsWhiteSpace(span[end - 1]))
                end--;
            return span.Slice(0, end);
        }

        // ---- splitting ----------------------------------------------------

        /// <summary>
        /// Splits on <paramref name="separator"/> without allocating: use it in foreach.
        /// Like string.Split, "a,,b" gives "a", "", "b" and "" gives one empty item.
        /// </summary>
        public static SpanSplitEnumerator Split(this ReadOnlySpan<char> span, char separator)
            => new SpanSplitEnumerator(span, separator);

        // ---- the same operations on a string receiver ----------------------
        // C# converts string to ReadOnlySpan<char> for arguments, not for the
        // receiver of an extension method, so "abc".StartsWith(...) needs these.

        public static bool SequenceEqual(this string text, ReadOnlySpan<char> other) => text.AsSpan().SequenceEqual(other);
        public static bool StartsWith(this string text, ReadOnlySpan<char> value) => text.AsSpan().StartsWith(value);
        public static bool EndsWith(this string text, ReadOnlySpan<char> value) => text.AsSpan().EndsWith(value);
        public static int IndexOf(this string text, char value) => text.AsSpan().IndexOf(value);
        public static int IndexOf(this string text, ReadOnlySpan<char> value) => text.AsSpan().IndexOf(value);
        public static int LastIndexOf(this string text, char value) => text.AsSpan().LastIndexOf(value);
        public static bool Contains(this string text, char value) => text.AsSpan().Contains(value);
        public static bool Contains(this string text, ReadOnlySpan<char> value) => text.AsSpan().Contains(value);
        public static ReadOnlySpan<char> Trim(this string text) => text.AsSpan().Trim();
        public static ReadOnlySpan<char> TrimStart(this string text) => text.AsSpan().TrimStart();
        public static ReadOnlySpan<char> TrimEnd(this string text) => text.AsSpan().TrimEnd();
        public static SpanSplitEnumerator Split(this string text, char separator) => text.AsSpan().Split(separator);
    }

    public ref struct SpanSplitEnumerator
    {
        ReadOnlySpan<char> _rest;
        ReadOnlySpan<char> _current;
        readonly char _separator;
        bool _done;

        internal SpanSplitEnumerator(ReadOnlySpan<char> span, char separator)
        {
            _rest = span;
            _current = default;
            _separator = separator;
            _done = false;
        }

        public SpanSplitEnumerator GetEnumerator() => this;

        public ReadOnlySpan<char> Current => _current;

        public bool MoveNext()
        {
            if (_done)
                return false;

            int i = _rest.IndexOf(_separator);
            if (i < 0)
            {
                _current = _rest;
                _done = true;
            }
            else
            {
                _current = _rest.Slice(0, i);
                _rest = _rest.Slice(i + 1);
            }
            return true;
        }
    }
}

public static class Str
{
    /// <summary>Same set as .NET's char.IsWhiteSpace.</summary>
    public static bool IsWhiteSpace(char c)
        => c == ' ' || (c >= 0x09 && c <= 0x0D) || c == 0x85 || c == 0xA0 || c == 0x1680
        || (c >= 0x2000 && c <= 0x200A) || c == 0x2028 || c == 0x2029 || c == 0x202F
        || c == 0x205F || c == 0x3000;

    /// <summary>Ordinal comparison: negative, zero or positive, like string.CompareOrdinal.</summary>
    public static int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int n = a.Length < b.Length ? a.Length : b.Length;
        for (int i = 0; i < n; i++)
            if (a[i] != b[i])
                return a[i] - b[i];
        return a.Length - b.Length;
    }

    // ---- parsing ---------------------------------------------------------
    // Invariant-culture number parsing. Stricter than .NET's defaults: no
    // surrounding whitespace (Trim() first) and no thousands separators. Each
    // returns false on malformed input or overflow, and sets value to 0.

    /// <summary>An optional sign followed by decimal digits.</summary>
    public static bool TryParse(ReadOnlySpan<char> s, out long value)
    {
        value = 0;
        bool negative = s.Length > 0 && s[0] == '-';
        int start = s.Length > 0 && (s[0] == '-' || s[0] == '+') ? 1 : 0;
        if (!TryParseDigits(s.Slice(start), negative ? 9223372036854775808UL : 9223372036854775807UL, out ulong magnitude))
            return false;
        value = negative ? (long)(0 - magnitude) : (long)magnitude;
        return true;
    }

    public static bool TryParse(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        if (!TryParse(s, out long wide) || wide < -2147483648L || wide > 2147483647L)
            return false;
        value = (int)wide;
        return true;
    }

    /// <summary>An optional '+' followed by decimal digits.</summary>
    public static bool TryParse(ReadOnlySpan<char> s, out ulong value)
    {
        int start = s.Length > 0 && s[0] == '+' ? 1 : 0;
        return TryParseDigits(s.Slice(start), 18446744073709551615UL, out value);
    }

    /// <summary>Hex digits (either case) with an optional 0x prefix, up to 64 bits.</summary>
    public static bool TryParseHex(ReadOnlySpan<char> s, out ulong value)
    {
        value = 0;
        if (s.Length >= 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
            s = s.Slice(2);
        if (s.Length == 0)
            return false;

        ulong result = 0;
        int significant = 0;
        for (int i = 0; i < s.Length; i++)
        {
            int digit = HexDigit(s[i]);
            if (digit < 0)
                return false;
            if (result != 0 || digit != 0)
                significant++;
            if (significant > 16)
                return false;
            result = (result << 4) | (uint)digit;
        }
        value = result;
        return true;
    }

    /// <summary>
    /// [sign] digits [. digits] [e|E [sign] digits], or NaN / Infinity / -Infinity (what
    /// Fmt prints; case-insensitive, "inf" too). ".5" and "5." are accepted, like .NET.
    /// Too large gives +-Infinity and too small gives 0, also like .NET.
    /// The result is correctly rounded (denormals included), except for rare inputs
    /// within about 2^-100 of a halfway point between two doubles. Digits past the
    /// 19th significant one only affect such halfway cases.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> s, out double value)
    {
        value = 0;
        int i = 0;
        bool negative = false;
        if (s.Length > 0 && (s[0] == '-' || s[0] == '+'))
        {
            negative = s[0] == '-';
            i = 1;
        }

        ReadOnlySpan<char> word = s.Slice(i);
        if (EqualsIgnoreCase(word, "infinity") || EqualsIgnoreCase(word, "inf"))
        {
            value = negative ? -Infinity : Infinity;
            return true;
        }
        if (EqualsIgnoreCase(word, "nan"))
        {
            value = NaN;
            return true;
        }

        ulong mantissa = 0;
        int kept = 0;
        int exponent = 0;
        bool anyDigits = false;
        for (; i < s.Length && IsDigit(s[i]); i++)
        {
            anyDigits = true;
            AddDigit(s[i] - '0', false, ref mantissa, ref kept, ref exponent);
        }
        if (i < s.Length && s[i] == '.')
        {
            for (i++; i < s.Length && IsDigit(s[i]); i++)
            {
                anyDigits = true;
                AddDigit(s[i] - '0', true, ref mantissa, ref kept, ref exponent);
            }
        }
        if (!anyDigits)
            return false;

        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            bool negativeExponent = false;
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            {
                negativeExponent = s[i] == '-';
                i++;
            }
            int digitsStart = i;
            int e = 0;
            for (; i < s.Length && IsDigit(s[i]); i++)
            {
                if (e < 100000) // anything past this is 0 or Infinity anyway
                    e = e * 10 + (s[i] - '0');
            }
            if (i == digitsStart)
                return false;
            exponent += negativeExponent ? -e : e;
        }
        if (i != s.Length)
            return false;

        double magnitude = Scale(mantissa, exponent);
        value = negative ? -magnitude : magnitude;
        return true;
    }

    const double Infinity = 1.0 / 0.0; // the core library's double has no PositiveInfinity or NaN
    const double NaN = 0.0 / 0.0;

    static bool TryParseDigits(ReadOnlySpan<char> digits, ulong limit, out ulong value)
    {
        value = 0;
        if (digits.Length == 0)
            return false;

        ulong result = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            uint digit = (uint)(digits[i] - '0');
            if (digit > 9 || result > (limit - digit) / 10)
                return false;
            result = result * 10 + digit;
        }
        value = result;
        return true;
    }

    static bool IsDigit(char c) => c >= '0' && c <= '9';

    static int HexDigit(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    static bool EqualsIgnoreCase(ReadOnlySpan<char> text, ReadOnlySpan<char> lowercase)
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

    // Keeps the first 19 significant digits (they fit in a ulong); later digits only
    // move the decimal exponent.
    static void AddDigit(int digit, bool fraction, ref ulong mantissa, ref int kept, ref int exponent)
    {
        if (mantissa == 0 && digit == 0)
        {
            if (fraction)
                exponent--; // leading zero after the point
            return;
        }
        if (kept < 19)
        {
            mantissa = mantissa * 10 + (ulong)digit;
            kept++;
            if (fraction)
                exponent--;
        }
        else if (!fraction)
        {
            exponent++; // dropped an integer digit
        }
    }

    // mantissa * 10^exponent.
    // Fast path: with mantissa <= 2^53 and |exponent| <= 22 both operands are exact,
    // so the single multiply or divide is correctly rounded.
    // Otherwise 10^exponent = 5^exponent * 2^exponent, with 5^exponent computed in
    // double-double arithmetic (about 106 bits) and the power of two applied exactly.
    // The result is correctly rounded except for inputs within about 2^-100 of a
    // halfway point; Finish keeps denormals to a single rounding.
    static double Scale(ulong mantissa, int exponent)
    {
        if (mantissa == 0)
            return 0;
        if (exponent > 400)
            return Infinity;
        if (exponent < -400)
            return 0;
        if (mantissa <= (1UL << 53) && exponent >= -22 && exponent <= 22)
        {
            double m = mantissa;
            return exponent >= 0 ? m * Pow10(exponent) : m / Pow10(-exponent);
        }

        DoubleDouble five = Pow5(exponent < 0 ? -exponent : exponent, out int binary);
        DoubleDouble value = DoubleDouble.FromULong(mantissa);
        if (exponent >= 0)
            return Finish(DoubleDouble.Mul(value, five), binary + exponent);
        return Finish(DoubleDouble.Div(value, five), exponent - binary);
    }

    const double MinNormal = 2.2250738585072014e-308;

    // value * 2^n, rounded once. In the normal range rounding hi + lo to a double
    // and scaling by 2^n is exact. Below it, that would round twice (to 53 bits,
    // then to the denormal grid), so round the double-double straight to a
    // multiple of 2^-1074 instead.
    static double Finish(DoubleDouble value, int n)
    {
        double x = LdExp(value.ToDouble(), n);
        if (x > MinNormal)
            return x;

        int shift = n + 1074; // value * 2^shift = the result in units of 2^-1074
        double hi = LdExp(value.Hi, shift); // exact unless the result rounds to 0 anyway
        if (hi < 0.25)
            return 0;                        // hi + lo < 0.5 units
        double lo = LdExp(value.Lo, shift);
        double whole = (long)hi;            // floor; hi < 2^53
        double fraction = (hi - whole) + lo;
        if (fraction >= 1)
        {
            whole += 1;
            fraction -= 1;
        }
        else if (fraction < 0)
        {
            whole -= 1;
            fraction += 1;
        }
        if (fraction > 0.5 || (fraction == 0.5 && ((long)whole & 1) != 0))
            whole += 1;
        return whole * TwoPow(-1022) * TwoPow(-52); // exact: whole <= 2^52
    }

    // 5^n as a double-double in [1, 2), times 2^binary.
    static DoubleDouble Pow5(int n, out int binary)
    {
        DoubleDouble result = new DoubleDouble(1, 0);
        DoubleDouble power = new DoubleDouble(1.25, 0); // 5 = 1.25 * 2^2
        int resultBinary = 0;
        int powerBinary = 2;
        while (n > 0)
        {
            if ((n & 1) != 0)
            {
                result = DoubleDouble.Mul(result, power);
                resultBinary += powerBinary;
                result.Normalize(ref resultBinary);
            }
            n >>= 1;
            if (n > 0)
            {
                power = DoubleDouble.Mul(power, power);
                powerBinary *= 2;
                power.Normalize(ref powerBinary);
            }
        }
        binary = resultBinary;
        return result;
    }

    // x * 2^n, exact unless the result is denormal.
    static double LdExp(double x, int n)
    {
        while (n > 1023)
        {
            x *= TwoPow(1023);
            n -= 1023;
        }
        while (n < -1022)
        {
            x *= TwoPow(-1022);
            n += 1022;
        }
        return x * TwoPow(n);
    }

    // 2^k for -1022 <= k <= 1023, built from its bit pattern.
    static double TwoPow(int k)
    {
        long bits = (long)(k + 1023) << 52;
        unsafe { return *(double*)&bits; } // SAFETY: bits is a local of the same size as double.
    }

    // A value hi + lo with |lo| <= ulp(hi) / 2: about 106 bits of precision.
    struct DoubleDouble
    {
        public double Hi;
        public double Lo;

        public DoubleDouble(double hi, double lo)
        {
            Hi = hi;
            Lo = lo;
        }

        public double ToDouble() => Hi + Lo;

        public static DoubleDouble FromULong(ulong value)
        {
            double hi = value;                         // rounded to 53 bits
            long rest = (long)(value - (ulong)hi);     // the rounding error, exact
            return QuickTwoSum(hi, rest);
        }

        // Keeps Hi in [1, 2) by moving powers of two into binary (exact).
        public void Normalize(ref int binary)
        {
            while (Hi >= 2)
            {
                Hi *= 0.5;
                Lo *= 0.5;
                binary++;
            }
        }

        public static DoubleDouble Mul(DoubleDouble a, DoubleDouble b)
        {
            double p = a.Hi * b.Hi;
            double e = TwoProductError(a.Hi, b.Hi, p) + (a.Hi * b.Lo + a.Lo * b.Hi);
            return QuickTwoSum(p, e);
        }

        public static DoubleDouble Div(DoubleDouble a, DoubleDouble b)
        {
            double q1 = a.Hi / b.Hi;
            DoubleDouble r = Sub(a, MulDouble(b, q1));
            double q2 = r.Hi / b.Hi;
            r = Sub(r, MulDouble(b, q2));
            double q3 = r.Hi / b.Hi;
            DoubleDouble q = QuickTwoSum(q1, q2);
            return QuickTwoSum(q.Hi, q.Lo + q3);
        }

        static DoubleDouble MulDouble(DoubleDouble a, double b)
        {
            double p = a.Hi * b;
            return QuickTwoSum(p, TwoProductError(a.Hi, b, p) + a.Lo * b);
        }

        static DoubleDouble Sub(DoubleDouble a, DoubleDouble b)
        {
            double s = a.Hi - b.Hi;
            double v = s - a.Hi;
            double e = (a.Hi - (s - v)) + (-b.Hi - v);
            return QuickTwoSum(s, e + (a.Lo - b.Lo));
        }

        // a + b as hi + lo; needs |a| >= |b|.
        static DoubleDouble QuickTwoSum(double a, double b)
        {
            double s = a + b;
            return new DoubleDouble(s, b - (s - a));
        }

        // The exact error of p = a * b (Dekker), without FMA.
        static double TwoProductError(double a, double b, double p)
        {
            Split(a, out double aHi, out double aLo);
            Split(b, out double bHi, out double bLo);
            return ((aHi * bHi - p) + aHi * bLo + aLo * bHi) + aLo * bLo;
        }

        // Veltkamp split into two 26-bit halves.
        static void Split(double a, out double hi, out double lo)
        {
            double t = 134217729.0 * a; // 2^27 + 1
            hi = t - (t - a);
            lo = a - hi;
        }
    }

    // Exact for n <= 22: every intermediate power and product is a representable integer.
    static double Pow10(int n)
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

    // ---- arena strings ---------------------------------------------------

    /// <summary>
    /// $"..." into a new string in <paramref name="arena"/>. Returns null if the
    /// arena isn't created or is out of memory.
    /// </summary>
    /// <safety>The string is freed with the rest of the arena (Restore, Reset, Destroy).</safety>
    public static string Format(Arena arena, [InterpolatedStringHandlerArgument("arena")] ref StrBuilder.InterpolatedHandler handler)
        => handler.TakeString();

    /// <summary>Copies <paramref name="text"/> into a new string in <paramref name="arena"/>, or returns null.</summary>
    public static string ToArenaString(Arena arena, ReadOnlySpan<char> text) => Concat(arena, text, default);

    public static string Concat(Arena arena, ReadOnlySpan<char> a, ReadOnlySpan<char> b) => Concat(arena, a, b, default);

    /// <summary>The replacement for a + b + c. Returns null if the arena is out of memory.</summary>
    public static string Concat(Arena arena, ReadOnlySpan<char> a, ReadOnlySpan<char> b, ReadOnlySpan<char> c)
    {
        long length = (long)a.Length + b.Length + c.Length;
        if (length > int.MaxValue / 2)
            return null;

        byte* obj = AllocStringObject(arena, (int)length);
        if (obj == null)
            return null;

        Span<char> chars;
        unsafe { chars = new Span<char>(obj + RuntimeHelpers.OffsetToStringData, (int)length); } // SAFETY: AllocStringObject made room for length chars.
        a.CopyTo(chars);
        b.CopyTo(chars.Slice(a.Length));
        c.CopyTo(chars.Slice(a.Length + b.Length));
        unsafe { return MakeString(obj, (int)length); } // SAFETY: obj has room for length chars.
    }

    // A zero-mode string object is [MethodTable*][int length][chars...]: the core
    // library allocates objects with plain calloc, no header in front, and the layout is
    // fixed by the compiler (RuntimeHelpers.OffsetToStringData). So a string
    // laid out in arena memory is a real string.

    static nint s_stringMethodTable;

    internal static nuint StringObjectSize(int length)
        => (nuint)RuntimeHelpers.OffsetToStringData + (nuint)(length + 1) * sizeof(char);

    /// <summary>Room for a string object of <paramref name="length"/> chars plus a terminator, or null.</summary>
    internal static byte* AllocStringObject(Arena arena, int length)
        => (byte*)arena.Alloc(StringObjectSize(length), (nuint)sizeof(nint), zero: false);

    /// <summary>Writes the object header and terminator around chars already in place.</summary>
    /// <safety>obj must come from AllocStringObject with capacity for at least length chars.</safety>
    internal static unsafe string MakeString(byte* obj, int length)
    {
        if (s_stringMethodTable == 0)
        {
            unsafe
            {
                // SAFETY: a literal is a real string object; its method table pointer
                // sits OffsetToStringData bytes before its first char.
                fixed (char* p = "x")
                    s_stringMethodTable = *(nint*)((byte*)p - RuntimeHelpers.OffsetToStringData);
            }
        }

        unsafe
        {
            // SAFETY: the caller guarantees obj has room for the header, length chars
            // and a terminator.
            *(nint*)obj = s_stringMethodTable;
            *(int*)(obj + sizeof(nint)) = length;
            ((char*)(obj + RuntimeHelpers.OffsetToStringData))[length] = '\0';
        }

        nint address = (nint)obj;
        return Unsafe.As<nint, string>(ref address);
    }
}

/// <summary>
/// Number formatting shared by Out and StrBuilder, into a caller's buffer.
/// An integer without a format takes the small path here (Int, UInt), which
/// prints what .NET prints in fewer bytes of code than the general one, so a
/// program that only prints whole numbers stays small on a C64. Everything
/// else, {x:X4} and every double, goes through the value's TryFormat: .NET's
/// format strings, from zerocore (System/Number.Formatting.cs).
/// </summary>
static class Fmt
{
    /// <summary>Enough for any value without a format, and most with one.</summary>
    public const int MaxChars = 64;

    public static int UInt(ulong value, Span<char> dest)
    {
        Span<char> digits = stackalloc char[20];
        int n = 0;
        do
        {
            digits[n++] = (char)('0' + (int)(value % 10));
            value /= 10;
        } while (value != 0);

        for (int i = 0; i < n; i++)
            dest[i] = digits[n - 1 - i];
        return n;
    }

    public static int Int(long value, Span<char> dest)
    {
        if (value >= 0)
            return UInt((ulong)value, dest);
        // Negate as ulong so long.MinValue works too.
        dest[0] = '-';
        return 1 + UInt(0 - (ulong)value, dest.Slice(1));
    }

    /// <summary>
    /// value.TryFormat with the format ("X4", "F2", ... or null), or -1 if the
    /// text needs more than dest. A format .NET doesn't know, such as a
    /// custom "0.00", throws FormatException.
    /// </summary>
    public static int Format<T>(T value, string format, Span<char> dest) where T : ISpanFormattable
        => value.TryFormat(dest, out int written, format.AsSpan(), null) ? written : -1;

    public static ReadOnlySpan<char> Bool(bool value) => value ? "True" : "False";
}
