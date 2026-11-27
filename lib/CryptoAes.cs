// AES-GCM (NIST SP 800-38D): AES encryption with authentication, with .NET's
// System.Security.Cryptography.AesGcm API.
//
//   using var aes = new AesGcm(key, tagSizeInBytes: 16);  // key: 16, 24 or 32 bytes
//   aes.Encrypt(nonce, plaintext, ciphertext, tag);         // nonce: 12 bytes
//   aes.Decrypt(nonce, ciphertext, tag, plaintext);         // throws if anything was changed
//
// As with ChaCha20Poly1305, a nonce must never be used twice with one key.
// This is TLS 1.3's TLS_AES_128_GCM_SHA256 and TLS_AES_256_GCM_SHA384, the
// suites every server offers.
//
// AES is usually a table lookup per byte, and which table entries a program
// reads shows in the cache, where code on the same machine can time it and
// learn the key. This one has no tables: it's "bitsliced" (the design of
// Thomas Pornin's BearSSL aes_ct64). Four blocks travel together as eight
// 64-bit words, one per bit of each byte, and the S-box is a circuit of
// 113 ANDs and XORs (Boyar and Peralta) applied to all 64 bytes at once.
// GHASH, GCM's authenticator, multiplies without carries using ordinary
// multiplications on bits spread four apart, so carries fall into gaps and
// are masked away (BearSSL's ghash_ctmul64). The time depends only on the
// lengths. There are no hardware AES instructions here: zero mode has no
// run-time CPU detection.
//
// AesCt64 and Ghash are ports of BearSSL's aes_ct64 and ghash_ctmul64,
// under its MIT license (also in lib/THIRD-PARTY-NOTICES.txt):
//
//   Copyright (c) 2016 Thomas Pornin <pornin@bolet.org>
//
//   Permission is hereby granted, free of charge, to any person obtaining
//   a copy of this software and associated documentation files (the
//   "Software"), to deal in the Software without restriction, including
//   without limitation the rights to use, copy, modify, merge, publish,
//   distribute, sublicense, and/or sell copies of the Software, and to
//   permit persons to whom the Software is furnished to do so, subject to
//   the following conditions:
//
//   The above copyright notice and this permission notice shall be
//   included in all copies or substantial portions of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
//   EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
//   MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
//   NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS
//   BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN
//   ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
//   CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//   SOFTWARE.
//
// What differs from .NET: Decrypt's tag mismatch throws
// AuthenticationTagMismatchException, which ends the program unless a
// Catch.Run is waiting for it (Catch.cs). Only 12-byte nonces, as in .NET.

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;

namespace System.Security.Cryptography;

// Shift counts the compiler can't see. Shifting by a constant 8 and 16 in
// the patterns below is a byte swap to clang, which emits bswap: a 486
// instruction, and DOS programs must run on a 386 (build.sh checks). A
// count read from a field that is never written is the same number at run
// time, but not a pattern clang knows. The cost is one load per call.
internal static class Shifts
{
    static int s_zero;

    public static int Eight => 8 + Volatile.Read(ref s_zero);
    public static int Sixteen => 16 + Volatile.Read(ref s_zero);
}

[InlineArray(8)]
internal struct UInt64x8Block { ulong _element; }

[InlineArray(30)]
internal struct UInt64x30 { ulong _element; }

// AES encryption, four blocks at a time, in constant time (BearSSL's aes_ct64).
internal struct AesCt64
{
    UInt64x30 _compressedKey; // (rounds + 1) * 2 words
    int _rounds;

    public int Rounds => _rounds;

