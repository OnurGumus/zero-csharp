// TLS 1.3's record layer and key schedule (RFC 8446 sections 5 and 7), for
// SslStream (lib/SslStream.cs).
//
// Records are at most 16 KB of plaintext, sent in the clear until the
// handshake has keys and then sealed with the cipher suite's AEAD: each with
// its own nonce (the IV XOR the record's sequence number) and the 5-byte
// record header as associated data, the real content type hidden inside.
//
// Nothing here throws over what the other end sends: a bad record sets
// Error and an alert to send back, and SslStream decides what to do.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace System.Net.Security;

/// <summary>A TLS 1.3 cipher suite: its AEAD, key size and hash.</summary>
internal readonly struct TlsSuite
{
    internal readonly ushort Code;

    internal TlsSuite(ushort code) { Code = code; }

    internal const ushort Aes128GcmSha256 = 0x1301;
    internal const ushort Aes256GcmSha384 = 0x1302;
    internal const ushort ChaCha20Poly1305Sha256 = 0x1303;

    internal static bool IsKnown(ushort code) => code >= 0x1301 && code <= 0x1303;

    internal int KeySize => Code == Aes128GcmSha256 ? 16 : 32;
    internal HashKind Hash => Code == Aes256GcmSha384 ? HashKind.Sha384 : HashKind.Sha256;
    internal HashAlgorithmName HashName => Code == Aes256GcmSha384 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256;
    internal int HashSize => Code == Aes256GcmSha384 ? 48 : 32;
}

/// <summary>TLS 1.3's HKDF-Expand-Label and Derive-Secret.</summary>
internal static class TlsKeys
{
    /// <summary>HKDF-Expand(secret, HkdfLabel(length, "tls13 " + label, context), length).</summary>
    internal static void ExpandLabel(TlsSuite suite, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> context, Span<byte> output)
    {
        Span<byte> info = stackalloc byte[2 + 1 + 6 + label.Length + 1 + context.Length];
        BinaryPrimitives.WriteUInt16BigEndian(info, (ushort)output.Length);
        info[2] = (byte)(6 + label.Length);
        "tls13 "u8.CopyTo(info.Slice(3));
        label.CopyTo(info.Slice(9));
        info[9 + label.Length] = (byte)context.Length;
        context.CopyTo(info.Slice(10 + label.Length));
        HKDF.Expand(suite.HashName, secret, output, info);
    }

    /// <summary>Derive-Secret with a transcript hash already computed.</summary>
    internal static void DeriveSecret(TlsSuite suite, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> transcriptHash, Span<byte> output)
        => ExpandLabel(suite, secret, label, transcriptHash, output.Slice(0, suite.HashSize));

    /// <summary>The next secret: Extract(salt = Derive-Secret(secret, "derived", ""), ikm).</summary>
    internal static void Next(TlsSuite suite, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> ikm, Span<byte> output)
    {
        int h = suite.HashSize;
        Span<byte> emptyHash = stackalloc byte[h];
        HashState.Hash(suite.Hash, ReadOnlySpan<byte>.Empty, emptyHash);
        Span<byte> derived = stackalloc byte[h];
        DeriveSecret(suite, secret, "derived"u8, emptyHash, derived);
        HKDF.Extract(suite.HashName, ikm, derived, output.Slice(0, h));
        CryptographicOperations.ZeroMemory(derived);
    }

    /// <summary>The Finished message's verify_data: HMAC(finished_key, transcript hash).</summary>
    internal static void Finished(TlsSuite suite, ReadOnlySpan<byte> trafficSecret, ReadOnlySpan<byte> transcriptHash, Span<byte> output)
    {
        int h = suite.HashSize;
        Span<byte> key = stackalloc byte[h];
        ExpandLabel(suite, trafficSecret, "finished"u8, ReadOnlySpan<byte>.Empty, key);
        HmacState.Mac(suite.Hash, key, transcriptHash, output.Slice(0, h));
        CryptographicOperations.ZeroMemory(key);
    }
}

/// <summary>One direction's record protection: the AEAD, its IV and the sequence number.</summary>
internal sealed class TlsCipher : IDisposable
{
    readonly AesGcm _aes;
    readonly ChaCha20Poly1305 _chacha;
    Bytes12 _iv;
    ulong _sequence;

    internal TlsCipher(TlsSuite suite, ReadOnlySpan<byte> trafficSecret)
    {
        Span<byte> key = stackalloc byte[suite.KeySize];
        TlsKeys.ExpandLabel(suite, trafficSecret, "key"u8, ReadOnlySpan<byte>.Empty, key);
        TlsKeys.ExpandLabel(suite, trafficSecret, "iv"u8, ReadOnlySpan<byte>.Empty, _iv);
        if (suite.Code == TlsSuite.ChaCha20Poly1305Sha256)
            _chacha = new ChaCha20Poly1305(key);
        else
            _aes = new AesGcm(key, 16);
        CryptographicOperations.ZeroMemory(key);
    }

