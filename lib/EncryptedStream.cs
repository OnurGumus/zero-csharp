// EncryptedStream: a stream that encrypts what's written to it and decrypts
// what's read, over another stream (a NetworkStream, a Pipe's, a file), in
// TLS 1.3's record format with ChaCha20-Poly1305.
//
//   // Both ends know a secret (or agreed on one with X25519); each
//   // connection gets fresh keys from it and random bytes both ends send:
//   using var secure = EncryptedStream.FromSharedSecret(network, secret, isInitiator: true);
//   secure.Write("hello"u8);
//   int n = secure.Read(buffer);
//
// What's written goes out at once, in records of up to 16 KB, each sealed
// with its own nonce: the key's IV XOR the record's sequence number. A record
// that was changed, dropped, repeated or reordered fails its check, and Read
// throws IOException. Dispose sends a closing record (TLS's close_notify),
// and Read returns 0 only after one comes: a connection that ends without it
// may have been cut short by someone in the middle, so Read throws then too.
//
// This is the record layer of TLS 1.3 (RFC 8446 section 5) for
// TLS_CHACHA20_POLY1305_SHA256: give it the keys and IVs that a TLS
// handshake derives and it reads and writes what a TLS peer does. Here it
// serves two programs of yours that share a secret. To talk to TLS servers,
// use SslStream (lib/SslStream.cs), which has the handshake and the other
// cipher suites.
//
// Not in .NET (its CryptoStream is for block cipher transforms); the name is ours.

using System;
using System.Buffers.Binary;
using System.IO;

namespace System.Security.Cryptography;

/// <summary>
/// A stream that encrypts and authenticates what goes through it, in TLS 1.3
/// records sealed with ChaCha20-Poly1305, over another stream.
/// </summary>
public sealed class EncryptedStream : Stream
{
    /// <summary>16384: the most plaintext a record carries, as in TLS.</summary>
    public const int MaxRecordPlaintext = 16384;

    const int HeaderSize = 5;
    const int TagSize = 16;
    const byte ApplicationData = 23;
    const byte Alert = 21;
    // What may arrive: a full record plus the 255 bytes of padding TLS allows.
    const int MaxRecordBody = MaxRecordPlaintext + 256;

    readonly Stream _inner;
    readonly bool _leaveOpen;
    ChaCha20Poly1305 _sendCipher;
    ChaCha20Poly1305 _receiveCipher;
    Bytes12 _sendIV;
    Bytes12 _receiveIV;
    ulong _sendSequence;
    ulong _receiveSequence;

    byte[] _sendRecord;      // header, plaintext and content type, tag: sealed in place
    byte[] _receiveRecord;
    int _plainStart;         // the decrypted bytes not yet read
    int _plainEnd;
    bool _receivedClose;
    bool _sentClose;
    bool _disposed;

