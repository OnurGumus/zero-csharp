namespace System.Text
{
    // The small StringBuilder that records' ToString and string.Format use:
    // Append, AppendFormat and ToString. It grows by doubling, with new, so it
    // allocates; lib's StrBuilder builds strings in an arena instead.
    public sealed unsafe class StringBuilder
    {
        private char[] _chars = new char[16];
        private int _length;

        public int Length => _length;

        public StringBuilder() { }

        public StringBuilder(int capacity) => _chars = new char[capacity < 16 ? 16 : capacity];

        public StringBuilder(string value) => Append(value);

        // Environment.NewLine, which is "\n" on every target.
        public StringBuilder AppendLine() => Append('\n');

        public StringBuilder AppendLine(string value) => Append(value).Append('\n');

        public StringBuilder Append(char value)
        {
            if (_length == _chars.Length)
            {
                char[] bigger = new char[_chars.Length * 2];
                for (int i = 0; i < _length; i++)
                    bigger[i] = _chars[i];
                _chars = bigger;
            }
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

        public StringBuilder Append(char value, int repeatCount)
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
                Append(' ', padding);
            Append(text);
            if (left && padding > 0)
                Append(' ', padding);
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

        public override string ToString()
        {
            string result = string.Allocate(_length);
            ref char target = ref result.FirstChar;
            for (int i = 0; i < _length; i++)
                System.Runtime.CompilerServices.Unsafe.Add(ref target, i) = _chars[i];
            return result;
        }
    }
}
