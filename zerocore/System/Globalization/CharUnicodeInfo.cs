namespace System.Globalization
{
    // Unicode's general categories, numbered as in .NET.
    public enum UnicodeCategory
    {
        UppercaseLetter = 0,
        LowercaseLetter = 1,
        TitlecaseLetter = 2,
        ModifierLetter = 3,
        OtherLetter = 4,
        NonSpacingMark = 5,
        SpacingCombiningMark = 6,
        EnclosingMark = 7,
        DecimalDigitNumber = 8,
        LetterNumber = 9,
        OtherNumber = 10,
        SpaceSeparator = 11,
        LineSeparator = 12,
        ParagraphSeparator = 13,
        Control = 14,
        Format = 15,
        Surrogate = 16,
        PrivateUse = 17,
        ConnectorPunctuation = 18,
        DashPunctuation = 19,
        OpenPunctuation = 20,
        ClosePunctuation = 21,
        InitialQuotePunctuation = 22,
        FinalQuotePunctuation = 23,
        OtherPunctuation = 24,
        MathSymbol = 25,
        CurrencySymbol = 26,
        ModifierSymbol = 27,
        OtherSymbol = 28,
        OtherNotAssigned = 29,
    }

    // A character's category and numeric value, from the tables in
    // UnicodeData.cs (generated from .NET 10's data, Unicode 16.0). Each table
    // is linked only into programs that use it: categories take about 16 KB,
    // numeric values 3 KB, casing (char.ToUpper and ToLower) 1.6 KB.
    public static class CharUnicodeInfo
    {
        public static UnicodeCategory GetUnicodeCategory(char ch) => UnicodeData.GetCategory(ch);

        public static UnicodeCategory GetUnicodeCategory(int codePoint)
        {
            if ((uint)codePoint > 0x10FFFF)
                Environment.FailFast("CharUnicodeInfo.GetUnicodeCategory: not a code point");
            return UnicodeData.GetCategory(codePoint);
        }

        // A surrogate pair at index counts as the character it encodes.
        public static UnicodeCategory GetUnicodeCategory(string s, int index)
            => UnicodeData.GetCategory(UnicodeData.CodePointAt(s, index));

        // The value of a digit, numeral or fraction ('5' is 5, '½' is 0.5,
        // 'Ⅻ' is 12); -1 for any other character.
        public static double GetNumericValue(char ch) => UnicodeData.GetNumericValue(ch);

        public static double GetNumericValue(string s, int index)
            => UnicodeData.GetNumericValue(UnicodeData.CodePointAt(s, index));
    }

    internal static partial class UnicodeData
    {
        internal static UnicodeCategory GetCategory(int codePoint)
            => (UnicodeCategory)Lookup(CategoryLevel1, CategoryLevel2, CategoryLevel3, CategoryLevel4,
                CategoryBits2, CategoryBits3, CategoryBits4, CategoryWidth1, CategoryWidth2, CategoryWidth3, CategoryWidth4, codePoint);

        internal static double GetNumericValue(int codePoint)
        {
            int index = Lookup(NumberLevel1, NumberLevel2, NumberLevel3, NumberLevel4,
                NumberBits2, NumberBits3, NumberBits4, NumberWidth1, NumberWidth2, NumberWidth3, NumberWidth4, codePoint);
            ReadOnlySpan<byte> numbers = Numbers;
            long bits = 0;
            for (int i = 7; i >= 0; i--)
                bits = (bits << 8) | numbers[index * 8 + i];
            return BitConverter.Int64BitsToDouble(bits);
        }

        internal static char ToUpper(char c) => (char)(c + CasingDistance(c, 0));

        internal static char ToLower(char c) => (char)(c + CasingDistance(c, 2));

        // A code point's simple, invariant upper and lower case, above U+FFFF
        // too (Deseret, Osage, Adlam, ...), as Rune and string casing give it.
        internal static int ToUpper(int codePoint)
            => codePoint < 0x10000 ? ToUpper((char)codePoint) : codePoint + WideDistance(codePoint, 5);

        internal static int ToLower(int codePoint)
            => codePoint < 0x10000 ? ToLower((char)codePoint) : codePoint + WideDistance(codePoint, 7);

        // A binary search of WideCasing's runs; offset 5: the distance to the
        // upper case, 7: to the lower case.
        private static int WideDistance(int codePoint, int offset)
        {
            ReadOnlySpan<byte> runs = WideCasing;
            int low = 0, high = runs.Length / 9 - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int at = middle * 9;
                int first = runs[at] | (runs[at + 1] << 8) | (runs[at + 2] << 16);
                if (codePoint < first)
                    high = middle - 1;
                else if (codePoint >= first + (runs[at + 3] | (runs[at + 4] << 8)))
                    low = middle + 1;
                else
                    return (short)(runs[at + offset] | (runs[at + offset + 1] << 8));
            }
            return 0;
        }

        // offset 0: the distance to the upper case; 2: to the lower case.
        private static int CasingDistance(char c, int offset)
        {
            int index = Lookup(CasingLevel1, CasingLevel2, CasingLevel3, CasingLevel4,
                CasingBits2, CasingBits3, CasingBits4, CasingWidth1, CasingWidth2, CasingWidth3, CasingWidth4, c);
            ReadOnlySpan<byte> pairs = CasingPairs;
            return pairs[index * 4 + offset] | (pairs[index * 4 + offset + 1] << 8);
        }

        // Four levels: level 1, by the code point's high bits, picks a block
        // of level 2; that, by the next bits, a block of level 3; that a block
        // of level 4, which holds the value.
        private static int Lookup(ReadOnlySpan<byte> level1, ReadOnlySpan<byte> level2, ReadOnlySpan<byte> level3,
            ReadOnlySpan<byte> level4, int bits2, int bits3, int bits4,
            int width1, int width2, int width3, int width4, int codePoint)
        {
            int block = Read(level1, width1, codePoint >> (bits2 + bits3 + bits4));
            block = Read(level2, width2, (block << bits2) + ((codePoint >> (bits3 + bits4)) & ((1 << bits2) - 1)));
            block = Read(level3, width3, (block << bits3) + ((codePoint >> bits4) & ((1 << bits3) - 1)));
            return Read(level4, width4, (block << bits4) + (codePoint & ((1 << bits4) - 1)));
        }

        private static int Read(ReadOnlySpan<byte> table, int width, int index)
            => width == 1 ? table[index] : table[index * 2] | (table[index * 2 + 1] << 8);

        // The character at index: the code point a surrogate pair there
        // encodes, or the char itself (a lone surrogate included).
        internal static int CodePointAt(string s, int index)
        {
            if (s == null)
                Environment.FailFast("ArgumentNullException: s");
            if ((uint)index >= (uint)s.Length)
                Environment.FailFast("ArgumentOutOfRangeException: index");
            char c = s[index];
            if (c >= 0xD800 && c <= 0xDBFF && index + 1 < s.Length)
            {
                char next = s[index + 1];
                if (next >= 0xDC00 && next <= 0xDFFF)
                    return ((c - 0xD800) << 10) + (next - 0xDC00) + 0x10000;
            }
            return c;
        }

        // Encoding.Latin1's replacement for a char above U+00FF: a similar
        // Latin-1 character, or '?'. A binary search of Latin1BestFit.
        internal static byte Latin1BestFitFor(char c)
        {
            ReadOnlySpan<byte> fit = Latin1BestFit;
            int low = 0, high = fit.Length / 3 - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int found = fit[middle * 3] | (fit[middle * 3 + 1] << 8);
                if (found == c)
                    return fit[middle * 3 + 2];
                if (found < c)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            return (byte)'?';
        }
    }
}
