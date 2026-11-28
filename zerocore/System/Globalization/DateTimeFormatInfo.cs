namespace System.Globalization
{
    // The names, patterns and separators dates are written with, as .NET's:
    // the invariant culture's (InvariantInfo, read-only), or your own. `new
    // DateTimeFormatInfo()` starts as a writable copy of the invariant one:
    // set ShortDatePattern to "dd.MM.yyyy" and "d" formats that way for
    // whatever is given it (or the current culture, once it has it). No
    // Calendar: the calendar is always the Gregorian one.
    public sealed class DateTimeFormatInfo : IFormatProvider
    {
        private static DateTimeFormatInfo s_invariant;

        private bool _isReadOnly;
        internal string _amDesignator = "AM", _pmDesignator = "PM", _dateSeparator = "/", _timeSeparator = ":";
        internal string _shortDatePattern = DateTimeFormat.ShortDate, _longDatePattern = DateTimeFormat.LongDate;
        internal string _shortTimePattern = DateTimeFormat.ShortTime, _longTimePattern = DateTimeFormat.LongTime;
        internal string _fullDateTimePattern = DateTimeFormat.FullDateTime, _monthDayPattern = DateTimeFormat.MonthDay;
        internal string _yearMonthPattern = DateTimeFormat.YearMonth;
        internal string[] _dayNames, _abbreviatedDayNames, _shortestDayNames;
        internal string[] _monthNames, _abbreviatedMonthNames, _monthGenitiveNames, _abbreviatedMonthGenitiveNames;
        private DayOfWeek _firstDayOfWeek = DayOfWeek.Sunday;
        private CalendarWeekRule _calendarWeekRule = CalendarWeekRule.FirstDay;
        internal string _eraName = "A.D.", _abbreviatedEraName = "AD", _nativeCalendarName = "Gregorian Calendar";
        // A culture's own lists of patterns (null: the invariant culture's).
        private string[] _shortDatePatterns, _longDatePatterns, _shortTimePatterns, _longTimePatterns, _yearMonthPatterns;

        // A culture's own values (CultureData), and whether they may change.
        internal void SetData(DayOfWeek firstDayOfWeek, CalendarWeekRule rule, string eraName, string abbreviatedEraName, string nativeCalendarName,
            string[] shortDates, string[] longDates, string[] shortTimes, string[] longTimes, string[] yearMonths, bool readOnly)
        {
            _firstDayOfWeek = firstDayOfWeek;
            _calendarWeekRule = rule;
            _eraName = eraName;
            _abbreviatedEraName = abbreviatedEraName;
            _nativeCalendarName = nativeCalendarName;
            _shortDatePatterns = shortDates;
            _longDatePatterns = longDates;
            _shortTimePatterns = shortTimes;
            _longTimePatterns = longTimes;
            _yearMonthPatterns = yearMonths;
            _isReadOnly = readOnly;
        }

        // The invariant one keeps no name arrays: the formatting code has its
        // names built in, and the getters make the arrays when asked.
        private DateTimeFormatInfo(bool invariant) => _isReadOnly = invariant;

        public DateTimeFormatInfo()
        {
            DateTimeFormat.UseCultures();
            _dayNames = InvariantDays(false);
            _abbreviatedDayNames = InvariantDays(true);
            _shortestDayNames = InvariantShortestDays();
            _monthNames = InvariantMonths(false);
            _abbreviatedMonthNames = InvariantMonths(true);
            _monthGenitiveNames = _monthNames;
            _abbreviatedMonthGenitiveNames = _abbreviatedMonthNames;
        }

        private static string[] InvariantDays(bool abbreviated)
        {
            string[] names = new string[7];
            for (int i = 0; i < 7; i++)
                names[i] = abbreviated ? DateTimeFormat.AbbreviatedDayName(i) : DateTimeFormat.DayName(i);
            return names;
        }

        private static string[] InvariantShortestDays() => new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };

        private static string[] InvariantMonths(bool abbreviated)
        {
            string[] names = new string[13];
            for (int i = 0; i < 13; i++)
                names[i] = abbreviated ? DateTimeFormat.AbbreviatedMonthName(i + 1) : DateTimeFormat.MonthName(i + 1);
            return names;
        }

        public static DateTimeFormatInfo InvariantInfo
        {
            get
            {
                if (s_invariant == null)
                    using (System.Runtime.ObjectAllocator.Lasting())
                        s_invariant = new DateTimeFormatInfo(true);
                return s_invariant;
            }
        }
        public static DateTimeFormatInfo CurrentInfo => CultureInfo.CurrentCulture.DateTimeFormat;

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

        public static DateTimeFormatInfo ReadOnly(DateTimeFormatInfo dtfi)
        {
            if (dtfi == null)
                throw new ArgumentNullException("dtfi");
            if (dtfi._isReadOnly)
                return dtfi;
            DateTimeFormatInfo copy = (DateTimeFormatInfo)dtfi.Clone();
            copy._isReadOnly = true;
            return copy;
        }

        // A writable copy.
        public object Clone()
        {
            DateTimeFormatInfo copy = new DateTimeFormatInfo();
            copy._amDesignator = _amDesignator;
            copy._pmDesignator = _pmDesignator;
            copy._dateSeparator = _dateSeparator;
            copy._timeSeparator = _timeSeparator;
            copy._shortDatePattern = _shortDatePattern;
            copy._longDatePattern = _longDatePattern;
            copy._shortTimePattern = _shortTimePattern;
            copy._longTimePattern = _longTimePattern;
            copy._fullDateTimePattern = _fullDateTimePattern;
            copy._monthDayPattern = _monthDayPattern;
            copy._yearMonthPattern = _yearMonthPattern;
            copy._dayNames = _dayNames;
            copy._abbreviatedDayNames = _abbreviatedDayNames;
            copy._shortestDayNames = _shortestDayNames;
            copy._monthNames = _monthNames;
            copy._abbreviatedMonthNames = _abbreviatedMonthNames;
            copy._monthGenitiveNames = _monthGenitiveNames;
            copy._abbreviatedMonthGenitiveNames = _abbreviatedMonthGenitiveNames;
            copy._firstDayOfWeek = _firstDayOfWeek;
            copy._calendarWeekRule = _calendarWeekRule;
            copy._eraName = _eraName;
            copy._abbreviatedEraName = _abbreviatedEraName;
            copy._nativeCalendarName = _nativeCalendarName;
            copy._shortDatePatterns = _shortDatePatterns;
            copy._longDatePatterns = _longDatePatterns;
            copy._shortTimePatterns = _shortTimePatterns;
            copy._longTimePatterns = _longTimePatterns;
            copy._yearMonthPatterns = _yearMonthPatterns;
            return copy;
        }

        public object GetFormat(Type formatType) => formatType == typeof(DateTimeFormatInfo) ? this : null;

        public bool IsReadOnly => _isReadOnly;

        private void VerifyWritable()
        {
            if (_isReadOnly)
                throw new InvalidOperationException("Instance is read-only.");
        }

        private string Text(string value)
        {
            VerifyWritable();
            return value ?? throw new ArgumentNullException("value");
        }

        // A copy of the names, which must be `length` long with none null.
        private string[] Names(string[] value, int length)
        {
            VerifyWritable();
            if (value == null)
                throw new ArgumentNullException("value");
            if (value.Length != length)
                throw new ArgumentException("Length of the array must be " + length.ToString() + ".", "value");
            string[] copy = new string[length];
            for (int i = 0; i < length; i++)
                copy[i] = value[i] ?? throw new ArgumentNullException("value", "Found a null value within an array.");
            return copy;
        }

        private static string[] Copy(string[] names)
        {
            string[] copy = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
                copy[i] = names[i];
            return copy;
        }

        public string AMDesignator { get => _amDesignator; set => _amDesignator = Text(value); }
        public string PMDesignator { get => _pmDesignator; set => _pmDesignator = Text(value); }
        public string DateSeparator { get => _dateSeparator; set => _dateSeparator = Text(value); }
        public string TimeSeparator { get => _timeSeparator; set => _timeSeparator = Text(value); }
        public string ShortDatePattern { get => _shortDatePattern; set => _shortDatePattern = Text(value); }
        // Setting the long date or time pattern resets the full one to the two
        // joined, as in .NET.
        public string LongDatePattern
        {
            get => _longDatePattern;
            set
            {
                _longDatePattern = Text(value);
                _fullDateTimePattern = null;
            }
        }
        public string ShortTimePattern { get => _shortTimePattern; set => _shortTimePattern = Text(value); }
        public string LongTimePattern
        {
            get => _longTimePattern;
            set
            {
                _longTimePattern = Text(value);
                _fullDateTimePattern = null;
            }
        }

        public string FullDateTimePattern { get => FullPattern; set => _fullDateTimePattern = Text(value); }

        // Kept by this object, which may be a cached culture's for good.
        internal string FullPattern
        {
            get
            {
                if (_fullDateTimePattern == null)
                    using (System.Runtime.ObjectAllocator.Lasting())
                        _fullDateTimePattern = _longDatePattern + " " + _longTimePattern;
                return _fullDateTimePattern;
            }
        }

        // A DateTimeOffset's text without a format: the short date and long
        // time, and " zzz" unless the time pattern shows the offset already.
        internal string OffsetPattern
        {
            get
            {
                string time = _longTimePattern;
                bool foundZ = false, inQuote = false;
                char quote = '\'';
                for (int i = 0; !foundZ && i < time.Length; i++)
                {
                    switch (time[i])
                    {
                        case 'z':
                            foundZ = !inQuote;
                            break;
                        case '\'':
                        case '"':
                            if (inQuote && quote == time[i])
                                inQuote = false;
                            else if (!inQuote)
                            {
                                quote = time[i];
                                inQuote = true;
                            }
                            break;
                        case '%':
                        case '\\':
                            i++;
                            break;
                    }
                }
                return foundZ ? _shortDatePattern + " " + time : _shortDatePattern + " " + time + " zzz";
            }
        }
        public string MonthDayPattern { get => _monthDayPattern; set => _monthDayPattern = Text(value); }
        public string YearMonthPattern { get => _yearMonthPattern; set => _yearMonthPattern = Text(value); }

        public DayOfWeek FirstDayOfWeek
        {
            get => _firstDayOfWeek;
            set
            {
                VerifyWritable();
                if ((uint)value > (uint)DayOfWeek.Saturday)
                    throw new ArgumentOutOfRangeException("value", value, "Valid values are between 0 and 6, inclusive.");
                _firstDayOfWeek = value;
            }
        }

        public CalendarWeekRule CalendarWeekRule
        {
            get => _calendarWeekRule;
            set
            {
                VerifyWritable();
                if ((uint)value > (uint)CalendarWeekRule.FirstFourDayWeek)
                    throw new ArgumentOutOfRangeException("value", value, "Valid values are between 0 and 2, inclusive.");
                _calendarWeekRule = value;
            }
        }

        public string NativeCalendarName => _nativeCalendarName;
        public string RFC1123Pattern => DateTimeFormat.Rfc1123;
        public string SortableDateTimePattern => DateTimeFormat.Sortable;
        public string UniversalSortableDateTimePattern => DateTimeFormat.UniversalSortable;

        public string[] DayNames { get => Copy(_dayNames ?? InvariantDays(false)); set => _dayNames = Names(value, 7); }
        public string[] AbbreviatedDayNames { get => Copy(_abbreviatedDayNames ?? InvariantDays(true)); set => _abbreviatedDayNames = Names(value, 7); }
        public string[] ShortestDayNames { get => Copy(_shortestDayNames ?? InvariantShortestDays()); set => _shortestDayNames = Names(value, 7); }
        public string[] MonthNames { get => Copy(_monthNames ?? InvariantMonths(false)); set => _monthNames = Names(value, 13); }
        public string[] AbbreviatedMonthNames { get => Copy(_abbreviatedMonthNames ?? InvariantMonths(true)); set => _abbreviatedMonthNames = Names(value, 13); }
        public string[] MonthGenitiveNames { get => Copy(_monthGenitiveNames ?? InvariantMonths(false)); set => _monthGenitiveNames = Names(value, 13); }
        public string[] AbbreviatedMonthGenitiveNames { get => Copy(_abbreviatedMonthGenitiveNames ?? InvariantMonths(true)); set => _abbreviatedMonthGenitiveNames = Names(value, 13); }

        public string GetDayName(DayOfWeek dayofweek)
            => _dayNames == null ? DateTimeFormat.DayName(CheckDay(dayofweek, "dayofweek")) : _dayNames[CheckDay(dayofweek, "dayofweek")];
        public string GetAbbreviatedDayName(DayOfWeek dayofweek)
            => _abbreviatedDayNames == null ? DateTimeFormat.AbbreviatedDayName(CheckDay(dayofweek, "dayofweek")) : _abbreviatedDayNames[CheckDay(dayofweek, "dayofweek")];
        public string GetShortestDayName(DayOfWeek dayOfWeek) => (_shortestDayNames ?? InvariantShortestDays())[CheckDay(dayOfWeek, "dayOfWeek")];
        public string GetMonthName(int month)
            => _monthNames == null ? DateTimeFormat.MonthName(CheckMonth(month)) : _monthNames[CheckMonth(month) - 1];
        public string GetAbbreviatedMonthName(int month)
            => _abbreviatedMonthNames == null ? DateTimeFormat.AbbreviatedMonthName(CheckMonth(month)) : _abbreviatedMonthNames[CheckMonth(month) - 1];

        public string GetEraName(int era)
        {
            if (era == 0 || era == 1)
                return _eraName;
            throw new ArgumentOutOfRangeException("era", "Era value was not valid.");
        }

        public string GetAbbreviatedEraName(int era)
        {
            if (era == 0 || era == 1)
                return _abbreviatedEraName;
            throw new ArgumentOutOfRangeException("era", "Era value was not valid.");
        }

        public int GetEra(string eraName)
        {
            if (eraName == null)
                throw new ArgumentNullException("eraName");
            return TextCasing.EqualsIgnoreCase(Number.AsSpan(eraName), Number.AsSpan(_eraName))
                || TextCasing.EqualsIgnoreCase(Number.AsSpan(eraName), Number.AsSpan(_abbreviatedEraName)) ? 1 : -1;
        }

        // The patterns each standard format may use, the one it formats with
        // first, as .NET lists them: a pattern you set goes first, ahead of
        // the invariant culture's others.
        public string[] GetAllDateTimePatterns(char format)
        {
            switch (format)
            {
                case 'd': return ShortDates();
                case 'D': return LongDates();
                case 't': return ShortTimes();
                case 'T': return LongTimes();
                case 'y': case 'Y': return Merged(_yearMonthPattern, _yearMonthPatterns ?? new[] { DateTimeFormat.YearMonth });
                case 'f': return Combined(LongDates(), ShortTimes());
                case 'F': case 'U': return Combined(LongDates(), LongTimes());
                case 'g': return Combined(ShortDates(), ShortTimes());
                case 'G': return Combined(ShortDates(), LongTimes());
                case 'm': case 'M': return new[] { _monthDayPattern };
                case 'o': case 'O': return new[] { DateTimeFormat.RoundTripPattern };
                case 'r': case 'R': return new[] { DateTimeFormat.Rfc1123 };
                case 's': return new[] { DateTimeFormat.Sortable };
                case 'u': return new[] { DateTimeFormat.UniversalSortable };
                default: throw new ArgumentException("Format specifier was invalid.", "format");
            }
        }

        public string[] GetAllDateTimePatterns()
        {
            const string Formats = "dDfFgGmMoOrRstTuUyY";
            int count = 0;
            for (int i = 0; i < Formats.Length; i++)
                count += GetAllDateTimePatterns(Formats[i]).Length;
            string[] all = new string[count];
            int n = 0;
            for (int i = 0; i < Formats.Length; i++)
            {
                string[] some = GetAllDateTimePatterns(Formats[i]);
                for (int j = 0; j < some.Length; j++)
                    all[n++] = some[j];
            }
            return all;
        }

        private string[] ShortDates() => Merged(_shortDatePattern, _shortDatePatterns ?? new[] { DateTimeFormat.ShortDate, "yyyy-MM-dd" });
        private string[] LongDates() => Merged(_longDatePattern, _longDatePatterns ?? new[] { DateTimeFormat.LongDate });
        private string[] ShortTimes() => Merged(_shortTimePattern, _shortTimePatterns ?? new[] { DateTimeFormat.ShortTime, "hh:mm tt", "H:mm", "h:mm tt" });
        private string[] LongTimes() => Merged(_longTimePattern, _longTimePatterns ?? new[] { DateTimeFormat.LongTime });

        // The current pattern first, as .NET merges them: found among the
        // defaults, it swaps places with the first; else it goes in front.
        private static string[] Merged(string current, params string[] defaults)
        {
            int at = 0;
            while (at < defaults.Length && defaults[at] != current)
                at++;
            if (at < defaults.Length)
            {
                string[] swapped = new string[defaults.Length];
                for (int i = 0; i < defaults.Length; i++)
                    swapped[i] = defaults[i];
                swapped[at] = swapped[0];
                swapped[0] = current;
                return swapped;
            }
            string[] result = new string[defaults.Length + 1];
            result[0] = current;
            for (int i = 0; i < defaults.Length; i++)
                result[i + 1] = defaults[i];
            return result;
        }

        private static string[] Combined(string[] dates, string[] times)
        {
            string[] result = new string[dates.Length * times.Length];
            int n = 0;
            for (int i = 0; i < dates.Length; i++)
            {
                for (int j = 0; j < times.Length; j++)
                    result[n++] = dates[i] + " " + times[j];
            }
            return result;
        }

        private static int CheckDay(DayOfWeek day, string paramName)
        {
            if ((uint)day > (uint)DayOfWeek.Saturday)
                throw new ArgumentOutOfRangeException(paramName, day, "Valid values are between 0 and 6, inclusive.");
            return (int)day;
        }

        private static int CheckMonth(int month)
        {
            if (month < 1 || month > 13)
                throw new ArgumentOutOfRangeException("month", month, "Valid values are between 1 and 13, inclusive.");
            return month;
        }
    }
}
