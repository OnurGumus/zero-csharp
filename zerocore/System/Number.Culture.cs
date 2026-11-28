using System.Globalization;

namespace System
{
    // Numbers written and read with a NumberFormatInfo of your own: its signs,
    // separators, group sizes, symbols and currency and percent patterns, as
    // .NET's NumberToString and TryParseNumber use them (after dotnet/runtime's
    // Number.Formatting.cs and Number.Parsing.cs, MIT).
    //
    // The invariant culture's formatting (Number.Formatting.cs and
    // Number.Parsing.cs) doesn't read a NumberFormatInfo at all. It reaches
    // this only through Number.s_culture, which a writable NumberFormatInfo's
    // constructor sets, so a program that never makes one doesn't carry this.
    internal static unsafe partial class Number
    {
        internal static NumberCulture s_culture;

        // Hands the text to the NumberFormatInfo's formatting (not inlined, so
        // Emit stays small).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void CultureEmit(ref Digits n, char format, int precision, int maxDigits, bool noScientific, ref Sink s)
            => s_culture.Emit(ref n, format, precision, maxDigits, noScientific, ref s);

        internal static void UseCultures()
        {
            if (s_culture == null)
                using (System.Runtime.ObjectAllocator.Lasting())
                    s_culture = new CultureNumbers();
        }

        // The symbols for a provider: null for the invariant culture's. Not
        // inlined, so each call site stays one call.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static NumberFormatInfo InfoFor(IFormatProvider provider) => s_culture == null ? null : s_culture.InfoFor(provider);

        // The symbols for a provider, and the precision a format without one
        // takes with them ("N" is NumberDecimalDigits digits).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static NumberFormatInfo InfoFor(IFormatProvider provider, char letter, ref int precision)
        {
            if (s_culture == null)
                return null;
            NumberFormatInfo info = s_culture.InfoFor(provider);
            if (info != null)
                precision = s_culture.Precision(letter, precision, info);
            return info;
        }

        internal abstract class NumberCulture
        {
            internal abstract NumberFormatInfo InfoFor(IFormatProvider provider);
            // An integer's text by the current culture's symbols, or null when
            // they are the invariant ones.
            internal abstract string CurrentText(ulong value, bool signed);
            internal abstract int Precision(char format, int precision, NumberFormatInfo info);
            internal abstract void Emit(ref Digits n, char format, int precision, int maxDigits, bool noScientific, ref Sink s);
            internal abstract void NonFinite(bool nan, bool negative, ref Sink s);
            internal abstract void Negative(ref Sink s);
            internal abstract bool Scan(ReadOnlySpan<char> s, NumberStyles style, ref NumberText n, NumberFormatInfo info);
            internal abstract bool ParseSymbol(ReadOnlySpan<char> s, FloatFormat format, NumberFormatInfo info, out ulong bits);
        }

        // A negative sign: '-', or the NumberFormatInfo's (through the hook,
        // so this stays a few bytes on a 6502).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void AddNegative(ref Sink s)
        {
            if (s.Info == null)
                s.Add('-');
            else
                s_culture.Negative(ref s);
        }

        private sealed class CultureNumbers : NumberCulture
        {
            internal override string CurrentText(ulong value, bool signed)
                => InfoFor(null) == null ? null : IntegerToString(value, signed, 64, null, null);

            internal override NumberFormatInfo InfoFor(IFormatProvider provider)
            {
                NumberFormatInfo info = NumberFormatInfo.GetInstance(provider);
                return ReferenceEquals(info, NumberFormatInfo.InvariantInfo) ? null : info;
            }

            // The digits "F", "N", "C" and "P" keep when no precision is given.
            internal override int Precision(char format, int precision, NumberFormatInfo info)
            {
                if (precision >= 0)
                    return precision;
                return Upper(format) switch
                {
                    'F' or 'N' => info._numberDecimalDigits,
                    'C' => info._currencyDecimalDigits,
                    'P' => info._percentDecimalDigits,
                    _ => precision,
                };
            }

            private static readonly string[] PositiveCurrency = { "$#", "#$", "$ #", "# $" };
            private static readonly string[] NegativeCurrency =
            {
                "($#)", "-$#", "$-#", "$#-", "(#$)", "-#$", "#-$", "#$-", "-# $", "-$ #", "# $-", "$ #-", "$ -#", "#- $", "($ #)", "(# $)", "$- #",
            };
            private static readonly string[] PositivePercent = { "# %", "#%", "%#", "% #" };
            private static readonly string[] NegativePercent =
            {
                "-# %", "-#%", "-%#", "%-#", "%#-", "#-%", "#%-", "-% #", "# %-", "% #-", "% -#", "#- %",
            };
            private static readonly string[] NegativeNumber = { "(#)", "-#", "- #", "#-", "# -" };

