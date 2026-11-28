using System.Globalization;

namespace System
{
    // .NET's date and time formats in the invariant culture, for DateTime,
    // DateTimeOffset, DateOnly, TimeOnly and TimeSpan: the standard ones
    // ("d", "o", "R", "c", ...) and custom patterns ("yyyy-MM-dd HH:mm"),
    // with .NET's output and FormatExceptions, after dotnet/runtime's
    // DateTimeFormat.cs and TimeSpanFormat.cs (MIT).
    //
    // Everything is written through a Writer: into the caller's span for
    // TryFormat, or into a stack buffer and then a string of the exact length
    // for ToString, so nothing is allocated but the result.
    internal static unsafe class DateTimeFormat
    {
        // A DateTime formats with no offset; a DateTimeOffset with its own.
        internal static readonly TimeSpan NullOffset = TimeSpan.MinValue;

        // Names and patterns of your own (a writable DateTimeFormatInfo) are
        // read only through this, which such a DateTimeFormatInfo sets: a
        // program without one keeps the invariant culture's built in.
        internal abstract class DateCulture
        {
            internal abstract DateTimeFormatInfo InfoFor(IFormatProvider provider);
            internal abstract string FullPattern(DateTimeFormatInfo info);
            internal abstract string OffsetPattern(DateTimeFormatInfo info);
            internal abstract string[] Patterns(DateTimeFormatInfo info, char format);
            internal abstract string MonthName(DateTimeFormatInfo info, int month, bool abbreviated, ReadOnlySpan<char> format, int index, int length);
        }

        private sealed class CultureDates : DateCulture
        {
            internal override DateTimeFormatInfo InfoFor(IFormatProvider provider)
            {
                DateTimeFormatInfo info = DateTimeFormatInfo.GetInstance(provider);
                return ReferenceEquals(info, DateTimeFormatInfo.InvariantInfo) ? null : info;
            }

            internal override string FullPattern(DateTimeFormatInfo info) => info.FullPattern;
            internal override string OffsetPattern(DateTimeFormatInfo info) => info.OffsetPattern;
            internal override string[] Patterns(DateTimeFormatInfo info, char format) => info.GetAllDateTimePatterns(format);

            // A month's name from your own names: the genitive form (as "1 of
            // January" puts it) next to a day number, when it has one.
            internal override string MonthName(DateTimeFormatInfo info, int month, bool abbreviated, ReadOnlySpan<char> format, int index, int length)
            {
                string[] names = abbreviated ? info._abbreviatedMonthNames : info._monthNames;
                if (IsGenitive(format, index, length))
                {
                    string[] forms = abbreviated ? info._abbreviatedMonthGenitiveNames : info._monthGenitiveNames;
                    bool differ = false;
                    for (int k = 0; k < 12 && !differ; k++)
                        differ = forms[k] != names[k];
                    if (differ)
                        names = forms;
                }
                return names[month - 1];
            }

            // Whether "d" or "dd" (not "ddd") is before or after the month at
            // index, as .NET's IsUseGenitiveForm decides.
            private static bool IsGenitive(ReadOnlySpan<char> format, int index, int length)
            {
                int i;
                for (i = index - 1; i >= 0 && format[i] != 'd'; i--) { }
                if (i >= 0)
                {
                    int repeat = 0;
                    while (--i >= 0 && format[i] == 'd')
                        repeat++;
                    if (repeat <= 1)
                        return true;
                }
                for (i = index + length; i < format.Length && format[i] != 'd'; i++) { }
                if (i < format.Length)
                {
                    int repeat = 0;
                    while (++i < format.Length && format[i] == 'd')
                        repeat++;
                    if (repeat <= 1)
                        return true;
                }
                return false;
            }
        }

        private static DateCulture s_culture;

        internal static void UseCultures()
        {
            if (s_culture == null)
                using (System.Runtime.ObjectAllocator.Lasting())
                    s_culture = new CultureDates();
        }

        // The names and patterns for a provider: null for the invariant ones.
        internal static DateTimeFormatInfo InfoFor(IFormatProvider provider) => s_culture == null ? null : s_culture.InfoFor(provider);

        private const string InvalidString = "Input string was not in a correct format.";

