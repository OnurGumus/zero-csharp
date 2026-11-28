namespace System.Globalization
{
    // Invariant casing and ordinal-ignore-case comparison, as .NET does them
    // in invariant mode (InvariantModeCasing and Ordinal): one code point to
    // one, by the simple Unicode mapping, surrogate pairs included and lone
    // surrogates left alone. Ignoring case compares upper cases.
    internal static unsafe class TextCasing
    {
        // The code point at text[i], and how many chars it takes.
        private static int CodePointAt(ReadOnlySpan<char> text, int i, out int length)
        {
            char c = text[i];
            if (c >= 0xD800 && c <= 0xDBFF && i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (next >= 0xDC00 && next <= 0xDFFF)
                {
                    length = 2;
                    return ((c - 0xD800) << 10) + (next - 0xDC00) + 0x10000;
                }
            }
            length = 1;
            return c;
        }

        internal static int Change(int codePoint, bool upper)
            => upper ? UnicodeData.ToUpper(codePoint) : UnicodeData.ToLower(codePoint);

        // Casing modes: the invariant culture's; a named culture's, which (as
        // .NET's ICU casing) also maps İ down to i and ı up to I; and Turkish
        // and Azerbaijani, which also map i up to İ and I down to ı.
        internal const int Invariant = 0, Culture = 1, Turkish = 2;

        // The current culture's casing mode, for char.ToUpper(c) and
        // string.ToUpper(): set (by CultureInfo's CurrentCulture setters) only
        // once a culture is made current, so programs that never do that
        // don't link CultureInfo for them.
        internal static delegate*<int> s_currentMode;

        internal static int CurrentMode => s_currentMode == null ? Invariant : s_currentMode();

        internal static int Change(int codePoint, bool upper, int mode)
        {
            if (mode != Invariant)
            {
                if (upper)
                {
                    if (codePoint == 'i' && mode == Turkish)
                        return 0x0130;
                    if (codePoint == 0x0131)
                        return 'I';
                }
                else
                {
                    if (codePoint == 'I' && mode == Turkish)
                        return 0x0131;
                    if (codePoint == 0x0130)
                        return 'i';
                }
            }
            return Change(codePoint, upper);
        }

        // The first index whose casing changes, or -1.
        private static int FirstChange(ReadOnlySpan<char> source, bool upper, int mode)
        {
            for (int i = 0; i < source.Length; )
            {
                int cp = CodePointAt(source, i, out int length);
                if (Change(cp, upper, mode) != cp)
                    return i;
                i += length;
            }
            return -1;
        }

        // Writes source's upper or lower case into destination, which must be
        // at least as long (casing never changes the length).
        internal static void Change(ReadOnlySpan<char> source, Span<char> destination, bool upper, int mode = Invariant)
        {
            for (int i = 0; i < source.Length; )
            {
                int cp = Change(CodePointAt(source, i, out int length), upper, mode);
                if (length == 1)
                {
                    destination[i] = (char)cp;
                }
                else
                {
                    cp -= 0x10000;
                    destination[i] = (char)(0xD800 + (cp >> 10));
                    destination[i + 1] = (char)(0xDC00 + (cp & 0x3FF));
                }
                i += length;
            }
        }

        // The string's upper or lower case: the same string when nothing
        // changes, as in .NET.
        internal static string Change(string text, bool upper, int mode = Invariant)
        {
            ReadOnlySpan<char> source = Number.AsSpan(text);
            if (FirstChange(source, upper, mode) < 0)
                return text;
            if (source.Length <= 256)
            {
                // A fixed size: a variable stackalloc needs __alloca, which DOS lacks.
                char* buffer = stackalloc char[256];
                Change(source, new Span<char>(buffer, source.Length), upper, mode);
                return Number.FromChars(buffer, source.Length);
            }
            char[] chars = new char[source.Length];
            fixed (char* p = chars)
            {
                Change(source, new Span<char>(p, source.Length), upper, mode);
                return Number.FromChars(p, source.Length);
            }
        }

        // Ordinal comparison: the difference of the first chars that differ,
        // else of the lengths.
        internal static int CompareOrdinal(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        {
            int length = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < length; i++)
            {
                if (a[i] != b[i])
                    return a[i] - b[i];
            }
            return a.Length - b.Length;
        }

        // Ignoring case: the difference of the first upper cases that differ
        // (code points, for surrogate pairs), else of the lengths.
        internal static int CompareIgnoreCase(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                int x = CodePointAt(a, i, out int lengthA);
                int y = CodePointAt(b, j, out int lengthB);
                if (x != y)
                {
                    x = UnicodeData.ToUpper(x);
                    y = UnicodeData.ToUpper(y);
                    if (x != y)
                        return x - y;
                }
                i += lengthA;
                j += lengthB;
            }
            return (a.Length - i) - (b.Length - j);
        }

        internal static bool EqualsIgnoreCase(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
            => a.Length == b.Length && CompareIgnoreCase(a, b) == 0;

        // Whether value starts at source[start], ignoring case or not.
        private static bool MatchAt(ReadOnlySpan<char> source, int start, ReadOnlySpan<char> value, bool ignoreCase)
        {
            if (!ignoreCase)
            {
                for (int k = 0; k < value.Length; k++)
                {
                    if (source[start + k] != value[k])
                        return false;
                }
                return true;
            }
            return EqualsIgnoreCase(source.Slice(start, value.Length), value);
        }

        internal static int IndexOf(ReadOnlySpan<char> source, ReadOnlySpan<char> value, bool ignoreCase)
        {
            for (int i = 0; i + value.Length <= source.Length; i++)
            {
                if (MatchAt(source, i, value, ignoreCase))
                    return i;
            }
            return -1;
        }

        internal static int LastIndexOf(ReadOnlySpan<char> source, ReadOnlySpan<char> value, bool ignoreCase)
        {
            for (int i = source.Length - value.Length; i >= 0; i--)
            {
                if (MatchAt(source, i, value, ignoreCase))
                    return i;
            }
            return -1;
        }

        internal static bool StartsWith(ReadOnlySpan<char> source, ReadOnlySpan<char> value, bool ignoreCase)
            => value.Length <= source.Length && MatchAt(source, 0, value, ignoreCase);

        internal static bool EndsWith(ReadOnlySpan<char> source, ReadOnlySpan<char> value, bool ignoreCase)
            => value.Length <= source.Length && MatchAt(source, source.Length - value.Length, value, ignoreCase);

        // A hash that equal strings share (ignoring case: equal upper cases).
        internal static int GetHashCode(ReadOnlySpan<char> text, bool ignoreCase)
        {
            uint hash = 2166136261;
            for (int i = 0; i < text.Length; )
            {
                int cp = CodePointAt(text, i, out int length);
                if (ignoreCase)
                    cp = UnicodeData.ToUpper(cp);
                hash = (hash ^ (uint)cp) * 16777619;
                i += length;
            }
            return (int)hash;
        }
    }
}
