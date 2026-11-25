// Example: LINQ-style queries (lib/Seq.cs) and tuples, and proof that they
// allocate nothing while they run. Exit code = number of failed checks.
// Build: ./build.sh Queries
//
// Without a garbage collector, memory a query allocates is never given back.
// So the checks at the end run every query a thousand times and compare
// GC.GetTotalAllocatedBytes() before and after: the operators and `static`
// lambdas must not allocate at all. A lambda that captures a parameter of a
// method allocates on every call of that method, which the last checks show,
// unless the call runs in an arena's object scope.

using System;

namespace QueriesSample;

public struct Order
{
    public int Id;
    public int Customer;
    public int Amount;
}

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
    static int Opaque(int value) => value;

    static readonly Order[] s_orders =
    {
        new Order { Id = 1, Customer = 7, Amount = 120 },
        new Order { Id = 2, Customer = 3, Amount = 40 },
        new Order { Id = 3, Customer = 7, Amount = 300 },
        new Order { Id = 4, Customer = 5, Amount = 75 },
        new Order { Id = 5, Customer = 3, Amount = 210 },
    };

    // docs:tuples
    // A tuple return: two results without an out parameter or a new type.
    static (int Count, int Total) Totals(ReadOnlySpan<Order> orders, int customer)
    {
        int count = 0, total = 0;
        foreach (Order order in orders)
        {
            if (order.Customer == customer)
            {
                count++;
                total += order.Amount;
            }
        }
        return (count, total);
    }
    // docs:end

    // docs:capture-method
    // The trap: the lambda captures `limit`, so every call of CountAbove
    // allocates a closure to hold `limit`, and a delegate, never freed.
    static int CountAbove(int[] numbers, int limit)
        => numbers.Where(x => x > limit).Count();
    // docs:end

    // Every query the sample runs, so the allocation check can repeat them.
    // Returns a checksum of the results.
    static int RunQueries(int[] numbers, int threshold, Arena scratch)
    {
        int checksum = 0;

        // docs:operators
        // Operators chain like LINQ's. `static` lambdas are allocated once,
        // the first time they run, and reused after that.
        int evenSquares = numbers.Where(static x => x % 2 == 0)
                                 .Select(static x => x * x)
                                 .Sum();
        int big = numbers.Where(static x => x > 4).Count();
        // docs:end
        checksum += evenSquares + big;

        // docs:state
        // A value from outside goes in as state, not as a capture.
        int above = numbers.Where(threshold, static (t, x) => x > t).Count();
        // docs:end
        checksum += above;

        // docs:query-syntax
        // Query syntax: the compiler turns this into Where and Select calls.
        // Its lambdas can't be marked static, but these capture nothing.
        var largeOrders = from order in s_orders
                          where order.Amount > 100
                          select order.Id;
        foreach (int id in largeOrders)
            checksum += id;
        // docs:end

        // docs:tuple-select
        // A tuple where LINQ would use an anonymous type.
        foreach (var (customer, amount) in s_orders
                     .Select(static o => (o.Customer, o.Amount))
                     .Where(static p => p.Amount < 100))
        {
            checksum += customer * amount;
        }
        // docs:end

        checksum += Seq.Range(1, 10).Skip(2).Take(3).Sum();
        checksum += Seq.Range(1, 5).Aggregate(1, static (product, x) => product * x);

        // GroupBy builds its groups in an arena; resetting it frees them.
        foreach (Group<int, Order> group in s_orders.GroupBy(scratch, static o => o.Customer).AsQuery())
            checksum += group.Key * group.Items.Count;
        unsafe { scratch.Reset(); } // SAFETY: the groups aren't used after this.
        return checksum;
    }

    public static int Main()
    {
        int[] numbers = { 5, 3, 8, 1, 9, 2, 7, 4 };
        int threshold = Opaque(4);

        Check("Where, Select, Sum", numbers.Where(static x => x % 2 == 0).Select(static x => x * x).Sum() == 64 + 4 + 16);
        Check("Count", numbers.Where(static x => x > 4).Count() == 4);
        Check("Where with state", numbers.Where(threshold, static (t, x) => x > t).Count() == 4);
        Check("Any, All", numbers.AsQuery().Any(static x => x == 9) && !numbers.AsQuery().All(static x => x > 1));
        Check("TryFirst", numbers.Where(static x => x > 7).TryFirst(out int first) && first == 8);
        Check("empty TryFirst", !numbers.Where(static x => x > 100).TryFirst(out _));
        Check("Skip, Take", Seq.Range(1, 10).Skip(2).Take(3).Sum() == 3 + 4 + 5);
        Check("Aggregate", Seq.Range(1, 5).Aggregate(1, static (product, x) => product * x) == 120);
        Section("operators");

        int ids = 0;
        foreach (int id in from order in s_orders where order.Amount > 100 select order.Id)
            ids = ids * 10 + id;
        Check("where and select", ids == 135);
        Section("query syntax");

        var (count, total) = Totals(s_orders, Opaque(7));
        Check("tuple return and deconstruction", count == 2 && total == 420);
        (int, int, int, int) four = (1, 2, 3, 4);
        Check("four elements", four.Item1 + four.Item4 == 5);
        int small = 0;
        foreach (var (customer, amount) in s_orders.Select(static o => (o.Customer, o.Amount)).Where(static p => p.Amount < 100))
            small += customer * amount;
        Check("tuples in a query", small == 3 * 40 + 5 * 75);
        Section("tuples");

        Arena arena = Arena.Create(1024);
        List<int> odd = numbers.Where(static x => x % 2 == 1).ToList(arena);
        Check("ToList", odd.Count == 5 && odd.Get(0) == 5 && odd.Get(4) == 7);
        Section("keeping results");

        // docs:group-by
        List<Group<int, Order>> byCustomer =
            s_orders.GroupBy(arena, static o => o.Customer);
        foreach (Group<int, Order> group in byCustomer.AsQuery())
        {
            int sum = group.Items.AsQuery().Select(static o => o.Amount).Sum();
            Console.WriteLine($"customer {group.Key}: total {sum}, "
                + $"count {group.Items.Count}");
        }
        // docs:end
        Check("one group per customer", byCustomer.Count == 3);
        Check("in order of first appearance", byCustomer.Get(0).Key == 7 && byCustomer.Get(2).Key == 5);
        Check("items keep their order", byCustomer.Get(1).Items.Get(0).Id == 2 && byCustomer.Get(1).Items.Get(1).Id == 5);
        Section("grouping");

        // docs:allocation-check
        Arena scratch = Arena.Create(1024);
        int expected = RunQueries(numbers, threshold, scratch); // allocates the lambdas
        long before = GC.GetTotalAllocatedBytes();
        int checksum = 0;
        for (int i = 0; i < 1000; i++)
            checksum += RunQueries(numbers, threshold, scratch);
        long allocated = GC.GetTotalAllocatedBytes() - before;
        // docs:end
        Check("same answers every time", checksum == expected * 1000);
        Check("a thousand runs allocate nothing", allocated == 0);

        // docs:capture
        before = GC.GetTotalAllocatedBytes();
        for (int i = 0; i < 1000; i++)
            checksum += CountAbove(numbers, threshold);
        long captured = GC.GetTotalAllocatedBytes() - before;
        // docs:end
        // At least 16 bytes a call: a closure and a delegate, on any target.
        Check("a capturing lambda allocates on every call", captured >= 16 * 1000);

        // docs:capture-in-scope
        // In an arena's object scope the closure and delegate go into the
        // arena, and each Reset frees them. With DebugChecks on, closing the
        // scope also checks that no static field points into the arena: the
        // lambdas the compiler caches in static fields all ran before, so
        // none does.
        Arena.DebugChecks = true;
        Arena frame = Arena.Create(4096, "frame");
        Arena.DebugChecks = false;
        before = GC.GetTotalAllocatedBytes();
        for (int i = 0; i < 1000; i++)
        {
            Arena.ObjectScope objects;
            // SAFETY: nothing created inside is kept past the Reset.
            unsafe { objects = frame.AllocateObjectsHere(); }
            using (objects)
                checksum += CountAbove(numbers, threshold);
            unsafe { frame.Reset(); } // SAFETY: see above.
        }
        long scoped = GC.GetTotalAllocatedBytes() - before;
        // docs:end
        Check("the same lambda in an object scope leaves nothing behind", scoped == 0);
        Section("allocation");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine($"{s_failures} checks FAILED.");
        return s_failures;
    }
}
