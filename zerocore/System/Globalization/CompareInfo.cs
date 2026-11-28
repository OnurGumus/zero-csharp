namespace System
{
    public enum StringComparison
    {
        CurrentCulture = 0,
        CurrentCultureIgnoreCase = 1,
        InvariantCulture = 2,
        InvariantCultureIgnoreCase = 3,
        Ordinal = 4,
        OrdinalIgnoreCase = 5,
    }
}

namespace System.Globalization
{
    [Flags]
    public enum CompareOptions
    {
        None = 0x0,
        IgnoreCase = 0x1,
        IgnoreNonSpace = 0x2,
        IgnoreSymbols = 0x4,
        IgnoreKanaType = 0x8,
        IgnoreWidth = 0x10,
        NumericOrdering = 0x20,
        OrdinalIgnoreCase = 0x10000000,
        StringSort = 0x20000000,
        Ordinal = 0x40000000,
    }

    // Comparing and searching strings the invariant culture's way, which here
    // is .NET's invariant mode: ordinal, by UTF-16 code unit, and ignoring
    // case compares upper cases. The other options are accepted and change
    // nothing, as in that mode; StringSort and unknown flags throw.
    public sealed class CompareInfo
    {
        // What searching accepts besides Ordinal and OrdinalIgnoreCase alone;
        // comparing accepts NumericOrdering and StringSort too.
        private const CompareOptions IndexOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace
            | CompareOptions.IgnoreSymbols | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
        private const CompareOptions CompareOnlyOptions = CompareOptions.NumericOrdering | CompareOptions.StringSort;

        private static CompareInfo s_invariant;

        private CompareInfo() { }

        internal static CompareInfo Invariant => s_invariant ??= new CompareInfo();

        public static CompareInfo GetCompareInfo(string name) => CultureInfo.GetCultureInfo(name ?? throw new ArgumentNullException("name")).CompareInfo;
        public static CompareInfo GetCompareInfo(int culture) => CultureInfo.GetCultureInfo(culture).CompareInfo;

        public string Name => "";
        public int LCID => CultureInfo.LocaleInvariant;
        public override string ToString() => "CompareInfo - ";
        public override bool Equals(object value) => value is CompareInfo;
        public override int GetHashCode() => 0;

        // In invariant mode every non-empty text sorts.
        public static bool IsSortable(char ch) => true;
        public static bool IsSortable(string text) => IsSortable(Number.AsSpan(text ?? throw new ArgumentNullException("text")));
        public static bool IsSortable(ReadOnlySpan<char> text) => text.Length != 0;
        public static bool IsSortable(System.Text.Rune value) => true;

        private enum Use { Compare, Affix, Search }

        // Whether to ignore case, after checking the options as .NET does:
        // Ordinal and OrdinalIgnoreCase only alone. Comparing also takes
        // NumericOrdering and StringSort; searching words its refusal of
        // Ordinal with others differently.
        private static bool IgnoreCase(CompareOptions options, Use use)
        {
            CompareOptions valid = use == Use.Compare ? IndexOptions | CompareOnlyOptions : IndexOptions;
            if ((options & ~valid) == 0)
                return (options & CompareOptions.IgnoreCase) != 0;
            if (options == CompareOptions.Ordinal)
                return false;
            if (options == CompareOptions.OrdinalIgnoreCase)
                return true;
            throw new ArgumentException(use != Use.Search && (options & CompareOptions.Ordinal) != 0
                ? "CompareOption.Ordinal cannot be used with other options." : "Value of flags is invalid.", "options");
        }

        // ---- Compare ----

        public int Compare(string string1, string string2) => Compare(string1, string2, CompareOptions.None);

        // A null string comes first (after the options are checked).
        public int Compare(string string1, string string2, CompareOptions options)
        {
            bool ignoreCase = IgnoreCase(options, Use.Compare);
            if (string1 == null)
                return string2 == null ? 0 : -1;
            if (string2 == null)
                return 1;
            return Compare(Number.AsSpan(string1), Number.AsSpan(string2), ignoreCase);
        }

        public int Compare(ReadOnlySpan<char> string1, ReadOnlySpan<char> string2, CompareOptions options = CompareOptions.None)
            => Compare(string1, string2, IgnoreCase(options, Use.Compare));

        private static int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b, bool ignoreCase)
            => ignoreCase ? TextCasing.CompareIgnoreCase(a, b) : TextCasing.CompareOrdinal(a, b);

        public int Compare(string string1, int offset1, string string2, int offset2)
            => Compare(string1, offset1, string2, offset2, CompareOptions.None);

        public int Compare(string string1, int offset1, string string2, int offset2, CompareOptions options)
            => Compare(string1, offset1, string1 == null ? 0 : string1.Length - offset1,
                string2, offset2, string2 == null ? 0 : string2.Length - offset2, options);

        public int Compare(string string1, int offset1, int length1, string string2, int offset2, int length2)
            => Compare(string1, offset1, length1, string2, offset2, length2, CompareOptions.None);

