// Test: Kernels.DotI8, the int8 dot product, against a plain loop: lengths
// around each path's blocks (SIMD's 16 and 32 bytes, the C64's 255), the
// largest values, and pseudo-random ones. On a Commodore 64 it runs in 6502
// assembly with a table of quarter squares; elsewhere in SIMD or plain C#.
// It runs on every target. Exit code = number of failed checks.
// Build: ./build.sh SmallDot

using System;
using System.Runtime.InteropServices;

namespace SmallDotSample;

public static class Program
{
    const int Most = 600;

    static int s_checks, s_failures;

    static void Check(string name, int length, bool ok)
    {
        s_checks++;
        if (!ok)
        {
            s_failures++;
            Console.WriteLine($"  failed: {name}, {length} values");
        }
    }

    static uint s_seed = 12345;

    // -127..127, from a linear congruential generator.
    static sbyte Next()
    {
        s_seed = s_seed * 1664525 + 1013904223;
        return (sbyte)((int)(s_seed >> 16) % 255 - 127);
    }

    // DotI8 on the first length values against the sum of the products.
    static unsafe bool Same(sbyte* a, sbyte* b, int length)
    {
        unsafe
        {
            // SAFETY: the callers' blocks hold at least length values.
            int expected = 0;
            for (int i = 0; i < length; i++)
                expected += a[i] * b[i];
            return Kernels.DotI8(a, b, length) == expected;
        }
    }

    public static int Main()
    {
        int[] lengths = [0, 1, 2, 15, 16, 17, 31, 32, 33, 254, 255, 256, 257, 510, 511, Most];
        unsafe
        {
            // SAFETY: both blocks hold Most values, and every length is at
            // most Most.
            sbyte* a = (sbyte*)NativeMemory.Alloc(Most);
            sbyte* b = (sbyte*)NativeMemory.Alloc(Most);

            foreach (int length in lengths)
            {
                for (int i = 0; i < Most; i++)
                {
                    a[i] = Next();
                    b[i] = Next();
                }
                Check("random values", length, Same(a, b, length));
            }

            // The largest products, of either sign: 600 * 127 * 127 needs
            // more than 24 bits.
            for (int i = 0; i < Most; i++)
            {
                a[i] = 127;
                b[i] = (sbyte)(i % 3 == 0 ? -127 : 127);
            }
            Check("the largest values", Most, Same(a, b, Most));
            for (int i = 0; i < Most; i++)
                b[i] = -127;
            Check("the largest negative sum", Most, Same(a, b, Most)
                && Kernels.DotI8(a, b, Most) == -Most * 127 * 127);

            // Every product there is, one at a time.
            bool all = true;
            for (int x = -127; x <= 127 && all; x++)
            {
                a[0] = (sbyte)x;
                for (int y = -127; y <= 127 && all; y++)
                {
                    b[0] = (sbyte)y;
                    all = Kernels.DotI8(a, b, 1) == x * y;
                }
            }
            Check("every product", 1, all);

            NativeMemory.Free(a);
            NativeMemory.Free(b);
        }

        if (s_failures == 0)
            Console.WriteLine($"PASS DotI8 ({s_checks} checks)");
        return s_failures;
    }
}