        internal const string ShortDate = "MM/dd/yyyy";
        internal const string LongDate = "dddd, dd MMMM yyyy";
        internal const string ShortTime = "HH:mm";
        internal const string LongTime = "HH:mm:ss";
        internal const string FullDateTime = "dddd, dd MMMM yyyy HH:mm:ss";
        internal const string MonthDay = "MMMM dd";
        internal const string YearMonth = "yyyy MMMM";
        internal const string Rfc1123 = "ddd, dd MMM yyyy HH':'mm':'ss 'GMT'";
        internal const string Sortable = "yyyy'-'MM'-'dd'T'HH':'mm':'ss";
        internal const string UniversalSortable = "yyyy'-'MM'-'dd HH':'mm':'ss'Z'";
        internal const string RoundTripPattern = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.fffffffK";

        internal static string DayName(int day) => day switch
        {
            0 => "Sunday", 1 => "Monday", 2 => "Tuesday", 3 => "Wednesday", 4 => "Thursday", 5 => "Friday", _ => "Saturday",
        };

        internal static string AbbreviatedDayName(int day) => day switch
        {
            0 => "Sun", 1 => "Mon", 2 => "Tue", 3 => "Wed", 4 => "Thu", 5 => "Fri", _ => "Sat",
        };

        internal static string MonthName(int month) => month switch
        {
            1 => "January", 2 => "February", 3 => "March", 4 => "April", 5 => "May", 6 => "June",
            7 => "July", 8 => "August", 9 => "September", 10 => "October", 11 => "November", 12 => "December", _ => "",
        };

        internal static string AbbreviatedMonthName(int month) => month switch
        {
            1 => "Jan", 2 => "Feb", 3 => "Mar", 4 => "Apr", 5 => "May", 6 => "Jun",
            7 => "Jul", 8 => "Aug", 9 => "Sep", 10 => "Oct", 11 => "Nov", 12 => "Dec", _ => "",
        };

        // Collects output: what fits goes into the span, and Length counts
        // all of it, so a too-small span can be retried at the right size.
        private ref struct Writer
        {
            private readonly Span<char> _buffer;
            public int Length;
            private char _last;
            // Names and patterns of your own, or null for the invariant ones.
            public DateTimeFormatInfo Info;

            public Writer(Span<char> buffer)
            {
                _buffer = buffer;
                Length = 0;
                _last = '\0';
                Info = null;
            }

            public bool Fits => Length <= _buffer.Length;

            public void Add(char c)
            {
                if (Length < _buffer.Length)
                    _buffer[Length] = c;
                Length++;
                _last = c;
            }

            public void Add(string s)
            {
                for (int i = 0; i < s.Length; i++)
                    Add(s[i]);
            }

            // At least minDigits digits.
            public void Digits(ulong value, int minDigits)
            {
                int count = 1;
                for (ulong v = value / 10; v != 0; v /= 10)
                    count++;
                for (int i = count; i < minDigits; i++)
                    Add('0');
                int start = Length;
                Length += count;
                _last = (char)('0' + (int)(value % 10));
                for (int i = Length - 1; i >= start; i--, value /= 10)
                {
                    if (i < _buffer.Length)
                        _buffer[i] = (char)('0' + (int)(value % 10));
                }
            }

            // The last character written, even past the end of the span.
            public char Last => _last;

            public void RemoveLast()
            {
                Length--;
                _last = '\0';
            }
        }

        private enum What { DateTime, Date, Time, Interval }

        // ---- entry points ----

        internal static string Format(DateTime value, string format, TimeSpan offset, IFormatProvider provider = null)
            => ToText(What.DateTime, value.Ticks, value, offset.Ticks, format == null ? default : Number.AsSpan(format), InfoFor(provider));

        internal static bool TryFormat(DateTime value, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, TimeSpan offset, IFormatProvider provider = null)
            => Try(What.DateTime, value, offset.Ticks, format, destination, out charsWritten, InfoFor(provider));

        internal static string FormatDate(DateOnly value, string format, IFormatProvider provider = null)
            => ToText(What.Date, value.DayNumber * TimeSpan.TicksPerDay, new DateTime(value.DayNumber * TimeSpan.TicksPerDay), NullOffset.Ticks, format == null ? default : Number.AsSpan(format), InfoFor(provider));

        internal static bool TryFormatDate(DateOnly value, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider = null)
            => Try(What.Date, new DateTime(value.DayNumber * TimeSpan.TicksPerDay), NullOffset.Ticks, format, destination, out charsWritten, InfoFor(provider));

