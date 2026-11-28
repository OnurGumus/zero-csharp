// ChaCha20-Poly1305 (RFC 8439): encryption with authentication, with .NET's
// System.Security.Cryptography.ChaCha20Poly1305 API.
//
//   using var aead = new ChaCha20Poly1305(key);           // 32 bytes
//   aead.Encrypt(nonce, plaintext, ciphertext, tag);       // nonce: 12, tag: 16
//   aead.Decrypt(nonce, ciphertext, tag, plaintext);       // throws if anything was changed
//
// A nonce must never be used twice with the same key: that gives away both
// messages' XOR and lets anyone forge tags. Count messages (as TLS does) or
// take a random nonce for each (RandomNumberGenerator.Fill).
//
// This is TLS 1.3's TLS_CHACHA20_POLY1305_SHA256. It's additions, rotations
// and XORs on 32-bit words, and Poly1305 is multiplications of 26-bit limbs,
// so it runs in the same time whatever the key and data, on every target
// (no tables indexed by secrets, no branches on them), and fast without SIMD.
//
// What differs from .NET: Decrypt's tag mismatch throws
// AuthenticationTagMismatchException as in .NET, which ends the program
// unless a Catch.Run is waiting for it (Catch.cs).

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace System.Security.Cryptography;

/// <summary>Decrypt found that the ciphertext, tag, nonce or associated data isn't what was encrypted.</summary>
public class AuthenticationTagMismatchException : CryptographicException
{
    public AuthenticationTagMismatchException()
        : base("The computed authentication tag did not match the input authentication tag.") { }
    public AuthenticationTagMismatchException(string message) : base(message) { }
}

[InlineArray(16)]
internal struct UInt32x16 { uint _element; }

// The ChaCha20 stream cipher, IETF version: 32-byte key, 12-byte nonce, 32-bit block counter.
internal struct ChaCha20
{
    UInt32x16 _state;

    public void Initialize(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter)
    {
        _state[0] = 0x61707865; // "expand 32-byte k"
        _state[1] = 0x3320646e;
        _state[2] = 0x79622d32;
        _state[3] = 0x6b206574;
        for (int i = 0; i < 8; i++)
            _state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4 * i));
        _state[12] = counter;
        for (int i = 0; i < 3; i++)
            _state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4 * i));
    }

    // Writes the 64-byte block for the current counter, and moves the counter on.
    public void NextBlock(Span<byte> block)
    {
        UInt32x16 x = _state;
        for (int round = 0; round < 10; round++)
        {
            QuarterRound(ref x[0], ref x[4], ref x[8], ref x[12]);
            QuarterRound(ref x[1], ref x[5], ref x[9], ref x[13]);
            QuarterRound(ref x[2], ref x[6], ref x[10], ref x[14]);
            QuarterRound(ref x[3], ref x[7], ref x[11], ref x[15]);
            QuarterRound(ref x[0], ref x[5], ref x[10], ref x[15]);
            QuarterRound(ref x[1], ref x[6], ref x[11], ref x[12]);
            QuarterRound(ref x[2], ref x[7], ref x[8], ref x[13]);
            QuarterRound(ref x[3], ref x[4], ref x[9], ref x[14]);
        }
        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(4 * i), x[i] + _state[i]);
        _state[12]++;
        ((Span<uint>)x).Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d ^= a; d = BitOperations.RotateLeft(d, 16);
        c += d; b ^= c; b = BitOperations.RotateLeft(b, 12);
        a += b; d ^= a; d = BitOperations.RotateLeft(d, 8);
        c += d; b ^= c; b = BitOperations.RotateLeft(b, 7);
    }

    // destination = source XOR the key stream, from the current counter on.
    public void Xor(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        Span<byte> block = stackalloc byte[64];
        while (!source.IsEmpty)
        {
            NextBlock(block);
            int n = Math.Min(64, source.Length);
            for (int i = 0; i < n; i++)
                destination[i] = (byte)(source[i] ^ block[i]);
            source = source.Slice(n);
            destination = destination.Slice(n);
        }
        CryptographicOperations.ZeroMemory(block);
    }

    public void Clear() => ((Span<uint>)_state).Clear();
}

