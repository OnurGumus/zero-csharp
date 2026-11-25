// Test: strings and spans of characters, sized for a small machine.
// Strings.cs checks far more, and is far too big for a Commodore 64
// (./build.sh --c64); the Small samples are its ground in pieces that each
// fit. This one needs no arena: comparing, searching, splitting, parsing.
// SmallBuilders.cs makes new strings. It runs on every target.
// Exit code = number of failed checks.
// Build: ./build.sh SmallStrings
//
// The Small samples print with Out.Write and Out.Line, not $"...": formatting
// a line costs a small machine several KB, and these are about what fits.

using System;
using System.Runtime.CompilerServices;

namespace SmallStringsSample;

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

    // Hides a value from the optimizer, which would otherwise answer most
    // checks at build time.
    // Always false: see SmallIntegers.cs. It keeps a compiler that optimizes
    // the whole program at once from seeing through the method below.
    static bool s_never;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Opaque(string text) => s_never ? null : text;

    static int Classify(string word)
    {
        switch (word)
        {
            case "alpha": return 1;
            case "beta": return 2;
            case "gamma": return 3;
            default: return 0;
        }
    }

    static string s_label = "initial";

    public static int Main()
    {
        s_never = false;
        string hello = Opaque("hello");
        Check("Length", hello.Length == 5);
        Check("indexer", hello[0] == 'h' && hello[4] == 'o');
        Check("== compares the text", hello == Opaque("hello") && hello != Opaque("help"));
        Check("null and empty", Opaque(null) == null && Opaque("") != null && string.IsNullOrEmpty(Opaque("")));
        Check("switch", Classify(Opaque("gamma")) == 3 && Classify(Opaque("omega")) == 0);
        Check("static field", s_label == "initial");
        s_label = hello;
        Check("static field, assigned", s_label == "hello");
        Check("characters outside ASCII", Opaque("né☃").Length == 3 && Opaque("né☃")[1] == 'é');
        End("string");

        ReadOnlySpan<char> text = Opaque("  key = value  ").Trim();
        Check("Trim", text.SequenceEqual("key = value"));
        int equals = text.IndexOf('=');
        Check("IndexOf", equals == 4 && text.IndexOf("value") == 6 && text.IndexOf('z') == -1);
        Check("Slice, TrimEnd", text.Slice(0, equals).TrimEnd().SequenceEqual("key"));
        Check("StartsWith, EndsWith", text.StartsWith("key") && text.EndsWith("value") && !text.StartsWith("value"));
        Check("Contains", text.Contains("= v") && !text.Contains('z'));
        Check("LastIndexOf", text.LastIndexOf('e') == 10);
        Check("Compare", Str.Compare("apple", "banana") < 0 && Str.Compare("b", "a") > 0 && Str.Compare("same", "same") == 0);
        int parts = 0, total = 0;
        foreach (ReadOnlySpan<char> part in Opaque("10,20,,30").Split(','))
        {
            parts++;
            if (Str.TryParse(part, out int number))
                total += number;
        }
        Check("Split", parts == 4 && total == 60);
        End("spans");

        Check("TryParse int", Str.TryParse(Opaque("-12345"), out int i) && i == -12345);
        Check("TryParse long", Str.TryParse(Opaque("9223372036854775807"), out long l) && l == long.MaxValue);
        Check("TryParse rejects", !Str.TryParse(Opaque("12x"), out i) && !Str.TryParse(Opaque(""), out i)
            && !Str.TryParse(Opaque("99999999999"), out i));
        Check("TryParseHex", Str.TryParseHex(Opaque("ff80"), out ulong hex) && hex == 0xFF80);
        End("parsing");

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
