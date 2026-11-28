// X25519 (RFC 7748): agreeing on a shared secret over an open channel.
//
//   Span<byte> mine = stackalloc byte[32], myPublic = stackalloc byte[32];
//   X25519.GeneratePrivateKey(mine);                 // 32 random bytes
//   X25519.GetPublicKey(mine, myPublic);             // send this
//   ... receive theirPublic ...
//   Span<byte> shared = stackalloc byte[32];
//   if (!X25519.TryGetSharedSecret(mine, theirPublic, shared))
//       ...                                          // their key was a bad one: stop
//   // derive keys from shared with HKDF; never use it directly as a key
//
// Each side does the same with its own private key and the other's public
// key, and both get the same 32 bytes, which nobody who only saw the public
// keys can work out. This is how TLS 1.3 (the x25519 group) agrees on its
// keys. On its own it doesn't tell you who's on the other end: anyone can
// send a public key, so something must authenticate it (TLS's certificates).
//
// .NET has no X25519 class (ECDiffieHellman has only the NIST curves), so
// this one is ours, on spans.
//
// The arithmetic is modulo 2^255 - 19, in ten limbs of 26 and 25 bits held in
// 64-bit integers (the layout of the ref10 and donna implementations), with
// no branches or table lookups on secret data: the ladder swaps with masks.
// About 2600 field multiplications a key.

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace System.Security.Cryptography;

/// <summary>X25519 key agreement (RFC 7748): 32-byte private keys, public keys and shared secrets.</summary>
public static class X25519
{
    /// <summary>32: the size of a private key, a public key and a shared secret.</summary>
    public const int KeySizeInBytes = 32;

    /// <summary>Fills privateKey (32 bytes) with random bytes from the system (RandomNumberGenerator).</summary>
    public static void GeneratePrivateKey(Span<byte> privateKey)
    {
        CheckSize(privateKey.Length, nameof(privateKey));
        RandomNumberGenerator.Fill(privateKey);
    }

    /// <summary>Writes the public key (32 bytes) for <paramref name="privateKey"/>.</summary>
    public static void GetPublicKey(ReadOnlySpan<byte> privateKey, Span<byte> publicKey)
    {
        CheckSize(privateKey.Length, nameof(privateKey));
        CheckSize(publicKey.Length, nameof(publicKey));
        Span<byte> basePoint = stackalloc byte[32];
        basePoint.Clear();
        basePoint[0] = 9;
        ScalarMultiply(privateKey, basePoint, publicKey);
    }

