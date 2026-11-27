// Random bytes for keys, nonces and tokens, from the operating system, with
// .NET's System.Security.Cryptography.RandomNumberGenerator API.
//
//   Span<byte> key = stackalloc byte[32];
//   RandomNumberGenerator.Fill(key);
//   int pick = RandomNumberGenerator.GetInt32(1, 7);     // 1 to 6, unbiased
//
// Unlike Random (lib/Random.cs), whose numbers anyone can predict from a few
// of them, these come from the system's own generator, through the C
// library's getentropy, which each target answers:
//
// - Linux and Android: getentropy (the getrandom system call).
// - macOS and iOS: getentropy, in libSystem.
// - Windows: BCryptGenRandom with the system's preferred generator (win/runtime.c).
// - WebAssembly: WASI's random_get.
// - UEFI: the firmware's EFI_RNG_PROTOCOL, where it has one (efi/runtime.c);
//   OVMF has it when QEMU gives the machine a virtio-rng device or the CPU RDRAND.
// - DOS, microcontrollers and the C64 have no source of randomness a key
//   could come from: Fill throws CryptographicException there rather than
//   hand out bytes that only look random.

using System;
using System.Runtime.InteropServices;

namespace System.Security.Cryptography;

/// <summary>Random bytes from the operating system, good for keys and nonces.</summary>
public static class RandomNumberGenerator
{
    // At most 256 bytes a call: getentropy's limit everywhere.
    [DllImport("libc")]
    static unsafe extern int getentropy(byte* buffer, nuint length);

    /// <summary>Fills data with random bytes from the system. Throws CryptographicException where there's no source.</summary>
    public static void Fill(Span<byte> data)
    {
        while (!data.IsEmpty)
        {
            int n = Math.Min(data.Length, 256);
            int result;
            unsafe
            {
                // SAFETY: getentropy writes exactly n bytes, and n <= data.Length.
                fixed (byte* p = data)
                    result = getentropy(p, (nuint)n);
            }
            if (result != 0)
                throw new CryptographicException("RandomNumberGenerator: this system has no source of random bytes.");
            data = data.Slice(n);
        }
    }

    /// <summary><paramref name="count"/> random bytes.</summary>
    public static byte[] GetBytes(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        byte[] bytes = new byte[count];
        Fill(bytes);
        return bytes;
    }

    /// <summary>A random number from 0 up to, not including, <paramref name="toExclusive"/>, every one equally likely.</summary>
    public static int GetInt32(int toExclusive) => GetInt32(0, toExclusive);

    /// <summary>A random number from fromInclusive up to, not including, toExclusive, every one equally likely.</summary>
    public static int GetInt32(int fromInclusive, int toExclusive)
    {
        if (fromInclusive >= toExclusive)
            throw new ArgumentException("The range is empty.", nameof(toExclusive));
        uint range = (uint)(toExclusive - fromInclusive) - 1;
        if (range == 0)
            return fromInclusive;
        // Mask to the range's bit width and try again when over it: no
        // modulo bias, and fewer than two tries on average.
        uint mask = range;
        mask |= mask >> 1;
        mask |= mask >> 2;
        mask |= mask >> 4;
        mask |= mask >> 8;
        mask |= mask >> 16;
        Span<byte> four = stackalloc byte[4];
        uint value;
        do
        {
            Fill(four);
            value = (uint)(four[0] | four[1] << 8 | four[2] << 16 | four[3] << 24) & mask;
        }
        while (value > range);
        return (int)(fromInclusive + value);
    }
}