    void Nonce(Span<byte> nonce)
    {
        ((ReadOnlySpan<byte>)_iv).CopyTo(nonce);
        for (int i = 0; i < 8; i++)
            nonce[11 - i] ^= (byte)(_sequence >> (8 * i));
        _sequence++;
    }

    internal bool Exhausted => _sequence == ulong.MaxValue;

    internal void Seal(Span<byte> text, Span<byte> tag, ReadOnlySpan<byte> header)
    {
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce);
        if (_chacha != null)
            _chacha.Encrypt(nonce, text, text, tag, header);
        else
            _aes.Encrypt(nonce, text, text, tag, header);
    }

    internal bool Open(Span<byte> text, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> header)
    {
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce);
        return _chacha != null
            ? _chacha.TryDecrypt(nonce, text, tag, text, header)
            : _aes.TryDecrypt(nonce, text, tag, text, header);
    }

    public void Dispose()
    {
        _aes?.Dispose();
        _chacha?.Dispose();
        CryptographicOperations.ZeroMemory(_iv);
    }
}

/// <summary>TLS alert descriptions (RFC 8446 6).</summary>
internal static class TlsAlert
{
    internal const byte CloseNotify = 0;
    internal const byte UnexpectedMessage = 10;
    internal const byte BadRecordMac = 20;
    internal const byte RecordOverflow = 22;
    internal const byte HandshakeFailure = 40;
    internal const byte BadCertificate = 42;
    internal const byte UnsupportedCertificate = 43;
    internal const byte CertificateExpired = 45;
    internal const byte CertificateUnknown = 46;
    internal const byte IllegalParameter = 47;
    internal const byte UnknownCa = 48;
    internal const byte DecodeError = 50;
    internal const byte DecryptError = 51;
    internal const byte ProtocolVersion = 70;
    internal const byte InternalError = 80;
    internal const byte UserCanceled = 90;
    internal const byte MissingExtension = 109;
    internal const byte UnsupportedExtension = 110;
    internal const byte UnrecognizedName = 112;
    internal const byte NoApplicationProtocol = 120;

    internal static string Name(int alert) => alert switch
    {
        CloseNotify => "close_notify",
        UnexpectedMessage => "unexpected_message",
        BadRecordMac => "bad_record_mac",
        RecordOverflow => "record_overflow",
        HandshakeFailure => "handshake_failure",
        BadCertificate => "bad_certificate",
        UnsupportedCertificate => "unsupported_certificate",
        CertificateExpired => "certificate_expired",
        CertificateUnknown => "certificate_unknown",
        IllegalParameter => "illegal_parameter",
        UnknownCa => "unknown_ca",
        DecodeError => "decode_error",
        DecryptError => "decrypt_error",
        ProtocolVersion => "protocol_version",
        InternalError => "internal_error",
        UserCanceled => "user_canceled",
        MissingExtension => "missing_extension",
        UnsupportedExtension => "unsupported_extension",
        UnrecognizedName => "unrecognized_name",
        NoApplicationProtocol => "no_application_protocol",
        _ => string.Concat("alert ", alert.ToString()),
    };
}

/// <summary>Reads and writes TLS records over a stream.</summary>
internal sealed class TlsRecordLayer : IDisposable
{
    internal const byte ChangeCipherSpec = 20;
    internal const byte Alert = 21;
    internal const byte Handshake = 22;
    internal const byte ApplicationData = 23;

    internal const int MaxPlaintext = 16384;
    const int HeaderSize = 5;
    const int TagSize = 16;

    readonly Stream _inner;
    TlsCipher _read, _write;
    readonly byte[] _in = new byte[HeaderSize + MaxPlaintext + 256];
    byte[] _out;

    /// <summary>The first ClientHello's record says TLS 1.0 (0x0301), as RFC 8446 suggests.</summary>
    internal bool FirstRecord = true;

    /// <summary>Why the last Read failed, and the alert to send for it.</summary>
    internal string Error;
    internal byte ErrorAlert;

    internal TlsRecordLayer(Stream inner) { _inner = inner; }

    internal bool Encrypting => _write != null;
    internal bool Decrypting => _read != null;

    internal void SetReadKey(TlsSuite suite, ReadOnlySpan<byte> secret)
    {
        _read?.Dispose();
        _read = new TlsCipher(suite, secret);
    }

    internal void SetWriteKey(TlsSuite suite, ReadOnlySpan<byte> secret)
    {
        _write?.Dispose();
        _write = new TlsCipher(suite, secret);
    }

    bool Fail(string message, byte alert)
    {
        Error = message;
        ErrorAlert = alert;
        return false;
    }