// Poly1305 (RFC 8439 section 2.5), with five 26-bit limbs and 64-bit
// products, which suits 32-bit targets as well as 64-bit ones (the
// "poly1305-donna-32" layout).
internal struct Poly1305
{
    uint _r0, _r1, _r2, _r3, _r4;
    uint _h0, _h1, _h2, _h3, _h4;
    uint _pad0, _pad1, _pad2, _pad3;
    Bytes16 _buffer;
    int _buffered;

    public void Initialize(ReadOnlySpan<byte> key)
    {
        // r, clamped: some bits cleared, as the standard asks.
        _r0 = BinaryPrimitives.ReadUInt32LittleEndian(key) & 0x3ffffff;
        _r1 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(3)) >> 2) & 0x3ffff03;
        _r2 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(6)) >> 4) & 0x3ffc0ff;
        _r3 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(9)) >> 6) & 0x3f03fff;
        _r4 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(12)) >> 8) & 0x00fffff;
        _h0 = _h1 = _h2 = _h3 = _h4 = 0;
        _pad0 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(16));
        _pad1 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(20));
        _pad2 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(24));
        _pad3 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(28));
        _buffered = 0;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_buffered > 0)
        {
            int n = Math.Min(16 - _buffered, data.Length);
            data.Slice(0, n).CopyTo(((Span<byte>)_buffer).Slice(_buffered));
            _buffered += n;
            data = data.Slice(n);
            if (_buffered < 16)
                return;
            Block(_buffer, 1u << 24);
            _buffered = 0;
        }
        while (data.Length >= 16)
        {
            Block(data.Slice(0, 16), 1u << 24);
            data = data.Slice(16);
        }
        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    // Zeros up to the next multiple of 16 bytes, as the AEAD construction pads.
    public void PadTo16()
    {
        if (_buffered == 0)
            return;
        Span<byte> zeros = stackalloc byte[16];
        zeros.Clear();
        Append(zeros.Slice(0, 16 - _buffered));
    }

    // h = (h + block + hibit * 2^128) * r mod 2^130 - 5
    void Block(ReadOnlySpan<byte> m, uint hibit)
    {
        uint h0 = _h0 + (BinaryPrimitives.ReadUInt32LittleEndian(m) & 0x3ffffff);
        uint h1 = _h1 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(3)) >> 2) & 0x3ffffff);
        uint h2 = _h2 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(6)) >> 4) & 0x3ffffff);
        uint h3 = _h3 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(9)) >> 6) & 0x3ffffff);
        uint h4 = _h4 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(12)) >> 8) | hibit);

        ulong r0 = _r0, r1 = _r1, r2 = _r2, r3 = _r3, r4 = _r4;
        ulong s1 = r1 * 5, s2 = r2 * 5, s3 = r3 * 5, s4 = r4 * 5;

        ulong d0 = h0 * r0 + h1 * s4 + h2 * s3 + h3 * s2 + h4 * s1;
        ulong d1 = h0 * r1 + h1 * r0 + h2 * s4 + h3 * s3 + h4 * s2;
        ulong d2 = h0 * r2 + h1 * r1 + h2 * r0 + h3 * s4 + h4 * s3;
        ulong d3 = h0 * r3 + h1 * r2 + h2 * r1 + h3 * r0 + h4 * s4;
        ulong d4 = h0 * r4 + h1 * r3 + h2 * r2 + h3 * r1 + h4 * r0;

        ulong c = d0 >> 26; h0 = (uint)d0 & 0x3ffffff;
        d1 += c; c = d1 >> 26; h1 = (uint)d1 & 0x3ffffff;
        d2 += c; c = d2 >> 26; h2 = (uint)d2 & 0x3ffffff;
        d3 += c; c = d3 >> 26; h3 = (uint)d3 & 0x3ffffff;
        d4 += c; c = d4 >> 26; h4 = (uint)d4 & 0x3ffffff;
        h0 += (uint)c * 5;
        uint carry = h0 >> 26; h0 &= 0x3ffffff;
        h1 += carry;

        _h0 = h0; _h1 = h1; _h2 = h2; _h3 = h3; _h4 = h4;
    }

    // Writes the 16-byte tag; the state is used up.
    public void Finish(Span<byte> tag)
    {
        if (_buffered > 0)
        {
            // The last, short block: a 1 after it, then zeros, and no 2^128 bit.
            Span<byte> last = stackalloc byte[16];
            last.Clear();
            ((ReadOnlySpan<byte>)_buffer).Slice(0, _buffered).CopyTo(last);
            last[_buffered] = 1;
            Block(last, 0);
        }

        uint h0 = _h0, h1 = _h1, h2 = _h2, h3 = _h3, h4 = _h4;
        uint c;
        c = h1 >> 26; h1 &= 0x3ffffff;
        h2 += c; c = h2 >> 26; h2 &= 0x3ffffff;
        h3 += c; c = h3 >> 26; h3 &= 0x3ffffff;
        h4 += c; c = h4 >> 26; h4 &= 0x3ffffff;
        h0 += c * 5; c = h0 >> 26; h0 &= 0x3ffffff;
        h1 += c;

        // g = h + 5 - 2^130: if it doesn't go negative, h was >= p and g is h mod p.
        uint g0 = h0 + 5; c = g0 >> 26; g0 &= 0x3ffffff;
        uint g1 = h1 + c; c = g1 >> 26; g1 &= 0x3ffffff;
        uint g2 = h2 + c; c = g2 >> 26; g2 &= 0x3ffffff;
        uint g3 = h3 + c; c = g3 >> 26; g3 &= 0x3ffffff;
        uint g4 = h4 + c - (1u << 26);

        uint useG = (g4 >> 31) - 1; // all ones when g4 didn't go negative
        uint useH = ~useG;
        h0 = (h0 & useH) | (g0 & useG);
        h1 = (h1 & useH) | (g1 & useG);
        h2 = (h2 & useH) | (g2 & useG);
        h3 = (h3 & useH) | (g3 & useG);
        h4 = (h4 & useH) | (g4 & useG);

        // h mod 2^128, as four 32-bit words, plus the pad.
        uint w0 = h0 | (h1 << 26);
        uint w1 = (h1 >> 6) | (h2 << 20);
        uint w2 = (h2 >> 12) | (h3 << 14);
        uint w3 = (h3 >> 18) | (h4 << 8);

        ulong f = (ulong)w0 + _pad0;
        BinaryPrimitives.WriteUInt32LittleEndian(tag, (uint)f);
        f = (ulong)w1 + _pad1 + (f >> 32);
        BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(4), (uint)f);
        f = (ulong)w2 + _pad2 + (f >> 32);
        BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(8), (uint)f);
        f = (ulong)w3 + _pad3 + (f >> 32);
        BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(12), (uint)f);

        this = default;
    }
}

