// Arithmetic on big numbers modulo an odd number, for checking signatures:
// RSA (lib/CryptoRsa.cs) and ECDSA (lib/CryptoEcdsa.cs).
//
// Numbers are arrays of 32-bit limbs, lowest first, and multiplication is
// Montgomery's: a number x is kept as x·R mod N (R = 2^(32·limbs)), so that
// reducing a product is a multiply and a shift instead of a division. The
// limbs are 32 bits so that every product fits a ulong on every target,
// 32-bit ones (DOS, microcontrollers, WebAssembly) included.
//
// Everything here works on public values only: a signature, a public key, a
// hash. So it may take more or less time depending on them (it skips zero
// bits of an exponent, for one). Code that handles secrets (X25519, the
// ciphers) has its own constant-time arithmetic.

using System;

namespace System.Security.Cryptography;

internal sealed class Modulus
{
    internal readonly uint[] N;
    internal readonly int Length;
    internal readonly int Bits;
    readonly uint _n0;    // -N⁻¹ mod 2^32
    readonly uint[] _rr;  // R² mod N
    readonly uint[] _one; // R mod N: 1 in Montgomery form

    Modulus(uint[] n)
    {
        N = n;
        Length = n.Length;
        Bits = BitLength(n);

        // N⁻¹ mod 2^32 by Newton's iteration: an odd x is its own inverse
        // mod 8, and each step doubles the number of correct bits.
        uint inverse = n[0];
        for (int i = 0; i < 4; i++)
            inverse *= 2 - n[0] * inverse;
        _n0 = 0 - inverse;

        // R mod N, then R² mod N, by doubling 1 and reducing each time.
        _one = new uint[Length];
        _one[0] = 1;
        for (int i = 0; i < 32 * Length; i++)
            DoubleModN(_one);
        _rr = new uint[Length];
        _one.AsSpan().CopyTo(_rr);
        for (int i = 0; i < 32 * Length; i++)
            DoubleModN(_rr);
    }

    /// <summary>A modulus from big-endian bytes; null unless it is odd and above 1.</summary>
    internal static Modulus Create(ReadOnlySpan<byte> bigEndian)
    {
        bigEndian = TrimZeros(bigEndian);
        if (bigEndian.IsEmpty || (bigEndian[bigEndian.Length - 1] & 1) == 0)
            return null;
        if (bigEndian.Length == 1 && bigEndian[0] == 1)
            return null;
        uint[] n = new uint[(bigEndian.Length + 3) / 4];
        FromBytes(bigEndian, n);
        return new Modulus(n);
    }

    internal static ReadOnlySpan<byte> TrimZeros(ReadOnlySpan<byte> bigEndian)
    {
        int start = 0;
        while (start < bigEndian.Length && bigEndian[start] == 0)
            start++;
        return bigEndian.Slice(start);
    }

    /// <summary>
    /// Reads big-endian bytes into limbs; false if the value doesn't fit in them.
    /// </summary>
    internal static bool FromBytes(ReadOnlySpan<byte> bigEndian, Span<uint> limbs)
    {
        bigEndian = TrimZeros(bigEndian);
        if (bigEndian.Length > 4 * limbs.Length)
            return false;
        limbs.Clear();
        for (int i = 0; i < bigEndian.Length; i++)
        {
            int fromEnd = bigEndian.Length - 1 - i;
            limbs[fromEnd / 4] |= (uint)bigEndian[i] << (8 * (fromEnd % 4));
        }
        return true;
    }

    /// <summary>Writes limbs as big-endian bytes, filling all of <paramref name="bigEndian"/>.</summary>
    internal static void ToBytes(ReadOnlySpan<uint> limbs, Span<byte> bigEndian)
    {
        for (int i = 0; i < bigEndian.Length; i++)
        {
            int fromEnd = bigEndian.Length - 1 - i;
            int limb = fromEnd / 4;
            bigEndian[i] = limb < limbs.Length ? (byte)(limbs[limb] >> (8 * (fromEnd % 4))) : (byte)0;
        }
    }

    internal static int BitLength(ReadOnlySpan<uint> x)
    {
        for (int i = x.Length - 1; i >= 0; i--)
            if (x[i] != 0)
                return 32 * i + 32 - System.Numerics.BitOperations.LeadingZeroCount(x[i]);
        return 0;
    }

    internal static bool IsZero(ReadOnlySpan<uint> x)
    {
        for (int i = 0; i < x.Length; i++)
            if (x[i] != 0)
                return false;
        return true;
    }

    /// <summary>-1, 0 or 1 as a is below, equal to or above b (same length).</summary>
    internal static int Compare(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        for (int i = a.Length - 1; i >= 0; i--)
            if (a[i] != b[i])
                return a[i] < b[i] ? -1 : 1;
        return 0;
    }

    /// <summary>True if x (Length limbs) is below N.</summary>
    internal bool IsReduced(ReadOnlySpan<uint> x) => Compare(x, N) < 0;

    /// <summary>x = x mod N, for x below 2N.</summary>
    internal void ReduceOnce(Span<uint> x)
    {
        if (Compare(x, N) >= 0)
            SubtractN(x);
    }