    /// <summary>
    /// Reads one record. True with its content type and plaintext; false at the end of
    /// the stream (Error null) or for a bad record (Error set). The fragment is valid
    /// until the next Read.
    /// </summary>
    internal bool Read(out byte contentType, out Span<byte> fragment)
    {
        contentType = 0;
        fragment = default;
        Error = null;
        Span<byte> header = new Span<byte>(_in, 0, HeaderSize);
        int got = _inner.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false);
        if (got == 0)
            return false;
        if (got < HeaderSize)
            return Fail("the connection ended inside a record", TlsAlert.DecodeError);
        byte type = header[0];
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(3));
        if (header[1] != 3 || type < ChangeCipherSpec || type > ApplicationData)
            return Fail("what came isn't TLS (a record with an unknown type or version)", TlsAlert.DecodeError);
        if (length > MaxPlaintext + 256)
            return Fail("a record was too long", TlsAlert.RecordOverflow);
        Span<byte> body = new Span<byte>(_in, HeaderSize, length);
        if (_inner.ReadAtLeast(body, length, throwOnEndOfStream: false) < length)
            return Fail("the connection ended inside a record", TlsAlert.DecodeError);

        // change_cipher_spec stays in the clear, for middleboxes (RFC 8446 D.4).
        if (_read == null || type == ChangeCipherSpec)
        {
            if (length > MaxPlaintext)
                return Fail("a record was too long", TlsAlert.RecordOverflow);
            if (length == 0 && type != ApplicationData)
                return Fail("an empty record", TlsAlert.UnexpectedMessage);
            contentType = type;
            fragment = body;
            return true;
        }
        if (type != ApplicationData || length < TagSize + 1)
            return Fail("a record that should have been encrypted wasn't", TlsAlert.UnexpectedMessage);
        int textLength = length - TagSize;
        Span<byte> text = body.Slice(0, textLength);
        if (!_read.Open(text, body.Slice(textLength), header))
            return Fail("a record failed its check (changed on the way, or a wrong key)", TlsAlert.BadRecordMac);
        // The content type is the last byte that isn't padding.
        int end = textLength;
        while (end > 0 && text[end - 1] == 0)
            end--;
        if (end == 0)
            return Fail("a record had no content type", TlsAlert.UnexpectedMessage);
        if (end - 1 > MaxPlaintext)
            return Fail("a record was too long", TlsAlert.RecordOverflow);
        contentType = text[end - 1];
        if (contentType == ChangeCipherSpec || contentType < Alert || contentType > ApplicationData)
            return Fail("a record of an unexpected type", TlsAlert.UnexpectedMessage);
        fragment = text.Slice(0, end - 1);
        return true;
    }

    /// <summary>Sends data as records of this type, encrypted once there is a write key.</summary>
    internal void Write(byte contentType, ReadOnlySpan<byte> data)
    {
        do
        {
            int n = Math.Min(data.Length, MaxPlaintext);
            WriteOne(contentType, data.Slice(0, n));
            data = data.Slice(n);
        } while (!data.IsEmpty);
    }

    void WriteOne(byte contentType, ReadOnlySpan<byte> data)
    {
        _out ??= new byte[HeaderSize + MaxPlaintext + 1 + TagSize];
        Span<byte> record;
        if (_write == null || contentType == ChangeCipherSpec)
        {
            record = new Span<byte>(_out, 0, HeaderSize + data.Length);
            record[0] = contentType;
            record[1] = 3;
            record[2] = FirstRecord ? (byte)1 : (byte)3;
            BinaryPrimitives.WriteUInt16BigEndian(record.Slice(3), (ushort)data.Length);
            data.CopyTo(record.Slice(HeaderSize));
        }
        else
        {
            if (_write.Exhausted)
                throw new IOException("SslStream: too many records under one key.");
            int textLength = data.Length + 1;
            int length = textLength + TagSize;
            record = new Span<byte>(_out, 0, HeaderSize + length);
            record[0] = ApplicationData;
            record[1] = 3;
            record[2] = 3;
            BinaryPrimitives.WriteUInt16BigEndian(record.Slice(3), (ushort)length);
            Span<byte> text = record.Slice(HeaderSize, textLength);
            data.CopyTo(text);
            text[data.Length] = contentType;
            _write.Seal(text, record.Slice(HeaderSize + textLength, TagSize), record.Slice(0, HeaderSize));
        }
        FirstRecord = false;
        _inner.Write(record);
    }

    /// <summary>Sends an alert: close_notify is a warning, everything else fatal.</summary>
    internal void SendAlert(byte description)
    {
        Span<byte> alert = stackalloc byte[2];
        alert[0] = description == TlsAlert.CloseNotify || description == TlsAlert.UserCanceled ? (byte)1 : (byte)2;
        alert[1] = description;
        Write(Alert, alert);
        _inner.Flush();
    }

    internal void Flush() => _inner.Flush();

    public void Dispose()
    {
        _read?.Dispose();
        _write?.Dispose();
        _read = _write = null;
        CryptographicOperations.ZeroMemory(_in);
        if (_out != null)
            CryptographicOperations.ZeroMemory(_out);
    }
}
