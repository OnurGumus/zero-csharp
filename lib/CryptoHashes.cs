// Hashes and keys derived from them, with .NET's names from
// System.Security.Cryptography: SHA-256, SHA-384 and SHA-512, HMAC on each,
// HKDF, IncrementalHash for data that comes in pieces, and
// CryptographicOperations' constant-time compare.
//
//   byte[] digest = SHA256.HashData("abc"u8);
//   byte[] mac = HMACSHA256.HashData(key, message);
//   byte[] keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, info);
//
//   using var transcript = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
//   transcript.AppendData(hello);
//   byte[] soFar = transcript.GetCurrentHash();     // and go on appending
//
//   if (!CryptographicOperations.FixedTimeEquals(mac, expected)) ...
//
// These are the hashes TLS 1.3 needs: its key schedule is HKDF on SHA-256
// or SHA-384 over a running hash of the handshake. They're plain C#, the same
// code on every target, checked against the standards' test vectors
// (samples/Crypto.cs) and against .NET's own on random input
// (scripts/crypto-fuzz).
//
// What differs from .NET:
// - No MD5, SHA-1, SHA-3 or the HashAlgorithm class hierarchy (SHA256.Create(),
//   ComputeHash, TransformBlock): the static HashData methods and
//   IncrementalHash do the same work.
// - A wrong argument (an HKDF output that's too long, a destination that's
//   too small) throws, which ends the program unless a Catch.Run is waiting
//   for it (Catch.cs).

using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace System.Security.Cryptography;

/// <summary>A failure in a cryptographic operation. Thrown, it ends the program unless a Catch.Run is waiting for it.</summary>
public class CryptographicException : Exception
{
    public CryptographicException() : base("A cryptographic operation failed.") { }
    public CryptographicException(string message) : base(message) { }
}

/// <summary>Which hash: SHA256, SHA384 or SHA512 are the ones here.</summary>
public readonly struct HashAlgorithmName
{
    readonly string _name;

    /// <summary>A hash by its name, such as "SHA256".</summary>
    public HashAlgorithmName(string name) { _name = name; }

    /// <summary>The name, such as "SHA256".</summary>
    public string Name => _name;

    /// <summary>SHA-256: 32 bytes.</summary>
    public static HashAlgorithmName SHA256 => new HashAlgorithmName("SHA256");
    /// <summary>SHA-384: 48 bytes.</summary>
    public static HashAlgorithmName SHA384 => new HashAlgorithmName("SHA384");
    /// <summary>SHA-512: 64 bytes.</summary>
    public static HashAlgorithmName SHA512 => new HashAlgorithmName("SHA512");

    public static bool operator ==(HashAlgorithmName left, HashAlgorithmName right) => left._name == right._name;
    public static bool operator !=(HashAlgorithmName left, HashAlgorithmName right) => left._name != right._name;
    public override bool Equals(object obj) => obj is HashAlgorithmName other && other._name == _name;
    public override int GetHashCode() => _name == null ? 0 : _name.GetHashCode();
    public override string ToString() => _name ?? "";

    internal HashKind Kind
    {
        get
        {
            if (_name == "SHA256") return HashKind.Sha256;
            if (_name == "SHA384") return HashKind.Sha384;
            if (_name == "SHA512") return HashKind.Sha512;
            throw new CryptographicException("Unknown hash algorithm: " + (_name ?? "(null)"));
        }
    }
}

/// <summary>Comparing and erasing secrets.</summary>
public static class CryptographicOperations
{
    /// <summary>
    /// Whether the two are the same bytes, in a time that depends only on
    /// their length, not on where they first differ. For comparing MACs and
    /// tags: an early return would tell an attacker how much of a guess was right.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
            return false;
        int difference = 0;
        for (int i = 0; i < left.Length; i++)
            difference |= left[i] ^ right[i];
        // (difference - 1) >> 8 has bit 0 set only when difference is 0.
        return ((difference - 1) >> 8 & 1) == 1;
    }

    /// <summary>Overwrites buffer with zeros, in a way the compiler doesn't remove as a dead store.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ZeroMemory(Span<byte> buffer) => buffer.Clear();
}

internal enum HashKind
{
    Sha256,
    Sha384,
    Sha512,
}

[InlineArray(8)]
internal struct UInt32x8 { uint _element; }

[InlineArray(8)]
internal struct UInt64x8 { ulong _element; }

[InlineArray(64)]
internal struct Bytes64 { byte _element; }

[InlineArray(128)]
internal struct Bytes128 { byte _element; }

// SHA-256 (FIPS 180-4). A struct, so a copy carries on from the same point:
// that's how IncrementalHash.GetCurrentHash finishes without disturbing it.
internal struct Sha256Core
{
    UInt32x8 _h;
    Bytes64 _buffer;
    int _buffered;
    ulong _length;

