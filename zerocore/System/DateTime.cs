using System.Runtime;

namespace System
{
    // A date and time of day, in ticks of 100 ns since 0001-01-01 00:00, with
    // .NET's members, results and messages. As in .NET the top two bits hold
    // the Kind, so a DateTime is one ulong.
    //
    // Local time is the system's zone, through TimeZoneInfo.Local (the C
    // library's localtime_r). Where there is no zone (WebAssembly, DOS, UEFI,
    // the C64 and microcontrollers) local time is UTC, as in .NET on a
    // machine set to UTC; on DOS and UEFI the clock is the PC's, which
    // usually keeps local time, so UtcNow is really local time there.
    //
    // No Calendar overloads (the calendar is always the Gregorian one) and no
    // IConvertible or ISerializable.
    public readonly unsafe struct DateTime : IComparable, IComparable<DateTime>, IEquatable<DateTime>, ISpanFormattable
    {
        private const long TicksPerMillisecond = TimeSpan.TicksPerMillisecond;
        private const long TicksPerSecond = TimeSpan.TicksPerSecond;
        private const long TicksPerMinute = TimeSpan.TicksPerMinute;
        private const long TicksPerHour = TimeSpan.TicksPerHour;
        private const long TicksPerDay = TimeSpan.TicksPerDay;
        private const int MillisPerDay = 86_400_000;

        private const int DaysPerYear = 365;
        private const int DaysPer4Years = DaysPerYear * 4 + 1;
        private const int DaysPer100Years = DaysPer4Years * 25 - 1;
        private const int DaysPer400Years = DaysPer100Years * 4 + 1;
        private const int DaysTo1601 = DaysPer400Years * 4;
        private const int DaysTo1899 = DaysPer400Years * 4 + DaysPer100Years * 3 - 367;
        internal const int DaysTo1970 = DaysPer400Years * 4 + DaysPer100Years * 3 + DaysPer4Years * 17 + DaysPerYear;
        internal const int DaysTo10000 = DaysPer400Years * 25 - 366;

        internal const long MinTicks = 0;
        internal const long MaxTicks = DaysTo10000 * TicksPerDay - 1;
        private const long MaxMillis = (long)DaysTo10000 * MillisPerDay;
        internal const long UnixEpochTicks = DaysTo1970 * TicksPerDay;
        private const long FileTimeOffset = DaysTo1601 * TicksPerDay;
        private const long DoubleDateOffset = DaysTo1899 * TicksPerDay;
        private const long OADateMinAsTicks = (DaysPer100Years - DaysPerYear) * TicksPerDay;
        private const double OADateMinAsDouble = -657435.0;
        private const double OADateMaxAsDouble = 2958466.0;

        private const ulong TicksMask = 0x3FFFFFFFFFFFFFFF;
        private const ulong FlagsMask = 0xC000000000000000;
        private const ulong KindUtc = 0x4000000000000000;
        private const ulong KindLocal = 0x8000000000000000;
        // Local, and the daylight saving one of two local times that look
        // the same (when the clock goes back), as in .NET.
        private const ulong KindLocalAmbiguousDst = 0xC000000000000000;
        private const int KindShift = 62;
        private const long TicksCeiling = 0x4000000000000000;

        public static readonly DateTime MinValue = default;
        public static readonly DateTime MaxValue = new DateTime((ulong)MaxTicks);
        public static readonly DateTime UnixEpoch = new DateTime((ulong)UnixEpochTicks | KindUtc);

        private readonly ulong _dateData;

        private DateTime(ulong dateData) => _dateData = dateData;

        // A local time, remembering whether it is the daylight saving one of
        // two that look the same.
        internal DateTime(long ticks, DateTimeKind kind, bool isAmbiguousDst)
            => _dateData = (ulong)ticks | (isAmbiguousDst && kind == DateTimeKind.Local ? KindLocalAmbiguousDst : (ulong)kind << KindShift);

        public DateTime(long ticks)
        {
            if ((ulong)ticks > MaxTicks)
                throw BadTicks();
            _dateData = (ulong)ticks;
        }

        public DateTime(long ticks, DateTimeKind kind)
        {
            if ((ulong)ticks > MaxTicks)
                throw BadTicks();
            _dateData = (ulong)ticks | KindBits(kind);
        }

        public DateTime(DateOnly date, TimeOnly time) : this(date.DayNumber * TicksPerDay + time.Ticks) { }

        public DateTime(DateOnly date, TimeOnly time, DateTimeKind kind) : this(date.DayNumber * TicksPerDay + time.Ticks, kind) { }

        public DateTime(int year, int month, int day) => _dateData = (ulong)DateToTicks(year, month, day);

        public DateTime(int year, int month, int day, int hour, int minute, int second)
            => _dateData = (ulong)(DateToTicks(year, month, day) + TimeToTicks(hour, minute, second));

        public DateTime(int year, int month, int day, int hour, int minute, int second, DateTimeKind kind)
        {
            ulong flags = KindBits(kind);
            _dateData = (ulong)(DateToTicks(year, month, day) + TimeToTicks(hour, minute, second)) | flags;
        }

        public DateTime(int year, int month, int day, int hour, int minute, int second, int millisecond)
            => _dateData = (ulong)ComponentTicks(year, month, day, hour, minute, second, millisecond, 0);

        public DateTime(int year, int month, int day, int hour, int minute, int second, int millisecond, DateTimeKind kind)
        {
            ulong flags = KindBits(kind);
            _dateData = (ulong)ComponentTicks(year, month, day, hour, minute, second, millisecond, 0) | flags;
        }

        public DateTime(int year, int month, int day, int hour, int minute, int second, int millisecond, int microsecond)
            => _dateData = (ulong)ComponentTicks(year, month, day, hour, minute, second, millisecond, microsecond);

        public DateTime(int year, int month, int day, int hour, int minute, int second, int millisecond, int microsecond, DateTimeKind kind)
        {
            ulong flags = KindBits(kind);
            _dateData = (ulong)ComponentTicks(year, month, day, hour, minute, second, millisecond, microsecond) | flags;
        }

        private static long ComponentTicks(int year, int month, int day, int hour, int minute, int second, int millisecond, int microsecond)
        {
            long ticks = DateToTicks(year, month, day) + TimeToTicks(hour, minute, second);
            if ((uint)millisecond >= 1000)
                throw new ArgumentOutOfRangeException("millisecond", "Valid values are between 0 and 999, inclusive.");
            if ((uint)microsecond >= 1000)
                throw new ArgumentOutOfRangeException("microsecond", "Valid values are between 0 and 999, inclusive.");
            return ticks + millisecond * TicksPerMillisecond + microsecond * TimeSpan.TicksPerMicrosecond;
        }

        private static ulong KindBits(DateTimeKind kind)
        {
            if ((uint)kind > (uint)DateTimeKind.Local)
                throw new ArgumentException("Invalid DateTimeKind value.", "kind");
            return (ulong)kind << KindShift;
        }

        private static ArgumentOutOfRangeException BadTicks()
            => new ArgumentOutOfRangeException("ticks", "Ticks must be between DateTime.MinValue.Ticks and DateTime.MaxValue.Ticks.");

        private static ArgumentOutOfRangeException BadArithmetic(string paramName)
            => new ArgumentOutOfRangeException(paramName, "The added or subtracted value results in an un-representable DateTime.");

        internal static long DateToTicks(int year, int month, int day)
        {
            if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1)
                throw BadYearMonthDay();
            int daysInMonth = DaysInMonthUnchecked(year, month);
            if (day > daysInMonth)
                throw BadYearMonthDay();
            return DaysToDate(year, month, day) * TicksPerDay;
        }

