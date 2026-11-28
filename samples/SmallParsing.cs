// Test: text to integers with .NET's Parse and TryParse, sized for a small
// machine. Parsing.cs checks far more, doubles too, and is far too big for a
// Commodore 64 (./build.sh --c64): double.Parse alone doesn't fit in one
// beside a test, as a 6502 does its 64-bit and floating-point arithmetic a
// byte at a time. Every expected value is what .NET 10 gives. It runs on
// every target.
// Exit code = number of failed checks.
// Build: ./build.sh SmallParsing
//
// The Small samples print with Console.Write pieces, not $"...": formatting
// a line costs a small machine several KB, and these are about what fits.

using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace SmallParsingSample;

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

    // Text the optimizer can't see through. (Always false: see SmallIntegers.cs.)
    static bool s_never;
    [MethodImpl(MethodImplOptions.NoInlining)] static string T(string text) => s_never ? "" : text;

    public static int Main()
    {
        s_never = false;

        Check("int", int.Parse(T("42")) == 42);
        Check("whitespace and a sign", int.Parse(T(" -17 ")) == -17);
        Check("int.MinValue", int.Parse(T("-2147483648")) == int.MinValue);
        Check("int too large", !int.TryParse(T("2147483648"), out int value) && value == 0);
        Check("not a number", !int.TryParse(T("4x"), out value));
        Check("long", long.Parse(T("-9223372036854775808")) == long.MinValue);
        Check("ulong", ulong.Parse(T("18446744073709551615")) == ulong.MaxValue);
        Check("byte", byte.Parse(T("255")) == 255 && !byte.TryParse(T("256"), out _));
        Check("hex", int.Parse(T("7fffFFFF"), NumberStyles.HexNumber) == int.MaxValue);
        Check("hex is the bits", short.Parse(T("FFFF"), NumberStyles.HexNumber) == -1);
        Check("thousands", int.Parse(T("1,234"), NumberStyles.Number) == 1234);
        End("integers");


        Console.WriteLine(s_failures == 0 ? "All checks passed." : "Some checks failed.");
        return s_failures;
    }
}
