using System.Runtime.CompilerServices;

namespace System.Text
{
    // Text to bytes and back: Encoding.UTF8, ASCII, Latin1, Unicode (UTF-16
    // little-endian), BigEndianUnicode and UTF32, with .NET's members and
    // results, invalid input included. What doesn't fit becomes a
    // replacement, as with .NET's default fallbacks: a lone surrogate becomes
    // U+FFFD (EF BF BD in UTF-8); bytes that aren't valid become U+FFFD, one
    // for each maximal invalid sequence, as .NET does; ASCII writes and reads
    // '?' for anything above 0x7F; Latin1 writes a similar Latin-1 character
    // where .NET has one (Ā as A) and '?' otherwise. An encoding made with
    // throwOnInvalidBytes: true ends the program instead.
    //
    // The span and pointer overloads allocate nothing. Those returning a
    // byte[], char[] or string allocate it, and in zero mode it is never
    // freed: in a loop, encode into a span (stackalloc or an arena's).
    //
    // A destination too small ends the program, as .NET throws; TryGetBytes
    // and TryGetChars return false instead.
    //
    // There are no Encoder, Decoder or fallback objects, and no code pages
    // besides these. Classes outside zerocore can't derive from Encoding.
    public abstract unsafe class Encoding
    {
        // Each encoding is made by a static constructor of its own, on first
        // use: what a static constructor makes is allocated outside any object
        // scope a program has open then (lib/Arena.cs), where a cache filled
        // inside one would be freed with it. One class each keeps the others
        // out of programs that don't use them.
        private static class Utf8Bom { internal static readonly Encoding Value = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true); }
        private static class Utf8NoBom { internal static readonly Encoding Value = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
        private static class Ascii { internal static readonly Encoding Value = new ASCIIEncoding(); }
        private static class Latin1One { internal static readonly Encoding Value = new Latin1Encoding(); }
        private static class Utf16 { internal static readonly Encoding Value = new UnicodeEncoding(bigEndian: false, byteOrderMark: true); }
        private static class Utf16BigEndian { internal static readonly Encoding Value = new UnicodeEncoding(bigEndian: true, byteOrderMark: true); }
        private static class Utf32 { internal static readonly Encoding Value = new UTF32Encoding(bigEndian: false, byteOrderMark: true); }
        private static class Utf32BigEndian { internal static readonly Encoding Value = new UTF32Encoding(bigEndian: true, byteOrderMark: true); }

        private protected readonly bool _throwOnInvalid;

        private protected Encoding(bool throwOnInvalid) => _throwOnInvalid = throwOnInvalid;

        // UTF-8 that writes a byte order mark where a preamble is asked for
        // (GetPreamble); GetBytes never writes one.
        public static Encoding UTF8 => Utf8Bom.Value;

        // UTF-8 without a byte order mark, as .NET (Core) has it.
        public static Encoding Default => Utf8NoBom.Value;

        public static Encoding ASCII => Ascii.Value;

        public static Encoding Latin1 => Latin1One.Value;

        public static Encoding Unicode => Utf16.Value;

        public static Encoding BigEndianUnicode => Utf16BigEndian.Value;

        public static Encoding UTF32 => Utf32.Value;

        // 65001, 20127, 28591, 1200, 1201, 12000 or 12001; anything else ends
        // the program.
        public static Encoding GetEncoding(int codepage)
        {
            switch (codepage)
            {
                case 65001: return UTF8;
                case 20127: return ASCII;
                case 28591: return Latin1;
                case 1200: return Unicode;
                case 1201: return BigEndianUnicode;
                case 12000: return UTF32;
                case 12001: return Utf32BigEndian.Value;
            }
            Environment.FailFast("Encoding.GetEncoding: only UTF-8, ASCII, Latin-1, UTF-16 and UTF-32 are available");
            return null;
        }

