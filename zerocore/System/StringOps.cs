using System.Globalization;

namespace System
{
    // What string's casing and StringComparison methods do, with .NET's
    // checks, results and messages; String.Comparison.cs forwards to these.
    // The culture comparisons are the invariant culture's, which here is
    // .NET's invariant mode: ordinal, and ignoring case compares upper cases.
    internal static class StringOps
    {
        private static CompareInfo Culture => CompareInfo.Invariant;

        internal static void CheckComparison(StringComparison comparisonType)
        {
            if ((uint)comparisonType > (uint)StringComparison.OrdinalIgnoreCase)
                throw new ArgumentException("The string comparison type passed in is currently not supported.", "comparisonType");
        }

        // Whether the comparison ignores case (after checking it).
        private static bool IgnoreCase(StringComparison comparisonType)
        {
            CheckComparison(comparisonType);
            return ((int)comparisonType & 1) != 0;
        }

        internal static string ToUpper(string s) => TextCasing.Change(s, true);
        internal static string ToLower(string s) => TextCasing.Change(s, false);

        // ---- Equals and Compare ----

        internal static bool Equals(string a, string b, StringComparison comparisonType)
        {
            bool ignoreCase = IgnoreCase(comparisonType);
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Length != b.Length)
                return false;
            return ignoreCase ? TextCasing.EqualsIgnoreCase(Number.AsSpan(a), Number.AsSpan(b)) : TextCasing.CompareOrdinal(Number.AsSpan(a), Number.AsSpan(b)) == 0;
        }

        internal static int Compare(string a, string b, StringComparison comparisonType)
        {
            bool ignoreCase = IgnoreCase(comparisonType);
            if (ReferenceEquals(a, b))
                return 0;
            if (a == null)
                return -1;
            if (b == null)
                return 1;
            if (ignoreCase)
                return TextCasing.CompareIgnoreCase(Number.AsSpan(a), Number.AsSpan(b));
            return comparisonType == StringComparison.Ordinal ? CompareOrdinal(a, b) : TextCasing.CompareOrdinal(Number.AsSpan(a), Number.AsSpan(b));
        }

        // string.CompareOrdinal's result, as .NET computes it: the first
        // chars first (an empty string's is its terminating '\0', so "" against
        // "b" is -98), then two chars at a time, so when the shorter length
        // is odd the char after it counts too ("abc" against "a" is 'b' -
        // '\0'), else the difference of the lengths.
        internal static int CompareOrdinal(string a, string b)
        {
            if (ReferenceEquals(a, b))
                return 0;
            if (a == null)
                return -1;
            if (b == null)
                return 1;
            char first = a.Length == 0 ? '\0' : a[0], other = b.Length == 0 ? '\0' : b[0];
            if (first != other)
                return first - other;
            int length = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < length; i++)
            {
                if (a[i] != b[i])
                    return a[i] - b[i];
            }
            if ((length & 1) != 0)
            {
                char x = length < a.Length ? a[length] : '\0', y = length < b.Length ? b[length] : '\0';
                if (x != y)
                    return x - y;
            }
            return a.Length - b.Length;
        }

        internal static int Compare(string a, string b, CultureInfo culture, CompareOptions options)
        {
            if (culture == null)
                throw new ArgumentNullException("culture");
            return culture.CompareInfo.Compare(a, b, options);
        }

        internal static int Compare(string a, int indexA, string b, int indexB, int length, StringComparison comparisonType)
        {
            bool ignoreCase = IgnoreCase(comparisonType);
            if (a == null || b == null)
                return ReferenceEquals(a, b) ? 0 : a == null ? -1 : 1;
            ArgumentOutOfRangeException.ThrowIfNegative(length, "length");
            if (indexA < 0 || indexB < 0)
                throw new ArgumentOutOfRangeException(indexA < 0 ? "indexA" : "indexB", "Index was out of range. Must be non-negative and less than or equal to the size of the collection.");
            if (a.Length - indexA < 0 || b.Length - indexB < 0)
                throw new ArgumentOutOfRangeException(a.Length - indexA < 0 ? "indexA" : "indexB", "Index was out of range. Must be non-negative and less than or equal to the size of the collection.");
            if (length == 0 || (ReferenceEquals(a, b) && indexA == indexB))
                return 0;
            int lengthA = Math.Min(length, a.Length - indexA);
            int lengthB = Math.Min(length, b.Length - indexB);
            ReadOnlySpan<char> x = Number.AsSpan(a).Slice(indexA, lengthA), y = Number.AsSpan(b).Slice(indexB, lengthB);
            return ignoreCase ? TextCasing.CompareIgnoreCase(x, y) : TextCasing.CompareOrdinal(x, y);
        }

        internal static int Compare(string a, int indexA, string b, int indexB, int length, CultureInfo culture, CompareOptions options)
        {
            if (culture == null)
                throw new ArgumentNullException("culture");
            int lengthA = length, lengthB = length;
            if (a != null)
                lengthA = Math.Min(lengthA, a.Length - indexA);
            if (b != null)
                lengthB = Math.Min(lengthB, b.Length - indexB);
            return culture.CompareInfo.Compare(a, indexA, lengthA, b, indexB, lengthB, options);
        }

        // ---- searching ----

        internal static bool StartsWith(string s, string value, StringComparison comparisonType)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            bool ignoreCase = IgnoreCase(comparisonType);
            if (ReferenceEquals(s, value) || value.Length == 0)
                return true;
            return TextCasing.StartsWith(Number.AsSpan(s), Number.AsSpan(value), ignoreCase);
        }

        internal static bool EndsWith(string s, string value, StringComparison comparisonType)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            bool ignoreCase = IgnoreCase(comparisonType);
            if (ReferenceEquals(s, value) || value.Length == 0)
                return true;
            return TextCasing.EndsWith(Number.AsSpan(s), Number.AsSpan(value), ignoreCase);
        }

        // Checks the comparison first, then searches as CompareInfo does.
        private static CompareOptions Options(StringComparison comparisonType)
        {
            CheckComparison(comparisonType);
            return comparisonType switch
            {
                StringComparison.Ordinal => CompareOptions.Ordinal,
                StringComparison.OrdinalIgnoreCase => CompareOptions.OrdinalIgnoreCase,
                _ => ((int)comparisonType & 1) != 0 ? CompareOptions.IgnoreCase : CompareOptions.None,
            };
        }

        internal static int IndexOf(string s, char value, StringComparison comparisonType)
            => Culture.IndexOf(s, value, Options(comparisonType));

        internal static int IndexOf(string s, string value, int startIndex, int count, StringComparison comparisonType)
            => Culture.IndexOf(s, value, startIndex, count, Options(comparisonType));

        internal static int LastIndexOf(string s, string value, int startIndex, int count, StringComparison comparisonType)
            => Culture.LastIndexOf(s, value, startIndex, count, Options(comparisonType));

        // Ordinal is string's own hash, as in .NET.
        internal static int GetHashCode(string s, StringComparison comparisonType)
            => IgnoreCase(comparisonType) ? TextCasing.GetHashCode(Number.AsSpan(s), true) : s.GetHashCode();
    }
}
