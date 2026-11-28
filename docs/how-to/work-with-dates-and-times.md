---
title: Work with dates and times
index: 32
---

# Work with dates and times

Dates are easy to get subtly wrong: January 31 plus a month, a year that is a leap year except when it
isn't, a meeting at 9:00 that is a different instant in Istanbul and in New York, an hour that happens
twice on the night the clocks go back. .NET's date types already handle all of that, and the code
people write with them is everywhere. So zero mode has the same types, with .NET's members, results and
messages: `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `TimeZoneInfo` and
`ISOWeek`. Code that uses them compiles unchanged.

```text
  DateOnly  2026-12-24            a day, no time            (days since 0001-01-01)
  TimeOnly             22:30      a time of day, no date    (ticks since midnight)
  DateTime  2026-12-24 22:30      both, and a Kind:         (ticks of 100 ns)
                                  Utc, Local or Unspecified
  DateTimeOffset
            2026-12-24 22:30 +03:00
                                  both, and its offset from UTC: one instant
  TimeSpan  1.02:03:04.5          a length of time          (ticks of 100 ns)
```

`samples/Dates.cs` runs every example on this page.

## Build dates and do arithmetic

<!-- sample: samples/Dates.cs build -->
```csharp
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
```

`AddMonths` keeps the day where it can and moves it back where it can't (January 31 plus a month is
February 28), and a date out of range throws `ArgumentOutOfRangeException` with .NET's message.
`DateTime`, `DateOnly` and `TimeOnly` are values of 8 bytes or less, so nothing here allocates.

## Format them

`ToString` and `TryFormat` take .NET's standard formats (`"o"`, `"s"`, `"R"`, `"d"`, ...) and custom
patterns (`"yyyy-MM-dd HH:mm"`), in the invariant culture:

<!-- sample: samples/Dates.cs formats -->
```csharp
Is("round trip", t.ToString("o"), "2026-03-05T14:07:09.0450000");
Is("sortable", t.ToString("s"), "2026-03-05T14:07:09");
Is("RFC 1123", t.ToString("R"), "Thu, 05 Mar 2026 14:07:09 GMT");
Is("short date", t.ToString("d"), "03/05/2026");
Is("long date", t.ToString("D"), "Thursday, 05 March 2026");
Is("custom", t.ToString("yyyy-MM-dd HH:mm:ss.fff"), "2026-03-05 14:07:09.045");
Is("names and 12 hours", t.ToString("dddd d MMMM, h:mm tt"),
    "Thursday 5 March, 2:07 PM");
Is("literals", t.ToString("'at' HH\\h"), "at 14h");
```

`ToString` allocates the string and never frees it, like every string the core library makes. In a
loop, use `TryFormat` into a span, or a hole, which formats straight into the output:
`Console.WriteLine($"{t:yyyy-MM-dd}")` or `Str.Format(arena, $"{t:o}")`. A format .NET refuses throws
the same `FormatException`.

A `DateTimeFormatInfo` of your own changes the names, patterns, separators and AM/PM designators
that `ToString` uses ("o", "r", "s" and "u" stay the same everywhere, as in .NET):

<!-- sample: samples/CustomFormats.cs dates -->
```csharp
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
```

## Offsets: one instant, anywhere

A `DateTime` is a clock reading; a `DateTimeOffset` is a reading and how far it is from UTC, so it
names one instant. Two are equal when they are the same instant, whatever their offsets:

<!-- sample: samples/Dates.cs offsets -->
```csharp
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
```

## Dates without times, times without dates

<!-- sample: samples/Dates.cs dateonly -->
```csharp
DateOnly due = new DateOnly(2026, 12, 24);
Is("a date", due.ToString("o"), "2026-12-24");
Check("days between",
    due.DayNumber - new DateOnly(2026, 11, 28).DayNumber == 26);
TimeOnly start = new TimeOnly(22, 30);
TimeOnly end = start.Add(TimeSpan.FromHours(3), out int wrappedDays);
Check("wraps past midnight", end == new TimeOnly(1, 30) && wrappedDays == 1);
Check("IsBetween across midnight", new TimeOnly(23, 0).IsBetween(start, end));
```

## Lengths of time

<!-- sample: samples/Dates.cs timespan -->
```csharp
TimeSpan span = new TimeSpan(1, 2, 3, 4, 500);
Is("constant", span.ToString(), "1.02:03:04.5000000");
Is("general", span.ToString("g"), "1:2:03:04.5");
Is("custom", span.ToString("hh\\:mm\\:ss"), "02:03:04");
Check("exact from integers",
    TimeSpan.FromHours(1, 30) == TimeSpan.FromMinutes(90));
Check("multiply", span * 2 == new TimeSpan(2, 4, 6, 9));
```

The `From*` overloads that take integers (.NET 9's) are exact; the ones that take a `double` truncate
to a tick, as in .NET.

## Time zones and local time

`DateTime.Now`, `ToLocalTime` and `DateTimeOffset.Now` use the system's time zone, through the C
library's `localtime_r`, which knows the zone and its daylight saving rules for any year. A custom
zone with a fixed offset converts like any other:

<!-- sample: samples/Dates.cs zones -->
```csharp
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
```

```text
  target                             local time comes from
  ---------------------------------  ------------------------------------------
  Linux, macOS, iOS, Android         the tz database, through localtime_r
                                     (Id: TZ, or where /etc/localtime points)
  Windows                            the system's settings (Id: TZ or "Local")
  WebAssembly, DOS, UEFI, C64,       nothing: local time is UTC
  microcontrollers
```

Ambiguous and skipped local times (when the clocks go back or forward) count as standard time, and
old local mean times are rounded to whole minutes, both as .NET does. On DOS and UEFI the clock is the
PC's, which usually keeps local time, so there `UtcNow` is really local time.

## What's different from .NET

- **No time zone database.** `FindSystemTimeZoneById` knows `"UTC"` and the local zone's Id; any
  other Id throws `TimeZoneNotFoundException`. `CreateCustomTimeZone` makes zones with a fixed
  offset, without adjustment rules.
- **On Windows** the local zone's Id and names are `TZ` or `"Local"`, not Windows' zone names, and
  dates before 1970 or after 3000 take the offset at that end.
- **No `Calendar` overloads** (the calendar is always the Gregorian one), no `IConvertible`, and no
  serialization.
- **No parsing yet.** `Parse`, `TryParse` and `ParseExact` come next.

`scripts/datetime-check/check-datetime.sh` compares all of this with .NET's over millions of values,
in six time zones.
