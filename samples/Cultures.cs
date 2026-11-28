// Test: CultureInfo and NumberFormatInfo, as .NET has them with
// InvariantGlobalization set. The invariant culture is the only one: code
// that passes CultureInfo.InvariantCulture or CurrentCulture compiles and
// formats and parses as .NET does in the invariant culture, and asking for
// any other culture throws CultureNotFoundException with .NET's message.
// Every expected value here is what .NET 10 gives in that mode.
// Exit code = number of failed checks.
// Build: ./build.sh Cultures

using System;
using System.Globalization;

namespace CulturesSample;

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

    // Runs body and returns the message of what it threw, or null.
    static string Thrown(Action body)
    {
        Exception e = Catch.Run(body);
        return e?.Message;
    }

    const string InvariantOnly = "Only the invariant culture is supported in globalization-invariant mode. See https://aka.ms/GlobalizationInvariantMode for more information.";

    // A provider of the program's own: formatting and parsing ask it nothing
    // and use the invariant culture.
    sealed class MyProvider : IFormatProvider
    {
        public object GetFormat(Type formatType) => null;
    }

    public static int Main()
    {
        // docs:invariant
        CultureInfo invariant = CultureInfo.InvariantCulture;
        Check("format", 1234.5.ToString("N1", invariant) == "1,234.5");
        Check("parse", double.Parse("1,234.5", invariant) == 1234.5);
        Check("string.Format",
            string.Format(invariant, "{0:F2}", 2.5) == "2.50");
        Check("the current culture is the invariant one",
            CultureInfo.CurrentCulture.Name == ""
            && CultureInfo.CurrentCulture.Equals(invariant));
        Check("its numbers",
            NumberFormatInfo.CurrentInfo.NumberDecimalSeparator == "."
            && invariant.NumberFormat.NumberGroupSeparator == ",");
        // docs:end

        Span<char> text = stackalloc char[16];
        Check("TryFormat", 42.TryFormat(text, out int written, "D5", invariant)
            && text.Slice(0, written).SequenceEqual("00042"));
        Check("TryParse", int.TryParse("-7", NumberStyles.Integer, invariant, out int seven) && seven == -7);
        Check("a provider of your own", 0.5.ToString(new MyProvider()) == "0.5"
            && float.Parse("0.25", new MyProvider()) == 0.25f);
        Check("a NumberFormatInfo as provider", 3.ToString("N0", NumberFormatInfo.InvariantInfo) == "3");
        Section("formatting and parsing");

        Check("Name", invariant.Name == "" && invariant.ToString() == "" && invariant.IetfLanguageTag == "");
        Check("names", invariant.DisplayName == "Invariant Language (Invariant Country)"
            && invariant.EnglishName == "Invariant Language (Invariant Country)"
            && invariant.NativeName == "Invariant Language (Invariant Country)");
        Check("ISO names", invariant.TwoLetterISOLanguageName == "iv"
            && invariant.ThreeLetterISOLanguageName == "ivl"
            && invariant.ThreeLetterWindowsLanguageName == "IVL");
        Check("LCID", invariant.LCID == 127 && invariant.KeyboardLayoutId == 127);
        Check("not neutral", !invariant.IsNeutralCulture);
        Check("read-only", invariant.IsReadOnly && !invariant.UseUserOverride);
        Check("its own parent", ReferenceEquals(invariant.Parent, invariant));
        Check("the same object each time", ReferenceEquals(CultureInfo.InvariantCulture, invariant));
        Check("current UI and installed", ReferenceEquals(CultureInfo.CurrentUICulture, invariant)
            && ReferenceEquals(CultureInfo.InstalledUICulture, invariant));
        Check("no default for threads", CultureInfo.DefaultThreadCurrentCulture == null
            && CultureInfo.DefaultThreadCurrentUICulture == null);
        Section("the invariant culture");

        CultureInfo made = new CultureInfo("");
        Check("new CultureInfo(\"\") is writable", !made.IsReadOnly && !made.UseUserOverride);
        Check("and equal", made.Equals(invariant) && made.GetHashCode() == invariant.GetHashCode());
        Check("by number", new CultureInfo(127).Name == "" && CultureInfo.GetCultureInfo(127).IsReadOnly);
        Check("GetCultureInfo(\"\")", CultureInfo.GetCultureInfo("").IsReadOnly
            && CultureInfo.GetCultureInfo("").Name == "");
        Check("ReadOnly", CultureInfo.ReadOnly(made).IsReadOnly && ReferenceEquals(CultureInfo.ReadOnly(invariant), invariant));
        Check("Clone is writable", !((CultureInfo)invariant.Clone()).IsReadOnly);
        Check("not equal to null", !invariant.Equals(null));
        Check("GetFormat", ReferenceEquals(invariant.GetFormat(typeof(NumberFormatInfo)), invariant.NumberFormat)
            && invariant.GetFormat(typeof(string)) == null);

        CultureInfo.CurrentCulture = made;
        Check("set CurrentCulture", ReferenceEquals(CultureInfo.CurrentCulture, made));
        CultureInfo.CurrentCulture = invariant;
        Check("set it to null", Thrown(() => CultureInfo.CurrentCulture = null) != null);
        Section("making cultures");

        // docs:other
        string message = Thrown(() => new CultureInfo("de-DE"));
        Check("another culture throws", message == InvariantOnly
            + " (Parameter 'name')\nde-DE is an invalid culture identifier.");
        // docs:end
        Check("GetCultureInfo lowercases the name", Thrown(() => CultureInfo.GetCultureInfo("en-US"))
            == InvariantOnly + " (Parameter 'name')\nen-us is an invalid culture identifier.");
        Check("only ASCII", Thrown(() => CultureInfo.GetCultureInfo("\u00C4B"))
            == InvariantOnly + " (Parameter 'name')\n\u00C4b is an invalid culture identifier.");
        Check("by number", Thrown(() => new CultureInfo(2748))
            == InvariantOnly + " (Parameter 'culture')\n2748 (0x0abc) is an invalid culture identifier.");
        Check("never a culture", Thrown(() => new CultureInfo(0x0400))
            == "Culture is not supported. (Parameter 'culture')\n1024 (0x0400) is an invalid culture identifier.");
        Check("GetCultureInfo(int) checks differently", Thrown(() => CultureInfo.GetCultureInfo(0x0400))
            == InvariantOnly + " (Parameter 'culture')\n1024 (0x0400) is an invalid culture identifier.");
        Check("negative", Thrown(() => new CultureInfo(-1)) != null && Thrown(() => CultureInfo.GetCultureInfo(0)) != null);
        Check("null", Thrown(() => new CultureInfo((string)null)) != null);
        Exception caught = Catch.Run(() => new CultureInfo("fr"));
        Check("its type", caught is CultureNotFoundException notFound && notFound.InvalidCultureName == "fr"
            && notFound.InvalidCultureId == null && caught is ArgumentException);
        Check("its own constructors", new CultureNotFoundException().Message == "Culture is not supported."
            && new CultureNotFoundException("p", "nm", "msg").Message == "msg (Parameter 'p')\nnm is an invalid culture identifier.");
        Section("other cultures");

        NumberFormatInfo nfi = NumberFormatInfo.InvariantInfo;
        Check("one NumberFormatInfo", ReferenceEquals(nfi, invariant.NumberFormat)
            && ReferenceEquals(NumberFormatInfo.CurrentInfo, nfi) && nfi.IsReadOnly);
        Check("GetInstance", ReferenceEquals(NumberFormatInfo.GetInstance(null), nfi)
            && ReferenceEquals(NumberFormatInfo.GetInstance(invariant), nfi)
            && ReferenceEquals(NumberFormatInfo.GetInstance(new MyProvider()), nfi)
            && ReferenceEquals(NumberFormatInfo.ReadOnly(nfi), nfi));
        Check("signs", nfi.NegativeSign == "-" && nfi.PositiveSign == "+");
        Check("symbols", nfi.NaNSymbol == "NaN" && nfi.PositiveInfinitySymbol == "Infinity"
            && nfi.NegativeInfinitySymbol == "-Infinity");
        Check("numbers", nfi.NumberDecimalSeparator == "." && nfi.NumberGroupSeparator == ","
            && nfi.NumberDecimalDigits == 2 && nfi.NumberNegativePattern == 1);
        Check("currency", nfi.CurrencySymbol == "\u00A4" && nfi.CurrencyDecimalSeparator == "."
            && nfi.CurrencyGroupSeparator == "," && nfi.CurrencyDecimalDigits == 2
            && nfi.CurrencyPositivePattern == 0 && nfi.CurrencyNegativePattern == 0);
        Check("percent", nfi.PercentSymbol == "%" && nfi.PerMilleSymbol == "\u2030"
            && nfi.PercentDecimalSeparator == "." && nfi.PercentGroupSeparator == ","
            && nfi.PercentDecimalDigits == 2 && nfi.PercentPositivePattern == 0 && nfi.PercentNegativePattern == 0);
        Check("groups of 3", nfi.NumberGroupSizes.Length == 1 && nfi.NumberGroupSizes[0] == 3
            && nfi.CurrencyGroupSizes[0] == 3 && nfi.PercentGroupSizes[0] == 3);
        Check("digits", nfi.NativeDigits.Length == 10 && nfi.NativeDigits[7] == "7"
            && nfi.DigitSubstitution == DigitShapes.None);
        Section("NumberFormatInfo");

        Console.WriteLine(s_failures == 0 ? "\nAll checks passed." : $"\n{s_failures} checks failed.");
        return s_failures;
    }
}