        internal static string FormatTime(TimeOnly value, string format, IFormatProvider provider = null)
            => ToText(What.Time, value.Ticks, new DateTime(value.Ticks), NullOffset.Ticks, format == null ? default : Number.AsSpan(format), InfoFor(provider));

        internal static bool TryFormatTime(TimeOnly value, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider = null)
            => Try(What.Time, new DateTime(value.Ticks), NullOffset.Ticks, format, destination, out charsWritten, InfoFor(provider));

        internal static string FormatTimeSpan(TimeSpan value, string format)
            => ToText(What.Interval, value.Ticks, default, 0, format == null ? default : Number.AsSpan(format), null);

        internal static bool TryFormatTimeSpan(TimeSpan value, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format)
        {
            Writer w = new Writer(destination);
            WriteInterval(ref w, value.Ticks, format);
            return Done(ref w, out charsWritten);
        }

        private static bool Try(What what, DateTime value, long offset, ReadOnlySpan<char> format, Span<char> destination, out int charsWritten, DateTimeFormatInfo info)
        {
            Writer w = new Writer(destination) { Info = info };
            Write(ref w, what, value, offset, format, true);
            return Done(ref w, out charsWritten);
        }

        private static bool Done(ref Writer w, out int charsWritten)
        {
            if (w.Fits)
            {
                charsWritten = w.Length;
                return true;
            }
            charsWritten = 0;
            return false;
        }

        private static string ToText(What what, long ticks, DateTime value, long offset, ReadOnlySpan<char> format, DateTimeFormatInfo info)
        {
            char* stack = stackalloc char[64];
            Writer w = new Writer(new Span<char>(stack, 64)) { Info = info };
            if (what == What.Interval)
                WriteInterval(ref w, ticks, format);
            else
                Write(ref w, what, value, offset, format, false);
            if (w.Fits)
                return Number.FromChars(stack, w.Length);
            // Longer: again, into a buffer of the length it needs.
            char[] buffer = new char[w.Length];
            fixed (char* p = buffer)
            {
                Writer again = new Writer(new Span<char>(p, w.Length)) { Info = info };
                if (what == What.Interval)
                    WriteInterval(ref again, ticks, format);
                else
                    Write(ref again, what, value, offset, format, false);
                return Number.FromChars(p, w.Length);
            }
        }

        // ---- DateTime, DateTimeOffset, DateOnly, TimeOnly ----

