// Test: what Out prints, sized for a small machine: whole numbers in every
// width and base, alignment and text, through $"..." and through Console.Write.
// Its output is compared with tests/expected character for character, on
// every target; on a Commodore 64 (./build.sh --c64) the 64-bit divisions
// behind it run on an 8-bit processor. SmallFloats.cs prints floating point.
// Build: ./build.sh SmallText

using System;
using System.Runtime.CompilerServices;

namespace SmallTextSample;

public static class Program
{
    // Hides a value from the optimizer, which would otherwise format the
    // constants at build time where it can.
    // Always false: see SmallIntegers.cs. It keeps a compiler that optimizes
    // the whole program at once from seeing through the method below.
    static bool s_never;
    [MethodImpl(MethodImplOptions.NoInlining)] static long L(long value) => s_never ? 0 : value;

    public static int Main()
    {
        s_never = false;
        int answer = (int)L(42);
        Console.WriteLine($"int: {answer}, negative: {-answer}, zero: {(int)L(0)}");
        Console.WriteLine($"limits: {(int)L(int.MinValue)} to {(int)L(int.MaxValue)}");
        Console.WriteLine($"long: {L(long.MinValue)} to {L(long.MaxValue)}");
        Console.WriteLine($"ulong: {(ulong)L(-1)}, uint: {(uint)L(4000000000)}");
        Console.WriteLine($"small: {(byte)L(255)} {(sbyte)L(-128)} {(short)L(-32768)} {(ushort)L(65535)}");
        Console.WriteLine($"hex: {answer:x} {255:X} {answer:x4} {L(0xDEADBEEF):x8} {L(-1):x}");
        Console.WriteLine($"padded: [{answer,6}] [{answer,-6}] [{-answer,6}] [{answer:d5}]");
        Console.WriteLine($"text: [{"left",-8}] [{"right",8}] {'c'} {true} {false}");

        Console.Write("Write: ");
        Console.Write(answer);
        Console.Write(' ');
        Console.Write(L(-9000000000));
        Console.Write(' ');
        Console.Write(true);
        Console.WriteLine();
        return 0;
    }
}
