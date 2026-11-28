namespace System.Globalization
{
    // The invariant culture's dates and times, read-only: names, patterns
    // and separators, with .NET's values. Like NumberFormatInfo there is no
    // public constructor and there are no setters yet, so a custom pattern
    // doesn't compile instead of being silently ignored. No Calendar.
    public sealed class DateTimeFormatInfo : IFormatProvider
    {
        private static DateTimeFormatInfo s_invariant;

        private DateTimeFormatInfo() { }

        public static DateTimeFormatInfo InvariantInfo => s_invariant ??= new DateTimeFormatInfo();
        public static DateTimeFormatInfo CurrentInfo => InvariantInfo;

        public static DateTimeFormatInfo GetInstance(IFormatProvider provider)
        {
            if (provider is CultureInfo culture)
                return culture.DateTimeFormat;
            if (provider is DateTimeFormatInfo info)
                return info;
            if (provider != null && provider.GetFormat(typeof(DateTimeFormatInfo)) is DateTimeFormatInfo provided)
                return provided;
            return CurrentInfo;
        }

        public static DateTimeFormatInfo ReadOnly(DateTimeFormatInfo dtfi) => dtfi ?? throw new ArgumentNullException("dtfi");

        public object GetFormat(Type formatType) => formatType == typeof(DateTimeFormatInfo) ? this : null;

        public bool IsReadOnly => true;

        public string AMDesignator => "AM";
        public string PMDesignator => "PM";
        public string DateSeparator => "/";
        public string TimeSeparator => ":";
        public DayOfWeek FirstDayOfWeek => DayOfWeek.Sunday;
        public CalendarWeekRule CalendarWeekRule => CalendarWeekRule.FirstDay;
        public string NativeCalendarName => "Gregorian Calendar";

        public string ShortDatePattern => DateTimeFormat.ShortDate;
        public string LongDatePattern => DateTimeFormat.LongDate;
        public string ShortTimePattern => DateTimeFormat.ShortTime;
        public string LongTimePattern => DateTimeFormat.LongTime;
        public string FullDateTimePattern => DateTimeFormat.FullDateTime;
        public string MonthDayPattern => DateTimeFormat.MonthDay;
        public string YearMonthPattern => DateTimeFormat.YearMonth;
        public string RFC1123Pattern => DateTimeFormat.Rfc1123;
        public string SortableDateTimePattern => DateTimeFormat.Sortable;
        public string UniversalSortableDateTimePattern => DateTimeFormat.UniversalSortable;

        public string[] DayNames => Days(DateTimeFormat.DayName);
        public string[] AbbreviatedDayNames => Days(DateTimeFormat.AbbreviatedDayName);
        public string[] ShortestDayNames => new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
        public string[] MonthNames => Months(DateTimeFormat.MonthName);
        public string[] AbbreviatedMonthNames => Months(DateTimeFormat.AbbreviatedMonthName);
        public string[] MonthGenitiveNames => Months(DateTimeFormat.MonthName);
        public string[] AbbreviatedMonthGenitiveNames => Months(DateTimeFormat.AbbreviatedMonthName);

        public string GetDayName(DayOfWeek dayofweek) => DateTimeFormat.DayName(CheckDay(dayofweek));
        public string GetAbbreviatedDayName(DayOfWeek dayofweek) => DateTimeFormat.AbbreviatedDayName(CheckDay(dayofweek));
        public string GetShortestDayName(DayOfWeek dayOfWeek) => ShortestDayNames[CheckDay(dayOfWeek, "dayOfWeek")];
        public string GetMonthName(int month) => DateTimeFormat.MonthName(CheckMonth(month));
        public string GetAbbreviatedMonthName(int month) => DateTimeFormat.AbbreviatedMonthName(CheckMonth(month));

        public string GetEraName(int era)
        {
            if (era == 0 || era == 1)
                return "A.D.";
            throw new ArgumentOutOfRangeException("era", "Era value was not valid.");
        }

        public string GetAbbreviatedEraName(int era)
        {
            if (era == 0 || era == 1)
                return "AD";
            throw new ArgumentOutOfRangeException("era", "Era value was not valid.");
        }

        public int GetEra(string eraName)
        {
            if (eraName == null)
                throw new ArgumentNullException("eraName");
            return EqualsIgnoreCase(eraName, "A.D.") || EqualsIgnoreCase(eraName, "AD") ? 1 : -1;
        }

        // The patterns each standard format uses (the first is the one it
        // formats with).
        public string[] GetAllDateTimePatterns(char format) => DateTimeFormat.Patterns(format);

        public string[] GetAllDateTimePatterns()
        {
            const string Formats = "dDfFgGmMoOrRstTuUyY";
            int count = 0;
            for (int i = 0; i < Formats.Length; i++)
                count += DateTimeFormat.Patterns(Formats[i]).Length;
            string[] all = new string[count];
            int n = 0;
            for (int i = 0; i < Formats.Length; i++)
            {
                string[] some = DateTimeFormat.Patterns(Formats[i]);
                for (int j = 0; j < some.Length; j++)
                    all[n++] = some[j];
            }
            return all;
        }

        private static int CheckDay(DayOfWeek day, string paramName = "dayofweek")
        {
            if ((uint)day > (uint)DayOfWeek.Saturday)
                throw new ArgumentOutOfRangeException(paramName, "Valid values are between " + "0" + " and " + "6" + ", inclusive.");
            return (int)day;
        }

        private static int CheckMonth(int month)
        {
            if (month < 1 || month > 13)
                throw new ArgumentOutOfRangeException("month", "Valid values are between 1 and 13, inclusive.");
            return month;
        }

        private static string[] Days(Func<int, string> name)
        {
            string[] names = new string[7];
            for (int i = 0; i < 7; i++)
                names[i] = name(i);
            return names;
        }

        private static string[] Months(Func<int, string> name)
        {
            string[] names = new string[13];
            for (int i = 0; i < 13; i++)
                names[i] = name(i + 1);
            return names;
        }

        private static bool EqualsIgnoreCase(string a, string b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                char x = a[i], y = b[i];
                if (x >= 'a' && x <= 'z') x = (char)(x - 32);
                if (y >= 'a' && y <= 'z') y = (char)(y - 32);
                if (x != y)
                    return false;
            }
            return true;
        }
    }
}
