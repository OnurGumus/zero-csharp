using System.Runtime;
using System.Runtime.InteropServices;

namespace System
{
    // Time zones: UTC, the system's local zone, and fixed offsets made with
    // CreateCustomTimeZone. There is no time zone database here, so
    // FindSystemTimeZoneById knows only "UTC" and the local zone's Id.
    //
    // The local zone comes from the C library's localtime_r, which knows the
    // system's zone and its daylight saving rules, past and future: on
    // Linux, macOS, iOS and Android from the tz database, on Windows from
    // the system's settings (through win/runtime.c). Its Id is TZ, or where
    // /etc/localtime points. The WebAssembly, DOS, C64 and microcontroller
    // builds (-d WASM) and UEFI have no zone: local time is UTC there.
    //
    // Offsets are whole minutes, as in .NET: an old local mean time such as
    // New York's -4:56:02 in 1850 is rounded as .NET rounds it (toward the
    // zone's standard offset). Ambiguous and skipped local times count as
    // standard time, as in .NET.
    public sealed unsafe class TimeZoneInfo : IEquatable<TimeZoneInfo>
    {
        private const long TicksPerSecond = TimeSpan.TicksPerSecond;
        private const long TicksPerMinute = TimeSpan.TicksPerMinute;
        private const long TicksPerDay = TimeSpan.TicksPerDay;
        private const string UtcId = "UTC";
        private const string UtcName = "Coordinated Universal Time";

        private enum ZoneKind { Fixed, Local }

        private static TimeZoneInfo s_utc, s_local;

        private readonly ZoneKind _kind;
        private readonly string _id;
        private readonly long _baseOffset;     // ticks
        private string _displayName, _standardName, _daylightName;
        private int _supportsDst;              // 0: not computed, 1: no, 2: yes

        private TimeZoneInfo(ZoneKind kind, string id, long baseOffset, string displayName, string standardName, string daylightName)
        {
            _kind = kind;
            _id = id;
            _baseOffset = baseOffset;
            _displayName = displayName;
            _standardName = standardName;
            _daylightName = daylightName;
        }

        public static TimeZoneInfo Utc => s_utc ??= new TimeZoneInfo(ZoneKind.Fixed, UtcId, 0,
            "(UTC) " + UtcName, UtcName, UtcName) { _supportsDst = 1 };

        public static TimeZoneInfo Local => s_local ??= MakeLocal();

        // Always an object of its own, even when it is UTC: converting
        // between zones gives local and UTC Kinds by which object it is, as
        // in .NET.
        private static TimeZoneInfo MakeLocal()
        {
#if WASM
            return new TimeZoneInfo(ZoneKind.Fixed, UtcId, 0, "(UTC) " + UtcName, UtcName, UtcName) { _supportsDst = 1 };
#else
            string id = LocalId();
            if (!HasZone())
                return new TimeZoneInfo(ZoneKind.Fixed, id, 0, "(UTC) " + UtcName, UtcName, UtcName) { _supportsDst = 1 };
            long baseOffset = LocalBaseOffset();
            if (IsUtcId(id) && baseOffset == 0)
                return new TimeZoneInfo(ZoneKind.Local, id, 0, "(UTC) " + UtcName, UtcName, UtcName);
            return new TimeZoneInfo(ZoneKind.Local, id, baseOffset, null, null, null);
#endif
        }

        private static DateTimeKind KindOf(TimeZoneInfo zone)
            => ReferenceEquals(zone, s_utc) ? DateTimeKind.Utc : ReferenceEquals(zone, s_local) ? DateTimeKind.Local : DateTimeKind.Unspecified;

