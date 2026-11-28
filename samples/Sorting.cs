// Test: sorting and searching text in each culture's order, against .NET's
// answers with ICU: Turkish dotless ı, Swedish ö after z, Czech ch, Danish aa,
// Chinese by pinyin and by strokes, Japanese, Korean hanja by their reading,
// what ignoring case and accents means per language, numbers by value
// (NumericOrdering), and the current culture's string.Compare.
// scripts/collation-check compares every culture in the table with .NET's on
// millions of random texts.
// Exit code = number of failed checks.
// Build: ./build.sh Sorting

using System;
using System.Globalization;

namespace SortingSample;

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

    static void Is(string name, string actual, string expected)
    {
        s_sectionChecks++;
        if (actual != expected)
        {
            s_failures++;
            s_sectionFailures++;
            Console.WriteLine($"  FAIL: {name}: \"{actual}\", expected \"{expected}\"");
        }
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

    // The words in the culture's order, joined by spaces (a sorted copy).
    static string Sorted(string culture, params string[] words) => Sorted(culture, CompareOptions.None, words);

    static string Sorted(string culture, CompareOptions options, params string[] words)
    {
        var compare = CultureInfo.GetCultureInfo(culture).CompareInfo;
        string[] sorted = new string[words.Length];
        for (int i = 0; i < words.Length; i++)
            sorted[i] = words[i];
        new Span<string>(sorted).Sort((a, b) => compare.Compare(a, b, options));
        string joined = sorted[0];
        for (int i = 1; i < sorted.Length; i++)
            joined = joined + " " + sorted[i];
        return joined;
    }

    public static int Main()
    {
        var turkish = CultureInfo.GetCultureInfo("tr-TR");
        var german = CultureInfo.GetCultureInfo("de-DE");
        var french = CultureInfo.GetCultureInfo("fr-FR");
        var american = CultureInfo.GetCultureInfo("en-US");

        // docs:sorting
        string[] words = { "ılık", "ilaç", "çay", "cami", "ısı", "İzmir" };
        Is("Turkish", Sorted("tr-TR", words), "cami çay ılık ısı ilaç İzmir");
        Is("English", Sorted("en-US", words), "cami çay ilaç İzmir ılık ısı");
        Is("Swedish", Sorted("sv-SE", "öl", "zon", "ost"), "ost zon öl");
        Is("German", Sorted("de-DE", "öl", "zon", "ost"), "öl ost zon");
        Is("pinyin", Sorted("zh-CN", "中国", "北京", "上海"), "北京 上海 中国");
        // docs:end
        Is("Turkish order", Sorted("tr-TR", "ılık", "ilaç", "çay", "cami", "ısı", "iğne", "umut", "Ünal", "şeker", "sabun", "göl", "gaz", "öğle", "ocak", "İzmir", "Irmak"),
            "cami çay gaz göl ılık Irmak ısı iğne ilaç İzmir ocak öğle sabun şeker umut Ünal");
        Is("German order", Sorted("de-DE", "Zebra", "Äpfel", "apfel", "Apfel", "Arzt", "Öl", "Ofen"), "apfel Apfel Äpfel Arzt Ofen Öl Zebra");
        Is("Swedish order", Sorted("sv-SE", "Zebra", "Äpfel", "apfel", "Apfel", "Arzt", "Öl", "Ofen"), "apfel Apfel Arzt Ofen Zebra Äpfel Öl");
        Is("Chinese by pinyin", Sorted("zh-CN", "中国", "北京", "上海", "广州", "深圳"), "北京 广州 上海 深圳 中国");
        Is("English order", Sorted("en-US", "ılık", "ilaç", "çay", "cami", "ısı", "iğne", "umut", "Ünal", "şeker", "sabun", "göl", "gaz", "öğle", "ocak", "İzmir", "Irmak"),
            "cami çay gaz göl iğne ilaç Irmak İzmir ılık ısı ocak öğle sabun şeker umut Ünal");
        Is("Czech ch after h", Sorted("cs-CZ", "chata", "hrad", "cesta", "čas", "ihned"), "cesta čas hrad chata ihned");
        Is("Danish aa last", Sorted("da-DK", "Aalborg", "Århus", "Zealand", "Odense", "aase", "Abe"), "Abe Odense Zealand Aalborg Århus aase");
        Is("Chinese by strokes", Sorted("zh-TW", "中国", "北京", "上海", "广州", "深圳"), "上海 广州 中国 北京 深圳");
        Is("Japanese", Sorted("ja-JP", "東京", "大阪", "京都", "とうきょう", "Tokyo"), "Tokyo とうきょう 京都 大阪 東京");
        Check("hiragana and katakana alike", CultureInfo.GetCultureInfo("ja-JP").CompareInfo.Compare("とうきょう", "トウキョウ") == 0
            && CultureInfo.GetCultureInfo("ja-JP").CompareInfo.Compare("とうきょう", "トウキョウ", CompareOptions.IgnoreCase) < 0);
        Is("Korean, hanja by reading", Sorted("ko-KR", "서울", "부산", "大邱", "인천", "Seoul"), "大邱 부산 서울 인천 Seoul");
        Is("Russian", Sorted("ru-RU", "ёлка", "ель", "яблоко", "арбуз", "Zebra"), "арбуз ёлка ель яблоко Zebra");
        Section("orders");

        // docs:ignorecase
        var ignoreCase = CompareOptions.IgnoreCase;
        Check("Turkish İ is i, ignoring case",
            string.Compare("İSTANBUL", "istanbul", turkish, ignoreCase) == 0);
        Check("but not in English",
            string.Compare("İSTANBUL", "istanbul", american, ignoreCase) != 0);
        var loose = CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreCase;
        Check("accents ignored",
            french.CompareInfo.IndexOf("Un résumé", "RESUME", loose) == 3);
        // docs:end
        Check("ı and I", string.Compare("ISPARTA", "ısparta", turkish, CompareOptions.IgnoreCase) == 0);
        Check("symbols", string.Compare("co-op", "coop", american, CompareOptions.None) < 0
            && string.Compare("co-op", "coop", american, CompareOptions.IgnoreSymbols) == 0);
        Check("ß isn't ss", !"Straße".StartsWith("Strasse", false, german) && german.CompareInfo.Compare("Straße", "Strasse", CompareOptions.IgnoreCase) > 0);
        Check("equal text hashes alike", turkish.CompareInfo.GetHashCode("İSTANBUL", CompareOptions.IgnoreCase) == turkish.CompareInfo.GetHashCode("istanbul", CompareOptions.IgnoreCase));
        Section("ignoring case, accents and symbols");

        // docs:numeric
        var numeric = CompareOptions.NumericOrdering;
        string[] files = { "file10", "file2", "File3", "file1" };
        Is("by value", Sorted("en-US", numeric, files), "file1 file2 File3 file10");
        Is("digit by digit", Sorted("en-US", CompareOptions.None, files),
            "file1 file10 file2 File3");
        Check("leading zeros don't count",
            american.CompareInfo.Compare("file02", "file2", numeric) == 0);
        // docs:end
        Check("digits of every script", american.CompareInfo.Compare("\u0661\u0662", "12", numeric) == 0);
        Check("decimals aren't numbers", american.CompareInfo.Compare("1.5", "1.10", numeric) < 0);
        Check("ignoring case too", turkish.CompareInfo.Compare("A2", "a10", numeric | CompareOptions.IgnoreCase) < 0);
        Check("the invariant culture ignores it, as .NET's invariant mode",
            CultureInfo.InvariantCulture.CompareInfo.Compare("a2", "a10", numeric) > 0);
        Section("numbers");

        CultureInfo.CurrentCulture = turkish;
        Check("string.Compare follows the current culture", string.Compare("ıa", "ib") < 0);
        Check("so does CompareTo", "kılıç".CompareTo("kilit") < 0);
        Check("and StartsWith", "İstanbul".StartsWith("istanbul", StringComparison.CurrentCultureIgnoreCase));
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Check("back to ordinal", string.Compare("a", "B") > 0);
        Section("current culture");

        Console.WriteLine(s_failures == 0 ? "\nAll checks passed." : $"\n{s_failures} checks failed.");
        return s_failures;
    }
}
