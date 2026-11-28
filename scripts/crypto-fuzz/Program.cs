// Runs lib/'s cryptography (namespace ZeroTest) against .NET's, and X25519
// and Poly1305 against BigInteger versions written straight from the RFCs,
// on random and edge-case input. Prints a line per algorithm; exit code =
// number of algorithms with a mismatch.

using System;
using System.Collections.Generic;
using System.Numerics;
using S = System.Security.Cryptography;
using Z = ZeroTest;

static partial class Program
{
    static Random s_random;
    static readonly List<(string Name, int Runs, int Failures, string First)> s_results = new();

    static void Report(string name, int runs, int failures, string first)
    {
        s_results.Add((name, runs, failures, first));
        Console.WriteLine($"{(failures == 0 ? "ok  " : "FAIL")} {name,-22} {runs,8} runs" +
                          (failures == 0 ? "" : $", {failures} failed; first: {first}"));
    }

    static byte[] Bytes(int length)
    {
        byte[] bytes = new byte[length];
        s_random.NextBytes(bytes);
        return bytes;
    }

    // Lengths around the block sizes matter most: 0, 55, 56, 63, 64, 111, 112, 127, 128...
    static int Length(int max)
    {
        int pick = s_random.Next(4);
        if (pick == 0)
        {
            int[] edges = { 0, 1, 15, 16, 17, 31, 32, 33, 55, 56, 57, 63, 64, 65, 111, 112, 113, 127, 128, 129, 255, 256 };
            int e = edges[s_random.Next(edges.Length)];
            return Math.Min(e, max);
        }
        return s_random.Next(max + 1);
    }

    static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    static int Main(string[] args)
    {
        int iterations = args.Length > 0 ? int.Parse(args[0]) : 20000;
        int seed = args.Length > 1 ? int.Parse(args[1]) : 12345;
        s_random = new Random(seed);

        Hashes(iterations);
        Hmacs(iterations);
        Hkdf(iterations / 4);
        Aead(iterations);
        AesGcm(iterations);
        Poly1305(iterations);
        X25519(Math.Max(200, iterations / 20));
        Stream(Math.Max(100, iterations / 100));
        Rsa(Math.Max(200, iterations / 20));
        Ecdsa(Math.Max(200, iterations / 20));
        P256(Math.Max(200, iterations / 20));
        KeysDamaged(iterations);
        X509Damaged(iterations);
        X509Chains();
        X509SystemRoots();
        TlsReplay(Math.Max(500, iterations / 10));
        TlsRetryReplay();

        int failed = 0;
        foreach (var r in s_results)
            if (r.Failures > 0)
                failed++;
        return failed;
    }

    // Appends data to a hash in random pieces.
    static void AppendInPieces(Z.IncrementalHash hash, byte[] data)
    {
        int at = 0;
        while (at < data.Length)
        {
            int n = Math.Min(data.Length - at, 1 + s_random.Next(200));
            hash.AppendData(data, at, n);
            at += n;
            if (s_random.Next(8) == 0)
                hash.GetCurrentHash();
        }
    }

    static void Hashes(int iterations)
    {
        var names = new[] { (Z.HashAlgorithmName.SHA256, S.HashAlgorithmName.SHA256),
                            (Z.HashAlgorithmName.SHA384, S.HashAlgorithmName.SHA384),
                            (Z.HashAlgorithmName.SHA512, S.HashAlgorithmName.SHA512) };
        int failures = 0;
        string first = "";
        for (int i = 0; i < iterations; i++)
        {
            byte[] data = Bytes(Length(2000));
            var (zn, sn) = names[i % 3];
            byte[] expected = sn == S.HashAlgorithmName.SHA256 ? S.SHA256.HashData(data)
                            : sn == S.HashAlgorithmName.SHA384 ? S.SHA384.HashData(data) : S.SHA512.HashData(data);
            byte[] once = zn == Z.HashAlgorithmName.SHA256 ? Z.SHA256.HashData(data)
                        : zn == Z.HashAlgorithmName.SHA384 ? Z.SHA384.HashData(data) : Z.SHA512.HashData(data);
            using var incremental = Z.IncrementalHash.CreateHash(zn);
            AppendInPieces(incremental, data);
            byte[] pieces = incremental.GetHashAndReset();
            if (Hex(once) != Hex(expected) || Hex(pieces) != Hex(expected))
            {
                if (failures++ == 0)
                    first = $"{zn.Name} of {data.Length} bytes";
            }
        }
        Report("SHA-2", iterations, failures, first);
    }