        // By the names .NET knows these encodings by, ignoring case:
        // "utf-8", "us-ascii", "iso-8859-1", "utf-16", "utf-16BE", "utf-32", ...
        public static Encoding GetEncoding(string name)
        {
            if (NameIs(name, "utf-8") || NameIs(name, "unicode-1-1-utf-8") || NameIs(name, "unicode-2-0-utf-8")
                || NameIs(name, "x-unicode20utf8"))
                return UTF8;
            if (NameIs(name, "us-ascii") || NameIs(name, "ascii") || NameIs(name, "us") || NameIs(name, "ansi_x3.4-1968")
                || NameIs(name, "ansi_x3.4-1986") || NameIs(name, "cp367") || NameIs(name, "csascii") || NameIs(name, "ibm367")
                || NameIs(name, "iso-ir-6") || NameIs(name, "iso646-us") || NameIs(name, "iso_646.irv:1991"))
                return ASCII;
            if (NameIs(name, "iso-8859-1") || NameIs(name, "latin1") || NameIs(name, "l1") || NameIs(name, "cp819")
                || NameIs(name, "csisolatin1") || NameIs(name, "ibm819") || NameIs(name, "iso-ir-100") || NameIs(name, "iso8859-1")
                || NameIs(name, "iso_8859-1") || NameIs(name, "iso_8859-1:1987"))
                return Latin1;
            if (NameIs(name, "utf-16") || NameIs(name, "utf-16le") || NameIs(name, "unicode") || NameIs(name, "ucs-2")
                || NameIs(name, "iso-10646-ucs-2") || NameIs(name, "unicode-1-1-utf-16") || NameIs(name, "unicode-2-0-utf-16"))
                return Unicode;
            if (NameIs(name, "utf-16be") || NameIs(name, "unicodefffe"))
                return BigEndianUnicode;
            if (NameIs(name, "utf-32") || NameIs(name, "utf-32le") || NameIs(name, "ucs-4"))
                return UTF32;
            if (NameIs(name, "utf-32be"))
                return GetEncoding(12001);
            Environment.FailFast("Encoding.GetEncoding: only UTF-8, ASCII, Latin-1, UTF-16 and UTF-32 are available");
            return null;
        }

        // ASCII letters compared without case; name is lower case.
        private static bool NameIs(string text, string name)
        {
            if (text == null || text.Length != name.Length)
                return false;
            for (int i = 0; i < name.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != name[i])
                    return false;
            }
            return true;
        }

        public abstract int CodePage { get; }

        // The IANA name: "utf-8", "us-ascii", "utf-16", ...
        public abstract string WebName { get; }

        public abstract string EncodingName { get; }

        public string BodyName => WebName;

        public string HeaderName => WebName;

        public virtual bool IsSingleByte => false;

        // The byte order mark that starts a file in this encoding, if it
        // writes one; empty otherwise.
        public virtual ReadOnlySpan<byte> Preamble => default;

        public byte[] GetPreamble() => Preamble.Length == 0 ? new byte[0] : ToArray(Preamble);

        // The most bytes charCount chars can become, and the most chars
        // byteCount bytes can become (both count one unfinished character
        // from before, as .NET does).
        public abstract int GetMaxByteCount(int charCount);

        public abstract int GetMaxCharCount(int byteCount);

        // What each encoding implements. capacity -1 counts without writing;
        // otherwise each returns the number written, or -1 if it didn't fit.
        internal abstract int Encode(char* chars, int charCount, byte* bytes, int capacity);

        internal abstract int Decode(byte* bytes, int byteCount, char* chars, int capacity);

        // Bytes

        public int GetByteCount(char* chars, int count)
        {
            if (chars == null && count != 0 || count < 0)
                Environment.FailFast("Encoding.GetByteCount: invalid pointer or count");
            return Encode(chars, count, null, -1);
        }

        public int GetByteCount(ReadOnlySpan<char> chars)
        {
            fixed (char* c = chars)
                return Encode(c, chars.Length, null, -1);
        }

        public int GetByteCount(string s) => GetByteCount(Slice(s, 0, s == null ? 0 : s.Length));

        public int GetByteCount(string s, int index, int count) => GetByteCount(Slice(s, index, count));

        public int GetByteCount(char[] chars)
        {
            if (chars == null)
                Environment.FailFast("ArgumentNullException: chars");
            return GetByteCount(new ReadOnlySpan<char>(chars));
        }

        public int GetByteCount(char[] chars, int index, int count) => GetByteCount(Slice(chars, index, count));

