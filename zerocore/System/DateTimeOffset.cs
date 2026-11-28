namespace System
{
    // A date and time with its offset from UTC, with .NET's members, results
    // and messages: the UTC time (a DateTime) and the offset in minutes.
    // Two are equal when they are the same instant, whatever their offsets
    // (EqualsExact compares the offsets too). No Calendar overloads.
    public readonly struct DateTimeOffset : IComparable, IComparable<DateTimeOffset>, IEquatable<DateTimeOffset>, ISpanFormattable
    {
        private const long MaxOffset = 14 * TimeSpan.TicksPerHour;
        private const long MinOffset = -MaxOffset;
        private const long UnixEpochSeconds = DateTime.UnixEpochTicks / TimeSpan.TicksPerSecond;
        private const long UnixEpochMilliseconds = DateTime.UnixEpochTicks / TimeSpan.TicksPerMillisecond;
        internal const long UnixMinSeconds = DateTime.MinTicks / TimeSpan.TicksPerSecond - UnixEpochSeconds;
        internal const long UnixMaxSeconds = DateTime.MaxTicks / TimeSpan.TicksPerSecond - UnixEpochSeconds;

        public static readonly DateTimeOffset MinValue = new DateTimeOffset(0, TimeSpan.Zero);
        public static readonly DateTimeOffset MaxValue = new DateTimeOffset(DateTime.MaxTicks, TimeSpan.Zero);
        public static readonly DateTimeOffset UnixEpoch = new DateTimeOffset(DateTime.UnixEpochTicks, TimeSpan.Zero);

        // The UTC time, with Kind Unspecified, and the offset.
        private readonly DateTime _dateTime;
        private readonly int _offsetMinutes;

        private DateTimeOffset(int offsetMinutes, DateTime utc)
        {
            _dateTime = utc;
            _offsetMinutes = offsetMinutes;
        }

        public DateTimeOffset(long ticks, TimeSpan offset)
        {
            _offsetMinutes = ValidateOffset(offset);
            // Let the DateTime constructor check the ticks.
            DateTime dateTime = new DateTime(ticks);
            _dateTime = ValidateDate(dateTime, offset);
        }

        public DateTimeOffset(DateTime dateTime)
        {
            long offset;
            if (dateTime.Kind != DateTimeKind.Utc)
                offset = TimeZoneInfo.Local.GetUtcOffset(dateTime).Ticks;
            else
                offset = 0;
            _offsetMinutes = (int)(offset / TimeSpan.TicksPerMinute);
            _dateTime = ValidateDate(dateTime, new TimeSpan(offset));
        }

        public DateTimeOffset(DateTime dateTime, TimeSpan offset)
        {
            if (dateTime.Kind == DateTimeKind.Local)
            {
                if (offset != TimeZoneInfo.Local.GetUtcOffset(dateTime))
                    throw new ArgumentException("The UTC Offset of the local dateTime parameter does not match the offset argument.", "offset");
            }
            else if (dateTime.Kind == DateTimeKind.Utc)
            {
                if (offset != TimeSpan.Zero)
                    throw new ArgumentException("The UTC Offset for Utc DateTime instances must be 0.", "offset");
            }
            _offsetMinutes = ValidateOffset(offset);
            _dateTime = ValidateDate(dateTime, offset);
        }

        public DateTimeOffset(DateOnly date, TimeOnly time, TimeSpan offset)
            : this(new DateTime(date, time), offset) { }

        public DateTimeOffset(int year, int month, int day, int hour, int minute, int second, TimeSpan offset)
        {
            _offsetMinutes = ValidateOffset(offset);
            _dateTime = ValidateDate(new DateTime(year, month, day, hour, minute, second), offset);
        }

        public DateTimeOffset(int year, int month, int day, int hour, int minute, int second, int millisecond, TimeSpan offset)
        {
            _offsetMinutes = ValidateOffset(offset);
            _dateTime = ValidateDate(new DateTime(year, month, day, hour, minute, second, millisecond), offset);
        }

        public DateTimeOffset(int year, int month, int day, int hour, int minute, int second, int millisecond, int microsecond, TimeSpan offset)
        {
            _offsetMinutes = ValidateOffset(offset);
            _dateTime = ValidateDate(new DateTime(year, month, day, hour, minute, second, millisecond, microsecond), offset);
        }

        private static int ValidateOffset(TimeSpan offset)
        {
            long ticks = offset.Ticks;
            if (ticks % TimeSpan.TicksPerMinute != 0)
                throw new ArgumentException("Offset must be specified in whole minutes.", "offset");
            if (ticks < MinOffset || ticks > MaxOffset)
                throw new ArgumentOutOfRangeException("offset", "Offset must be within plus or minus 14 hours.");
            return (int)(ticks / TimeSpan.TicksPerMinute);
        }

        private static DateTime ValidateDate(DateTime dateTime, TimeSpan offset)
        {
            long utcTicks = dateTime.Ticks - offset.Ticks;
            if ((ulong)utcTicks > DateTime.MaxTicks)
                throw new ArgumentOutOfRangeException("offset", "The UTC time represented when the offset is applied must be between year 0 and 10,000.");
            return new DateTime(utcTicks, DateTimeKind.Unspecified);
        }

        public static DateTimeOffset Now => ToLocalTime(DateTime.UtcNow, true);

        public static DateTimeOffset UtcNow => new DateTimeOffset(0, new DateTime(DateTime.UtcNow.Ticks));

        public DateTime DateTime => ClockDateTime;
        public DateTime UtcDateTime => DateTime.SpecifyKind(_dateTime, DateTimeKind.Utc);
        public DateTime LocalDateTime => UtcDateTime.ToLocalTime();

        private DateTime ClockDateTime => new DateTime(_dateTime.Ticks + _offsetMinutes * TimeSpan.TicksPerMinute);

        public DateTime Date => ClockDateTime.Date;
        public int Year => ClockDateTime.Year;
        public int Month => ClockDateTime.Month;
        public int Day => ClockDateTime.Day;
        public DayOfWeek DayOfWeek => ClockDateTime.DayOfWeek;
        public int DayOfYear => ClockDateTime.DayOfYear;
        public int Hour => ClockDateTime.Hour;
        public int Minute => ClockDateTime.Minute;
        public int Second => ClockDateTime.Second;
        public int Millisecond => ClockDateTime.Millisecond;
        public int Microsecond => ClockDateTime.Microsecond;
        public int Nanosecond => ClockDateTime.Nanosecond;
        public TimeSpan TimeOfDay => ClockDateTime.TimeOfDay;
        public long Ticks => ClockDateTime.Ticks;
        public long UtcTicks => _dateTime.Ticks;
        public TimeSpan Offset => new TimeSpan(_offsetMinutes * TimeSpan.TicksPerMinute);
        public int TotalOffsetMinutes => _offsetMinutes;

        public void Deconstruct(out DateOnly date, out TimeOnly time, out TimeSpan offset)
        {
            (date, time) = ClockDateTime;
            offset = Offset;
        }

        // ---- arithmetic ----

        public DateTimeOffset Add(TimeSpan timeSpan) => new DateTimeOffset(ClockDateTime.Add(timeSpan), Offset);
        public DateTimeOffset AddDays(double days) => new DateTimeOffset(ClockDateTime.AddDays(days), Offset);
        public DateTimeOffset AddHours(double hours) => new DateTimeOffset(ClockDateTime.AddHours(hours), Offset);
        public DateTimeOffset AddMinutes(double minutes) => new DateTimeOffset(ClockDateTime.AddMinutes(minutes), Offset);
        public DateTimeOffset AddSeconds(double seconds) => new DateTimeOffset(ClockDateTime.AddSeconds(seconds), Offset);
        public DateTimeOffset AddMilliseconds(double milliseconds) => new DateTimeOffset(ClockDateTime.AddMilliseconds(milliseconds), Offset);
        public DateTimeOffset AddMicroseconds(double microseconds) => new DateTimeOffset(ClockDateTime.AddMicroseconds(microseconds), Offset);
        public DateTimeOffset AddTicks(long ticks) => new DateTimeOffset(ClockDateTime.AddTicks(ticks), Offset);
        public DateTimeOffset AddMonths(int months) => new DateTimeOffset(ClockDateTime.AddMonths(months), Offset);
        public DateTimeOffset AddYears(int years) => new DateTimeOffset(ClockDateTime.AddYears(years), Offset);

        public TimeSpan Subtract(DateTimeOffset value) => UtcDateTime.Subtract(value.UtcDateTime);
        public DateTimeOffset Subtract(TimeSpan value) => new DateTimeOffset(ClockDateTime.Subtract(value), Offset);

        public static DateTimeOffset operator +(DateTimeOffset dateTimeOffset, TimeSpan timeSpan)
            => new DateTimeOffset(dateTimeOffset.ClockDateTime + timeSpan, dateTimeOffset.Offset);

        public static DateTimeOffset operator -(DateTimeOffset dateTimeOffset, TimeSpan timeSpan)
            => new DateTimeOffset(dateTimeOffset.ClockDateTime - timeSpan, dateTimeOffset.Offset);

        public static TimeSpan operator -(DateTimeOffset left, DateTimeOffset right) => left.UtcDateTime - right.UtcDateTime;

        public static implicit operator DateTimeOffset(DateTime dateTime) => new DateTimeOffset(dateTime);

        // ---- comparing ----

        public static bool operator ==(DateTimeOffset left, DateTimeOffset right) => left.UtcTicks == right.UtcTicks;
        public static bool operator !=(DateTimeOffset left, DateTimeOffset right) => left.UtcTicks != right.UtcTicks;
        public static bool operator <(DateTimeOffset left, DateTimeOffset right) => left.UtcTicks < right.UtcTicks;
        public static bool operator <=(DateTimeOffset left, DateTimeOffset right) => left.UtcTicks <= right.UtcTicks;
        public static bool operator >(DateTimeOffset left, DateTimeOffset right) => left.UtcTicks > right.UtcTicks;
        public static bool operator >=(DateTimeOffset left, DateTimeOffset right) => left.UtcTicks >= right.UtcTicks;

        public static int Compare(DateTimeOffset first, DateTimeOffset second) => first.UtcTicks.CompareTo(second.UtcTicks);
        public int CompareTo(DateTimeOffset other) => UtcTicks.CompareTo(other.UtcTicks);

        int IComparable.CompareTo(object obj)
        {
            if (obj == null)
                return 1;
            if (obj is not DateTimeOffset other)
                throw new ArgumentException("Object must be of type DateTimeOffset.");
            return CompareTo(other);
        }

        public static bool Equals(DateTimeOffset first, DateTimeOffset second) => first.UtcTicks == second.UtcTicks;
        public bool Equals(DateTimeOffset other) => UtcTicks == other.UtcTicks;
        public override bool Equals(object obj) => obj is DateTimeOffset other && UtcTicks == other.UtcTicks;
        public bool EqualsExact(DateTimeOffset other) => UtcTicks == other.UtcTicks && _offsetMinutes == other._offsetMinutes;
        public override int GetHashCode() => UtcDateTime.GetHashCode();

        // ---- conversions ----

        public DateTimeOffset ToOffset(TimeSpan offset) => new DateTimeOffset((_dateTime + offset).Ticks, offset);

        public DateTimeOffset ToUniversalTime() => new DateTimeOffset(UtcDateTime);

        public DateTimeOffset ToLocalTime() => ToLocalTime(UtcDateTime, false);

        private static DateTimeOffset ToLocalTime(DateTime utc, bool throwOnOverflow)
        {
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(utc);
            long localTicks = utc.Ticks + offset.Ticks;
            if (localTicks < DateTime.MinTicks || localTicks > DateTime.MaxTicks)
            {
                if (throwOnOverflow)
                    throw new ArgumentException("Specified argument was out of the range of valid values.");
                localTicks = localTicks < DateTime.MinTicks ? DateTime.MinTicks : DateTime.MaxTicks;
            }
            return new DateTimeOffset(localTicks, offset);
        }

        public long ToFileTime() => UtcDateTime.ToFileTime();

        public static DateTimeOffset FromFileTime(long fileTime) => ToLocalTime(DateTime.FromFileTimeUtc(fileTime), true);

        public long ToUnixTimeSeconds() => _dateTime.Ticks / TimeSpan.TicksPerSecond - UnixEpochSeconds;

        public long ToUnixTimeMilliseconds() => _dateTime.Ticks / TimeSpan.TicksPerMillisecond - UnixEpochMilliseconds;

        public static DateTimeOffset FromUnixTimeSeconds(long seconds)
        {
            if (seconds < UnixMinSeconds || seconds > UnixMaxSeconds)
                throw new ArgumentOutOfRangeException("seconds", seconds, "Valid values are between " + UnixMinSeconds.ToString() + " and " + UnixMaxSeconds.ToString() + ", inclusive.");
            return new DateTimeOffset(0, new DateTime(seconds * TimeSpan.TicksPerSecond + DateTime.UnixEpochTicks));
        }

        public static DateTimeOffset FromUnixTimeMilliseconds(long milliseconds)
        {
            const long MinMilliseconds = DateTime.MinTicks / TimeSpan.TicksPerMillisecond - UnixEpochMilliseconds;
            const long MaxMilliseconds = DateTime.MaxTicks / TimeSpan.TicksPerMillisecond - UnixEpochMilliseconds;
            if (milliseconds < MinMilliseconds || milliseconds > MaxMilliseconds)
                throw new ArgumentOutOfRangeException("milliseconds", milliseconds, "Valid values are between " + MinMilliseconds.ToString() + " and " + MaxMilliseconds.ToString() + ", inclusive.");
            return new DateTimeOffset(0, new DateTime(milliseconds * TimeSpan.TicksPerMillisecond + DateTime.UnixEpochTicks));
        }

        // ---- text ----

        public override string ToString() => DateTimeFormat.Format(ClockDateTime, null, Offset);
        public string ToString(string format) => DateTimeFormat.Format(ClockDateTime, format, Offset);
        public string ToString(IFormatProvider formatProvider) => DateTimeFormat.Format(ClockDateTime, null, Offset);
        public string ToString(string format, IFormatProvider formatProvider) => DateTimeFormat.Format(ClockDateTime, format, Offset);

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider formatProvider = null)
            => DateTimeFormat.TryFormat(ClockDateTime, destination, out charsWritten, format, Offset);
    }
}
