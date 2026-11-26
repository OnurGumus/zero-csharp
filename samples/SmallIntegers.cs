// Test: whole-number arithmetic, sized for a small machine. On a Commodore 64
// (./build.sh --c64) none of this is the processor's own: the 6502 adds
// 8-bit numbers and has no multiply and no divide, so every 32-bit and
// 64-bit operation here is a routine of the compiler's. SmallDoubles.cs does
// the same for floating point. It runs on every target.
// Exit code = number of failed checks.
// Build: ./build.sh SmallIntegers

using System;
using System.Runtime.CompilerServices;

namespace SmallIntegersSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void End(string section)
    {
        Console.Write(s_sectionFailures == 0 ? "PASS " : "FAIL ");
        Console.Write(section);
        Console.Write(" (");
        Console.Write(s_sectionChecks);
        Console.WriteLine(" checks)");
        s_failures += s_sectionFailures;
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_sectionFailures++;
            Console.Write("  failed: ");
            Console.WriteLine(name);
        }
    }

    // Hide a value from the optimizer, which would otherwise do the
    // arithmetic at build time.
    //
    // Always false. A method that isn't inlined hides a value from most
    // compilers, but one that optimizes the whole program at once (the
    // Commodore 64's) sees that the method returns its argument, and does
    // the checks' arithmetic at build time. It can't see what a static field
    // holds, as long as something writes it: Main does, or the C# compiler
    // would take a field nothing writes for a constant.
    static bool s_never;
    [MethodImpl(MethodImplOptions.NoInlining)] static int I(int value) => s_never ? 0 : value;
    [MethodImpl(MethodImplOptions.NoInlining)] static uint U(uint value) => s_never ? 0 : value;
    [MethodImpl(MethodImplOptions.NoInlining)] static long L(long value) => s_never ? 0 : value;
    [MethodImpl(MethodImplOptions.NoInlining)] static ulong UL(ulong value) => s_never ? 0 : value;

    // Numbers at the edges of what division and multiplication do in pieces
    // (a byte, 16 bits, the sign), for every pair of which the C64 runtime's
    // fast paths and llvm-mos's routines must agree with .NET.
    static readonly int[] s_values =
    [
        0, 1, -1, 2, 3, 7, 10, -10, 37, 40, 255, 256, 257, -255, -256, 1000, 65535, 65536,
        -65536, 123456, 9999999, int.MaxValue, int.MinValue, int.MinValue + 1,
    ];

    // FNV-1a over the results, the same on every target.
    static uint s_hash = 2166136261;
    static void Mix(int value) => s_hash = (s_hash ^ (uint)value) * 16777619;

    static void DivisionTable()
    {
        bool identities = true;
        foreach (int x in s_values)
        {
            int a = I(x);
            foreach (int y in s_values)
            {
                int b = I(y);
                Mix(a * b);
                if (b == 0)
                    continue;
                uint ua = (uint)a, ub = (uint)b;
                Mix((int)(ua / ub));
                Mix((int)(ua % ub));
                identities &= (ua / ub) * ub + ua % ub == ua && ua % ub < ub;
                if (a == int.MinValue && b == -1)
                    continue;           // .NET throws OverflowException
                Mix(a / b);
                Mix(a % b);
                identities &= (a / b) * b + a % b == a && (a % b == 0 || (a % b < 0) == (a < 0));
            }
            // By constants: C# folds nothing here, the compiler may.
            Mix(a / 10);
            Mix(a % 10);
            Mix(a / -3);
            Mix(a * 37);
            Mix(a * -5);
            Mix((int)((uint)a / 40));
            Mix((int)((uint)a % 40));
        }
        Check("quotient * divisor + remainder == dividend, for every pair", identities);
        Check("the same results as .NET, for every pair", s_hash == 0xEF03E68Bu);
    }

    public static int Main()
    {
        s_never = false;
        int a = I(123456), b = I(-789);
        Check("int *", a * b == -97406784);
        Check("int / and %", a / b == -156 && a % b == 372 && I(-7) / 2 == -3 && I(-7) % 2 == -1);
        Check("int overflow wraps", I(int.MaxValue) + 1 == int.MinValue && I(65536) * 65536 == 0);
        Check("shifts", I(1) << 31 == int.MinValue && I(-16) >> 2 == -4 && I(-16) >>> 28 == 15);
        Check("uint", U(4000000000) / 3 == 1333333333 && U(4000000000) % 7 == 3 && U(4000000000) > U(1));
        Check("small types", (byte)I(300) == 44 && (sbyte)I(200) == -56 && (short)I(70000) == 4464
            && (ushort)I(-1) == 65535);
        Check("bits", (I(0xF0F0) & 0x0FF0) == 0x00F0 && (I(0xF0F0) | 0x0F) == 0xF0FF && (I(0xFF) ^ 0x0F) == 0xF0
            && ~I(0) == -1);
        Check("Math", Math.Abs(I(-5)) == 5 && Math.Min(I(3), 7) == 3 && Math.Max(I(3), 7) == 7
            && Math.Clamp(I(15), 0, 10) == 10);
        End("32-bit integers");

        long big = L(1234567890123);
        Check("long *", big * 1000 == 1234567890123000 && (long)I(100000) * I(100000) == 10000000000);
        Check("long / and %", big / 1000 == 1234567890 && big % 1000 == 123 && L(-7) / 2 == -3 && L(-7) % 2 == -1);
        Check("long shifts", L(1) << 40 == 1099511627776 && L(-1) >>> 60 == 15 && L(long.MinValue) >> 63 == -1);
        Check("ulong", UL(ulong.MaxValue) / 10 == 1844674407370955161 && UL(ulong.MaxValue) % 10 == 5);
        Check("long compares", L(-1) < L(1) && UL(ulong.MaxValue) > UL(1) && L(1L << 32) > L(int.MaxValue));
        Check("long to int", (int)L(0x1_2345_6789) == 0x2345_6789 && (long)I(-1) == -1 && (long)U(uint.MaxValue) == 4294967295);
        End("64-bit integers");

        DivisionTable();
        End("Division and multiplication tables");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
        {
            Console.Write(s_failures);
            Console.WriteLine(" checks FAILED.");
        }
        return s_failures;
    }
}
