namespace System
{
    // Numbers as text, for the primitives' ToString (and so for `"n=" + 5`,
    // string.Format and records). Each call allocates the string it returns.
    // A provider (or a current culture) with a NumberFormatInfo of your own
    // makes the text use its symbols (Number.Culture.cs).
    // Integers without a format take the short path here; everything else,
    // and the standard format strings, is in Number.Formatting.cs.
    internal static unsafe partial class Number
    {
        internal static string FormatInt64(long value)
        {
            // A current culture with symbols of its own takes the long way.
            if (s_culture != null)
            {
                string text = s_culture.CurrentText((ulong)value, true);
                if (text != null)
                    return text;
            }
            if (value >= 0)
                return FormatUInt64((ulong)value, negative: false);
            return FormatUInt64(unchecked(0 - (ulong)value), negative: true);
        }

        internal static string FormatUInt64(ulong value, bool negative = false)
        {
            if (!negative && s_culture != null)
            {
                string text = s_culture.CurrentText(value, false);
                if (text != null)
                    return text;
            }
            char* digits = stackalloc char[21];
            int start = 21;
            do
            {
                digits[--start] = (char)('0' + (int)(value % 10));
                value /= 10;
            }
            while (value != 0);
            if (negative)
                digits[--start] = '-';
            return FromChars(digits + start, 21 - start);
        }

        // ToString(format) and TryFormat for the numbers; the formatting itself
        // is in Number.Formatting.cs. A string is formatted on the stack first
        // and copied; one too long for that is formatted again into a string
        // of its exact length.
        private const int StackChars = 64;

        internal static string IntegerToString(ulong raw, bool signed, int bits, string format, IFormatProvider provider)
        {
            char letter = ParseFormat(AsSpan(format), out int precision);
            System.Globalization.NumberFormatInfo info = InfoFor(provider, letter, ref precision);
            char* chars = stackalloc char[StackChars];
            var sink = new Sink(chars, StackChars) { Info = info };
            FormatInteger(raw, signed, bits, letter, precision, ref sink);
            if (sink.Fits)
                return FromChars(chars, sink.Length);
            string result = string.Allocate(sink.Length);
            fixed (char* target = &result.FirstChar)
            {
                sink = new Sink(target, result.Length) { Info = info };
                FormatInteger(raw, signed, bits, letter, precision, ref sink);
            }
            return result;
        }

        internal static bool TryFormatInteger(ulong raw, bool signed, int bits, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
        {
            char letter = ParseFormat(format, out int precision);
            System.Globalization.NumberFormatInfo info = InfoFor(provider, letter, ref precision);
            fixed (char* chars = destination)
            {
                var sink = new Sink(chars, destination.Length) { Info = info };
                FormatInteger(raw, signed, bits, letter, precision, ref sink);
                return Done(ref sink, out charsWritten);
            }
        }

        // double, float and Half by their bits (see FormatIeee).
        internal static string IeeeToString(ulong bits, int fractionBits, int exponentBits, int roundTripDigits, string format, IFormatProvider provider)
        {
            char letter = ParseFormat(AsSpan(format), out int precision);
            System.Globalization.NumberFormatInfo info = InfoFor(provider, letter, ref precision);
            char* chars = stackalloc char[StackChars];
            var sink = new Sink(chars, StackChars) { Info = info };
            FormatIeee(bits, fractionBits, exponentBits, roundTripDigits, letter, precision, ref sink);
            if (sink.Fits)
                return FromChars(chars, sink.Length);
            string result = string.Allocate(sink.Length);
            fixed (char* target = &result.FirstChar)
            {
                sink = new Sink(target, result.Length) { Info = info };
                FormatIeee(bits, fractionBits, exponentBits, roundTripDigits, letter, precision, ref sink);
            }
            return result;
        }

        internal static bool TryFormatIeee(ulong bits, int fractionBits, int exponentBits, int roundTripDigits, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
        {
            char letter = ParseFormat(format, out int precision);
            System.Globalization.NumberFormatInfo info = InfoFor(provider, letter, ref precision);
            fixed (char* chars = destination)
            {
                var sink = new Sink(chars, destination.Length) { Info = info };
                FormatIeee(bits, fractionBits, exponentBits, roundTripDigits, letter, precision, ref sink);
                return Done(ref sink, out charsWritten);
            }
        }

        internal static string DecimalToString(ulong lo64, uint hi32, int scale, bool negative, string format, IFormatProvider provider)
        {
            char letter = ParseFormat(AsSpan(format), out int precision);
            System.Globalization.NumberFormatInfo info = InfoFor(provider, letter, ref precision);
            char* chars = stackalloc char[StackChars];
            var sink = new Sink(chars, StackChars) { Info = info };
            FormatDecimal(lo64, hi32, scale, negative, letter, precision, ref sink);
            if (sink.Fits)
                return FromChars(chars, sink.Length);
            string result = string.Allocate(sink.Length);
            fixed (char* target = &result.FirstChar)
            {
                sink = new Sink(target, result.Length) { Info = info };
                FormatDecimal(lo64, hi32, scale, negative, letter, precision, ref sink);
            }
            return result;
        }

        internal static bool TryFormatDecimal(ulong lo64, uint hi32, int scale, bool negative, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
        {
            char letter = ParseFormat(format, out int precision);
            System.Globalization.NumberFormatInfo info = InfoFor(provider, letter, ref precision);
            fixed (char* chars = destination)
            {
                var sink = new Sink(chars, destination.Length) { Info = info };
                FormatDecimal(lo64, hi32, scale, negative, letter, precision, ref sink);
                return Done(ref sink, out charsWritten);
            }
        }

        // TryFormat's result: false, and nothing written, if it didn't fit.
        private static bool Done(ref Sink sink, out int charsWritten)
        {
            charsWritten = sink.Fits ? sink.Length : 0;
            return sink.Fits;
        }

        internal static ReadOnlySpan<char> AsSpan(string text) =>
            text == null ? default : new ReadOnlySpan<char>(ref text.FirstChar, text.Length);

        internal static string FromChars(char* chars, int length)
        {
            string result = string.Allocate(length);
            ref char target = ref result.FirstChar;
            for (int i = 0; i < length; i++)
                System.Runtime.CompilerServices.Unsafe.Add(ref target, i) = chars[i];
            return result;
        }

        // FNV-1a over a value's bytes, for GetHashCode.
        internal static int HashBytes(byte* bytes, int count)
        {
            uint hash = 2166136261;
            for (int i = 0; i < count; i++)
                hash = (hash ^ bytes[i]) * 16777619;
            return (int)hash;
        }
    }
}
