// lib/CryptoP256.cs (constant-time P-256 ECDH) against .NET's ECDiffieHellman:
// public keys from the same private key, and shared secrets with random
// peers, must match; edge scalars (1, 2, n - 1, n - 2) too; 0, n and points
// off the curve must be refused.

using System;
using System.Numerics;
using S = System.Security.Cryptography;
using Z = ZeroTest;

static partial class Program
{
    static void P256(int iterations)
    {
        int failures = 0;
        string first = "";
        void Fail(string what) { if (failures++ == 0) first = what; }
        BigInteger n = BigInteger.Parse("0ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551", System.Globalization.NumberStyles.HexNumber);
        byte[] Scalar(BigInteger k)
        {
            byte[] b = k.ToByteArray(isUnsigned: true, isBigEndian: true);
            byte[] r = new byte[32];
            b.CopyTo(r, 32 - b.Length);
            return r;
        }
        byte[] Public(S.ECParameters p)
        {
            byte[] q = new byte[65];
            q[0] = 4;
            p.Q.X.CopyTo(q, 1);
            p.Q.Y.CopyTo(q, 33);
            return q;
        }
        S.ECDiffieHellman FromPrivate(byte[] d)
            => S.ECDiffieHellman.Create(new S.ECParameters { Curve = S.ECCurve.NamedCurves.nistP256, D = d });

        for (int i = 0; i < iterations; i++)
        {
            byte[] d;
            if (i < 4)
                d = Scalar(new[] { BigInteger.One, new BigInteger(2), n - 1, n - 2 }[i]);
            else
                d = Bytes(32);
            bool valid = new BigInteger(d, isUnsigned: true, isBigEndian: true) is var k && k > 0 && k < n;
            byte[] ours = new byte[65];
            bool made = Z.P256Ecdh.TryGetPublicKey(d, ours);
            if (made != valid)
            {
                Fail($"TryGetPublicKey({Hex(d)}) gave {made}");
                continue;
            }
            if (!valid)
                continue;
            using S.ECDiffieHellman a = FromPrivate(d);
            S.ECParameters pa = a.ExportParameters(true);
            if (Hex(ours) != Hex(Public(pa)))
                Fail($"public key of {Hex(d)}");
            using S.ECDiffieHellman b = S.ECDiffieHellman.Create(S.ECCurve.NamedCurves.nistP256);
            byte[] expected = a.DeriveRawSecretAgreement(b.PublicKey);
            byte[] secret = new byte[32];
            if (!Z.P256Ecdh.TryGetSharedSecret(d, Public(b.ExportParameters(false)), secret) || Hex(secret) != Hex(expected))
                Fail($"shared secret with private key {Hex(d)}");
        }

        // GenerateKey's keys are ones .NET agrees on.
        for (int i = 0; i < 20; i++)
        {
            byte[] d = new byte[32], q = new byte[65];
            Z.P256Ecdh.GenerateKey(d, q);
            using S.ECDiffieHellman a = FromPrivate(d);
            if (Hex(q) != Hex(Public(a.ExportParameters(false))))
                Fail("GenerateKey's public key");
        }

        // 0 and n as private keys; points off the curve or not uncompressed.
        byte[] good = new byte[65];
        Z.P256Ecdh.TryGetPublicKey(Scalar(7), good);
        byte[] s = new byte[32];
        if (Z.P256Ecdh.TryGetSharedSecret(new byte[32], good, s) || Z.P256Ecdh.TryGetSharedSecret(Scalar(n), good, s))
            Fail("a private key of 0 or n was accepted");
        byte[] off = (byte[])good.Clone();
        off[64] ^= 1;
        byte[] compressed = (byte[])good.Clone();
        compressed[0] = 2;
        if (Z.P256Ecdh.TryGetSharedSecret(Scalar(5), off, s) || Z.P256Ecdh.TryGetSharedSecret(Scalar(5), compressed, s)
            || Z.P256Ecdh.TryGetSharedSecret(Scalar(5), good.AsSpan(0, 33), s))
            Fail("a bad public key was accepted");
        Report("P-256 ECDH", iterations + 20, failures, first);
    }
}