    /// <summary>
    /// An encrypted stream over <paramref name="inner"/> with the given keys
    /// (32 bytes) and IVs (12 bytes): the other end must have them the other
    /// way round. Record numbers start at 0 on every stream, so these keys
    /// must be new for each connection, as a TLS handshake's are: two streams
    /// sending with the same key and IV give their plaintexts away and let
    /// tags be forged. With a long-lived shared secret, use FromSharedSecret,
    /// which makes fresh keys from it each time.
    /// </summary>
    public EncryptedStream(Stream inner, ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> sendIV,
                           ReadOnlySpan<byte> receiveKey, ReadOnlySpan<byte> receiveIV, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (sendIV.Length != 12 || receiveIV.Length != 12)
            throw new ArgumentException("The IVs must be 12 bytes.");
        if (CryptographicOperations.FixedTimeEquals(sendKey, receiveKey))
            throw new ArgumentException("The send and receive keys must differ.", nameof(receiveKey));
        _sendCipher = new ChaCha20Poly1305(sendKey);
        _receiveCipher = new ChaCha20Poly1305(receiveKey);
        sendIV.CopyTo(_sendIV);
        receiveIV.CopyTo(_receiveIV);
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// An encrypted stream from a secret both ends know (at least 16 random
    /// bytes, or an X25519 shared secret). One end passes isInitiator true,
    /// the other false. First each end sends 32 fresh random bytes
    /// (RandomNumberGenerator) and reads the other's; HKDF-SHA256 then
    /// derives a key and IV for each direction from the secret and both of
    /// those. So the keys are new for every connection even when the secret
    /// is reused, and an end's sending keys are new even if someone replays
    /// the other end's random bytes: a repeated key and nonce would give
    /// the plaintext away and let tags be forged. The secret still has to be
    /// secret: anyone who knows it can derive the keys.
    /// </summary>
    public static EncryptedStream FromSharedSecret(Stream inner, ReadOnlySpan<byte> secret, bool isInitiator, bool leaveOpen = false)
    {
        if (inner == null)
            throw new ArgumentNullException(nameof(inner));
        if (secret.Length < 16)
            throw new ArgumentException("The secret must be at least 16 bytes.", nameof(secret));

        // Both ends write before reading: 32 bytes fit any transport's buffer.
        Span<byte> randoms = stackalloc byte[64];
        Span<byte> mine = isInitiator ? randoms.Slice(0, 32) : randoms.Slice(32, 32);
        Span<byte> theirs = isInitiator ? randoms.Slice(32, 32) : randoms.Slice(0, 32);
        RandomNumberGenerator.Fill(mine);
        inner.Write(mine);
        inner.Flush();
        if (inner.ReadAtLeast(theirs, 32, throwOnEndOfStream: false) < 32)
            throw new IOException("EncryptedStream: the connection ended before the other end's random bytes came.");

        Span<byte> prk = stackalloc byte[32];
        Span<byte> keys = stackalloc byte[88];
        // salt = initiator's random | responder's random
        HKDF.Extract(HashAlgorithmName.SHA256, secret, randoms, prk);
        HKDF.Expand(HashAlgorithmName.SHA256, prk, keys, "initiator key, iv; responder key, iv"u8);
        ReadOnlySpan<byte> initiatorKey = keys.Slice(0, 32), initiatorIV = keys.Slice(32, 12);
        ReadOnlySpan<byte> responderKey = keys.Slice(44, 32), responderIV = keys.Slice(76, 12);
        EncryptedStream stream = isInitiator
            ? new EncryptedStream(inner, initiatorKey, initiatorIV, responderKey, responderIV, leaveOpen)
            : new EncryptedStream(inner, responderKey, responderIV, initiatorKey, initiatorIV, leaveOpen);
        CryptographicOperations.ZeroMemory(prk);
        CryptographicOperations.ZeroMemory(keys);
        return stream;
    }

    /// <summary>The stream the records go through.</summary>
    public Stream InnerStream => _inner;

    public override bool CanRead => !_disposed;
    public override bool CanWrite => !_disposed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Flushes the inner stream; Write has already sent its records.</summary>
    public override void Flush()
    {
        CheckOpen();
        _inner.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(new Span<byte>(buffer, offset, count));
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    /// <summary>
    /// Reads decrypted bytes, waiting for a record if none are left; 0 once
    /// the other end has closed. Throws IOException for a record that fails
    /// its check, or a connection that ends without the closing record.
    /// </summary>
    public override int Read(Span<byte> buffer)
    {
        CheckOpen();
        if (buffer.IsEmpty)
            return 0;
        while (_plainStart == _plainEnd)
        {
            if (_receivedClose || !ReceiveRecord())
                return 0;
        }
        int n = Math.Min(buffer.Length, _plainEnd - _plainStart);
        new ReadOnlySpan<byte>(_receiveRecord, _plainStart, n).CopyTo(buffer);
        _plainStart += n;
        return n;
    }

    /// <summary>Encrypts buffer and sends it, in as many records as it needs.</summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckOpen();
        if (_sentClose)
            throw new IOException("EncryptedStream: written after closing.");
        while (!buffer.IsEmpty)
        {
            int n = Math.Min(buffer.Length, MaxRecordPlaintext);
            SendRecord(ApplicationData, buffer.Slice(0, n));
            buffer = buffer.Slice(n);
        }
    }

    // Seals and sends one record: plaintext, then its real content type.
    void SendRecord(byte contentType, ReadOnlySpan<byte> plaintext)
    {
        if (_sendSequence == ulong.MaxValue)
            throw new IOException("EncryptedStream: too many records for one key.");
        _sendRecord ??= new byte[HeaderSize + MaxRecordPlaintext + 1 + TagSize];
        int innerLength = plaintext.Length + 1;
        int bodyLength = innerLength + TagSize;
        Span<byte> record = new Span<byte>(_sendRecord, 0, HeaderSize + bodyLength);
        // The outer header always says application data, TLS 1.2 version.
        record[0] = ApplicationData;
        record[1] = 3;
        record[2] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(record.Slice(3), (ushort)bodyLength);
        Span<byte> inner = record.Slice(HeaderSize, innerLength);
        plaintext.CopyTo(inner);
        inner[plaintext.Length] = contentType;

        Span<byte> nonce = stackalloc byte[12];
        Nonce(_sendIV, _sendSequence++, nonce);
        _sendCipher.Encrypt(nonce, inner, inner, record.Slice(HeaderSize + innerLength, TagSize), record.Slice(0, HeaderSize));
        _inner.Write(record);
    }

    // Reads and opens one record. False at the inner stream's end, which is
    // only all right after the closing record.
    bool ReceiveRecord()
    {
        _receiveRecord ??= new byte[HeaderSize + MaxRecordBody];
        Span<byte> header = new Span<byte>(_receiveRecord, 0, HeaderSize);
        int got = _inner.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false);
        if (got == 0)
            throw new IOException("EncryptedStream: the connection ended without closing (it may have been cut short).");
        if (got < HeaderSize)
            throw new IOException("EncryptedStream: the connection ended inside a record.");
        int bodyLength = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(3));
        if (header[0] != ApplicationData || header[1] != 3 || header[2] != 3
            || bodyLength < TagSize + 1 || bodyLength > MaxRecordBody)
            throw new IOException("EncryptedStream: not a record of this kind (wrong header).");
        Span<byte> body = new Span<byte>(_receiveRecord, HeaderSize, bodyLength);
        if (_inner.ReadAtLeast(body, bodyLength, throwOnEndOfStream: false) < bodyLength)
            throw new IOException("EncryptedStream: the connection ended inside a record.");