    static ReadOnlySpan<uint> Initial =>
    [
        0x6a09e667U, 0xbb67ae85U, 0x3c6ef372U, 0xa54ff53aU, 0x510e527fU, 0x9b05688cU, 0x1f83d9abU, 0x5be0cd19U,
    ];

    static ReadOnlySpan<uint> K =>
    [
        0x428a2f98U, 0x71374491U, 0xb5c0fbcfU, 0xe9b5dba5U, 0x3956c25bU, 0x59f111f1U, 0x923f82a4U, 0xab1c5ed5U,
        0xd807aa98U, 0x12835b01U, 0x243185beU, 0x550c7dc3U, 0x72be5d74U, 0x80deb1feU, 0x9bdc06a7U, 0xc19bf174U,
        0xe49b69c1U, 0xefbe4786U, 0x0fc19dc6U, 0x240ca1ccU, 0x2de92c6fU, 0x4a7484aaU, 0x5cb0a9dcU, 0x76f988daU,
        0x983e5152U, 0xa831c66dU, 0xb00327c8U, 0xbf597fc7U, 0xc6e00bf3U, 0xd5a79147U, 0x06ca6351U, 0x14292967U,
        0x27b70a85U, 0x2e1b2138U, 0x4d2c6dfcU, 0x53380d13U, 0x650a7354U, 0x766a0abbU, 0x81c2c92eU, 0x92722c85U,
        0xa2bfe8a1U, 0xa81a664bU, 0xc24b8b70U, 0xc76c51a3U, 0xd192e819U, 0xd6990624U, 0xf40e3585U, 0x106aa070U,
        0x19a4c116U, 0x1e376c08U, 0x2748774cU, 0x34b0bcb5U, 0x391c0cb3U, 0x4ed8aa4aU, 0x5b9cca4fU, 0x682e6ff3U,
        0x748f82eeU, 0x78a5636fU, 0x84c87814U, 0x8cc70208U, 0x90befffaU, 0xa4506cebU, 0xbef9a3f7U, 0xc67178f2U,
    ];

    public void Initialize()
    {
        Initial.CopyTo(_h);
        _buffered = 0;
        _length = 0;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _length += (ulong)data.Length;
        if (_buffered > 0)
        {
            int n = Math.Min(64 - _buffered, data.Length);
            data.Slice(0, n).CopyTo(((Span<byte>)_buffer).Slice(_buffered));
            _buffered += n;
            data = data.Slice(n);
            if (_buffered < 64)
                return;
            Compress(_buffer);
            _buffered = 0;
        }
        while (data.Length >= 64)
        {
            Compress(data.Slice(0, 64));
            data = data.Slice(64);
        }
        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    // Writes the hash's first output.Length bytes (32, or fewer); the state is used up.
    public void Finish(Span<byte> output)
    {
        ulong bits = _length * 8;
        Span<byte> pad = stackalloc byte[72];
        pad.Clear();
        pad[0] = 0x80;
        int padLength = (_buffered < 56 ? 56 : 120) - _buffered;
        BinaryPrimitives.WriteUInt64BigEndian(pad.Slice(padLength), bits);
        Append(pad.Slice(0, padLength + 8));
        Span<byte> digest = stackalloc byte[32];
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32BigEndian(digest.Slice(4 * i), _h[i]);
        digest.Slice(0, output.Length).CopyTo(output);
    }

    void Compress(ReadOnlySpan<byte> block)
    {
        Span<uint> w = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
            w[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(4 * i));
        uint a = _h[0], b = _h[1], c = _h[2], d = _h[3], e = _h[4], f = _h[5], g = _h[6], h = _h[7];
        ReadOnlySpan<uint> k = K;
        for (int i = 0; i < 64; i++)
        {
            uint wi;
            if (i < 16)
                wi = w[i];
            else
            {
                // w[i & 15] still holds W[i-16], which the new word replaces.
                uint w15 = w[(i - 15) & 15], w2 = w[(i - 2) & 15];
                uint s0 = BitOperations.RotateRight(w15, 7) ^ BitOperations.RotateRight(w15, 18) ^ (w15 >> 3);
                uint s1 = BitOperations.RotateRight(w2, 17) ^ BitOperations.RotateRight(w2, 19) ^ (w2 >> 10);
                wi = w[i & 15] = w[i & 15] + s0 + w[(i - 7) & 15] + s1;
            }
            uint sum1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);
            uint choose = (e & f) ^ (~e & g);
            uint t1 = h + sum1 + choose + k[i] + wi;
            uint sum0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
            uint majority = (a & b) ^ (a & c) ^ (b & c);
            h = g; g = f; f = e; e = d + t1;
            d = c; c = b; b = a; a = t1 + sum0 + majority;
        }
        _h[0] += a; _h[1] += b; _h[2] += c; _h[3] += d;
        _h[4] += e; _h[5] += f; _h[6] += g; _h[7] += h;
    }
}

