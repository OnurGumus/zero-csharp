namespace System
{
    // A length of time, in ticks of 100 ns, with .NET's members, results and
    // messages: making, reading, comparing, adding, multiplying and dividing
    // them, and the standard ("c", "g", "G") and custom formats.
    // The From* methods taking a double truncate to a tick, as .NET's do;
    // past the range they throw OverflowException. Those taking integers are
    // exact, and throw ArgumentOutOfRangeException past the range.
    public readonly struct TimeSpan : IEquatable<TimeSpan>, IComparable<TimeSpan>, IComparable, ISpanFormattable
    {
        public const long NanosecondsPerTick = 100;
        public const long TicksPerMicrosecond = 10;
        public const long TicksPerMillisecond = 10_000;
        public const long TicksPerSecond = 10_000_000;
        public const long TicksPerMinute = 600_000_000;
        public const long TicksPerHour = 36_000_000_000;
        public const long TicksPerDay = 864_000_000_000;

        public const long MicrosecondsPerMillisecond = 1000;
        public const long MicrosecondsPerSecond = 1_000_000;
        public const long MicrosecondsPerMinute = 60_000_000;
        public const long MicrosecondsPerHour = 3_600_000_000;
        public const long MicrosecondsPerDay = 86_400_000_000;
        public const long MillisecondsPerSecond = 1000;
        public const long MillisecondsPerMinute = 60_000;
        public const long MillisecondsPerHour = 3_600_000;
        public const long MillisecondsPerDay = 86_400_000;
        public const long SecondsPerMinute = 60;
        public const long SecondsPerHour = 3600;
        public const long SecondsPerDay = 86_400;
        public const long MinutesPerHour = 60;
        public const long MinutesPerDay = 1440;
        public const int HoursPerDay = 24;

        private const long MaxMicroseconds = long.MaxValue / TicksPerMicrosecond;
        private const long MinMicroseconds = long.MinValue / TicksPerMicrosecond;
        private const string TooLong = "TimeSpan overflowed because the duration is too long.";

        public static readonly TimeSpan Zero = new TimeSpan(0);
        public static readonly TimeSpan MaxValue = new TimeSpan(long.MaxValue);
        public static readonly TimeSpan MinValue = new TimeSpan(long.MinValue);

        private readonly long _ticks;

        public TimeSpan(long ticks) => _ticks = ticks;

        public TimeSpan(int hours, int minutes, int seconds)
            : this(0, hours, minutes, seconds, 0) { }

        public TimeSpan(int days, int hours, int minutes, int seconds)
            : this(days, hours, minutes, seconds, 0) { }

        public TimeSpan(int days, int hours, int minutes, int seconds, int milliseconds)
            : this(days, hours, minutes, seconds, milliseconds, 0) { }

        public TimeSpan(int days, int hours, int minutes, int seconds, int milliseconds, int microseconds)
        {
            // In longs that wrap around, as .NET adds them (the From*
            // methods are exact).
            long totalMicroseconds = unchecked(days * MicrosecondsPerDay + hours * MicrosecondsPerHour + minutes * MicrosecondsPerMinute
                + seconds * MicrosecondsPerSecond + milliseconds * MicrosecondsPerMillisecond + microseconds);
            if (totalMicroseconds > MaxMicroseconds || totalMicroseconds < MinMicroseconds)
                throw new ArgumentOutOfRangeException(null, TooLong);
            _ticks = totalMicroseconds * TicksPerMicrosecond;
        }

        public long Ticks => _ticks;

        public int Days => (int)(_ticks / TicksPerDay);
        public int Hours => (int)(_ticks / TicksPerHour % 24);
        public int Minutes => (int)(_ticks / TicksPerMinute % 60);
        public int Seconds => (int)(_ticks / TicksPerSecond % 60);
        public int Milliseconds => (int)(_ticks / TicksPerMillisecond % 1000);
        public int Microseconds => (int)(_ticks / TicksPerMicrosecond % 1000);
        public int Nanoseconds => (int)(_ticks % TicksPerMicrosecond * NanosecondsPerTick);

        public double TotalDays => (double)_ticks / TicksPerDay;
        public double TotalHours => (double)_ticks / TicksPerHour;
        public double TotalMinutes => (double)_ticks / TicksPerMinute;
        public double TotalSeconds => (double)_ticks / TicksPerSecond;
        public double TotalMicroseconds => (double)_ticks / TicksPerMicrosecond;
        public double TotalNanoseconds => (double)_ticks * NanosecondsPerTick;
        public double TotalMilliseconds
        {
            get
            {
                double milliseconds = (double)_ticks / TicksPerMillisecond;
                // .NET clamps to the range of whole milliseconds a TimeSpan can hold.
                const double Max = (double)(long.MaxValue / TicksPerMillisecond);
                const double Min = (double)(long.MinValue / TicksPerMillisecond);
                return milliseconds > Max ? Max : milliseconds < Min ? Min : milliseconds;
            }
        }

        public static TimeSpan FromTicks(long ticks) => new TimeSpan(ticks);
        public static TimeSpan FromDays(double value) => Interval(value, TicksPerDay);
        public static TimeSpan FromHours(double value) => Interval(value, TicksPerHour);
        public static TimeSpan FromMinutes(double value) => Interval(value, TicksPerMinute);
        public static TimeSpan FromSeconds(double value) => Interval(value, TicksPerSecond);
        public static TimeSpan FromMilliseconds(double value) => Interval(value, TicksPerMillisecond);
        public static TimeSpan FromMicroseconds(double value) => Interval(value, TicksPerMicrosecond);

        private static TimeSpan Interval(double value, double scale)
        {
            if (value != value)
                throw new ArgumentException("TimeSpan does not accept floating point Not-a-Number values.");
            return FromDoubleTicks(value * scale);
        }

        private static TimeSpan FromDoubleTicks(double ticks)
        {
            if (ticks > long.MaxValue || ticks < long.MinValue || ticks != ticks)
                throw new OverflowException(TooLong);
            return ticks >= long.MaxValue ? MaxValue : new TimeSpan((long)ticks);
        }

        // The integer overloads, exact (.NET 9's).
        public static TimeSpan FromDays(int days) => FromUnits(days, TicksPerDay);
        public static TimeSpan FromHours(int hours) => FromUnits(hours, TicksPerHour);
        public static TimeSpan FromMinutes(long minutes) => FromUnits(minutes, TicksPerMinute);
        public static TimeSpan FromSeconds(long seconds) => FromUnits(seconds, TicksPerSecond);
        public static TimeSpan FromMilliseconds(long milliseconds) => FromUnits(milliseconds, TicksPerMillisecond);
        public static TimeSpan FromMicroseconds(long microseconds) => FromUnits(microseconds, TicksPerMicrosecond);

        private static TimeSpan FromUnits(long units, long ticksPerUnit)
        {
            if (units > long.MaxValue / ticksPerUnit || units < long.MinValue / ticksPerUnit)
                throw new ArgumentOutOfRangeException(null, TooLong);
            return new TimeSpan(units * ticksPerUnit);
        }

        public static TimeSpan FromDays(int days, int hours = 0, long minutes = 0, long seconds = 0, long milliseconds = 0, long microseconds = 0)
            => FromMicroseconds(Wide.Product(days, MicrosecondsPerDay) + Wide.Product(hours, MicrosecondsPerHour)
                + Wide.Product(minutes, MicrosecondsPerMinute) + Wide.Product(seconds, MicrosecondsPerSecond)
                + Wide.Product(milliseconds, MicrosecondsPerMillisecond) + Wide.Product(microseconds, 1));

        public static TimeSpan FromHours(int hours, long minutes = 0, long seconds = 0, long milliseconds = 0, long microseconds = 0)
            => FromMicroseconds(Wide.Product(hours, MicrosecondsPerHour) + Wide.Product(minutes, MicrosecondsPerMinute)
                + Wide.Product(seconds, MicrosecondsPerSecond) + Wide.Product(milliseconds, MicrosecondsPerMillisecond)
                + Wide.Product(microseconds, 1));

        public static TimeSpan FromMinutes(long minutes, long seconds = 0, long milliseconds = 0, long microseconds = 0)
            => FromMicroseconds(Wide.Product(minutes, MicrosecondsPerMinute) + Wide.Product(seconds, MicrosecondsPerSecond)
                + Wide.Product(milliseconds, MicrosecondsPerMillisecond) + Wide.Product(microseconds, 1));

        public static TimeSpan FromSeconds(long seconds, long milliseconds = 0, long microseconds = 0)
            => FromMicroseconds(Wide.Product(seconds, MicrosecondsPerSecond) + Wide.Product(milliseconds, MicrosecondsPerMillisecond)
                + Wide.Product(microseconds, 1));

        public static TimeSpan FromMilliseconds(long milliseconds, long microseconds)
            => FromMicroseconds(Wide.Product(milliseconds, MicrosecondsPerMillisecond) + Wide.Product(microseconds, 1));

        private static TimeSpan FromMicroseconds(Wide microseconds)
        {
            if (!microseconds.FitsLong || microseconds.Low > MaxMicroseconds || microseconds.Low < MinMicroseconds)
                throw new ArgumentOutOfRangeException(null, TooLong);
            return new TimeSpan(microseconds.Low * TicksPerMicrosecond);
        }

        // A signed 128-bit integer, for adding products of longs exactly.
        private readonly struct Wide
        {
            private readonly ulong _low;
            private readonly long _high;

            private Wide(long high, ulong low)
            {
                _high = high;
                _low = low;
            }

            public long Low => (long)_low;
            public bool FitsLong => _high == ((long)_low >> 63);

            public static Wide Product(long a, long b)
            {
                bool negative = (a < 0) != (b < 0);
                ulong x = a < 0 ? (ulong)-(a + 1) + 1 : (ulong)a;
                ulong y = b < 0 ? (ulong)-(b + 1) + 1 : (ulong)b;
                ulong x0 = (uint)x, x1 = x >> 32, y0 = (uint)y, y1 = y >> 32;
                ulong p00 = x0 * y0, p01 = x0 * y1, p10 = x1 * y0, p11 = x1 * y1;
                ulong middle = (p00 >> 32) + (uint)p01 + (uint)p10;
                ulong low = (middle << 32) | (uint)p00;
                ulong high = p11 + (p01 >> 32) + (p10 >> 32) + (middle >> 32);
                Wide result = new Wide((long)high, low);
                return negative ? -result : result;
            }

            public static Wide operator -(Wide value)
            {
                ulong low = ~value._low + 1;
                long high = ~value._high + (low == 0 ? 1 : 0);
                return new Wide(high, low);
            }

            public static Wide operator +(Wide a, Wide b)
            {
                ulong low = a._low + b._low;
                long high = a._high + b._high + (low < a._low ? 1 : 0);
                return new Wide(high, low);
            }
        }

        public TimeSpan Multiply(double factor) => this * factor;
        public TimeSpan Divide(double divisor) => this / divisor;
        public double Divide(TimeSpan ts) => this / ts;

        public static TimeSpan operator *(TimeSpan timeSpan, double factor)
        {
            if (factor != factor)
                throw new ArgumentException("TimeSpan does not accept floating point Not-a-Number values.", "factor");
            return FromDoubleTicks(Math.Round(timeSpan._ticks * factor));
        }

        public static TimeSpan operator *(double factor, TimeSpan timeSpan) => timeSpan * factor;

        public static TimeSpan operator /(TimeSpan timeSpan, double divisor)
        {
            if (divisor != divisor)
                throw new ArgumentException("TimeSpan does not accept floating point Not-a-Number values.", "divisor");
            return FromDoubleTicks(Math.Round(timeSpan._ticks / divisor));
        }

        public static double operator /(TimeSpan t1, TimeSpan t2) => t1._ticks / (double)t2._ticks;

        public TimeSpan Add(TimeSpan ts) => this + ts;
        public TimeSpan Subtract(TimeSpan ts) => this - ts;
        public TimeSpan Negate() => -this;

        /// <summary>The absolute value.</summary>
        public TimeSpan Duration()
        {
            if (_ticks == long.MinValue)
                throw new OverflowException("The duration cannot be returned for TimeSpan.MinValue because the absolute value of TimeSpan.MinValue exceeds the value of TimeSpan.MaxValue.");
            return new TimeSpan(_ticks < 0 ? -_ticks : _ticks);
        }

        public static TimeSpan operator +(TimeSpan t1, TimeSpan t2)
        {
            long result = t1._ticks + t2._ticks;
            // Overflow: both had the same sign and the result's differs.
            if ((t1._ticks >> 63) == (t2._ticks >> 63) && (t1._ticks >> 63) != (result >> 63))
                throw new OverflowException("TimeSpan overflowed because the duration is too long.");
            return new TimeSpan(result);
        }

        public static TimeSpan operator -(TimeSpan t1, TimeSpan t2)
        {
            long result = t1._ticks - t2._ticks;
            // Overflow: the signs differed and the result's differs from the first's.
            if ((t1._ticks >> 63) != (t2._ticks >> 63) && (t1._ticks >> 63) != (result >> 63))
                throw new OverflowException("TimeSpan overflowed because the duration is too long.");
            return new TimeSpan(result);
        }

        public static TimeSpan operator -(TimeSpan t)
        {
            if (t._ticks == long.MinValue)
                throw new OverflowException("Negating the minimum value of a twos complement number is invalid.");
            return new TimeSpan(-t._ticks);
        }

        public static TimeSpan operator +(TimeSpan t) => t;

        public static bool operator ==(TimeSpan t1, TimeSpan t2) => t1._ticks == t2._ticks;
        public static bool operator !=(TimeSpan t1, TimeSpan t2) => t1._ticks != t2._ticks;
        public static bool operator <(TimeSpan t1, TimeSpan t2) => t1._ticks < t2._ticks;
        public static bool operator >(TimeSpan t1, TimeSpan t2) => t1._ticks > t2._ticks;
        public static bool operator <=(TimeSpan t1, TimeSpan t2) => t1._ticks <= t2._ticks;
        public static bool operator >=(TimeSpan t1, TimeSpan t2) => t1._ticks >= t2._ticks;

        public static int Compare(TimeSpan t1, TimeSpan t2) => t1._ticks.CompareTo(t2._ticks);
        public int CompareTo(TimeSpan value) => _ticks.CompareTo(value._ticks);

        public int CompareTo(object value)
        {
            if (value == null)
                return 1;
            if (value is not TimeSpan other)
                throw new ArgumentException("Object must be of type TimeSpan.");
            return CompareTo(other);
        }

        public static bool Equals(TimeSpan t1, TimeSpan t2) => t1._ticks == t2._ticks;
        public bool Equals(TimeSpan obj) => _ticks == obj._ticks;
        public override bool Equals(object value) => value is TimeSpan other && _ticks == other._ticks;
        public override int GetHashCode() => _ticks.GetHashCode();

        /// <summary>[-][d.]hh:mm:ss[.fffffff], as .NET's "c" format.</summary>
        public override string ToString() => DateTimeFormat.FormatTimeSpan(this, null);
        public string ToString(string format) => DateTimeFormat.FormatTimeSpan(this, format);
        public string ToString(string format, IFormatProvider formatProvider) => DateTimeFormat.FormatTimeSpan(this, format);

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider formatProvider = null)
            => DateTimeFormat.TryFormatTimeSpan(this, destination, out charsWritten, format);
    }
}
