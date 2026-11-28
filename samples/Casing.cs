// Test: string casing and comparing, against .NET's answers in invariant
// mode: ToUpper/ToLower (every character, surrogate pairs too), ToTitleCase,
// the StringComparison and CultureInfo overloads of Equals, Compare,
// StartsWith, IndexOf and the rest, CompareInfo, the span versions that
// allocate nothing, and Rune. scripts/text-check compares the code with
// .NET's over every code point and millions of strings.
// Exit code = number of failed checks.
// Build: ./build.sh Casing

using System;
using System.Globalization;
using System.Text;

namespace CasingSample;

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

    public static int Main()
    {
        // docs:casing
        Check("ToUpper", "Grüße, Ελλάδα!".ToUpper() == "GRÜßE, ΕΛΛΆΔΑ!");
        Check("ToLower", "ÇAĞRI".ToLowerInvariant() == "çağri");
        Check("one char to one: ß stays", "ß".ToUpperInvariant() == "ß");
        Check("surrogate pairs", "𐐨".ToUpperInvariant() == "𐐀");
        string done = "ALREADY";
        Check("unchanged is the same string",
            ReferenceEquals(done.ToUpperInvariant(), done));
        // docs:end
        // The invariant culture has no Turkish rules: i and I pair up, and
        // İ and ı have no other case.
        Check("invariant i", "i".ToUpper() == "I" && "I".ToLower() == "i"
            && "İ".ToLowerInvariant() == "İ" && "ı".ToUpperInvariant() == "ı");
        Check("ToUpper(culture)", "abc".ToUpper(CultureInfo.InvariantCulture) == "ABC");
        TextInfo text = CultureInfo.InvariantCulture.TextInfo;
        // docs:title
        Check("title case", text.ToTitleCase("the lord of the rings")
            == "The Lord Of The Rings");
        Check("acronyms stay",
            text.ToTitleCase("NASA and the ESA") == "NASA And The ESA");
        Check("apostrophes", text.ToTitleCase("it's o'neil") == "It's O'neil");
        // docs:end
        Check("digraphs", text.ToTitleCase("ǆemal") == "ǅemal");
        Check("TextInfo char", text.ToUpper('é') == 'É' && text.ToLower('Ж') == 'ж');
        Section("casing");

        // docs:compare
        Check("Equals ignoring case", string.Equals("README.md", "readme.MD",
            StringComparison.OrdinalIgnoreCase));
        Check("StartsWith", "Content-Type: text/html"
            .StartsWith("content-type", StringComparison.OrdinalIgnoreCase));
        Check("IndexOf", "Hello World"
            .IndexOf("WORLD", StringComparison.OrdinalIgnoreCase) == 6);
        Check("Contains",
            "Zero C#".Contains("c#", StringComparison.CurrentCultureIgnoreCase));
        Check("Compare ignoring case", string.Compare("apple", "APPLE",
            StringComparison.InvariantCultureIgnoreCase) == 0);
        // The culture compares by character code, as .NET's invariant mode.
        Check("capitals first", string.Compare("Banana", "apple") < 0);
        // docs:end
        Check("Compare ordinal", string.Compare("a", "b", StringComparison.Ordinal) < 0 && string.CompareOrdinal("b", "a") > 0);
        Check("Compare with null", string.Compare(null, "a") < 0 && string.Compare("a", null, StringComparison.Ordinal) > 0);
        Check("Compare(bool)", string.Compare("x", "X", ignoreCase: true) == 0);
        Check("Compare substrings", string.Compare("xHELLO", 1, "hello!", 0, 5, StringComparison.OrdinalIgnoreCase) == 0);
        Check("EndsWith", "photo.JPG".EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
        Check("LastIndexOf", "a.b.c".LastIndexOf(".", StringComparison.Ordinal) == 3);
        Check("IndexOf(char)", "ABC".IndexOf('b', StringComparison.OrdinalIgnoreCase) == 1);
        Check("GetHashCode ignoring case",
            "Key".GetHashCode(StringComparison.OrdinalIgnoreCase) == "KEY".GetHashCode(StringComparison.OrdinalIgnoreCase));
        CompareInfo compare = CultureInfo.InvariantCulture.CompareInfo;
        Check("CompareInfo", compare.Compare("abc", "ABD", CompareOptions.IgnoreCase) < 0
            && compare.IsPrefix("Prefix", "pre", CompareOptions.IgnoreCase)
            && compare.IndexOf("aXbX", "x", CompareOptions.IgnoreCase) == 1);
        Check("CompareInfo.LastIndexOf", compare.LastIndexOf("aXbX", "x", CompareOptions.OrdinalIgnoreCase) == 3);
        Section("comparing");

        // docs:spans
        Span<char> buffer = stackalloc char[32];
        ReadOnlySpan<char> word = "straße";
        int length = word.ToUpperInvariant(buffer);
        Check("into a buffer", buffer.Slice(0, length).SequenceEqual("STRAßE"));
        Check("too small", word.ToUpperInvariant(buffer.Slice(0, 3)) == -1);
        Check("span comparison",
            word.Equals("STRASSE", StringComparison.OrdinalIgnoreCase) == false
            && word.StartsWith("STR", StringComparison.OrdinalIgnoreCase));
        // docs:end
        Check("span IndexOf", "ab-AB".AsSpan().IndexOf("ab", StringComparison.Ordinal) == 0
            && "ab-AB".AsSpan().LastIndexOf("ab", StringComparison.OrdinalIgnoreCase) == 3);
        Check("span CompareTo", "a".AsSpan().CompareTo("B", StringComparison.OrdinalIgnoreCase) < 0);
        Check("span ToLower", "ÀÉ𐐀".AsSpan().ToLowerInvariant(buffer) == 4 && buffer.Slice(0, 4).SequenceEqual("àé𐐨"));
        Section("spans");

        Rune deseret = Rune.GetRuneAt("x𐐨", 1);
        Check("Rune", deseret.Value == 0x10428 && deseret.Utf16SequenceLength == 2 && !deseret.IsBmp);
        Check("Rune casing", Rune.ToUpperInvariant(deseret).Value == 0x10400);
        Check("Rune classes", Rune.IsLetter(deseret) && Rune.IsLower(deseret) && Rune.IsDigit(new Rune('7')));
        Check("Rune decoding", Rune.DecodeFromUtf16("😀!", out Rune smile, out int used) == System.Buffers.OperationStatus.Done
            && smile.Value == 0x1F600 && used == 2);
        Check("Rune to text", new Rune(0x1F600).ToString() == "😀");
        Section("Rune");

        Console.WriteLine(s_failures == 0 ? "\nAll checks passed." : $"\n{s_failures} checks failed.");
        return s_failures;
    }
}
