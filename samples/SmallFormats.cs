// Test: numbers as text with .NET's format strings, sized for a small
// machine. Formats.cs checks far more, and is far too big for a Commodore 64
// (./build.sh --c64); this is the part that matters most on one: integers in
// hex and with separators, and string.Format. SmallDoubleFormats.cs has
// double and decimal, which need a machine to themselves. Every expected
// string is what .NET 10 prints. It runs on every target.
// Exit code = number of failed checks.
// Build: ./build.sh SmallFormats
//
// The Small samples print with Out.Write and Out.Line, not $"...": formatting
// a line costs a small machine several KB, and these are about what fits.

using System;
using System.Runtime.CompilerServices;

namespace SmallFormatsSample;

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

    static void Is(string text, string expected)
    {
        s_sectionChecks++;
        if (text != expected)
        {
            s_sectionFailures++;
            Console.Write("  failed: expected ");
            Console.Write(expected);
            Console.Write(", got ");
            Console.WriteLine(text);
        }
    }

    // Hide a value from the optimizer. (Always false: see SmallIntegers.cs.)
    static bool s_never;
    [MethodImpl(MethodImplOptions.NoInlining)] static int I(int value) => s_never ? 0 : value;
    [MethodImpl(MethodImplOptions.NoInlining)] static long L(long value) => s_never ? 0 : value;

    public static int Main()
    {
        s_never = false;

        Is(I(1234567).ToString("N0"), "1,234,567");
        Is(I(255).ToString("X4"), "00FF");
        Is(I(-1).ToString("x"), "ffffffff");
        Is(((sbyte)I(-2)).ToString("B"), "11111110");
        Is(I(-42).ToString("D8"), "-00000042");
        Is(L(long.MinValue).ToString("N0"), "-9,223,372,036,854,775,808");
        Is(I(12355).ToString("E2"), "1.24E+004");
        Is(I(25).ToString("G1"), "3E+01");
        End("integers");

        Is(string.Format("[{0,5}][{0,-5}][{1,6:X}]", I(42), I(255)), "[   42][42   ][    FF]");
        Is(string.Format("{{{0:X2}}}", I(171)), "{AB}");
        End("string.Format");

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
