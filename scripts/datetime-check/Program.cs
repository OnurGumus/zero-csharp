// Compares zerocore's dates and times (namespace ZeroTest here) with .NET's,
// in whatever time zone TZ names: components, arithmetic, conversions,
// local time, and every standard and custom format, on random and edge
// values. Each check compares the result's text, or the exception's type and
// message. Usage: check-datetime.sh [iterations] [seed]
using System;
using System.Text;
using S = System;
using Z = ZeroTest;

static class Program
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
        if (n < 4 && shown < 200)
        {
            shown++;
            Console.WriteLine($"FAIL {label} [{input}]\n  zero: {a}\n  .NET: {b}");
        }
    }

    const long MaxTicks = 3155378975999999999;
    const long TicksPerDay = 864_000_000_000;

    static long Ticks()
    {
        switch (R.Next(8))
        {
            case 0: return R.NextInt64(0, MaxTicks + 1);
            case 1: return R.NextInt64(0, 1000);
            case 2: return MaxTicks - R.NextInt64(0, 1000);
            case 3: return R.NextInt64(0, 400) * TicksPerDay + R.NextInt64(0, TicksPerDay);
            case 4: // around 1800 to 2100, where time zones change
                return 567_709_344_000_000_000 + R.NextInt64(0, 300L * 365 * TicksPerDay);
            case 5: // near midnight
                return R.NextInt64(1, 3_652_059) * TicksPerDay + R.NextInt64(-36_000_000_000, 36_000_000_000);
            case 6: // a whole second
                return R.NextInt64(0, MaxTicks / 10_000_000) * 10_000_000;
            default: // a recent day, to the minute
                return 638_000_000_000_000_000 + R.NextInt64(0, 20L * 365 * 1440) * 600_000_000;
        }
    }

    static long AnyLong() => R.Next(6) switch
    {
        0 => R.NextInt64(long.MinValue, long.MaxValue),
        1 => R.NextInt64(-1000, 1000),
        2 => long.MaxValue - R.NextInt64(0, 1000),
        3 => long.MinValue + R.NextInt64(0, 1000),
        4 => R.NextInt64(-MaxTicks - 10, MaxTicks + 10),
        _ => (long)(R.NextDouble() * Math.Pow(10, R.Next(20))) * (R.Next(2) == 0 ? 1 : -1),
    };

    static int AnyInt() => R.Next(5) switch
    {
        0 => R.Next(int.MinValue, int.MaxValue),
        1 => R.Next(-100, 100),
        2 => int.MaxValue - R.Next(1000),
        3 => int.MinValue + R.Next(1000),
        _ => R.Next(-200_000, 200_000),
    };

    static double AnyDouble() => R.Next(9) switch
    {
        0 => (R.NextDouble() - 0.5) * Math.Pow(10, R.Next(-3, 20)),
        1 => R.Next(-1000, 1000) + R.NextDouble(),
        2 => double.NaN,
        3 => double.PositiveInfinity,
        4 => double.NegativeInfinity,
        5 => R.Next(-100, 100),
        6 => BitConverter.Int64BitsToDouble(R.NextInt64()),
        7 => (R.NextDouble() - 0.5) * 3e6,
        _ => (R.NextDouble() - 0.5) * 1e9,
    };

    static S.DateTimeKind Kind() => (S.DateTimeKind)R.Next(3);

    static string Show(S.DateTime d) => $"{d.Ticks}/{d.Kind}";
    static string Show(Z.DateTime d) => $"{d.Ticks}/{(S.DateTimeKind)(int)d.Kind}";
    static string Show(S.DateTimeOffset d) => $"{d.Ticks}@{d.Offset.Ticks}";
    static string Show(Z.DateTimeOffset d) => $"{d.Ticks}@{d.Offset.Ticks}";
    static string Show(S.TimeSpan t) => t.Ticks.ToString();
    static string Show(Z.TimeSpan t) => t.Ticks.ToString();

    // A random DateTime of each kind, built through both libraries' binary
    // form so a local one keeps its ambiguous-daylight flag the same way.
    static (Z.DateTime, S.DateTime) Pair()
    {
        long ticks = Ticks();
        var kind = Kind();
        return (new Z.DateTime(ticks, (Z.DateTimeKind)(int)kind), new S.DateTime(ticks, kind));
    }

    static readonly string[] DateTokens =
    {
        "y", "yy", "yyy", "yyyy", "yyyyy", "yyyyyyyyyyyyyyyyy", "M", "MM", "MMM", "MMMM", "MMMMM", "d", "dd", "ddd", "dddd", "ddddd",
        "h", "hh", "hhh", "H", "HH", "HHH", "m", "mm", "mmm", "s", "ss", "sss",
        "f", "ff", "fff", "ffff", "fffff", "ffffff", "fffffff", "ffffffff",
        "F", "FF", "FFF", "FFFF", "FFFFF", "FFFFFF", "FFFFFFF", "FFFFFFFF", ".F", ".FFF", ".FFFFFFF",
        "t", "tt", "ttt", "z", "zz", "zzz", "zzzz", "K", "KK", "g", "gg", ":", "/", "'lit'", "\"q\"", "'a\\'b'", "\\x", "\\",
        "%d", "%M", "%y", "%h", "%%", "%", " ", "-", ".", "T", "Z", ",", "'unterminated", "\"open",
    };

    static string CustomFormat(string[] tokens)
    {
        var sb = new StringBuilder();
        int n = R.Next(1, 6);
        for (int i = 0; i < n; i++)
            sb.Append(tokens[R.Next(tokens.Length)]);
        return sb.ToString();
    }

    static string DateFormat()
    {
        switch (R.Next(4))
        {
            case 0: return ((char)R.Next(32, 127)).ToString();
            case 1: return "dDfFgGmMoOrRstTuUyY"[R.Next(19)].ToString();
            case 2: return R.Next(10) == 0 ? (R.Next(2) == 0 ? "" : null) : CustomFormat(DateTokens);
            default: return CustomFormat(DateTokens);
        }
    }

    static readonly string[] SpanTokens =
    {
        "d", "dd", "ddd", "dddddddd", "ddddddddd", "h", "hh", "hhh", "m", "mm", "mmm", "s", "ss", "sss",
        "f", "fff", "fffffff", "ffffffff", "F", "FFF", "FFFFFFF", "FFFFFFFF", "'.'", "'lit'", "\\:", "\\.", "\\",
        "%h", "%d", "%%", "%", ":", ".", " ", "'open",
    };

    static string SpanFormat()
    {
        switch (R.Next(4))
        {
            case 0: return ((char)R.Next(32, 127)).ToString();
            case 1: return "cgGtT"[R.Next(5)].ToString();
            case 2: return R.Next(5) == 0 ? (R.Next(2) == 0 ? "" : null) : CustomFormat(SpanTokens);
            default: return CustomFormat(SpanTokens);
        }
    }

    static string Text(Func<char[], (bool, int)> tryFormat)
    {
        // TryFormat: too small first (it must write nothing), then enough.
        var small = new char[3];
        var (ok, n) = tryFormat(small);
        if (ok)
            return "small:" + new string(small, 0, n);
        if (n != 0)
            return "small wrote " + n;
        var big = new char[400];
        (ok, n) = tryFormat(big);
        return ok ? new string(big, 0, n) : "failed";
    }


    // A writable DateTimeFormatInfo set up the same way on both sides (bad
    // values too, comparing the exceptions).
    static readonly string[] PatternChoices =
    {
        "dd.MM.yyyy", "yyyy-MM-dd", "d MMMM yyyy", "dddd d MMMM", "HH.mm", "h:mm tt", "H:mm:ss", "tt hh:mm", "MMM yyyy",
        "yyyy'r.' MMMM", "dd MMMM yyyy HH:mm:ss", "d/M/yy", "MMMM d, yyyy", "%d", "HH:mm:ss.fff zzz",
    };
    static readonly string[] SeparatorChoices = { "/", ".", "-", ":", " ", "h", "", "::" };
    static readonly string[] DesignatorChoices = { "AM", "PM", "a.m.", "\u00F6\u00F6", "", "vorm.", "x" };

    static (Z.DateTimeFormatInfo, S.Globalization.DateTimeFormatInfo, string) DateInfo()
    {
        var z = new Z.DateTimeFormatInfo();
        var s = new S.Globalization.DateTimeFormatInfo();
        var log = new StringBuilder();
        int settings = R.Next(0, 8);
        for (int k = 0; k < settings; k++)
        {
            bool bad = R.Next(10) == 0;
            string text = bad ? null : PatternChoices[R.Next(PatternChoices.Length)];
            string label;
            string result;
            switch (R.Next(13))
            {
                case 0: label = "ShortDatePattern=" + text; result = Both(() => z.ShortDatePattern = text, () => s.ShortDatePattern = text); break;
                case 1: label = "LongDatePattern=" + text; result = Both(() => z.LongDatePattern = text, () => s.LongDatePattern = text); break;
                case 2: label = "ShortTimePattern=" + text; result = Both(() => z.ShortTimePattern = text, () => s.ShortTimePattern = text); break;
                case 3: label = "LongTimePattern=" + text; result = Both(() => z.LongTimePattern = text, () => s.LongTimePattern = text); break;
                case 4: label = "FullDateTimePattern=" + text; result = Both(() => z.FullDateTimePattern = text, () => s.FullDateTimePattern = text); break;
                case 5: label = "MonthDayPattern=" + text; result = Both(() => z.MonthDayPattern = text, () => s.MonthDayPattern = text); break;
                case 6: label = "YearMonthPattern=" + text; result = Both(() => z.YearMonthPattern = text, () => s.YearMonthPattern = text); break;
                case 7: { string v = bad ? null : SeparatorChoices[R.Next(SeparatorChoices.Length)]; label = "DateSeparator=" + v; result = Both(() => z.DateSeparator = v, () => s.DateSeparator = v); break; }
                case 8: { string v = bad ? null : SeparatorChoices[R.Next(SeparatorChoices.Length)]; label = "TimeSeparator=" + v; result = Both(() => z.TimeSeparator = v, () => s.TimeSeparator = v); break; }
                case 9: { string v = bad ? null : DesignatorChoices[R.Next(DesignatorChoices.Length)]; label = "AMDesignator=" + v; result = Both(() => z.AMDesignator = v, () => s.AMDesignator = v); break; }
                case 10: { string v = bad ? null : DesignatorChoices[R.Next(DesignatorChoices.Length)]; label = "PMDesignator=" + v; result = Both(() => z.PMDesignator = v, () => s.PMDesignator = v); break; }
                case 11:
                {
                    string[] v = bad ? new string[6] : new[] { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" };
                    if (R.Next(2) == 0)
                    {
                        label = "AbbreviatedDayNames"; result = Both(() => z.AbbreviatedDayNames = v, () => s.AbbreviatedDayNames = v);
                    }
                    else
                    {
                        label = "DayNames"; result = Both(() => z.DayNames = v, () => s.DayNames = v);
                    }
                    break;
                }
                default:
                {
                    string[] v = bad ? new[] { "a" } : new[] { "Ocak", "\u015Eubat", "Mart", "Nisan", "May\u0131s", "Haziran", "Temmuz", "A\u011Fustos", "Eyl\u00FCl", "Ekim", "Kas\u0131m", "Aral\u0131k", "" };
                    string[] genitive = new[] { "ocaka", "\u015Fubata", "marta", "nisana", "may\u0131sa", "hazirana", "temmuza", "a\u011Fustosa", "eyl\u00FCle", "ekime", "kas\u0131ma", "aral\u0131ka", "" };
                    switch (R.Next(3))
                    {
                        case 0: label = "MonthNames"; result = Both(() => z.MonthNames = v, () => s.MonthNames = v); break;
                        case 1: label = "AbbreviatedMonthNames"; result = Both(() => z.AbbreviatedMonthNames = v, () => s.AbbreviatedMonthNames = v); break;
                        default: label = "MonthGenitiveNames"; result = Both(() => z.MonthGenitiveNames = genitive, () => s.MonthGenitiveNames = genitive); break;
                    }
                    break;
                }
            }
            checks++;
            if (result != "same")
            {
                failures++;
                if (shown++ < 200)
                    Console.WriteLine($"FAIL DateTimeFormatInfo setter [{label}]\n  {result}");
            }
            log.Append(label).Append("; ");
        }
        return (z, s, log.ToString());
    }

    static string Both(Action zero, Action net)
    {
        string a = Run(() => { zero(); return "ok"; }), b = Run(() => { net(); return "ok"; });
        return a == b ? "same" : $"zero {a} | .NET {b}";
    }

    public static int Main(string[] args)
    {
        int iterations = args.Length > 0 ? int.Parse(args[0]) : 100_000;
        R = new Random(args.Length > 1 ? int.Parse(args[1]) : 12345);

        // ---- the local zone ----
        var zl = Z.TimeZoneInfo.Local;
        var sl = S.TimeZoneInfo.Local;
        Same("Local.Id", "", () => zl.Id, () => sl.Id);
        Same("Local.BaseUtcOffset", "", () => Show(zl.BaseUtcOffset), () => Show(sl.BaseUtcOffset));
        Same("Local.DisplayName", "", () => zl.DisplayName, () => sl.DisplayName);
        Same("Local.StandardName", "", () => zl.StandardName, () => sl.StandardName);
        Same("Local.DaylightName", "", () => zl.DaylightName, () => sl.DaylightName);
        Same("Local.SupportsDaylightSavingTime", "", () => zl.SupportsDaylightSavingTime.ToString(), () => sl.SupportsDaylightSavingTime.ToString());
        Same("Local.Equals(Utc)", "", () => zl.Equals(Z.TimeZoneInfo.Utc).ToString(), () => sl.Equals(S.TimeZoneInfo.Utc).ToString());
        Same("Utc.DisplayName", "", () => Z.TimeZoneInfo.Utc.DisplayName + Z.TimeZoneInfo.Utc.Id, () => S.TimeZoneInfo.Utc.DisplayName + S.TimeZoneInfo.Utc.Id);
        Same("Find(UTC)", "", () => Z.TimeZoneInfo.FindSystemTimeZoneById("UTC").Id, () => S.TimeZoneInfo.FindSystemTimeZoneById("UTC").Id);
        Same("Find(nowhere)", "", () => Z.TimeZoneInfo.FindSystemTimeZoneById("Nowhere/Here").Id, () => S.TimeZoneInfo.FindSystemTimeZoneById("Nowhere/Here").Id);

        var zc = Z.TimeZoneInfo.CreateCustomTimeZone("X", Z.TimeSpan.FromHours(5.5), "x display", "x std");
        var sc = S.TimeZoneInfo.CreateCustomTimeZone("X", S.TimeSpan.FromHours(5.5), "x display", "x std");

        // Now: the same to within a second, and the same offset.
        Same("Now offset", "", () => Z.DateTimeOffset.Now.Offset.ToString(), () => S.DateTimeOffset.Now.Offset.ToString());
        Same("Now - UtcNow", "", () => ((int)Math.Round((Z.DateTime.Now - Z.DateTime.UtcNow).TotalMinutes)).ToString(), () => ((int)Math.Round((S.DateTime.Now - S.DateTime.UtcNow).TotalMinutes)).ToString());
        Same("UtcNow", "", () => (Z.DateTime.UtcNow.Ticks / 10_000_000 / 10).ToString(), () => (S.DateTime.UtcNow.Ticks / 10_000_000 / 10).ToString());

        Z.DateTimeFormatInfo zdi = null;
        S.Globalization.DateTimeFormatInfo sdi = null;
        string dconfig = "";
        for (int it = 0; it < iterations; it++)
        {
            var (z, s) = Pair();
            string input = Show(s);
            // Read each time: .NET may replace its cached Local, and then
            // treats the old one as just another zone.
            zl = Z.TimeZoneInfo.Local;
            sl = S.TimeZoneInfo.Local;

            Same("components", input,
                () => $"{z.Year} {z.Month} {z.Day} {z.DayOfYear} {z.DayOfWeek} {z.Hour} {z.Minute} {z.Second} {z.Millisecond} {z.Microsecond} {z.Nanosecond} {Show(z.Date)} {Show(z.TimeOfDay)}",
                () => $"{s.Year} {s.Month} {s.Day} {s.DayOfYear} {s.DayOfWeek} {s.Hour} {s.Minute} {s.Second} {s.Millisecond} {s.Microsecond} {s.Nanosecond} {Show(s.Date)} {Show(s.TimeOfDay)}");

            // Local time.
            Same("ToLocalTime", input, () => Show(z.ToLocalTime()) + " " + z.ToLocalTime().ToBinary(), () => Show(s.ToLocalTime()) + " " + s.ToLocalTime().ToBinary());
            Same("ToUniversalTime", input, () => Show(z.ToUniversalTime()), () => Show(s.ToUniversalTime()));
            Same("ToLocalTime.ToUniversalTime", input, () => Show(z.ToLocalTime().ToUniversalTime()), () => Show(s.ToLocalTime().ToUniversalTime()));
            Same("ToBinary", input, () => z.ToBinary().ToString(), () => s.ToBinary().ToString());
            long binary = R.Next(3) == 0 ? AnyLong() : s.ToBinary() + R.NextInt64(-TicksPerDay, TicksPerDay);
            Same("FromBinary", binary.ToString(), () => Show(Z.DateTime.FromBinary(binary)) + " " + Z.DateTime.FromBinary(binary).ToBinary(), () => Show(S.DateTime.FromBinary(binary)) + " " + S.DateTime.FromBinary(binary).ToBinary());
            Same("IsDaylightSavingTime", input, () => z.IsDaylightSavingTime().ToString(), () => s.IsDaylightSavingTime().ToString());
            Same("GetUtcOffset", input, () => Show(zl.GetUtcOffset(z)), () => Show(sl.GetUtcOffset(s)));
            Same("IsAmbiguousTime", input, () => zl.IsAmbiguousTime(z).ToString(), () => sl.IsAmbiguousTime(s).ToString());
            Same("IsInvalidTime", input, () => zl.IsInvalidTime(z).ToString(), () => sl.IsInvalidTime(s).ToString());
            Same("Local.IsDaylightSavingTime", input, () => zl.IsDaylightSavingTime(z).ToString(), () => sl.IsDaylightSavingTime(s).ToString());
            Same("GetAmbiguousTimeOffsets", input, () => string.Join(",", Array.ConvertAll(zl.GetAmbiguousTimeOffsets(z), Show)), () => string.Join(",", Array.ConvertAll(sl.GetAmbiguousTimeOffsets(s), Show)));
            Same("ConvertTime(custom)", input, () => Show(Z.TimeZoneInfo.ConvertTime(z, zc)), () => Show(S.TimeZoneInfo.ConvertTime(s, sc)));
            Same("ConvertTime(local, custom)", input, () => Show(Z.TimeZoneInfo.ConvertTime(z, Z.TimeZoneInfo.Local, zc)), () => Show(S.TimeZoneInfo.ConvertTime(s, S.TimeZoneInfo.Local, sc)));
            Same("ConvertTime(custom, utc)", input, () => Show(Z.TimeZoneInfo.ConvertTime(z, zc, Z.TimeZoneInfo.Utc)), () => Show(S.TimeZoneInfo.ConvertTime(s, sc, S.TimeZoneInfo.Utc)));
            Same("ConvertTimeToUtc", input, () => Show(Z.TimeZoneInfo.ConvertTimeToUtc(z)), () => Show(S.TimeZoneInfo.ConvertTimeToUtc(s)));
            Same("ConvertTimeFromUtc", input, () => Show(Z.TimeZoneInfo.ConvertTimeFromUtc(z, Z.TimeZoneInfo.Local)), () => Show(S.TimeZoneInfo.ConvertTimeFromUtc(s, S.TimeZoneInfo.Local)));

            // Arithmetic.
            double d = AnyDouble();
            string di = input + " " + d.ToString("R");
            Same("AddDays", di, () => Show(z.AddDays(d)), () => Show(s.AddDays(d)));
            Same("AddHours", di, () => Show(z.AddHours(d)), () => Show(s.AddHours(d)));
            Same("AddMinutes", di, () => Show(z.AddMinutes(d)), () => Show(s.AddMinutes(d)));
            Same("AddSeconds", di, () => Show(z.AddSeconds(d)), () => Show(s.AddSeconds(d)));
            Same("AddMilliseconds", di, () => Show(z.AddMilliseconds(d)), () => Show(s.AddMilliseconds(d)));
            Same("AddMicroseconds", di, () => Show(z.AddMicroseconds(d)), () => Show(s.AddMicroseconds(d)));
            long lt = R.Next(2) == 0 ? AnyLong() : R.NextInt64(-MaxTicks, MaxTicks);
            Same("AddTicks", input + " " + lt, () => Show(z.AddTicks(lt)), () => Show(s.AddTicks(lt)));
            Same("+ TimeSpan", input + " " + lt, () => Show(z + new Z.TimeSpan(lt)), () => Show(s + new S.TimeSpan(lt)));
            Same("- TimeSpan", input + " " + lt, () => Show(z - new Z.TimeSpan(lt)), () => Show(s - new S.TimeSpan(lt)));
            Same("Subtract(TimeSpan)", input + " " + lt, () => Show(z.Subtract(new Z.TimeSpan(lt))), () => Show(s.Subtract(new S.TimeSpan(lt))));
            int months = R.Next(3) == 0 ? AnyInt() : R.Next(-130_000, 130_000);
            Same("AddMonths", input + " " + months, () => Show(z.AddMonths(months)), () => Show(s.AddMonths(months)));
            int years = R.Next(3) == 0 ? AnyInt() : R.Next(-11_000, 11_000);
            Same("AddYears", input + " " + years, () => Show(z.AddYears(years)), () => Show(s.AddYears(years)));
            var (z2, s2) = Pair();
            Same("compare", input + " " + Show(s2), () => $"{z == z2} {z < z2} {z.CompareTo(z2)} {Z.DateTime.Compare(z, z2)} {z.Equals(z2)} {Show(z - z2)} {z.GetHashCode()}",
                () => $"{s == s2} {s < s2} {s.CompareTo(s2)} {S.DateTime.Compare(s, s2)} {s.Equals(s2)} {Show(s - s2)} {s.GetHashCode()}");

            // Construction.
            int y = R.Next(4) == 0 ? AnyInt() : R.Next(-2, 10_002), mo = R.Next(-1, 14), da = R.Next(-1, 33);
            int h = R.Next(-1, 25), mi = R.Next(-1, 61), se = R.Next(-1, 61), ms = R.Next(-1, 1001), us = R.Next(-1, 1001);
            var kind = Kind();
            string ci = $"{y} {mo} {da} {h} {mi} {se} {ms} {us} {kind}";
            Same("ctor(y,m,d)", ci, () => Show(new Z.DateTime(y, mo, da)), () => Show(new S.DateTime(y, mo, da)));
            Same("ctor(y..s, kind)", ci, () => Show(new Z.DateTime(y, mo, da, h, mi, se, (Z.DateTimeKind)(int)kind)), () => Show(new S.DateTime(y, mo, da, h, mi, se, kind)));
            Same("ctor(y..ms)", ci, () => Show(new Z.DateTime(y, mo, da, h, mi, se, ms)), () => Show(new S.DateTime(y, mo, da, h, mi, se, ms)));
            Same("ctor(y..us, kind)", ci, () => Show(new Z.DateTime(y, mo, da, h, mi, se, ms, us, (Z.DateTimeKind)(int)kind)), () => Show(new S.DateTime(y, mo, da, h, mi, se, ms, us, kind)));
            Same("ctor(ticks)", lt.ToString(), () => Show(new Z.DateTime(lt)), () => Show(new S.DateTime(lt)));
            Same("ctor(kind)", R.Next(5).ToString(), () => Show(new Z.DateTime(0, (Z.DateTimeKind)(it % 5))), () => Show(new S.DateTime(0, (S.DateTimeKind)(it % 5))));
            Same("DaysInMonth", ci, () => Z.DateTime.DaysInMonth(y, mo).ToString(), () => S.DateTime.DaysInMonth(y, mo).ToString());
            Same("IsLeapYear", ci, () => Z.DateTime.IsLeapYear(y).ToString(), () => S.DateTime.IsLeapYear(y).ToString());
            Same("SpecifyKind", input, () => Show(Z.DateTime.SpecifyKind(z, (Z.DateTimeKind)(it % 4))), () => Show(S.DateTime.SpecifyKind(s, (S.DateTimeKind)(it % 4))));
            Same("Deconstruct", input, () => { var (dd, tt) = z; var (yy, mm, dy) = z; return $"{dd.DayNumber} {tt.Ticks} {yy} {mm} {dy}"; },
                () => { var (dd, tt) = s; var (yy, mm, dy) = s; return $"{dd.DayNumber} {tt.Ticks} {yy} {mm} {dy}"; });

            // File times and OLE dates.
            long ft = R.Next(2) == 0 ? AnyLong() : R.NextInt64(0, 2_650_467_743_999_999_999);
            Same("FromFileTimeUtc", ft.ToString(), () => Show(Z.DateTime.FromFileTimeUtc(ft)), () => Show(S.DateTime.FromFileTimeUtc(ft)));
            Same("FromFileTime", ft.ToString(), () => Show(Z.DateTime.FromFileTime(ft)), () => Show(S.DateTime.FromFileTime(ft)));
            Same("ToFileTimeUtc", input, () => z.ToFileTimeUtc().ToString(), () => s.ToFileTimeUtc().ToString());
            Same("ToFileTime", input, () => z.ToFileTime().ToString(), () => s.ToFileTime().ToString());
            double oa = R.Next(3) == 0 ? AnyDouble() : (R.NextDouble() - 0.2) * 3_000_000;
            Same("FromOADate", oa.ToString("R"), () => Show(Z.DateTime.FromOADate(oa)), () => Show(S.DateTime.FromOADate(oa)));
            Same("ToOADate", input, () => z.ToOADate().ToString("R"), () => s.ToOADate().ToString("R"));

            // Text.
            string f = DateFormat();
            Same("ToString(format)", input + " \"" + f + "\"", () => z.ToString(f), () => s.ToString(f));
            Same("TryFormat", input + " \"" + f + "\"", () => Text(b => (z.TryFormat(b, out int n, f), n)), () => Text(b => (s.TryFormat(b, out int n, f), n)));
            if (it % 50 == 0)
            {
                Same("ToString()", input, () => z.ToString() + z.ToLongDateString() + z.ToShortTimeString(), () => s.ToString() + s.ToLongDateString() + s.ToShortTimeString());
                Same("GetDateTimeFormats", input, () => string.Join("|", z.GetDateTimeFormats()), () => string.Join("|", s.GetDateTimeFormats()));
                char gc = "dDfFgGmMoOrRstTuUyYxz"[R.Next(21)];
                Same("GetDateTimeFormats(c)", input + " " + gc, () => string.Join("|", z.GetDateTimeFormats(gc)), () => string.Join("|", s.GetDateTimeFormats(gc)));
            }

            // DateTimeOffset.
            long off = R.Next(5) switch
            {
                0 => R.NextInt64(-15 * 60, 15 * 60) * 600_000_000,
                1 => R.NextInt64(-50_000_000_000, 50_000_000_000),
                2 => 0,
                _ => R.NextInt64(-14 * 60, 14 * 60 + 1) * 600_000_000,
            };
            long ot = Ticks();
            string oi = ot + "@" + off;
            Same("DateTimeOffset(ticks, offset)", oi, () => Show(new Z.DateTimeOffset(ot, new Z.TimeSpan(off))), () => Show(new S.DateTimeOffset(ot, new S.TimeSpan(off))));
            Same("DateTimeOffset(DateTime)", input, () => Show(new Z.DateTimeOffset(z)), () => Show(new S.DateTimeOffset(s)));
            Same("DateTimeOffset(DateTime, offset)", input + " " + off, () => Show(new Z.DateTimeOffset(z, new Z.TimeSpan(off))), () => Show(new S.DateTimeOffset(s, new S.TimeSpan(off))));
            Z.DateTimeOffset zo;
            S.DateTimeOffset so;
            try { so = new S.DateTimeOffset(ot, new S.TimeSpan(off)); zo = new Z.DateTimeOffset(ot, new Z.TimeSpan(off)); }
            catch { so = new S.DateTimeOffset(ot, S.TimeSpan.Zero); zo = new Z.DateTimeOffset(ot, Z.TimeSpan.Zero); }
            oi = Show(so);
            Same("offset parts", oi, () => $"{zo.Year} {zo.Month} {zo.Day} {zo.Hour} {zo.Minute} {zo.DayOfWeek} {zo.DayOfYear} {zo.UtcTicks} {Show(zo.DateTime)} {Show(zo.UtcDateTime)} {zo.TotalOffsetMinutes} {Show(zo.TimeOfDay)}",
                () => $"{so.Year} {so.Month} {so.Day} {so.Hour} {so.Minute} {so.DayOfWeek} {so.DayOfYear} {so.UtcTicks} {Show(so.DateTime)} {Show(so.UtcDateTime)} {so.TotalOffsetMinutes} {Show(so.TimeOfDay)}");
            Same("offset LocalDateTime", oi, () => Show(zo.LocalDateTime), () => Show(so.LocalDateTime));
            Same("offset ToLocalTime", oi, () => Show(zo.ToLocalTime()), () => Show(so.ToLocalTime()));
            Same("offset ToUniversalTime", oi, () => Show(zo.ToUniversalTime()), () => Show(so.ToUniversalTime()));
            Same("ToOffset", oi + " " + off, () => Show(zo.ToOffset(new Z.TimeSpan(off))), () => Show(so.ToOffset(new S.TimeSpan(off))));
            Same("ToUnixTime", oi, () => zo.ToUnixTimeSeconds() + " " + zo.ToUnixTimeMilliseconds(), () => so.ToUnixTimeSeconds() + " " + so.ToUnixTimeMilliseconds());
            long unix = R.Next(2) == 0 ? AnyLong() : R.NextInt64(-62_135_596_810, 253_402_300_810);
            Same("FromUnixTimeSeconds", unix.ToString(), () => Show(Z.DateTimeOffset.FromUnixTimeSeconds(unix)), () => Show(S.DateTimeOffset.FromUnixTimeSeconds(unix)));
            long unixMs = R.Next(2) == 0 ? AnyLong() : R.NextInt64(-62_135_596_800_010, 253_402_300_800_010);
            Same("FromUnixTimeMilliseconds", unixMs.ToString(), () => Show(Z.DateTimeOffset.FromUnixTimeMilliseconds(unixMs)), () => Show(S.DateTimeOffset.FromUnixTimeMilliseconds(unixMs)));
            Same("offset FromFileTime", ft.ToString(), () => Show(Z.DateTimeOffset.FromFileTime(ft)), () => Show(S.DateTimeOffset.FromFileTime(ft)));
            Same("offset ToFileTime", oi, () => zo.ToFileTime().ToString(), () => so.ToFileTime().ToString());
            Same("offset AddMonths", oi + " " + months, () => Show(zo.AddMonths(months)), () => Show(so.AddMonths(months)));
            Same("offset AddHours", oi + " " + d, () => Show(zo.AddHours(d)), () => Show(so.AddHours(d)));
            Same("offset + TimeSpan", oi + " " + lt, () => Show(zo + new Z.TimeSpan(lt)), () => Show(so + new S.TimeSpan(lt)));
            Same("offset compare", oi, () => $"{zo == new Z.DateTimeOffset(z)} {zo.CompareTo(new Z.DateTimeOffset(z))} {zo.EqualsExact(zo.ToUniversalTime())} {zo.GetHashCode()}",
                () => $"{so == new S.DateTimeOffset(s)} {so.CompareTo(new S.DateTimeOffset(s))} {so.EqualsExact(so.ToUniversalTime())} {so.GetHashCode()}");
            Same("offset ToString(format)", oi + " \"" + f + "\"", () => zo.ToString(f), () => so.ToString(f));
            Same("offset TryFormat", oi + " \"" + f + "\"", () => Text(b => (zo.TryFormat(b, out int n, f), n)), () => Text(b => (so.TryFormat(b, out int n, f), n)));
            Same("ConvertTime(offset)", oi, () => Show(Z.TimeZoneInfo.ConvertTime(zo, zl)), () => Show(S.TimeZoneInfo.ConvertTime(so, sl)));
            Same("offset GetUtcOffset", oi, () => Show(zl.GetUtcOffset(zo)) + zl.IsDaylightSavingTime(zo), () => Show(sl.GetUtcOffset(so)) + sl.IsDaylightSavingTime(so));

            // DateOnly and TimeOnly.
            int dayNumber = R.Next(3) == 0 ? AnyInt() : R.Next(-2, 3_652_060);
            Same("DateOnly.FromDayNumber", dayNumber.ToString(), () => Z.DateOnly.FromDayNumber(dayNumber).ToString("o"), () => S.DateOnly.FromDayNumber(dayNumber).ToString("o"));
            var zd = Z.DateOnly.FromDateTime(z);
            var sd = S.DateOnly.FromDateTime(s);
            Same("DateOnly parts", input, () => $"{zd.Year} {zd.Month} {zd.Day} {zd.DayOfWeek} {zd.DayOfYear} {zd.DayNumber}", () => $"{sd.Year} {sd.Month} {sd.Day} {sd.DayOfWeek} {sd.DayOfYear} {sd.DayNumber}");
            Same("DateOnly(y,m,d)", ci, () => new Z.DateOnly(y, mo, da).DayNumber.ToString(), () => new S.DateOnly(y, mo, da).DayNumber.ToString());
            int addDays = R.Next(2) == 0 ? AnyInt() : R.Next(-4_000_000, 4_000_000);
            Same("DateOnly.AddDays", input + " " + addDays, () => zd.AddDays(addDays).DayNumber.ToString(), () => sd.AddDays(addDays).DayNumber.ToString());
            Same("DateOnly.AddMonths", input + " " + months, () => zd.AddMonths(months).DayNumber.ToString(), () => sd.AddMonths(months).DayNumber.ToString());
            Same("DateOnly.AddYears", input + " " + years, () => zd.AddYears(years).DayNumber.ToString(), () => sd.AddYears(years).DayNumber.ToString());
            Same("DateOnly.ToString(format)", input + " \"" + f + "\"", () => zd.ToString(f), () => sd.ToString(f));
            Same("DateOnly.TryFormat", input + " \"" + f + "\"", () => Text(b => (zd.TryFormat(b, out int n, f), n)), () => Text(b => (sd.TryFormat(b, out int n, f), n)));
            var zt = Z.TimeOnly.FromDateTime(z);
            var st = S.TimeOnly.FromDateTime(s);
            Same("TimeOnly parts", input, () => $"{zt.Hour} {zt.Minute} {zt.Second} {zt.Millisecond} {zt.Microsecond} {zt.Nanosecond} {zt.Ticks}", () => $"{st.Hour} {st.Minute} {st.Second} {st.Millisecond} {st.Microsecond} {st.Nanosecond} {st.Ticks}");
            Same("TimeOnly(h,m,s,ms,us)", ci, () => new Z.TimeOnly(h, mi, se, ms, us).Ticks.ToString(), () => new S.TimeOnly(h, mi, se, ms, us).Ticks.ToString());
            Same("TimeOnly(ticks)", lt.ToString(), () => new Z.TimeOnly(lt).Ticks.ToString(), () => new S.TimeOnly(lt).Ticks.ToString());
            Same("TimeOnly.Add", input + " " + lt, () => { var r = zt.Add(new Z.TimeSpan(lt), out int w); return r.Ticks + " " + w; }, () => { var r = st.Add(new S.TimeSpan(lt), out int w); return r.Ticks + " " + w; });
            Same("TimeOnly.AddHours", input + " " + d, () => { var r = zt.AddHours(d, out int w); return r.Ticks + " " + w; }, () => { var r = st.AddHours(d, out int w); return r.Ticks + " " + w; });
            var zt2 = Z.TimeOnly.FromDateTime(z2);
            var st2 = S.TimeOnly.FromDateTime(s2);
            Same("TimeOnly compare", input, () => $"{Show(zt - zt2)} {zt.IsBetween(zt2, Z.TimeOnly.FromDateTime(z.AddHours(3)))} {zt.CompareTo(zt2)}", () => $"{Show(st - st2)} {st.IsBetween(st2, S.TimeOnly.FromDateTime(s.AddHours(3)))} {st.CompareTo(st2)}");
            Same("TimeOnly.ToString(format)", input + " \"" + f + "\"", () => zt.ToString(f), () => st.ToString(f));
            Same("TimeOnly.TryFormat", input + " \"" + f + "\"", () => Text(b => (zt.TryFormat(b, out int n, f), n)), () => Text(b => (st.TryFormat(b, out int n, f), n)));


            // With a DateTimeFormatInfo of your own.
            if (it % 4 == 0)
            {
                if (it % 200 == 0 || zdi == null)
                    (zdi, sdi, dconfig) = DateInfo();
                string df = DateFormat();
                Same("ToString(format, info)", input + " \"" + df + "\" " + dconfig, () => z.ToString(df, zdi), () => s.ToString(df, sdi));
                Same("offset ToString(format, info)", oi + " \"" + df + "\" " + dconfig, () => zo.ToString(df, zdi), () => so.ToString(df, sdi));
                Same("DateOnly ToString(format, info)", input + " \"" + df + "\" " + dconfig, () => zd.ToString(df, zdi), () => sd.ToString(df, sdi));
                Same("TimeOnly ToString(format, info)", input + " \"" + df + "\" " + dconfig, () => zt.ToString(df, zdi), () => st.ToString(df, sdi));
                char gc2 = "dDfFgGmMtTyY"[R.Next(12)];
                if (it % 40 == 0)
                    Same("GetDateTimeFormats(c, info)", input + " " + gc2 + " " + dconfig, () => string.Join("|", z.GetDateTimeFormats(gc2, zdi)), () => string.Join("|", s.GetDateTimeFormats(gc2, sdi)));
            }
            // TimeSpan.
            long tt2 = AnyLong();
            var zs = new Z.TimeSpan(tt2);
            var ss = new S.TimeSpan(tt2);
            string si = tt2.ToString();
            Same("TimeSpan parts", si, () => $"{zs.Days} {zs.Hours} {zs.Minutes} {zs.Seconds} {zs.Milliseconds} {zs.Microseconds} {zs.Nanoseconds} {zs.TotalDays:R} {zs.TotalHours:R} {zs.TotalMinutes:R} {zs.TotalSeconds:R} {zs.TotalMilliseconds:R} {zs.TotalMicroseconds:R} {zs.TotalNanoseconds:R}",
                () => $"{ss.Days} {ss.Hours} {ss.Minutes} {ss.Seconds} {ss.Milliseconds} {ss.Microseconds} {ss.Nanoseconds} {ss.TotalDays:R} {ss.TotalHours:R} {ss.TotalMinutes:R} {ss.TotalSeconds:R} {ss.TotalMilliseconds:R} {ss.TotalMicroseconds:R} {ss.TotalNanoseconds:R}");
            string sf = SpanFormat();
            Same("TimeSpan.ToString(format)", si + " \"" + sf + "\"", () => zs.ToString(sf), () => ss.ToString(sf));
            Same("TimeSpan.TryFormat", si + " \"" + sf + "\"", () => Text(b => (zs.TryFormat(b, out int n, sf), n)), () => Text(b => (ss.TryFormat(b, out int n, sf), n)));
            Same("TimeSpan * /", si + " " + d, () => $"{Show(zs * d)} {Show(zs / d)} {(zs / new Z.TimeSpan(lt)):R}", () => $"{Show(ss * d)} {Show(ss / d)} {(ss / new S.TimeSpan(lt)):R}");
            Same("TimeSpan.Duration/Negate", si, () => Show(zs.Duration()) + " " + Show(-zs), () => Show(ss.Duration()) + " " + Show(-ss));
            Same("TimeSpan.From(double)", d.ToString("R"), () => $"{Show(Z.TimeSpan.FromDays(d))} {Show(Z.TimeSpan.FromHours(d))} {Show(Z.TimeSpan.FromMilliseconds(d))} {Show(Z.TimeSpan.FromMicroseconds(d))}",
                () => $"{Show(S.TimeSpan.FromDays(d))} {Show(S.TimeSpan.FromHours(d))} {Show(S.TimeSpan.FromMilliseconds(d))} {Show(S.TimeSpan.FromMicroseconds(d))}");
            int i1 = AnyInt(), i2 = AnyInt();
            long l1 = AnyLong(), l2 = AnyLong(), l3 = R.Next(2) == 0 ? AnyLong() : R.Next(-1000, 1000), l4 = R.Next(-100000, 100000);
            string fi = $"{i1} {i2} {l1} {l2} {l3} {l4}";
            Same("TimeSpan.FromDays(int...)", fi, () => Show(Z.TimeSpan.FromDays(i1, i2, l1, l2, l3, l4)), () => Show(S.TimeSpan.FromDays(i1, i2, l1, l2, l3, l4)));
            Same("TimeSpan.FromDays(small)", fi, () => Show(Z.TimeSpan.FromDays(i1 % 20000, i2 % 100, l3, l4)), () => Show(S.TimeSpan.FromDays(i1 % 20000, i2 % 100, l3, l4)));
            Same("TimeSpan.FromHours(int...)", fi, () => Show(Z.TimeSpan.FromHours(i1 % 300000, l3, l4)), () => Show(S.TimeSpan.FromHours(i1 % 300000, l3, l4)));
            Same("TimeSpan.FromX(one)", fi, () => $"{Show(Z.TimeSpan.FromDays(i1 % 20000000))} {Show(Z.TimeSpan.FromHours(i2))} {Show(Z.TimeSpan.FromMinutes(l3))} {Show(Z.TimeSpan.FromSeconds(l1 % 1000000000000))} {Show(Z.TimeSpan.FromMilliseconds(l2 % 10000000000000000))} {Show(Z.TimeSpan.FromMicroseconds(l1))}",
                () => $"{Show(S.TimeSpan.FromDays(i1 % 20000000))} {Show(S.TimeSpan.FromHours(i2))} {Show(S.TimeSpan.FromMinutes(l3))} {Show(S.TimeSpan.FromSeconds(l1 % 1000000000000))} {Show(S.TimeSpan.FromMilliseconds(l2 % 10000000000000000))} {Show(S.TimeSpan.FromMicroseconds(l1))}");
            Same("TimeSpan.FromMilliseconds(long, long)", fi, () => Show(Z.TimeSpan.FromMilliseconds(l3, l4)), () => Show(S.TimeSpan.FromMilliseconds(l3, l4)));
            int a1 = R.Next(2) == 0 ? AnyInt() : R.Next(-20_000_000, 20_000_000);
            int sign = R.Next(2) == 0 ? 1 : -1;
            Same("TimeSpan(d,h,m,s,ms,us)", fi, () => Show(new Z.TimeSpan(a1, i2 % 1000, (int)(l3 % 1000), (int)l4, sign, 7)), () => Show(new S.TimeSpan(a1, i2 % 1000, (int)(l3 % 1000), (int)l4, sign, 7)));
            Same("TimeSpan + -", si + " " + lt, () => Show(zs + new Z.TimeSpan(lt)) + " " + Show(zs - new Z.TimeSpan(lt)), () => Show(ss + new S.TimeSpan(lt)) + " " + Show(ss - new S.TimeSpan(lt)));

            // ISO weeks.
            Same("ISOWeek", input, () => $"{Z.ISOWeek.GetWeekOfYear(z)} {Z.ISOWeek.GetYear(z)} {Z.ISOWeek.GetWeekOfYear(zd)}",
                () => $"{S.Globalization.ISOWeek.GetWeekOfYear(s)} {S.Globalization.ISOWeek.GetYear(s)} {S.Globalization.ISOWeek.GetWeekOfYear(sd)}");
            int wy = R.Next(-1, 10_002), wk = R.Next(-1, 55), wd = R.Next(-1, 9);
            Same("ISOWeek.ToDateTime", $"{wy} {wk} {wd}", () => Show(Z.ISOWeek.ToDateTime(wy, wk, (Z.DayOfWeek)wd)) + " " + Z.ISOWeek.GetWeeksInYear(wy) + Show(Z.ISOWeek.GetYearStart(wy)) + Show(Z.ISOWeek.GetYearEnd(wy)),
                () => Show(S.Globalization.ISOWeek.ToDateTime(wy, wk, (S.DayOfWeek)wd)) + " " + S.Globalization.ISOWeek.GetWeeksInYear(wy) + Show(S.Globalization.ISOWeek.GetYearStart(wy)) + Show(S.Globalization.ISOWeek.GetYearEnd(wy)));
        }

        foreach (var (label, n) in perLabel)
            Console.WriteLine($"  {label}: {n} failures");
        Console.WriteLine(failures == 0 ? $"OK: {checks} checks" : $"FAILED: {failures} of {checks} checks");
        return failures == 0 ? 0 : 1;
    }
}

namespace ZeroTest
{
    using System;

    static unsafe partial class Number
    {
        internal static string FromChars(char* chars, int length) => new string(chars, 0, length);
        internal static ReadOnlySpan<char> AsSpan(string text) => text;
    }

    static unsafe class Platform
    {
        [System.Runtime.InteropServices.DllImport("libc")]
        internal static extern int clock_gettime(int clock, long* timespec);
    }

    // What the copied files ask of CultureInfo: the invariant culture.
    public class CultureInfo : IFormatProvider
    {
        public static CultureInfo CurrentCulture { get; } = new CultureInfo();
        public DateTimeFormatInfo DateTimeFormat => DateTimeFormatInfo.InvariantInfo;
        public object GetFormat(Type formatType) => null;
    }
}