        int innerLength = bodyLength - TagSize;
        Span<byte> inner = body.Slice(0, innerLength);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(_receiveIV, _receiveSequence++, nonce);
        if (!_receiveCipher.TryDecrypt(nonce, inner, body.Slice(innerLength), inner, header))
            throw new IOException("EncryptedStream: a record failed its check (changed, out of order, or a wrong key).");

        // The content type is the last byte that isn't padding.
        int end = innerLength;
        while (end > 0 && inner[end - 1] == 0)
            end--;
        if (end == 0)
            throw new IOException("EncryptedStream: a record had no content type.");
        byte contentType = inner[end - 1];
        int length = end - 1;
        if (contentType == ApplicationData)
        {
            if (length > MaxRecordPlaintext)
                throw new IOException("EncryptedStream: a record was too long.");
            _plainStart = HeaderSize;
            _plainEnd = HeaderSize + length;
            return true;
        }
        if (contentType == Alert && length == 2 && inner[1] == 0)
        {
            // close_notify: the other end has finished writing.
            _receivedClose = true;
            _plainStart = _plainEnd = 0;
            return false;
        }
        if (contentType == Alert && length == 2)
            throw new IOException("EncryptedStream: the other end reported an error (alert " + inner[1] + ").");
        throw new IOException("EncryptedStream: a record of an unexpected type (" + contentType + ").");
    }

    // TLS 1.3's per-record nonce: the IV, its last 8 bytes XOR the sequence number.
    static void Nonce(ReadOnlySpan<byte> iv, ulong sequence, Span<byte> nonce)
    {
        iv.CopyTo(nonce);
        for (int i = 0; i < 8; i++)
            nonce[11 - i] ^= (byte)(sequence >> (8 * i));
    }

    /// <summary>
    /// Sends the closing record, after which the other end's Read returns 0;
    /// writing after it throws. Dispose does it too.
    /// </summary>
    public void Shutdown()
    {
        CheckOpen();
        if (_sentClose)
            return;
        Span<byte> closeNotify = stackalloc byte[2];
        closeNotify[0] = 1; // warning
        closeNotify[1] = 0; // close_notify
        SendRecord(Alert, closeNotify);
        _sentClose = true;
        _inner.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        if (disposing)
        {
            if (!_sentClose && _inner.CanWrite)
                Shutdown();
            _disposed = true;
            _sendCipher.Dispose();
            _receiveCipher.Dispose();
            CryptographicOperations.ZeroMemory(_sendIV);
            CryptographicOperations.ZeroMemory(_receiveIV);
            if (_receiveRecord != null)
                CryptographicOperations.ZeroMemory(_receiveRecord);
            if (_sendRecord != null)
                CryptographicOperations.ZeroMemory(_sendRecord);
            if (!_leaveOpen)
                _inner.Dispose();
        }
        _disposed = true;
    }

    void CheckOpen()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(EncryptedStream));
    }
}

[System.Runtime.CompilerServices.InlineArray(12)]
internal struct Bytes12 { byte _element; }
