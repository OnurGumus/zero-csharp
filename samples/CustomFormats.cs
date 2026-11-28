// Test: numbers and dates written and read with formats of your own, against
// .NET's answers: a NumberFormatInfo with its own separators, signs, group
// sizes and currency and percent patterns, a DateTimeFormatInfo with its own
// names and patterns, passed to ToString and Parse or made current through
// a writable CultureInfo. scripts/culture-check and scripts/datetime-check
// compare the code with .NET's over millions of values.
// Exit code = number of failed checks.
// Build: ./build.sh CustomFormats

using System;
using System.Globalization;

namespace CustomFormatsSample;

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
        var turkish = new NumberFormatInfo
        {
            NumberDecimalSeparator = ",",
            NumberGroupSeparator = ".",
            CurrencySymbol = "₺",
            CurrencyDecimalSeparator = ",",
            CurrencyGroupSeparator = ".",
            CurrencyPositivePattern = 2,
            CurrencyNegativePattern = 1,
        };
        Is("N", 1234567.891.ToString("N2", turkish), "1.234.567,89");
        Is("C", 1234.5m.ToString("C", turkish), "₺ 1.234,50");
        Is("negative C", (-5m).ToString("C", turkish), "-₺5,00");
        Check("Parse", double.Parse("1.234,5", NumberStyles.Number, turkish) == 1234.5);
        // docs:end
        Is("F with its digits", 2.5.ToString("F", turkish), "2,50");
        Is("E", 12345.678.ToString("E2", turkish), "1,23E+004");
        Is("G", 0.5.ToString(turkish), "0,5");
        Check("TryParse a currency",
            double.TryParse("₺ 7,25", NumberStyles.Currency, turkish, out double price) && price == 7.25);
        Check("integer with groups", int.Parse("1.000.000", NumberStyles.Number, turkish) == 1000000);
        Check("the point is now a group separator", !double.TryParse("1,5.5", NumberStyles.Float, turkish, out _));

        var indian = new NumberFormatInfo { NumberGroupSizes = new[] { 3, 2 } };
        Is("group sizes 3, 2", 123456789.ToString("N0", indian), "12,34,56,789");
        var percent = new NumberFormatInfo { PercentPositivePattern = 1, PercentNegativePattern = 2, PercentDecimalDigits = 1 };
        Is("percent patterns", 0.125.ToString("P", percent) + " " + (-0.5).ToString("P", percent), "12.5% -%50.0");
        var signs = new NumberFormatInfo { NegativeSign = "−", NaNSymbol = "-", PositiveInfinitySymbol = "∞" };
        Is("a Unicode minus", (-3).ToString(signs) + " " + (-1.5).ToString(signs), "−3 −1.5");
        Check("parses its minus and a hyphen", int.Parse("−7", signs) == -7 && int.Parse("-7", signs) == -7);
        Is("symbols", double.NaN.ToString(signs) + " " + double.PositiveInfinity.ToString(signs), "- ∞");
        Span<char> buffer = stackalloc char[32];
        Check("TryFormat", 1234.5.TryFormat(buffer, out int written, "N2", turkish)
            && buffer.Slice(0, written).SequenceEqual("1.234,50"));
        Section("NumberFormatInfo");

        // docs:culture
        CultureInfo mine = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        mine.NumberFormat.NumberDecimalSeparator = ",";
        mine.DateTimeFormat.ShortDatePattern = "dd.MM.yyyy";
        CultureInfo.CurrentCulture = mine;
        Is("ToString follows the current culture", 3.25.ToString(), "3,25");
        Is("so does a date", new DateTime(2026, 11, 28).ToString("d"), "28.11.2026");
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Is("and back", 3.25.ToString(), "3.25");
        // docs:end
        Check("ReadOnly", NumberFormatInfo.ReadOnly(turkish).IsReadOnly && !turkish.IsReadOnly
            && CultureInfo.InvariantCulture.NumberFormat.IsReadOnly && !mine.NumberFormat.IsReadOnly);
        Section("CultureInfo");

        // docs:dates
        string[] months = { "Januar", "Februar", "März", "April", "Mai", "Juni",
            "Juli", "August", "September", "Oktober", "November", "Dezember", "" };
        var german = new DateTimeFormatInfo
        {
            ShortDatePattern = "dd.MM.yyyy",
            LongTimePattern = "HH:mm:ss",
            DayNames = new[] { "Sonntag", "Montag", "Dienstag", "Mittwoch",
                "Donnerstag", "Freitag", "Samstag" },
            // Next to a day number .NET uses the genitive names: set both.
            MonthNames = months,
            MonthGenitiveNames = months,
        };
        var day = new DateTime(2026, 3, 5, 14, 7, 9);
        Is("d", day.ToString("d", german), "05.03.2026");
        Is("names", day.ToString("dddd, d. MMMM yyyy", german),
            "Donnerstag, 5. März 2026");
        // docs:end
        Is("G", day.ToString("G", german), "05.03.2026 14:07:09");
        Is("full pattern follows the long ones", day.ToString("F", german), "Donnerstag, 05 März 2026 14:07:09");
        var ampm = new DateTimeFormatInfo { AMDesignator = "vorm.", PMDesignator = "nachm.", TimeSeparator = "." };
        Is("designators and separators", day.ToString("h:mm tt", ampm), "2.07 nachm.");
        Is("o stays invariant", day.ToString("o", german), "2026-03-05T14:07:09.0000000");
        Is("DateTimeOffset", new DateTimeOffset(day, TimeSpan.FromHours(1)).ToString(german), "05.03.2026 14:07:09 +01:00");
        Is("DateOnly", new DateOnly(2026, 12, 24).ToString(german), "24.12.2026");
        Is("TimeOnly", new TimeOnly(9, 5).ToString("T", german), "09:05:00");
        var genitive = new DateTimeFormatInfo
        {
            MonthGenitiveNames = new[] { "stycznia", "lutego", "marca", "kwietnia", "maja", "czerwca", "lipca",
                "sierpnia", "września", "października", "listopada", "grudnia", "" },
        };
        Is("genitive month next to a day", day.ToString("d MMMM", genitive) + " | " + day.ToString("MMMM yyyy", genitive), "5 marca | March 2026");
        Section("DateTimeFormatInfo");

        Console.WriteLine(s_failures == 0 ? "\nAll checks passed." : $"\n{s_failures} checks failed.");
        return s_failures;
    }
}