        // DateOnly and TimeOnly throw other messages from TryFormat than from
        // ToString, as in .NET.
        private static void Write(ref Writer w, What what, DateTime value, long offset, ReadOnlySpan<char> format, bool isTry)
        {
            bool isOffset = offset != NullOffset.Ticks;
            if (what == What.Date)
            {
                if (format.Length == 0)
                    format = Number.AsSpan("d");
                if (format.Length == 1)
                {
                    switch (format[0] | 0x20)
                    {
                        case 'o':
                            RoundTripDate(ref w, value);
                            return;
                        case 'r':
                            Invariant(ref w, value, "ddd, dd MMM yyyy", offset);
                            return;
                        case 'd':
                        case 'm':
                        case 'y':
                            break;
                        default:
                            throw new FormatException(isTry ? "Format specifier was invalid." : InvalidString);
                    }
                }
                else
                {
                    CheckCustom(format, "tfFhHmszK:", "DateOnly", isTry);
                }
            }
            else if (what == What.Time)
            {
                if (format.Length == 0)
                    format = Number.AsSpan("t");
                if (format.Length == 1)
                {
                    switch (format[0] | 0x20)
                    {
                        case 'o':
                            Invariant(ref w, value, "HH':'mm':'ss'.'fffffff", offset);
                            return;
                        case 'r':
                            Invariant(ref w, value, "HH':'mm':'ss", offset);
                            return;
                        case 't':
                            break;
                        default:
                            throw new FormatException(isTry ? "Format specifier was invalid." : InvalidString);
                    }
                }
                else
                {
                    CheckCustom(format, "dMy/z", "TimeOnly", isTry);
                }
            }

            DateTimeFormatInfo info = w.Info;
            if (format.Length == 0)
            {
                if (!isOffset)
                    format = Number.AsSpan("G");
                else if (info == null)
                    format = Number.AsSpan("MM/dd/yyyy HH:mm:ss zzz");
                else
                {
                    Custom(ref w, value, s_culture.OffsetPattern(info), offset);
                    return;
                }
            }

            if (format.Length == 1)
            {
                string pattern;
                switch (format[0])
                {
                    case 'o':
                    case 'O':
                        RoundTrip(ref w, value, offset);
                        return;
                    case 'r':
                    case 'R':
                        if (isOffset)
                            value = new DateTime(value.Ticks - offset);
                        Invariant(ref w, value, Rfc1123, NullOffset.Ticks);
                        return;
                    case 'u':
                        if (isOffset)
                            value = new DateTime(value.Ticks - offset);
                        Invariant(ref w, value, UniversalSortable, NullOffset.Ticks);
                        return;
                    case 'U':
                        if (isOffset)
                            throw new FormatException(InvalidString);
                        Custom(ref w, value.ToUniversalTime(), info == null ? FullDateTime : s_culture.FullPattern(info), offset);
                        return;
                    case 's':
                        Invariant(ref w, value, Sortable, offset);
                        return;
                    case 'd': pattern = info == null ? ShortDate : info._shortDatePattern; break;
                    case 'D': pattern = info == null ? LongDate : info._longDatePattern; break;
                    case 'f': pattern = info == null ? LongDate + " " + ShortTime : info._longDatePattern + " " + info._shortTimePattern; break;
                    case 'F': pattern = info == null ? FullDateTime : s_culture.FullPattern(info); break;
                    case 'g': pattern = info == null ? ShortDate + " " + ShortTime : info._shortDatePattern + " " + info._shortTimePattern; break;
                    case 'G': pattern = info == null ? ShortDate + " " + LongTime : info._shortDatePattern + " " + info._longTimePattern; break;
                    case 'm':
                    case 'M': pattern = info == null ? MonthDay : info._monthDayPattern; break;
                    case 't': pattern = info == null ? ShortTime : info._shortTimePattern; break;
                    case 'T': pattern = info == null ? LongTime : info._longTimePattern; break;
                    case 'y':
                    case 'Y': pattern = info == null ? YearMonth : info._yearMonthPattern; break;
                    default:
                        throw new FormatException(InvalidString);
                }
                Custom(ref w, value, pattern, offset);
                return;
            }
            Custom(ref w, value, format, offset);
        }

        // DateOnly and TimeOnly refuse custom patterns with the other's parts.
        private static void CheckCustom(ReadOnlySpan<char> format, string forbidden, string type, bool isTry)
        {
            int i = 0;
            while (i < format.Length)
            {
                char c = format[i];
                if (c == '\\')
                {
                    if (i == format.Length - 1)
                        throw Unspecific(format, type, isTry, null);
                    i += 2;
                }
                else if (c == '\'' || c == '"')
                {
                    i++;
                    while (i < format.Length && format[i] != c)
                        i++;
                    if (i >= format.Length)
                        throw Unspecific(format, type, isTry, BadQuote(c));
                    i++;
                }
                else
                {
                    for (int j = 0; j < forbidden.Length; j++)
                    {
                        if (forbidden[j] == c)
                            throw Unspecific(format, type, isTry, null);
                    }
                    i++;
                }
            }
        }

        private static FormatException Unspecific(ReadOnlySpan<char> format, string type, bool isTry, FormatException otherwise)
            => isTry ? new FormatException("String '" + Text(format) + "' contains parts which are not specific to the " + type + ".")
                : otherwise ?? new FormatException(InvalidString);

        private static string Text(ReadOnlySpan<char> text)
        {
            fixed (char* p = text)
                return Number.FromChars(p, text.Length);
        }

        private static FormatException BadQuote(char quote)
            => new FormatException("Cannot find a matching quote character for the character '" + quote.ToString() + "'.");

