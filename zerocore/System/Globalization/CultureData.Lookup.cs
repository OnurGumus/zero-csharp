namespace System.Globalization
{
    // Reading the culture table that scripts/gen-cultures writes into
    // CultureData.cs: a culture by name or number, and its fields as
    // strings, numbers and lists. Only programs that look a culture up by
    // name or number link the table (about 35 KB).
    internal static unsafe partial class CultureData
    {
        // The culture's row, or -1. Names are ASCII; case is ignored.
        internal static int Find(string name)
        {
            int low = 0, high = Count - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int order = CompareName(middle, name);
                if (order == 0)
                    return middle;
                if (order < 0)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            return -1;
        }

        internal static int FindLcid(int lcid)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Int(i, LCID) == lcid)
                    return i;
            }
            return -1;
        }

        // The table's names compared as .NET's OrdinalIgnoreCase sorts them:
        // by upper case.
        private static int CompareName(int culture, string name)
        {
            Span(culture, Name, out int start, out int length);
            ReadOnlySpan<byte> pool = Pool;
            int n = length < name.Length ? length : name.Length;
            for (int i = 0; i < n; i++)
            {
                int a = Upper(pool[start + i]), b = Upper(name[i]);
                if (a != b)
                    return a - b;
            }
            return length - name.Length;
        }

        private static int Upper(int c) => c >= 'a' && c <= 'z' ? c - 32 : c;

        private static void Span(int culture, int field, out int start, out int length)
        {
            ReadOnlySpan<byte> records = Records;
            int at = (culture * FieldCount + field) * 2;
            int index = records[at] | (records[at + 1] << 8);
            ReadOnlySpan<byte> strings = Strings;
            int entry = index * 5;
            start = strings[entry] | (strings[entry + 1] << 8) | (strings[entry + 2] << 16);
            length = strings[entry + 3] | (strings[entry + 4] << 8);
        }

        internal static string Text(int culture, int field)
        {
            Span(culture, field, out int start, out int length);
            return Decode(Pool.Slice(start, length));
        }

        internal static int Int(int culture, int field)
        {
            Span(culture, field, out int start, out int length);
            ReadOnlySpan<byte> pool = Pool;
            int value = 0;
            for (int i = 0; i < length; i++)
                value = value * 10 + (pool[start + i] - '0');
            return value;
        }

        internal static bool Flag(int culture, int field) => Int(culture, field) != 0;

        // A list joined by U+0001.
        internal static string[] List(int culture, int field)
        {
            Span(culture, field, out int start, out int length);
            ReadOnlySpan<byte> text = Pool.Slice(start, length);
            int count = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == 1)
                    count++;
            }
            string[] items = new string[count];
            int from = 0, n = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == 1)
                {
                    items[n++] = Decode(text.Slice(from, i - from));
                    from = i + 1;
                }
            }
            return items;
        }

        // Group sizes, written "3,2".
        internal static int[] Sizes(int culture, int field)
        {
            Span(culture, field, out int start, out int length);
            ReadOnlySpan<byte> text = Pool.Slice(start, length);
            if (length == 0)
                return new int[0];
            int count = 1;
            for (int i = 0; i < length; i++)
            {
                if (text[i] == ',')
                    count++;
            }
            int[] sizes = new int[count];
            int n = 0;
            for (int i = 0; i < length; i++)
            {
                if (text[i] == ',')
                    n++;
                else
                    sizes[n] = sizes[n] * 10 + (text[i] - '0');
            }
            return sizes;
        }

        // UTF-8 (valid: the generator wrote it) to a string. A fixed-size
        // stack buffer: a variable one needs __alloca, which DOS lacks.
        private static string Decode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > 256)
            {
                char[] heap = new char[bytes.Length];
                fixed (char* p = heap)
                    return Decode(bytes, p);
            }
            char* stack = stackalloc char[256];
            return Decode(bytes, stack);
        }

        private static string Decode(ReadOnlySpan<byte> bytes, char* chars)
        {
            int n = 0;
            for (int i = 0; i < bytes.Length; )
            {
                int b = bytes[i];
                int cp;
                if (b < 0x80)
                {
                    cp = b;
                    i++;
                }
                else if (b < 0xE0)
                {
                    cp = ((b & 0x1F) << 6) | (bytes[i + 1] & 0x3F);
                    i += 2;
                }
                else if (b < 0xF0)
                {
                    cp = ((b & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F);
                    i += 3;
                }
                else
                {
                    cp = ((b & 0x07) << 18) | ((bytes[i + 1] & 0x3F) << 12) | ((bytes[i + 2] & 0x3F) << 6) | (bytes[i + 3] & 0x3F);
                    i += 4;
                }
                if (cp >= 0x10000)
                {
                    cp -= 0x10000;
                    chars[n++] = (char)(0xD800 + (cp >> 10));
                    chars[n++] = (char)(0xDC00 + (cp & 0x3FF));
                }
                else
                {
                    chars[n++] = (char)cp;
                }
            }
            return Number.FromChars(chars, n);
        }

        // ---- the culture's formats, filled from its row ----

        internal static NumberFormatInfo NumberFormat(int culture, bool readOnly)
        {
            NumberFormatInfo n = new NumberFormatInfo();
            n._negativeSign = Text(culture, NegativeSign);
            n._positiveSign = Text(culture, PositiveSign);
            n._nanSymbol = Text(culture, NaNSymbol);
            n._positiveInfinitySymbol = Text(culture, PositiveInfinitySymbol);
            n._negativeInfinitySymbol = Text(culture, NegativeInfinitySymbol);
            n._numberDecimalSeparator = Text(culture, NumberDecimalSeparator);
            n._numberGroupSeparator = Text(culture, NumberGroupSeparator);
            n._numberDecimalDigits = Int(culture, NumberDecimalDigits);
            n._numberNegativePattern = Int(culture, NumberNegativePattern);
            n._numberGroupSizes = Sizes(culture, NumberGroupSizes);
            n._currencySymbol = Text(culture, CurrencySymbol);
            n._currencyDecimalSeparator = Text(culture, CurrencyDecimalSeparator);
            n._currencyGroupSeparator = Text(culture, CurrencyGroupSeparator);
            n._currencyDecimalDigits = Int(culture, CurrencyDecimalDigits);
            n._currencyPositivePattern = Int(culture, CurrencyPositivePattern);
            n._currencyNegativePattern = Int(culture, CurrencyNegativePattern);
            n._currencyGroupSizes = Sizes(culture, CurrencyGroupSizes);
            n._percentSymbol = Text(culture, PercentSymbol);
            n._perMilleSymbol = Text(culture, PerMilleSymbol);
            n._percentDecimalSeparator = Text(culture, PercentDecimalSeparator);
            n._percentGroupSeparator = Text(culture, PercentGroupSeparator);
            n._percentDecimalDigits = Int(culture, PercentDecimalDigits);
            n._percentPositivePattern = Int(culture, PercentPositivePattern);
            n._percentNegativePattern = Int(culture, PercentNegativePattern);
            n._percentGroupSizes = Sizes(culture, PercentGroupSizes);
            n.SetData((DigitShapes)Int(culture, DigitSubstitution), List(culture, NativeDigits), readOnly);
            return n;
        }

        internal static DateTimeFormatInfo DateTimeFormat(int culture, bool readOnly)
        {
            DateTimeFormatInfo d = new DateTimeFormatInfo();
            d._amDesignator = Text(culture, AMDesignator);
            d._pmDesignator = Text(culture, PMDesignator);
            d._dateSeparator = Text(culture, DateSeparator);
            d._timeSeparator = Text(culture, TimeSeparator);
            d._fullDateTimePattern = Text(culture, FullDateTimePattern);
            d._longDatePattern = Text(culture, LongDatePattern);
            d._longTimePattern = Text(culture, LongTimePattern);
            d._shortDatePattern = Text(culture, ShortDatePattern);
            d._shortTimePattern = Text(culture, ShortTimePattern);
            d._monthDayPattern = Text(culture, MonthDayPattern);
            d._yearMonthPattern = Text(culture, YearMonthPattern);
            d._dayNames = List(culture, DayNames);
            d._abbreviatedDayNames = List(culture, AbbreviatedDayNames);
            d._shortestDayNames = List(culture, ShortestDayNames);
            d._monthNames = List(culture, MonthNames);
            d._abbreviatedMonthNames = List(culture, AbbreviatedMonthNames);
            d._monthGenitiveNames = List(culture, MonthGenitiveNames);
            d._abbreviatedMonthGenitiveNames = List(culture, AbbreviatedMonthGenitiveNames);
            d.SetData((DayOfWeek)Int(culture, FirstDayOfWeek), (CalendarWeekRule)Int(culture, CalendarWeekRule),
                Text(culture, EraName), Text(culture, AbbreviatedEraName), Text(culture, NativeCalendarName),
                List(culture, ShortDatePatterns), List(culture, LongDatePatterns), List(culture, ShortTimePatterns),
                List(culture, LongTimePatterns), List(culture, YearMonthPatterns), readOnly);
            return d;
        }
    }
}
