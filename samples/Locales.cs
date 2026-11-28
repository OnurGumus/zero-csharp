// Test: the built-in cultures, against .NET's answers with ICU: numbers and
// dates in German, French, Hindi and Japanese, Turkish casing (the "i"
// problem), parsing with a culture, CurrentCulture, and the table itself.
// Sorting.cs has how each sorts. scripts/cultures-check compares every
// culture in the table with .NET's.
// Exit code = number of failed checks.
// Build: ./build.sh Locales

using System;
using System.Globalization;

namespace LocalesSample;

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

    public static int Main()
    {
        // docs:numbers
        var german = CultureInfo.GetCultureInfo("de-DE");
        var hindi = CultureInfo.GetCultureInfo("hi-IN");
        Is("German", 1234567.891.ToString("N2", german), "1.234.567,89");
        Is("German euros", 1234.5m.ToString("C", german), "1.234,50 €");
        Is("Indian grouping", 12345678.ToString("N0", hindi), "1,23,45,678");
        Check("parse German", double.Parse("1.234,5", german) == 1234.5);
        // docs:end
        var french = CultureInfo.GetCultureInfo("fr-FR");
        Is("French", 1234567.5.ToString("N1", french), "1 234 567,5");
        Check("parse French with a space", double.Parse("1 234,5", french) == 1234.5);
        Is("percent", 0.25.ToString("P0", french), "25 %");
        Section("numbers");

        var day = new DateTime(2026, 3, 5, 14, 7, 9);
        var japanese = CultureInfo.GetCultureInfo("ja-JP");
        // docs:dates
        Is("German date", day.ToString("D", german), "Donnerstag, 5. März 2026");
        Is("Japanese date", day.ToString("D", japanese), "2026年3月5日 木曜日");
        Is("French short", day.ToString("d", french), "05/03/2026");
        var american = CultureInfo.GetCultureInfo("en-US");
        Is("US", day.ToString("g", american), "3/5/2026 2:07\u202FPM");
        // docs:end
        Is("Turkish full", day.ToString("F", CultureInfo.GetCultureInfo("tr-TR")), "5 Mart 2026 Perşembe 14:07:09");
        Is("month names", german.DateTimeFormat.GetMonthName(3) + " " + japanese.DateTimeFormat.GetAbbreviatedDayName(DayOfWeek.Sunday), "März 日");
        Section("dates");

        // docs:turkish
        var turkish = CultureInfo.GetCultureInfo("tr-TR");
        Is("Turkish upper case", "istanbul".ToUpper(turkish), "İSTANBUL");
        Is("Turkish lower case", "ISPARTA".ToLower(turkish), "ısparta");
        Is("invariant upper case", "istanbul".ToUpperInvariant(), "ISTANBUL");
        // docs:end
        Is("Azerbaijani too", "i".ToUpper(CultureInfo.GetCultureInfo("az-Latn-AZ")), "İ");
        Is("other cultures map İ and ı", "İ".ToLower(german) + "ı".ToUpper(german), "iI");
        Is("title case", turkish.TextInfo.ToTitleCase("istanbul ve izmir"), "İstanbul Ve İzmir");
        Is("Dutch IJ", CultureInfo.GetCultureInfo("nl-NL").TextInfo.ToTitleCase("ijsland"), "IJsland");
        Section("casing");

        // docs:current
        CultureInfo.CurrentCulture = turkish;
        Is("ToUpper() follows the current culture", "i".ToUpper(), "İ");
        Is("so does char.ToUpper", char.ToUpper('i').ToString(), "İ");
        Is("and number text", 1.5.ToString(), "1,5");
        Check("and string.Compare", string.Compare("ılık", "ilaç") < 0);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        // docs:end
        Is("back to invariant", "i".ToUpper() + 1.5.ToString(), "I1.5");
        Section("CurrentCulture");

        Check("names", german.EnglishName == "German (Germany)" && german.NativeName == "Deutsch (Deutschland)"
            && german.TwoLetterISOLanguageName == "de" && german.LCID == 1031);
        Check("parents", german.Parent.Name == "de" && german.Parent.Parent.Equals(CultureInfo.InvariantCulture));
        Check("by number", new CultureInfo(1041).Name == "ja-JP");
        Check("the same object", ReferenceEquals(CultureInfo.GetCultureInfo("de-de"), german));
        Check("new is writable", !new CultureInfo("de-DE").NumberFormat.IsReadOnly && german.NumberFormat.IsReadOnly);
        Check("the table", CultureInfo.GetCultures(CultureTypes.SpecificCultures).Length > 40);
        Check("right to left", CultureInfo.GetCultureInfo("he-IL").TextInfo.IsRightToLeft);
        Section("the table");

        Console.WriteLine(s_failures == 0 ? "\nAll checks passed." : $"\n{s_failures} checks failed.");
        return s_failures;
    }
}
