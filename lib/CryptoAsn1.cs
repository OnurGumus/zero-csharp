// Reading DER, the encoding of certificates and keys (ITU-T X.690), and Oid.
//
// DerReader takes one element at a time off the front of a span. It reads
// data from the other end of a connection, so nothing in it throws: a
// malformed or non-minimal encoding sets Failed, which sticks, and every
// read after that returns empty contents. The caller checks Failed once at
// the end. Only what certificates use is accepted: definite lengths,
// minimal encodings, tag numbers below 31 and lengths below 16 MB.

using System;

namespace System.Security.Cryptography;

internal ref struct DerReader
{
    internal const int Boolean = 0x01;
    internal const int Integer = 0x02;
    internal const int BitString = 0x03;
    internal const int OctetString = 0x04;
    internal const int Null = 0x05;
    internal const int ObjectIdentifier = 0x06;
    internal const int Utf8String = 0x0C;
    internal const int PrintableString = 0x13;
    internal const int TeletexString = 0x14;
    internal const int Ia5String = 0x16;
    internal const int UtcTime = 0x17;
    internal const int GeneralizedTime = 0x18;
    internal const int UniversalString = 0x1C;
    internal const int BmpString = 0x1E;
    internal const int Sequence = 0x30;
    internal const int Set = 0x31;

    /// <summary>Context-specific tag [n], constructed (EXPLICIT tagging, or IMPLICIT of a SEQUENCE).</summary>
    internal static int Context(int n) => 0xA0 | n;

    /// <summary>Context-specific tag [n], primitive (IMPLICIT of a string or INTEGER).</summary>
    internal static int ContextPrimitive(int n) => 0x80 | n;

    ReadOnlySpan<byte> _data;
    bool _failed;

    internal DerReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _failed = false;
    }

    internal bool Failed => _failed;
    internal bool HasData => !_failed && !_data.IsEmpty;
    internal int PeekTag() => HasData ? _data[0] : -1;

    internal void Fail()
    {
        _failed = true;
        _data = default;
    }

    /// <summary>Fails unless everything was read.</summary>
    internal void End()
    {
        if (!_data.IsEmpty)
            Fail();
    }

    /// <summary>Reads the next element, whatever its tag; returns its contents.</summary>
    internal ReadOnlySpan<byte> ReadAny(out int tag, out ReadOnlySpan<byte> encoded)
    {
        tag = -1;
        encoded = default;
        if (_failed || _data.Length < 2 || (_data[0] & 0x1F) == 0x1F)
        {
            Fail();
            return default;
        }
        tag = _data[0];
        int length = _data[1];
        int header = 2;
        if (length >= 0x80)
        {
            int count = length & 0x7F;
            // 0x80 is BER's indefinite length; more than 3 bytes is over 16 MB.
            if (count == 0 || count > 3 || _data.Length < 2 + count || _data[2] == 0)
            {
                Fail();
                return default;
            }
            length = 0;
            for (int i = 0; i < count; i++)
                length = (length << 8) | _data[2 + i];
            if (length < 0x80)
            {
                Fail(); // DER uses the short form when it can
                return default;
            }
            header += count;
        }
        if (length > _data.Length - header)
        {
            Fail();
            return default;
        }
        encoded = _data.Slice(0, header + length);
        ReadOnlySpan<byte> contents = _data.Slice(header, length);
        _data = _data.Slice(header + length);
        return contents;
    }

    /// <summary>Reads an element with this tag; returns its contents.</summary>
    internal ReadOnlySpan<byte> Read(int tag) => Read(tag, out _);

    /// <summary>Reads an element with this tag; returns its contents, and the whole element in <paramref name="encoded"/>.</summary>
    internal ReadOnlySpan<byte> Read(int tag, out ReadOnlySpan<byte> encoded)
    {
        if (PeekTag() != tag)
        {
            Fail();
            encoded = default;
            return default;
        }
        return ReadAny(out _, out encoded);
    }

    /// <summary>Reads the element if the next one has this tag (an OPTIONAL field).</summary>
    internal bool TryRead(int tag, out ReadOnlySpan<byte> contents)
    {
        if (PeekTag() != tag)
        {
            contents = default;
            return false;
        }
        contents = ReadAny(out _, out _);
        return !_failed;
    }

    internal DerReader ReadSequence() => new DerReader(Read(Sequence));

    internal DerReader ReadSequence(int tag) => new DerReader(Read(tag));

    /// <summary>
    /// A non-negative INTEGER's magnitude, big-endian, without the leading zero DER adds
    /// when the top bit is set. Fails on negative or non-minimal encodings.
    /// </summary>
    internal ReadOnlySpan<byte> ReadUnsignedInteger()
    {
        ReadOnlySpan<byte> value = Read(Integer);
        if (_failed)
            return default;
        if (value.IsEmpty || (value[0] & 0x80) != 0)
        {
            Fail();
            return default;
        }
        if (value.Length > 1 && value[0] == 0)
        {
            if ((value[1] & 0x80) == 0)
            {
                Fail(); // a zero byte that wasn't needed
                return default;
            }
            value = value.Slice(1);
        }
        return value;
    }

    /// <summary>A small non-negative INTEGER, such as a version or path length.</summary>
    internal int ReadSmallInteger()
    {
        ReadOnlySpan<byte> value = ReadUnsignedInteger();
        if (value.Length > 3)
        {
            Fail();
            return 0;
        }
        int result = 0;
        foreach (byte b in value)
            result = (result << 8) | b;
        return result;
    }

    internal bool ReadBoolean()
    {
        ReadOnlySpan<byte> value = Read(Boolean);
        if (_failed)
            return false;
        // DER: true is 0xFF, false is 0x00, nothing else.
        if (value.Length != 1 || (value[0] != 0 && value[0] != 0xFF))
        {
            Fail();
            return false;
        }
        return value[0] != 0;
    }

    /// <summary>A BIT STRING whose bits fill whole bytes (keys, signatures).</summary>
    internal ReadOnlySpan<byte> ReadBitStringBytes()
    {
        ReadOnlySpan<byte> value = Read(BitString);
        if (_failed)
            return default;
        if (value.IsEmpty || value[0] != 0)
        {
            Fail();
            return default;
        }
        return value.Slice(1);
    }

    /// <summary>A BIT STRING of flags (key usage): bit 0 is the top bit of the first byte.</summary>
    internal int ReadFlags()
    {
        ReadOnlySpan<byte> value = Read(BitString);
        if (_failed)
            return 0;
        if (value.IsEmpty || value[0] > 7 || (value.Length == 1 && value[0] != 0))
        {
            Fail();
            return 0;
        }
        int flags = 0;
        for (int i = 1; i < value.Length && i <= 3; i++)
            for (int bit = 0; bit < 8; bit++)
                if ((value[i] & (0x80 >> bit)) != 0)
                    flags |= 1 << (8 * (i - 1) + bit);
        return flags;
    }

    /// <summary>An OBJECT IDENTIFIER's encoded contents (compare with Same).</summary>
    internal ReadOnlySpan<byte> ReadOid()
    {
        ReadOnlySpan<byte> value = Read(ObjectIdentifier);
        if (!_failed && (value.IsEmpty || (value[value.Length - 1] & 0x80) != 0))
            Fail();
        return value;
    }

    /// <summary>A string of these characters (zerocore's string has no span constructor).</summary>
    internal static string Chars(ReadOnlySpan<char> chars) => new System.Text.StringBuilder(chars.Length).Append(chars).ToString();

    internal static bool Same(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    /// <summary>
    /// An encoded OBJECT IDENTIFIER's dotted form ("1.2.840.10045.2.1"), or null
    /// if the encoding is malformed.
    /// </summary>
    internal static string OidToString(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty || (encoded[encoded.Length - 1] & 0x80) != 0)
            return null;
        Span<char> text = stackalloc char[encoded.Length * 4 + 8];
        int at = 0;
        ulong value = 0;
        bool first = true;
        for (int i = 0; i < encoded.Length; i++)
        {
            if (value == 0 && encoded[i] == 0x80)
                return null; // a leading 0x80 isn't minimal
            if (value > (ulong.MaxValue >> 7))
                return null;
            value = (value << 7) | (uint)(encoded[i] & 0x7F);
            if ((encoded[i] & 0x80) != 0)
                continue;
            if (first)
            {
                ulong top = value < 40 ? 0UL : value < 80 ? 1UL : 2UL;
                at = Append(text, at, top);
                text[at++] = '.';
                at = Append(text, at, value - 40 * top);
                first = false;
            }
            else
            {
                text[at++] = '.';
                at = Append(text, at, value);
            }
            value = 0;
        }
        return Chars(text.Slice(0, at));
    }

    static int Append(Span<char> text, int at, ulong value)
    {
        Span<char> digits = stackalloc char[20];
        int n = 0;
        do
        {
            digits[n++] = (char)('0' + (int)(value % 10));
            value /= 10;
        } while (value != 0);
        while (n > 0)
            text[at++] = digits[--n];
        return at;
    }

    /// <summary>
    /// A dotted OID's DER contents, or null if <paramref name="dotted"/> isn't one.
    /// </summary>
    internal static byte[] OidFromString(string dotted)
    {
        if (dotted == null)
            return null;
        Span<byte> buffer = stackalloc byte[dotted.Length * 2 + 8];
        int at = 0, arcIndex = 0;
        ulong first = 0;
        int i = 0;
        while (i <= dotted.Length)
        {
            int start = i;
            ulong arc = 0;
            while (i < dotted.Length && dotted[i] != '.')
            {
                char c = dotted[i];
                if (c < '0' || c > '9' || arc > (ulong.MaxValue - 9) / 10 / 128)
                    return null;
                arc = arc * 10 + (ulong)(c - '0');
                i++;
            }
            if (i == start || (i - start > 1 && dotted[start] == '0'))
                return null;
            if (arcIndex == 0)
            {
                if (arc > 2)
                    return null;
                first = arc;
            }
            else
            {
                if (arcIndex == 1)
                {
                    if (first < 2 && arc >= 40)
                        return null;
                    arc += 40 * first;
                }
                int groups = 1;
                for (ulong v = arc >> 7; v != 0; v >>= 7)
                    groups++;
                for (int g = groups - 1; g >= 0; g--)
                    buffer[at++] = (byte)(((arc >> (7 * g)) & 0x7F) | (g != 0 ? 0x80u : 0u));
            }
            arcIndex++;
            i++; // past the '.' (or the end)
        }
        if (arcIndex < 2)
            return null;
        return buffer.Slice(0, at).ToArray();
    }
}

/// <summary>An object identifier: the numbers ASN.1 names algorithms and fields with.</summary>
public sealed class Oid
{
    /// <summary>The dotted form, such as "1.3.6.1.5.5.7.3.1".</summary>
    public string Value { get; set; }

    /// <summary>A name for it, such as "Server Authentication", or null.</summary>
    public string FriendlyName { get; set; }

    public Oid() { }

    public Oid(string oid) { Value = oid; }

    public Oid(string value, string friendlyName)
    {
        Value = value;
        FriendlyName = friendlyName;
    }

    public Oid(Oid oid)
    {
        Value = oid.Value;
        FriendlyName = oid.FriendlyName;
    }

    public override string ToString() => Value ?? "";
}

/// <summary>A list of Oids, such as X509ChainPolicy.ApplicationPolicy.</summary>
public sealed class OidCollection
{
    Oid[] _items = new Oid[4];
    int _count;

    public int Count => _count;

    public Oid this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _items[index];
        }
    }

    public int Add(Oid oid)
    {
        if (_count == _items.Length)
        {
            Oid[] bigger = new Oid[_items.Length * 2];
            _items.AsSpan().CopyTo(bigger);
            _items = bigger;
        }
        _items[_count] = oid;
        return _count++;
    }
}
