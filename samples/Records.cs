// Example: records, anonymous types, ToString and `"n=" + 5` in zero mode.
// Exit code = number of failed checks.
// Build: ./build.sh Records
//
// They all rest on object's Equals, GetHashCode and ToString, which zerocore
// now has, with EqualityComparer<T>, StringBuilder and Type behind them.
// With no reflection, a Type has no name and a class prints as "object",
// but records print their own fields.

using System;

namespace RecordsSample;

// docs:records
public record struct Point(int X, int Y);

public record Person(string Name, int Age);

public record Employee(string Name, int Age, string Team) : Person(Name, Age);
// docs:end

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void Section(string section)
    {
        Console.WriteLine($"{(s_sectionFailures == 0 ? "PASS" : "FAIL")} {section} ({s_sectionChecks} checks)");
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_failures++;
            s_sectionFailures++;
            Console.WriteLine($"  failed: {name}");
        }
    }

    // Keeps the compiler from computing a value at build time.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static T Opaque<T>(T value) => value;

    public static int Main()
    {
        // docs:record-use
        var a = new Point(1, 2);
        var b = new Point(Opaque(1), 2);
        Point moved = a with { Y = 5 };
        Console.WriteLine(a.ToString());     // Point { X = 1, Y = 2 }
        Console.WriteLine(moved.ToString()); // Point { X = 1, Y = 5 }
        // docs:end
        Check("equal by value", a == b && a.Equals(b));
        Check("with makes a changed copy", moved.Y == 5 && a.Y == 2 && a != moved);
        Check("equal values hash alike", a.GetHashCode() == b.GetHashCode());
        Check("deconstruction", a is var (x, y) && x == 1 && y == 2);
        Section("record struct");

        var ann = new Person("Ann", Opaque(41));
        var ann2 = new Person("Ann", 41);
        var dev = new Employee("Ann", 41, "Compilers");
        Console.WriteLine(ann.ToString());
        Console.WriteLine(dev.ToString());
        Check("equal by value, not by reference", ann == ann2 && !ReferenceEquals(ann, ann2));
        Check("a derived record is never equal to its base", ann != dev && !ann.Equals(dev));
        Check("with on a class", (ann with { Age = 42 }).Age == 42 && ann.Age == 41);
        Check("typeof and GetType", ann.GetType() == typeof(Person) && dev.GetType() != typeof(Person));
        Section("record class");

        // docs:anonymous
        var item = new { Name = "pen", Price = 3 };
        var same = new { Name = "pen", Price = Opaque(3) };
        Console.WriteLine(item.ToString()); // { Name = pen, Price = 3 }
        // docs:end
        Check("anonymous types compare by value", item.Equals(same) && item.GetHashCode() == same.GetHashCode());
        Section("anonymous types");

        // docs:concat
        int count = Opaque(5);
        double ratio = Opaque(2.5);
        string line = "count=" + count + ", ratio=" + ratio + ", ok=" + true;
        // docs:end
        Console.WriteLine(line);
        Check("numbers in string +", line == "count=5, ratio=2.5, ok=True");
        Check("ToString of numbers", (-42).ToString() == "-42" && Opaque(0.1f).ToString() == "0.1"
            && Opaque(1e20).ToString() == "1E+20" && Opaque(0.000125).ToString() == "0.000125");
        Check("string.Format", string.Format("{0} + {1} = {2}", 1, 2, Opaque(3)) == "1 + 2 = 3");
        Section("text");

        // docs:compare-cost
        long before = GC.GetTotalAllocatedBytes();
        int equal = 0;
        for (int i = 0; i < 1000; i++)
            equal += a == b ? 1 : 0;
        long compareCost = GC.GetTotalAllocatedBytes() - before;
        // docs:end
        Check("comparing record structs allocates nothing", equal == 1000 && compareCost == 0);
        Section("cost");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine($"{s_failures} checks FAILED.");
        return s_failures;
    }
}