        // yyyy-MM-ddTHH:mm:ss.fffffff, then Z, the offset or nothing.
        private static void RoundTrip(ref Writer w, DateTime value, long offset)
        {
            long ticks = value.Ticks;
            DateTime.GetDate(ticks / TimeSpan.TicksPerDay, out int year, out int month, out int day, out _);
            w.Digits((ulong)year, 4);
            w.Add('-');
            w.Digits((ulong)month, 2);
            w.Add('-');
            w.Digits((ulong)day, 2);
            w.Add('T');
            long time = ticks % TimeSpan.TicksPerDay;
            w.Digits((ulong)(time / TimeSpan.TicksPerHour), 2);
            w.Add(':');
            w.Digits((ulong)(time / TimeSpan.TicksPerMinute % 60), 2);
            w.Add(':');
            w.Digits((ulong)(time / TimeSpan.TicksPerSecond % 60), 2);
            w.Add('.');
            w.Digits((ulong)(time % TimeSpan.TicksPerSecond), 7);
            RoundTripZone(ref w, value, offset);
        }

        private static void RoundTripDate(ref Writer w, DateTime value)
        {
            DateTime.GetDate(value.Ticks / TimeSpan.TicksPerDay, out int year, out int month, out int day, out _);
            w.Digits((ulong)year, 4);
            w.Add('-');
            w.Digits((ulong)month, 2);
            w.Add('-');
            w.Digits((ulong)day, 2);
        }

        // "K": Z for UTC, the offset for local time or a DateTimeOffset,
        // nothing for Unspecified.
        private static void RoundTripZone(ref Writer w, DateTime value, long offset)
        {
            if (offset == NullOffset.Ticks)
            {
                switch (value.Kind)
                {
                    case DateTimeKind.Local:
                        offset = TimeZoneInfo.Local.GetUtcOffset(value).Ticks;
                        break;
                    case DateTimeKind.Utc:
                        w.Add('Z');
                        return;
                    default:
                        return;
                }
            }
            if (offset >= 0)
            {
                w.Add('+');
            }
            else
            {
                w.Add('-');
                offset = -offset;
            }
            w.Digits((ulong)(offset / TimeSpan.TicksPerHour), 2);
            w.Add(':');
            w.Digits((ulong)(offset / TimeSpan.TicksPerMinute % 60), 2);
        }

        private static int Repeat(ReadOnlySpan<char> format, int pos, char c)
        {
            int end = pos + 1;
            while (end < format.Length && format[end] == c)
                end++;
            return end - pos;
        }

        private static ulong Pow10(int n)
        {
            ulong result = 1;
            while (n-- > 0)
                result *= 10;
            return result;
        }

        private static void Custom(ref Writer w, DateTime value, string format, long offset)
            => Custom(ref w, value, Number.AsSpan(format), offset);

        // "o", "r", "s", "u": the invariant culture's, whatever the provider.
        private static void Invariant(ref Writer w, DateTime value, string format, long offset)
        {
            DateTimeFormatInfo info = w.Info;
            w.Info = null;
            Custom(ref w, value, Number.AsSpan(format), offset);
            w.Info = info;
        }

