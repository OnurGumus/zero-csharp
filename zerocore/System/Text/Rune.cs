using System.Buffers;
using System.Globalization;

namespace System.Text
{
    // A Unicode scalar value (a code point that isn't a surrogate), with
    // .NET's members for making, decoding, encoding, casing and classifying
    // one. Its answers come from the same tables as char's (Unicode 16.0).
    public readonly struct Rune : IComparable, IComparable<Rune>, IEquatable<Rune>
    {
        private readonly uint _value;

        private Rune(uint value, bool unchecked_) => _value = value;

        public Rune(char ch)
        {
            if (char.IsSurrogate(ch))
                throw new ArgumentOutOfRangeException("ch");
            _value = ch;
        }

        public Rune(char highSurrogate, char lowSurrogate)
        {
            if (!char.IsHighSurrogate(highSurrogate))
                throw new ArgumentOutOfRangeException("highSurrogate", "A valid high surrogate character is between 0xd800 and 0xdbff, inclusive.");
            if (!char.IsLowSurrogate(lowSurrogate))
                throw new ArgumentOutOfRangeException("lowSurrogate", "A valid low surrogate character is between 0xdc00 and 0xdfff, inclusive.");
            _value = (uint)char.ConvertToUtf32(highSurrogate, lowSurrogate);
        }

        public Rune(int value) : this((uint)value) { }

        public Rune(uint value)
        {
            if (!IsValid(value))
                throw new ArgumentOutOfRangeException("value");
            _value = value;
        }

        public static Rune ReplacementChar => new Rune(0xFFFD, false);

        public int Value => (int)_value;
        public bool IsAscii => _value <= 0x7F;
        public bool IsBmp => _value <= 0xFFFF;
        public int Plane => (int)(_value >> 16);
        public int Utf16SequenceLength => _value <= 0xFFFF ? 1 : 2;

        public static bool IsValid(int value) => IsValid((uint)value);
        public static bool IsValid(uint value) => value <= 0x10FFFF && (value < 0xD800 || value > 0xDFFF);

        public static bool TryCreate(char ch, out Rune result)
        {
            result = char.IsSurrogate(ch) ? default : new Rune(ch, false);
            return !char.IsSurrogate(ch);
        }

        public static bool TryCreate(char highSurrogate, char lowSurrogate, out Rune result)
        {
            bool ok = char.IsHighSurrogate(highSurrogate) && char.IsLowSurrogate(lowSurrogate);
            result = ok ? new Rune((uint)char.ConvertToUtf32(highSurrogate, lowSurrogate), false) : default;
            return ok;
        }

        public static bool TryCreate(int value, out Rune result) => TryCreate((uint)value, out result);

        public static bool TryCreate(uint value, out Rune result)
        {
            bool ok = IsValid(value);
            result = ok ? new Rune(value, false) : default;
            return ok;
        }

        public static explicit operator Rune(char ch) => new Rune(ch);
        public static explicit operator Rune(int value) => new Rune(value);
        public static explicit operator Rune(uint value) => new Rune(value);

        // ---- UTF-16 ----

        // The first scalar value: Done, or InvalidData with U+FFFD for a lone
        // surrogate, or NeedMoreData for a high surrogate at the end.
        public static OperationStatus DecodeFromUtf16(ReadOnlySpan<char> source, out Rune result, out int charsConsumed)
        {
            if (source.Length == 0)
            {
                result = ReplacementChar;
                charsConsumed = 0;
                return OperationStatus.NeedMoreData;
            }
            char c = source[0];
            if (!char.IsSurrogate(c))
            {
                result = new Rune(c, false);
                charsConsumed = 1;
                return OperationStatus.Done;
            }
            if (char.IsHighSurrogate(c))
            {
                if (source.Length == 1)
                {
                    result = ReplacementChar;
                    charsConsumed = 1;
                    return OperationStatus.NeedMoreData;
                }
                if (char.IsLowSurrogate(source[1]))
                {
                    result = new Rune((uint)char.ConvertToUtf32(c, source[1]), false);
                    charsConsumed = 2;
                    return OperationStatus.Done;
                }
            }
            result = ReplacementChar;
            charsConsumed = 1;
            return OperationStatus.InvalidData;
        }

        public static OperationStatus DecodeLastFromUtf16(ReadOnlySpan<char> source, out Rune result, out int charsConsumed)
        {
            if (source.Length == 0)
            {
                result = ReplacementChar;
                charsConsumed = 0;
                return OperationStatus.NeedMoreData;
            }
            char c = source[source.Length - 1];
            if (!char.IsSurrogate(c))
            {
                result = new Rune(c, false);
                charsConsumed = 1;
                return OperationStatus.Done;
            }
            if (char.IsLowSurrogate(c) && source.Length >= 2 && char.IsHighSurrogate(source[source.Length - 2]))
            {
                result = new Rune((uint)char.ConvertToUtf32(source[source.Length - 2], c), false);
                charsConsumed = 2;
                return OperationStatus.Done;
            }
            // A high surrogate at the end may yet get its low one.
            result = ReplacementChar;
            charsConsumed = 1;
            return char.IsHighSurrogate(c) ? OperationStatus.NeedMoreData : OperationStatus.InvalidData;
        }

        public int EncodeToUtf16(Span<char> destination)
        {
            if (!TryEncodeToUtf16(destination, out int written))
                throw new ArgumentException("Destination is too short.", "destination");
            return written;
        }

        public bool TryEncodeToUtf16(Span<char> destination, out int charsWritten)
        {
            if (_value <= 0xFFFF)
            {
                if (destination.Length >= 1)
                {
                    destination[0] = (char)_value;
                    charsWritten = 1;
                    return true;
                }
            }
            else if (destination.Length >= 2)
            {
                uint v = _value - 0x10000;
                destination[0] = (char)(0xD800 + (v >> 10));
                destination[1] = (char)(0xDC00 + (v & 0x3FF));
                charsWritten = 2;
                return true;
            }
            charsWritten = 0;
            return false;
        }

        public static Rune GetRuneAt(string input, int index)
        {
            if (input == null)
                throw new ArgumentNullException("input");
            if ((uint)index >= (uint)input.Length)
                throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection.");
            if (!TryGetRuneAt(input, index, out Rune value))
                throw new ArgumentException("Cannot extract a Unicode scalar value from the specified index in the input.", "index");
            return value;
        }

        public static bool TryGetRuneAt(string input, int index, out Rune value)
        {
            if (input == null)
                throw new ArgumentNullException("input");
            if ((uint)index >= (uint)input.Length)
                throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection.");
            return DecodeFromUtf16(Number.AsSpan(input).Slice(index), out value, out _) == OperationStatus.Done;
        }

        // ---- casing and classes ----

        public static Rune ToUpperInvariant(Rune value) => new Rune((uint)TextCasing.Change((int)value._value, true), false);
        public static Rune ToLowerInvariant(Rune value) => new Rune((uint)TextCasing.Change((int)value._value, false), false);

        public static Rune ToUpper(Rune value, CultureInfo culture)
        {
            if (culture == null)
                throw new ArgumentNullException("culture");
            return new Rune((uint)TextCasing.Change((int)value._value, true, culture.TextInfo._mode), false);
        }

        public static Rune ToLower(Rune value, CultureInfo culture)
        {
            if (culture == null)
                throw new ArgumentNullException("culture");
            return new Rune((uint)TextCasing.Change((int)value._value, false, culture.TextInfo._mode), false);
        }

        public static UnicodeCategory GetUnicodeCategory(Rune value) => CharUnicodeInfo.GetUnicodeCategory((int)value._value);
        public static double GetNumericValue(Rune value) => UnicodeData.GetNumericValue((int)value._value);

        public static bool IsControl(Rune value) => GetUnicodeCategory(value) == UnicodeCategory.Control;
        public static bool IsDigit(Rune value) => GetUnicodeCategory(value) == UnicodeCategory.DecimalDigitNumber;
        public static bool IsLetter(Rune value) => GetUnicodeCategory(value) <= UnicodeCategory.OtherLetter;
        public static bool IsLetterOrDigit(Rune value) => IsLetter(value) || IsDigit(value);
        public static bool IsLower(Rune value) => GetUnicodeCategory(value) == UnicodeCategory.LowercaseLetter;
        public static bool IsUpper(Rune value) => GetUnicodeCategory(value) == UnicodeCategory.UppercaseLetter;
        public static bool IsNumber(Rune value) => GetUnicodeCategory(value) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
        public static bool IsPunctuation(Rune value) => GetUnicodeCategory(value) is >= UnicodeCategory.ConnectorPunctuation and <= UnicodeCategory.OtherPunctuation;
        public static bool IsSeparator(Rune value) => GetUnicodeCategory(value) is >= UnicodeCategory.SpaceSeparator and <= UnicodeCategory.ParagraphSeparator;
        public static bool IsSymbol(Rune value) => GetUnicodeCategory(value) is >= UnicodeCategory.MathSymbol and <= UnicodeCategory.OtherSymbol;
        public static bool IsWhiteSpace(Rune value) => value._value <= 0xFFFF && char.IsWhiteSpace((char)value._value);

        // ---- comparing ----

        public static bool operator ==(Rune left, Rune right) => left._value == right._value;
        public static bool operator !=(Rune left, Rune right) => left._value != right._value;
        public static bool operator <(Rune left, Rune right) => left._value < right._value;
        public static bool operator <=(Rune left, Rune right) => left._value <= right._value;
        public static bool operator >(Rune left, Rune right) => left._value > right._value;
        public static bool operator >=(Rune left, Rune right) => left._value >= right._value;

        public int CompareTo(Rune other) => Value - other.Value;

        int IComparable.CompareTo(object obj)
        {
            if (obj == null)
                return 1;
            if (obj is not Rune other)
                throw new ArgumentException("Object must be of type Rune.");
            return CompareTo(other);
        }

        public bool Equals(Rune other) => _value == other._value;
        public override bool Equals(object obj) => obj is Rune other && _value == other._value;
        public override int GetHashCode() => Value;

        public override unsafe string ToString()
        {
            char* chars = stackalloc char[2];
            TryEncodeToUtf16(new Span<char>(chars, 2), out int written);
            return Number.FromChars(chars, written);
        }
    }
}