// SHA-512, and SHA-384, which is SHA-512 from other starting values, cut to 48 bytes.
internal struct Sha512Core
{
    UInt64x8 _h;
    Bytes128 _buffer;
    int _buffered;
    ulong _length;

    static ReadOnlySpan<ulong> Initial512 =>
    [
        0x6a09e667f3bcc908UL, 0xbb67ae8584caa73bUL, 0x3c6ef372fe94f82bUL, 0xa54ff53a5f1d36f1UL,
        0x510e527fade682d1UL, 0x9b05688c2b3e6c1fUL, 0x1f83d9abfb41bd6bUL, 0x5be0cd19137e2179UL,
    ];

    static ReadOnlySpan<ulong> Initial384 =>
    [
        0xcbbb9d5dc1059ed8UL, 0x629a292a367cd507UL, 0x9159015a3070dd17UL, 0x152fecd8f70e5939UL,
        0x67332667ffc00b31UL, 0x8eb44a8768581511UL, 0xdb0c2e0d64f98fa7UL, 0x47b5481dbefa4fa4UL,
    ];

    static ReadOnlySpan<ulong> K =>
    [
        0x428a2f98d728ae22UL, 0x7137449123ef65cdUL, 0xb5c0fbcfec4d3b2fUL, 0xe9b5dba58189dbbcUL,
        0x3956c25bf348b538UL, 0x59f111f1b605d019UL, 0x923f82a4af194f9bUL, 0xab1c5ed5da6d8118UL,
        0xd807aa98a3030242UL, 0x12835b0145706fbeUL, 0x243185be4ee4b28cUL, 0x550c7dc3d5ffb4e2UL,
        0x72be5d74f27b896fUL, 0x80deb1fe3b1696b1UL, 0x9bdc06a725c71235UL, 0xc19bf174cf692694UL,
        0xe49b69c19ef14ad2UL, 0xefbe4786384f25e3UL, 0x0fc19dc68b8cd5b5UL, 0x240ca1cc77ac9c65UL,
        0x2de92c6f592b0275UL, 0x4a7484aa6ea6e483UL, 0x5cb0a9dcbd41fbd4UL, 0x76f988da831153b5UL,
        0x983e5152ee66dfabUL, 0xa831c66d2db43210UL, 0xb00327c898fb213fUL, 0xbf597fc7beef0ee4UL,
        0xc6e00bf33da88fc2UL, 0xd5a79147930aa725UL, 0x06ca6351e003826fUL, 0x142929670a0e6e70UL,
        0x27b70a8546d22ffcUL, 0x2e1b21385c26c926UL, 0x4d2c6dfc5ac42aedUL, 0x53380d139d95b3dfUL,
        0x650a73548baf63deUL, 0x766a0abb3c77b2a8UL, 0x81c2c92e47edaee6UL, 0x92722c851482353bUL,
        0xa2bfe8a14cf10364UL, 0xa81a664bbc423001UL, 0xc24b8b70d0f89791UL, 0xc76c51a30654be30UL,
        0xd192e819d6ef5218UL, 0xd69906245565a910UL, 0xf40e35855771202aUL, 0x106aa07032bbd1b8UL,
        0x19a4c116b8d2d0c8UL, 0x1e376c085141ab53UL, 0x2748774cdf8eeb99UL, 0x34b0bcb5e19b48a8UL,
        0x391c0cb3c5c95a63UL, 0x4ed8aa4ae3418acbUL, 0x5b9cca4f7763e373UL, 0x682e6ff3d6b2b8a3UL,
        0x748f82ee5defb2fcUL, 0x78a5636f43172f60UL, 0x84c87814a1f0ab72UL, 0x8cc702081a6439ecUL,
        0x90befffa23631e28UL, 0xa4506cebde82bde9UL, 0xbef9a3f7b2c67915UL, 0xc67178f2e372532bUL,
        0xca273eceea26619cUL, 0xd186b8c721c0c207UL, 0xeada7dd6cde0eb1eUL, 0xf57d4f7fee6ed178UL,
        0x06f067aa72176fbaUL, 0x0a637dc5a2c898a6UL, 0x113f9804bef90daeUL, 0x1b710b35131c471bUL,
        0x28db77f523047d84UL, 0x32caab7b40c72493UL, 0x3c9ebe0a15c9bebcUL, 0x431d67c49c100d4cUL,
        0x4cc5d4becb3e42b6UL, 0x597f299cfc657e2aUL, 0x5fcb6fab3ad6faecUL, 0x6c44198c4a475817UL,
    ];