        private static ArgumentOutOfRangeException BadYearMonthDay()
            => new ArgumentOutOfRangeException(null, "Year, Month, and Day parameters describe an un-representable DateTime.");

        internal static long TimeToTicks(int hour, int minute, int second)
        {
            if ((uint)hour >= 24 || (uint)minute >= 60 || (uint)second >= 60)
                throw new ArgumentOutOfRangeException(null, "Hour, Minute, and Second parameters describe an un-representable DateTime.");
            return hour * TicksPerHour + minute * TicksPerMinute + second * TicksPerSecond;
        }

        // Days from 0001-01-01 to a valid date.
        internal static long DaysToDate(int year, int month, int day)
        {
            int y = year - 1;
            return y * 365L + y / 4 - y / 100 + y / 400 + DaysBeforeMonth(year, month) + day - 1;
        }

        private static int DaysBeforeMonth(int year, int month)
        {
            if (month == 1)
                return 0;
            if (month == 2)
                return 31;
            return (153 * (month - 3) + 2) / 5 + 59 + (IsLeapYearUnchecked(year) ? 1 : 0);
        }

        private static bool IsLeapYearUnchecked(int year) => (year & 3) == 0 && (year % 100 != 0 || year % 400 == 0);

        private static int DaysInMonthUnchecked(int year, int month)
            => month == 2 ? (IsLeapYearUnchecked(year) ? 29 : 28) : 30 + ((month + (month >> 3)) & 1);