        private static void Custom(ref Writer w, DateTime value, ReadOnlySpan<char> format, long offset)
        {
            long ticks = value.Ticks;
            DateTime.GetDate(ticks / TimeSpan.TicksPerDay, out int year, out int month, out int day, out _);
            long time = ticks % TimeSpan.TicksPerDay;
            int hour = (int)(time / TimeSpan.TicksPerHour);
            bool timeOnly = true;
            int i = 0;
            while (i < format.Length)
            {
                char c = format[i];
                int length;
                switch (c)
                {
                    case 'g':
                        length = Repeat(format, i, c);
                        if (w.Info == null)
                            w.Add("A.D.");
                        else
                            w.Add(w.Info._eraName);
                        break;
                    case 'h':
                        length = Repeat(format, i, c);
                        int hour12 = hour % 12;
                        w.Digits((ulong)(hour12 == 0 ? 12 : hour12), length > 2 ? 2 : length);
                        break;
                    case 'H':
                        length = Repeat(format, i, c);
                        w.Digits((ulong)hour, length > 2 ? 2 : length);
                        break;
                    case 'm':
                        length = Repeat(format, i, c);
                        w.Digits((ulong)(time / TimeSpan.TicksPerMinute % 60), length > 2 ? 2 : length);
                        break;
                    case 's':
                        length = Repeat(format, i, c);
                        w.Digits((ulong)(time / TimeSpan.TicksPerSecond % 60), length > 2 ? 2 : length);
                        break;
                    case 'f':
                    case 'F':
                        length = Repeat(format, i, c);
                        if (length > 7)
                            throw new FormatException(InvalidString);
                        ulong fraction = (ulong)(time % TimeSpan.TicksPerSecond) / Pow10(7 - length);
                        if (c == 'f')
                        {
                            w.Digits(fraction, length);
                        }
                        else
                        {
                            int digits = length;
                            while (digits > 0 && fraction % 10 == 0)
                            {
                                fraction /= 10;
                                digits--;
                            }
                            if (digits > 0)
                                w.Digits(fraction, digits);
                            else if (w.Length > 0 && w.Last == '.')
                                w.RemoveLast();
                        }
                        break;
                    case 't':
                        length = Repeat(format, i, c);
                        if (w.Info == null)
                        {
                            if (length == 1)
                                w.Add(hour < 12 ? 'A' : 'P');
                            else
                                w.Add(hour < 12 ? "AM" : "PM");
                        }
                        else
                        {
                            string designator = hour < 12 ? w.Info._amDesignator : w.Info._pmDesignator;
                            if (length != 1)
                                w.Add(designator);
                            else if (designator.Length >= 1)
                                w.Add(designator[0]);
                        }
                        break;
                    case 'd':
                        length = Repeat(format, i, c);
                        if (length <= 2)
                            w.Digits((ulong)day, length);
                        else
                        {
                            int dayOfWeek = (int)((ticks / TimeSpan.TicksPerDay + 1) % 7);
                            if (w.Info == null)
                                w.Add(length == 3 ? AbbreviatedDayName(dayOfWeek) : DayName(dayOfWeek));
                            else
                                w.Add(length == 3 ? w.Info._abbreviatedDayNames[dayOfWeek] : w.Info._dayNames[dayOfWeek]);
                        }
                        timeOnly = false;
                        break;
                    case 'M':
                        length = Repeat(format, i, c);
                        if (length <= 2)
                            w.Digits((ulong)month, length);
                        else if (w.Info == null)
                            w.Add(length == 3 ? AbbreviatedMonthName(month) : MonthName(month));
                        else
                            w.Add(s_culture.MonthName(w.Info, month, length == 3, format, i, length));
                        timeOnly = false;
                        break;
                    case 'y':
                        length = Repeat(format, i, c);
                        if (length <= 2)
                            w.Digits((ulong)(year % 100), length);
                        else
                            w.Digits((ulong)year, length);
                        timeOnly = false;
                        break;
                    case 'z':
                        length = Repeat(format, i, c);
                        Zone(ref w, value, offset, length, timeOnly);
                        break;
                    case 'K':
                        length = 1;
                        RoundTripZone(ref w, value, offset);
                        break;
                    case ':':
                        if (w.Info == null)
                            w.Add(':');
                        else
                            w.Add(w.Info._timeSeparator);
                        length = 1;
                        break;
                    case '/':
                        if (w.Info == null)
                            w.Add('/');
                        else
                            w.Add(w.Info._dateSeparator);
                        length = 1;
                        break;
                    case '\'':
                    case '"':
                        length = Quoted(ref w, format, i);
                        break;
                    case '%':
                        if (i + 1 < format.Length && format[i + 1] != '%')
                        {
                            char next = format[i + 1];
                            Custom(ref w, value, new ReadOnlySpan<char>(&next, 1), offset);
                            length = 2;
                        }
                        else
                        {
                            throw new FormatException(InvalidString);
                        }
                        break;
                    case '\\':
                        if (i + 1 < format.Length)
                        {
                            w.Add(format[i + 1]);
                            length = 2;
                        }
                        else
                        {
                            throw new FormatException(InvalidString);
                        }
                        break;
                    default:
                        w.Add(c);
                        length = 1;
                        break;
                }
                i += length;
            }
        }

        // "z", "zz", "zzz": the offset; for a DateTime, the local zone's
        // (a time of day alone on 0001-01-01 takes today's offset).
        private static void Zone(ref Writer w, DateTime value, long offset, int length, bool timeOnly)
        {
            if (offset == NullOffset.Ticks)
            {
                if (timeOnly && value.Ticks < TimeSpan.TicksPerDay)
                    offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).Ticks;
                else if (value.Kind == DateTimeKind.Utc)
                    offset = 0;
                else
                    offset = TimeZoneInfo.Local.GetUtcOffset(value).Ticks;
            }
            if (offset >= 0)
            {
                w.Add('+');
            }
            else
            {
                w.Add('-');
                offset = -offset;
            }
            ulong hours = (ulong)(offset / TimeSpan.TicksPerHour);
            if (length <= 1)
            {
                w.Digits(hours, 1);
            }
            else
            {
                w.Digits(hours, 2);
                if (length >= 3)
                {
                    w.Add(':');
                    w.Digits((ulong)(offset / TimeSpan.TicksPerMinute % 60), 2);
                }
            }
        }

