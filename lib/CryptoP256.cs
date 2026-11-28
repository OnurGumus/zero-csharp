// Key agreement on NIST P-256 (ECDH, SEC 1 3.3.1), in constant time, for
// TLS servers that want a secp256r1 key share instead of X25519.
//
//   P256Ecdh.GenerateKey(privateKey, publicKey);  // 32 bytes; 65: 04, x, y
//   P256Ecdh.TryGetSharedSecret(privateKey, theirPublicKey, secret); // 32: x of d·Q
//
// Unlike the signature checks (lib/CryptoEcdsa.cs), this works on a secret:
// the private key. So nothing here branches on it or indexes memory with it:
//
// - Field arithmetic modulo p on eight 32-bit limbs, in Montgomery form (the
//   representation CryptoEcdsa's Modulus uses), where every reduction
//   computes both candidates and picks one with a mask.
// - Points in projective coordinates (X:Y:Z), added with Renes, Costello and
//   Batina's complete formula for a = -3 (2015, algorithm 4): one formula
//   for every pair of points, the point at infinity and doubling included,
//   so there are no special cases to branch on.
// - The scalar multiplication is a Montgomery ladder over all 256 bits,
//   with the two points swapped by masks.
//
// The other end's point is public: it is checked (on the curve, coordinates
// below p) with the variable-time code. The result's inversion uses the
// fixed exponent p - 2.

using System;

namespace System.Security.Cryptography;

internal static class P256Ecdh
{
    const int L = 8;

    // p = 2^256 - 2^224 + 2^192 + 2^96 - 1, lowest limb first.
    static ReadOnlySpan<uint> P => [0xffffffff, 0xffffffff, 0xffffffff, 0, 0, 0, 1, 0xffffffff];

    // n, the group order.
    static ReadOnlySpan<uint> N => [0xfc632551, 0xf3b9cac2, 0xa7179e84, 0xbce6faad, 0xffffffff, 0xffffffff, 0, 0xffffffff];

    // -p⁻¹ mod 2^32 is 1, since p ≡ -1 mod 2^32.

    // ---- field arithmetic, constant time ----

    // r = a·b/R mod p. r may be a or b.
    static void Mul(Span<uint> r, ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        Span<uint> t = stackalloc uint[L + 2];
        t.Clear();
        ReadOnlySpan<uint> p = P;
        for (int i = 0; i < L; i++)
        {
            ulong carry = 0;
            uint bi = b[i];
            for (int j = 0; j < L; j++)
            {
                ulong s = t[j] + (ulong)a[j] * bi + carry;
                t[j] = (uint)s;
                carry = s >> 32;
            }
            ulong top = t[L] + carry;
            t[L] = (uint)top;
            t[L + 1] = (uint)(top >> 32);

            uint m = t[0]; // t[0]·(-p⁻¹) with -p⁻¹ = 1
            carry = (t[0] + (ulong)m * p[0]) >> 32;
            for (int j = 1; j < L; j++)
            {
                ulong s = t[j] + (ulong)m * p[j] + carry;
                t[j - 1] = (uint)s;
                carry = s >> 32;
            }
            top = t[L] + carry;
            t[L - 1] = (uint)top;
            t[L] = t[L + 1] + (uint)(top >> 32);
            t[L + 1] = 0;
        }
        ReduceOnce(r, t.Slice(0, L), t[L]);
    }

    // r = (high·2^256 + t) mod p for a value below 2p: subtract p, and keep
    // the difference unless that borrowed (all with masks).
    static void ReduceOnce(Span<uint> r, ReadOnlySpan<uint> t, uint high)
    {
        Span<uint> d = stackalloc uint[L];
        ReadOnlySpan<uint> p = P;
        long borrow = 0;
        for (int i = 0; i < L; i++)
        {
            long x = (long)t[i] - p[i] + borrow;
            d[i] = (uint)x;
            borrow = x >> 32;
        }
        // The subtraction borrowed from `high` if borrow is -1; t < p exactly when
        // it went below zero overall: high + borrow < 0.
        uint below = (uint)(((long)high + borrow) >> 63); // all ones: keep t
        for (int i = 0; i < L; i++)
            r[i] = (t[i] & below) | (d[i] & ~below);
    }

    static void Add(Span<uint> r, ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        Span<uint> s = stackalloc uint[L];
        ulong carry = 0;
        for (int i = 0; i < L; i++)
        {
            ulong x = (ulong)a[i] + b[i] + carry;
            s[i] = (uint)x;
            carry = x >> 32;
        }
        ReduceOnce(r, s, (uint)carry);
    }