        // The year, month, day and day of the year (0-based) of a number of
        // days since 0001-01-01.
        internal static void GetDate(long days, out int year, out int month, out int day, out int dayOfYear)
        {
            int n = (int)days;
            int y400 = n / DaysPer400Years;
            n -= y400 * DaysPer400Years;
            int y100 = n / DaysPer100Years;
            if (y100 == 4)
                y100 = 3;
            n -= y100 * DaysPer100Years;
            int y4 = n / DaysPer4Years;
            n -= y4 * DaysPer4Years;
            int y1 = n / DaysPerYear;
            if (y1 == 4)
                y1 = 3;
            year = y400 * 400 + y100 * 100 + y4 * 4 + y1 + 1;
            n -= y1 * DaysPerYear;
            dayOfYear = n;
            int leap = y1 == 3 && (y4 != 24 || y100 == 3) ? 1 : 0;
            if (n < 31)
            {
                month = 1;
                day = n + 1;
            }
            else if (n < 59 + leap)
            {
                month = 2;
                day = n - 30;
            }
            else
            {
                int d = n - 59 - leap;
                int shifted = (5 * d + 2) / 153; // 0 is March
                month = shifted + 3;
                day = d - (153 * shifted + 2) / 5 + 1;
            }
        }

        private long InternalTicks => (long)(_dateData & TicksMask);
        private ulong InternalKind => _dateData & FlagsMask;

        public long Ticks => InternalTicks;

        public DateTimeKind Kind => InternalKind switch
        {
            0 => DateTimeKind.Unspecified,
            KindUtc => DateTimeKind.Utc,
            _ => DateTimeKind.Local,
        };

        public DateTime Date
        {
            get
            {
                ulong ticks = (ulong)InternalTicks;
                return new DateTime((ticks - ticks % TicksPerDay) | InternalKind);
            }
        }

        public int Year { get { GetDate(InternalTicks / TicksPerDay, out int y, out _, out _, out _); return y; } }
        public int Month { get { GetDate(InternalTicks / TicksPerDay, out _, out int m, out _, out _); return m; } }
        public int Day { get { GetDate(InternalTicks / TicksPerDay, out _, out _, out int d, out _); return d; } }
        public int DayOfYear { get { GetDate(InternalTicks / TicksPerDay, out _, out _, out _, out int n); return n + 1; } }
        public DayOfWeek DayOfWeek => (DayOfWeek)((InternalTicks / TicksPerDay + 1) % 7);
        public int Hour => (int)(InternalTicks / TicksPerHour % 24);
        public int Minute => (int)(InternalTicks / TicksPerMinute % 60);
        public int Second => (int)(InternalTicks / TicksPerSecond % 60);
        public int Millisecond => (int)(InternalTicks / TicksPerMillisecond % 1000);
        public int Microsecond => (int)(InternalTicks / TimeSpan.TicksPerMicrosecond % 1000);
        public int Nanosecond => (int)(InternalTicks % TimeSpan.TicksPerMicrosecond * TimeSpan.NanosecondsPerTick);
        public TimeSpan TimeOfDay => new TimeSpan(InternalTicks % TicksPerDay);

