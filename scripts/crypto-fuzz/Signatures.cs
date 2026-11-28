// RSA and ECDSA verification (lib/CryptoRsa.cs, lib/CryptoEcdsa.cs) against
// signatures .NET makes: each must be accepted, and a changed bit in the
// signature, the data or the key must not be.

using System;
using S = System.Security.Cryptography;
using Z = ZeroTest;

static partial class Program
{
    static readonly (Z.HashAlgorithmName Z, S.HashAlgorithmName S)[] s_hashNames =
    {
        (Z.HashAlgorithmName.SHA256, S.HashAlgorithmName.SHA256),
        (Z.HashAlgorithmName.SHA384, S.HashAlgorithmName.SHA384),
        (Z.HashAlgorithmName.SHA512, S.HashAlgorithmName.SHA512),
    };

    static byte[] Flip(byte[] bytes)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[s_random.Next(copy.Length)] ^= (byte)(1 << s_random.Next(8));
        return copy;
    }

    static void Rsa(int iterations)
    {
        int[] sizes = { 1024, 1032, 2048, 3072, 4096 };
        int failures = 0;
        string first = "";
        int runs = 0;
        foreach (int size in sizes)
        {
            using S.RSA dotnet = S.RSA.Create(size);
            using Z.RSA ours = Z.RSA.Create();
            // Half the keys through SubjectPublicKeyInfo, half through the numbers.
            if (size % 2048 == 0)
                ours.ImportSubjectPublicKeyInfo(dotnet.ExportSubjectPublicKeyInfo(), out _);
            else
            {
                S.RSAParameters p = dotnet.ExportParameters(false);
                ours.ImportParameters(new Z.RSAParameters { Modulus = p.Modulus, Exponent = p.Exponent });
            }
            if (ours.KeySize != size && failures++ == 0)
                first = $"KeySize {ours.KeySize} for {size}";
            int count = Math.Max(20, iterations / sizes.Length / (size / 1024));
            for (int i = 0; i < count; i++, runs++)
            {
                var (zh, sh) = s_hashNames[s_random.Next(3)];
                bool pss = s_random.Next(2) == 0;
                // PSS's salt is as long as the hash: SHA-512 needs a key over 1040 bits.
                if (pss && size < 1048 && sh == S.HashAlgorithmName.SHA512)
                    sh = S.HashAlgorithmName.SHA256;
                if (pss && size < 1048 && zh == Z.HashAlgorithmName.SHA512)
                    zh = Z.HashAlgorithmName.SHA256;
                byte[] data = Bytes(Length(500));
                byte[] signature = dotnet.SignData(data, sh, pss ? S.RSASignaturePadding.Pss : S.RSASignaturePadding.Pkcs1);
                Z.RSASignaturePadding padding = pss ? Z.RSASignaturePadding.Pss : Z.RSASignaturePadding.Pkcs1;
                string what = $"{size}-bit {(pss ? "PSS" : "PKCS1")} {zh.Name}";
                if (!ours.VerifyData(data, signature, zh, padding) && failures++ == 0)
                    first = $"{what}: a good signature refused";
                if (ours.VerifyData(data, Flip(signature), zh, padding) && failures++ == 0)
                    first = $"{what}: a changed signature accepted";
                if (data.Length > 0 && ours.VerifyData(Flip(data), signature, zh, padding) && failures++ == 0)
                    first = $"{what}: changed data accepted";
                var other = s_hashNames[(Array.IndexOf(s_hashNames, (zh, sh)) + 1) % 3].Z;
                if (ours.VerifyData(data, signature, other, padding) && failures++ == 0)
                    first = $"{what}: accepted under another hash";
                var otherPadding = pss ? Z.RSASignaturePadding.Pkcs1 : Z.RSASignaturePadding.Pss;
                if (ours.VerifyData(data, signature, zh, otherPadding) && failures++ == 0)
                    first = $"{what}: accepted with the other padding";
            }
        }
        Report("RSA verify", runs, failures, first);
    }

    static void Ecdsa(int iterations)
    {
        var curves = new[] { (S.ECCurve.NamedCurves.nistP256, 32), (S.ECCurve.NamedCurves.nistP384, 48) };
        int failures = 0;
        string first = "";
        int runs = 0;
        int keys = Math.Max(4, iterations / 50);
        for (int k = 0; k < keys; k++)
        {
            var (curve, n) = curves[k % 2];
            using S.ECDsa dotnet = S.ECDsa.Create(curve);
            using Z.ECDsa ours = Z.ECDsa.Create();
            if (k % 4 < 2)
                ours.ImportSubjectPublicKeyInfo(dotnet.ExportSubjectPublicKeyInfo(), out _);
            else
            {
                S.ECParameters p = dotnet.ExportParameters(false);
                ours.ImportParameters(new Z.ECParameters
                {
                    Curve = n == 32 ? Z.ECCurve.NamedCurves.nistP256 : Z.ECCurve.NamedCurves.nistP384,
                    Q = new Z.ECPoint { X = p.Q.X, Y = p.Q.Y },
                });
            }
            Z.ECParameters back = ours.ExportParameters(false);
            S.ECParameters original = dotnet.ExportParameters(false);
            if ((Hex(back.Q.X) != Hex(original.Q.X) || Hex(back.Q.Y) != Hex(original.Q.Y)) && failures++ == 0)
                first = $"P-{8 * n}: exported point differs";

            for (int i = 0; i < 50; i++, runs++)
            {
                var (zh, sh) = s_hashNames[s_random.Next(3)];
                bool der = s_random.Next(2) == 0;
                byte[] data = Bytes(Length(500));
                byte[] signature = dotnet.SignData(data, sh,
                    der ? S.DSASignatureFormat.Rfc3279DerSequence : S.DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                var format = der ? Z.DSASignatureFormat.Rfc3279DerSequence : Z.DSASignatureFormat.IeeeP1363FixedFieldConcatenation;
                string what = $"P-{8 * n} {zh.Name} {(der ? "DER" : "P1363")}";
                if (!ours.VerifyData(data, signature, zh, format) && failures++ == 0)
                    first = $"{what}: a good signature refused";
                if (ours.VerifyData(data, Flip(signature), zh, format) && failures++ == 0)
                    first = $"{what}: a changed signature accepted";
                if (data.Length > 0 && ours.VerifyData(Flip(data), signature, zh, format) && failures++ == 0)
                    first = $"{what}: changed data accepted";
                var otherFormat = der ? Z.DSASignatureFormat.IeeeP1363FixedFieldConcatenation : Z.DSASignatureFormat.Rfc3279DerSequence;
                if (ours.VerifyData(data, signature, zh, otherFormat) && failures++ == 0)
                    first = $"{what}: accepted in the other format";

                // Verifying a hash directly, including hashes longer and shorter
                // than the curve (SHA-512 on P-256, SHA-256 on P-384).
                byte[] hash = Bytes(new[] { 32, 48, 64 }[s_random.Next(3)]);
                byte[] hashSignature = dotnet.SignHash(hash);
                if (!ours.VerifyHash(hash, hashSignature) && failures++ == 0)
                    first = $"P-{8 * n}: VerifyHash of {hash.Length} bytes refused";
            }

            // r or s of 0, or of n and above, is never a signature.
            byte[] zero = new byte[2 * n];
            byte[] ones = new byte[2 * n];
            Array.Fill(ones, (byte)0xFF);
            if ((ours.VerifyHash(new byte[32], zero) || ours.VerifyHash(new byte[32], ones)) && failures++ == 0)
                first = $"P-{8 * n}: r = s = 0 or all ones accepted";
        }

        // Points off the curve, and coordinates at or above p, are refused.
        foreach (var (curve, n) in curves)
        {
            using S.ECDsa dotnet = S.ECDsa.Create(curve);
            S.ECParameters p = dotnet.ExportParameters(false);
            var zCurve = n == 32 ? Z.ECCurve.NamedCurves.nistP256 : Z.ECCurve.NamedCurves.nistP384;
            byte[] y = (byte[])p.Q.Y.Clone();
            y[n - 1] ^= 1;
            byte[] pAll = new byte[n];
            Array.Fill(pAll, (byte)0xFF);
            foreach (var (x, yy) in new[] { (p.Q.X, y), (pAll, p.Q.Y) })
            {
                bool threw = false;
                try
                {
                    Z.ECDsa.Create(new Z.ECParameters { Curve = zCurve, Q = new Z.ECPoint { X = x, Y = yy } });
                }
                catch (Z.CryptographicException)
                {
                    threw = true;
                }
                if (!threw && failures++ == 0)
                    first = $"P-{8 * n}: a point off the curve was accepted";
            }
        }
        Report("ECDSA verify", runs, failures, first);
    }
}