    // x = x - N, returning the borrow.
    uint SubtractN(Span<uint> x)
    {
        long borrow = 0;
        for (int i = 0; i < Length; i++)
        {
            long d = (long)x[i] - N[i] + borrow;
            x[i] = (uint)d;
            borrow = d >> 32;
        }
        return (uint)-borrow;
    }

    void DoubleModN(Span<uint> x)
    {
        uint carry = 0;
        for (int i = 0; i < Length; i++)
        {
            uint next = x[i] >> 31;
            x[i] = (x[i] << 1) | carry;
            carry = next;
        }
        if (carry != 0 || Compare(x, N) >= 0)
            SubtractN(x);
    }

    /// <summary>r = a + b mod N, for a and b below N.</summary>
    internal void Add(Span<uint> r, ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        ulong carry = 0;
        for (int i = 0; i < Length; i++)
        {
            ulong s = (ulong)a[i] + b[i] + carry;
            r[i] = (uint)s;
            carry = s >> 32;
        }
        if (carry != 0 || Compare(r, N) >= 0)
            SubtractN(r);
    }

    /// <summary>r = a - b mod N, for a and b below N.</summary>
    internal void Subtract(Span<uint> r, ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        long borrow = 0;
        for (int i = 0; i < Length; i++)
        {
            long d = (long)a[i] - b[i] + borrow;
            r[i] = (uint)d;
            borrow = d >> 32;
        }
        if (borrow != 0)
        {
            ulong carry = 0;
            for (int i = 0; i < Length; i++)
            {
                ulong s = (ulong)r[i] + N[i] + carry;
                r[i] = (uint)s;
                carry = s >> 32;
            }
        }
    }

    /// <summary>
    /// r = a·b/R mod N (Montgomery multiplication), for a and b below N. r may be a or b.
    /// </summary>
    internal void Multiply(Span<uint> r, ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        int length = Length;
        Span<uint> t = stackalloc uint[length + 2];
        t.Clear();
        for (int i = 0; i < length; i++)
        {
            // t += a·b[i]
            ulong carry = 0;
            uint bi = b[i];
            for (int j = 0; j < length; j++)
            {
                ulong s = t[j] + (ulong)a[j] * bi + carry;
                t[j] = (uint)s;
                carry = s >> 32;
            }
            ulong top = t[length] + carry;
            t[length] = (uint)top;
            t[length + 1] = (uint)(top >> 32);

            // t = (t + m·N) / 2^32, with m chosen so the low limb becomes 0.
            uint m = t[0] * _n0;
            carry = (t[0] + (ulong)m * N[0]) >> 32;
            for (int j = 1; j < length; j++)
            {
                ulong s = t[j] + (ulong)m * N[j] + carry;
                t[j - 1] = (uint)s;
                carry = s >> 32;
            }
            top = t[length] + carry;
            t[length - 1] = (uint)top;
            t[length] = t[length + 1] + (uint)(top >> 32);
            t[length + 1] = 0;
        }
        // t < 2N: subtract N once if needed.
        if (t[length] != 0 || Compare(t.Slice(0, length), N) >= 0)
            SubtractN(t);
        t.Slice(0, length).CopyTo(r);
    }

    /// <summary>x·R mod N, for x below N.</summary>
    internal void ToMontgomery(Span<uint> r, ReadOnlySpan<uint> x) => Multiply(r, x, _rr);

    /// <summary>x/R mod N: back from Montgomery form.</summary>
    internal void FromMontgomery(Span<uint> r, ReadOnlySpan<uint> x)
    {
        Span<uint> one = stackalloc uint[Length];
        one.Clear();
        one[0] = 1;
        Multiply(r, x, one);
    }

    /// <summary>1 in Montgomery form.</summary>
    internal ReadOnlySpan<uint> One => _one;

    /// <summary>
    /// r = x^e mod N, x and r in Montgomery form, e big-endian bytes. Square and
    /// multiply, left to right: its time depends on e, which is public here.
    /// </summary>
    internal void Power(Span<uint> r, ReadOnlySpan<uint> x, ReadOnlySpan<byte> exponent)
    {
        Span<uint> result = stackalloc uint[Length];
        _one.AsSpan().CopyTo(result);
        Span<uint> bas = stackalloc uint[Length];
        x.Slice(0, Length).CopyTo(bas);
        bool started = false;
        for (int i = 0; i < exponent.Length; i++)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                if (started)
                    Multiply(result, result, result);
                if (((exponent[i] >> bit) & 1) != 0)
                {
                    Multiply(result, result, bas);
                    started = true;
                }
            }
        }
        result.CopyTo(r);
    }

    /// <summary>r = x⁻¹ mod N for a prime N (Fermat: x^(N-2)), Montgomery form in and out.</summary>
    internal void InvertPrime(Span<uint> r, ReadOnlySpan<uint> x)
    {
        Span<uint> exponent = stackalloc uint[Length];
        N.AsSpan().CopyTo(exponent);
        for (int i = 0, borrow = 2; i < Length && borrow != 0; i++)
        {
            uint before = exponent[i];
            exponent[i] -= (uint)borrow;
            borrow = exponent[i] > before ? 1 : 0;
        }
        Span<byte> bytes = stackalloc byte[4 * Length];
        ToBytes(exponent, bytes);
        Power(r, x, bytes);
    }
}