        public void Deconstruct(out int year, out int month, out int day)
            => GetDate(InternalTicks / TicksPerDay, out year, out month, out day, out _);

        public void Deconstruct(out DateOnly date, out TimeOnly time)
        {
            date = DateOnly.FromDayNumber((int)(InternalTicks / TicksPerDay));
            time = new TimeOnly(InternalTicks % TicksPerDay);
        }

        // ---- the clock ----

        // The C library's wall clock (clock 0), as lib/Clock.cs reads it.
        private static long ClockTicks()
        {
            long* timespec = stackalloc long[2];
            if (Platform.clock_gettime(0, timespec) != 0)
                return UnixEpochTicks;
            long ticks = UnixEpochTicks + timespec[0] * TicksPerSecond + timespec[1] / TimeSpan.NanosecondsPerTick;
            return ticks < MinTicks ? MinTicks : ticks > MaxTicks ? MaxTicks : ticks;
        }

        public static DateTime UtcNow => new DateTime((ulong)ClockTicks() | KindUtc);

        public static DateTime Now => FromUtcToLocal(ClockTicks());

        // A UTC time as local time, clamped to the range as in .NET.
        private static DateTime FromUtcToLocal(long utcTicks)
        {
            long ticks = utcTicks + TimeZoneInfo.LocalOffsetFromUtc(utcTicks, out bool ambiguousDst);
            if (ticks > MaxTicks)
                return new DateTime((ulong)MaxTicks | KindLocal);
            if (ticks < MinTicks)
                return new DateTime((ulong)MinTicks | KindLocal);
            return new DateTime((ulong)ticks | (ambiguousDst ? KindLocalAmbiguousDst : KindLocal));
        }

        internal bool IsAmbiguousDaylightSavingTime() => InternalKind == KindLocalAmbiguousDst;

        public static DateTime Today => Now.Date;

        // ---- arithmetic ----

        public DateTime Add(TimeSpan value) => AddTicks(value.Ticks);

        private DateTime AddUnits(double value, long maxUnitCount, long ticksPerUnit)
        {
            if (Math.Abs(value) > maxUnitCount)
                throw new ArgumentOutOfRangeException("value", "Value to add was out of range.");
            double integralPart = Math.Truncate(value);
            double fractionalPart = value - integralPart;
            long ticks = (long)integralPart * ticksPerUnit;
            ticks += (long)(fractionalPart * ticksPerUnit);
            return AddTicks(ticks);
        }

        public DateTime AddDays(double value) => AddUnits(value, DaysTo10000, TicksPerDay);
        public DateTime AddHours(double value) => AddUnits(value, DaysTo10000 * 24L, TicksPerHour);
        public DateTime AddMinutes(double value) => AddUnits(value, DaysTo10000 * 24L * 60, TicksPerMinute);
        public DateTime AddSeconds(double value) => AddUnits(value, DaysTo10000 * 24L * 3600, TicksPerSecond);
        public DateTime AddMilliseconds(double value) => AddUnits(value, MaxMillis, TicksPerMillisecond);
        public DateTime AddMicroseconds(double value) => AddUnits(value, MaxMillis * 1000, TimeSpan.TicksPerMicrosecond);

        public DateTime AddTicks(long value)
        {
            ulong ticks = (ulong)(InternalTicks + value);
            if (ticks > MaxTicks)
                throw BadArithmetic("value");
            return new DateTime(ticks | InternalKind);
        }