[InlineArray(16)]
internal struct Bytes16 { byte _element; }

[InlineArray(32)]
internal struct Bytes32 { byte _element; }

/// <summary>
/// ChaCha20-Poly1305 (RFC 8439): encrypts and authenticates, with a
/// 32-byte key, a 12-byte nonce that must not repeat under one key, and a
/// 16-byte tag.
/// </summary>
public sealed class ChaCha20Poly1305 : IDisposable
{
    /// <summary>32: the key's size in bytes.</summary>
    public const int KeySizeInBytes = 32;
    /// <summary>12: the nonce's size in bytes.</summary>
    public const int NonceSizeInBytes = 12;
    /// <summary>16: the tag's size in bytes.</summary>
    public const int TagSizeInBytes = 16;

    Bytes32 _key;

    /// <summary>Always true: it's plain C# on every target.</summary>
    public static bool IsSupported => true;

    /// <summary>16, the only tag size there is.</summary>
    public static int TagByteSize => TagSizeInBytes;

    /// <summary>A cipher with this 32-byte key.</summary>
    public ChaCha20Poly1305(byte[] key) : this(new ReadOnlySpan<byte>(key ?? throw new ArgumentNullException(nameof(key)))) { }

    /// <summary>A cipher with this 32-byte key.</summary>
    public ChaCha20Poly1305(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySizeInBytes)
            throw new CryptographicException("The key must be 32 bytes.");
        key.CopyTo(_key);
    }

    /// <summary>
    /// Encrypts plaintext into ciphertext (the same length) and writes the
    /// 16-byte tag, which covers the ciphertext and <paramref name="associatedData"/>.
    /// </summary>
    public void Encrypt(byte[] nonce, byte[] plaintext, byte[] ciphertext, byte[] tag, byte[] associatedData = null)
        => Encrypt(In(nonce, nameof(nonce)), In(plaintext, nameof(plaintext)),
                   Out(ciphertext, nameof(ciphertext)), Out(tag, nameof(tag)), associatedData);

    /// <summary>
    /// Encrypts plaintext into ciphertext (the same length) and writes the
    /// 16-byte tag, which covers the ciphertext and <paramref name="associatedData"/>.
    /// </summary>
    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext,
                        Span<byte> tag, ReadOnlySpan<byte> associatedData = default)
    {
        CheckSizes(nonce, plaintext.Length, ciphertext.Length, tag.Length);
        ChaCha20 chacha = default;
        Poly1305 poly = default;
        Start(nonce, ref chacha, ref poly);
        chacha.Xor(plaintext, ciphertext);
        Authenticate(ref poly, associatedData, ciphertext, tag);
        chacha.Clear();
    }

    /// <summary>
    /// Checks the tag, then decrypts ciphertext into plaintext. If the tag
    /// doesn't match (anything was changed, or a wrong key, nonce or
    /// associated data) it writes zeros and throws AuthenticationTagMismatchException.
    /// </summary>
    public void Decrypt(byte[] nonce, byte[] ciphertext, byte[] tag, byte[] plaintext, byte[] associatedData = null)
        => Decrypt(In(nonce, nameof(nonce)), In(ciphertext, nameof(ciphertext)),
                   In(tag, nameof(tag)), Out(plaintext, nameof(plaintext)), associatedData);

    /// <summary>
    /// Checks the tag, then decrypts ciphertext into plaintext. If the tag
    /// doesn't match (anything was changed, or a wrong key, nonce or
    /// associated data) it writes zeros and throws AuthenticationTagMismatchException.
    /// </summary>
    public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
                        Span<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        if (!TryDecrypt(nonce, ciphertext, tag, plaintext, associatedData))
            throw new AuthenticationTagMismatchException();
    }

    // Decrypt without the throw: false (and zeros in plaintext) when the tag doesn't match.
    internal bool TryDecrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
                             Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        CheckSizes(nonce, plaintext.Length, ciphertext.Length, tag.Length);
        ChaCha20 chacha = default;
        Poly1305 poly = default;
        Start(nonce, ref chacha, ref poly);
        Span<byte> expected = stackalloc byte[TagSizeInBytes];
        Authenticate(ref poly, associatedData, ciphertext, expected);
        bool match = CryptographicOperations.FixedTimeEquals(expected, tag);
        if (match)
            chacha.Xor(ciphertext, plaintext);
        else
            CryptographicOperations.ZeroMemory(plaintext);
        chacha.Clear();
        return match;
    }

    /// <summary>Erases the key.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_key);

    static ReadOnlySpan<byte> In(byte[] array, string name)
        => array ?? throw new ArgumentNullException(name);

    static Span<byte> Out(byte[] array, string name)
        => array ?? throw new ArgumentNullException(name);

    static void CheckSizes(ReadOnlySpan<byte> nonce, int plaintextLength, int ciphertextLength, int tagLength)
    {
        if (nonce.Length != NonceSizeInBytes)
            throw new ArgumentException("The nonce must be 12 bytes.", nameof(nonce));
        if (tagLength != TagSizeInBytes)
            throw new ArgumentException("The tag must be 16 bytes.", "tag");
        if (plaintextLength != ciphertextLength)
            throw new ArgumentException("The plaintext and ciphertext must be the same length.", "ciphertext");
    }

    // The Poly1305 key is the first 32 bytes of block 0; the message starts at block 1.
    void Start(ReadOnlySpan<byte> nonce, ref ChaCha20 chacha, ref Poly1305 poly)
    {
        chacha.Initialize(_key, nonce, 0);
        Span<byte> block = stackalloc byte[64];
        chacha.NextBlock(block);
        poly.Initialize(block.Slice(0, 32));
        CryptographicOperations.ZeroMemory(block);
    }

    // The tag: associated data, padded; ciphertext, padded; the two lengths.
    static void Authenticate(ref Poly1305 poly, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext, Span<byte> tag)
    {
        poly.Append(associatedData);
        poly.PadTo16();
        poly.Append(ciphertext);
        poly.PadTo16();
        Span<byte> lengths = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(lengths, (ulong)associatedData.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(lengths.Slice(8), (ulong)ciphertext.Length);
        poly.Append(lengths);
        poly.Finish(tag);
    }
}
