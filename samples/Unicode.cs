// Test: char's methods and System.Text.Encoding, against .NET's answers.
// Every char's category, case and numeric value is hashed, method by method,
// and compared with the hash .NET 10 gives (dotnet run --project
// scripts/gen-unicode -- --check prints them). The encodings are checked on
// the inputs where they differ most: invalid bytes, lone surrogates, chars
// that don't fit.
// Exit code = number of failed checks.
// Build: ./build.sh Unicode

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace UnicodeSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void Section(string section)
    {
        Console.WriteLine($"{(s_sectionFailures == 0 ? "PASS" : "FAIL")} {section} ({s_sectionChecks} checks)");
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_failures++;
            s_sectionFailures++;
            Console.WriteLine($"  FAIL: {name}");
        }
    }

    // Hides a value from the optimizer, which would otherwise answer at build
    // time and never run the code being tested.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Opaque(string value) => value;

    static uint Mix(uint hash, uint value) => (hash ^ value) * 16777619;

    static uint B(bool b) => b ? 1u : 0u;

    // A hash of f over every char, as scripts/gen-unicode's Checksums does.
    static void CheckAllChars(string name, uint expected, Func<char, uint> f)
    {
        uint hash = 2166136261;
        for (int c = 0; c <= 0xFFFF; c++)
            hash = Mix(hash, f((char)c));
        if (hash != expected)
            Console.WriteLine($"  {name}: {hash:x8}, .NET gives {expected:x8}");
        Check(name, hash == expected);
    }

    static bool SameBytes(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        if (actual.Length != expected.Length)
            return false;
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] != expected[i])
                return false;
        }
        return true;
    }

    public static int Main()
    {
        CheckAscii();
        CheckEveryChar();
        CheckStringsAndSurrogates();
        CheckEncodingExamples();
        CheckUtf8();
        CheckOtherEncodings();
        CheckEncodingMembers();

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine($"{s_failures} checks FAILED.");
        return s_failures;
    }

    static void CheckAscii()
    {
        Check("IsAsciiDigit", char.IsAsciiDigit('7') && !char.IsAsciiDigit('a') && !char.IsAsciiDigit('٣'));
        Check("IsAsciiLetter", char.IsAsciiLetter('q') && char.IsAsciiLetter('Q') && !char.IsAsciiLetter('@')
            && !char.IsAsciiLetter('[') && !char.IsAsciiLetter('é'));
        Check("IsAsciiLetterUpper and Lower", char.IsAsciiLetterUpper('Z') && !char.IsAsciiLetterUpper('z')
            && char.IsAsciiLetterLower('a') && !char.IsAsciiLetterLower('A'));
        Check("IsAsciiLetterOrDigit", char.IsAsciiLetterOrDigit('0') && !char.IsAsciiLetterOrDigit('_'));
        Check("IsAsciiHexDigit", char.IsAsciiHexDigit('f') && char.IsAsciiHexDigit('F') && !char.IsAsciiHexDigit('g'));
        Check("IsAsciiHexDigitLower and Upper", char.IsAsciiHexDigitLower('c') && !char.IsAsciiHexDigitLower('C')
            && char.IsAsciiHexDigitUpper('C') && !char.IsAsciiHexDigitUpper('c'));
        Check("IsAscii", char.IsAscii('\x7F') && !char.IsAscii('\x80'));
        Check("IsBetween", char.IsBetween('m', 'a', 'z') && !char.IsBetween('A', 'a', 'z'));
        Check("Parse and TryParse", char.Parse(Opaque("x")) == 'x' && char.TryParse(Opaque("é"), out char e) && e == 'é'
            && !char.TryParse(Opaque("xy"), out char none) && none == '\0');
        Check("ToString", char.ToString('ß') == "ß" && 'ж'.ToString() == "ж");
        Section("ASCII and parsing");
    }

    static void CheckEveryChar()
    {
        CheckAllChars("IsLetter", 0x7a02c33a, c => B(char.IsLetter(c)));
        CheckAllChars("IsDigit", 0xcf40209f, c => B(char.IsDigit(c)));
        CheckAllChars("IsNumber", 0x6db5b258, c => B(char.IsNumber(c)));
        CheckAllChars("IsLetterOrDigit", 0xd3fc12c8, c => B(char.IsLetterOrDigit(c)));
        CheckAllChars("IsUpper", 0xcd7caad1, c => B(char.IsUpper(c)));
        CheckAllChars("IsLower", 0x5fa1b6e5, c => B(char.IsLower(c)));
        CheckAllChars("IsPunctuation", 0x8f807961, c => B(char.IsPunctuation(c)));
        CheckAllChars("IsSymbol", 0x0d82c8cf, c => B(char.IsSymbol(c)));
        CheckAllChars("IsSeparator", 0x25f33834, c => B(char.IsSeparator(c)));
        CheckAllChars("IsWhiteSpace", 0x5341a10c, c => B(char.IsWhiteSpace(c)));
        CheckAllChars("IsControl", 0x5d7c7232, c => B(char.IsControl(c)));
        CheckAllChars("GetUnicodeCategory", 0x92dee932, c => (uint)char.GetUnicodeCategory(c));
        CheckAllChars("ToUpper", 0x46426b10, c => char.ToUpper(c));
        CheckAllChars("ToLower", 0x5b83a394, c => char.ToLower(c));
        CheckAllChars("ToUpperInvariant", 0x46426b10, c => char.ToUpperInvariant(c));
        CheckAllChars("ToLowerInvariant", 0x5b83a394, c => char.ToLowerInvariant(c));
        CheckAllChars("GetNumericValue", 0x27c0afed, c => (uint)(long)(char.GetNumericValue(c) * 1000000));

        // Every code point's category, and the numeric value of each one above
        // U+FFFF that has one.
        uint hash = 2166136261;
        for (int cp = 0; cp < 0x110000; cp++)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(cp);
            hash = Mix(hash, (uint)category);
            if (cp > 0xFFFF && (category == UnicodeCategory.DecimalDigitNumber
                || category == UnicodeCategory.LetterNumber || category == UnicodeCategory.OtherNumber))
                hash = Mix(hash, (uint)(long)(char.GetNumericValue(char.ConvertFromUtf32(cp), 0) * 1000000));
        }
        if (hash != 0x8af2746d)
            Console.WriteLine($"  every code point: {hash:x8}, .NET gives 8af2746d");
        Check("every code point's category and numeric value", hash == 0x8af2746d);
        Section("every char, as .NET answers");
    }

    static void CheckStringsAndSurrogates()
    {
        // docs:char-methods
        Check("letters in any script",
            char.IsLetter('é') && char.IsLetter('ж') && char.IsLetter('中'));
        Check("digits in any script",
            char.IsDigit('٣') && char.GetNumericValue('٣') == 3);
        Check("numbers that aren't digits", char.IsNumber('½') && !char.IsDigit('½')
            && char.GetNumericValue('½') == 0.5 && char.GetNumericValue('Ⅻ') == 12);
        Check("case", char.ToUpper('ж') == 'Ж' && char.ToLower('Σ') == 'σ'
            && char.IsUpper('Σ'));
        Check("invariant case: one char to one",
            char.ToUpper('ß') == 'ß' && char.ToUpper('ı') == 'ı');
        Check("categories",
            char.GetUnicodeCategory('€') == UnicodeCategory.CurrencySymbol
            && char.IsPunctuation('¿') && char.IsSymbol('+')
            && char.IsWhiteSpace('\u00A0'));
        // docs:end

        string deseret = Opaque("𐐀x"); // U+10400, an upper-case letter, as a surrogate pair
        Check("a surrogate pair counts as its character", char.IsLetter(deseret, 0) && char.IsUpper(deseret, 0)
            && char.GetUnicodeCategory(deseret, 0) == UnicodeCategory.UppercaseLetter);
        Check("its second half alone is a surrogate", !char.IsLetter(deseret, 1)
            && char.GetUnicodeCategory(deseret, 1) == UnicodeCategory.Surrogate);
        Check("IsSurrogate and the halves", char.IsSurrogate(deseret, 0) && char.IsHighSurrogate(deseret[0])
            && char.IsLowSurrogate(deseret, 1) && char.IsSurrogatePair(deseret, 0) && !char.IsSurrogatePair(deseret, 1)
            && char.IsSurrogatePair(deseret[0], deseret[1]));
        Check("ConvertToUtf32", char.ConvertToUtf32(deseret, 0) == 0x10400 && char.ConvertToUtf32(deseret, 2) == 'x'
            && char.ConvertToUtf32('\uD83D', '\uDE00') == 0x1F600);
        Check("ConvertFromUtf32", char.ConvertFromUtf32(0x1F600) == "😀" && char.ConvertFromUtf32('A') == "A");
        Check("a lone high surrogate", char.GetUnicodeCategory(Opaque("\uD800x"), 0) == UnicodeCategory.Surrogate);
        Check("numeric value above U+FFFF", char.GetNumericValue(Opaque("𐄇"), 0) == 1
            && CharUnicodeInfo.GetNumericValue(Opaque("x𝟗"), 1) == 9);
        Check("string overloads of the rest", char.IsDigit(Opaque("a1"), 1) && char.IsNumber(Opaque("½"), 0)
            && char.IsLower(Opaque("aB"), 0) && !char.IsLower(Opaque("aB"), 1) && char.IsLetterOrDigit(Opaque("_9"), 1)
            && char.IsPunctuation(Opaque("a!"), 1) && char.IsSymbol(Opaque("€"), 0) && char.IsSeparator(Opaque(" "), 0)
            && char.IsWhiteSpace(Opaque("a\t"), 1) && char.IsControl(Opaque("\n"), 0));
        Section("strings and surrogate pairs");
    }

    static void CheckEncodingExamples()
    {
        // docs:encoding
        string text = "naïve ☕ 😀";
        byte[] utf8 = Encoding.UTF8.GetBytes(text); // allocated
        Check("UTF-8 bytes",
            utf8.Length == 15 && Encoding.UTF8.GetString(utf8) == text);

        // Into a buffer: nothing allocated.
        Span<byte> buffer = stackalloc byte[64];
        int written = Encoding.UTF8.GetBytes(text, buffer);
        Span<char> chars = stackalloc char[64];
        int read = Encoding.UTF8.GetChars(buffer.Slice(0, written), chars);
        Check("into a span and back", written == 15 && read == text.Length
            && Encoding.UTF8.GetByteCount(text) == 15);

        // What doesn't fit becomes '?' in ASCII, a close letter in Latin-1.
        byte[] ascii = Encoding.ASCII.GetBytes("naïve");
        Check("ASCII", Encoding.ASCII.GetString(ascii) == "na?ve");
        byte[] latin1 = Encoding.Latin1.GetBytes("Āb");
        Check("Latin-1", Encoding.Latin1.GetString(latin1) == "Ab");
        // docs:end

        Encoding unicode = Encoding.Unicode; // made on first use
        long before = GC.GetTotalAllocatedBytes();
        written = Encoding.UTF8.GetBytes(text, buffer);
        read = unicode.GetChars(unicode.Preamble, chars);
        bool fits = Encoding.UTF8.TryGetBytes(text, buffer.Slice(0, 3), out int none);
        Check("span overloads allocate nothing", GC.GetTotalAllocatedBytes() == before && read == 1 && chars[0] == '﻿');
        Check("TryGetBytes: not enough room", !fits && none == 0);
        Check("TryGetChars", Encoding.UTF8.TryGetChars(buffer.Slice(0, written), chars, out int count) && count == text.Length);
        Section("encoding examples");
    }

    static void CheckUtf8()
    {
        Check("encodes every length", SameBytes(Encoding.UTF8.GetBytes(Opaque("Aé€\U0001F600")),
            [0x41, 0xC3, 0xA9, 0xE2, 0x82, 0xAC, 0xF0, 0x9F, 0x98, 0x80]));
        Check("a lone surrogate becomes U+FFFD", SameBytes(Encoding.UTF8.GetBytes(Opaque("a\uD800b\uDC00")),
            [0x61, 0xEF, 0xBF, 0xBD, 0x62, 0xEF, 0xBF, 0xBD]));
        // Shorter forms, surrogates, a character cut short, bytes that can't
        // start one, above U+10FFFF: as .NET replaces them.
        byte[] invalid = [0x41, 0xE0, 0x80, 0x41, 0xED, 0xA0, 0x80, 0xF0, 0x9F, 0x98, 0x41, 0xC0, 0xAF,
            0xF4, 0x90, 0x80, 0x80, 0xE2, 0x82];
        Check("invalid bytes: one U+FFFD per maximal invalid sequence",
            Encoding.UTF8.GetString(invalid) == "A��A����A�������"
            && Encoding.UTF8.GetCharCount(invalid) == 16);
        Check("decodes a pair", Encoding.UTF8.GetString([0xF0, 0x9F, 0x98, 0x80]) == "😀");
        Check("GetBytes writes no byte order mark", Encoding.UTF8.GetBytes(Opaque("a")).Length == 1);
        Check("a part of an array", Encoding.UTF8.GetString([0x20, 0x68, 0x69, 0x20], 1, 2) == "hi"
            && Encoding.UTF8.GetByteCount(Opaque("xhéx"), 1, 2) == 3);
        byte[] into = new byte[6];
        int n = Encoding.UTF8.GetBytes(Opaque("héllo"), 0, 2, into, 3);
        Check("into an array at an index", n == 3 && into[3] == 0x68 && into[4] == 0xC3 && into[5] == 0xA9);
        char[] charsInto = new char[4];
        n = Encoding.UTF8.GetChars([0x61, 0xC3, 0xA9], 0, 3, charsInto, 1);
        Check("chars into an array at an index", n == 2 && charsInto[1] == 'a' && charsInto[2] == 'é');
        Check("GetChars and GetCharCount", Encoding.UTF8.GetChars([0xC3, 0xA9]).Length == 1
            && Encoding.UTF8.GetCharCount([0xF0, 0x9F, 0x98, 0x80]) == 2);
        Section("UTF-8");
    }

    static void CheckOtherEncodings()
    {
        Check("ASCII: '?' for each char above 0x7F", SameBytes(Encoding.ASCII.GetBytes(Opaque("a\U0001F600b\uD800c")),
            [0x61, 0x3F, 0x3F, 0x62, 0x3F, 0x63]));
        Check("ASCII: '?' for each byte above 0x7F", Encoding.ASCII.GetString([0x41, 0xFF, 0x80]) == "A??");
        Check("Latin-1: best fit, then '?'", SameBytes(Encoding.Latin1.GetBytes(Opaque("éĀ\U0001F600\uDC00")),
            [0xE9, 0x41, 0x3F, 0x3F, 0x3F]));
        Check("Latin-1: every byte is its char", Encoding.Latin1.GetString([0xE9, 0xFF, 0x00]) == "éÿ\0");
        Check("UTF-16: a lone surrogate becomes U+FFFD", SameBytes(Encoding.Unicode.GetBytes(Opaque("a\uD800")),
            [0x61, 0x00, 0xFD, 0xFF]));
        Check("UTF-16: lone surrogates and an odd byte",
            Encoding.Unicode.GetString([0x41, 0x00, 0x00, 0xD8, 0x42, 0x00, 0x99]) == "A�B�");
        Check("UTF-16 big-endian", Encoding.BigEndianUnicode.GetString([0xD8, 0x3D, 0xDE, 0x00, 0x00, 0x41]) == "😀A"
            && SameBytes(Encoding.BigEndianUnicode.GetBytes(Opaque("é")), [0x00, 0xE9]));
        Check("UTF-32: invalid values and leftover bytes", Encoding.UTF32.GetString(
            [0x41, 0, 0, 0, 0, 0xD8, 0, 0, 0, 0, 0x11, 0, 0x00, 0xF6, 0x01, 0, 0x42, 0]) == "A��😀�");
        Check("UTF-32: a lone surrogate becomes U+FFFD", SameBytes(Encoding.UTF32.GetBytes(Opaque("\uD800a")),
            [0xFD, 0xFF, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00]));
        var utf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        Check("UTF-32 big-endian", SameBytes(utf32BigEndian.GetBytes(Opaque("😀")), [0x00, 0x01, 0xF6, 0x00])
            && utf32BigEndian.GetString([0x00, 0x01, 0xF6, 0x00]) == "😀");
        byte[] latin1 = Encoding.Convert(Encoding.UTF8, Encoding.Latin1, [0xC3, 0xA9]);
        Check("Convert", latin1.Length == 1 && latin1[0] == 0xE9);
        Section("ASCII, Latin-1, UTF-16 and UTF-32");
    }

    static void CheckEncodingMembers()
    {
        Check("names", Encoding.UTF8.WebName == "utf-8" && Encoding.ASCII.WebName == "us-ascii"
            && Encoding.Latin1.WebName == "iso-8859-1" && Encoding.Unicode.WebName == "utf-16"
            && Encoding.BigEndianUnicode.WebName == "utf-16BE" && Encoding.UTF32.WebName == "utf-32"
            && Encoding.UTF8.EncodingName == "Unicode (UTF-8)" && Encoding.Latin1.EncodingName == "Western European (ISO)"
            && Encoding.UTF8.BodyName == "utf-8" && Encoding.ASCII.HeaderName == "us-ascii");
        Check("code pages", Encoding.UTF8.CodePage == 65001 && Encoding.ASCII.CodePage == 20127
            && Encoding.Latin1.CodePage == 28591 && Encoding.Unicode.CodePage == 1200
            && Encoding.BigEndianUnicode.CodePage == 1201 && Encoding.UTF32.CodePage == 12000);
        Check("preambles", SameBytes(Encoding.UTF8.GetPreamble(), [0xEF, 0xBB, 0xBF])
            && Encoding.Default.Preamble.Length == 0 && new UTF8Encoding().GetPreamble().Length == 0
            && SameBytes(Encoding.Unicode.Preamble, [0xFF, 0xFE]) && SameBytes(Encoding.BigEndianUnicode.Preamble, [0xFE, 0xFF])
            && SameBytes(Encoding.UTF32.Preamble, [0xFF, 0xFE, 0x00, 0x00]) && Encoding.ASCII.Preamble.Length == 0);
        Check("IsSingleByte", Encoding.ASCII.IsSingleByte && Encoding.Latin1.IsSingleByte && !Encoding.UTF8.IsSingleByte);
        Check("GetMaxByteCount", Encoding.UTF8.GetMaxByteCount(10) == 33 && Encoding.ASCII.GetMaxByteCount(10) == 11
            && Encoding.Unicode.GetMaxByteCount(10) == 22 && Encoding.UTF32.GetMaxByteCount(10) == 44);
        Check("GetMaxCharCount", Encoding.UTF8.GetMaxCharCount(10) == 11 && Encoding.ASCII.GetMaxCharCount(10) == 10
            && Encoding.Unicode.GetMaxCharCount(11) == 7 && Encoding.UTF32.GetMaxCharCount(10) == 7);
        Check("GetEncoding by name", Encoding.GetEncoding(Opaque("UTF-8")) == Encoding.UTF8
            && Encoding.GetEncoding(Opaque("latin1")) == Encoding.Latin1
            && Encoding.GetEncoding(Opaque("unicodeFFFE")) == Encoding.BigEndianUnicode
            && Encoding.GetEncoding(Opaque("utf-32BE")).CodePage == 12001);
        Check("GetEncoding by code page", Encoding.GetEncoding(20127) == Encoding.ASCII && Encoding.GetEncoding(1200) == Encoding.Unicode);
        Check("Equals", Encoding.UTF8.Equals(new UTF8Encoding(true)) && !Encoding.UTF8.Equals(new UTF8Encoding(false))
            && Encoding.Default.Equals(new UTF8Encoding()) && !Encoding.UTF8.Equals(Encoding.ASCII));
        Section("Encoding's members");
    }
}
