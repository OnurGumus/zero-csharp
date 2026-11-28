// Test: DateTime, DateTimeOffset, DateOnly, TimeOnly, TimeSpan, TimeZoneInfo
// and ISOWeek, against .NET's answers: building and reading dates, adding
// to them, the standard and custom formats, offsets and Unix times, and
// time zones. Every expected value is what .NET 10 gives; local time
// differs by machine, so it is checked against itself (Now against UtcNow,
// round trips), not against fixed values. scripts/datetime-check compares
// the code with .NET's over millions of values in several time zones.
// Exit code = number of failed checks.
// Build: ./build.sh Dates

using System;
using System.Diagnostics;
using System.Globalization;

namespace DatesSample;

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

    public static int Main()
    {
        // docs:build
        DateTime landing = new DateTime(1969, 7, 20, 20, 17, 40, DateTimeKind.Utc);
        Check("parts", landing.Year == 1969 && landing.Month == 7
            && landing.Day == 20 && landing.Hour == 20 && landing.Minute == 17);
        Check("day of the week", landing.DayOfWeek == DayOfWeek.Sunday);
        Check("day of the year", landing.DayOfYear == 201);
        Check("Kind", landing.Kind == DateTimeKind.Utc);

        DateTime later = landing.AddDays(1.5).AddMonths(6);
        Is("AddDays, AddMonths", later.ToString("o"), "1970-01-22T08:17:40.0000000Z");
        TimeSpan between = new DateTime(2026, 1, 1) - new DateTime(2000, 1, 1);
        Check("subtracting gives a TimeSpan", between.Days == 9497);
        // docs:end

        Check("Jan 31 + 1 month is Feb 28", new DateTime(2026, 1, 31).AddMonths(1) == new DateTime(2026, 2, 28));
        Check("leap years", DateTime.IsLeapYear(2024) && !DateTime.IsLeapYear(1900) && DateTime.IsLeapYear(2000));
        Check("DaysInMonth", DateTime.DaysInMonth(2024, 2) == 29 && DateTime.DaysInMonth(2026, 4) == 30);
        Check("Date and TimeOfDay", landing.Date == new DateTime(1969, 7, 20)
            && landing.TimeOfDay == new TimeSpan(20, 17, 40));
        Check("ticks", new DateTime(2026, 11, 28).Ticks == 639314208000000000);
        Check("compare", landing < later && landing.CompareTo(later) < 0 && landing == new DateTime(landing.Ticks));
        Check("MinValue, MaxValue", DateTime.MinValue.Ticks == 0 && DateTime.MaxValue.Ticks == 3155378975999999999);
        Check("UnixEpoch", DateTime.UnixEpoch == new DateTime(1970, 1, 1) && DateTime.UnixEpoch.Kind == DateTimeKind.Utc);
        Check("FileTime", DateTime.FromFileTimeUtc(0) == new DateTime(1601, 1, 1) && landing.ToFileTimeUtc() == 116302906600000000);
        Check("OADate", new DateTime(2000, 1, 1, 12, 0, 0).ToOADate() == 36526.5 && DateTime.FromOADate(36526.5) == new DateTime(2000, 1, 1, 12, 0, 0));
        Check("binary", DateTime.FromBinary(landing.ToBinary()) == landing && DateTime.FromBinary(landing.ToBinary()).Kind == DateTimeKind.Utc);
        Check("Deconstruct", landing is var (y, m, d) && y == 1969 && m == 7 && d == 20);
        Section("DateTime");

        DateTime t = new DateTime(2026, 3, 5, 14, 7, 9, 45);
        // docs:formats
        Is("round trip", t.ToString("o"), "2026-03-05T14:07:09.0450000");
        Is("sortable", t.ToString("s"), "2026-03-05T14:07:09");
        Is("RFC 1123", t.ToString("R"), "Thu, 05 Mar 2026 14:07:09 GMT");
        Is("short date", t.ToString("d"), "03/05/2026");
        Is("long date", t.ToString("D"), "Thursday, 05 March 2026");
        Is("custom", t.ToString("yyyy-MM-dd HH:mm:ss.fff"), "2026-03-05 14:07:09.045");
        Is("names and 12 hours", t.ToString("dddd d MMMM, h:mm tt"),
            "Thursday 5 March, 2:07 PM");
        Is("literals", t.ToString("'at' HH\\h"), "at 14h");
        // docs:end
        Is("default", t.ToString(), "03/05/2026 14:07:09");
        Is("general short", t.ToString("g"), "03/05/2026 14:07");
        Is("full", t.ToString("F"), "Thursday, 05 March 2026 14:07:09");
        Is("universal sortable", t.ToString("u"), "2026-03-05 14:07:09Z");
        Is("month day, year month", t.ToString("M") + "|" + t.ToString("Y"), "March 05|2026 March");
        Is("F trims zeros", t.ToString("ss.FFFFFFF"), "09.045");
        Is("F drops the point", new DateTime(2026, 1, 1).ToString("HH:mm:ss.FFF"), "00:00:00");
        Is("era, years", t.ToString("g yy yyyyy"), "A.D. 26 02026");
        Is("UTC round trip", landing.ToString("o"), "1969-07-20T20:17:40.0000000Z");
        Is("UTC K", landing.ToString("HH:mmK"), "20:17Z");
        Is("one char custom", t.ToString("%d"), "5");
        Span<char> buffer = stackalloc char[40];
        Check("TryFormat", t.TryFormat(buffer, out int written, "yyyyMMdd") && buffer.Slice(0, written).SequenceEqual("20260305"));
        Check("TryFormat too small", !t.TryFormat(buffer.Slice(0, 4), out written, "yyyyMMdd") && written == 0);
        Is("ToShortTimeString", t.ToShortTimeString(), "14:07");
        Is("ToLongDateString", t.ToLongDateString(), "Thursday, 05 March 2026");
        Is("interpolated", $"{t:yyyy/MM}", "2026/03");
        Check("GetDateTimeFormats", t.GetDateTimeFormats('d').Length == 2 && t.GetDateTimeFormats('d')[1] == "2026-03-05");
        Section("formats");

        // docs:offsets
        DateTimeOffset meeting =
            new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.FromHours(3));
        Is("with its offset", meeting.ToString("o"),
            "2026-06-01T09:00:00.0000000+03:00");
        Is("the same instant in UTC", meeting.ToUniversalTime().ToString("o"),
            "2026-06-01T06:00:00.0000000+00:00");
        Check("equal across offsets",
            meeting == meeting.ToOffset(TimeSpan.FromHours(-5)));
        Check("Unix seconds", meeting.ToUnixTimeSeconds() == 1780293600
            && DateTimeOffset.FromUnixTimeSeconds(1780293600) == meeting);
        // docs:end
        Is("default text", meeting.ToString(), "06/01/2026 09:00:00 +03:00");
        Is("RFC 1123 in UTC", meeting.ToString("R"), "Mon, 01 Jun 2026 06:00:00 GMT");
        Is("zzz", meeting.ToString("HH:mm zzz z"), "09:00 +03:00 +3");
        Check("EqualsExact", !meeting.EqualsExact(meeting.ToUniversalTime()) && meeting.EqualsExact(meeting));
        Check("parts", meeting.Hour == 9 && meeting.UtcDateTime.Hour == 6 && meeting.Offset == TimeSpan.FromHours(3) && meeting.TotalOffsetMinutes == 180);
        Check("Unix milliseconds", DateTimeOffset.FromUnixTimeMilliseconds(1500).ToUnixTimeMilliseconds() == 1500);
        Check("subtract", (meeting.AddHours(2) - meeting) == TimeSpan.FromHours(2));
        Check("from a UTC DateTime", new DateTimeOffset(landing).Offset == TimeSpan.Zero);
        Section("DateTimeOffset");

        // docs:dateonly
        DateOnly due = new DateOnly(2026, 12, 24);
        Is("a date", due.ToString("o"), "2026-12-24");
        Check("days between",
            due.DayNumber - new DateOnly(2026, 11, 28).DayNumber == 26);
        TimeOnly start = new TimeOnly(22, 30);
        TimeOnly end = start.Add(TimeSpan.FromHours(3), out int wrappedDays);
        Check("wraps past midnight", end == new TimeOnly(1, 30) && wrappedDays == 1);
        Check("IsBetween across midnight", new TimeOnly(23, 0).IsBetween(start, end));
        // docs:end
        Is("DateOnly default", due.ToString(), "12/24/2026");
        Is("DateOnly long", due.ToLongDateString(), "Thursday, 24 December 2026");
        Is("TimeOnly default", start.ToString(), "22:30");
        Is("TimeOnly long", start.ToLongTimeString() + " " + start.ToString("o"), "22:30:00 22:30:00.0000000");
        Check("DateOnly parts", due.DayOfWeek == DayOfWeek.Thursday && due.DayOfYear == 358);
        Check("to DateTime", due.ToDateTime(start) == new DateTime(2026, 12, 24, 22, 30, 0));
        Check("AddMonths", due.AddMonths(2) == new DateOnly(2027, 2, 24));
        Check("TimeOnly difference wraps", new TimeOnly(1, 0) - new TimeOnly(23, 0) == TimeSpan.FromHours(2));
        Section("DateOnly and TimeOnly");

        // docs:timespan
        TimeSpan span = new TimeSpan(1, 2, 3, 4, 500);
        Is("constant", span.ToString(), "1.02:03:04.5000000");
        Is("general", span.ToString("g"), "1:2:03:04.5");
        Is("custom", span.ToString("hh\\:mm\\:ss"), "02:03:04");
        Check("exact from integers",
            TimeSpan.FromHours(1, 30) == TimeSpan.FromMinutes(90));
        Check("multiply", span * 2 == new TimeSpan(2, 4, 6, 9));
        // docs:end
        Is("general long", span.ToString("G"), "1:02:03:04.5000000");
        Is("negative", (-span).ToString("c") + " " + (-span).ToString("g"), "-1.02:03:04.5000000 -1:2:03:04.5");
        Is("days padded", span.ToString("ddd\\.hh"), "001.02");
        Check("divide", span / 2 == new TimeSpan(0, 13, 1, 32, 250) && span / span == 1);
        Check("parts", span.Milliseconds == 500 && span.Microseconds == 0 && span.TotalHours > 26.0512 && span.TotalHours < 26.0513);
        Check("FromDays(int, ...)", TimeSpan.FromDays(1, 2, 3, 4, 5, 6).Ticks == 937840050060);
        Section("TimeSpan");

        // docs:zones
        TimeZoneInfo india = TimeZoneInfo.CreateCustomTimeZone(
            "India", new TimeSpan(5, 30, 0), "India", "India Standard Time");
        DateTime noonUtc = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        Is("to a zone", TimeZoneInfo.ConvertTime(noonUtc, india).ToString("o"),
            "2026-01-15T17:30:00.0000000");
        DateTime inIndia = new DateTime(2026, 1, 15, 17, 30, 0);
        Check("and back",
            TimeZoneInfo.ConvertTimeToUtc(inIndia, india) == noonUtc);

        TimeSpan local = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        Check("Now is UtcNow plus the local offset",
            Math.Abs((DateTime.Now - DateTime.UtcNow - local).TotalSeconds) < 2);
        // docs:end
        Check("UTC", TimeZoneInfo.Utc.Id == "UTC" && TimeZoneInfo.Utc.BaseUtcOffset == TimeSpan.Zero);
        Check("local round trip", noonUtc.ToLocalTime().ToUniversalTime() == noonUtc && noonUtc.ToLocalTime().Kind == DateTimeKind.Local);
        Check("local offset", noonUtc.ToLocalTime() - noonUtc == TimeZoneInfo.Local.GetUtcOffset(noonUtc));
        Check("DateTimeOffset.Now", DateTimeOffset.Now.Offset == TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow)
            || DateTimeOffset.Now.Offset == TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow.AddSeconds(2)));
        Check("kinds", DateTime.UtcNow.Kind == DateTimeKind.Utc && DateTime.Now.Kind == DateTimeKind.Local && DateTime.Today.TimeOfDay == TimeSpan.Zero);
        Check("not found", !TimeZoneInfo.TryFindSystemTimeZoneById("Nowhere/Here", out _));
        Section("time zones");

        Check("ISO week", ISOWeek.GetWeekOfYear(new DateTime(2026, 1, 1)) == 1 && ISOWeek.GetWeekOfYear(new DateTime(2027, 1, 1)) == 53);
        Check("ISO year", ISOWeek.GetYear(new DateTime(2027, 1, 1)) == 2026 && ISOWeek.GetWeeksInYear(2026) == 53);
        Check("ISO week to a date", ISOWeek.ToDateTime(2026, 1, DayOfWeek.Monday) == new DateTime(2025, 12, 29));
        Check("invariant names", CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(3) == "March"
            && DateTimeFormatInfo.InvariantInfo.ShortDatePattern == "MM/dd/yyyy");
        Section("ISO weeks and names");

        // docs:stopwatch
        Stopwatch watch = Stopwatch.StartNew();
        long sum = 0;
        for (int i = 0; i < 100_000; i++)
            sum += i;
        watch.Stop();
        TimeSpan took = watch.Elapsed;
        // docs:end
        Check("Elapsed", sum == 4_999_950_000 && took >= TimeSpan.Zero && !watch.IsRunning);
        Check("ticks are nanoseconds", Stopwatch.Frequency == 1_000_000_000
            && watch.ElapsedMilliseconds == watch.ElapsedTicks / 1_000_000);
        long before = Stopwatch.GetTimestamp();
        Check("timestamps move forward", Stopwatch.GetTimestamp() >= before
            && Stopwatch.GetElapsedTime(before) >= TimeSpan.Zero);
        watch.Restart();
        Check("Restart", watch.IsRunning);
        Section("Stopwatch");

        Console.WriteLine(s_failures == 0 ? "\nAll checks passed." : $"\n{s_failures} checks failed.");
        return s_failures;
    }
}
