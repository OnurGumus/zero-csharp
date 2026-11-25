// Test: floating point as text, sized for a small machine: Out's default
// format, fixed decimals and the special values. Its output is compared with
// tests/expected character for character, on every target.
//
// A Commodore 64 (./build.sh --c64) has no floating point at all: every
// operation is software, the compiler's or c64/runtime.c's, and that software
// and this formatting take 46 of its 50 KB. Reading a double from text
// (Str.TryParse) as well doesn't fit. SmallDoubles.cs checks the arithmetic.
// Build: ./build.sh SmallFloats

using System;
using System.Runtime.CompilerServices;

namespace SmallFloatsSample;

public static class Program
{
    // Hides a value from the optimizer, which would otherwise format the
    // constants at build time where it can.
    // Always false: see SmallIntegers.cs. It keeps a compiler that optimizes
    // the whole program at once from seeing through the method below.
    static bool s_never;
    [MethodImpl(MethodImplOptions.NoInlining)] static double D(double value) => s_never ? 0 : value;

    public static int Main()
    {
        s_never = false;
        Console.WriteLine($"double: {D(1.5)} {D(-0.25)} {D(100)} {D(0.1)} {D(1e21)}");
        Console.WriteLine($"rounded: {D(3.14159):f2} {D(2.5):f0} {D(1234.5678):f1} {D(-0.005):f2}");
        Console.WriteLine($"float: {(float)D(1.5)} {(float)D(0.1)}");
        Console.WriteLine($"special: {D(double.NaN)} {D(double.PositiveInfinity)} {D(0)} {D(1e-7)}");

        return 0;
    }
}