    /// <summary>
    /// Writes the secret shared with the owner of <paramref name="peerPublicKey"/>.
    /// False, with zeros in sharedSecret, when the peer's key is one of the
    /// few that give an all-zero result whatever the private key: a
    /// misbehaving peer, so stop (TLS 1.3 requires it).
    /// </summary>
    public static bool TryGetSharedSecret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey, Span<byte> sharedSecret)
    {
        CheckSize(privateKey.Length, nameof(privateKey));
        CheckSize(peerPublicKey.Length, nameof(peerPublicKey));
        CheckSize(sharedSecret.Length, nameof(sharedSecret));
        ScalarMultiply(privateKey, peerPublicKey, sharedSecret);
        int any = 0;
        for (int i = 0; i < 32; i++)
            any |= sharedSecret[i];
        return any != 0;
    }

    /// <summary>
    /// The X25519 function itself: scalar times the point with
    /// u-coordinate u, both 32 bytes, as RFC 7748 defines it (the scalar is
    /// clamped, u's top bit ignored). GetPublicKey and TryGetSharedSecret
    /// are this with the base point and with a check.
    /// </summary>
    public static void ScalarMultiply(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> u, Span<byte> result)
    {
        CheckSize(scalar.Length, nameof(scalar));
        CheckSize(u.Length, nameof(u));
        CheckSize(result.Length, nameof(result));

        Span<byte> k = stackalloc byte[32];
        scalar.CopyTo(k);
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;

        Field x1 = default, x2 = default, z2 = default, x3 = default, z3 = default;
        Field a = default, aa = default, b = default, bb = default, e = default;
        Field c = default, d = default, da = default, cb = default, t = default;
        Field.FromBytes(ref x1, u);
        x2[0] = 1;
        x3 = x1;
        z3[0] = 1;

        long swap = 0;
        for (int i = 254; i >= 0; i--)
        {
            long bit = (k[i >> 3] >> (i & 7)) & 1;
            swap ^= bit;
            Field.ConditionalSwap(ref x2, ref x3, swap);
            Field.ConditionalSwap(ref z2, ref z3, swap);
            swap = bit;

            Field.Add(ref a, x2, z2);           // A = x2 + z2
            Field.Square(ref aa, a);            // AA = A^2
            Field.Subtract(ref b, x2, z2);      // B = x2 - z2
            Field.Square(ref bb, b);            // BB = B^2
            Field.Subtract(ref e, aa, bb);      // E = AA - BB
            Field.Add(ref c, x3, z3);           // C = x3 + z3
            Field.Subtract(ref d, x3, z3);      // D = x3 - z3
            Field.Multiply(ref da, d, a);       // DA = D * A
            Field.Multiply(ref cb, c, b);       // CB = C * B
            Field.Add(ref t, da, cb);
            Field.Square(ref x3, t);            // x3 = (DA + CB)^2
            Field.Subtract(ref t, da, cb);
            Field.Square(ref t, t);
            Field.Multiply(ref z3, x1, t);      // z3 = x1 * (DA - CB)^2
            Field.Multiply(ref x2, aa, bb);     // x2 = AA * BB
            Field.MultiplySmall(ref t, e, 121665);
            Field.Add(ref t, aa, t);
            Field.Multiply(ref z2, e, t);       // z2 = E * (AA + a24 * E)
        }
        Field.ConditionalSwap(ref x2, ref x3, swap);
        Field.ConditionalSwap(ref z2, ref z3, swap);

        Field.Invert(ref t, z2);
        Field.Multiply(ref x2, x2, t);
        Field.ToBytes(result, x2);

        CryptographicOperations.ZeroMemory(k);
    }

    static void CheckSize(int length, string name)
    {
        if (length != KeySizeInBytes)
            throw new ArgumentException("X25519 keys and secrets are 32 bytes.", name);
    }

    // An element of the field of integers modulo p = 2^255 - 19, as
    // f[0] + f[1]*2^26 + f[2]*2^51 + f[3]*2^77 + ... + f[9]*2^230: even limbs
    // hold 26 bits, odd ones 25. Limbs may be negative or a little over size
    // between operations; Multiply and Square carry their results back to size.
    [InlineArray(10)]
    internal struct Field
    {
        long _limb;

        static int Bits(int i) => 26 - (i & 1);

        // Little-endian 32 bytes, top bit ignored (RFC 7748 asks for that).
        public static void FromBytes(ref Field f, ReadOnlySpan<byte> bytes)
        {
            Span<byte> padded = stackalloc byte[40];
            padded.Clear();
            bytes.Slice(0, 32).CopyTo(padded);
            padded[31] &= 127;
            int position = 0;
            for (int i = 0; i < 10; i++)
            {
                ulong window = BinaryPrimitives.ReadUInt64LittleEndian(padded.Slice(position >> 3));
                f[i] = (long)((window >> (position & 7)) & ((1UL << Bits(i)) - 1));
                position += Bits(i);
            }
        }

        // The unique value below p, little-endian.
        public static void ToBytes(Span<byte> bytes, in Field f)
        {
            Field h = f;
            // Twice: the first can leave limb 1 a little negative, which
            // the second borrows for. After it every limb is non-negative
            // (limb 1 may be one over its size) and 0 <= h < 2p.
            Carry(ref h);
            Carry(ref h);
            // q = 1 when h >= p, that is when h + 19 reaches 2^255: carry
            // the 19 through the limbs and see what comes out of the top.
            long q = 19;
            for (int i = 0; i < 10; i++)
                q = (h[i] + q) >> Bits(i);
            // h + 19q - q*2^255 = h - q*p.
            h[0] += 19 * q;
            for (int i = 0; i < 9; i++)
            {
                long carry = h[i] >> Bits(i);
                h[i + 1] += carry;
                h[i] -= carry << Bits(i);
            }
            h[9] &= (1L << 25) - 1; // drop the 2^255 that q stood for

            ulong accumulator = 0;
            int filled = 0, outIndex = 0;
            for (int i = 0; i < 10; i++)
            {
                accumulator |= (ulong)h[i] << filled;
                filled += Bits(i);
                while (filled >= 8)
                {
                    bytes[outIndex++] = (byte)accumulator;
                    accumulator >>= 8;
                    filled -= 8;
                }
            }
            bytes[outIndex] = (byte)accumulator; // the last 7 bits
        }

        // Brings every limb back to its size, the overflow of the top one
        // wrapping round to the bottom times 19 (2^255 = 19 mod p).
        static void Carry(ref Field h)
        {
            for (int i = 0; i < 9; i++)
            {
                long carry = h[i] >> Bits(i);
                h[i + 1] += carry;
                h[i] -= carry << Bits(i);
            }
            long top = h[9] >> 25;
            h[9] -= top << 25;
            h[0] += 19 * top;
            long c0 = h[0] >> 26;
            h[1] += c0;
            h[0] -= c0 << 26;
        }

        public static void Add(ref Field h, in Field f, in Field g)
        {
            for (int i = 0; i < 10; i++)
                h[i] = f[i] + g[i];
        }

        public static void Subtract(ref Field h, in Field f, in Field g)
        {
            for (int i = 0; i < 10; i++)
                h[i] = f[i] - g[i];
        }

        public static void MultiplySmall(ref Field h, in Field f, long n)
        {
            for (int i = 0; i < 10; i++)
                h[i] = f[i] * n;
            Carry(ref h);
        }

        // h = f * g. Limb i holds 2^ceil(25.5 i); the product of two odd
        // limbs lands half a bit short of its place, so it counts twice, and
        // what passes limb 9 wraps round times 19.
        public static void Multiply(ref Field h, in Field f, in Field g)
        {
            Field r = default;
            for (int i = 0; i < 10; i++)
            {
                long fi = f[i];
                long fi2 = fi * 2;
                for (int j = 0; j < 10; j++)
                {
                    long fij = (i & j & 1) != 0 ? fi2 : fi;
                    int k = i + j;
                    if (k < 10)
                        r[k] += fij * g[j];
                    else
                        r[k - 10] += fij * (19 * g[j]);
                }
            }
            Carry(ref r);
            h = r;
        }

        public static void Square(ref Field h, in Field f) => Multiply(ref h, f, f);

        // swap is 0 or 1: exchanges f and g when it's 1, the same work either way.
        public static void ConditionalSwap(ref Field f, ref Field g, long swap)
        {
            long mask = -swap;
            for (int i = 0; i < 10; i++)
            {
                long x = mask & (f[i] ^ g[i]);
                f[i] ^= x;
                g[i] ^= x;
            }
        }

        // h = z^(p-2) = 1/z, by the fixed exponent 2^255 - 21: the same
        // sequence of squarings and multiplications whatever z is.
        public static void Invert(ref Field h, in Field z)
        {
            Field result = default;
            result[0] = 1;
            Field power = z;
            // p - 2 = 2^255 - 21: bits 0..254, all ones except bits 2 and 4.
            for (int bit = 0; bit < 255; bit++)
            {
                if (bit != 2 && bit != 4)
                    Multiply(ref result, result, power);
                Square(ref power, power);
            }
            h = result;
        }
    }
}
