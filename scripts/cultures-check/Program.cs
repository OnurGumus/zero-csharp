// Compares zerocore's built-in cultures (namespace ZeroTest here) with
// .NET's with ICU, culture by culture: properties, NumberFormatInfo,
// DateTimeFormatInfo and TextInfo, then random numbers and dates formatted
// and parsed with the culture, and text cased with it.
// Usage: check-cultures.sh [iterations] [seed]
using System;
using System.Linq;
using System.Text;
using G = System.Globalization;
using Z = ZeroTest;

static unsafe class Program
{
    static Random R = new Random(12345);
    static int failures;
    static long checks;
    static int shown;
    static readonly System.Collections.Generic.Dictionary<string, int> perLabel = new();

    static string Run(Func<string> f)
    {
        try { return f(); }
        catch (Exception e) { return "!" + e.GetType().Name + ": " + e.Message; }
    }

    static void Same(string label, string input, Func<string> zero, Func<string> net)
    {
        checks++;
        string a = Run(zero), b = Run(net);
        if (a == b)
            return;
        failures++;
        perLabel.TryGetValue(label, out int n);
        perLabel[label] = n + 1;
        if (n < 3 && shown < 300)
        {
            shown++;
            Console.WriteLine($"FAIL {label} [{Escape(input)}]\n  zero: {Escape(a)}\n  .NET: {Escape(b)}");
        }
    }