        // A quoted literal; a backslash inside escapes the next character.
        private static int Quoted(ref Writer w, ReadOnlySpan<char> format, int pos)
        {
            int begin = pos;
            char quote = format[pos++];
            while (pos < format.Length)
            {
                char c = format[pos++];
                if (c == quote)
                    return pos - begin;
                if (c == '\\')
                {
                    if (pos < format.Length)
                        w.Add(format[pos++]);
                    else
                        throw new FormatException(InvalidString);
                }
                else
                {
                    w.Add(c);
                }
            }
            throw BadQuote(quote);
        }

        // ---- TimeSpan ----

        private static void WriteInterval(ref Writer w, long ticks, ReadOnlySpan<char> format)
        {
            if (format.Length == 0)
                format = Number.AsSpan("c");
            if (format.Length == 1)
            {
                char c = format[0];
                if (c == 'c' || c == 't' || c == 'T' || c == 'g' || c == 'G')
                {
                    IntervalStandard(ref w, ticks, c);
                    return;
                }
                throw new FormatException(InvalidString);
            }
            IntervalCustom(ref w, ticks, format);
        }

        private static ulong Magnitude(long ticks) => ticks < 0 ? (ulong)-(ticks + 1) + 1 : (ulong)ticks;

        // c: [-][d.]hh:mm:ss[.fffffff]; g: [-][d:]h:mm:ss[.FFFFFFF];
        // G: [-]d:hh:mm:ss.fffffff.
        private static void IntervalStandard(ref Writer w, long ticks, char format)
        {
            ulong value = Magnitude(ticks);
            if (ticks < 0)
                w.Add('-');
            ulong days = value / TimeSpan.TicksPerDay;
            ulong rest = value % TimeSpan.TicksPerDay;
            ulong fraction = rest % TimeSpan.TicksPerSecond;
            if (format == 'G' || days != 0)
            {
                w.Digits(days, 1);
                w.Add(format == 'g' || format == 'G' ? ':' : '.');
            }
            w.Digits(rest / TimeSpan.TicksPerHour, format == 'g' ? 1 : 2);
            w.Add(':');
            w.Digits(rest / TimeSpan.TicksPerMinute % 60, 2);
            w.Add(':');
            w.Digits(rest / TimeSpan.TicksPerSecond % 60, 2);
            if (format == 'G')
            {
                w.Add('.');
                w.Digits(fraction, 7);
            }
            else if (fraction != 0)
            {
                w.Add('.');
                int digits = 7;
                if (format == 'g')
                {
                    while (fraction % 10 == 0)
                    {
                        fraction /= 10;
                        digits--;
                    }
                }
                w.Digits(fraction, digits);
            }
        }

