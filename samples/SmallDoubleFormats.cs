// Test: doubles as text with .NET's format strings, sized for a small
// machine. A double is printed exactly, with big-number arithmetic that a
// 6502 does 8 bits at a time: on a Commodore 64 (./build.sh --c64) that is
// about 14 KB of code, so it has a sample to itself. SmallFormats.cs has the
// integers and string.Format, SmallDecimalFormats.cs decimal; Formats.cs
// checks far more. Every expected string is what .NET 10 prints. It runs on
// every target.
// Exit code = number of failed checks.
// Build: ./build.sh SmallDoubleFormats
//
// The Small samples print with Out.Write and Out.Line, not $"...": formatting
// a line costs a small machine several KB, and these are about what fits.

using System;
using System.Runtime.CompilerServices;

namespace SmallDoubleFormatsSample;

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
    [MethodImpl(MethodImplOptions.NoInlining)] static double D(double value) => s_never ? 0 : value;

    public static int Main()
    {
        s_never = false;

        Is(D(0.30000000000000004).ToString(), "0.30000000000000004");
        Is(D(1e17).ToString(), "1E+17");
        Is(D(double.Epsilon).ToString(), "5E-324");
        Is(D(0.125).ToString("F2"), "0.12");
        Is(D(2.675).ToString("F2"), "2.67");
        Is(D(0.1).ToString("F20"), "0.10000000000000000555");
        Is(D(1e23).ToString("F0"), "99999999999999991611392");
        Is(D(-1234.5).ToString("C"), "(¤1,234.50)");
        Is(D(0.1234).ToString("P1"), "12.3 %");
        Is(D(1234.56).ToString("E2"), "1.23E+003");
        Is((-D(0.001)).ToString("F2"), "-0.00");
        End("double");


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
