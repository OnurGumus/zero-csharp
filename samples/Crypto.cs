// Example: cryptography (lib/CryptoHashes.cs, lib/CryptoChaCha.cs,
// lib/CryptoX25519.cs, lib/EncryptedStream.cs): SHA-2, HMAC, HKDF,
// ChaCha20-Poly1305, X25519 and the encrypted record stream, checked against
// the standards' own test vectors (FIPS 180-4, RFC 4231, 5869, 8439, 7748
// and 8448). Nothing here needs random bytes, so it runs on every target;
// CryptoRandom checks the system's random source, and CryptoErrors that
// changed data is refused (it needs Catch.Run, which WebAssembly lacks).
// Exit code = number of failed checks.
// Build: ./build.sh Crypto

using System;
using System.IO;
using System.Security.Cryptography;

namespace CryptoSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void Section(string section)
    {
        Out.Line($"{(s_sectionFailures == 0 ? "PASS" : "FAIL")} {section} ({s_sectionChecks} checks)");
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_failures++;
            s_sectionFailures++;
            Out.Line($"  failed: {name}");
        }
    }

    static void CheckHex(string name, ReadOnlySpan<byte> actual, string expected)
    {
        string hex = Convert.ToHexStringLower(actual);
        Check(name, hex == expected);
        if (hex != expected)
            Out.Line($"    got      {hex}\n    expected {expected}");
    }

    static byte[] FromHex(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(Digit(hex[2 * i]) * 16 + Digit(hex[2 * i + 1]));
        return bytes;
    }

    static int Digit(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    static bool Same(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    static bool AllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            if (b != 0)
                return false;
        return true;
    }

    static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (Same(haystack.Slice(i, needle.Length), needle))
                return true;
        return false;
    }

    static byte[] Copy(byte[] bytes) => new ReadOnlySpan<byte>(bytes).ToArray();

    static byte[] Repeat(byte value, int count)
    {
        byte[] bytes = new byte[count];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    static byte[] Ascii(string text)
    {
        byte[] bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
            bytes[i] = (byte)text[i];
        return bytes;
    }

    public static int Main()
    {
        Hashes();
        Hmacs();
        Hkdf();
        Aead();
        KeyAgreement();
        Tls13KeySchedule();
        Records();
        return s_failures;
    }

    static void Hashes()
    {
        // docs:hash
        byte[] digest = SHA256.HashData("abc"u8);
        byte[] mac = HMACSHA256.HashData("key"u8, "message"u8);
        // docs:end
        CheckHex("SHA256 of a span", digest,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        CheckHex("HMAC of spans", mac,
            "6e9ef29b75fffc5b7abae527d58fdadb2fe42e7219011976917343065f58ed4a");

        // FIPS 180-4's examples: empty, "abc", the two-block messages, a million 'a'.
        string[] messages =
        {
            "",
            "abc",
            "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
            "abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu",
        };
        string[][] expected =
        {
            new[] { "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
              "38b060a751ac96384cd9327eb1b1e36a21fdb71114be07434c0cc7bf63f6e1da274edebfe76f65fbd51ad2f14898b95b",
              "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e" },
            new[] { "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
              "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7",
              "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f" },
            new[] { "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1",
              "3391fdddfc8dc7393707a65b1b4709397cf8b1d162af05abfe8f450de5f36bc6b0455a8520bc4e6f5fe95b1fe3c8452b",
              "204a8fc6dda82f0a0ced7beb8e08a41657c16ef468b228a8279be331a703c33596fd15c13b1b07f9aa1d3bea57789ca031ad85c7a71dd70354ec631238ca3445" },
            new[] { "cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1",
              "09330c33f71147e83d192fc782cd1b4753111b173b3b05d22fa08086e3b0f712fcc7c71a557e2db966c3e9fa91746039",
              "8e959b75dae313da8cf4f72814fc143f8f7779c6eb9f7fa17299aeadb6889018501d289e4900f7e4331b99dec4b5433ac7d329eeb6dd26545e96e55b874be909" },
        };
        for (int i = 0; i < messages.Length; i++)
        {
            byte[] message = Ascii(messages[i]);
            CheckHex($"SHA256 #{i}", SHA256.HashData(message), expected[i][0]);
            CheckHex($"SHA384 #{i}", SHA384.HashData(message), expected[i][1]);
            CheckHex($"SHA512 #{i}", SHA512.HashData(message), expected[i][2]);
        }

        // A million 'a', appended in uneven pieces, so the buffering is tested too.
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        byte[] a = Repeat((byte)'a', 1000);
        int total = 0;
        for (int piece = 1; total < 1_000_000; piece = piece % 997 + 1)
        {
            int n = Math.Min(piece, 1_000_000 - total);
            sha256.AppendData(a, 0, n);
            sha512.AppendData(a, 0, n);
            total += n;
        }
        CheckHex("SHA256 million", sha256.GetHashAndReset(), "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0");
        CheckHex("SHA512 million", sha512.GetHashAndReset(),
            "e718483d0ce769644e2e42c7bc15b4638e1f98b13b2044285632a803afa973ebde0ff244877ea60a4cb0432ce577c31beb009c5c2c49aa2e4eadb217ad8cc09b");

        // GetCurrentHash leaves the hash going; reset starts over.
        sha256.AppendData("ab"u8);
        CheckHex("current", sha256.GetCurrentHash(), "fb8e20fc2e4c3f248c60c39bd652f3c1347298bb977b8b4d5903b85055620603");
        sha256.AppendData("c"u8);
        CheckHex("after current", sha256.GetHashAndReset(), expected[1][0]);

        var stream = new MemoryStream(Ascii(messages[2]));
        CheckHex("from a stream", SHA256.HashData(stream), expected[2][0]);
        Span<byte> small = stackalloc byte[31];
        Check("TryHashData too small", !SHA256.TryHashData("abc"u8, small, out int written) && written == 0);
        Section("SHA-2");
    }

    static void Hmacs()
    {
        // RFC 4231 test cases 1-4, 6 and 7 (5 truncates the output).
        byte[][] keys =
        {
            Repeat(0x0b, 20), Ascii("Jefe"), Repeat(0xaa, 20),
            FromHex("0102030405060708090a0b0c0d0e0f10111213141516171819"),
            Repeat(0xaa, 131), Repeat(0xaa, 131),
        };
        byte[][] data =
        {
            Ascii("Hi There"), Ascii("what do ya want for nothing?"), Repeat(0xdd, 50), Repeat(0xcd, 50),
            Ascii("Test Using Larger Than Block-Size Key - Hash Key First"),
            Ascii("This is a test using a larger than block-size key and a larger than block-size data. The key needs to be hashed before being used by the HMAC algorithm."),
        };
        string[][] expected =
        {
            new[] { "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7",
              "afd03944d84895626b0825f4ab46907f15f9dadbe4101ec682aa034c7cebc59cfaea9ea9076ede7f4af152e8b2fa9cb6",
              "87aa7cdea5ef619d4ff0b4241a1d6cb02379f4e2ce4ec2787ad0b30545e17cdedaa833b7d6b8a702038b274eaea3f4e4be9d914eeb61f1702e696c203a126854" },
            new[] { "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843",
              "af45d2e376484031617f78d2b58a6b1b9c7ef464f5a01b47e42ec3736322445e8e2240ca5e69e2c78b3239ecfab21649",
              "164b7a7bfcf819e2e395fbe73b56e0a387bd64222e831fd610270cd7ea2505549758bf75c05a994a6d034f65f8f0e6fdcaeab1a34d4a6b4b636e070a38bce737" },
            new[] { "773ea91e36800e46854db8ebd09181a72959098b3ef8c122d9635514ced565fe",
              "88062608d3e6ad8a0aa2ace014c8a86f0aa635d947ac9febe83ef4e55966144b2a5ab39dc13814b94e3ab6e101a34f27",
              "fa73b0089d56a284efb0f0756c890be9b1b5dbdd8ee81a3655f83e33b2279d39bf3e848279a722c806b485a47e67c807b946a337bee8942674278859e13292fb" },
            new[] { "82558a389a443c0ea4cc819899f2083a85f0faa3e578f8077a2e3ff46729665b",
              "3e8a69b7783c25851933ab6290af6ca77a9981480850009cc5577c6e1f573b4e6801dd23c4a7d679ccf8a386c674cffb",
              "b0ba465637458c6990e5a8c5f61d4af7e576d97ff94b872de76f8050361ee3dba91ca5c11aa25eb4d679275cc5788063a5f19741120c4f2de2adebeb10a298dd" },
            new[] { "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54",
              "4ece084485813e9088d2c63a041bc5b44f9ef1012a2b588f3cd11f05033ac4c60c2ef6ab4030fe8296248df163f44952",
              "80b24263c7c1a3ebb71493c1dd7be8b49b46d1f41b4aeec1121b013783f8f3526b56d037e05f2598bd0fd2215d6a1e5295e64f73f63f0aec8b915a985d786598" },
            new[] { "9b09ffa71b942fcb27635fbcd5b0e944bfdc63644f0713938a7f51535c3a35e2",
              "6617178e941f020d351e2f254e8fd32c602420feb0b8fb9adccebb82461e99c5a678cc31e799176d3860e6110c46523e",
              "e37b6a775dc87dbaa4dfa9f96e5e3ffddebd71f8867289865df5a32d20cdc944b6022cac3c4982b10d5eeb55c3e4de15134676fb6de0446065c97440fa8c6a58" },
        };
        for (int i = 0; i < keys.Length; i++)
        {
            CheckHex($"HMAC-SHA256 #{i}", HMACSHA256.HashData(keys[i], data[i]), expected[i][0]);
            CheckHex($"HMAC-SHA384 #{i}", HMACSHA384.HashData(keys[i], data[i]), expected[i][1]);
            CheckHex($"HMAC-SHA512 #{i}", HMACSHA512.HashData(keys[i], data[i]), expected[i][2]);
        }

        // An incremental HMAC keeps its key across resets.
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, keys[1]);
        hmac.AppendData(Ascii("what do ya "));
        hmac.GetCurrentHash();
        hmac.AppendData(Ascii("want for nothing?"));
        CheckHex("incremental", hmac.GetHashAndReset(), expected[1][0]);
        hmac.AppendData(data[1]);
        CheckHex("after reset", hmac.GetHashAndReset(), expected[1][0]);

        Check("FixedTimeEquals same", CryptographicOperations.FixedTimeEquals("tag!"u8, "tag!"u8));
        Check("FixedTimeEquals differs", !CryptographicOperations.FixedTimeEquals("tag!"u8, "tag?"u8));
        Check("FixedTimeEquals length", !CryptographicOperations.FixedTimeEquals("tag"u8, "tag!"u8));
        Section("HMAC");
    }

    static void Hkdf()
    {
        // RFC 5869 A.1 to A.3 (SHA-256).
        byte[] ikm = Repeat(0x0b, 22);
        byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, FromHex("000102030405060708090a0b0c"));
        CheckHex("A.1 PRK", prk, "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5");
        CheckHex("A.1 OKM", HKDF.Expand(HashAlgorithmName.SHA256, prk, 42, FromHex("f0f1f2f3f4f5f6f7f8f9")),
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");

        byte[] ikm2 = new byte[80], salt2 = new byte[80], info2 = new byte[80];
        for (int i = 0; i < 80; i++)
        {
            ikm2[i] = (byte)i;
            salt2[i] = (byte)(0x60 + i);
            info2[i] = (byte)(0xb0 + i);
        }
        CheckHex("A.2 PRK", HKDF.Extract(HashAlgorithmName.SHA256, ikm2, salt2),
            "06a6b88c5853361a06104c9ceb35b45cef760014904671014a193f40c15fc244");
        CheckHex("A.2 OKM", HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm2, 82, salt2, info2),
            "b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71cc30c58179ec3e87c14c01d5c1f3434f1d87");

        CheckHex("A.3 PRK", HKDF.Extract(HashAlgorithmName.SHA256, ikm),
            "19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04");
        CheckHex("A.3 OKM", HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 42),
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8");
        Section("HKDF");
    }

    static void Aead()
    {
        // RFC 8439 2.8.2.
        byte[] key = FromHex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        byte[] nonce = FromHex("070000004041424344454647");
        byte[] aad = FromHex("50515253c0c1c2c3c4c5c6c7");
        byte[] plaintext = Ascii("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        // docs:seal
        // Encrypt: ciphertext as long as the plaintext, and a 16-byte tag
        // over it and the associated data (sent as is, but protected).
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using var aead = new ChaCha20Poly1305(key);
        aead.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        // docs:end
        CheckHex("2.8.2 ciphertext", ciphertext,
            "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc3ff4def08e4b7a9de576d26586cec64b6116");
        CheckHex("2.8.2 tag", tag, "1ae10b594f09e26a7e902ecbd0600691");

        // docs:open
        byte[] back = new byte[plaintext.Length];
        aead.Decrypt(nonce, ciphertext, tag, back, aad);
        // docs:end
        Check("decrypt", Same(back.AsSpan(), plaintext));

        // In place: the same buffer for plaintext and ciphertext.
        byte[] buffer = Copy(plaintext);
        aead.Encrypt(nonce, buffer, buffer, tag, aad);
        Check("in place", Same(buffer.AsSpan(), ciphertext));

        // RFC 8439 A.5: decryption, 265 bytes.
        using var aead2 = new ChaCha20Poly1305(FromHex("1c9240a5eb55d38af333888604f6b5f0473917c1402b80099dca5cbc207075c0"));
        byte[] received = FromHex(
            "64a0861575861af460f062c79be643bd5e805cfd345cf389f108670ac76c8cb24c6cfc18755d43eea09ee94e382d26b0bdb7b73c321b0100d4f03b7f355894cf" +
            "332f830e710b97ce98c8a84abd0b948114ad176e008d33bd60f982b1ff37c8559797a06ef4f0ef61c186324e2b3506383606907b6a7c02b0f9f6157b53c867e4" +
            "b9166c767b804d46a59b5216cde7a4e99040c5a40433225ee282a1b0a06c523eaf4534d7f83fa1155b0047718cbc546a0d072b04b3564eea1b422273f548271a" +
            "0bb2316053fa76991955ebd63159434ecebb4e466dae5a1073a6727627097a1049e617d91d361094fa68f0ff77987130305beaba2eda04df997b714d6c6f2c29" +
            "a6ad5cb4022b02709b");
        byte[] opened = new byte[received.Length];
        aead2.Decrypt(FromHex("000000000102030405060708"), received, FromHex("eead9d67890cbb22392336fea1851f38"), opened,
                      FromHex("f33388860000000000004e91"));
        CheckHex("A.5 plaintext", SHA256.HashData(opened), "686ff73f1610b08cd80f98a321bba01dca634c41bad02ebd166661fc4ca1757c");

        // Empty plaintext: the tag covers the associated data alone.
        Span<byte> emptyTag = stackalloc byte[16];
        aead.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, emptyTag, aad);
        aead.Decrypt(nonce, ReadOnlySpan<byte>.Empty, emptyTag, Span<byte>.Empty, aad);
        Check("empty", true);
        Section("ChaCha20-Poly1305");
    }

    static void KeyAgreement()
    {
        // RFC 7748 5.2.
        Span<byte> result = stackalloc byte[32];
        X25519.ScalarMultiply(FromHex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
                              FromHex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"), result);
        CheckHex("5.2 #1", result, "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552");
        X25519.ScalarMultiply(FromHex("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d"),
                              FromHex("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493"), result);
        CheckHex("5.2 #2 (u's top bit set)", result, "95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957");

        // The iteration test, one step: k = u = 9; k, u = X25519(k, u), k.
        Span<byte> k = stackalloc byte[32];
        k.Clear();
        k[0] = 9;
        X25519.ScalarMultiply(k, k, result);
        CheckHex("5.2 one iteration", result, "422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079");

        // RFC 7748 6.1: Alice and Bob.
        byte[] alice = FromHex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        byte[] bob = FromHex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        Span<byte> alicePublic = stackalloc byte[32], bobPublic = stackalloc byte[32];
        X25519.GetPublicKey(alice, alicePublic);
        X25519.GetPublicKey(bob, bobPublic);
        CheckHex("Alice's public key", alicePublic, "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        CheckHex("Bob's public key", bobPublic, "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        Span<byte> aliceShared = stackalloc byte[32], bobShared = stackalloc byte[32];
        Check("Alice agrees", X25519.TryGetSharedSecret(alice, bobPublic, aliceShared));
        Check("Bob agrees", X25519.TryGetSharedSecret(bob, alicePublic, bobShared));
        CheckHex("shared secret", aliceShared, "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");
        Check("the same secret", Same(aliceShared, bobShared));

        // Points of small order give zeros whatever the private key: refused.
        Span<byte> bad = stackalloc byte[32];
        bad.Clear();
        Check("u = 0 refused", !X25519.TryGetSharedSecret(alice, bad, result));
        bad[0] = 1;
        Check("u = 1 refused", !X25519.TryGetSharedSecret(alice, bad, result));
        Check("zeros when refused", AllZero(result));
        // u = p, which isn't reduced: it's 0 modulo p.
        byte[] p = FromHex("edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f");
        Check("u = p refused", !X25519.TryGetSharedSecret(alice, p, result));
        Section("X25519");
    }

    // RFC 8448 3 (a TLS 1.3 handshake, written out byte by byte): its X25519
    // keys and its key schedule, which is HKDF on SHA-256 over the transcript.
    static void Tls13KeySchedule()
    {
        byte[] clientPrivate = FromHex("49af42ba7f7994852d713ef2784bcbcaa7911de26adc5642cb634540e7ea5005");
        byte[] serverPrivate = FromHex("b1580eeadf6dd589b8ef4f2d5652578cc810e9980191ec8d058308cea216a21e");
        Span<byte> clientPublic = stackalloc byte[32], serverPublic = stackalloc byte[32];
        X25519.GetPublicKey(clientPrivate, clientPublic);
        X25519.GetPublicKey(serverPrivate, serverPublic);
        CheckHex("client key share", clientPublic, "99381de560e4bd43d23d8e435a7dbafeb3c06e51c13cae4d5413691e529aaf2c");
        CheckHex("server key share", serverPublic, "c9828876112095fe66762bdbf7c672e156d6cc253b833df1dd69b1b04e751f0f");
        Span<byte> shared = stackalloc byte[32];
        X25519.TryGetSharedSecret(serverPrivate, clientPublic, shared);
        CheckHex("ECDHE secret", shared, "8bd4054fb55b9d63fdfbacf9f04b9f0d35e6d63f537563efd46272900f89492d");

        var sha256 = HashAlgorithmName.SHA256;
        byte[] early = HKDF.Extract(sha256, new byte[32], new byte[0]);
        CheckHex("early secret", early, "33ad0a1c607ec03b09e6cd9893680ce210adf300aa1f2660e1b22e10f170f92a");
        byte[] emptyHash = SHA256.HashData(ReadOnlySpan<byte>.Empty);
        byte[] derived = ExpandLabel(early, "derived", emptyHash, 32);
        CheckHex("derived", derived, "6f2615a108c702c5678f54fc9dbab69716c076189c48250cebeac3576c3611ba");
        byte[] handshake = HKDF.Extract(sha256, shared.ToArray(), derived);
        CheckHex("handshake secret", handshake, "1dc826e93606aa6fdc0aadc12f741b01046aa6b99f691ed221a9f0ca043fbeac");

        // The transcript: ClientHello and ServerHello, as RFC 8448 gives them.
        using var transcript = IncrementalHash.CreateHash(sha256);
        transcript.AppendData(FromHex(
            "010000c00303cb34ecb1e78163ba1c38c6dacb196a6dffa21a8d9912ec18a2ef6283024dece7000006130113031302010000910000000b" +
            "0009000006736572766572ff01000100000a00140012001d0017001800190100010101020103010400230000003300260024001d002099" +
            "381de560e4bd43d23d8e435a7dbafeb3c06e51c13cae4d5413691e529aaf2c002b0003020304000d0020001e04030503060302030804080508" +
            "0604010501060102010402050206020202002d00020101001c00024001"));
        transcript.AppendData(FromHex(
            "020000560303a6af06a4121860dc5e6e60249cd34c95930c8ac5cb1434dac155772ed3e2692800130100002e00330024001d0020c982" +
            "8876112095fe66762bdbf7c672e156d6cc253b833df1dd69b1b04e751f0f002b00020304"));
        byte[] helloHash = transcript.GetCurrentHash();
        byte[] clientHandshake = ExpandLabel(handshake, "c hs traffic", helloHash, 32);
        byte[] serverHandshake = ExpandLabel(handshake, "s hs traffic", helloHash, 32);
        CheckHex("client handshake traffic secret", clientHandshake, "b3eddb126e067f35a780b3abf45e2d8f3b1a950738f52e9600746a0e27a55a21");
        CheckHex("server handshake traffic secret", serverHandshake, "b67b7d690cc16c4e75e54213cb2d37b4e9c912bcded9105d42befd59d391ad38");
        // The record keys (16 bytes: this handshake used AES-128-GCM) and IV.
        CheckHex("server handshake key", ExpandLabel(serverHandshake, "key", new byte[0], 16), "3fce516009c21727d0f2e4e86ee403bc");
        CheckHex("server handshake iv", ExpandLabel(serverHandshake, "iv", new byte[0], 12), "5d313eb2671276ee13000b30");
        Section("TLS 1.3 key schedule");
    }

    // HKDF-Expand-Label (RFC 8446 7.1): the label and context in a small structure as info.
    static byte[] ExpandLabel(byte[] secret, string label, byte[] context, int length)
    {
        byte[] info = new byte[2 + 1 + 6 + label.Length + 1 + context.Length];
        info[0] = (byte)(length >> 8);
        info[1] = (byte)length;
        info[2] = (byte)(6 + label.Length);
        Ascii("tls13 ").AsSpan().CopyTo(info.AsSpan(3));
        Ascii(label).AsSpan().CopyTo(info.AsSpan(9));
        info[9 + label.Length] = (byte)context.Length;
        context.AsSpan().CopyTo(info.AsSpan(10 + label.Length));
        return HKDF.Expand(HashAlgorithmName.SHA256, secret, length, info);
    }

    static void Records()
    {
        byte[] keyA = new byte[32], keyB = new byte[32], ivA = new byte[12], ivB = new byte[12];
        for (int i = 0; i < 32; i++)
        {
            keyA[i] = (byte)i;
            keyB[i] = (byte)(100 + i);
        }
        for (int i = 0; i < 12; i++)
        {
            ivA[i] = (byte)(200 + i);
            ivB[i] = (byte)(50 + i);
        }

        // One end writes into the wire, the other reads it back.
        var wire = new MemoryStream();
        byte[] big = new byte[40000];
        for (int i = 0; i < big.Length; i++)
            big[i] = (byte)(i * 7);
        using (var sender = new EncryptedStream(wire, keyA, ivA, keyB, ivB, leaveOpen: true))
        {
            sender.Write("hello, "u8);
            sender.Write("records"u8);
            sender.Write(big);
        }   // Dispose sends the closing record
        // Two small records, three for the 40000 bytes, the closing one: each
        // has a header, its content type and a tag.
        Check("record sizes", wire.Length == 7 + 7 + 40000 + 2 + 6 * (5 + 1 + 16));
        Check("encrypted", !Contains(wire.ToArray(), "hello"u8));

        byte[] wireBytes = wire.ToArray();
        byte[] received = ReadAll(wireBytes, keyB, ivB, keyA, ivA);
        Check("read back", received.Length == 14 + big.Length
                           && Same(received.AsSpan(0, 14), "hello, records"u8)
                           && Same(received.AsSpan(14), big));

        Section("EncryptedStream");
    }

    static byte[] ReadAll(byte[] wireBytes, byte[] sendKey, byte[] sendIV, byte[] receiveKey, byte[] receiveIV)
    {
        var received = new MemoryStream();
        // leaveOpen: Dispose would send a closing record into a read-only stream.
        var receiver = new EncryptedStream(new MemoryStream(wireBytes, false), sendKey, sendIV, receiveKey, receiveIV, leaveOpen: true);
        Span<byte> chunk = stackalloc byte[1000];
        int n;
        while ((n = receiver.Read(chunk)) > 0)
            received.Write(chunk.Slice(0, n));
        return received.ToArray();
    }
}