        private static void IntervalCustom(ref Writer w, long ticks, ReadOnlySpan<char> format)
        {
            ulong value = Magnitude(ticks);
            ulong days = value / TimeSpan.TicksPerDay;
            ulong time = value % TimeSpan.TicksPerDay;
            int i = 0;
            while (i < format.Length)
            {
                char c = format[i];
                int length;
                switch (c)
                {
                    case 'h':
                    case 'm':
                    case 's':
                        length = Repeat(format, i, c);
                        if (length > 2)
                            throw new FormatException(InvalidString);
                        ulong part = c == 'h' ? time / TimeSpan.TicksPerHour
                            : c == 'm' ? time / TimeSpan.TicksPerMinute % 60
                            : time / TimeSpan.TicksPerSecond % 60;
                        w.Digits(part, length);
                        break;
                    case 'f':
                    case 'F':
                        length = Repeat(format, i, c);
                        if (length > 7)
                            throw new FormatException(InvalidString);
                        ulong fraction = time % TimeSpan.TicksPerSecond / Pow10(7 - length);
                        if (c == 'f')
                        {
                            w.Digits(fraction, length);
                        }
                        else
                        {
                            int digits = length;
                            while (digits > 0 && fraction % 10 == 0)
                            {
                                fraction /= 10;
                                digits--;
                            }
                            if (digits > 0)
                                w.Digits(fraction, digits);
                        }
                        break;
                    case 'd':
                        length = Repeat(format, i, c);
                        if (length > 8)
                            throw new FormatException(InvalidString);
                        w.Digits(days, length);
                        break;
                    case '\'':
                    case '"':
                        length = Quoted(ref w, format, i);
                        break;
                    case '%':
                        if (i + 1 < format.Length && format[i + 1] != '%')
                        {
                            char next = format[i + 1];
                            IntervalCustom(ref w, ticks, new ReadOnlySpan<char>(&next, 1));
                            length = 2;
                        }
                        else
                        {
                            throw new FormatException(InvalidString);
                        }
                        break;
                    case '\\':
                        if (i + 1 < format.Length)
                        {
                            w.Add(format[i + 1]);
                            length = 2;
                        }
                        else
                        {
                            throw new FormatException(InvalidString);
                        }
                        break;
                    default:
                        throw new FormatException(InvalidString);
                }
                i += length;
            }
        }

        // ---- every pattern, for GetDateTimeFormats ----

        internal static string[] Patterns(char format) => format switch
        {
            'd' => new[] { ShortDate, "yyyy-MM-dd" },
            'D' => new[] { LongDate },
            'f' => new[] { LongDate + " HH:mm", LongDate + " hh:mm tt", LongDate + " H:mm", LongDate + " h:mm tt" },
            'F' or 'U' => new[] { FullDateTime },
            'g' => new[] { "MM/dd/yyyy HH:mm", "MM/dd/yyyy hh:mm tt", "MM/dd/yyyy H:mm", "MM/dd/yyyy h:mm tt",
                           "yyyy-MM-dd HH:mm", "yyyy-MM-dd hh:mm tt", "yyyy-MM-dd H:mm", "yyyy-MM-dd h:mm tt" },
            'G' => new[] { "MM/dd/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
            'm' or 'M' => new[] { MonthDay },
            'o' or 'O' => new[] { RoundTripPattern },
            'r' or 'R' => new[] { Rfc1123 },
            's' => new[] { Sortable },
            't' => new[] { "HH:mm", "hh:mm tt", "H:mm", "h:mm tt" },
            'T' => new[] { LongTime },
            'u' => new[] { UniversalSortable },
            'y' or 'Y' => new[] { YearMonth },
            _ => throw new ArgumentException("Format specifier was invalid.", "format"),
        };

        private const string StandardFormats = "dDfFgGmMoOrRstTuUyY";

        internal static string[] AllFormats(DateTime value, char format, IFormatProvider provider = null)
        {
            if (format != '\0')
                return FormatsFor(value, format, provider);
            int count = 0;
            for (int i = 0; i < StandardFormats.Length; i++)
                count += FormatsFor(value, StandardFormats[i], provider).Length;
            string[] all = new string[count];
            int n = 0;
            for (int i = 0; i < StandardFormats.Length; i++)
            {
                string[] some = FormatsFor(value, StandardFormats[i], provider);
                for (int j = 0; j < some.Length; j++)
                    all[n++] = some[j];
            }
            return all;
        }

        private static string[] FormatsFor(DateTime value, char format, IFormatProvider provider)
        {
            switch (format)
            {
                case 'r':
                case 'R':
                case 'o':
                case 'O':
                case 's':
                case 'u':
                    return new[] { Format(value, format.ToString(), NullOffset, provider) };
                case 'd': case 'D': case 'f': case 'F': case 'g': case 'G':
                case 'm': case 'M': case 't': case 'T': case 'y': case 'Y': case 'U':
                    DateTimeFormatInfo info = InfoFor(provider);
                    string[] patterns = info == null ? Patterns(format) : s_culture.Patterns(info, format);
                    DateTime target = format == 'U' ? value.ToUniversalTime() : value;
                    string[] results = new string[patterns.Length];
                    for (int i = 0; i < patterns.Length; i++)
                        results[i] = Format(target, patterns[i], NullOffset, provider);
                    return results;
                default:
                    throw new FormatException(InvalidString);
            }
        }
    }
}