    static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            sb.Append(c >= 0x20 && c < 0x7F ? c.ToString() : $"\\u{(int)c:X4}");
        return sb.ToString();
    }

    static string J(string[] items) => string.Join("|", items);
    static string J(int[] items) => string.Join(",", items);

    static readonly string[] DateFormats =
    {
        "d", "D", "f", "F", "g", "G", "m", "M", "o", "r", "s", "t", "T", "u", "y", "Y", "", null,
        "dddd, d MMMM yyyy", "ddd d MMM", "MMMM", "MMM", "d MMMM", "dd MMMM yyyy", "hh:mm tt", "h tt", "HH:mm:ss", "g yyyy", "yy/M/d",
        "dddd", "tt", "t", "%t", "M/d", "MMMM yyyy", "d. MMMM", "zzz", "K",
    };

    static readonly string[] NumberFormats = { "N", "N0", "N3", "C", "C0", "P", "P1", "F", "F2", "E", "E3", "G", "G4", "D", "D6", "", null, "R" };

    static readonly string[] TextPieces =
    {
        "i", "I", "\u0130", "\u0131", "istanbul", "ISTANBUL", "ij", "IJ", "ijsland", "\u00DF", "\u03C3", "\u03A3", "\u0436", "Hello World",
        " ", "'", "-", "a", "Z", "\u00E9", "\u00C9", "\u10428", "\u01C5", "DOG's", "o'neil",
    };

    static string Text()
    {
        var sb = new StringBuilder();
        int n = R.Next(1, 6);
        for (int i = 0; i < n; i++)
            sb.Append(TextPieces[R.Next(TextPieces.Length)]);
        return sb.ToString();
    }

    static double AnyDouble() => R.Next(5) switch
    {
        0 => (R.NextDouble() - 0.5) * Math.Pow(10, R.Next(-6, 15)),
        1 => R.Next(-100000, 100000) / 100.0,
        2 => R.Next(4) switch { 0 => double.NaN, 1 => double.PositiveInfinity, 2 => double.NegativeInfinity, _ => -0.0 },
        3 => Math.Round((R.NextDouble() - 0.5) * 2e9, R.Next(0, 4)),
        _ => R.Next(-1000, 1000),
    };

    public static int Main(string[] args)
    {
        int iterations = args.Length > 0 ? int.Parse(args[0]) : 2000;
        R = new Random(args.Length > 1 ? int.Parse(args[1]) : 12345);
        G.CultureInfo.CurrentCulture = G.CultureInfo.InvariantCulture;
        G.CultureInfo.CurrentUICulture = G.CultureInfo.InvariantCulture;

        for (int row = 0; row < Z.CultureData.Count; row++)
        {
            string name = Z.CultureData.Text(row, Z.CultureData.Name);
            var z = Z.CultureInfo.GetCultureInfo(name);
            var s = G.CultureInfo.GetCultureInfo(name);

            Same("culture", name, () => $"{z.Name}|{z.EnglishName}|{z.NativeName}|{z.TwoLetterISOLanguageName}|{z.ThreeLetterISOLanguageName}|{z.ThreeLetterWindowsLanguageName}|{z.IetfLanguageTag}|{z.LCID}|{z.KeyboardLayoutId}|{z.IsNeutralCulture}|{z.Parent.Name}|{z.ToString()}|{z.IsReadOnly}|{z.UseUserOverride}",
                () => $"{s.Name}|{s.EnglishName}|{s.NativeName}|{s.TwoLetterISOLanguageName}|{s.ThreeLetterISOLanguageName}|{s.ThreeLetterWindowsLanguageName}|{s.IetfLanguageTag}|{s.LCID}|{s.KeyboardLayoutId}|{s.IsNeutralCulture}|{s.Parent.Name}|{s.ToString()}|{s.IsReadOnly}|{s.UseUserOverride}");
            // DisplayName follows CurrentUICulture: with an English UI it is the
            // English name, which zerocore gives (under the invariant UI, ICU
            // writes "zh (CN)" for a few Chinese cultures), so it isn't compared.
            Same("TextInfo", name, () => $"{z.TextInfo.CultureName}|{z.TextInfo.LCID}|{z.TextInfo.ListSeparator}|{z.TextInfo.ANSICodePage}|{z.TextInfo.OEMCodePage}|{z.TextInfo.MacCodePage}|{z.TextInfo.EBCDICCodePage}|{z.TextInfo.IsRightToLeft}|{z.TextInfo}",
                () => $"{s.TextInfo.CultureName}|{s.TextInfo.LCID}|{s.TextInfo.ListSeparator}|{s.TextInfo.ANSICodePage}|{s.TextInfo.OEMCodePage}|{s.TextInfo.MacCodePage}|{s.TextInfo.EBCDICCodePage}|{s.TextInfo.IsRightToLeft}|{s.TextInfo}");
            Same("CompareInfo", name, () => $"{z.CompareInfo.Name}|{z.CompareInfo.LCID}|{z.CompareInfo}", () => $"{s.CompareInfo.Name}|{s.CompareInfo.LCID}|{s.CompareInfo}");
            var zn = z.NumberFormat;
            var sn = s.NumberFormat;
            Same("NumberFormat", name,
                () => $"{zn.NegativeSign}|{zn.PositiveSign}|{zn.NaNSymbol}|{zn.PositiveInfinitySymbol}|{zn.NegativeInfinitySymbol}|{zn.NumberDecimalSeparator}|{zn.NumberGroupSeparator}|{zn.NumberDecimalDigits}|{zn.NumberNegativePattern}|{J(zn.NumberGroupSizes)}|{zn.CurrencySymbol}|{zn.CurrencyDecimalSeparator}|{zn.CurrencyGroupSeparator}|{zn.CurrencyDecimalDigits}|{zn.CurrencyPositivePattern}|{zn.CurrencyNegativePattern}|{J(zn.CurrencyGroupSizes)}|{zn.PercentSymbol}|{zn.PerMilleSymbol}|{zn.PercentDecimalSeparator}|{zn.PercentGroupSeparator}|{zn.PercentDecimalDigits}|{zn.PercentPositivePattern}|{zn.PercentNegativePattern}|{J(zn.PercentGroupSizes)}|{(int)zn.DigitSubstitution}|{J(zn.NativeDigits)}|{zn.IsReadOnly}",
                () => $"{sn.NegativeSign}|{sn.PositiveSign}|{sn.NaNSymbol}|{sn.PositiveInfinitySymbol}|{sn.NegativeInfinitySymbol}|{sn.NumberDecimalSeparator}|{sn.NumberGroupSeparator}|{sn.NumberDecimalDigits}|{sn.NumberNegativePattern}|{J(sn.NumberGroupSizes)}|{sn.CurrencySymbol}|{sn.CurrencyDecimalSeparator}|{sn.CurrencyGroupSeparator}|{sn.CurrencyDecimalDigits}|{sn.CurrencyPositivePattern}|{sn.CurrencyNegativePattern}|{J(sn.CurrencyGroupSizes)}|{sn.PercentSymbol}|{sn.PerMilleSymbol}|{sn.PercentDecimalSeparator}|{sn.PercentGroupSeparator}|{sn.PercentDecimalDigits}|{sn.PercentPositivePattern}|{sn.PercentNegativePattern}|{J(sn.PercentGroupSizes)}|{(int)sn.DigitSubstitution}|{J(sn.NativeDigits)}|{sn.IsReadOnly}");
            var zd = z.DateTimeFormat;
            var sd = s.DateTimeFormat;
            Same("DateTimeFormat", name,
                () => $"{zd.AMDesignator}|{zd.PMDesignator}|{zd.DateSeparator}|{zd.TimeSeparator}|{zd.FirstDayOfWeek}|{zd.CalendarWeekRule}|{zd.FullDateTimePattern}|{zd.LongDatePattern}|{zd.LongTimePattern}|{zd.ShortDatePattern}|{zd.ShortTimePattern}|{zd.MonthDayPattern}|{zd.YearMonthPattern}|{zd.RFC1123Pattern}|{zd.SortableDateTimePattern}|{zd.UniversalSortableDateTimePattern}|{zd.NativeCalendarName}|{zd.GetEraName(1)}|{zd.GetAbbreviatedEraName(1)}|{zd.IsReadOnly}",
                () => $"{sd.AMDesignator}|{sd.PMDesignator}|{sd.DateSeparator}|{sd.TimeSeparator}|{sd.FirstDayOfWeek}|{sd.CalendarWeekRule}|{sd.FullDateTimePattern}|{sd.LongDatePattern}|{sd.LongTimePattern}|{sd.ShortDatePattern}|{sd.ShortTimePattern}|{sd.MonthDayPattern}|{sd.YearMonthPattern}|{sd.RFC1123Pattern}|{sd.SortableDateTimePattern}|{sd.UniversalSortableDateTimePattern}|{sd.NativeCalendarName}|{sd.GetEraName(1)}|{sd.GetAbbreviatedEraName(1)}|{sd.IsReadOnly}");
            Same("names", name, () => $"{J(zd.DayNames)}#{J(zd.AbbreviatedDayNames)}#{J(zd.ShortestDayNames)}#{J(zd.MonthNames)}#{J(zd.AbbreviatedMonthNames)}#{J(zd.MonthGenitiveNames)}#{J(zd.AbbreviatedMonthGenitiveNames)}#{zd.GetDayName(Z.DayOfWeek.Monday)}#{zd.GetMonthName(3)}",
                () => $"{J(sd.DayNames)}#{J(sd.AbbreviatedDayNames)}#{J(sd.ShortestDayNames)}#{J(sd.MonthNames)}#{J(sd.AbbreviatedMonthNames)}#{J(sd.MonthGenitiveNames)}#{J(sd.AbbreviatedMonthGenitiveNames)}#{sd.GetDayName(DayOfWeek.Monday)}#{sd.GetMonthName(3)}");
            foreach (char c in "dDfFgGmMoOrRstTuUyY")
                Same("GetAllDateTimePatterns", name + " " + c, () => J(zd.GetAllDateTimePatterns(c)), () => J(sd.GetAllDateTimePatterns(c)));

            // A writable copy, as new CultureInfo(name) makes.
            var zw = new Z.CultureInfo(name);
            var sw = new G.CultureInfo(name);
            Same("new CultureInfo", name, () => $"{zw.IsReadOnly}|{zw.NumberFormat.IsReadOnly}|{zw.DateTimeFormat.IsReadOnly}|{zw.TextInfo.IsReadOnly}|{zw.UseUserOverride}|{zw.Equals(z)}|{zw.NumberFormat.CurrencySymbol}",
                () => $"{sw.IsReadOnly}|{sw.NumberFormat.IsReadOnly}|{sw.DateTimeFormat.IsReadOnly}|{sw.TextInfo.IsReadOnly}|{sw.UseUserOverride}|{sw.Equals(s)}|{sw.NumberFormat.CurrencySymbol}");

            for (int it = 0; it < iterations; it++)
            {
                // Numbers.
                string nf = NumberFormats[R.Next(NumberFormats.Length)];
                double d = AnyDouble();
                if (nf == null || !nf.StartsWith("D"))
                    Same("double", name + " " + d.ToString("R") + " " + nf, () => ZeroDouble(d, nf, z), () => d.ToString(nf, s));
                long l = R.Next(4) == 0 ? R.NextInt64(long.MinValue, long.MaxValue) : R.Next(-1000000, 1000000);
                Same("long", name + " " + l + " " + nf, () => ZeroLong(l, nf, z), () => l.ToString(nf, s));
                string text = R.Next(2) == 0 ? d.ToString(nf == null || nf.StartsWith("D") ? "N" : nf, s) : l.ToString(nf, s);
                var style = R.Next(3) switch { 0 => G.NumberStyles.Any, 1 => G.NumberStyles.Number, _ => G.NumberStyles.Float | G.NumberStyles.AllowThousands };
                Same("parse double", name + " \"" + text + "\" " + style,
                    () => Z.Number.TryParseFloat(text, style, Z.Number.DoubleFormat, z, out ulong bits) + " " + BitConverter.UInt64BitsToDouble(bits).ToString("R"),
                    () => double.TryParse(text, style, s, out double v) + " " + v.ToString("R"));

                // Dates.
                long ticks = R.NextInt64(0, 3155378975999999999);
                var zdt = new Z.DateTime(ticks, Z.DateTimeKind.Utc);
                var sdt = new DateTime(ticks, DateTimeKind.Utc);
                string df = DateFormats[R.Next(DateFormats.Length)];
                Same("date", name + " " + ticks + " \"" + df + "\"", () => zdt.ToString(df, z), () => sdt.ToString(df, s));
                var zo = new Z.DateTimeOffset(ticks, new Z.TimeSpan(R.Next(-10, 10) * 36_000_000_000L));
                var so = new DateTimeOffset(ticks, new TimeSpan(zo.Offset.Ticks));
                if (df != "U")
                    Same("offset", name + " " + ticks + " \"" + df + "\"", () => zo.ToString(df, z), () => so.ToString(df, s));
                Same("DateOnly/TimeOnly", name + " " + ticks, () => Z.DateOnly.FromDateTime(zdt).ToString(z) + " " + Z.TimeOnly.FromDateTime(zdt).ToString(z) + " " + Z.DateOnly.FromDateTime(zdt).ToLongDateString(),
                    () => DateOnly.FromDateTime(sdt).ToString(s) + " " + TimeOnly.FromDateTime(sdt).ToString(s) + " " + DateOnly.FromDateTime(sdt).ToLongDateString());

                // Casing.
                string t = Text();
                Same("casing", name + " " + t, () => z.TextInfo.ToUpper(t) + "|" + z.TextInfo.ToLower(t) + "|" + z.TextInfo.ToTitleCase(t),
                    () => t.ToUpper(s) + "|" + t.ToLower(s) + "|" + s.TextInfo.ToTitleCase(t));
                char ch = t[0];
                Same("char casing", name + " " + t, () => $"{(int)z.TextInfo.ToUpper(ch)} {(int)z.TextInfo.ToLower(ch)}", () => $"{(int)s.TextInfo.ToUpper(ch)} {(int)s.TextInfo.ToLower(ch)}");
            }
        }

        // Culture lookups.
        foreach (string name in new[] { "TR-tr", "zh-hans", "", "@@", "de", "xx!" })
            Same("GetCultureInfo", name, () => Z.CultureInfo.GetCultureInfo(name).Name, () => G.CultureInfo.GetCultureInfo(name).Name);
        foreach (int lcid in new[] { 1055, 1031, 127, 9999, 0x0400, 31 })
            Same("GetCultureInfo(int)", lcid.ToString(), () => Z.CultureInfo.GetCultureInfo(lcid).Name, () => G.CultureInfo.GetCultureInfo(lcid).Name);

        foreach (var (label, n) in perLabel)
            Console.WriteLine($"  {label}: {n} failures");
        Console.WriteLine(failures == 0 ? $"OK: {checks} checks over {Z.CultureData.Count} cultures" : $"FAILED: {failures} of {checks} checks");
        return failures == 0 ? 0 : 1;
    }

    delegate void Formatter(char format, int precision, ref Z.Number.Sink sink);

    static string Zero(Z.NumberFormatInfo info, string format, Formatter f)
    {
        char letter = Z.Number.ParseFormat(format, out int precision);
        if (info != null)
            precision = Z.Number.s_culture.Precision(letter, precision, info);
        char[] buffer = new char[2000];
        fixed (char* chars = buffer)
        {
            var sink = new Z.Number.Sink(chars, buffer.Length) { Info = info };
            f(letter, precision, ref sink);
            return new string(chars, 0, sink.Length);
        }
    }

    static string ZeroDouble(double d, string format, Z.CultureInfo culture)
        => Zero(Z.Number.InfoFor(culture), format, (char l, int p, ref Z.Number.Sink sk) => Z.Number.FormatDouble(d, l, p, ref sk));

    static string ZeroLong(long v, string format, Z.CultureInfo culture)
        => Zero(Z.Number.InfoFor(culture), format, (char l, int p, ref Z.Number.Sink sk) => Z.Number.FormatInteger((ulong)v, true, 64, l, p, ref sk));
}

namespace ZeroTest
{
    using System;
    using System.Runtime.InteropServices;

    static unsafe partial class Number
    {
        internal static string FromChars(char* chars, int length) => new string(chars, 0, length);
        internal static ReadOnlySpan<char> AsSpan(string text) => text;
        internal static string IntegerToString(ulong raw, bool signed, int bits, string format, IFormatProvider provider) => throw new NotSupportedException();
    }

    static unsafe class Platform
    {
        [DllImport("libc")] internal static extern int clock_gettime(int clock, long* timespec);
        [DllImport("libc")] internal static extern int pthread_key_create(nuint* key, void* destructor);
        [DllImport("libc")] internal static extern void* pthread_getspecific(nuint key);
        [DllImport("libc")] internal static extern int pthread_setspecific(nuint key, void* value);
        [DllImport("libc")] internal static extern void* calloc(nuint count, nuint size);
    }
}
