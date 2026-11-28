using System.Globalization;

namespace System
{
    // string's casing and its StringComparison and culture overloads, as in
    // .NET. The culture is always the invariant one (see StringOps). Casing
    // returns the same string when nothing changes, else a new one, which
    // like every string the core library makes is never freed; for spans,
    // lib's MemoryExtensions.ToUpperInvariant writes into a buffer instead.
    public sealed partial class String
    {
        public string ToUpper() => StringOps.ToUpper(this);
        public string ToUpper(CultureInfo culture) => StringOps.ToUpper(this);
        public string ToUpperInvariant() => StringOps.ToUpper(this);
        public string ToLower() => StringOps.ToLower(this);
        public string ToLower(CultureInfo culture) => StringOps.ToLower(this);
        public string ToLowerInvariant() => StringOps.ToLower(this);

        public bool Equals(string value, StringComparison comparisonType) => StringOps.Equals(this, value, comparisonType);
        public static bool Equals(string a, string b, StringComparison comparisonType) => StringOps.Equals(a, b, comparisonType);

        public static int Compare(string strA, string strB) => StringOps.Compare(strA, strB, StringComparison.CurrentCulture);
        public static int Compare(string strA, string strB, bool ignoreCase)
            => StringOps.Compare(strA, strB, ignoreCase ? StringComparison.CurrentCultureIgnoreCase : StringComparison.CurrentCulture);
        public static int Compare(string strA, string strB, StringComparison comparisonType) => StringOps.Compare(strA, strB, comparisonType);
        public static int Compare(string strA, string strB, CultureInfo culture, CompareOptions options) => StringOps.Compare(strA, strB, culture, options);
        public static int Compare(string strA, string strB, bool ignoreCase, CultureInfo culture)
            => StringOps.Compare(strA, strB, culture, ignoreCase ? CompareOptions.IgnoreCase : CompareOptions.None);
        public static int Compare(string strA, int indexA, string strB, int indexB, int length)
            => StringOps.Compare(strA, indexA, strB, indexB, length, CultureInfo.CurrentCulture, CompareOptions.None);
        public static int Compare(string strA, int indexA, string strB, int indexB, int length, bool ignoreCase)
            => StringOps.Compare(strA, indexA, strB, indexB, length, CultureInfo.CurrentCulture, ignoreCase ? CompareOptions.IgnoreCase : CompareOptions.None);
        public static int Compare(string strA, int indexA, string strB, int indexB, int length, bool ignoreCase, CultureInfo culture)
            => StringOps.Compare(strA, indexA, strB, indexB, length, culture, ignoreCase ? CompareOptions.IgnoreCase : CompareOptions.None);
        public static int Compare(string strA, int indexA, string strB, int indexB, int length, CultureInfo culture, CompareOptions options)
            => StringOps.Compare(strA, indexA, strB, indexB, length, culture, options);
        public static int Compare(string strA, int indexA, string strB, int indexB, int length, StringComparison comparisonType)
            => StringOps.Compare(strA, indexA, strB, indexB, length, comparisonType);

        public bool StartsWith(string value, StringComparison comparisonType) => StringOps.StartsWith(this, value, comparisonType);
        public bool StartsWith(string value, bool ignoreCase, CultureInfo culture)
            => StringOps.StartsWith(this, value, ignoreCase ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture);
        public bool EndsWith(string value, StringComparison comparisonType) => StringOps.EndsWith(this, value, comparisonType);
        public bool EndsWith(string value, bool ignoreCase, CultureInfo culture)
            => StringOps.EndsWith(this, value, ignoreCase ? StringComparison.InvariantCultureIgnoreCase : StringComparison.InvariantCulture);

        public bool Contains(string value, StringComparison comparisonType) => StringOps.IndexOf(this, value, 0, Length, comparisonType) >= 0;
        public bool Contains(char value, StringComparison comparisonType) => StringOps.IndexOf(this, value, comparisonType) >= 0;

        public int IndexOf(char value, StringComparison comparisonType) => StringOps.IndexOf(this, value, comparisonType);
        public int IndexOf(string value, StringComparison comparisonType) => StringOps.IndexOf(this, value, 0, Length, comparisonType);
        public int IndexOf(string value, int startIndex, StringComparison comparisonType) => StringOps.IndexOf(this, value, startIndex, Length - startIndex, comparisonType);
        public int IndexOf(string value, int startIndex, int count, StringComparison comparisonType) => StringOps.IndexOf(this, value, startIndex, count, comparisonType);

        public int LastIndexOf(string value, StringComparison comparisonType) => StringOps.LastIndexOf(this, value, Length - 1, Length, comparisonType);
        public int LastIndexOf(string value, int startIndex, StringComparison comparisonType) => StringOps.LastIndexOf(this, value, startIndex, startIndex + 1, comparisonType);
        public int LastIndexOf(string value, int startIndex, int count, StringComparison comparisonType) => StringOps.LastIndexOf(this, value, startIndex, count, comparisonType);

        public int GetHashCode(StringComparison comparisonType) => StringOps.GetHashCode(this, comparisonType);
    }
}