    public void Initialize(ReadOnlySpan<byte> key)
    {
        _rounds = key.Length == 16 ? 10 : key.Length == 24 ? 12 : 14;
        int nk = key.Length / 4;
        int nkf = (_rounds + 1) * 4;
        Span<uint> words = stackalloc uint[60];
        for (int i = 0; i < nk; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4 * i));
        uint tmp = words[nk - 1];
        ReadOnlySpan<byte> rcon = [0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36];
        for (int i = nk, j = 0, k = 0; i < nkf; i++)
        {
            if (j == 0)
            {
                tmp = (tmp << 24) | (tmp >> 8);
                tmp = SubWord(tmp) ^ rcon[k];
            }
            else if (nk > 6 && j == 4)
                tmp = SubWord(tmp);
            tmp ^= words[i - nk];
            words[i] = tmp;
            if (++j == nk)
            {
                j = 0;
                k++;
            }
        }

        UInt64x8Block q = default;
        for (int i = 0, j = 0; i < nkf; i += 4, j += 2)
        {
            InterleaveIn(out q[0], out q[4], words.Slice(i, 4));
            q[1] = q[0]; q[2] = q[0]; q[3] = q[0];
            q[5] = q[4]; q[6] = q[4]; q[7] = q[4];
            Ortho(ref q);
            _compressedKey[j] = (q[0] & 0x1111111111111111) | (q[1] & 0x2222222222222222)
                              | (q[2] & 0x4444444444444444) | (q[3] & 0x8888888888888888);
            _compressedKey[j + 1] = (q[4] & 0x1111111111111111) | (q[5] & 0x2222222222222222)
                                  | (q[6] & 0x4444444444444444) | (q[7] & 0x8888888888888888);
        }
        words.Clear();
        ((Span<ulong>)q).Clear();
    }

    public void Clear() => this = default;

    // The round keys, spread from the compressed form: 8 words a round.
    void ExpandKey(Span<ulong> roundKeys)
    {
        int n = (_rounds + 1) * 2;
        for (int u = 0, v = 0; u < n; u++, v += 4)
        {
            ulong x0 = _compressedKey[u] & 0x1111111111111111;
            ulong x1 = (_compressedKey[u] & 0x2222222222222222) >> 1;
            ulong x2 = (_compressedKey[u] & 0x4444444444444444) >> 2;
            ulong x3 = (_compressedKey[u] & 0x8888888888888888) >> 3;
            roundKeys[v] = (x0 << 4) - x0;
            roundKeys[v + 1] = (x1 << 4) - x1;
            roundKeys[v + 2] = (x2 << 4) - x2;
            roundKeys[v + 3] = (x3 << 4) - x3;
        }
    }

    // Encrypts four 16-byte blocks of `blocks` in place (64 bytes).
    public void Encrypt4(Span<byte> blocks)
    {
        Span<ulong> roundKeys = stackalloc ulong[120];
        ExpandKey(roundKeys);
        Span<uint> w = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
            w[i] = BinaryPrimitives.ReadUInt32LittleEndian(blocks.Slice(4 * i));
        UInt64x8Block q = default;
        for (int i = 0; i < 4; i++)
            InterleaveIn(out q[i], out q[i + 4], w.Slice(4 * i, 4));
        Ortho(ref q);
        AddRoundKey(ref q, roundKeys);
        for (int u = 1; u < _rounds; u++)
        {
            Sbox(ref q);
            ShiftRows(ref q);
            MixColumns(ref q);
            AddRoundKey(ref q, roundKeys.Slice(8 * u));
        }
        Sbox(ref q);
        ShiftRows(ref q);
        AddRoundKey(ref q, roundKeys.Slice(8 * _rounds));
        Ortho(ref q);
        for (int i = 0; i < 4; i++)
            InterleaveOut(w.Slice(4 * i, 4), q[i], q[i + 4]);
        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(blocks.Slice(4 * i), w[i]);
        roundKeys.Clear();
        w.Clear();
        ((Span<ulong>)q).Clear();
    }

    static uint SubWord(uint x)
    {
        UInt64x8Block q = default;
        q[0] = x;
        Ortho(ref q);
        Sbox(ref q);
        Ortho(ref q);
        return (uint)q[0];
    }

    static void AddRoundKey(ref UInt64x8Block q, ReadOnlySpan<ulong> key)
    {
        for (int i = 0; i < 8; i++)
            q[i] ^= key[i];
    }

    static void ShiftRows(ref UInt64x8Block q)
    {
        int eight = Shifts.Eight;
        for (int i = 0; i < 8; i++)
        {
            ulong x = q[i];
            q[i] = (x & 0x000000000000FFFF)
                 | ((x & 0x00000000FFF00000) >> 4)
                 | ((x & 0x00000000000F0000) << 12)
                 | ((x & 0x0000FF0000000000) >> eight)
                 | ((x & 0x000000FF00000000) << eight)
                 | ((x & 0xF000000000000000) >> 12)
                 | ((x & 0x0FFF000000000000) << 4);
        }
    }

    static ulong Rotr32(ulong x) => (x << 32) | (x >> 32);

    static void MixColumns(ref UInt64x8Block q)
    {
        ulong q0 = q[0], q1 = q[1], q2 = q[2], q3 = q[3], q4 = q[4], q5 = q[5], q6 = q[6], q7 = q[7];
        ulong r0 = (q0 >> 16) | (q0 << 48), r1 = (q1 >> 16) | (q1 << 48);
        ulong r2 = (q2 >> 16) | (q2 << 48), r3 = (q3 >> 16) | (q3 << 48);
        ulong r4 = (q4 >> 16) | (q4 << 48), r5 = (q5 >> 16) | (q5 << 48);
        ulong r6 = (q6 >> 16) | (q6 << 48), r7 = (q7 >> 16) | (q7 << 48);
        q[0] = q7 ^ r7 ^ r0 ^ Rotr32(q0 ^ r0);
        q[1] = q0 ^ r0 ^ q7 ^ r7 ^ r1 ^ Rotr32(q1 ^ r1);
        q[2] = q1 ^ r1 ^ r2 ^ Rotr32(q2 ^ r2);
        q[3] = q2 ^ r2 ^ q7 ^ r7 ^ r3 ^ Rotr32(q3 ^ r3);
        q[4] = q3 ^ r3 ^ q7 ^ r7 ^ r4 ^ Rotr32(q4 ^ r4);
        q[5] = q4 ^ r4 ^ r5 ^ Rotr32(q5 ^ r5);
        q[6] = q5 ^ r5 ^ r6 ^ Rotr32(q6 ^ r6);
        q[7] = q6 ^ r6 ^ r7 ^ Rotr32(q7 ^ r7);
    }

    // Moves between "one block's bytes per word" and "one bit of every byte per word".
    static void Ortho(ref UInt64x8Block q)
    {
        Swap(ref q[0], ref q[1], 0x5555555555555555, 0xAAAAAAAAAAAAAAAA, 1);
        Swap(ref q[2], ref q[3], 0x5555555555555555, 0xAAAAAAAAAAAAAAAA, 1);
        Swap(ref q[4], ref q[5], 0x5555555555555555, 0xAAAAAAAAAAAAAAAA, 1);
        Swap(ref q[6], ref q[7], 0x5555555555555555, 0xAAAAAAAAAAAAAAAA, 1);
        Swap(ref q[0], ref q[2], 0x3333333333333333, 0xCCCCCCCCCCCCCCCC, 2);
        Swap(ref q[1], ref q[3], 0x3333333333333333, 0xCCCCCCCCCCCCCCCC, 2);
        Swap(ref q[4], ref q[6], 0x3333333333333333, 0xCCCCCCCCCCCCCCCC, 2);
        Swap(ref q[5], ref q[7], 0x3333333333333333, 0xCCCCCCCCCCCCCCCC, 2);
        Swap(ref q[0], ref q[4], 0x0F0F0F0F0F0F0F0F, 0xF0F0F0F0F0F0F0F0, 4);
        Swap(ref q[1], ref q[5], 0x0F0F0F0F0F0F0F0F, 0xF0F0F0F0F0F0F0F0, 4);
        Swap(ref q[2], ref q[6], 0x0F0F0F0F0F0F0F0F, 0xF0F0F0F0F0F0F0F0, 4);
        Swap(ref q[3], ref q[7], 0x0F0F0F0F0F0F0F0F, 0xF0F0F0F0F0F0F0F0, 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Swap(ref ulong x, ref ulong y, ulong low, ulong high, int shift)
    {
        ulong a = x, b = y;
        x = (a & low) | ((b & low) << shift);
        y = ((a & high) >> shift) | (b & high);
    }

    static void InterleaveIn(out ulong q0, out ulong q1, ReadOnlySpan<uint> w)
    {
        ulong x0 = w[0], x1 = w[1], x2 = w[2], x3 = w[3];
        x0 |= x0 << 16; x1 |= x1 << 16; x2 |= x2 << 16; x3 |= x3 << 16;
        x0 &= 0x0000FFFF0000FFFF; x1 &= 0x0000FFFF0000FFFF;
        x2 &= 0x0000FFFF0000FFFF; x3 &= 0x0000FFFF0000FFFF;
        x0 |= x0 << 8; x1 |= x1 << 8; x2 |= x2 << 8; x3 |= x3 << 8;
        x0 &= 0x00FF00FF00FF00FF; x1 &= 0x00FF00FF00FF00FF;
        x2 &= 0x00FF00FF00FF00FF; x3 &= 0x00FF00FF00FF00FF;
        q0 = x0 | (x2 << 8);
        q1 = x1 | (x3 << 8);
    }

    static void InterleaveOut(Span<uint> w, ulong q0, ulong q1)
    {
        ulong x0 = q0 & 0x00FF00FF00FF00FF, x1 = q1 & 0x00FF00FF00FF00FF;
        ulong x2 = (q0 >> 8) & 0x00FF00FF00FF00FF, x3 = (q1 >> 8) & 0x00FF00FF00FF00FF;
        x0 |= x0 >> 8; x1 |= x1 >> 8; x2 |= x2 >> 8; x3 |= x3 >> 8;
        x0 &= 0x0000FFFF0000FFFF; x1 &= 0x0000FFFF0000FFFF;
        x2 &= 0x0000FFFF0000FFFF; x3 &= 0x0000FFFF0000FFFF;
        w[0] = (uint)x0 | (uint)(x0 >> 16);
        w[1] = (uint)x1 | (uint)(x1 >> 16);
        w[2] = (uint)x2 | (uint)(x2 >> 16);
        w[3] = (uint)x3 | (uint)(x3 >> 16);
    }

    // The AES S-box on all 64 bytes at once: Boyar and Peralta's circuit
    // (a linear layer, 32 ANDs, another linear layer), as in BearSSL.
    static void Sbox(ref UInt64x8Block q)
    {
        ulong x0 = q[7], x1 = q[6], x2 = q[5], x3 = q[4], x4 = q[3], x5 = q[2], x6 = q[1], x7 = q[0];

        ulong y14 = x3 ^ x5, y13 = x0 ^ x6, y9 = x0 ^ x3, y8 = x0 ^ x5, t0 = x1 ^ x2;
        ulong y1 = t0 ^ x7, y4 = y1 ^ x3, y12 = y13 ^ y14, y2 = y1 ^ x0, y5 = y1 ^ x6;
        ulong y3 = y5 ^ y8, t1 = x4 ^ y12, y15 = t1 ^ x5, y20 = t1 ^ x1, y6 = y15 ^ x7;
        ulong y10 = y15 ^ t0, y11 = y20 ^ y9, y7 = x7 ^ y11, y17 = y10 ^ y11, y19 = y10 ^ y8;
        ulong y16 = t0 ^ y11, y21 = y13 ^ y16, y18 = x0 ^ y16;

        ulong t2 = y12 & y15, t3 = y3 & y6, t4 = t3 ^ t2, t5 = y4 & x7, t6 = t5 ^ t2;
        ulong t7 = y13 & y16, t8 = y5 & y1, t9 = t8 ^ t7, t10 = y2 & y7, t11 = t10 ^ t7;
        ulong t12 = y9 & y11, t13 = y14 & y17, t14 = t13 ^ t12, t15 = y8 & y10, t16 = t15 ^ t12;
        ulong t17 = t4 ^ t14, t18 = t6 ^ t16, t19 = t9 ^ t14, t20 = t11 ^ t16;
        ulong t21 = t17 ^ y20, t22 = t18 ^ y19, t23 = t19 ^ y21, t24 = t20 ^ y18;
        ulong t25 = t21 ^ t22, t26 = t21 & t23, t27 = t24 ^ t26, t28 = t25 & t27, t29 = t28 ^ t22;
        ulong t30 = t23 ^ t24, t31 = t22 ^ t26, t32 = t31 & t30, t33 = t32 ^ t24, t34 = t23 ^ t33;
        ulong t35 = t27 ^ t33, t36 = t24 & t35, t37 = t36 ^ t34, t38 = t27 ^ t36, t39 = t29 & t38;
        ulong t40 = t25 ^ t39, t41 = t40 ^ t37, t42 = t29 ^ t33, t43 = t29 ^ t40, t44 = t33 ^ t37;
        ulong t45 = t42 ^ t41;
        ulong z0 = t44 & y15, z1 = t37 & y6, z2 = t33 & x7, z3 = t43 & y16, z4 = t40 & y1;
        ulong z5 = t29 & y7, z6 = t42 & y11, z7 = t45 & y17, z8 = t41 & y10, z9 = t44 & y12;
        ulong z10 = t37 & y3, z11 = t33 & y4, z12 = t43 & y13, z13 = t40 & y5, z14 = t29 & y2;
        ulong z15 = t42 & y9, z16 = t45 & y14, z17 = t41 & y8;

        ulong t46 = z15 ^ z16, t47 = z10 ^ z11, t48 = z5 ^ z13, t49 = z9 ^ z10, t50 = z2 ^ z12;
        ulong t51 = z2 ^ z5, t52 = z7 ^ z8, t53 = z0 ^ z3, t54 = z6 ^ z7, t55 = z16 ^ z17;
        ulong t56 = z12 ^ t48, t57 = t50 ^ t53, t58 = z4 ^ t46, t59 = z3 ^ t54, t60 = t46 ^ t57;
        ulong t61 = z14 ^ t57, t62 = t52 ^ t58, t63 = t49 ^ t58, t64 = z4 ^ t59, t65 = t61 ^ t62;
        ulong t66 = z1 ^ t63;
        ulong s0 = t59 ^ t63, s6 = t56 ^ ~t62, s7 = t48 ^ ~t60, t67 = t64 ^ t65;
        ulong s3 = t53 ^ t66, s4 = t51 ^ t66, s5 = t47 ^ t65, s1 = t64 ^ ~s3, s2 = t55 ^ ~t67;

        q[7] = s0; q[6] = s1; q[5] = s2; q[4] = s3;
        q[3] = s4; q[2] = s5; q[1] = s6; q[0] = s7;
    }
}

// GHASH (SP 800-38D): y = (y XOR block) * h in GF(2^128), in constant time
// (BearSSL's ghash_ctmul64).
internal struct Ghash
{
    ulong _h0, _h1, _h0r, _h1r, _h2, _h2r;
    ulong _y0, _y1;
    Bytes16 _buffer;
    int _buffered;

    public void Initialize(ReadOnlySpan<byte> h)
    {
        _h1 = BinaryPrimitives.ReadUInt64BigEndian(h);
        _h0 = BinaryPrimitives.ReadUInt64BigEndian(h.Slice(8));
        _h0r = Reverse(_h0, Shifts.Eight, Shifts.Sixteen);
        _h1r = Reverse(_h1, Shifts.Eight, Shifts.Sixteen);
        _h2 = _h0 ^ _h1;
        _h2r = _h0r ^ _h1r;
        _y0 = _y1 = 0;
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
            Block(_buffer);
            _buffered = 0;
        }
        while (data.Length >= 16)
        {
            Block(data.Slice(0, 16));
            data = data.Slice(16);
        }
        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    // Zeros up to the next 16 bytes, as GCM pads the associated data and the ciphertext.
    public void PadTo16()
    {
        if (_buffered == 0)
            return;
        ((Span<byte>)_buffer).Slice(_buffered).Clear();
        Block(_buffer);
        _buffered = 0;
    }

    public void Finish(Span<byte> output)
    {
        PadTo16();
        BinaryPrimitives.WriteUInt64BigEndian(output, _y1);
        BinaryPrimitives.WriteUInt64BigEndian(output.Slice(8), _y0);
        this = default;
    }

    void Block(ReadOnlySpan<byte> block)
    {
        ulong y1 = _y1 ^ BinaryPrimitives.ReadUInt64BigEndian(block);
        ulong y0 = _y0 ^ BinaryPrimitives.ReadUInt64BigEndian(block.Slice(8));
        int eight = Shifts.Eight, sixteen = Shifts.Sixteen;
        ulong y0r = Reverse(y0, eight, sixteen), y1r = Reverse(y1, eight, sixteen);
        ulong y2 = y0 ^ y1, y2r = y0r ^ y1r;

        // Karatsuba, on the values and their bit reversals: a product's low
        // half from the first, its high half from the second.
        ulong z0 = Multiply(y0, _h0), z1 = Multiply(y1, _h1), z2 = Multiply(y2, _h2);
        ulong z0h = Multiply(y0r, _h0r), z1h = Multiply(y1r, _h1r), z2h = Multiply(y2r, _h2r);
        z2 ^= z0 ^ z1;
        z2h ^= z0h ^ z1h;
        z0h = Reverse(z0h, eight, sixteen) >> 1;
        z1h = Reverse(z1h, eight, sixteen) >> 1;
        z2h = Reverse(z2h, eight, sixteen) >> 1;

        ulong v0 = z0, v1 = z0h ^ z2, v2 = z1 ^ z2h, v3 = z1h;
        v3 = (v3 << 1) | (v2 >> 63);
        v2 = (v2 << 1) | (v1 >> 63);
        v1 = (v1 << 1) | (v0 >> 63);
        v0 <<= 1;

        // Reduce modulo x^128 + x^7 + x^2 + x + 1 (GCM's bit order).
        v2 ^= v0 ^ (v0 >> 1) ^ (v0 >> 2) ^ (v0 >> 7);
        v1 ^= (v0 << 63) ^ (v0 << 62) ^ (v0 << 57);
        v3 ^= v1 ^ (v1 >> 1) ^ (v1 >> 2) ^ (v1 >> 7);
        v2 ^= (v1 << 63) ^ (v1 << 62) ^ (v1 << 57);
        _y0 = v2;
        _y1 = v3;
    }

    // The low 64 bits of the carry-less product of x and y. Each operand
    // keeps every fourth bit in each of four parts; in an integer product of
    // two parts the carries land in the three bits between, masked away.
    static ulong Multiply(ulong x, ulong y)
    {
        ulong x0 = x & 0x1111111111111111, x1 = x & 0x2222222222222222;
        ulong x2 = x & 0x4444444444444444, x3 = x & 0x8888888888888888;
        ulong y0 = y & 0x1111111111111111, y1 = y & 0x2222222222222222;
        ulong y2 = y & 0x4444444444444444, y3 = y & 0x8888888888888888;
        ulong z0 = (x0 * y0) ^ (x1 * y3) ^ (x2 * y2) ^ (x3 * y1);
        ulong z1 = (x0 * y1) ^ (x1 * y0) ^ (x2 * y3) ^ (x3 * y2);
        ulong z2 = (x0 * y2) ^ (x1 * y1) ^ (x2 * y0) ^ (x3 * y3);
        ulong z3 = (x0 * y3) ^ (x1 * y2) ^ (x2 * y1) ^ (x3 * y0);
        return (z0 & 0x1111111111111111) | (z1 & 0x2222222222222222)
             | (z2 & 0x4444444444444444) | (z3 & 0x8888888888888888);
    }

    // The 64 bits in reverse order. eight and sixteen are 8 and 16 (see Shifts).
    static ulong Reverse(ulong x, int eight, int sixteen)
    {
        x = ((x & 0x5555555555555555) << 1) | ((x >> 1) & 0x5555555555555555);
        x = ((x & 0x3333333333333333) << 2) | ((x >> 2) & 0x3333333333333333);
        x = ((x & 0x0F0F0F0F0F0F0F0F) << 4) | ((x >> 4) & 0x0F0F0F0F0F0F0F0F);
        x = ((x & 0x00FF00FF00FF00FF) << eight) | ((x >> eight) & 0x00FF00FF00FF00FF);
        x = ((x & 0x0000FFFF0000FFFF) << sixteen) | ((x >> sixteen) & 0x0000FFFF0000FFFF);
        return (x << 32) | (x >> 32);
    }
}

/// <summary>
/// AES-GCM: encrypts and authenticates with AES (16-, 24- or 32-byte key),
/// a 12-byte nonce that must not repeat under one key, and a tag of 12 to 16 bytes.
/// </summary>
public sealed class AesGcm : IDisposable
{
    /// <summary>12: the only nonce size.</summary>
    public const int NonceSizeInBytes = 12;

    AesCt64 _aes;
    Bytes16 _h;           // the hash key: AES of 16 zero bytes
    readonly int _tagSize;

    /// <summary>Always true: it's plain C# on every target.</summary>
    public static bool IsSupported => true;

    /// <summary>The tag size Encrypt writes and Decrypt expects.</summary>
    public int? TagSizeInBytes => _tagSize;

    /// <summary>A cipher with this key (16, 24 or 32 bytes) and tag size (12 to 16 bytes).</summary>
    public AesGcm(byte[] key, int tagSizeInBytes)
        : this(new ReadOnlySpan<byte>(key ?? throw new ArgumentNullException(nameof(key))), tagSizeInBytes) { }

    /// <summary>A cipher with this key (16, 24 or 32 bytes) and tag size (12 to 16 bytes).</summary>
    public AesGcm(ReadOnlySpan<byte> key, int tagSizeInBytes)
    {
        if (key.Length != 16 && key.Length != 24 && key.Length != 32)
            throw new ArgumentException("The key must be 16, 24 or 32 bytes.", nameof(key));
        if (tagSizeInBytes < 12 || tagSizeInBytes > 16)
            throw new ArgumentException("The tag must be 12 to 16 bytes.", nameof(tagSizeInBytes));
        _tagSize = tagSizeInBytes;
        _aes.Initialize(key);
        Span<byte> blocks = stackalloc byte[64];
        blocks.Clear();
        _aes.Encrypt4(blocks);
        blocks.Slice(0, 16).CopyTo(_h);
        CryptographicOperations.ZeroMemory(blocks);
    }

    /// <summary>
    /// Encrypts plaintext into ciphertext (the same length) and writes the
    /// tag, which covers the ciphertext and <paramref name="associatedData"/>.
    /// </summary>
    public void Encrypt(byte[] nonce, byte[] plaintext, byte[] ciphertext, byte[] tag, byte[] associatedData = null)
        => Encrypt(In(nonce, nameof(nonce)), In(plaintext, nameof(plaintext)),
                   Out(ciphertext, nameof(ciphertext)), Out(tag, nameof(tag)), associatedData);

    /// <summary>
    /// Encrypts plaintext into ciphertext (the same length) and writes the
    /// tag, which covers the ciphertext and <paramref name="associatedData"/>.
    /// </summary>
    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext,
                        Span<byte> tag, ReadOnlySpan<byte> associatedData = default)
    {
        CheckSizes(nonce, plaintext.Length, ciphertext.Length, tag.Length);
        Counter(nonce, plaintext, ciphertext);
        Tag(nonce, associatedData, ciphertext, tag);
    }

    /// <summary>
    /// Checks the tag, then decrypts ciphertext into plaintext. If the tag
    /// doesn't match it writes zeros and throws AuthenticationTagMismatchException.
    /// </summary>
    public void Decrypt(byte[] nonce, byte[] ciphertext, byte[] tag, byte[] plaintext, byte[] associatedData = null)
        => Decrypt(In(nonce, nameof(nonce)), In(ciphertext, nameof(ciphertext)),
                   In(tag, nameof(tag)), Out(plaintext, nameof(plaintext)), associatedData);

    /// <summary>
    /// Checks the tag, then decrypts ciphertext into plaintext. If the tag
    /// doesn't match it writes zeros and throws AuthenticationTagMismatchException.
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
        Span<byte> expected = stackalloc byte[16];
        expected = expected.Slice(0, _tagSize);
        Tag(nonce, associatedData, ciphertext, expected);
        bool match = CryptographicOperations.FixedTimeEquals(expected, tag);
        if (match)
            Counter(nonce, ciphertext, plaintext);
        else
            CryptographicOperations.ZeroMemory(plaintext);
        return match;
    }

    /// <summary>Erases the key.</summary>
    public void Dispose()
    {
        _aes.Clear();
        CryptographicOperations.ZeroMemory(_h);
    }

    static ReadOnlySpan<byte> In(byte[] array, string name) => array ?? throw new ArgumentNullException(name);

    static Span<byte> Out(byte[] array, string name) => array ?? throw new ArgumentNullException(name);

    void CheckSizes(ReadOnlySpan<byte> nonce, int plaintextLength, int ciphertextLength, int tagLength)
    {
        if (nonce.Length != NonceSizeInBytes)
            throw new ArgumentException("The nonce must be 12 bytes.", nameof(nonce));
        if (tagLength != _tagSize)
            throw new ArgumentException("The tag must be the size given to the constructor.", "tag");
        if (plaintextLength != ciphertextLength)
            throw new ArgumentException("The plaintext and ciphertext must be the same length.", "ciphertext");
    }

    // destination = source XOR AES(nonce | counter), the counter from 2 up
    // (1 is the tag's), four blocks at a time.
    void Counter(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        Span<byte> blocks = stackalloc byte[64];
        uint counter = 2;
        while (!source.IsEmpty)
        {
            for (int b = 0; b < 4; b++)
            {
                nonce.CopyTo(blocks.Slice(16 * b));
                BinaryPrimitives.WriteUInt32BigEndian(blocks.Slice(16 * b + 12), counter++);
            }
            _aes.Encrypt4(blocks);
            int n = Math.Min(64, source.Length);
            for (int i = 0; i < n; i++)
                destination[i] = (byte)(source[i] ^ blocks[i]);
            source = source.Slice(n);
            destination = destination.Slice(n);
        }
        CryptographicOperations.ZeroMemory(blocks);
    }

    // GHASH over the associated data, the ciphertext and their bit lengths,
    // XORed with AES(nonce | 1).
    void Tag(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext, Span<byte> tag)
    {
        Ghash ghash = default;
        ghash.Initialize(_h);
        ghash.Append(associatedData);
        ghash.PadTo16();
        ghash.Append(ciphertext);
        ghash.PadTo16();
        Span<byte> lengths = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(lengths, (ulong)associatedData.Length * 8);
        BinaryPrimitives.WriteUInt64BigEndian(lengths.Slice(8), (ulong)ciphertext.Length * 8);
        ghash.Append(lengths);
        Span<byte> sum = stackalloc byte[16];
        ghash.Finish(sum);

        Span<byte> blocks = stackalloc byte[64];
        blocks.Clear();
        nonce.CopyTo(blocks);
        blocks[15] = 1;
        _aes.Encrypt4(blocks);
        for (int i = 0; i < tag.Length; i++)
            tag[i] = (byte)(sum[i] ^ blocks[i]);
        CryptographicOperations.ZeroMemory(blocks);
    }
}