    public void Initialize(bool sha384)
    {
        (sha384 ? Initial384 : Initial512).CopyTo(_h);
        _buffered = 0;
        _length = 0;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _length += (ulong)data.Length;
        if (_buffered > 0)
        {
            int n = Math.Min(128 - _buffered, data.Length);
            data.Slice(0, n).CopyTo(((Span<byte>)_buffer).Slice(_buffered));
            _buffered += n;
            data = data.Slice(n);
            if (_buffered < 128)
                return;
            Compress(_buffer);
            _buffered = 0;
        }
        while (data.Length >= 128)
        {
            Compress(data.Slice(0, 128));
            data = data.Slice(128);
        }
        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    // Writes the hash's first output.Length bytes (64, or 48 for SHA-384); the state is used up.
    public void Finish(Span<byte> output)
    {
        ulong bits = _length * 8;
        Span<byte> pad = stackalloc byte[144];
        pad.Clear();
        pad[0] = 0x80;
        int padLength = (_buffered < 112 ? 112 : 240) - _buffered;
        // The length is 128 bits; its upper 64 are zero (the pad is cleared).
        BinaryPrimitives.WriteUInt64BigEndian(pad.Slice(padLength + 8), bits);
        Append(pad.Slice(0, padLength + 16));
        Span<byte> digest = stackalloc byte[64];
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt64BigEndian(digest.Slice(8 * i), _h[i]);
        digest.Slice(0, output.Length).CopyTo(output);
    }

    void Compress(ReadOnlySpan<byte> block)
    {
        Span<ulong> w = stackalloc ulong[16];
        for (int i = 0; i < 16; i++)
            w[i] = BinaryPrimitives.ReadUInt64BigEndian(block.Slice(8 * i));
        ulong a = _h[0], b = _h[1], c = _h[2], d = _h[3], e = _h[4], f = _h[5], g = _h[6], h = _h[7];
        ReadOnlySpan<ulong> k = K;
        for (int i = 0; i < 80; i++)
        {
            ulong wi;
            if (i < 16)
                wi = w[i];
            else
            {
                ulong w15 = w[(i - 15) & 15], w2 = w[(i - 2) & 15];
                ulong s0 = BitOperations.RotateRight(w15, 1) ^ BitOperations.RotateRight(w15, 8) ^ (w15 >> 7);
                ulong s1 = BitOperations.RotateRight(w2, 19) ^ BitOperations.RotateRight(w2, 61) ^ (w2 >> 6);
                wi = w[i & 15] = w[i & 15] + s0 + w[(i - 7) & 15] + s1;
            }
            ulong sum1 = BitOperations.RotateRight(e, 14) ^ BitOperations.RotateRight(e, 18) ^ BitOperations.RotateRight(e, 41);
            ulong choose = (e & f) ^ (~e & g);
            ulong t1 = h + sum1 + choose + k[i] + wi;
            ulong sum0 = BitOperations.RotateRight(a, 28) ^ BitOperations.RotateRight(a, 34) ^ BitOperations.RotateRight(a, 39);
            ulong majority = (a & b) ^ (a & c) ^ (b & c);
            h = g; g = f; f = e; e = d + t1;
            d = c; c = b; b = a; a = t1 + sum0 + majority;
        }
        _h[0] += a; _h[1] += b; _h[2] += c; _h[3] += d;
        _h[4] += e; _h[5] += f; _h[6] += g; _h[7] += h;
    }
}

// One of the three hashes, picked when it starts.
internal struct HashState
{
    HashKind _kind;
    Sha256Core _sha256;
    Sha512Core _sha512;

    public static int SizeOf(HashKind kind) => kind == HashKind.Sha256 ? 32 : kind == HashKind.Sha384 ? 48 : 64;
    public static int BlockSizeOf(HashKind kind) => kind == HashKind.Sha256 ? 64 : 128;

    public HashKind Kind => _kind;
    public int Size => SizeOf(_kind);

    public void Initialize(HashKind kind)
    {
        _kind = kind;
        if (kind == HashKind.Sha256)
            _sha256.Initialize();
        else
            _sha512.Initialize(kind == HashKind.Sha384);
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_kind == HashKind.Sha256)
            _sha256.Append(data);
        else
            _sha512.Append(data);
    }

    // Writes the Size bytes of the hash; the state is used up.
    public void Finish(Span<byte> output)
    {
        if (_kind == HashKind.Sha256)
            _sha256.Finish(output.Slice(0, 32));
        else
            _sha512.Finish(output.Slice(0, Size));
    }

    public static void Hash(HashKind kind, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        HashState state = default;
        state.Initialize(kind);
        state.Append(source);
        state.Finish(destination);
    }
}

// HMAC (RFC 2104) on one of the hashes: the inner hash, plus the states
// after the padded key, to start over from without the key.
internal struct HmacState
{
    HashState _inner;
    HashState _innerStart;
    HashState _outerStart;

    public HashKind Kind => _inner.Kind;
    public int Size => _inner.Size;

    public void Initialize(HashKind kind, ReadOnlySpan<byte> key)
    {
        int blockSize = HashState.BlockSizeOf(kind);
        Span<byte> block = stackalloc byte[128];
        block.Clear();
        if (key.Length > blockSize)
            HashState.Hash(kind, key, block);
        else
            key.CopyTo(block);
        block = block.Slice(0, blockSize);

        for (int i = 0; i < blockSize; i++)
            block[i] ^= 0x36;
        _innerStart.Initialize(kind);
        _innerStart.Append(block);
        for (int i = 0; i < blockSize; i++)
            block[i] ^= 0x36 ^ 0x5c;
        _outerStart.Initialize(kind);
        _outerStart.Append(block);
        CryptographicOperations.ZeroMemory(block);
        _inner = _innerStart;
    }