    static void Hmacs(int iterations)
    {
        int failures = 0;
        string first = "";
        for (int i = 0; i < iterations; i++)
        {
            byte[] key = Bytes(Length(300));
            byte[] data = Bytes(Length(1000));
            int which = i % 3;
            byte[] expected = which == 0 ? S.HMACSHA256.HashData(key, data)
                            : which == 1 ? S.HMACSHA384.HashData(key, data) : S.HMACSHA512.HashData(key, data);
            byte[] once = which == 0 ? Z.HMACSHA256.HashData(key, data)
                        : which == 1 ? Z.HMACSHA384.HashData(key, data) : Z.HMACSHA512.HashData(key, data);
            var name = which == 0 ? Z.HashAlgorithmName.SHA256 : which == 1 ? Z.HashAlgorithmName.SHA384 : Z.HashAlgorithmName.SHA512;
            using var incremental = Z.IncrementalHash.CreateHMAC(name, key);
            AppendInPieces(incremental, data);
            byte[] pieces = incremental.GetHashAndReset();
            // After a reset, the same key goes on.
            incremental.AppendData(data);
            byte[] again = incremental.GetHashAndReset();
            if (Hex(once) != Hex(expected) || Hex(pieces) != Hex(expected) || Hex(again) != Hex(expected))
            {
                if (failures++ == 0)
                    first = $"{name.Name}, key {key.Length} bytes, data {data.Length}";
            }
        }
        Report("HMAC", iterations, failures, first);
    }

    static void Hkdf(int iterations)
    {
        int failures = 0;
        string first = "";
        for (int i = 0; i < iterations; i++)
        {
            int which = i % 3;
            var zn = which == 0 ? Z.HashAlgorithmName.SHA256 : which == 1 ? Z.HashAlgorithmName.SHA384 : Z.HashAlgorithmName.SHA512;
            var sn = which == 0 ? S.HashAlgorithmName.SHA256 : which == 1 ? S.HashAlgorithmName.SHA384 : S.HashAlgorithmName.SHA512;
            int hashSize = which == 0 ? 32 : which == 1 ? 48 : 64;
            byte[] ikm = Bytes(Length(200));
            byte[] salt = Bytes(Length(200));
            byte[] info = Bytes(Length(200));
            int length = 1 + s_random.Next(s_random.Next(4) == 0 ? 255 * hashSize : 200);
            byte[] expected = S.HKDF.DeriveKey(sn, ikm, length, salt, info);
            byte[] actual = Z.HKDF.DeriveKey(zn, ikm, length, salt, info);
            byte[] prk = Z.HKDF.Extract(zn, ikm, salt);
            bool prkOk = Hex(prk) == Hex(S.HKDF.Extract(sn, ikm, salt));
            if (Hex(actual) != Hex(expected) || !prkOk)
            {
                if (failures++ == 0)
                    first = $"{zn.Name}, ikm {ikm.Length}, salt {salt.Length}, info {info.Length}, length {length}";
            }
        }
        Report("HKDF", iterations, failures, first);
    }