        public static TimeZoneInfo CreateCustomTimeZone(string id, TimeSpan baseUtcOffset, string displayName, string standardDisplayName)
        {
            if (id == null)
                throw new ArgumentNullException("id");
            if (id.Length == 0)
                throw new ArgumentException("The specified ID parameter '" + id + "' is not supported.", "id");
            if (baseUtcOffset.Ticks % TicksPerMinute != 0)
                throw new ArgumentException("The TimeSpan parameter cannot be specified more precisely than whole minutes.", "baseUtcOffset");
            if (baseUtcOffset.Ticks > 14 * TimeSpan.TicksPerHour || baseUtcOffset.Ticks < -14 * TimeSpan.TicksPerHour)
                throw new ArgumentOutOfRangeException("baseUtcOffset", "The TimeSpan parameter must be within plus or minus 14.0 hours.");
            return new TimeZoneInfo(ZoneKind.Fixed, id, baseUtcOffset.Ticks, displayName ?? "", standardDisplayName ?? "", standardDisplayName ?? "") { _supportsDst = 1 };
        }

        public static TimeZoneInfo FindSystemTimeZoneById(string id)
        {
            if (TryFindSystemTimeZoneById(id, out TimeZoneInfo zone))
                return zone;
            if (id == null)
                throw new ArgumentNullException("id");
            throw new TimeZoneNotFoundException("The time zone ID '" + id + "' was not found on the local computer.");
        }

        public static bool TryFindSystemTimeZoneById(string id, out TimeZoneInfo timeZoneInfo)
        {
            timeZoneInfo = null;
            if (id == null || id.Length == 0)
                return false;
            if (EqualsIgnoreCase(id, Local._id))
                timeZoneInfo = Local;
            else if (EqualsIgnoreCase(id, UtcId))
                timeZoneInfo = Utc;
            return timeZoneInfo != null;
        }

        public static void ClearCachedData() => s_local = null;

        public string Id => _id;
        public bool HasIanaId => true;
        public TimeSpan BaseUtcOffset => new TimeSpan(_baseOffset);

        public string DisplayName => _displayName ??= "(UTC" + OffsetText(_baseOffset) + ") " + _id;
        public string StandardName => _standardName ??= LocalStandardName();
        public string DaylightName { get { if (_daylightName == null) FindDaylight(); return _daylightName; } }

        // As in .NET: whether the zone's offset ever changes, not only for
        // daylight saving time.
        public bool SupportsDaylightSavingTime
        {
            get
            {
                if (_supportsDst == 0)
                    FindDaylight();
                return _supportsDst == 2;
            }
        }

        public override string ToString() => DisplayName;
        public bool Equals(TimeZoneInfo other) => other != null && EqualsIgnoreCase(_id, other._id) && HasSameRules(other);
        public override bool Equals(object obj) => obj is TimeZoneInfo other && Equals(other);
        public override int GetHashCode() => _id.Length ^ (int)_baseOffset;
        public bool HasSameRules(TimeZoneInfo other)
        {
            if (other == null)
                throw new ArgumentNullException("other");
            return _kind == other._kind && _baseOffset == other._baseOffset;
        }

        // ---- offsets ----

        public TimeSpan GetUtcOffset(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc && _kind == ZoneKind.Fixed)
                return new TimeSpan(_baseOffset);
            if (dateTime.Kind == DateTimeKind.Utc)
                return new TimeSpan(OffsetFromUtc(dateTime.Ticks, out _));
            if (dateTime.Kind == DateTimeKind.Local && this != Local)
                return new TimeSpan(OffsetFromUtc(Local.ToUtcTicks(dateTime.Ticks, dateTime.IsAmbiguousDaylightSavingTime(), out _), out _));
            return new TimeSpan(OffsetFromLocal(dateTime.Ticks, dateTime.IsAmbiguousDaylightSavingTime(), out _));
        }

        public TimeSpan GetUtcOffset(DateTimeOffset dateTimeOffset) => new TimeSpan(OffsetFromUtc(dateTimeOffset.UtcTicks, out _));

