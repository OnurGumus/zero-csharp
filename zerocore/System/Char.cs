using System.Globalization;

namespace System
{
    // char's methods, answering as .NET does. The ASCII ones (IsAsciiDigit,
    // IsAsciiLetter, ...) are comparisons; IsLetter, IsDigit and the other
    // Unicode ones look the character up in CharUnicodeInfo's tables (about
    // 16 KB, linked only into programs that use them), as do ToUpper and
    // ToLower (1.6 KB) and GetNumericValue (3 KB).
    //
    // There are no cultures: ToUpper and ToLower are ToUpperInvariant and
    // ToLowerInvariant, one char to one char, as in .NET's invariant mode
    // ('ß' stays 'ß', 'ı' stays 'ı').
    //
    // The (string s, int index) overloads read a surrogate pair at index as
    // the character it encodes, as .NET does: char.IsLetter("𐐀", 0) is true.
    public unsafe struct Char : IEquatable<char>, IComparable<char>, IComparable
    {
        public const char MaxValue = (char)0xFFFF;
        public const char MinValue = (char)0;

        public bool Equals(char other) => (char)this == other;

        public int CompareTo(char other) => (char)this < other ? -1 : (char)this > other ? 1 : 0;

        public int CompareTo(object obj) => obj is char other ? CompareTo(other) : obj == null ? 1 : throw new ArgumentException("Object must be of type Char.");
        public override bool Equals(object obj) => obj is char other && (char)this == other;
        public override int GetHashCode() => (char)this;
        public override string ToString() => ToString((char)this);
        public string ToString(IFormatProvider provider) => ToString((char)this);

        public static string ToString(char c) => Number.FromChars(&c, 1);

        // The only char in a one-char string.
        public static char Parse(string s)
        {
            if (s == null || s.Length != 1)
                Environment.FailFast("char.Parse: the string must be exactly one character long");
            return s[0];
        }

        public static bool TryParse(string s, out char result)
        {
            if (s == null || s.Length != 1)
            {
                result = '\0';
                return false;
            }
            result = s[0];
            return true;
        }

        // ASCII

        public static bool IsAscii(char c) => c <= 0x7F;

        public static bool IsBetween(char c, char minInclusive, char maxInclusive)
            => (uint)(c - minInclusive) <= (uint)(maxInclusive - minInclusive);

        public static bool IsAsciiDigit(char c) => IsBetween(c, '0', '9');

        public static bool IsAsciiLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';

        public static bool IsAsciiLetterLower(char c) => IsBetween(c, 'a', 'z');

        public static bool IsAsciiLetterUpper(char c) => IsBetween(c, 'A', 'Z');

        public static bool IsAsciiLetterOrDigit(char c) => IsAsciiLetter(c) || IsAsciiDigit(c);

        public static bool IsAsciiHexDigit(char c) => IsAsciiDigit(c) || (uint)((c | 0x20) - 'a') <= 'f' - 'a';

        public static bool IsAsciiHexDigitLower(char c) => IsAsciiDigit(c) || IsBetween(c, 'a', 'f');

        public static bool IsAsciiHexDigitUpper(char c) => IsAsciiDigit(c) || IsBetween(c, 'A', 'F');

        // Unicode categories

        public static UnicodeCategory GetUnicodeCategory(char c) => UnicodeData.GetCategory(c);

        public static UnicodeCategory GetUnicodeCategory(string s, int index)
            => UnicodeData.GetCategory(UnicodeData.CodePointAt(s, index));

        public static double GetNumericValue(char c) => UnicodeData.GetNumericValue(c);

        public static double GetNumericValue(string s, int index)
            => UnicodeData.GetNumericValue(UnicodeData.CodePointAt(s, index));

        // A decimal digit in any script ('٣' too); IsAsciiDigit for 0-9 only.
        public static bool IsDigit(char c)
            => c < 0x80 ? IsAsciiDigit(c) : UnicodeData.GetCategory(c) == UnicodeCategory.DecimalDigitNumber;

        public static bool IsDigit(string s, int index) => At(s, index) == UnicodeCategory.DecimalDigitNumber;

