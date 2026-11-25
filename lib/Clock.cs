// The time: a clock for measuring how long something takes, and the date and
// time of day.
//
//   long start = Clock.Monotonic;
//   Work();
//   Console.WriteLine($"took {(Clock.Monotonic - start) / 1_000_000} ms");
//
//   CalendarTime now = Clock.UtcNow;
//   Console.WriteLine($"{now.Year}-{now.Month:D2}-{now.Day:D2}");
//
// Both come from the C library's clock_gettime, with Linux's clock numbers:
// 1 for the monotonic clock and 0 for the wall clock. Each target's runtime
// answers both (win/, mac/, wasm/, dos/ and efi/runtime.c). On DOS and UEFI the
// wall clock is the PC's, which usually keeps local time rather than UTC.

using System;
using System.Runtime.InteropServices;

/// <summary>The monotonic clock, for measuring durations, and the wall clock.</summary>
public static class Clock
{
    [DllImport("libc")]
    static unsafe extern int clock_gettime(int clock, long* timespec);

    const int Realtime = 0;
    const int MonotonicClock = 1;

    static long Nanoseconds(int clock)
    {
        Span<long> timespec = stackalloc long[2];
        unsafe
        {
            // SAFETY: clock_gettime writes two longs: seconds, then nanoseconds.
            fixed (long* t = timespec)
                if (clock_gettime(clock, t) != 0)
                    return 0;
        }
        return timespec[0] * 1_000_000_000 + timespec[1];
    }

    /// <summary>
    /// Nanoseconds from an arbitrary starting point, on a clock that only moves forward,
    /// whatever happens to the time of day. Subtract two readings to measure a duration.
    /// Its resolution depends on the target: about a millisecond on DOS.
    /// </summary>
    public static long Monotonic => Nanoseconds(MonotonicClock);

    /// <summary>Milliseconds since 1970-01-01 00:00 UTC, by the system's clock.</summary>
    public static long UnixMilliseconds => FloorDivide(Nanoseconds(Realtime), 1_000_000);

    /// <summary>Seconds since 1970-01-01 00:00 UTC, by the system's clock.</summary>
    public static long UnixSeconds => FloorDivide(Nanoseconds(Realtime), 1_000_000_000);

    /// <summary>
    /// The current date and time of day in UTC, to the millisecond. On DOS and UEFI it is
    /// what the PC's clock says, which is usually local time.
    /// </summary>
    public static CalendarTime UtcNow => CalendarTime.FromUnixMilliseconds(UnixMilliseconds);

    internal static long FloorDivide(long value, long divisor)
    {
        long quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }
}

/// <summary>
/// A date and time of day, in the proleptic Gregorian calendar, with no time zone:
/// Clock.UtcNow, or the conversion of a Unix time.
/// </summary>
public struct CalendarTime
{
    /// <summary>The year, such as 2026.</summary>
    public int Year;
    /// <summary>The month, 1 to 12.</summary>
    public int Month;
    /// <summary>The day of the month, 1 to 31.</summary>
    public int Day;
    /// <summary>0 to 23.</summary>
    public int Hour;
    /// <summary>0 to 59.</summary>
    public int Minute;
    /// <summary>0 to 59.</summary>
    public int Second;
    /// <summary>0 to 999.</summary>
    public int Millisecond;
    /// <summary>0 for Sunday to 6 for Saturday, as .NET's DayOfWeek.</summary>
    public int DayOfWeek;

    const long MillisecondsPerDay = 86_400_000;

    /// <summary>The date and time <paramref name="milliseconds"/> after 1970-01-01 00:00 (before, if negative).</summary>
    public static CalendarTime FromUnixMilliseconds(long milliseconds)
    {
        long days = Clock.FloorDivide(milliseconds, MillisecondsPerDay);
        int inDay = (int)(milliseconds - days * MillisecondsPerDay);
        var time = new CalendarTime
        {
            Hour = inDay / 3_600_000,
            Minute = inDay / 60_000 % 60,
            Second = inDay / 1000 % 60,
            Millisecond = inDay % 1000,
            // 1970-01-01 was a Thursday.
            DayOfWeek = (int)(days - Clock.FloorDivide(days + 4, 7) * 7 + 4),
        };

        // Howard Hinnant's civil_from_days: days since 1970-01-01 to a date, in
        // 400-year eras that start on March 1st, so leap days fall at the end.
        long z = days + 719468;
        long era = Clock.FloorDivide(z, 146097);
        long dayOfEra = z - era * 146097;
        long yearOfEra = (dayOfEra - dayOfEra / 1460 + dayOfEra / 36524 - dayOfEra / 146096) / 365;
        long dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
        long shiftedMonth = (5 * dayOfYear + 2) / 153; // 0 is March
        time.Day = (int)(dayOfYear - (153 * shiftedMonth + 2) / 5 + 1);
        time.Month = (int)(shiftedMonth < 10 ? shiftedMonth + 3 : shiftedMonth - 9);
        time.Year = (int)(yearOfEra + era * 400 + (time.Month <= 2 ? 1 : 0));
        return time;
    }

    /// <summary>Milliseconds since 1970-01-01 00:00: the inverse of FromUnixMilliseconds.</summary>
    public long ToUnixMilliseconds()
    {
        // days_from_civil, the inverse of the above.
        long year = Year - (Month <= 2 ? 1 : 0);
        long era = Clock.FloorDivide(year, 400);
        long yearOfEra = year - era * 400;
        long dayOfYear = (153 * (Month > 2 ? Month - 3 : Month + 9) + 2) / 5 + Day - 1;
        long dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        long days = era * 146097 + dayOfEra - 719468;
        return days * MillisecondsPerDay + Hour * 3_600_000L + Minute * 60_000L + Second * 1000L + Millisecond;
    }
}