        public DateTime AddMonths(int months)
        {
            if (months < -120_000 || months > 120_000)
                throw new ArgumentOutOfRangeException("months", "Months value must be between +/-120000.");
            GetDate(InternalTicks / TicksPerDay, out int year, out int month, out int day, out _);
            int y = year, d = day;
            int m = month + months;
            int q = m > 0 ? (int)((uint)(m - 1) / 12) : m / 12 - 1;
            y += q;
            m -= q * 12;
            if (y < 1 || y > 9999)
                throw BadArithmetic("months");
            int days = DaysInMonthUnchecked(y, m);
            if (d > days)
                d = days;
            ulong n = (ulong)(DaysToDate(y, m, d) * TicksPerDay) + (ulong)(InternalTicks % TicksPerDay);
            return new DateTime(n | InternalKind);
        }

        public DateTime AddYears(int value)
        {
            if (value < -10_000 || value > 10_000)
                throw new ArgumentOutOfRangeException("value", "Years value must be between +/-10000.");
            GetDate(InternalTicks / TicksPerDay, out int year, out int month, out int day, out _);
            int y = year + value;
            if (y < 1 || y > 9999)
                throw BadArithmetic("value");
            if (month == 2 && day == 29 && !IsLeapYearUnchecked(y))
                day = 28;
            ulong n = (ulong)(DaysToDate(y, month, day) * TicksPerDay) + (ulong)(InternalTicks % TicksPerDay);
            return new DateTime(n | InternalKind);
        }

        public TimeSpan Subtract(DateTime value) => new TimeSpan(InternalTicks - value.InternalTicks);

        public DateTime Subtract(TimeSpan value)
        {
            ulong ticks = (ulong)(InternalTicks - value.Ticks);
            if (ticks > MaxTicks)
                throw BadArithmetic("value");
            return new DateTime(ticks | InternalKind);
        }

        public static DateTime operator +(DateTime d, TimeSpan t)
        {
            ulong ticks = (ulong)(d.InternalTicks + t.Ticks);
            if (ticks > MaxTicks)
                throw BadArithmetic("t");
            return new DateTime(ticks | d.InternalKind);
        }

        public static DateTime operator -(DateTime d, TimeSpan t)
        {
            ulong ticks = (ulong)(d.InternalTicks - t.Ticks);
            if (ticks > MaxTicks)
                throw BadArithmetic("t");
            return new DateTime(ticks | d.InternalKind);
        }

        public static TimeSpan operator -(DateTime d1, DateTime d2) => new TimeSpan(d1.InternalTicks - d2.InternalTicks);

        // ---- comparing ----

        public static bool operator ==(DateTime d1, DateTime d2) => d1.InternalTicks == d2.InternalTicks;
        public static bool operator !=(DateTime d1, DateTime d2) => d1.InternalTicks != d2.InternalTicks;
        public static bool operator <(DateTime t1, DateTime t2) => t1.InternalTicks < t2.InternalTicks;
        public static bool operator <=(DateTime t1, DateTime t2) => t1.InternalTicks <= t2.InternalTicks;
        public static bool operator >(DateTime t1, DateTime t2) => t1.InternalTicks > t2.InternalTicks;
        public static bool operator >=(DateTime t1, DateTime t2) => t1.InternalTicks >= t2.InternalTicks;

        public static int Compare(DateTime t1, DateTime t2) => t1.InternalTicks.CompareTo(t2.InternalTicks);
        public int CompareTo(DateTime value) => InternalTicks.CompareTo(value.InternalTicks);

        public int CompareTo(object value)
        {
            if (value == null)
                return 1;
            if (value is not DateTime other)
                throw new ArgumentException("Object must be of type DateTime.");
            return CompareTo(other);
        }

