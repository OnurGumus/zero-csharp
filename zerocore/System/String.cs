using System.Collections;
using System.Collections.Generic;
using System.Runtime;
using System.Runtime.CompilerServices;
using Internal.Runtime;

namespace System
{
    // A string object is [method table][length][characters][0]. Strings made
    // here (by + and Concat) are allocated like any other object and never freed.
    // Object has no Equals or GetHashCode to override here. A string is an
    // IEnumerable<char>, as in .NET (F# passes strings to Seq functions this
    // way); C#'s foreach over a string stays an indexed loop.
#pragma warning disable CS0660, CS0661
    public sealed unsafe class String : IEquatable<string>, IComparable<string>, IComparable, IEnumerable<char>
#pragma warning restore CS0660, CS0661
    {
        // The compiler finds these two fields by name, to lay out string literals.
        private readonly int _stringLength;
        private char _firstChar;

        public int Length => _stringLength;

        [IndexerName("Chars")]
        public char this[int index]
        {
            [Intrinsic]
            get
            {
                if ((uint)index >= (uint)_stringLength)
                    Environment.FailFast("String: index out of range");
                return Unsafe.Add(ref _firstChar, index);
            }
        }

        // For `fixed (char* p = text)`.
        public ref readonly char GetPinnableReference() => ref _firstChar;

        public static bool IsNullOrEmpty(string value) => value == null || value._stringLength == 0;

        public static bool Equals(string a, string b)
        {
            if ((object)a == (object)b)
                return true;
            if ((object)a == null || (object)b == null || a._stringLength != b._stringLength)
                return false;
            ref char x = ref a._firstChar;
            ref char y = ref b._firstChar;
            for (int i = 0; i < a._stringLength; i++)
            {
                if (Unsafe.Add(ref x, i) != Unsafe.Add(ref y, i))
                    return false;
            }
            return true;
        }

        public bool Equals(string other) => Equals(this, other);

        // By UTF-16 code unit, as string.CompareOrdinal does. .NET's CompareTo
        // uses the current culture's rules; zero mode has no cultures. So
        // "B" goes before "a" here ('B' is 66, 'a' is 97), not after it.
        public int CompareTo(string other) => CompareOrdinal(this, other);

        public int CompareTo(object obj) => obj is string other ? CompareOrdinal(this, other) : obj == null ? 1 : throw new ArgumentException("Object must be of type String.");

        // Null goes before every string, as in .NET.
        public static int CompareOrdinal(string a, string b)
        {
            if ((object)a == b)
                return 0;
            if (a is null)
                return -1;
            if (b is null)
                return 1;
            int length = a._stringLength < b._stringLength ? a._stringLength : b._stringLength;
            for (int i = 0; i < length; i++)
            {
                int difference = Unsafe.Add(ref a._firstChar, i) - Unsafe.Add(ref b._firstChar, i);
                if (difference != 0)
                    return difference;
            }
            return a._stringLength - b._stringLength;
        }

        public override bool Equals(object obj) => obj is string other && Equals(this, other);

        // FNV-1a over the characters: equal strings hash alike.
        public override int GetHashCode()
        {
            uint hash = 2166136261;
            ref char c = ref _firstChar;
            for (int i = 0; i < _stringLength; i++)
                hash = (hash ^ Unsafe.Add(ref c, i)) * 16777619;
            return (int)hash;
        }

        public override string ToString() => this;

        // string.Format as .NET has it: "{index[,alignment][:format]}" holes,
        // and "{{" and "}}" for braces. A number gets the format ("{0:F2}",
        // "{1,8:X4}"; see Number.Formatting.cs), anything else its ToString();
        // null is empty. $"{x:F2}" compiles to this. A malformed format string
        // throws FormatException.
        public static string Format(string format, params object[] args) => Format(null, format, args);

        public static string Format(IFormatProvider provider, string format, params object[] args)
        {
            if (format == null)
                throw new ArgumentNullException(nameof(format));
            return new System.Text.StringBuilder().AppendFormat(provider, format, args).ToString();
        }

        public static bool operator ==(string a, string b) => Equals(a, b);

        public static bool operator !=(string a, string b) => !Equals(a, b);

        // As .NET's: the string's characters, without a copy (null: an empty
        // span). C# converts a string to a span by itself; F# calls this, so
        // that a string can be passed where a ReadOnlySpan<char> is taken.
        public static implicit operator ReadOnlySpan<char>(string value)
            => value is null ? default : new ReadOnlySpan<char>(ref value._firstChar, value._stringLength);

        // A new string holding a copy of these characters; text[a..b] calls it.
        // text.AsSpan(start, length) gives the same characters without a copy.
        public string Substring(int startIndex) => Substring(startIndex, _stringLength - startIndex);

        public string Substring(int startIndex, int length)
        {
            if ((uint)startIndex > (uint)_stringLength || (uint)length > (uint)(_stringLength - startIndex))
                Environment.FailFast("string.Substring: out of range");
            if (length == _stringLength)
                return this;
            string result = Allocate(length);
            for (int i = 0; i < length; i++)
                Unsafe.Add(ref result._firstChar, i) = Unsafe.Add(ref _firstChar, startIndex + i);
            return result;
        }

        // Null counts as the empty string, as in .NET.
        public static string Concat(string a, string b)
        {
            string result = Allocate(LengthOf(a) + LengthOf(b));
            int at = result.Put(a, 0);
            result.Put(b, at);
            return result;
        }

        public static string Concat(string a, string b, string c)
        {
            string result = Allocate(LengthOf(a) + LengthOf(b) + LengthOf(c));
            int at = result.Put(a, 0);
            at = result.Put(b, at);
            result.Put(c, at);
            return result;
        }

        public static string Concat(string a, string b, string c, string d)
        {
            string result = Allocate(LengthOf(a) + LengthOf(b) + LengthOf(c) + LengthOf(d));
            int at = result.Put(a, 0);
            at = result.Put(b, at);
            at = result.Put(c, at);
            result.Put(d, at);
            return result;
        }

        public static string Concat(params string[] values)
        {
            int length = 0;
            for (int i = 0; i < values.Length; i++)
                length += LengthOf(values[i]);
            string result = Allocate(length);
            int at = 0;
            for (int i = 0; i < values.Length; i++)
                at = result.Put(values[i], at);
            return result;
        }

        private static int LengthOf(string value) => value == null ? 0 : value._stringLength;

        // Copies `value` in at `index` and returns the index after it.
        private int Put(string value, int index)
        {
            int length = LengthOf(value);
            for (int i = 0; i < length; i++)
                Unsafe.Add(ref _firstChar, index + i) = Unsafe.Add(ref value._firstChar, i);
            return index + length;
        }

        // A string of `length` characters, all zero, for the caller to fill in.
        internal static string Allocate(int length)
            => Unsafe.As<string>(RuntimeExports.RhpNewArray(MethodTable.Of<string>(), length));

        internal ref char FirstChar => ref _firstChar;

        // A string of the characters in `value` (.NET's `new string(span)`).
        internal static string FromChars(ReadOnlySpan<char> value)
        {
            string result = Allocate(value.Length);
            for (int i = 0; i < value.Length; i++)
                Unsafe.Add(ref result._firstChar, i) = value[i];
            return result;
        }

        // new string(...), as in .NET. The constructors have no body: the
        // compiler turns `new string(args)` into a call of the static Ctor with
        // the same parameters, which makes the string. Each is a new string on
        // the heap, never freed, as + and Concat make.
#pragma warning disable CS0824 // marked external: Ctor is its body
        [MethodImpl(MethodImplOptions.InternalCall)]
        public extern String(ReadOnlySpan<char> value);

        [MethodImpl(MethodImplOptions.InternalCall)]
        public extern String(char[] value);

        [MethodImpl(MethodImplOptions.InternalCall)]
        public extern String(char[] value, int startIndex, int length);

        [MethodImpl(MethodImplOptions.InternalCall)]
        public extern String(char c, int count);
#pragma warning restore CS0824

        private static string Ctor(ReadOnlySpan<char> value) => FromChars(value);

        // A null array makes "", as in .NET.
        private static string Ctor(char[] value) => FromChars(new ReadOnlySpan<char>(value));

        private static string Ctor(char[] value, int startIndex, int length)
        {
            if (value == null || (uint)startIndex > (uint)value.Length || (uint)length > (uint)(value.Length - startIndex))
                Environment.FailFast("new string: range out of bounds");
            return FromChars(new ReadOnlySpan<char>(value, startIndex, length));
        }

        private static string Ctor(char c, int count)
        {
            if (count < 0)
                Environment.FailFast("new string: count is negative");
            string result = Allocate(count);
            for (int i = 0; i < count; i++)
                Unsafe.Add(ref result._firstChar, i) = c;
            return result;
        }

        IEnumerator<char> IEnumerable<char>.GetEnumerator() => new StringEnumerator(this);

        IEnumerator IEnumerable.GetEnumerator() => new StringEnumerator(this);
    }

    internal sealed class StringEnumerator : IEnumerator<char>
    {
        private readonly string _text;
        private int _index = -1;

        public StringEnumerator(string text) => _text = text;

        public char Current => _text[_index];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_index < _text.Length)
                _index++;
            return _index < _text.Length;
        }

        public void Reset() => _index = -1;

        public void Dispose() { }
    }
}

namespace System.Runtime.CompilerServices
{
    public sealed class IndexerNameAttribute : Attribute
    {
        public IndexerNameAttribute(string indexerName) { }
    }
}
