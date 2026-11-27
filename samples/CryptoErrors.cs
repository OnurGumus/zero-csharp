// Example: what the cryptography refuses (lib/CryptoChaCha.cs,
// lib/EncryptedStream.cs): a changed ciphertext, tag, nonce or associated
// data; records changed, cut short, reordered or under a wrong key; wrong
// argument sizes. Each throw comes back through Catch.Run, which WebAssembly
// doesn't have, so this runs everywhere else. Crypto checks the results
// that should come out.
// Exit code = number of failed checks.
// Build: ./build.sh CryptoErrors

using System;
using System.IO;
using System.Security.Cryptography;

namespace CryptoErrorsSample;

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

    static bool AllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            if (b != 0)
                return false;
        return true;
    }

    static byte[] Numbered(int length, int start)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)(start + i);
        return bytes;
    }

    public static int Main()
    {
        Aead();
        Records();
        Arguments();
        return s_failures;
    }

    static void Aead()
    {
        byte[] key = Numbered(32, 1), nonce = Numbered(12, 50), aad = Numbered(20, 90);
        byte[] plaintext = Numbered(100, 7);
        byte[] ciphertext = new byte[100], tag = new byte[16], back = new byte[100];
        using var aead = new ChaCha20Poly1305(key);
        aead.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        // docs:tag-mismatch
        // Decrypt checks the tag before anything else: a changed byte
        // anywhere throws, and the destination gets zeros, not plaintext.
        ciphertext[40] ^= 1;
        Exception error = Catch.Run(
            () => aead.Decrypt(nonce, ciphertext, tag, back, aad));
        // docs:end
        Check("changed ciphertext", error is AuthenticationTagMismatchException);
        Check("zeros after failure", AllZero(back));
        ciphertext[40] ^= 1;

        tag[15] ^= 0x80;
        error = Catch.Run(() => aead.Decrypt(nonce, ciphertext, tag, back, aad));
        Check("changed tag", error is AuthenticationTagMismatchException);
        tag[15] ^= 0x80;

        aad[0] ^= 1;
        error = Catch.Run(() => aead.Decrypt(nonce, ciphertext, tag, back, aad));
        Check("changed associated data", error is AuthenticationTagMismatchException);
        aad[0] ^= 1;

        nonce[11] ^= 1;
        error = Catch.Run(() => aead.Decrypt(nonce, ciphertext, tag, back, aad));
        Check("another nonce", error is AuthenticationTagMismatchException);
        nonce[11] ^= 1;

        error = Catch.Run(() => aead.Decrypt(nonce, ciphertext, tag, back, aad));
        Check("unchanged opens", error == null && back[0] == 7 && back[99] == 106);
        Check("a mismatch is a CryptographicException", new AuthenticationTagMismatchException() is CryptographicException);
        Section("ChaCha20-Poly1305 refusals");
    }

    static void Records()
    {
        byte[] keyA = Numbered(32, 0), keyB = Numbered(32, 100), ivA = Numbered(12, 200), ivB = Numbered(12, 50);
        var wire = new MemoryStream();
        using (var sender = new EncryptedStream(wire, keyA, ivA, keyB, ivB, leaveOpen: true))
        {
            sender.Write("hello, "u8);
            sender.Write("records"u8);
            sender.Write(Numbered(20000, 3));
        }
        byte[] wireBytes = wire.ToArray();

        Check("unchanged reads", ReadAll(wireBytes, keyB, ivB, keyA, ivA) == null);

        // docs:record-refused
        // A changed byte anywhere in a record: Read throws IOException.
        byte[] changed = wireBytes.AsSpan().ToArray();
        changed[30] ^= 0x80;
        Exception error = ReadAll(changed, keyB, ivB, keyA, ivA);
        // docs:end
        Check("changed record", error is IOException);

        // The closing record dropped: the connection may have been cut short.
        byte[] cut = wireBytes.AsSpan(0, wireBytes.Length - (5 + 2 + 1 + 16)).ToArray();
        Check("cut short", ReadAll(cut, keyB, ivB, keyA, ivA) is IOException);
        // Cut inside a record.
        Check("cut inside a record", ReadAll(wireBytes.AsSpan(0, 40).ToArray(), keyB, ivB, keyA, ivA) is IOException);

        // The first two records (29 bytes each) swapped: each is checked
        // against its place's nonce, so the first already fails.
        byte[] swapped = wireBytes.AsSpan().ToArray();
        wireBytes.AsSpan(29, 29).CopyTo(swapped.AsSpan(0, 29));
        wireBytes.AsSpan(0, 29).CopyTo(swapped.AsSpan(29, 29));
        Check("reordered", ReadAll(swapped, keyB, ivB, keyA, ivA) is IOException);

        // A record repeated: the copy is out of place.
        byte[] repeated = new byte[wireBytes.Length + 29];
        wireBytes.AsSpan(0, 29).CopyTo(repeated);
        wireBytes.AsSpan().CopyTo(repeated.AsSpan(29));
        Check("repeated", ReadAll(repeated, keyB, ivB, keyA, ivA) is IOException);

        Check("wrong key", ReadAll(wireBytes, keyA, ivA, keyB, ivB) is IOException);

        // Something that isn't a record at all.
        Check("not a record", ReadAll("GET / HTTP/1.1\r\n\r\n"u8.ToArray(), keyB, ivB, keyA, ivA) is IOException);

        // Writing after Shutdown.
        var stream = new EncryptedStream(new MemoryStream(), keyA, ivA, keyB, ivB);
        stream.Shutdown();
        Check("written after closing", Catch.Run(() => stream.Write("late"u8)) is IOException);
        Section("EncryptedStream refusals");
    }

    // Reads the records to the end; the exception, or null.
    static Exception ReadAll(byte[] wireBytes, byte[] sendKey, byte[] sendIV, byte[] receiveKey, byte[] receiveIV)
        => Catch.Run(() =>
        {
            var receiver = new EncryptedStream(new MemoryStream(wireBytes, false), sendKey, sendIV, receiveKey, receiveIV, leaveOpen: true);
            Span<byte> chunk = stackalloc byte[1000];
            while (receiver.Read(chunk) > 0) { }
        });

    static void Arguments()
    {
        byte[] key = Numbered(32, 1);
        Check("short key", Catch.Run(() => new ChaCha20Poly1305(new byte[16])) is ArgumentException);
        using var aead = new ChaCha20Poly1305(key);
        Check("short nonce", Catch.Run(() => aead.Encrypt(new byte[8], new byte[4], new byte[4], new byte[16])) is ArgumentException);
        Check("short tag", Catch.Run(() => aead.Encrypt(new byte[12], new byte[4], new byte[4], new byte[12])) is ArgumentException);
        Check("lengths differ", Catch.Run(() => aead.Encrypt(new byte[12], new byte[4], new byte[5], new byte[16])) is ArgumentException);
        Check("X25519 short key", Catch.Run(() =>
        {
            Span<byte> output = stackalloc byte[32];
            X25519.GetPublicKey(new byte[31], output);
        }) is ArgumentException);
        Check("HKDF too long", Catch.Run(() =>
            HKDF.Expand(HashAlgorithmName.SHA256, new byte[32], 255 * 32 + 1)) is ArgumentOutOfRangeException);
        Check("HKDF short key", Catch.Run(() =>
            HKDF.Expand(HashAlgorithmName.SHA256, new byte[16], 32)) is ArgumentException);
        Check("unknown hash", Catch.Run(() => IncrementalHash.CreateHash(new HashAlgorithmName("MD5"))) is CryptographicException);
        Check("same key both ways", Catch.Run(() =>
            new EncryptedStream(new MemoryStream(), key, new byte[12], key, new byte[12])) is ArgumentException);
        Check("short secret", Catch.Run(() =>
            EncryptedStream.FromSharedSecret(new MemoryStream(), new byte[8], true)) is ArgumentException);
        Section("Arguments");
    }
}