    public void Append(ReadOnlySpan<byte> data) => _inner.Append(data);

    // Writes the Size bytes of the MAC and starts over for a new message with the same key.
    public void FinishAndReset(Span<byte> output)
    {
        Span<byte> innerHash = stackalloc byte[64];
        innerHash = innerHash.Slice(0, Size);
        _inner.Finish(innerHash);
        HashState outer = _outerStart;
        outer.Append(innerHash);
        outer.Finish(output);
        _inner = _innerStart;
    }

    // The MAC of what's been appended so far; appending goes on from there.
    public void Current(Span<byte> output)
    {
        HashState saved = _inner;
        FinishAndReset(output);
        _inner = saved;
    }

    public static void Mac(HashKind kind, ReadOnlySpan<byte> key, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        HmacState state = default;
        state.Initialize(kind, key);
        state.Append(source);
        state.FinishAndReset(destination);
    }
}

internal static class HashArguments
{
    internal static int Check(Span<byte> destination, int size)
    {
        if (destination.Length < size)
            throw new ArgumentException("The destination is too small for the hash.", nameof(destination));
        return size;
    }

    internal static byte[] HashStream(HashKind kind, Stream source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        HashState state = default;
        state.Initialize(kind);
        Span<byte> chunk = stackalloc byte[512];
        int n;
        while ((n = source.Read(chunk)) > 0)
            state.Append(chunk.Slice(0, n));
        byte[] hash = new byte[state.Size];
        state.Finish(hash);
        return hash;
    }
}

/// <summary>SHA-256: a 32-byte hash.</summary>
public static class SHA256
{
    /// <summary>32: the hash's size in bytes.</summary>
    public const int HashSizeInBytes = 32;
    /// <summary>256: the hash's size in bits.</summary>
    public const int HashSizeInBits = 256;

    /// <summary>The hash of <paramref name="source"/>.</summary>
    public static byte[] HashData(byte[] source) => HashData(new ReadOnlySpan<byte>(source ?? throw new ArgumentNullException(nameof(source))));

    /// <summary>The hash of <paramref name="source"/>.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> source)
    {
        byte[] hash = new byte[HashSizeInBytes];
        HashState.Hash(HashKind.Sha256, source, hash);
        return hash;
    }

    /// <summary>Writes the hash of <paramref name="source"/> to destination, and returns its size (32).</summary>
    public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        HashState.Hash(HashKind.Sha256, source, destination.Slice(0, HashArguments.Check(destination, HashSizeInBytes)));
        return HashSizeInBytes;
    }

    /// <summary>Like HashData, but false instead of a throw when destination is too small.</summary>
    public static bool TryHashData(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = destination.Length < HashSizeInBytes ? 0 : HashData(source, destination);
        return bytesWritten != 0;
    }

    /// <summary>The hash of the rest of the stream.</summary>
    public static byte[] HashData(Stream source) => HashArguments.HashStream(HashKind.Sha256, source);
}

/// <summary>SHA-384: a 48-byte hash.</summary>
public static class SHA384
{
    /// <summary>48: the hash's size in bytes.</summary>
    public const int HashSizeInBytes = 48;
    /// <summary>384: the hash's size in bits.</summary>
    public const int HashSizeInBits = 384;

    /// <summary>The hash of <paramref name="source"/>.</summary>
    public static byte[] HashData(byte[] source) => HashData(new ReadOnlySpan<byte>(source ?? throw new ArgumentNullException(nameof(source))));

    /// <summary>The hash of <paramref name="source"/>.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> source)
    {
        byte[] hash = new byte[HashSizeInBytes];
        HashState.Hash(HashKind.Sha384, source, hash);
        return hash;
    }

    /// <summary>Writes the hash of <paramref name="source"/> to destination, and returns its size (48).</summary>
    public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        HashState.Hash(HashKind.Sha384, source, destination.Slice(0, HashArguments.Check(destination, HashSizeInBytes)));
        return HashSizeInBytes;
    }

    /// <summary>Like HashData, but false instead of a throw when destination is too small.</summary>
    public static bool TryHashData(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = destination.Length < HashSizeInBytes ? 0 : HashData(source, destination);
        return bytesWritten != 0;
    }

    /// <summary>The hash of the rest of the stream.</summary>
    public static byte[] HashData(Stream source) => HashArguments.HashStream(HashKind.Sha384, source);
}

/// <summary>SHA-512: a 64-byte hash.</summary>
public static class SHA512
{
    /// <summary>64: the hash's size in bytes.</summary>
    public const int HashSizeInBytes = 64;
    /// <summary>512: the hash's size in bits.</summary>
    public const int HashSizeInBits = 512;

