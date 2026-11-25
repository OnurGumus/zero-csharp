// Test: Encoding, sized for a small machine: UTF-8, UTF-16, ASCII and
// Latin-1, into spans and back, invalid input included. Unicode.cs checks
// far more; SmallUnicode.cs checks char's methods. It runs on every target.
// Exit code = number of failed checks.
// Build: ./build.sh SmallEncoding
//
// The Small samples print with Out.Write and Out.Line, not $"...": formatting
// a line costs a small machine several KB, and these are about what fits.

using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace SmallEncodingSample;

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

    static bool SameBytes(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        if (actual.Length != expected.Length)
            return false;
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] != expected[i])
                return false;
        }
        return true;
    }

    public static int Main()
    {
        s_never = false;

        Span<byte> bytes = stackalloc byte[16];
        Span<char> chars = stackalloc char[16];
        int written = Encoding.UTF8.GetBytes(Opaque("aé€😀"), bytes);
        Check("UTF-8 out", SameBytes(bytes.Slice(0, written),
            [0x61, 0xC3, 0xA9, 0xE2, 0x82, 0xAC, 0xF0, 0x9F, 0x98, 0x80]));
        int read = Encoding.UTF8.GetChars(bytes.Slice(0, written), chars);
        Check("UTF-8 back", read == 5 && chars[1] == 'é' && chars[3] == '\uD83D' && chars[4] == '\uDE00');
        read = Encoding.UTF8.GetChars([0x41, 0xE0, 0x80, 0xC3], chars);
        Check("invalid UTF-8", read == 4 && chars[0] == 'A' && chars[1] == '�' && chars[2] == '�'
            && chars[3] == '�');
        Check("a lone surrogate", Encoding.UTF8.GetBytes(Opaque("\uD800"), bytes) == 3 && bytes[0] == 0xEF);
        written = Encoding.Unicode.GetBytes(Opaque("é"), bytes);
        Check("UTF-16", written == 2 && bytes[0] == 0xE9 && bytes[1] == 0);
        written = Encoding.ASCII.GetBytes(Opaque("aé"), bytes);
        Check("ASCII", written == 2 && bytes[1] == '?');
        written = Encoding.Latin1.GetBytes(Opaque("éĀ"), bytes);
        Check("Latin-1", written == 2 && bytes[0] == 0xE9 && bytes[1] == 'A');
        Check("too small", !Encoding.UTF8.TryGetBytes(Opaque("😀"), bytes.Slice(0, 3), out _));
        End("Encoding");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine("Some checks FAILED.");
        return s_failures;
    }
}
