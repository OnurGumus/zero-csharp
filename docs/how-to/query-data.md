---
title: Query data without allocating
index: 10
---

# Query data without allocating

.NET's LINQ isn't available in zero mode, and it wouldn't fit if it were: each operator allocates an
iterator, each lambda that captures a variable allocates a closure, and without a garbage collector
none of that memory comes back. A query in a loop would make the program grow until it runs out.

`lib/Seq.cs` has the common operators with the same names (`Where`, `Select`, `Count`, `Sum`, `Any`,
`All`, `Take`, `Skip`, `Aggregate`, `TryFirst`, `GroupBy`, `ToList`) built so that a query allocates nothing
while it runs. `samples/Queries.cs` uses all of them and checks the allocation claim.

## Write a query

Queries start from a span, an array, a `List<T>` (through `AsSpan()`) or `Seq.Range`, and chain like
LINQ's:

<!-- sample: samples/Queries.cs operators -->
```csharp
// Operators chain like LINQ's. `static` lambdas are allocated once,
// the first time they run, and reused after that.
int evenSquares = numbers.Where(static x => x % 2 == 0)
                         .Select(static x => x * x)
                         .Sum();
int big = numbers.Where(static x => x > 4).Count();
```

Each operator returns a small struct that wraps the one before it, so the whole query is one value on
the stack, and `Sum`, `Count` or a `foreach` runs it. Nothing is allocated to build it or run it.

Mark lambdas `static`. The compiler allocates a lambda that captures nothing once, the first time it
runs, and reuses it after that. `static` makes sure it stays that way: a lambda that captures a local
by mistake becomes a compile error instead of a leak.

## Use a value from outside the lambda

A lambda that reads a local variable or a parameter captures it. The compiler then allocates a
closure to hold the variable each time the code that declares it runs, and a delegate with it: in a
method called in a loop, that's every call, and none of it is freed. Pass the value as state instead.
`Where` and `Select` take it as a first argument and hand it to the lambda:

<!-- sample: samples/Queries.cs state -->
```csharp
// A value from outside goes in as state, not as a capture.
int above = numbers.Where(threshold, static (t, x) => x > t).Count();
```

For comparison, this is what the state argument avoids:

<!-- sample: samples/Queries.cs capture-method -->
```csharp
// The trap: the lambda captures `limit`, so every call of CountAbove
// allocates a closure to hold `limit`, and a delegate, never freed.
static int CountAbove(int[] numbers, int limit)
    => numbers.Where(x => x > limit).Count();
```

The sample measures it:

<!-- sample: samples/Queries.cs capture -->
```csharp
before = GC.GetTotalAllocatedBytes();
for (int i = 0; i < 1000; i++)
    checksum += CountAbove(numbers, threshold);
long captured = GC.GetTotalAllocatedBytes() - before;
```

## Use query syntax

`from`, `where` and `select` work, because the compiler only looks for methods named `Where` and
`Select`:

<!-- sample: samples/Queries.cs query-syntax -->
```csharp
// Query syntax: the compiler turns this into Where and Select calls.
// Its lambdas can't be marked static, but these capture nothing.
var largeOrders = from order in s_orders
                  where order.Amount > 100
                  select order.Id;
foreach (int id in largeOrders)
    checksum += id;
```

Query syntax can't mark its lambdas `static`, so keep them free of captures yourself. `orderby`,
`group` and `join` aren't supported in query syntax: they need to collect the items somewhere, and
the syntax has no way to say where. For grouping, call `GroupBy` with an arena (below).

## Return or select several values

Anonymous types (`new { Name, Age }`) work, but each one is a class, so a query that selects one
allocates an object per item, never freed. Tuples do the same job as structs, without allocating. A
method can return one:

<!-- sample: samples/Queries.cs tuples -->
```csharp
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
```

And a query can select one where LINQ would use an anonymous type:

<!-- sample: samples/Queries.cs tuple-select -->
```csharp
// A tuple where LINQ would use an anonymous type.
foreach (var (customer, amount) in s_orders
             .Select(static o => (o.Customer, o.Amount))
             .Where(static p => p.Amount < 100))
{
    checksum += customer * amount;
}
```

Tuples are structs, so they don't allocate either. zerocore defines them with up to seven elements.

## Keep the results

A query reads its span as it runs and can't be stored in a field. To keep what it found, copy it into
a list in an arena with `ToList(arena)`, which returns a not-created list if the arena runs out of
memory.

## Group items by a key

`GroupBy` has to see every item before it can return a group, so it takes an arena, like `ToList`,
and builds the groups there: a `List` of `Group<TKey, T>`, each a `Key` and the `Items` that have it,
in the order the keys first appear. Resetting the arena frees them all:

<!-- sample: samples/Queries.cs group-by -->
```csharp
List<Group<int, Order>> byCustomer =
    s_orders.GroupBy(arena, static o => o.Customer);
foreach (Group<int, Order> group in byCustomer.AsQuery())
{
    int sum = group.Items.AsQuery().Select(static o => o.Amount).Sum();
    Console.WriteLine($"customer {group.Key}: total {sum}, "
        + $"count {group.Items.Count}");
}
```

The keys and the items must be unmanaged types, as in `List` and `Dict`, and keys are compared by
their bytes, as in `Dict`. `AsQuery()` turns a `List`, here each group's items, into a query.

## Check that a loop allocates nothing

`GC.GetTotalAllocatedBytes()` counts every byte allocated so far with `new`, boxing, closures,
delegates and string concatenation (arena blocks don't count). Compare it before and after:

<!-- sample: samples/Queries.cs allocation-check -->
```csharp
Arena scratch = Arena.Create(1024);
int expected = RunQueries(numbers, threshold, scratch); // allocates the lambdas
long before = GC.GetTotalAllocatedBytes();
int checksum = 0;
for (int i = 0; i < 1000; i++)
    checksum += RunQueries(numbers, threshold, scratch);
long allocated = GC.GetTotalAllocatedBytes() - before;
```

The first run is outside the measurement on purpose: that's when the compiler allocates the `static`
lambdas. After that, the sample's thousand runs allocate nothing.