            internal override void Emit(ref Digits n, char format, int precision, int maxDigits, bool noScientific, ref Sink s)
            {
                NumberFormatInfo info = s.Info;
                char upper = Upper(format);
                switch (upper)
                {
                    case 'F':
                        if (n.Negative)
                            s.Add(info._negativeSign);
                        Fixed(ref n, precision, null, info._numberDecimalSeparator, null, ref s);
                        return;
                    case 'N':
                        Pattern(ref n, n.Negative ? NegativeNumber[info._numberNegativePattern] : "#", precision,
                            info._numberGroupSizes, info._numberDecimalSeparator, info._numberGroupSeparator, null, ref s);
                        return;
                    case 'C':
                        Pattern(ref n, n.Negative ? NegativeCurrency[info._currencyNegativePattern] : PositiveCurrency[info._currencyPositivePattern],
                            precision, info._currencyGroupSizes, info._currencyDecimalSeparator, info._currencyGroupSeparator, info._currencySymbol, ref s);
                        return;
                    case 'P':
                        if (n.Count > 0)
                            n.Scale += 2;
                        Pattern(ref n, n.Negative ? NegativePercent[info._percentNegativePattern] : PositivePercent[info._percentPositivePattern],
                            precision, info._percentGroupSizes, info._percentDecimalSeparator, info._percentGroupSeparator, info._percentSymbol, ref s);
                        return;
                    case 'E':
                        if (n.Negative)
                            s.Add(info._negativeSign);
                        Scientific(ref n, precision < 0 ? 6 : precision, format == 'e' ? 'e' : 'E', info, ref s);
                        return;
                    default:
                        if (n.Negative)
                            s.Add(info._negativeSign);
                        General(ref n, precision > 0 ? precision : maxDigits, format == 'g' ? 'e' : 'E', noScientific, info, ref s);
                        return;
                }
            }

            // A pattern: '#' the number, '-' the negative sign, '$' or '%'
            // the symbol, anything else itself.
            private static void Pattern(ref Digits n, string pattern, int decimals, int[] groups, string point, string separator, string symbol, ref Sink s)
            {
                for (int i = 0; i < pattern.Length; i++)
                {
                    char c = pattern[i];
                    if (c == '#')
                        Fixed(ref n, decimals, groups, point, separator, ref s);
                    else if (c == '-')
                        s.Add(s.Info._negativeSign);
                    else if (c == '$' || c == '%')
                        s.Add(symbol);
                    else
                        s.Add(c);
                }
            }

            // The whole part, grouped from the right by `groups` (the last
            // size repeats; a 0 stops grouping), then `decimals` digits.
            private static void Fixed(ref Digits n, int decimals, int[] groups, string point, string separator, ref Sink s)
            {
                int whole = n.Scale;
                int i = 0;
                if (whole > 0)
                {
                    for (int position = whole; position > 0; position--)
                    {
                        s.Add(DigitAt(ref n, i++));
                        if (groups != null && IsBoundary(groups, position - 1))
                            s.Add(separator);
                    }
                }
                else
                {
                    s.Add('0');
                }
                if (decimals > 0)
                {
                    s.Add(point);
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

            // Whether a separator goes after a digit with `right` digits after
            // it in the whole part: group boundaries, counted from the right,
            // are the running sums of the sizes, the last size repeating, and
            // a size of 0 ends the grouping.
            private static bool IsBoundary(int[] groups, int right)
            {
                if (right <= 0 || groups.Length == 0)
                    return false;
                int boundary = 0;
                for (int k = 0; ; k++)
                {
                    int size = groups[k < groups.Length ? k : groups.Length - 1];
                    if (size == 0)
                        return false;
                    boundary += size;
                    if (boundary >= right)
                        return boundary == right;
                }
            }

            private static void Scientific(ref Digits n, int decimals, char e, NumberFormatInfo info, ref Sink s)
            {
                s.Add(DigitAt(ref n, 0));
                if (decimals > 0)
                {
                    s.Add(info._numberDecimalSeparator);
                    int i = 1;
                    while (decimals > 0 && i < n.Count)
                    {
                        s.Add(DigitAt(ref n, i++));
                        decimals--;
                    }
                    s.Add('0', decimals);
                }
                Exponent(n.Count == 0 ? 0 : n.Scale - 1, e, 3, info, ref s);
            }

            private static void General(ref Digits n, int maxDigits, char e, bool noScientific, NumberFormatInfo info, ref Sink s)
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
                    s.Add(info._numberDecimalSeparator);
                    s.Add('0', -position);
                    while (i < n.Count)
                        s.Add(DigitAt(ref n, i++));
                }
                if (scientific)
                    Exponent(n.Scale - 1, e, 2, info, ref s);
            }