    static void Aead(int iterations)
    {
        int failures = 0;
        string first = "";
        for (int i = 0; i < iterations; i++)
        {
            byte[] key = Bytes(32), nonce = Bytes(12);
            byte[] aad = Bytes(Length(100));
            byte[] plaintext = Bytes(Length(1000));
            byte[] expectedCipher = new byte[plaintext.Length], expectedTag = new byte[16];
            using (var dotnet = new S.ChaCha20Poly1305(key))
                dotnet.Encrypt(nonce, plaintext, expectedCipher, expectedTag, aad);

            using var ours = new Z.ChaCha20Poly1305(key);
            byte[] cipher = new byte[plaintext.Length], tag = new byte[16];
            ours.Encrypt(nonce, plaintext, cipher, tag, aad);
            byte[] back = new byte[plaintext.Length];
            bool opened = ours.TryDecrypt(nonce, cipher, tag, back, aad);

            // A flipped bit in the ciphertext, the tag or the associated data must fail.
            bool tamperRefused = true;
            int where = s_random.Next(cipher.Length + 16 + aad.Length);
            byte bit = (byte)(1 << s_random.Next(8));
            if (where < cipher.Length) cipher[where] ^= bit;
            else if (where < cipher.Length + 16) tag[where - cipher.Length] ^= bit;
            else aad[where - cipher.Length - 16] ^= bit;
            byte[] garbage = new byte[plaintext.Length];
            if (ours.TryDecrypt(nonce, cipher, tag, garbage, aad))
                tamperRefused = false;

            bool ok = opened && Hex(back) == Hex(plaintext) && tamperRefused;
            // cipher was tampered with above; compare the tag and the untouched parts via a fresh run.
            byte[] cipher2 = new byte[plaintext.Length], tag2 = new byte[16];
            ours.Encrypt(nonce, plaintext, cipher2, tag2, where >= cipher.Length + 16 ? FlipBack(aad, where - cipher.Length - 16, bit) : aad);
            ok &= Hex(cipher2) == Hex(expectedCipher) && Hex(tag2) == Hex(expectedTag);
            if (!ok)
            {
                if (failures++ == 0)
                    first = $"aad {aad.Length}, plaintext {plaintext.Length}, opened {opened}, tamper refused {tamperRefused}";
            }
        }
        Report("ChaCha20-Poly1305", iterations, failures, first);
    }

    static void AesGcm(int iterations)
    {
        int failures = 0;
        string first = "";
        int[] keySizes = { 16, 24, 32 };
        for (int i = 0; i < iterations; i++)
        {
            byte[] key = Bytes(keySizes[i % 3]), nonce = Bytes(12);
            int tagSize = 12 + s_random.Next(5);
            byte[] aad = Bytes(Length(100));
            byte[] plaintext = Bytes(s_random.Next(8) == 0 ? s_random.Next(2000) : Length(300));
            // .NET on macOS takes only 16-byte tags; a shorter GCM tag is
            // the full one's first bytes.
            byte[] expectedCipher = new byte[plaintext.Length], fullTag = new byte[16];
            using (var dotnet = new S.AesGcm(key, 16))
                dotnet.Encrypt(nonce, plaintext, expectedCipher, fullTag, aad);
            byte[] expectedTag = fullTag.AsSpan(0, tagSize).ToArray();

            using var ours = new Z.AesGcm(key, tagSize);
            byte[] cipher = new byte[plaintext.Length], tag = new byte[tagSize];
            ours.Encrypt(nonce, plaintext, cipher, tag, aad);
            bool same = Hex(cipher) == Hex(expectedCipher) && Hex(tag) == Hex(expectedTag);
            byte[] back = new byte[plaintext.Length];
            bool opened = ours.TryDecrypt(nonce, cipher, tag, back, aad) && Hex(back) == Hex(plaintext);

            int where = s_random.Next(cipher.Length + tagSize);
            byte bit = (byte)(1 << s_random.Next(8));
            if (where < cipher.Length) cipher[where] ^= bit; else tag[where - cipher.Length] ^= bit;
            bool refused = !ours.TryDecrypt(nonce, cipher, tag, back, aad);
            if (!(same && opened && refused) && failures++ == 0)
                first = $"key {key.Length}, tag {tagSize}, aad {aad.Length}, plaintext {plaintext.Length}: same {same}, opened {opened}, refused {refused}";
        }
        Report("AES-GCM", iterations, failures, first);
    }

    static byte[] FlipBack(byte[] aad, int index, byte bit)
    {
        aad[index] ^= bit;
        return aad;
    }

    // ---- Poly1305 -----------------------------------------------------------

    static readonly BigInteger P1305 = (BigInteger.One << 130) - 5;