        public static bool Equals(DateTime t1, DateTime t2) => t1.InternalTicks == t2.InternalTicks;
        public bool Equals(DateTime value) => InternalTicks == value.InternalTicks;
        public override bool Equals(object value) => value is DateTime other && InternalTicks == other.InternalTicks;

        public override int GetHashCode()
        {
            long ticks = InternalTicks;
            return unchecked((int)ticks) ^ (int)(ticks >> 32);
        }

        // ---- the calendar ----

        public static int DaysInMonth(int year, int month)
        {
            if (month < 1 || month > 12)
                throw new ArgumentOutOfRangeException("month", month, "Month must be between one and twelve.");
            if (year < 1 || year > 9999)
                throw new ArgumentOutOfRangeException("year", "Year must be between 1 and 9999.");
            return DaysInMonthUnchecked(year, month);
        }

        public static bool IsLeapYear(int year)
        {
            if (year < 1 || year > 9999)
                throw new ArgumentOutOfRangeException("year", "Year must be between 1 and 9999.");
            return IsLeapYearUnchecked(year);
        }

        public bool IsDaylightSavingTime()
            => Kind != DateTimeKind.Utc && TimeZoneInfo.Local.IsDaylightSavingTime(this);

        // ---- kinds and conversions ----

        public static DateTime SpecifyKind(DateTime value, DateTimeKind kind)
        {
            ulong flags = KindBits(kind);
            return new DateTime((ulong)value.InternalTicks | flags);
        }

        // Unspecified counts as local time going to UTC, and as UTC going
        // to local time, as in .NET.
        public DateTime ToLocalTime() => Kind == DateTimeKind.Local ? this : FromUtcToLocal(InternalTicks);

        public DateTime ToUniversalTime()
        {
            if (Kind == DateTimeKind.Utc)
                return this;
            long ticks = InternalTicks - TimeZoneInfo.LocalOffsetFromLocal(InternalTicks, IsAmbiguousDaylightSavingTime());
            if (ticks > MaxTicks)
                return new DateTime((ulong)MaxTicks | KindUtc);
            if (ticks < MinTicks)
                return new DateTime((ulong)MinTicks | KindUtc);
            return new DateTime((ulong)ticks | KindUtc);
        }

        public long ToBinary()
        {
            // A local time is stored as UTC with the local flag, and read back
            // in whatever zone reads it.
            if (Kind == DateTimeKind.Local)
            {
                long stored = InternalTicks - TimeZoneInfo.LocalOffsetFromLocal(InternalTicks, IsAmbiguousDaylightSavingTime());
                if (stored < 0)
                    stored += TicksCeiling;
                return stored | unchecked((long)KindLocal);
            }
            return (long)_dateData;
        }

        public static DateTime FromBinary(long dateData)
        {
            if ((dateData & unchecked((long)KindLocal)) != 0)
            {
                long ticks = dateData & (long)TicksMask;
                if (ticks > TicksCeiling - TicksPerDay)
                    ticks -= TicksCeiling;
                bool ambiguousDst = false;
                long offset;
                if (ticks < MinTicks)
                    offset = TimeZoneInfo.LocalOffsetFromLocal(MinTicks, false);
                else if (ticks > MaxTicks)
                    offset = TimeZoneInfo.LocalOffsetFromLocal(MaxTicks, false);
                else
                    offset = TimeZoneInfo.LocalOffsetFromUtc(ticks, out ambiguousDst);
                ticks += offset;
                if (ticks < 0)
                    ticks += TicksPerDay;
                if ((ulong)ticks > MaxTicks)
                    throw new ArgumentException("The binary data must result in a DateTime with ticks between DateTime.MinValue.Ticks and DateTime.MaxValue.Ticks.", "dateData");
                return new DateTime((ulong)ticks | (ambiguousDst ? KindLocalAmbiguousDst : KindLocal));
            }
            if ((ulong)(dateData & (long)TicksMask) > MaxTicks)
                throw new ArgumentException("The binary data must result in a DateTime with ticks between DateTime.MinValue.Ticks and DateTime.MaxValue.Ticks.", "dateData");
            return new DateTime((ulong)dateData);
        }

