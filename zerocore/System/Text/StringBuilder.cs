namespace System.Text
{
    // .NET's StringBuilder: Append, AppendFormat, Insert, Remove, Replace, the
    // indexer, Length and ToString. Records' ToString and string.Format use it
    // too. It grows by doubling, with new, so it allocates and never frees;
    // lib's StrBuilder builds strings in an arena instead.
    public sealed unsafe class StringBuilder
    {
        private char[] _chars = new char[16];
        private int _length;

        private const string IndexOutOfRange = "Index was out of range. Must be non-negative and less than or equal to the size of the collection.";

        // Shorter: cut. Longer: padded with '\0', as in .NET.
        public int Length
        {
            get => _length;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                Grow(value);
                new Span<char>(_chars, _length, Math.Max(value - _length, 0)).Clear();
                _length = value;
            }
        }

        public int Capacity => _chars.Length;

        public int EnsureCapacity(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);
            Grow(capacity);
            return _chars.Length;
        }

        // Without EnsureCapacity's check, whose message would bring in number
        // formatting: Append is all a C64 program's string.Format needs.
        private void Grow(int capacity)
        {
            if (capacity > _chars.Length)
            {
                char[] bigger = new char[Math.Max(capacity, _chars.Length * 2)];
                for (int i = 0; i < _length; i++)
                    bigger[i] = _chars[i];
                _chars = bigger;
            }
        }

        public StringBuilder Clear()
        {
            _length = 0;
            return this;
        }

        [System.Runtime.CompilerServices.IndexerName("Chars")]
        public char this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException();
                return _chars[index];
            }
            set
            {
                if ((uint)index >= (uint)_length)
                    throw new ArgumentOutOfRangeException(nameof(index), "Index was out of range. Must be non-negative and less than the size of the collection.");
                _chars[index] = value;
            }
        }

        public StringBuilder() { }

        public StringBuilder(int capacity) => _chars = new char[capacity < 16 ? 16 : capacity];

        public StringBuilder(string value) => Append(value);

        // Environment.NewLine, which is "\n" on every target.
        public StringBuilder AppendLine() => Append('\n');

        public StringBuilder AppendLine(string value) => Append(value).Append('\n');

        public StringBuilder Append(char value)
        {
            if (_length == _chars.Length)
                Grow(_length + 1);
            _chars[_length++] = value;
            return this;
        }

        public StringBuilder Append(string value)
        {
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                    Append(value[i]);
            }
            return this;
        }

        // count chars of value from startIndex, with .NET's checks.
        public StringBuilder Append(string value, int startIndex, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (value == null)
            {
                if (startIndex == 0 && count == 0)
                    return this;
                throw new ArgumentNullException("value");
            }
            if (count > value.Length - startIndex)
                throw new ArgumentOutOfRangeException("count", "Index and count must refer to a location within the string.");
            for (int i = 0; i < count; i++)
                Append(value[startIndex + i]);
            return this;
        }

        public StringBuilder Append(char value, int repeatCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(repeatCount);
            return AppendRepeated(value, repeatCount);
        }

        private StringBuilder AppendRepeated(char value, int repeatCount)
        {
            for (int i = 0; i < repeatCount; i++)
                Append(value);
            return this;
        }

        public StringBuilder Append(ReadOnlySpan<char> value)
        {
            for (int i = 0; i < value.Length; i++)
                Append(value[i]);
            return this;
        }

        public StringBuilder Insert(int index, ReadOnlySpan<char> value)
        {
            if ((uint)index > (uint)_length)
                throw new ArgumentOutOfRangeException(nameof(index), IndexOutOfRange);
            Grow(_length + value.Length);
            new ReadOnlySpan<char>(_chars, index, _length - index).CopyTo(new Span<char>(_chars, index + value.Length, _length - index));
            value.CopyTo(new Span<char>(_chars, index, value.Length));
            _length += value.Length;
            return this;
        }

        public StringBuilder Insert(int index, string value) => Insert(index, Number.AsSpan(value));

        public StringBuilder Insert(int index, string value, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if ((uint)index > (uint)_length)
                throw new ArgumentOutOfRangeException(nameof(index), IndexOutOfRange);
            for (int i = 0; i < count; i++)
                Insert(index, value);
            return this;
        }

        public StringBuilder Insert(int index, char value)
        {
            ReadOnlySpan<char> one = new ReadOnlySpan<char>(ref value, 1);
            return Insert(index, one);
        }

        public StringBuilder Insert(int index, int value) => Insert(index, Number.FormatInt64(value));

        public StringBuilder Insert(int index, long value) => Insert(index, Number.FormatInt64(value));

        public StringBuilder Insert(int index, bool value) => Insert(index, value ? "True" : "False");

        public StringBuilder Insert(int index, double value) => Insert(index, value.ToString());

        public StringBuilder Insert(int index, object value) => value == null ? Insert(index, default(ReadOnlySpan<char>)) : Insert(index, value.ToString());

        public StringBuilder Remove(int startIndex, int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (length > _length - startIndex)
                throw new ArgumentOutOfRangeException(nameof(length), IndexOutOfRange);
            int tail = _length - startIndex - length;
            new ReadOnlySpan<char>(_chars, startIndex + length, tail).CopyTo(new Span<char>(_chars, startIndex, tail));
            _length -= length;
            return this;
        }

        public StringBuilder Replace(char oldChar, char newChar) => Replace(oldChar, newChar, 0, _length);

        public StringBuilder Replace(char oldChar, char newChar, int startIndex, int count)
        {
            CheckRange(startIndex, count);
            for (int i = startIndex; i < startIndex + count; i++)
                if (_chars[i] == oldChar)
                    _chars[i] = newChar;
            return this;
        }

        public StringBuilder Replace(string oldValue, string newValue) => Replace(oldValue, newValue, 0, _length);

        // Every oldValue that lies wholly within the range, left to right.
        public StringBuilder Replace(string oldValue, string newValue, int startIndex, int count)
        {
            if (oldValue == null)
                throw new ArgumentNullException(nameof(oldValue));
            if (oldValue.Length == 0)
                throw new ArgumentException("The value cannot be an empty string.", nameof(oldValue));
            CheckRange(startIndex, count);
            ReadOnlySpan<char> find = Number.AsSpan(oldValue);
            ReadOnlySpan<char> put = Number.AsSpan(newValue);
            int end = startIndex + count;

            int matches = 0;
            for (int i = startIndex; i <= end - find.Length; )
            {
                if (Matches(_chars, i, find))
                {
                    matches++;
                    i += find.Length;
                }
                else
                {
                    i++;
                }
            }
            if (matches == 0)
                return this;

            // The text moves to the end of the buffer first when it gets
            // longer; the result, written from the start, never catches up
            // with what is still to be read.
            int length = _length;
            int newLength = checked(length + matches * (put.Length - find.Length));
            int shift = Math.Max(newLength - length, 0);
            Grow(newLength);
            char[] chars = _chars;
            if (shift > 0)
                new ReadOnlySpan<char>(chars, 0, length).CopyTo(new Span<char>(chars, shift, length));

            int read = 0;
            int written = 0;
            int scan = startIndex;
            for (int found = 0; found < matches; )
            {
                if (!Matches(chars, shift + scan, find))
                {
                    scan++;
                    continue;
                }
                new ReadOnlySpan<char>(chars, shift + read, scan - read).CopyTo(new Span<char>(chars, written, scan - read));
                written += scan - read;
                put.CopyTo(new Span<char>(chars, written, put.Length));
                written += put.Length;
                read = scan = scan + find.Length;
                found++;
            }
            new ReadOnlySpan<char>(chars, shift + read, length - read).CopyTo(new Span<char>(chars, written, length - read));
            _length = newLength;
            return this;
        }

        private static bool Matches(char[] chars, int at, ReadOnlySpan<char> find)
        {
            for (int i = 0; i < find.Length; i++)
                if (chars[at + i] != find[i])
                    return false;
            return true;
        }

        private void CheckRange(int startIndex, int count)
        {
            if ((uint)startIndex > (uint)_length)
                throw new ArgumentOutOfRangeException(nameof(startIndex), IndexOutOfRange);
            if (count < 0 || startIndex > _length - count)
                throw new ArgumentOutOfRangeException(nameof(count), IndexOutOfRange);
        }

        public StringBuilder Append(object value) => value == null ? this : Append(value.ToString());

        public StringBuilder Append(int value) => Append(Number.FormatInt64(value));

        public StringBuilder Append(long value) => Append(Number.FormatInt64(value));

        public StringBuilder Append(bool value) => Append(value ? "True" : "False");

        public StringBuilder Append(double value) => Append(value.ToString());

        public StringBuilder AppendFormat(string format, params object[] args) => AppendFormat(null, format, args);

        // string.Format's work: see String.cs.
        public StringBuilder AppendFormat(IFormatProvider provider, string format, params object[] args)
        {
            int i = 0;
            int n = format.Length;
            while (i < n)
            {
                char c = format[i++];
                if (c == '}')
                {
                    if (i < n && format[i] == '}')
                    {
                        i++;
                        Append('}');
                        continue;
                    }
                    throw BadFormat();
                }
                if (c != '{')
                {
                    Append(c);
                    continue;
                }
                if (i < n && format[i] == '{')
                {
                    i++;
                    Append('{');
                    continue;
                }

                // {index
                int index = ReadNumber(format, ref i);
                SkipSpaces(format, ref i);
                // ,alignment: right-aligned, or left with a minus
                int width = 0;
                bool left = false;
                if (i < n && format[i] == ',')
                {
                    i++;
                    SkipSpaces(format, ref i);
                    if (i < n && format[i] == '-')
                    {
                        left = true;
                        i++;
                    }
                    width = ReadNumber(format, ref i);
                    SkipSpaces(format, ref i);
                }
                // :format, up to the closing brace
                int formatStart = i;
                int formatLength = 0;
                if (i < n && format[i] == ':')
                {
                    formatStart = ++i;
                    while (i < n && format[i] != '}')
                    {
                        if (format[i] == '{')
                            throw BadFormat();
                        i++;
                    }
                    formatLength = i - formatStart;
                }
                if (i >= n || format[i] != '}')
                    throw BadFormat();
                i++;
                if (args == null || index >= args.Length)
                    throw new FormatException("Index (zero based) must be greater than or equal to zero and less than the size of the argument list.");
                AppendArgument(provider, args[index], format, formatStart, formatLength, width, left);
            }
            return this;
        }

        private void AppendArgument(IFormatProvider provider, object arg, string format, int formatStart, int formatLength, int width, bool left)
        {
            ReadOnlySpan<char> text = default;
            char* chars = stackalloc char[64];
            if (arg is ISpanFormattable spanFormattable)
            {
                ReadOnlySpan<char> itemFormat = formatLength == 0 ? default
                    : new ReadOnlySpan<char>(ref System.Runtime.CompilerServices.Unsafe.Add(ref format.FirstChar, formatStart), formatLength);
                if (spanFormattable.TryFormat(new Span<char>(chars, 64), out int written, itemFormat, provider))
                    text = new ReadOnlySpan<char>(chars, written);
                else
                    text = Number.AsSpan(spanFormattable.ToString(format.Substring(formatStart, formatLength), provider));
            }
            else if (arg is IFormattable formattable)
            {
                text = Number.AsSpan(formattable.ToString(formatLength == 0 ? null : format.Substring(formatStart, formatLength), provider));
            }
            else if (arg != null)
            {
                text = Number.AsSpan(arg.ToString());
            }
            int padding = width - text.Length;
            if (!left && padding > 0)
                AppendRepeated(' ', padding);
            Append(text);
            if (left && padding > 0)
                AppendRepeated(' ', padding);
        }

        private static int ReadNumber(string format, ref int i)
        {
            if (i >= format.Length || format[i] < '0' || format[i] > '9')
                throw BadFormat();
            int value = 0;
            do
            {
                value = value * 10 + format[i++] - '0';
                if (value >= 1_000_000)
                    throw BadFormat();
            }
            while (i < format.Length && format[i] >= '0' && format[i] <= '9');
            return value;
        }

        private static void SkipSpaces(string format, ref int i)
        {
            while (i < format.Length && format[i] == ' ')
                i++;
        }

        private static FormatException BadFormat() => new FormatException("Input string was not in a correct format.");

        // Kept apart from ToString(start, length), whose checks a C64 program's
        // string.Format would pay for.
        public override string ToString()
        {
            string result = string.Allocate(_length);
            ref char target = ref result.FirstChar;
            for (int i = 0; i < _length; i++)
                System.Runtime.CompilerServices.Unsafe.Add(ref target, i) = _chars[i];
            return result;
        }

        public string ToString(int startIndex, int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (startIndex > _length - length)
                throw new ArgumentOutOfRangeException(nameof(length), "Index and length must refer to a location within the string.");
            string result = string.Allocate(length);
            new ReadOnlySpan<char>(_chars, startIndex, length).CopyTo(new Span<char>(ref result.FirstChar, length));
            return result;
        }
    }
}
