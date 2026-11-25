// Test: List<T> in an arena, sized for a small machine. Collections.cs
// checks more and doesn't fit in a Commodore 64 (./build.sh --c64).
// It runs on every target. Exit code = number of failed checks.
// Build: ./build.sh SmallLists

using System;

namespace SmallListsSample;

public struct Item
{
    public int Id;
    public short Weight;
}

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

    static int Descending(int a, int b) => b - a;

    static int ByWeight(Item a, Item b) => a.Weight - b.Weight;

    static int Sum(ReadOnlySpan<int> values)
    {
        int sum = 0;
        foreach (int value in values)
            sum += value;
        return sum;
    }

    public static int Main()
    {
        Arena arena = Arena.Create(1024);
        List<int> list = List<int>.Create(arena, 4);
        for (int i = 0; i < 20; i++)
            list.Add((i * 7) % 20);
        Check("Add past the first capacity", list.Count == 20 && list.Capacity >= 20 && list.Get(3) == 1);
        list.Set(0, 99);
        Check("Set, Get", list.Get(0) == 99);
        Check("RemoveAt", list.RemoveAt(0) && list.Count == 19 && list.Get(0) == 7);
        unsafe { list.Sort(&Descending); } // SAFETY: a static method's address.
        Check("Sort", list.Get(0) == 19 && list.Get(18) == 1);
        Span<int> view;
        unsafe { view = list.AsSpan(); } // SAFETY: used before the list grows or is cleared.
        Check("AsSpan", view.Length == 19 && Sum(view) == 190);
        list.Clear();
        Check("Clear", list.Count == 0 && !list.RemoveAt(0));

        List<Item> items = List<Item>.Create(arena);
        items.Add(new Item { Id = 1, Weight = 30 });
        items.Add(new Item { Id = 2, Weight = -5 });
        items.Add(new Item { Id = 3, Weight = 12 });
        unsafe { items.Sort(&ByWeight); } // SAFETY: a static method's address.
        Check("list of structs, sorted by a field", items.Get(0).Id == 2 && items.Get(1).Id == 3 && items.Get(2).Id == 1);
        List<int> none = default;
        Check("a list that wasn't created", none.Count == 0 && !none.Add(1));
        End("List");

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