        public static DateTime FromFileTime(long fileTime) => FromFileTimeUtc(fileTime).ToLocalTime();

        public static DateTime FromFileTimeUtc(long fileTime)
        {
            if ((ulong)fileTime > MaxTicks - FileTimeOffset)
                throw new ArgumentOutOfRangeException("fileTime", "Not a valid Win32 FileTime.");
            return new DateTime((ulong)(fileTime + FileTimeOffset) | KindUtc);
        }

        public long ToFileTime() => ToUniversalTime().ToFileTimeUtc();

        public long ToFileTimeUtc()
        {
            long ticks = (Kind == DateTimeKind.Local ? ToUniversalTime().InternalTicks : InternalTicks) - FileTimeOffset;
            if (ticks < 0)
                throw new ArgumentOutOfRangeException(null, "Not a valid Win32 FileTime.");
            return ticks;
        }

        public static DateTime FromOADate(double d) => new DateTime((ulong)DoubleDateToTicks(d));

        private static long DoubleDateToTicks(double value)
        {
            if (!(value < OADateMaxAsDouble) || !(value > OADateMinAsDouble))
                throw new ArgumentException("Not a legal OleAut date.");
            long millis = (long)(value * MillisPerDay + (value >= 0 ? 0.5 : -0.5));
            // A negative date's time of day counts forward from midnight.
            if (millis < 0)
                millis -= millis % MillisPerDay * 2;
            millis += DoubleDateOffset / TicksPerMillisecond;
            if (millis < 0 || millis >= MaxMillis)
                throw new ArgumentException("Not a legal OleAut date.");
            return millis * TicksPerMillisecond;
        }

        public double ToOADate()
        {
            long value = InternalTicks;
            if (value == 0)
                return 0.0;
            if (value < TicksPerDay)
                value += DoubleDateOffset;
            if (value < OADateMinAsTicks)
                throw new OverflowException("Not a legal OleAut date.");
            long millis = (value - DoubleDateOffset) / TicksPerMillisecond;
            if (millis < 0)
            {
                long frac = millis % MillisPerDay;
                if (frac != 0)
                    millis -= (MillisPerDay + frac) * 2;
            }
            return (double)millis / MillisPerDay;
        }

        // ---- text ----

        public override string ToString() => DateTimeFormat.Format(this, null, DateTimeFormat.NullOffset);
        public string ToString(string format) => DateTimeFormat.Format(this, format, DateTimeFormat.NullOffset);
        public string ToString(IFormatProvider provider) => DateTimeFormat.Format(this, null, DateTimeFormat.NullOffset);
        public string ToString(string format, IFormatProvider provider) => DateTimeFormat.Format(this, format, DateTimeFormat.NullOffset);

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider provider = null)
            => DateTimeFormat.TryFormat(this, destination, out charsWritten, format, DateTimeFormat.NullOffset);

        public string ToLongDateString() => DateTimeFormat.Format(this, "D", DateTimeFormat.NullOffset);
        public string ToLongTimeString() => DateTimeFormat.Format(this, "T", DateTimeFormat.NullOffset);
        public string ToShortDateString() => DateTimeFormat.Format(this, "d", DateTimeFormat.NullOffset);
        public string ToShortTimeString() => DateTimeFormat.Format(this, "t", DateTimeFormat.NullOffset);

        // The value in every standard format, as .NET lists them for the
        // invariant culture.
        public string[] GetDateTimeFormats() => DateTimeFormat.AllFormats(this, '\0');
        public string[] GetDateTimeFormats(IFormatProvider provider) => DateTimeFormat.AllFormats(this, '\0');
        public string[] GetDateTimeFormats(char format) => DateTimeFormat.AllFormats(this, format);
        public string[] GetDateTimeFormats(char format, IFormatProvider provider) => DateTimeFormat.AllFormats(this, format);
    }
}
