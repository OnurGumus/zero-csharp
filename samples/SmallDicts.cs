// Test: Dict<TKey, TValue> in an arena, sized for a small machine.
// Collections.cs checks more and doesn't fit in a Commodore 64
// (./build.sh --c64), where each kind of dictionary a program uses costs
// about 8 KB of the 50 there are: SmallStrDicts.cs has the one with string
// keys. It runs on every target. Exit code = number of failed checks.
// Build: ./build.sh SmallDicts

using System;

namespace SmallDictsSample;

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

    public static int Main()
    {
        Arena arena = Arena.Create(1024);
        Dict<int, int> squares = Dict<int, int>.Create(arena, 8);
        for (int i = 1; i <= 40; i++)
            squares.Set(i, i * i);
        Check("Set past the first capacity", squares.Count == 40);
        Check("TryGet", squares.TryGet(7, out int square) && square == 49 && !squares.TryGet(41, out square));
        squares.Set(7, -1);
        Check("Set replaces", squares.Count == 40 && squares.TryGet(7, out square) && square == -1);
        Check("Remove", squares.Remove(7) && !squares.ContainsKey(7) && !squares.Remove(7) && squares.Count == 39);
        int keys = 0;
        foreach (var pair in squares)
            keys += pair.Key;
        Check("foreach", keys == 820 - 7);
        Dict<int, int> none = default;
        Check("a dictionary that wasn't created", none.Count == 0 && !none.Set(1, 1) && !none.ContainsKey(1));
        End("Dict");

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