        public bool IsDaylightSavingTime(DateTime dateTime)
        {
            if (_kind == ZoneKind.Fixed)
                return false;
            if (dateTime.Kind == DateTimeKind.Utc)
            {
                OffsetFromUtc(dateTime.Ticks, out bool isDst);
                return isDst;
            }
            LocalCandidates(dateTime.Ticks, dateTime.IsAmbiguousDaylightSavingTime(), out _, out ZoneState state);
            return state == ZoneState.Daylight;
        }

        public bool IsDaylightSavingTime(DateTimeOffset dateTimeOffset)
        {
            OffsetFromUtc(dateTimeOffset.UtcTicks, out bool isDst);
            return isDst;
        }

        public bool IsAmbiguousTime(DateTime dateTime)
        {
            if (_kind == ZoneKind.Fixed)
                return false;
            LocalCandidates(InThisZone(dateTime, false), false, out _, out ZoneState state, out bool ambiguous);
            return ambiguous;
        }

        // A DateTime as this zone's local time: a UTC one converted, a local
        // one (in the local zone) as it is.
        private long InThisZone(DateTime dateTime, bool throwIfInvalid)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
                return dateTime.Ticks + OffsetFromUtc(dateTime.Ticks, out _);
            if (dateTime.Kind == DateTimeKind.Local && throwIfInvalid)
                return ConvertTime(dateTime, Local, this).Ticks;
            return dateTime.Ticks;
        }

        public bool IsAmbiguousTime(DateTimeOffset dateTimeOffset)
            => IsAmbiguousTime(new DateTime(dateTimeOffset.UtcTicks + OffsetFromUtc(dateTimeOffset.UtcTicks, out _)));

        public bool IsInvalidTime(DateTime dateTime)
        {
            if (_kind == ZoneKind.Fixed || dateTime.Kind == DateTimeKind.Utc)
                return false;
            LocalCandidates(dateTime.Ticks, false, out _, out ZoneState state);
            return state == ZoneState.Invalid;
        }

        public TimeSpan[] GetAmbiguousTimeOffsets(DateTime dateTime)
        {
            if (_kind == ZoneKind.Local)
            {
                LocalCandidates(InThisZone(dateTime, true), false, out _, out _, out bool ambiguous, out long first, out long second);
                if (ambiguous)
                    return first < second ? new[] { new TimeSpan(first), new TimeSpan(second) } : new[] { new TimeSpan(second), new TimeSpan(first) };
            }
            throw new ArgumentException("The supplied DateTime is not in an ambiguous time range.", "dateTime");
        }

        public TimeSpan[] GetAmbiguousTimeOffsets(DateTimeOffset dateTimeOffset)
            => GetAmbiguousTimeOffsets(new DateTime(dateTimeOffset.UtcTicks + OffsetFromUtc(dateTimeOffset.UtcTicks, out _)));

        // ---- conversions ----

        public static DateTime ConvertTime(DateTime dateTime, TimeZoneInfo destinationTimeZone)
        {
            if (destinationTimeZone == null)
                throw new ArgumentNullException("destinationTimeZone");
            return ConvertTime(dateTime, dateTime.Kind == DateTimeKind.Utc ? Utc : Local, destinationTimeZone);
        }

        public static DateTimeOffset ConvertTime(DateTimeOffset dateTimeOffset, TimeZoneInfo destinationTimeZone)
        {
            if (destinationTimeZone == null)
                throw new ArgumentNullException("destinationTimeZone");
            long utc = dateTimeOffset.UtcTicks;
            long offset = destinationTimeZone.OffsetFromUtc(utc, out _);
            long ticks = utc + offset;
            if (ticks > DateTime.MaxTicks)
                return DateTimeOffset.MaxValue;
            if (ticks < DateTime.MinTicks)
                return DateTimeOffset.MinValue;
            return new DateTimeOffset(ticks, new TimeSpan(offset));
        }

        public static DateTime ConvertTime(DateTime dateTime, TimeZoneInfo sourceTimeZone, TimeZoneInfo destinationTimeZone)
        {
            if (sourceTimeZone == null)
                throw new ArgumentNullException("sourceTimeZone");
            if (destinationTimeZone == null)
                throw new ArgumentNullException("destinationTimeZone");
            _ = Utc;
            _ = Local;
            DateTimeKind sourceKind = KindOf(sourceTimeZone);
            if (dateTime.Kind != DateTimeKind.Unspecified && dateTime.Kind != sourceKind)
                throw new ArgumentException("The conversion could not be completed because the supplied DateTime did not have the Kind property set correctly.  For example, when the Kind property is DateTimeKind.Local, the source time zone must be TimeZoneInfo.Local.", "sourceTimeZone");
            long utc = sourceTimeZone.ToUtcTicks(dateTime.Ticks, dateTime.IsAmbiguousDaylightSavingTime(), out bool invalid);
            if (invalid)
                throw new ArgumentException("The supplied DateTime represents an invalid time.  For example, when the clock is adjusted forward, any time in the period that is skipped is invalid.", "dateTime");
            DateTimeKind targetKind = KindOf(destinationTimeZone);
            // Local to local and UTC to UTC: nothing to do.
            if (sourceKind != DateTimeKind.Unspecified && dateTime.Kind != DateTimeKind.Unspecified && sourceKind == targetKind)
                return dateTime;
            return FromUtc(utc, destinationTimeZone, targetKind);
        }

        public static DateTime ConvertTimeFromUtc(DateTime dateTime, TimeZoneInfo destinationTimeZone)
        {
            if (destinationTimeZone == null)
                throw new ArgumentNullException("destinationTimeZone");
            return ConvertTime(dateTime, Utc, destinationTimeZone);
        }

        public static DateTime ConvertTimeToUtc(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
                return dateTime;
            return ConvertTime(dateTime, Local, Utc);
        }

        public static DateTime ConvertTimeToUtc(DateTime dateTime, TimeZoneInfo sourceTimeZone) => ConvertTime(dateTime, sourceTimeZone, Utc);

        public static DateTime ConvertTimeBySystemTimeZoneId(DateTime dateTime, string destinationTimeZoneId)
            => ConvertTime(dateTime, FindSystemTimeZoneById(destinationTimeZoneId));

        public static DateTime ConvertTimeBySystemTimeZoneId(DateTime dateTime, string sourceTimeZoneId, string destinationTimeZoneId)
            => ConvertTime(dateTime, FindSystemTimeZoneById(sourceTimeZoneId), FindSystemTimeZoneById(destinationTimeZoneId));

        public static DateTimeOffset ConvertTimeBySystemTimeZoneId(DateTimeOffset dateTimeOffset, string destinationTimeZoneId)
            => ConvertTime(dateTimeOffset, FindSystemTimeZoneById(destinationTimeZoneId));

        private static DateTime FromUtc(long utc, TimeZoneInfo zone, DateTimeKind kind)
        {
            long offset = zone.OffsetFromUtc(utc, out bool isDst);
            long ticks = utc + offset;
            if (ticks > DateTime.MaxTicks)
                return new DateTime(DateTime.MaxTicks, kind);
            if (ticks < DateTime.MinTicks)
                return new DateTime(DateTime.MinTicks, kind);
            bool ambiguousDst = false;
            if (isDst && kind == DateTimeKind.Local)
                zone.LocalCandidates(ticks, false, out _, out _, out ambiguousDst);
            return new DateTime(ticks, kind, ambiguousDst);
        }

        // ---- what DateTime and DateTimeOffset ask ----

        // The local zone's offset at a UTC time, in ticks.
        internal static long LocalOffsetFromUtc(long utcTicks, out bool isAmbiguousLocalDst)
        {
            isAmbiguousLocalDst = false;
#if WASM
            return 0;
#else
            TimeZoneInfo local = Local;
            if (local._kind == ZoneKind.Fixed)
                return local._baseOffset;
            long offset = local.OffsetFromUtc(utcTicks, out bool isDst);
            // A local time that also happens in standard time remembers that
            // it was the daylight one, as .NET's DateTime does.
            if (isDst)
            {
                local.LocalCandidates(utcTicks + offset, false, out _, out _, out bool ambiguous);
                isAmbiguousLocalDst = ambiguous;
            }
            return offset;
#endif
        }

        // The local zone's offset for a local time, in ticks: standard time
        // for an ambiguous or skipped one, unless preferDaylight.
        internal static long LocalOffsetFromLocal(long localTicks, bool preferDaylight)
        {
#if WASM
            return 0;
#else
            TimeZoneInfo local = Local;
            return local._kind == ZoneKind.Fixed ? local._baseOffset : local.OffsetFromLocal(localTicks, preferDaylight, out _);
#endif
        }

        private long ToUtcTicks(long ticks, bool preferDaylight, out bool invalid)
        {
            invalid = false;
            if (_kind == ZoneKind.Fixed)
                return ticks - _baseOffset;
            long offset = OffsetFromLocal(ticks, preferDaylight, out ZoneState state);
            invalid = state == ZoneState.Invalid;
            return ticks - offset;
        }

        private long OffsetFromUtc(long utcTicks, out bool isDst)
        {
            isDst = false;
            if (_kind == ZoneKind.Fixed)
                return _baseOffset;
            long seconds = RawOffset(FloorDivide(utcTicks - DateTime.UnixEpochTicks, TicksPerSecond), out isDst, out _);
            // .NET keeps whole minutes, truncating the difference from the
            // standard offset.
            long difference = seconds * TicksPerSecond - _baseOffset;
            return _baseOffset + difference / TicksPerMinute * TicksPerMinute;
        }

        private enum ZoneState { Standard, Daylight, Invalid }

        private long OffsetFromLocal(long localTicks, bool preferDaylight, out ZoneState state)
            => LocalCandidates(localTicks, preferDaylight, out _, out state);

        private long LocalCandidates(long localTicks, bool preferDaylight, out bool unused, out ZoneState state)
            => LocalCandidates(localTicks, preferDaylight, out unused, out state, out _, out _, out _);

        private long LocalCandidates(long localTicks, bool preferDaylight, out bool unused, out ZoneState state, out bool ambiguous)
            => LocalCandidates(localTicks, preferDaylight, out unused, out state, out ambiguous, out _, out _);

        // Which offsets give this local time: the offsets a day before and a
        // day after it, each checked by converting back. Two that work make
        // it ambiguous, none skipped (in a gap).
        private long LocalCandidates(long localTicks, bool preferDaylight, out bool unused, out ZoneState state, out bool ambiguous, out long first, out long second)
        {
            unused = false;
            first = OffsetFromUtc(localTicks - TicksPerDay, out bool firstDst);
            second = OffsetFromUtc(localTicks + TicksPerDay, out bool secondDst);
            bool firstWorks = OffsetFromUtc(localTicks - first, out _) == first;
            bool secondWorks = first != second && OffsetFromUtc(localTicks - second, out _) == second;
            ambiguous = firstWorks && secondWorks;
            if (ambiguous)
            {
                bool pickSecond = preferDaylight ? secondDst && !firstDst : firstDst && !secondDst;
                if (!firstDst && !secondDst)
                    pickSecond = true;
                state = (pickSecond ? secondDst : firstDst) ? ZoneState.Daylight : ZoneState.Standard;
                return pickSecond ? second : first;
            }
            if (firstWorks)
            {
                state = firstDst ? ZoneState.Daylight : ZoneState.Standard;
                return first;
            }
            if (secondWorks)
            {
                state = secondDst ? ZoneState.Daylight : ZoneState.Standard;
                return second;
            }
            // Skipped: the standard offset of the two.
            state = ZoneState.Invalid;
            return firstDst && !secondDst ? second : first;
        }

        private static long FloorDivide(long value, long divisor)
        {
            long quotient = value / divisor;
            return value % divisor < 0 ? quotient - 1 : quotient;
        }

        private static string OffsetText(long ticks)
        {
            char sign = '+';
            if (ticks < 0)
            {
                sign = '-';
                ticks = -ticks;
            }
            long minutes = ticks / TicksPerMinute;
            char* text = stackalloc char[6];
            text[0] = sign;
            text[1] = (char)('0' + minutes / 600);
            text[2] = (char)('0' + minutes / 60 % 10);
            text[3] = ':';
            text[4] = (char)('0' + minutes % 60 / 10);
            text[5] = (char)('0' + minutes % 10);
            return Number.FromChars(text, 6);
        }

        private static bool IsUtcId(string id)
            => EqualsIgnoreCase(id, "UTC") || EqualsIgnoreCase(id, "Etc/UTC") || EqualsIgnoreCase(id, "Etc/UCT") || EqualsIgnoreCase(id, "UCT")
            || EqualsIgnoreCase(id, "Etc/Universal") || EqualsIgnoreCase(id, "Universal") || EqualsIgnoreCase(id, "Etc/Zulu") || EqualsIgnoreCase(id, "Zulu");

        private static bool EqualsIgnoreCase(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                char x = a[i], y = b[i];
                if (x >= 'A' && x <= 'Z') x = (char)(x + 32);
                if (y >= 'A' && y <= 'Z') y = (char)(y + 32);
                if (x != y)
                    return false;
            }
            return true;
        }

        // ---- the C library ----

        // struct tm as Linux, macOS, Android and WebAssembly lay it out:
        // nine ints, then tm_gmtoff and tm_zone on the platforms that have
        // them (Windows' localtime_r in win/runtime.c fills the nine).
        [StructLayout(LayoutKind.Sequential)]
        private struct Tm
        {
            public int Second, Minute, Hour, Day, Month, Year, WeekDay, YearDay, IsDst;
            public nint GmtOffset;
            public byte* Zone;
            public nint Spare1, Spare2;
        }