    /// <summary>The hash of <paramref name="source"/>.</summary>
    public static byte[] HashData(byte[] source) => HashData(new ReadOnlySpan<byte>(source ?? throw new ArgumentNullException(nameof(source))));

    /// <summary>The hash of <paramref name="source"/>.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> source)
    {
        byte[] hash = new byte[HashSizeInBytes];
        HashState.Hash(HashKind.Sha512, source, hash);
        return hash;
    }

    /// <summary>Writes the hash of <paramref name="source"/> to destination, and returns its size (64).</summary>
    public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        HashState.Hash(HashKind.Sha512, source, destination.Slice(0, HashArguments.Check(destination, HashSizeInBytes)));
        return HashSizeInBytes;
    }

    /// <summary>Like HashData, but false instead of a throw when destination is too small.</summary>
    public static bool TryHashData(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = destination.Length < HashSizeInBytes ? 0 : HashData(source, destination);
        return bytesWritten != 0;
    }

    /// <summary>The hash of the rest of the stream.</summary>
    public static byte[] HashData(Stream source) => HashArguments.HashStream(HashKind.Sha512, source);
}

/// <summary>HMAC with SHA-256: a 32-byte message authentication code.</summary>
public static class HMACSHA256
{
    /// <summary>32: the MAC's size in bytes.</summary>
    public const int HashSizeInBytes = 32;
    /// <summary>256: the MAC's size in bits.</summary>
    public const int HashSizeInBits = 256;

    /// <summary>The MAC of <paramref name="source"/> under <paramref name="key"/>.</summary>
    public static byte[] HashData(byte[] key, byte[] source)
        => HashData(new ReadOnlySpan<byte>(key ?? throw new ArgumentNullException(nameof(key))),
                    new ReadOnlySpan<byte>(source ?? throw new ArgumentNullException(nameof(source))));

    /// <summary>The MAC of <paramref name="source"/> under <paramref name="key"/>.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source)
    {
        byte[] mac = new byte[HashSizeInBytes];
        HmacState.Mac(HashKind.Sha256, key, source, mac);
        return mac;
    }

    /// <summary>Writes the MAC to destination, and returns its size (32).</summary>
    public static int HashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        HmacState.Mac(HashKind.Sha256, key, source, destination.Slice(0, HashArguments.Check(destination, HashSizeInBytes)));
        return HashSizeInBytes;
    }

    /// <summary>Like HashData, but false instead of a throw when destination is too small.</summary>
    public static bool TryHashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = destination.Length < HashSizeInBytes ? 0 : HashData(key, source, destination);
        return bytesWritten != 0;
    }
}

/// <summary>HMAC with SHA-384: a 48-byte message authentication code.</summary>
public static class HMACSHA384
{
    /// <summary>48: the MAC's size in bytes.</summary>
    public const int HashSizeInBytes = 48;
    /// <summary>384: the MAC's size in bits.</summary>
    public const int HashSizeInBits = 384;

    /// <summary>The MAC of <paramref name="source"/> under <paramref name="key"/>.</summary>
    public static byte[] HashData(byte[] key, byte[] source)
        => HashData(new ReadOnlySpan<byte>(key ?? throw new ArgumentNullException(nameof(key))),
                    new ReadOnlySpan<byte>(source ?? throw new ArgumentNullException(nameof(source))));

    /// <summary>The MAC of <paramref name="source"/> under <paramref name="key"/>.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source)
    {
        byte[] mac = new byte[HashSizeInBytes];
        HmacState.Mac(HashKind.Sha384, key, source, mac);
        return mac;
    }

    /// <summary>Writes the MAC to destination, and returns its size (48).</summary>
    public static int HashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        HmacState.Mac(HashKind.Sha384, key, source, destination.Slice(0, HashArguments.Check(destination, HashSizeInBytes)));
        return HashSizeInBytes;
    }

    /// <summary>Like HashData, but false instead of a throw when destination is too small.</summary>
    public static bool TryHashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = destination.Length < HashSizeInBytes ? 0 : HashData(key, source, destination);
        return bytesWritten != 0;
    }
}

/// <summary>HMAC with SHA-512: a 64-byte message authentication code.</summary>
public static class HMACSHA512
{
    /// <summary>64: the MAC's size in bytes.</summary>
    public const int HashSizeInBytes = 64;
    /// <summary>512: the MAC's size in bits.</summary>
    public const int HashSizeInBits = 512;

    /// <summary>The MAC of <paramref name="source"/> under <paramref name="key"/>.</summary>
    public static byte[] HashData(byte[] key, byte[] source)
        => HashData(new ReadOnlySpan<byte>(key ?? throw new ArgumentNullException(nameof(key))),
                    new ReadOnlySpan<byte>(source ?? throw new ArgumentNullException(nameof(source))));