    static void Sub(Span<uint> r, ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        Span<uint> d = stackalloc uint[L];
        long borrow = 0;
        for (int i = 0; i < L; i++)
        {
            long x = (long)a[i] - b[i] + borrow;
            d[i] = (uint)x;
            borrow = x >> 32;
        }
        // Add p back under a mask if it went below zero.
        uint mask = (uint)borrow; // 0 or all ones
        ReadOnlySpan<uint> p = P;
        ulong carry = 0;
        for (int i = 0; i < L; i++)
        {
            ulong x = (ulong)d[i] + (p[i] & mask) + carry;
            r[i] = (uint)x;
            carry = x >> 32;
        }
    }

    // ---- points, projective (X:Y:Z), Montgomery form; 3L limbs ----

    // r = a + b, Renes–Costello–Batina algorithm 4 (complete, a = -3). r may be a or b.
    static void PointAdd(Span<uint> r, ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, ReadOnlySpan<uint> curveB)
    {
        ReadOnlySpan<uint> x1 = a.Slice(0, L), y1 = a.Slice(L, L), z1 = a.Slice(2 * L, L);
        ReadOnlySpan<uint> x2 = b.Slice(0, L), y2 = b.Slice(L, L), z2 = b.Slice(2 * L, L);
        Span<uint> t0 = stackalloc uint[L], t1 = stackalloc uint[L], t2 = stackalloc uint[L];
        Span<uint> t3 = stackalloc uint[L], t4 = stackalloc uint[L];
        Span<uint> x3 = stackalloc uint[L], y3 = stackalloc uint[L], z3 = stackalloc uint[L];

        Mul(t0, x1, x2);
        Mul(t1, y1, y2);
        Mul(t2, z1, z2);
        Add(t3, x1, y1);
        Add(t4, x2, y2);
        Mul(t3, t3, t4);
        Add(t4, t0, t1);
        Sub(t3, t3, t4);
        Add(t4, y1, z1);
        Add(x3, y2, z2);
        Mul(t4, t4, x3);
        Add(x3, t1, t2);
        Sub(t4, t4, x3);
        Add(x3, x1, z1);
        Add(y3, x2, z2);
        Mul(x3, x3, y3);
        Add(y3, t0, t2);
        Sub(y3, x3, y3);
        Mul(z3, curveB, t2);
        Sub(x3, y3, z3);
        Add(z3, x3, x3);
        Add(x3, x3, z3);
        Sub(z3, t1, x3);
        Add(x3, t1, x3);
        Mul(y3, curveB, y3);
        Add(t1, t2, t2);
        Add(t2, t1, t2);
        Sub(y3, y3, t2);
        Sub(y3, y3, t0);
        Add(t1, y3, y3);
        Add(y3, t1, y3);
        Add(t1, t0, t0);
        Add(t0, t1, t0);
        Sub(t0, t0, t2);
        Mul(t1, t4, y3);
        Mul(t2, t0, y3);
        Mul(y3, x3, z3);
        Add(y3, y3, t2);
        Mul(x3, x3, t3);
        Sub(x3, x3, t1);
        Mul(z3, t4, z3);
        Mul(t1, t3, t0);
        Add(z3, z3, t1);

        x3.CopyTo(r);
        y3.CopyTo(r.Slice(L));
        z3.CopyTo(r.Slice(2 * L));
    }

    // Swaps a and b if swap is 1, without a branch.
    static void ConditionalSwap(Span<uint> a, Span<uint> b, uint swap)
    {
        uint mask = 0 - swap;
        for (int i = 0; i < a.Length; i++)
        {
            uint d = (a[i] ^ b[i]) & mask;
            a[i] ^= d;
            b[i] ^= d;
        }
    }

    // r = k·q (q affine, Montgomery form; r projective), Montgomery ladder.
    static void Multiply(Span<uint> r, ReadOnlySpan<byte> scalar, ReadOnlySpan<uint> q, Curve curve)
    {
        Span<uint> r0 = stackalloc uint[3 * L], r1 = stackalloc uint[3 * L];
        // r0 = the point at infinity (0 : 1 : 0); r1 = q.
        r0.Clear();
        curve.P.One.CopyTo(r0.Slice(L, L));
        q.Slice(0, 2 * L).CopyTo(r1);
        curve.P.One.CopyTo(r1.Slice(2 * L, L));
        ReadOnlySpan<uint> b = curve.B;
        uint previous = 0;
        for (int i = 0; i < 256; i++)
        {
            uint bit = (uint)(scalar[i >> 3] >> (7 - (i & 7))) & 1;
            ConditionalSwap(r0, r1, bit ^ previous);
            previous = bit;
            PointAdd(r1, r0, r1, b);
            PointAdd(r0, r0, r0, b);
        }
        ConditionalSwap(r0, r1, previous);
        r0.CopyTo(r);
    }

