namespace System
{
    // A date with no time of day: the number of days since 0001-01-01, with
    // .NET's members, results and messages. No Calendar overloads.
    public readonly struct DateOnly : IComparable, IComparable<DateOnly>, IEquatable<DateOnly>, ISpanFormattable
    {
        private const int MaxDayNumber = DateTime.DaysTo10000 - 1;

        private readonly int _dayNumber;

        private DateOnly(int dayNumber) => _dayNumber = dayNumber;

        public DateOnly(int year, int month, int day) => _dayNumber = (int)(DateTime.DateToTicks(year, month, day) / TimeSpan.TicksPerDay);

        public static DateOnly MinValue => new DateOnly(0);
        public static DateOnly MaxValue => new DateOnly(MaxDayNumber);

        public static DateOnly FromDayNumber(int dayNumber)
        {
            if ((uint)dayNumber > MaxDayNumber)
                throw new ArgumentOutOfRangeException("dayNumber", dayNumber, "Day number must be between 0 and DateOnly.MaxValue.DayNumber.");
            return new DateOnly(dayNumber);
        }

        public static DateOnly FromDateTime(DateTime dateTime) => new DateOnly((int)(dateTime.Ticks / TimeSpan.TicksPerDay));

        public int DayNumber => _dayNumber;

        private DateTime Equivalent => new DateTime(_dayNumber * TimeSpan.TicksPerDay);

        public int Year { get { DateTime.GetDate(_dayNumber, out int y, out _, out _, out _); return y; } }
        public int Month { get { DateTime.GetDate(_dayNumber, out _, out int m, out _, out _); return m; } }
        public int Day { get { DateTime.GetDate(_dayNumber, out _, out _, out int d, out _); return d; } }
        public int DayOfYear { get { DateTime.GetDate(_dayNumber, out _, out _, out _, out int n); return n + 1; } }
        public DayOfWeek DayOfWeek => (DayOfWeek)((_dayNumber + 1) % 7);

        public void Deconstruct(out int year, out int month, out int day) => DateTime.GetDate(_dayNumber, out year, out month, out day, out _);

        public DateOnly AddDays(int value)
        {
            int dayNumber = _dayNumber + value;
            if ((uint)dayNumber > MaxDayNumber)
                throw new ArgumentOutOfRangeException("value", "Value to add was out of range.");
            return new DateOnly(dayNumber);
        }

        public DateOnly AddMonths(int value) => FromDateTime(Equivalent.AddMonths(value));
        public DateOnly AddYears(int value) => FromDateTime(Equivalent.AddYears(value));

        public DateTime ToDateTime(TimeOnly time) => new DateTime(_dayNumber * TimeSpan.TicksPerDay + time.Ticks);
        public DateTime ToDateTime(TimeOnly time, DateTimeKind kind) => new DateTime(_dayNumber * TimeSpan.TicksPerDay + time.Ticks, kind);

        public static bool operator ==(DateOnly left, DateOnly right) => left._dayNumber == right._dayNumber;
        public static bool operator !=(DateOnly left, DateOnly right) => left._dayNumber != right._dayNumber;
        public static bool operator <(DateOnly left, DateOnly right) => left._dayNumber < right._dayNumber;
        public static bool operator <=(DateOnly left, DateOnly right) => left._dayNumber <= right._dayNumber;
        public static bool operator >(DateOnly left, DateOnly right) => left._dayNumber > right._dayNumber;
        public static bool operator >=(DateOnly left, DateOnly right) => left._dayNumber >= right._dayNumber;

        public int CompareTo(DateOnly value) => _dayNumber.CompareTo(value._dayNumber);

        public int CompareTo(object value)
        {
            if (value == null)
                return 1;
            if (value is not DateOnly other)
                throw new ArgumentException("Object must be of type DateOnly.");
            return CompareTo(other);
        }

        public bool Equals(DateOnly value) => _dayNumber == value._dayNumber;
        public override bool Equals(object value) => value is DateOnly other && _dayNumber == other._dayNumber;
        public override int GetHashCode() => _dayNumber;

        public override string ToString() => DateTimeFormat.FormatDate(this, null);
        public string ToString(string format) => DateTimeFormat.FormatDate(this, format);
        public string ToString(IFormatProvider provider) => DateTimeFormat.FormatDate(this, null);
        public string ToString(string format, IFormatProvider provider) => DateTimeFormat.FormatDate(this, format);
        public string ToLongDateString() => DateTimeFormat.FormatDate(this, "D");
        public string ToShortDateString() => DateTimeFormat.FormatDate(this, "d");

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null)
            => DateTimeFormat.TryFormatDate(this, destination, out charsWritten, format);
    }

    // A time of day with no date: ticks since midnight, with .NET's members,
    // results and messages. Adding wraps around midnight.
    public readonly struct TimeOnly : IComparable, IComparable<TimeOnly>, IEquatable<TimeOnly>, ISpanFormattable
    {
        private const long MaxTimeTicks = TimeSpan.TicksPerDay - 1;

        private readonly long _ticks;

        public TimeOnly(int hour, int minute) : this(DateTime.TimeToTicks(hour, minute, 0)) { }
        public TimeOnly(int hour, int minute, int second) : this(DateTime.TimeToTicks(hour, minute, second)) { }
        public TimeOnly(int hour, int minute, int second, int millisecond) : this(TimeTicks(hour, minute, second, millisecond, 0)) { }
        public TimeOnly(int hour, int minute, int second, int millisecond, int microsecond) : this(TimeTicks(hour, minute, second, millisecond, microsecond)) { }

        public TimeOnly(long ticks)
        {
            if ((ulong)ticks > MaxTimeTicks)
                throw new ArgumentOutOfRangeException("ticks", "Ticks must be between 0 and and TimeOnly.MaxValue.Ticks.");
            _ticks = ticks;
        }

        private static long TimeTicks(int hour, int minute, int second, int millisecond, int microsecond)
        {
            long ticks = DateTime.TimeToTicks(hour, minute, second);
            if ((uint)millisecond >= 1000)
                throw new ArgumentOutOfRangeException("millisecond", "Valid values are between 0 and 999, inclusive.");
            if ((uint)microsecond >= 1000)
                throw new ArgumentOutOfRangeException("microsecond", "Valid values are between 0 and 999, inclusive.");
            return ticks + millisecond * TimeSpan.TicksPerMillisecond + microsecond * TimeSpan.TicksPerMicrosecond;
        }

        public static TimeOnly MinValue => new TimeOnly(0);
        public static TimeOnly MaxValue => new TimeOnly(MaxTimeTicks);

        public int Hour => (int)(_ticks / TimeSpan.TicksPerHour);
        public int Minute => (int)(_ticks / TimeSpan.TicksPerMinute % 60);
        public int Second => (int)(_ticks / TimeSpan.TicksPerSecond % 60);
        public int Millisecond => (int)(_ticks / TimeSpan.TicksPerMillisecond % 1000);
        public int Microsecond => (int)(_ticks / TimeSpan.TicksPerMicrosecond % 1000);
        public int Nanosecond => (int)(_ticks % TimeSpan.TicksPerMicrosecond * TimeSpan.NanosecondsPerTick);
        public long Ticks => _ticks;

        public static TimeOnly FromDateTime(DateTime dateTime) => new TimeOnly(dateTime.TimeOfDay.Ticks);
        public static TimeOnly FromTimeSpan(TimeSpan timeSpan) => new TimeOnly(timeSpan.Ticks);
        public TimeSpan ToTimeSpan() => new TimeSpan(_ticks);

        public TimeOnly Add(TimeSpan value) => Add(value, out _);

        public TimeOnly Add(TimeSpan value, out int wrappedDays)
        {
            long days = value.Ticks / TimeSpan.TicksPerDay;
            long ticks = _ticks + value.Ticks % TimeSpan.TicksPerDay;
            if (ticks < 0)
            {
                days--;
                ticks += TimeSpan.TicksPerDay;
            }
            else if (ticks >= TimeSpan.TicksPerDay)
            {
                days++;
                ticks -= TimeSpan.TicksPerDay;
            }
            wrappedDays = (int)days;
            return new TimeOnly(ticks);
        }

        public TimeOnly AddHours(double value) => AddHours(value, out _);
        public TimeOnly AddHours(double value, out int wrappedDays) => Add(new TimeSpan(Saturate(value * TimeSpan.TicksPerHour)), out wrappedDays);
        public TimeOnly AddMinutes(double value) => AddMinutes(value, out _);
        public TimeOnly AddMinutes(double value, out int wrappedDays) => Add(new TimeSpan(Saturate(value * TimeSpan.TicksPerMinute)), out wrappedDays);

        // (long)value as .NET 9 converts it: NaN is 0, and past the range the
        // nearest end.
        private static long Saturate(double value)
            => value != value ? 0 : value >= 9.2233720368547758E18 ? long.MaxValue : value <= -9.2233720368547758E18 ? long.MinValue : (long)value;

        // Whether the time is in [start, end), going past midnight when end
        // is before start.
        public bool IsBetween(TimeOnly start, TimeOnly end)
        {
            long startTicks = start._ticks, endTicks = end._ticks;
            return startTicks <= endTicks
                ? startTicks <= _ticks && endTicks > _ticks
                : startTicks <= _ticks || endTicks > _ticks;
        }

        public void Deconstruct(out int hour, out int minute)
        {
            hour = Hour;
            minute = Minute;
        }

        public void Deconstruct(out int hour, out int minute, out int second)
        {
            Deconstruct(out hour, out minute);
            second = Second;
        }

        public void Deconstruct(out int hour, out int minute, out int second, out int millisecond)
        {
            Deconstruct(out hour, out minute, out second);
            millisecond = Millisecond;
        }

        public void Deconstruct(out int hour, out int minute, out int second, out int millisecond, out int microsecond)
        {
            Deconstruct(out hour, out minute, out second, out millisecond);
            microsecond = Microsecond;
        }

        // The time from t2 forward to t1, wrapping past midnight.
        public static TimeSpan operator -(TimeOnly t1, TimeOnly t2)
        {
            long difference = t1._ticks - t2._ticks;
            return new TimeSpan(difference < 0 ? difference + TimeSpan.TicksPerDay : difference);
        }

        public static bool operator ==(TimeOnly left, TimeOnly right) => left._ticks == right._ticks;
        public static bool operator !=(TimeOnly left, TimeOnly right) => left._ticks != right._ticks;
        public static bool operator <(TimeOnly left, TimeOnly right) => left._ticks < right._ticks;
        public static bool operator <=(TimeOnly left, TimeOnly right) => left._ticks <= right._ticks;
        public static bool operator >(TimeOnly left, TimeOnly right) => left._ticks > right._ticks;
        public static bool operator >=(TimeOnly left, TimeOnly right) => left._ticks >= right._ticks;

        public int CompareTo(TimeOnly value) => _ticks.CompareTo(value._ticks);

        public int CompareTo(object value)
        {
            if (value == null)
                return 1;
            if (value is not TimeOnly other)
                throw new ArgumentException("Object must be of type TimeOnly.");
            return CompareTo(other);
        }

        public bool Equals(TimeOnly value) => _ticks == value._ticks;
        public override bool Equals(object value) => value is TimeOnly other && _ticks == other._ticks;

        public override int GetHashCode()
        {
            long ticks = _ticks;
            return unchecked((int)ticks) ^ (int)(ticks >> 32);
        }

        public override string ToString() => DateTimeFormat.FormatTime(this, null);
        public string ToString(string format) => DateTimeFormat.FormatTime(this, format);
        public string ToString(IFormatProvider provider) => DateTimeFormat.FormatTime(this, null);
        public string ToString(string format, IFormatProvider provider) => DateTimeFormat.FormatTime(this, format);
        public string ToLongTimeString() => DateTimeFormat.FormatTime(this, "T");
        public string ToShortTimeString() => DateTimeFormat.FormatTime(this, "t");

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null)
            => DateTimeFormat.TryFormatTime(this, destination, out charsWritten, format);
    }
}
