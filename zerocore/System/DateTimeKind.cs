namespace System
{
    // Whether a DateTime is local time, UTC, or neither, as in .NET. There
    // are no time zones here: local time is UTC (see TimeZoneInfo.Local).
    public enum DateTimeKind
    {
        Unspecified = 0,
        Utc = 1,
        Local = 2,
    }

    public enum DayOfWeek
    {
        Sunday = 0,
        Monday = 1,
        Tuesday = 2,
        Wednesday = 3,
        Thursday = 4,
        Friday = 5,
        Saturday = 6,
    }
}

namespace System.Globalization
{
    // What date and time parsing accepts and how it reads the time zone,
    // numbered as in .NET.
    [Flags]
    public enum DateTimeStyles
    {
        None = 0,
        AllowLeadingWhite = 1,
        AllowTrailingWhite = 2,
        AllowInnerWhite = 4,
        AllowWhiteSpaces = AllowLeadingWhite | AllowInnerWhite | AllowTrailingWhite,
        NoCurrentDateDefault = 8,
        AdjustToUniversal = 16,
        AssumeLocal = 32,
        AssumeUniversal = 64,
        RoundtripKind = 128,
    }

    [Flags]
    public enum TimeSpanStyles
    {
        None = 0,
        AssumeNegative = 1,
    }

    public enum CalendarWeekRule
    {
        FirstDay = 0,
        FirstFullWeek = 1,
        FirstFourDayWeek = 2,
    }
}