        public int GetBytes(char* chars, int charCount, byte* bytes, int byteCount)
        {
            if (chars == null && charCount != 0 || bytes == null && byteCount != 0 || charCount < 0 || byteCount < 0)
                Environment.FailFast("Encoding.GetBytes: invalid pointer or count");
            return Written(Encode(chars, charCount, bytes, byteCount), "Encoding.GetBytes");
        }

        public int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
        {
            fixed (char* c = chars)
            fixed (byte* b = bytes)
                return Written(Encode(c, chars.Length, b, bytes.Length), "Encoding.GetBytes");
        }

        public bool TryGetBytes(ReadOnlySpan<char> chars, Span<byte> bytes, out int bytesWritten)
        {
            fixed (char* c = chars)
            fixed (byte* b = bytes)
            {
                int written = Encode(c, chars.Length, b, bytes.Length);
                bytesWritten = written < 0 ? 0 : written;
                return written >= 0;
            }
        }

        public byte[] GetBytes(string s) => GetBytes(Slice(s, 0, s == null ? 0 : s.Length));

        public byte[] GetBytes(string s, int index, int count) => GetBytes(Slice(s, index, count));

        public byte[] GetBytes(char[] chars)
        {
            if (chars == null)
                Environment.FailFast("ArgumentNullException: chars");
            return GetBytes(new ReadOnlySpan<char>(chars));
        }

        public byte[] GetBytes(char[] chars, int index, int count) => GetBytes(Slice(chars, index, count));

        public int GetBytes(string s, int charIndex, int charCount, byte[] bytes, int byteIndex)
            => GetBytes(Slice(s, charIndex, charCount), SliceToEnd(bytes, byteIndex));

        public int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
            => GetBytes(Slice(chars, charIndex, charCount), SliceToEnd(bytes, byteIndex));

        private byte[] GetBytes(ReadOnlySpan<char> chars)
        {
            byte[] result = new byte[GetByteCount(chars)];
            GetBytes(chars, result);
            return result;
        }

        // Chars

        public int GetCharCount(byte* bytes, int count)
        {
            if (bytes == null && count != 0 || count < 0)
                Environment.FailFast("Encoding.GetCharCount: invalid pointer or count");
            return Decode(bytes, count, null, -1);
        }

        public int GetCharCount(ReadOnlySpan<byte> bytes)
        {
            fixed (byte* b = bytes)
                return Decode(b, bytes.Length, null, -1);
        }

        public int GetCharCount(byte[] bytes)
        {
            if (bytes == null)
                Environment.FailFast("ArgumentNullException: bytes");
            return GetCharCount(new ReadOnlySpan<byte>(bytes));
        }

        public int GetCharCount(byte[] bytes, int index, int count) => GetCharCount(Slice(bytes, index, count));

        public int GetChars(byte* bytes, int byteCount, char* chars, int charCount)
        {
            if (bytes == null && byteCount != 0 || chars == null && charCount != 0 || byteCount < 0 || charCount < 0)
                Environment.FailFast("Encoding.GetChars: invalid pointer or count");
            return Written(Decode(bytes, byteCount, chars, charCount), "Encoding.GetChars");
        }