    /// <summary>The MAC of <paramref name="source"/> under <paramref name="key"/>.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source)
    {
        byte[] mac = new byte[HashSizeInBytes];
        HmacState.Mac(HashKind.Sha512, key, source, mac);
        return mac;
    }

    /// <summary>Writes the MAC to destination, and returns its size (64).</summary>
    public static int HashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        HmacState.Mac(HashKind.Sha512, key, source, destination.Slice(0, HashArguments.Check(destination, HashSizeInBytes)));
        return HashSizeInBytes;
    }

    /// <summary>Like HashData, but false instead of a throw when destination is too small.</summary>
    public static bool TryHashData(ReadOnlySpan<byte> key, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = destination.Length < HashSizeInBytes ? 0 : HashData(key, source, destination);
        return bytesWritten != 0;
    }
}

/// <summary>
/// A hash or HMAC of data that comes in pieces: append each, then get the
/// result. GetCurrentHash gives the result so far and lets appending go on,
/// which a TLS handshake's running hash needs.
/// </summary>
public sealed class IncrementalHash : IDisposable
{
    HashState _hash;
    HmacState _hmac;
    readonly bool _isHmac;
    readonly HashAlgorithmName _name;
    bool _disposed;

    IncrementalHash(HashAlgorithmName name, bool isHmac)
    {
        _name = name;
        _isHmac = isHmac;
    }

    /// <summary>A hash of the given algorithm: SHA256, SHA384 or SHA512.</summary>
    public static IncrementalHash CreateHash(HashAlgorithmName hashAlgorithm)
    {
        var hash = new IncrementalHash(hashAlgorithm, false);
        hash._hash.Initialize(hashAlgorithm.Kind);
        return hash;
    }

    /// <summary>An HMAC on the given hash, under <paramref name="key"/>.</summary>
    public static IncrementalHash CreateHMAC(HashAlgorithmName hashAlgorithm, byte[] key)
        => CreateHMAC(hashAlgorithm, new ReadOnlySpan<byte>(key ?? throw new ArgumentNullException(nameof(key))));

    /// <summary>An HMAC on the given hash, under <paramref name="key"/>.</summary>
    public static IncrementalHash CreateHMAC(HashAlgorithmName hashAlgorithm, ReadOnlySpan<byte> key)
    {
        var hash = new IncrementalHash(hashAlgorithm, true);
        hash._hmac.Initialize(hashAlgorithm.Kind, key);
        return hash;
    }

    /// <summary>Which hash this is.</summary>
    public HashAlgorithmName AlgorithmName => _name;

    /// <summary>The size of the result in bytes.</summary>
    public int HashLengthInBytes => _isHmac ? _hmac.Size : _hash.Size;

    /// <summary>Adds data to what's hashed.</summary>
    public void AppendData(byte[] data)
        => AppendData(new ReadOnlySpan<byte>(data ?? throw new ArgumentNullException(nameof(data))));

    /// <summary>Adds <paramref name="count"/> bytes of data from offset to what's hashed.</summary>
    public void AppendData(byte[] data, int offset, int count)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (offset < 0 || count < 0 || count > data.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(count));
        AppendData(new ReadOnlySpan<byte>(data, offset, count));
    }

    /// <summary>Adds data to what's hashed.</summary>
    public void AppendData(ReadOnlySpan<byte> data)
    {
        CheckOpen();
        if (_isHmac)
            _hmac.Append(data);
        else
            _hash.Append(data);
    }

    /// <summary>The result, and a fresh start (an HMAC keeps its key).</summary>
    public byte[] GetHashAndReset()
    {
        byte[] result = new byte[HashLengthInBytes];
        GetHashAndReset(result);
        return result;
    }

    /// <summary>Writes the result to destination, returns its size, and starts over.</summary>
    public int GetHashAndReset(Span<byte> destination)
    {
        CheckOpen();
        int size = HashArguments.Check(destination, HashLengthInBytes);
        if (_isHmac)
            _hmac.FinishAndReset(destination.Slice(0, size));
        else
        {
            _hash.Finish(destination.Slice(0, size));
            _hash.Initialize(_hash.Kind);
        }
        return size;
    }

    /// <summary>Like GetHashAndReset, but false instead of a throw when destination is too small.</summary>
    public bool TryGetHashAndReset(Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = destination.Length < HashLengthInBytes ? 0 : GetHashAndReset(destination);
        return bytesWritten != 0;
    }

    /// <summary>The result for what's been appended so far; appending goes on after it.</summary>
    public byte[] GetCurrentHash()
    {
        byte[] result = new byte[HashLengthInBytes];
        GetCurrentHash(result);
        return result;
    }

    /// <summary>Writes the result so far to destination and returns its size; appending goes on after it.</summary>
    public int GetCurrentHash(Span<byte> destination)
    {
        CheckOpen();
        int size = HashArguments.Check(destination, HashLengthInBytes);
        if (_isHmac)
            _hmac.Current(destination.Slice(0, size));
        else
        {
            HashState copy = _hash;
            copy.Finish(destination.Slice(0, size));
        }
        return size;
    }

    /// <summary>Like GetCurrentHash, but false instead of a throw when destination is too small.</summary>
    public bool TryGetCurrentHash(Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = destination.Length < HashLengthInBytes ? 0 : GetCurrentHash(destination);
        return bytesWritten != 0;
    }

    /// <summary>Erases the state (for an HMAC, what was derived from the key).</summary>
    public void Dispose()
    {
        _hash = default;
        _hmac = default;
        _disposed = true;
    }

    void CheckOpen()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(IncrementalHash));
    }
}