        public static bool IsLetter(char c) => c < 0x80 ? IsAsciiLetter(c) : IsLetter(UnicodeData.GetCategory(c));

        public static bool IsLetter(string s, int index) => IsLetter(At(s, index));

        public static bool IsLetterOrDigit(char c)
            => c < 0x80 ? IsAsciiLetterOrDigit(c) : IsLetterOrDigit(UnicodeData.GetCategory(c));

        public static bool IsLetterOrDigit(string s, int index) => IsLetterOrDigit(At(s, index));

        public static bool IsUpper(char c)
            => c < 0x80 ? IsAsciiLetterUpper(c) : UnicodeData.GetCategory(c) == UnicodeCategory.UppercaseLetter;

        public static bool IsUpper(string s, int index) => At(s, index) == UnicodeCategory.UppercaseLetter;

        public static bool IsLower(char c)
            => c < 0x80 ? IsAsciiLetterLower(c) : UnicodeData.GetCategory(c) == UnicodeCategory.LowercaseLetter;

        public static bool IsLower(string s, int index) => At(s, index) == UnicodeCategory.LowercaseLetter;

        // Digits, letter numbers ('Ⅻ') and other numbers ('½', '²').
        public static bool IsNumber(char c) => IsNumber(UnicodeData.GetCategory(c));

        public static bool IsNumber(string s, int index) => IsNumber(At(s, index));

        public static bool IsPunctuation(char c) => IsPunctuation(UnicodeData.GetCategory(c));

        public static bool IsPunctuation(string s, int index) => IsPunctuation(At(s, index));

        public static bool IsSymbol(char c) => IsSymbol(UnicodeData.GetCategory(c));

        public static bool IsSymbol(string s, int index) => IsSymbol(At(s, index));

        public static bool IsSeparator(char c) => IsSeparator(UnicodeData.GetCategory(c));

        public static bool IsSeparator(string s, int index) => IsSeparator(At(s, index));

        public static bool IsControl(char c) => c < 0x20 || IsBetween(c, (char)0x7F, (char)0x9F);

        public static bool IsControl(string s, int index) => IsControl(CharAt(s, index));

        // Unicode's White_Space: separators, \t to \r, and U+0085. Needs no table.
        public static bool IsWhiteSpace(char c)
        {
            if (c < 0x100)
                return c == ' ' || IsBetween(c, '\t', '\r') || c == 0x85 || c == 0xA0;
            return c == 0x1680 || IsBetween(c, (char)0x2000, (char)0x200A) || c == 0x2028 || c == 0x2029
                || c == 0x202F || c == 0x205F || c == 0x3000;
        }

        public static bool IsWhiteSpace(string s, int index) => IsWhiteSpace(CharAt(s, index));

        private static bool IsLetter(UnicodeCategory category) => (uint)category <= (uint)UnicodeCategory.OtherLetter;

        private static bool IsLetterOrDigit(UnicodeCategory category)
            => IsLetter(category) || category == UnicodeCategory.DecimalDigitNumber;

        private static bool IsNumber(UnicodeCategory category)
            => (uint)(category - UnicodeCategory.DecimalDigitNumber) <= UnicodeCategory.OtherNumber - UnicodeCategory.DecimalDigitNumber;

        private static bool IsPunctuation(UnicodeCategory category)
            => (uint)(category - UnicodeCategory.ConnectorPunctuation) <= UnicodeCategory.OtherPunctuation - UnicodeCategory.ConnectorPunctuation;

        private static bool IsSymbol(UnicodeCategory category)
            => (uint)(category - UnicodeCategory.MathSymbol) <= UnicodeCategory.OtherSymbol - UnicodeCategory.MathSymbol;

        private static bool IsSeparator(UnicodeCategory category)
            => (uint)(category - UnicodeCategory.SpaceSeparator) <= UnicodeCategory.ParagraphSeparator - UnicodeCategory.SpaceSeparator;

        // The category of the character at index, a surrogate pair included.
        private static UnicodeCategory At(string s, int index) => UnicodeData.GetCategory(UnicodeData.CodePointAt(s, index));

