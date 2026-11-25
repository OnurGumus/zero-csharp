// Test: char's methods, sized for a small machine. Unicode.cs checks every
// character against .NET; this one checks the first 1,280 (Latin to
// Cyrillic) and a few beyond, so the Unicode tables are read on an 8-bit
// processor too. SmallEncoding.cs checks Encoding. It runs on every target.
// Exit code = number of failed checks.
// Build: ./build.sh SmallUnicode
//
// The Small samples print with Out.Write and Out.Line, not $"...": formatting
// a line costs a small machine several KB, and these are about what fits.

using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace SmallUnicodeSample;

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

    // Always false: see SmallIntegers.cs. It keeps a compiler that optimizes
    // the whole program at once from seeing through the methods below.
    static bool s_never;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Opaque(string text) => s_never ? null : text;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static char Opaque(char c) => s_never ? 'x' : c;

    static uint Mix(uint hash, uint value) => (hash ^ value) * 16777619;

    public static int Main()
    {
        s_never = false;

        // Hashes of U+0000 to U+04FF, as .NET 10 gives them.
        uint categories = 2166136261, upper = 2166136261, lower = 2166136261;
        for (int c = 0; c < 0x500; c++)
        {
            char ch = Opaque((char)c);
            categories = Mix(categories, (uint)char.GetUnicodeCategory(ch));
            upper = Mix(upper, char.ToUpper(ch));
            lower = Mix(lower, char.ToLower(ch));
        }
        Check("categories to U+04FF", categories == 0x7b039e79);
        Check("ToUpper to U+04FF", upper == 0xae734cbd);
        Check("ToLower to U+04FF", lower == 0x713ed5ef);
        Check("letters", char.IsLetter(Opaque('é')) && char.IsLetter(Opaque('中')) && !char.IsLetter(Opaque('1')));
        Check("digits and numbers", char.IsDigit(Opaque('٣')) && char.IsNumber(Opaque('½')) && !char.IsDigit(Opaque('½')));
        Check("case beyond Cyrillic", char.ToUpper(Opaque('ա')) == 'Ա' && char.ToLower(Opaque('Ａ')) == 'ａ');
        Check("ASCII methods", char.IsAsciiHexDigit(Opaque('F')) && !char.IsAsciiLetter(Opaque('é')));
        Check("white space", char.IsWhiteSpace(Opaque('　')) && !char.IsWhiteSpace(Opaque('​')));
        Check("a surrogate pair", char.IsUpper(Opaque("𐐀"), 0) && char.ConvertToUtf32(Opaque("𐐀"), 0) == 0x10400);
        End("char");


        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine("Some checks FAILED.");
        return s_failures;
    }
}