        public int Compare(string string1, int offset1, int length1, string string2, int offset2, int length2, CompareOptions options)
        {
            if (!Fits(string1, offset1, length1) || !Fits(string2, offset2, length2))
                ThrowRange(string1, offset1, length1, offset2, length2);
            bool ignoreCase = IgnoreCase(options, Use.Compare);
            if (string1 == null)
                return string2 == null ? 0 : -1;
            if (string2 == null)
                return 1;
            return Compare(Number.AsSpan(string1).Slice(offset1, length1), Number.AsSpan(string2).Slice(offset2, length2), ignoreCase);
        }

        private static bool Fits(string s, int offset, int length)
            => s == null ? offset == 0 && length == 0 : (uint)offset <= (uint)s.Length && (uint)length <= (uint)(s.Length - offset);

        private static void ThrowRange(string string1, int offset1, int length1, int offset2, int length2)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length1, "length1");
            ArgumentOutOfRangeException.ThrowIfNegative(length2, "length2");
            ArgumentOutOfRangeException.ThrowIfNegative(offset1, "offset1");
            ArgumentOutOfRangeException.ThrowIfNegative(offset2, "offset2");
            if (offset1 > (string1 == null ? 0 : string1.Length) - length1)
                throw new ArgumentOutOfRangeException("string1", "Offset and length must refer to a position in the string.");
            throw new ArgumentOutOfRangeException("string2", "Offset and length must refer to a position in the string.");
        }

        // ---- prefix and suffix ----

        public bool IsPrefix(string source, string prefix) => IsPrefix(source, prefix, CompareOptions.None);

        public bool IsPrefix(string source, string prefix, CompareOptions options)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (prefix == null)
                throw new ArgumentNullException("prefix");
            return IsPrefix(Number.AsSpan(source), Number.AsSpan(prefix), options);
        }

        // An empty prefix is one before the options are checked, as in .NET.
        public bool IsPrefix(ReadOnlySpan<char> source, ReadOnlySpan<char> prefix, CompareOptions options = CompareOptions.None)
            => prefix.Length == 0 || TextCasing.StartsWith(source, prefix, IgnoreCase(options, Use.Affix));

        public bool IsSuffix(string source, string suffix) => IsSuffix(source, suffix, CompareOptions.None);

        public bool IsSuffix(string source, string suffix, CompareOptions options)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (suffix == null)
                throw new ArgumentNullException("suffix");
            return IsSuffix(Number.AsSpan(source), Number.AsSpan(suffix), options);
        }

        public bool IsSuffix(ReadOnlySpan<char> source, ReadOnlySpan<char> suffix, CompareOptions options = CompareOptions.None)
            => suffix.Length == 0 || TextCasing.EndsWith(source, suffix, IgnoreCase(options, Use.Affix));

        // ---- IndexOf ----

        public int IndexOf(string source, char value) => IndexOf(source, value, CompareOptions.None);
        public int IndexOf(string source, string value) => IndexOf(source, value, CompareOptions.None);
        public int IndexOf(string source, char value, CompareOptions options) => IndexOf(source, value, 0, Length(source), options);
        public int IndexOf(string source, string value, CompareOptions options) => IndexOf(source, value, 0, Length(source), options);
        public int IndexOf(string source, char value, int startIndex) => IndexOf(source, value, startIndex, CompareOptions.None);
        public int IndexOf(string source, string value, int startIndex) => IndexOf(source, value, startIndex, CompareOptions.None);
        public int IndexOf(string source, char value, int startIndex, CompareOptions options) => IndexOf(source, value, startIndex, Length(source) - startIndex, options);
        public int IndexOf(string source, string value, int startIndex, CompareOptions options) => IndexOf(source, value, startIndex, Length(source) - startIndex, options);
        public int IndexOf(string source, char value, int startIndex, int count) => IndexOf(source, value, startIndex, count, CompareOptions.None);
        public int IndexOf(string source, string value, int startIndex, int count) => IndexOf(source, value, startIndex, count, CompareOptions.None);

        public unsafe int IndexOf(string source, char value, int startIndex, int count, CompareOptions options)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            CheckStartCount(source, startIndex, count);
            int found = IndexOf(Number.AsSpan(source).Slice(startIndex, count), new ReadOnlySpan<char>(&value, 1), options);
            return found < 0 ? found : found + startIndex;
        }

        public int IndexOf(string source, string value, int startIndex, int count, CompareOptions options)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (value == null)
                throw new ArgumentNullException("value");
            CheckStartCount(source, startIndex, count);
            int found = IndexOf(Number.AsSpan(source).Slice(startIndex, count), Number.AsSpan(value), options);
            return found < 0 ? found : found + startIndex;
        }

        public int IndexOf(ReadOnlySpan<char> source, ReadOnlySpan<char> value, CompareOptions options = CompareOptions.None)
            => TextCasing.IndexOf(source, value, IgnoreCase(options, Use.Search));

        public int IndexOf(ReadOnlySpan<char> source, ReadOnlySpan<char> value, CompareOptions options, out int matchLength)
        {
            int found = IndexOf(source, value, options);
            matchLength = found < 0 ? 0 : value.Length;
            return found;
        }

        public int IndexOf(ReadOnlySpan<char> source, System.Text.Rune value, CompareOptions options = CompareOptions.None)
        {
            System.Span<char> chars = stackalloc char[2];
            return IndexOf(source, chars.Slice(0, value.EncodeToUtf16(chars)), options);
        }

        private static int Length(string source) => source == null ? 0 : source.Length;

        private static void CheckStartCount(string source, int startIndex, int count)
        {
            if ((uint)startIndex > (uint)source.Length)
                throw new ArgumentOutOfRangeException("startIndex", "Index was out of range. Must be non-negative and less than or equal to the size of the collection.");
            if ((uint)count > (uint)(source.Length - startIndex))
                throw new ArgumentOutOfRangeException("count", "Count must be positive and count must refer to a location within the string/array/collection.");
        }

        // ---- LastIndexOf ----

        public int LastIndexOf(string source, char value) => LastIndexOf(source, value, CompareOptions.None);
        public int LastIndexOf(string source, string value) => LastIndexOf(source, value, CompareOptions.None);

        public int LastIndexOf(string source, char value, CompareOptions options)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            return LastIndexOf(source, value, source.Length - 1, source.Length, options);
        }

        public int LastIndexOf(string source, string value, CompareOptions options)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            return LastIndexOf(source, value, source.Length - 1, source.Length, options);
        }

        public int LastIndexOf(string source, char value, int startIndex) => LastIndexOf(source, value, startIndex, startIndex + 1, CompareOptions.None);
        public int LastIndexOf(string source, string value, int startIndex) => LastIndexOf(source, value, startIndex, startIndex + 1, CompareOptions.None);
        public int LastIndexOf(string source, char value, int startIndex, CompareOptions options) => LastIndexOf(source, value, startIndex, startIndex + 1, options);
        public int LastIndexOf(string source, string value, int startIndex, CompareOptions options) => LastIndexOf(source, value, startIndex, startIndex + 1, options);
        public int LastIndexOf(string source, char value, int startIndex, int count) => LastIndexOf(source, value, startIndex, count, CompareOptions.None);
        public int LastIndexOf(string source, string value, int startIndex, int count) => LastIndexOf(source, value, startIndex, count, CompareOptions.None);

        public unsafe int LastIndexOf(string source, char value, int startIndex, int count, CompareOptions options)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            return LastIndexOf(source, new ReadOnlySpan<char>(&value, 1), startIndex, count, options);
        }

        public int LastIndexOf(string source, string value, int startIndex, int count, CompareOptions options)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (value == null)
                throw new ArgumentNullException("value");
            return LastIndexOf(source, Number.AsSpan(value), startIndex, count, options);
        }

        // As .NET: searches the count chars ending at startIndex.
        private int LastIndexOf(string source, ReadOnlySpan<char> value, int startIndex, int count, CompareOptions options)
        {
            while ((uint)startIndex >= (uint)source.Length)
            {
                if (startIndex == -1 && source.Length == 0)
                {
                    count = 0;
                    break;
                }
                if (startIndex != source.Length)
                    throw new ArgumentOutOfRangeException("startIndex", "Index was out of range. Must be non-negative and less than the size of the collection.");
                // Just past the end counts as the last char.
                startIndex--;
                if (count > 0)
                    count--;
            }
            int start = startIndex - count + 1;
            if (count < 0 || start < 0)
                throw new ArgumentOutOfRangeException("count", "Count must be positive and count must refer to a location within the string/array/collection.");
            int found = LastIndexOf(Number.AsSpan(source).Slice(start, count), value, options);
            return found < 0 ? found : found + start;
        }

        public int LastIndexOf(ReadOnlySpan<char> source, ReadOnlySpan<char> value, CompareOptions options = CompareOptions.None)
            => TextCasing.LastIndexOf(source, value, IgnoreCase(options, Use.Search));

        public int LastIndexOf(ReadOnlySpan<char> source, ReadOnlySpan<char> value, CompareOptions options, out int matchLength)
        {
            int found = LastIndexOf(source, value, options);
            matchLength = found < 0 ? 0 : value.Length;
            return found;
        }

        public int LastIndexOf(ReadOnlySpan<char> source, System.Text.Rune value, CompareOptions options = CompareOptions.None)
        {
            System.Span<char> chars = stackalloc char[2];
            return LastIndexOf(source, chars.Slice(0, value.EncodeToUtf16(chars)), options);
        }

        // ---- hashing ----

        public int GetHashCode(string source, CompareOptions options)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            return GetHashCode(Number.AsSpan(source), options);
        }

        public int GetHashCode(ReadOnlySpan<char> source, CompareOptions options)
            => TextCasing.GetHashCode(source, IgnoreCase(options, Use.Compare));
    }
}