            private static void Exponent(int exponent, char e, int minDigits, NumberFormatInfo info, ref Sink s)
            {
                s.Add(e);
                if (exponent < 0)
                    s.Add(info._negativeSign);
                else
                    s.Add(info._positiveSign);
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

            internal override void Negative(ref Sink s) => s.Add(s.Info._negativeSign);

            internal override void NonFinite(bool nan, bool negative, ref Sink s)
            {
                NumberFormatInfo info = s.Info;
                s.Add(nan ? info._nanSymbol : negative ? info._negativeInfinitySymbol : info._positiveInfinitySymbol);
            }

            // ---- parsing ----

            // Where `value` ends if s has it at p, else -1. A no-break space
            // (U+00A0 or U+202F) in value also matches a space, as in .NET.
            private static int Match(ReadOnlySpan<char> s, int p, string value)
            {
                if (value.Length == 0)
                    return -1;
                for (int i = 0; i < value.Length; i++, p++)
                {
                    char c = At(s, p);
                    char v = value[i];
                    if (c != v && !((v == '\u00A0' || v == '\u202F') && c == ' '))
                        return -1;
                }
                return p;
            }

            private static int MatchNegative(ReadOnlySpan<char> s, int p, NumberFormatInfo info)
            {
                int end = Match(s, p, info._negativeSign);
                if (end < 0 && info.AllowHyphenDuringParsing && At(s, p) == '-')
                    end = p + 1;
                return end;
            }

            internal override bool Scan(ReadOnlySpan<char> s, NumberStyles style, ref NumberText n, NumberFormatInfo info)
            {
                string point, separator, symbol = null;
                bool currency = false;
                if ((style & NumberStyles.AllowCurrencySymbol) != 0)
                {
                    symbol = info._currencySymbol;
                    point = info._currencyDecimalSeparator;
                    separator = info._currencyGroupSeparator;
                    currency = true;
                }
                else
                {
                    point = info._numberDecimalSeparator;
                    separator = info._numberGroupSeparator;
                }

                // Whitespace after a sign is allowed when the negative pattern
                // is "- n", except for integers with only the Integer styles,
                // which .NET reads by a faster path that never allows it.
                bool fast = n.Integer && (style & ~NumberStyles.Integer) == 0;
                bool spaceAfterSign = info._numberNegativePattern == 2 && !fast;
                int state = 0;
                int p = 0;
                char ch = At(s, 0);
                int next;
                while (true)
                {
                    if (!IsWhite(ch) || (style & NumberStyles.AllowLeadingWhite) == 0
                        || ((state & StateSign) != 0 && (state & StateCurrency) == 0 && !spaceAfterSign))
                    {
                        // The fast path tries a hyphen for a dash-like negative sign
                        // before the positive sign.
                        if (fast && (style & NumberStyles.AllowLeadingSign) != 0 && (state & StateSign) == 0
                            && ch == '-' && info.AllowHyphenDuringParsing)
                        {
                            state |= StateSign;
                            n.Negative = true;
                        }
                        else if ((style & NumberStyles.AllowLeadingSign) != 0 && (state & StateSign) == 0
                            && ((next = Match(s, p, info._positiveSign)) >= 0 || ((next = MatchNegative(s, p, info)) >= 0 && (n.Negative = true))))
                        {
                            state |= StateSign;
                            p = next - 1;
                        }
                        else if (ch == '(' && (style & NumberStyles.AllowParentheses) != 0 && (state & StateSign) == 0)
                        {
                            state |= StateSign | StateParens;
                            n.Negative = true;
                        }
                        else if (symbol != null && (next = Match(s, p, symbol)) >= 0)
                        {
                            state |= StateCurrency;
                            symbol = null;
                            p = next - 1;
                        }
                        else
                        {
                            break;
                        }
                    }
                    ch = At(s, ++p);
                }

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
                            {
                                n.NonZeroTail = true;
                            }
                            if ((state & StateDecimal) == 0)
                                n.Scale++;
                            count++;
                            state |= StateNonZero;
                        }
                        else if ((state & StateDecimal) != 0)
                        {
                            n.Scale--;
                        }
                    }
                    else if ((style & NumberStyles.AllowDecimalPoint) != 0 && (state & StateDecimal) == 0
                        && ((next = Match(s, p, point)) >= 0 || (currency && (state & StateCurrency) == 0 && (next = Match(s, p, info._numberDecimalSeparator)) >= 0)))
                    {
                        state |= StateDecimal;
                        p = next - 1;
                    }
                    else if ((style & NumberStyles.AllowThousands) != 0 && (state & StateDigits) != 0 && (state & StateDecimal) == 0
                        && ((next = Match(s, p, separator)) >= 0 || (currency && (state & StateCurrency) == 0 && (next = Match(s, p, info._numberGroupSeparator)) >= 0)))
                    {
                        p = next - 1;
                    }
                    else
                    {
                        break;
                    }
                    ch = At(s, ++p);
                }
                n.Count = end;
                if ((state & StateDigits) == 0)
                    return false;