#if !WASM
        [DllImport("*"), SuppressGCTransition]
        private static extern Tm* localtime_r(long* time, Tm* result);

        [DllImport("*"), SuppressGCTransition]
        private static extern nint readlink(byte* path, byte* buffer, nuint size);
#endif

        // The local zone's offset in seconds at a Unix time, whether it is
        // daylight saving time, and its abbreviation (null if unknown).
        private static long RawOffset(long unixSeconds, out bool isDst, out byte* zone)
        {
            isDst = false;
            zone = null;
#if WASM
            return 0;
#else
            Tm tm = default;
            if (localtime_r(&unixSeconds, &tm) == null)
                return 0;
            isDst = tm.IsDst > 0;
            zone = tm.Zone;
            long local = DaysFromCivil(tm.Year + 1900L, tm.Month + 1, tm.Day) * 86_400 + tm.Hour * 3600L + tm.Minute * 60L + tm.Second;
            return local - unixSeconds;
#endif
        }

        // Days from 1970-01-01 to a date, for any year (year 0 and before
        // too, which localtime_r gives near 0001-01-01): Howard Hinnant's
        // days_from_civil.
        private static long DaysFromCivil(long year, int month, int day)
        {
            year -= month <= 2 ? 1 : 0;
            long era = FloorDivide(year, 400);
            long yearOfEra = year - era * 400;
            long dayOfYear = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
            long dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
            return era * 146_097 + dayOfEra - 719_468;
        }

        private static long NowSeconds() => FloorDivide(DateTime.UtcNow.Ticks - DateTime.UnixEpochTicks, TicksPerSecond);

        // Whether the system has a zone other than UTC at all.
        private static bool HasZone()
        {
            long now = NowSeconds();
            return RawOffset(now, out _, out _) != 0 || RawOffset(now - 182 * 86_400, out _, out _) != 0
                || RawOffset(-3_000_000_000, out _, out _) != 0 || !IsUtcId(LocalId());
        }

        // The standard offset now: today's, or half a year away's if today is
        // in daylight saving time.
        private static long LocalBaseOffset()
        {
            long now = NowSeconds();
            long offset = RawOffset(now, out bool isDst, out _);
            if (isDst)
            {
                long other = RawOffset(now - 182 * 86_400, out bool otherDst, out _);
                if (otherDst)
                    other = RawOffset(now + 182 * 86_400, out otherDst, out _);
                if (!otherDst)
                    offset = other;
            }
            return offset / 60 * TicksPerMinute;
        }

        private string LocalStandardName()
        {
            if (_kind == ZoneKind.Fixed)
                return _id;
            long now = NowSeconds();
            RawOffset(now, out bool isDst, out byte* zone);
            if (isDst)
                RawOffset(now - 182 * 86_400, out _, out zone);
            return zone == null ? _id : Utf8(zone);
        }

        // The most recent daylight saving time's abbreviation, looking back
        // a week at a time to 1900, and whether the offset ever changed.
        private void FindDaylight()
        {
            if (_kind == ZoneKind.Fixed)
            {
                _supportsDst = 1;
                _daylightName ??= _standardName ?? _id;
                return;
            }
            long now = NowSeconds();
            const long Week = 7 * 86_400;
            const long Year1900 = -2_208_988_800;
            bool changes = false;
            string daylight = null;
            for (long t = now + 366 * 86_400; t > Year1900; t -= Week)
            {
                long offset = RawOffset(t, out bool isDst, out byte* zone);
                if (offset * TicksPerSecond != _baseOffset)
                    changes = true;
                if (isDst)
                {
                    changes = true;
                    daylight = zone == null ? _id : Utf8(zone);
                    break;
                }
            }
            if (!changes && RawOffset(-5_000_000_000, out _, out _) * TicksPerSecond != _baseOffset)
                changes = true;
            _supportsDst = changes ? 2 : 1;
            _daylightName = daylight ?? StandardName;
        }

        private static string Utf8(byte* text)
        {
            int length = 0;
            while (text[length] != 0)
                length++;
            char* chars = stackalloc char[length];
            for (int i = 0; i < length; i++)
                chars[i] = (char)text[i];
            return Number.FromChars(chars, length);
        }

        // TZ (without a leading ':', and after "zoneinfo/" if it's a path),
        // else where /etc/localtime points, else "Local".
        private static string LocalId()
        {
            string tz = Environment.GetEnvironmentVariable("TZ");
            if (tz != null && tz.Length > 0)
                return AfterZoneInfo(tz[0] == ':' ? Substring(tz, 1) : tz);
#if !WASM
            byte* path = stackalloc byte[] { (byte)'/', (byte)'e', (byte)'t', (byte)'c', (byte)'/', (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', (byte)'t', (byte)'i', (byte)'m', (byte)'e', 0 };
            byte* buffer = stackalloc byte[512];
            nint length = readlink(path, buffer, 511);
            if (length > 0)
            {
                buffer[length] = 0;
                return AfterZoneInfo(Utf8(buffer));
            }
#endif
            return "Local";
        }

        private static string AfterZoneInfo(string path)
        {
            const string Marker = "zoneinfo/";
            for (int i = path.Length - Marker.Length; i >= 0; i--)
            {
                bool match = true;
                for (int j = 0; j < Marker.Length && match; j++)
                    match = path[i + j] == Marker[j];
                if (match)
                    return Substring(path, i + Marker.Length);
            }
            return path;
        }

        private static string Substring(string s, int start)
        {
            fixed (char* p = s)
                return Number.FromChars(p + start, s.Length - start);
        }
    }

    public class TimeZoneNotFoundException : Exception
    {
        public TimeZoneNotFoundException() : base("Exception of type 'System.TimeZoneNotFoundException' was thrown.") { }
        public TimeZoneNotFoundException(string message) : base(message) { }
    }
}