    static byte[] ReferencePoly1305(byte[] key, byte[] message)
    {
        BigInteger r = new BigInteger(key.AsSpan(0, 16), isUnsigned: true)
                       & BigInteger.Parse("0ffffffc0ffffffc0ffffffc0fffffff", System.Globalization.NumberStyles.HexNumber);
        BigInteger s = new BigInteger(key.AsSpan(16, 16), isUnsigned: true);
        BigInteger accumulator = 0;
        for (int at = 0; at < message.Length; at += 16)
        {
            int n = Math.Min(16, message.Length - at);
            byte[] block = new byte[n + 1];
            message.AsSpan(at, n).CopyTo(block);
            block[n] = 1;
            accumulator = (accumulator + new BigInteger(block, isUnsigned: true)) * r % P1305;
        }
        BigInteger tag = (accumulator + s) & ((BigInteger.One << 128) - 1);
        byte[] result = new byte[16];
        tag.TryWriteBytes(result, out _, isUnsigned: true);
        return result;
    }

    static byte[] OurPoly1305(byte[] key, byte[] message, bool pieces)
    {
        Z.Poly1305 poly = default;
        poly.Initialize(key);
        if (!pieces)
            poly.Append(message);
        else
            for (int at = 0; at < message.Length;)
            {
                int n = Math.Min(message.Length - at, 1 + s_random.Next(40));
                poly.Append(message.AsSpan(at, n));
                at += n;
            }
        byte[] tag = new byte[16];
        poly.Finish(tag);
        return tag;
    }

    static void Poly1305(int iterations)
    {
        int failures = 0;
        string first = "";
        void Run(byte[] key, byte[] message, string what)
        {
            string expected = Hex(ReferencePoly1305(key, message));
            if (Hex(OurPoly1305(key, message, false)) != expected || Hex(OurPoly1305(key, message, true)) != expected)
                if (failures++ == 0)
                    first = what;
        }

        // RFC 8439 A.3 #5 to #11: carries and reductions at their limits.
        string[][] vectors =
        {
            new[] { "02000000000000000000000000000000", "00000000000000000000000000000000", "ffffffffffffffffffffffffffffffff" },
            new[] { "02000000000000000000000000000000", "ffffffffffffffffffffffffffffffff", "02000000000000000000000000000000" },
            new[] { "01000000000000000000000000000000", "00000000000000000000000000000000",
                    "fffffffffffffffffffffffffffffffff0ffffffffffffffffffffffffffffff11000000000000000000000000000000" },
            new[] { "01000000000000000000000000000000", "00000000000000000000000000000000",
                    "fffffffffffffffffffffffffffffffffbfefefefefefefefefefefefefefefe01010101010101010101010101010101" },
            new[] { "02000000000000000000000000000000", "00000000000000000000000000000000", "fdffffffffffffffffffffffffffffff" },
            new[] { "01000000000000000400000000000000", "00000000000000000000000000000000",
                    "e33594d7505e43b900000000000000003394d7505e4379cd01000000000000000000000000000000000000000000000001000000000000000000000000000000" },
            new[] { "01000000000000000400000000000000", "00000000000000000000000000000000",
                    "e33594d7505e43b900000000000000003394d7505e4379cd010000000000000000000000000000000000000000000000" },
        };
        string[] tags =
        {
            "03000000000000000000000000000000", "03000000000000000000000000000000", "05000000000000000000000000000000",
            "00000000000000000000000000000000", "faffffffffffffffffffffffffffffff", "14000000000000005500000000000000",
            "13000000000000000000000000000000",
        };
        for (int v = 0; v < vectors.Length; v++)
        {
            byte[] key = Convert.FromHexString(vectors[v][0] + vectors[v][1]);
            byte[] message = Convert.FromHexString(vectors[v][2]);
            if (Hex(OurPoly1305(key, message, false)) != tags[v] && failures++ == 0)
                first = $"RFC 8439 A.3 #{v + 5}";
            Run(key, message, $"RFC 8439 A.3 #{v + 5} against the reference");
        }

        for (int i = 0; i < iterations; i++)
        {
            byte[] key = Bytes(32);
            byte[] message = Bytes(Length(300));
            int shape = s_random.Next(4);
            if (shape == 0)
                Array.Fill(message, (byte)0xff);                  // every limb all ones
            if (shape == 1)
                for (int k = 0; k < 16; k++) key[k] = 0xff;       // the largest r clamping allows
            Run(key, message, $"key {Hex(key)}, message {message.Length} bytes, shape {shape}");
        }
        Report("Poly1305", iterations + vectors.Length, failures, first);
    }