/// <summary>
/// HKDF (RFC 5869): keys from a secret. Extract makes a uniform key from
/// the input secret and a salt; Expand stretches that into as many bytes as
/// asked for, bound to an info string; DeriveKey does both.
/// </summary>
public static class HKDF
{
    /// <summary>The pseudorandom key from <paramref name="ikm"/> and <paramref name="salt"/> (none: zeros).</summary>
    public static byte[] Extract(HashAlgorithmName hashAlgorithmName, byte[] ikm, byte[] salt = null)
    {
        if (ikm == null)
            throw new ArgumentNullException(nameof(ikm));
        byte[] prk = new byte[HashState.SizeOf(hashAlgorithmName.Kind)];
        Extract(hashAlgorithmName, ikm, salt, prk);
        return prk;
    }

    /// <summary>Writes the pseudorandom key to prk and returns its size (the hash's).</summary>
    public static int Extract(HashAlgorithmName hashAlgorithmName, ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, Span<byte> prk)
    {
        HashKind kind = hashAlgorithmName.Kind;
        int size = HashState.SizeOf(kind);
        if (prk.Length < size)
            throw new ArgumentException("prk is smaller than the hash.", nameof(prk));
        // An empty salt is the same key as a hash-sized one of zeros: HMAC pads keys with zeros.
        HmacState.Mac(kind, salt, ikm, prk.Slice(0, size));
        return size;
    }

    /// <summary><paramref name="outputLength"/> bytes of key from <paramref name="prk"/>, bound to info.</summary>
    public static byte[] Expand(HashAlgorithmName hashAlgorithmName, byte[] prk, int outputLength, byte[] info = null)
    {
        if (prk == null)
            throw new ArgumentNullException(nameof(prk));
        if (outputLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        byte[] output = new byte[outputLength];
        Expand(hashAlgorithmName, prk, output, info);
        return output;
    }

    /// <summary>Fills output with key from <paramref name="prk"/>, bound to info. At most 255 hashes' worth.</summary>
    public static void Expand(HashAlgorithmName hashAlgorithmName, ReadOnlySpan<byte> prk, Span<byte> output, ReadOnlySpan<byte> info)
    {
        HashKind kind = hashAlgorithmName.Kind;
        int size = HashState.SizeOf(kind);
        if (prk.Length < size)
            throw new ArgumentException("prk is shorter than the hash.", nameof(prk));
        if (output.Length > 255 * size)
            throw new ArgumentOutOfRangeException(nameof(output), "HKDF gives at most 255 times the hash's size.");

        HmacState hmac = default;
        hmac.Initialize(kind, prk);
        Span<byte> block = stackalloc byte[64];
        block = block.Slice(0, size);
        Span<byte> counter = stackalloc byte[1];
        int done = 0;
        for (int i = 1; done < output.Length; i++)
        {
            // T(i) = HMAC(prk, T(i-1) | info | i), T(0) empty.
            if (i > 1)
                hmac.Append(block);
            hmac.Append(info);
            counter[0] = (byte)i;
            hmac.Append(counter);
            hmac.FinishAndReset(block);
            int n = Math.Min(size, output.Length - done);
            block.Slice(0, n).CopyTo(output.Slice(done));
            done += n;
        }
        CryptographicOperations.ZeroMemory(block);
    }

    /// <summary>Extract then Expand: <paramref name="outputLength"/> bytes of key from a secret.</summary>
    public static byte[] DeriveKey(HashAlgorithmName hashAlgorithmName, byte[] ikm, int outputLength, byte[] salt = null, byte[] info = null)
    {
        if (ikm == null)
            throw new ArgumentNullException(nameof(ikm));
        if (outputLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        byte[] output = new byte[outputLength];
        DeriveKey(hashAlgorithmName, ikm, output, salt, info);
        return output;
    }

    /// <summary>Extract then Expand, into output.</summary>
    public static void DeriveKey(HashAlgorithmName hashAlgorithmName, ReadOnlySpan<byte> ikm, Span<byte> output, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info)
    {
        Span<byte> prk = stackalloc byte[64];
        int size = Extract(hashAlgorithmName, ikm, salt, prk);
        Expand(hashAlgorithmName, prk.Slice(0, size), output, info);
        CryptographicOperations.ZeroMemory(prk);
    }
}