        private static char CharAt(string s, int index)
        {
            if (s == null)
                Environment.FailFast("ArgumentNullException: s");
            if ((uint)index >= (uint)s.Length)
                Environment.FailFast("ArgumentOutOfRangeException: index");
            return s[index];
        }

        // Case

        // By the current culture's casing, as in .NET (invariant unless one is set).
        public static char ToUpper(char c) => (char)System.Globalization.TextCasing.Change(c, true, System.Globalization.TextCasing.CurrentMode);

        // By the culture's casing (Turkish maps i up to İ).
        public static char ToUpper(char c, System.Globalization.CultureInfo culture)
            => (culture ?? throw new ArgumentNullException("culture")).TextInfo.ToUpper(c);

        public static char ToLower(char c, System.Globalization.CultureInfo culture)
            => (culture ?? throw new ArgumentNullException("culture")).TextInfo.ToLower(c);

        public static char ToLower(char c) => (char)System.Globalization.TextCasing.Change(c, false, System.Globalization.TextCasing.CurrentMode);

        public static char ToUpperInvariant(char c)
            => c < 0x80 ? (IsAsciiLetterLower(c) ? (char)(c - 0x20) : c) : UnicodeData.ToUpper(c);

        public static char ToLowerInvariant(char c)
            => c < 0x80 ? (IsAsciiLetterUpper(c) ? (char)(c + 0x20) : c) : UnicodeData.ToLower(c);

        // Surrogates: the two halves of a character above U+FFFF in UTF-16.

        public static bool IsSurrogate(char c) => IsBetween(c, (char)0xD800, (char)0xDFFF);

        public static bool IsSurrogate(string s, int index) => IsSurrogate(CharAt(s, index));

        public static bool IsHighSurrogate(char c) => IsBetween(c, (char)0xD800, (char)0xDBFF);

        public static bool IsHighSurrogate(string s, int index) => IsHighSurrogate(CharAt(s, index));

        public static bool IsLowSurrogate(char c) => IsBetween(c, (char)0xDC00, (char)0xDFFF);

        public static bool IsLowSurrogate(string s, int index) => IsLowSurrogate(CharAt(s, index));

        public static bool IsSurrogatePair(char highSurrogate, char lowSurrogate)
            => IsHighSurrogate(highSurrogate) && IsLowSurrogate(lowSurrogate);

        public static bool IsSurrogatePair(string s, int index)
            => IsHighSurrogate(CharAt(s, index)) && index + 1 < s.Length && IsLowSurrogate(s[index + 1]);

        // A code point as a string: one char, or a surrogate pair above U+FFFF.
        public static string ConvertFromUtf32(int utf32)
        {
            if ((uint)utf32 > 0x10FFFF || IsBetween((char)utf32, (char)0xD800, (char)0xDFFF) && utf32 <= 0xFFFF)
                Environment.FailFast("char.ConvertFromUtf32: not a valid code point");
            if (utf32 <= 0xFFFF)
                return ToString((char)utf32);
            char* pair = stackalloc char[2];
            pair[0] = (char)(((utf32 - 0x10000) >> 10) + 0xD800);
            pair[1] = (char)(((utf32 - 0x10000) & 0x3FF) + 0xDC00);
            return Number.FromChars(pair, 2);
        }

        public static int ConvertToUtf32(char highSurrogate, char lowSurrogate)
        {
            if (!IsHighSurrogate(highSurrogate) || !IsLowSurrogate(lowSurrogate))
                Environment.FailFast("char.ConvertToUtf32: not a surrogate pair");
            return ((highSurrogate - 0xD800) << 10) + (lowSurrogate - 0xDC00) + 0x10000;
        }

        // The code point at index: a surrogate pair's, or the char's own. A
        // lone surrogate ends the program, as .NET throws for it.
        public static int ConvertToUtf32(string s, int index)
        {
            char c = CharAt(s, index);
            if (!IsSurrogate(c))
                return c;
            if (!IsHighSurrogate(c) || index + 1 >= s.Length || !IsLowSurrogate(s[index + 1]))
                Environment.FailFast("char.ConvertToUtf32: lone surrogate");
            return ConvertToUtf32(c, s[index + 1]);
        }
    }
}