    // ---- X25519 ---------------------------------------------------------------

    static readonly BigInteger P25519 = (BigInteger.One << 255) - 19;

    static BigInteger Mod(BigInteger x) => ((x % P25519) + P25519) % P25519;

    // RFC 7748 section 5, as written.
    static byte[] ReferenceX25519(byte[] scalar, byte[] u)
    {
        byte[] k = (byte[])scalar.Clone();
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
        BigInteger kk = new BigInteger(k, isUnsigned: true);
        byte[] uu = (byte[])u.Clone();
        uu[31] &= 127;
        BigInteger x1 = new BigInteger(uu, isUnsigned: true);
        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        int swap = 0;
        for (int t = 254; t >= 0; t--)
        {
            int kt = (int)((kk >> t) & 1);
            swap ^= kt;
            if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = kt;
            BigInteger a = Mod(x2 + z2), aa = Mod(a * a), b = Mod(x2 - z2), bb = Mod(b * b), e = Mod(aa - bb);
            BigInteger c = Mod(x3 + z3), d = Mod(x3 - z3), da = Mod(d * a), cb = Mod(c * b);
            x3 = Mod((da + cb) * (da + cb));
            z3 = Mod(x1 * Mod((da - cb) * (da - cb)));
            x2 = Mod(aa * bb);
            z2 = Mod(e * (aa + 121665 * e));
        }
        if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
        BigInteger result = Mod(x2 * BigInteger.ModPow(z2, P25519 - 2, P25519));
        byte[] bytes = new byte[32];
        result.TryWriteBytes(bytes, out _, isUnsigned: true);
        return bytes;
    }

    static byte[] OurX25519(byte[] scalar, byte[] u)
    {
        byte[] result = new byte[32];
        Z.X25519.ScalarMultiply(scalar, u, result);
        return result;
    }

    static byte[] Le(BigInteger value)
    {
        byte[] bytes = new byte[32];
        (value & ((BigInteger.One << 256) - 1)).TryWriteBytes(bytes, out _, isUnsigned: true);
        return bytes;
    }

