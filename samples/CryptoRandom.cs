// Example: random bytes from the system (lib/CryptoRandom.cs), for keys and
// nonces, and X25519 key agreement with fresh keys. Only for the targets with
// a source of random bytes: not DOS, microcontrollers or the C64, where
// RandomNumberGenerator throws.
// Exit code = number of failed checks.
// Build: ./build.sh CryptoRandom

using System;
using System.Security.Cryptography;

namespace CryptoRandomSample;

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

    static bool Same(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    public static int Main()
    {
        // docs:random-bytes
        Span<byte> key = stackalloc byte[32];
        RandomNumberGenerator.Fill(key);
        // docs:end
        Span<byte> other = stackalloc byte[32];
        RandomNumberGenerator.Fill(other);
        Check("two fills differ", !Same(key, other));

        // More than getentropy's 256 bytes a call, and every byte value turns up.
        byte[] many = RandomNumberGenerator.GetBytes(4096);
        Span<bool> seen = stackalloc bool[256];
        foreach (byte b in many)
            seen[b] = true;
        int distinct = 0;
        foreach (bool s in seen)
            if (s)
                distinct++;
        Check("all byte values", distinct == 256); // missing one has odds of about 256 * e^-16

        Span<int> counts = stackalloc int[6];
        bool inRange = true;
        for (int i = 0; i < 600; i++)
        {
            int die = RandomNumberGenerator.GetInt32(1, 7);
            if (die < 1 || die > 6)
                inRange = false;
            else
                counts[die - 1]++;
        }
        Check("GetInt32 in range", inRange);
        bool everyFace = true;
        foreach (int c in counts)
            if (c == 0)
                everyFace = false;
        Check("every face", everyFace);
        Check("GetInt32 one value", RandomNumberGenerator.GetInt32(5, 6) == 5);
        Section("RandomNumberGenerator");

        // Two parties with fresh keys agree on a secret; a third key doesn't get it.
        Span<byte> alice = stackalloc byte[32], alicePublic = stackalloc byte[32];
        Span<byte> bob = stackalloc byte[32], bobPublic = stackalloc byte[32];
        X25519.GeneratePrivateKey(alice);
        X25519.GetPublicKey(alice, alicePublic);
        X25519.GeneratePrivateKey(bob);
        X25519.GetPublicKey(bob, bobPublic);
        Span<byte> aliceShared = stackalloc byte[32], bobShared = stackalloc byte[32];
        Check("Alice agrees", X25519.TryGetSharedSecret(alice, bobPublic, aliceShared));
        Check("Bob agrees", X25519.TryGetSharedSecret(bob, alicePublic, bobShared));
        Check("the same secret", Same(aliceShared, bobShared));
        Span<byte> eve = stackalloc byte[32], eveShared = stackalloc byte[32];
        X25519.GeneratePrivateKey(eve);
        X25519.TryGetSharedSecret(eve, bobPublic, eveShared);
        Check("another key, another secret", !Same(eveShared, bobShared));
        Section("X25519 with fresh keys");
        return s_failures;
    }
}