                if ((ch == 'E' || ch == 'e') && (style & NumberStyles.AllowExponent) != 0)
                {
                    int before = p;
                    ch = At(s, ++p);
                    bool negative = false;
                    if ((next = Match(s, p, info._positiveSign)) >= 0)
                    {
                        p = next;
                        ch = At(s, p);
                    }
                    else if ((next = MatchNegative(s, p, info)) >= 0)
                    {
                        p = next;
                        ch = At(s, p);
                        negative = true;
                    }
                    if (IsDigit(ch))
                    {
                        int exponent = 0;
                        do
                        {
                            if (exponent >= 100_000_000)
                            {
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

                while (true)
                {
                    if (!IsWhite(ch) || (style & NumberStyles.AllowTrailingWhite) == 0)
                    {
                        if ((style & NumberStyles.AllowTrailingSign) != 0 && (state & StateSign) == 0
                            && ((next = Match(s, p, info._positiveSign)) >= 0 || ((next = MatchNegative(s, p, info)) >= 0 && (n.Negative = true))))
                        {
                            state |= StateSign;
                            p = next - 1;
                        }
                        else if (ch == ')' && (state & StateParens) != 0)
                        {
                            state &= ~StateParens;
                        }
                        else if (symbol != null && (next = Match(s, p, symbol)) >= 0)
                        {
                            symbol = null;
                            p = next - 1;
                        }
                        else
                        {
                            break;
                        }
                    }
                    ch = At(s, ++p);
                }
                if ((state & StateParens) != 0)
                    return false;
                if ((state & StateNonZero) == 0)
                {
                    n.Scale = 0;
                    if (n.Integer && (state & StateDecimal) == 0)
                        n.Negative = false;
                }
                return OnlyNulls(s, p);
            }

            // "Infinity", "-Infinity" and "NaN" by the NumberFormatInfo's
            // symbols, ignoring case, after trimming white space, with a sign
            // before them as .NET allows.
            internal override bool ParseSymbol(ReadOnlySpan<char> s, FloatFormat format, NumberFormatInfo info, out ulong bits)
            {
                bits = 0;
                int start = 0, stop = s.Length;
                while (start < stop && IsWhiteSpace(s[start]))
                    start++;
                while (stop > start && IsWhiteSpace(s[stop - 1]))
                    stop--;
                ReadOnlySpan<char> text = s.Slice(start, stop - start);
                ulong infinity = Infinity(format);
                ulong sign = 1UL << (format.Precision + format.ExponentBits - 1);
                if (Is(text, info._positiveInfinitySymbol))
                    bits = infinity;
                else if (Is(text, info._negativeInfinitySymbol))
                    bits = sign | infinity;
                else if (Is(text, info._nanSymbol))
                    bits = format.NaN;
                else if (StartsWith(text, info._positiveSign))
                {
                    text = text.Slice(info._positiveSign.Length);
                    if (Is(text, info._positiveInfinitySymbol))
                        bits = infinity;
                    else if (Is(text, info._nanSymbol))
                        bits = format.NaN;
                    else
                        return false;
                }
                else if (StartsWith(text, info._negativeSign) && Is(text.Slice(info._negativeSign.Length), info._nanSymbol))
                    bits = format.NaN;
                else
                    return false;
                return true;
            }

            private static bool Is(ReadOnlySpan<char> text, string value) => TextCasing.EqualsIgnoreCase(text, AsSpan(value));

            private static bool StartsWith(ReadOnlySpan<char> text, string value)
                => value.Length <= text.Length && TextCasing.EqualsIgnoreCase(text.Slice(0, value.Length), AsSpan(value));
        }
    }
}