    // a^(p-2) = a⁻¹ (Montgomery form in and out); p - 2 is public, so the
    // square-and-multiply may branch on its bits.
    static void Invert(Span<uint> r, ReadOnlySpan<uint> a, Curve curve)
    {
        Span<uint> result = stackalloc uint[L], bas = stackalloc uint[L];
        curve.P.One.CopyTo(result);
        a.Slice(0, L).CopyTo(bas);
        ReadOnlySpan<uint> p = P;
        for (int bit = 255; bit >= 0; bit--)
        {
            Mul(result, result, result);
            uint e = p[bit >> 5] >> (bit & 31) & 1;
            if (bit < 32)
                e = (0xfffffffdu >> bit) & 1; // the low limb of p - 2
            if (e != 0)
                Mul(result, result, bas);
        }
        result.CopyTo(r);
    }

    // True (all ones) if the 32-byte big-endian scalar is in [1, n - 1], in constant time.
    static uint InRange(ReadOnlySpan<byte> scalar)
    {
        Span<uint> k = stackalloc uint[L];
        for (int i = 0; i < 32; i++)
            k[(31 - i) >> 2] |= (uint)scalar[i] << (8 * ((31 - i) & 3));
        ReadOnlySpan<uint> n = N;
        long borrow = 0;
        uint any = 0;
        for (int i = 0; i < L; i++)
        {
            long x = (long)k[i] - n[i] + borrow;
            borrow = x >> 32;
            any |= k[i];
        }
        uint belowN = (uint)borrow; // all ones if k < n
        uint nonZero = (uint)(0 - (long)((any | (0 - any)) >> 31)) ; // all ones if any != 0
        k.Clear();
        return belowN & nonZero;
    }

    /// <summary>A new private key (32 bytes, 1 to n - 1) and its public key (65 bytes: 04, x, y).</summary>
    internal static void GenerateKey(Span<byte> privateKey, Span<byte> publicKey)
    {
        // Rejection sampling: almost every 32 random bytes are below n.
        do
            RandomNumberGenerator.Fill(privateKey.Slice(0, 32));
        while (InRange(privateKey) == 0);
        if (!TryGetPublicKey(privateKey, publicKey))
            throw new CryptographicException("P256Ecdh: the public key came out as the point at infinity.");
    }

    /// <summary>The public key (04, x, y) of a private key in [1, n - 1]; false otherwise.</summary>
    internal static bool TryGetPublicKey(ReadOnlySpan<byte> privateKey, Span<byte> publicKey)
    {
        if (privateKey.Length != 32 || publicKey.Length < 65 || InRange(privateKey) == 0)
            return false;
        Curve curve = Curve.P256;
        Span<uint> point = stackalloc uint[3 * L];
        Multiply(point, privateKey, curve.G, curve);
        bool ok = ToAffine(point, publicKey.Slice(1, 32), publicKey.Slice(33, 32), curve);
        publicKey[0] = 4;
        point.Clear();
        return ok;
    }

    /// <summary>
    /// The shared secret: the x coordinate of privateKey·theirs, 32 bytes. False if their
    /// key isn't an uncompressed point on P-256, or the private key is out of range.
    /// </summary>
    internal static bool TryGetSharedSecret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> theirPublicKey, Span<byte> secret)
    {
        secret.Clear();
        if (privateKey.Length != 32 || secret.Length < 32 || theirPublicKey.Length != 65 || theirPublicKey[0] != 4)
            return false;
        Curve curve = Curve.P256;
        Span<uint> q = stackalloc uint[2 * L];
        if (!curve.TryLoadPoint(theirPublicKey.Slice(1, 32), theirPublicKey.Slice(33, 32), q))
            return false;
        if (InRange(privateKey) == 0)
            return false;
        Span<uint> point = stackalloc uint[3 * L];
        Multiply(point, privateKey, q, curve);
        Span<byte> y = stackalloc byte[32];
        bool ok = ToAffine(point, secret.Slice(0, 32), y, curve);
        point.Clear();
        return ok;
    }

    // (X/Z, Y/Z) as big-endian bytes; false for the point at infinity.
    static bool ToAffine(ReadOnlySpan<uint> point, Span<byte> x, Span<byte> y, Curve curve)
    {
        ReadOnlySpan<uint> z = point.Slice(2 * L, L);
        uint any = 0;
        foreach (uint limb in z)
            any |= limb;
        if (any == 0)
            return false; // only for a key of 0 or n, which InRange refuses
        Span<uint> zInverse = stackalloc uint[L], t = stackalloc uint[L];
        Invert(zInverse, z, curve);
        // Out of Montgomery form by multiplying by plain 1, in constant time too:
        // x is the shared secret.
        Span<uint> one = stackalloc uint[L];
        one.Clear();
        one[0] = 1;
        Mul(t, point.Slice(0, L), zInverse);
        Mul(t, t, one);
        Modulus.ToBytes(t, x);
        Mul(t, point.Slice(L, L), zInverse);
        Mul(t, t, one);
        Modulus.ToBytes(t, y);
        t.Clear();
        zInverse.Clear();
        return true;
    }
}