        public int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars)
        {
            fixed (byte* b = bytes)
            fixed (char* c = chars)
                return Written(Decode(b, bytes.Length, c, chars.Length), "Encoding.GetChars");
        }

        public bool TryGetChars(ReadOnlySpan<byte> bytes, Span<char> chars, out int charsWritten)
        {
            fixed (byte* b = bytes)
            fixed (char* c = chars)
            {
                int written = Decode(b, bytes.Length, c, chars.Length);
                charsWritten = written < 0 ? 0 : written;
                return written >= 0;
            }
        }

        public char[] GetChars(byte[] bytes)
        {
            if (bytes == null)
                Environment.FailFast("ArgumentNullException: bytes");
            return GetChars(new ReadOnlySpan<byte>(bytes));
        }

        public char[] GetChars(byte[] bytes, int index, int count) => GetChars(Slice(bytes, index, count));

        public int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            if (chars == null || (uint)charIndex > (uint)chars.Length)
                Environment.FailFast("Encoding.GetChars: charIndex out of range");
            return GetChars(Slice(bytes, byteIndex, byteCount), new Span<char>(chars, charIndex, chars.Length - charIndex));
        }

        private char[] GetChars(ReadOnlySpan<byte> bytes)
        {
            char[] result = new char[GetCharCount(bytes)];
            GetChars(bytes, result);
            return result;
        }

        // Strings

        public string GetString(ReadOnlySpan<byte> bytes)
        {
            fixed (byte* b = bytes)
                return GetString(b, bytes.Length);
        }

        public string GetString(byte* bytes, int byteCount)
        {
            if (bytes == null && byteCount != 0 || byteCount < 0)
                Environment.FailFast("Encoding.GetString: invalid pointer or count");
            int length = Decode(bytes, byteCount, null, -1);
            if (length == 0)
                return "";
            string result = string.Allocate(length);
            fixed (char* c = result)
                Decode(bytes, byteCount, c, length);
            return result;
        }

        public string GetString(byte[] bytes)
        {
            if (bytes == null)
                Environment.FailFast("ArgumentNullException: bytes");
            return GetString(new ReadOnlySpan<byte>(bytes));
        }

        public string GetString(byte[] bytes, int index, int count) => GetString(Slice(bytes, index, count));

        // Bytes in one encoding as bytes in another.
        public static byte[] Convert(Encoding srcEncoding, Encoding dstEncoding, byte[] bytes)
        {
            if (bytes == null)
                Environment.FailFast("ArgumentNullException: bytes");
            return Convert(srcEncoding, dstEncoding, bytes, 0, bytes.Length);
        }

        public static byte[] Convert(Encoding srcEncoding, Encoding dstEncoding, byte[] bytes, int index, int count)
        {
            if (srcEncoding == null || dstEncoding == null)
                Environment.FailFast("ArgumentNullException: encoding");
            return dstEncoding.GetBytes(srcEncoding.GetChars(bytes, index, count));
        }

        // Equal when they write and read the same: the same code page, byte
        // order mark and handling of invalid input.
        public override bool Equals(object value)
            => value is Encoding other && other.CodePage == CodePage && other.Preamble.Length == Preamble.Length
                && other._throwOnInvalid == _throwOnInvalid;

        public override int GetHashCode() => CodePage + (Preamble.Length << 16) + (_throwOnInvalid ? 1 << 24 : 0);

        public override string ToString() => EncodingName;

        // For the encodings.

        // Where a replacement is needed: U+FFFD, or the end of the program.
        private protected void Invalid(string what)
        {
            if (_throwOnInvalid)
                Environment.FailFast(what);
        }

        private static int Written(int written, string method)
        {
            if (written < 0)
                Environment.FailFast(method == "Encoding.GetBytes"
                    ? "ArgumentException: the output byte buffer is too small for the encoded data"
                    : "ArgumentException: the output char buffer is too small for the decoded data");
            return written;
        }

        private static byte[] ToArray(ReadOnlySpan<byte> span)
        {
            byte[] result = new byte[span.Length];
            span.CopyTo(result);
            return result;
        }

        private static ReadOnlySpan<char> Slice(string s, int index, int count)
        {
            if (s == null)
                Environment.FailFast("ArgumentNullException: s");
            if (index < 0 || count < 0 || index > s.Length - count)
                Environment.FailFast("ArgumentOutOfRangeException: index or count");
            return new ReadOnlySpan<char>(ref Unsafe.Add(ref s.FirstChar, index), count);
        }

        private static ReadOnlySpan<T> Slice<T>(T[] array, int index, int count)
        {
            if (array == null)
                Environment.FailFast("ArgumentNullException: array");
            if (index < 0 || count < 0 || index > array.Length - count)
                Environment.FailFast("ArgumentOutOfRangeException: index or count");
            return new ReadOnlySpan<T>(array, index, count);
        }

        private static Span<byte> SliceToEnd(byte[] bytes, int index)
        {
            if (bytes == null)
                Environment.FailFast("ArgumentNullException: bytes");
            if ((uint)index > (uint)bytes.Length)
                Environment.FailFast("ArgumentOutOfRangeException: byteIndex");
            return new Span<byte>(bytes, index, bytes.Length - index);
        }
    }
}