    static void X25519(int iterations)
    {
        int failures = 0;
        string first = "";
        BigInteger[] edges =
        {
            0, 1, 2, 9, P25519 - 1, P25519, P25519 + 1, P25519 + 18,
            (BigInteger.One << 255) - 1, (BigInteger.One << 254), (BigInteger.One << 256) - 1,
        };
        int runs = 0;
        for (int i = 0; i < iterations + edges.Length; i++)
        {
            byte[] scalar = Bytes(32);
            byte[] u = i < edges.Length ? Le(edges[i]) : Bytes(32);
            if (i >= edges.Length && s_random.Next(4) == 0)
                for (int k = 1; k < 32; k++) u[k] = 0xff;          // near 2^256 - 1: limbs at their largest
            string expected = Hex(ReferenceX25519(scalar, u));
            runs++;
            if (Hex(OurX25519(scalar, u)) != expected && failures++ == 0)
                first = $"scalar {Hex(scalar)}, u {Hex(u)}";
        }

        // Encoding: a value decoded and encoded again comes out reduced
        // modulo p. Values within a few units of p and 2^255 are where a
        // missed carry or a wrong final subtraction shows, and random points
        // almost never land there.
        for (int i = 0; i < 2000; i++)
        {
            BigInteger value = i < 64 ? P25519 - 32 + i                              // p - 32 .. p + 31
                             : i < 96 ? (BigInteger.One << 255) - 1 - (i - 64)       // just under 2^255
                             : i < 128 ? i - 96                                      // small
                             : new BigInteger(Bytes(32), isUnsigned: true) & ((BigInteger.One << 255) - 1);
            byte[] encoded = Le(value);
            Z.X25519.Field f = default;
            Z.X25519.Field.FromBytes(ref f, encoded);
            // Through a multiplication by 1 too, so carried limbs are tested, not only fresh ones.
            Z.X25519.Field one = default;
            one[0] = 1;
            Z.X25519.Field product = default;
            Z.X25519.Field.Multiply(ref product, f, one);
            byte[] direct = new byte[32], viaProduct = new byte[32];
            Z.X25519.Field.ToBytes(direct, f);
            Z.X25519.Field.ToBytes(viaProduct, product);
            string expected = Hex(Le(Mod(value & ((BigInteger.One << 255) - 1)))); // decoding drops bit 255
            runs++;
            if ((Hex(direct) != expected || Hex(viaProduct) != expected) && failures++ == 0)
                first = $"encoding {value:X}";
        }

        // Subtraction leaves negative limbs; encoding must still reduce
        // into 0 .. p-1 (0 - 1 = p - 1, small - large, ...).
        for (int i = 0; i < 2000; i++)
        {
            BigInteger a = i < 50 ? i : new BigInteger(Bytes(32), isUnsigned: true) & ((BigInteger.One << 255) - 1);
            BigInteger b = i < 50 ? 50 - i + (i % 2 == 0 ? 0 : P25519 - 60) : new BigInteger(Bytes(32), isUnsigned: true) & ((BigInteger.One << 255) - 1);
            Z.X25519.Field fa = default, fb = default, difference = default;
            Z.X25519.Field.FromBytes(ref fa, Le(a));
            Z.X25519.Field.FromBytes(ref fb, Le(b));
            Z.X25519.Field.Subtract(ref difference, fa, fb);
            byte[] encoded = new byte[32];
            Z.X25519.Field.ToBytes(encoded, difference);
            runs++;
            if (Hex(encoded) != Hex(Le(Mod(a - b))) && failures++ == 0)
                first = $"encoding {a:X} - {b:X}";
        }

        // RFC 7748 5.2: 1000 iterations of k, u = X25519(k, u), k from k = u = 9.
        byte[] kIter = new byte[32], uIter = new byte[32];
        kIter[0] = 9;
        uIter[0] = 9;
        for (int i = 0; i < 1000; i++)
        {
            byte[] next = OurX25519(kIter, uIter);
            uIter = kIter;
            kIter = next;
        }
        runs++;
        if (Hex(kIter) != "684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51" && failures++ == 0)
            first = "RFC 7748 1000 iterations: " + Hex(kIter);
        Report("X25519", runs, failures, first);
    }

    // ---- EncryptedStream --------------------------------------------------------

    static void Stream(int iterations)
    {
        int failures = 0;
        string first = "";
        for (int i = 0; i < iterations; i++)
        {
            byte[] k1 = Bytes(32), k2 = Bytes(32), iv1 = Bytes(12), iv2 = Bytes(12);
            var wire = new System.IO.MemoryStream();
            var sent = new System.IO.MemoryStream();
            using (var writer = new Z.EncryptedStream(wire, k1, iv1, k2, iv2, leaveOpen: true))
            {
                int writes = s_random.Next(6);
                for (int w = 0; w < writes; w++)
                {
                    byte[] chunk = Bytes(s_random.Next(4) == 0 ? s_random.Next(40000) : Length(300));
                    writer.Write(chunk);
                    sent.Write(chunk);
                }
            }
            wire.Position = 0;
            var reader = new Z.EncryptedStream(wire, k2, iv2, k1, iv1, leaveOpen: true);
            var received = new System.IO.MemoryStream();
            byte[] buffer = new byte[1 + s_random.Next(5000)];
            int n;
            while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                received.Write(buffer, 0, n);
            if (Hex(received.ToArray()) != Hex(sent.ToArray()) && failures++ == 0)
                first = $"{sent.Length} bytes";

            // Any flipped bit on the wire makes Read throw.
            byte[] bytes = wire.ToArray();
            bytes[s_random.Next(bytes.Length)] ^= (byte)(1 << s_random.Next(8));
            var damaged = new Z.EncryptedStream(new System.IO.MemoryStream(bytes), k2, iv2, k1, iv1, leaveOpen: true);
            bool threw = false;
            try
            {
                while (damaged.Read(buffer, 0, buffer.Length) > 0) { }
            }
            catch (System.IO.IOException)
            {
                threw = true;
            }
            if (!threw && failures++ == 0)
                first = "a flipped bit went unnoticed";
        }
        Report("EncryptedStream", iterations, failures, first);
    }
}
