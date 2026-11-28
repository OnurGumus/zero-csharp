namespace System.Globalization
{
    // ISO 8601 week numbers: weeks start on Monday, and week 1 holds the
    // year's first Thursday. .NET's members, results and messages.
    public static class ISOWeek
    {
        private const int MinWeek = 1;
        private const int MaxWeek = 53;
        private const int MinYear = 1;
        private const int MaxYear = 9999;

        public static int GetWeekOfYear(DateTime date)
        {
            int week = GetWeekNumber(date);
            if (week < MinWeek)
                return GetWeeksInYear(date.Year - 1);
            if (week > GetWeeksInYear(date.Year))
                return MinWeek;
            return week;
        }

        public static int GetWeekOfYear(DateOnly date) => GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue));

        public static int GetYear(DateTime date)
        {
            int year = date.Year;
            int week = GetWeekNumber(date);
            if (week < MinWeek)
                return year - 1;
            if (week > GetWeeksInYear(year))
                return year + 1;
            return year;
        }

        public static int GetYear(DateOnly date) => GetYear(date.ToDateTime(TimeOnly.MinValue));

        public static DateTime GetYearStart(int year) => ToDateTime(year, MinWeek, DayOfWeek.Monday);

        public static DateTime GetYearEnd(int year) => ToDateTime(year, GetWeeksInYear(year), DayOfWeek.Sunday);

        public static int GetWeeksInYear(int year)
        {
            if (year < MinYear || year > MaxYear)
                throw new ArgumentOutOfRangeException("year", "Year must be between 1 and 9999.");
            if (P(year) == 4 || P(year - 1) == 3)
                return MaxWeek;
            return MaxWeek - 1;

            static int P(int y) => (y + y / 4 - y / 100 + y / 400) % 7;
        }

        public static DateTime ToDateTime(int year, int week, DayOfWeek dayOfWeek)
        {
            if (year < MinYear || year > MaxYear)
                throw new ArgumentOutOfRangeException("year", "Year must be between 1 and 9999.");
            if (week < MinWeek || week > MaxWeek)
                throw new ArgumentOutOfRangeException("week", "The week parameter must be in the range 1 through 53.");
            if ((uint)dayOfWeek > 7)
                throw new ArgumentOutOfRangeException("dayOfWeek", "The DayOfWeek enumeration must be in the range 0 through 6.");
            DateTime jan4 = new DateTime(year, 1, 4);
            int correction = GetWeekday(jan4.DayOfWeek) + 3;
            int ordinal = week * 7 + GetWeekday(dayOfWeek) - correction;
            return new DateTime(year, 1, 1).AddDays(ordinal - 1);
        }

        public static DateOnly ToDateOnly(int year, int week, DayOfWeek dayOfWeek)
            => DateOnly.FromDateTime(ToDateTime(year, week, dayOfWeek));

        private static int GetWeekNumber(DateTime date) => (date.DayOfYear - GetWeekday(date.DayOfWeek) + 10) / 7;

        private static int GetWeekday(DayOfWeek dayOfWeek) => dayOfWeek == DayOfWeek.Sunday ? 7 : (int)dayOfWeek;
    }
}
