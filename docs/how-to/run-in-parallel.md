---
title: Run a loop in parallel
index: 12
---

# Run a loop in parallel

`Parallel.For` from `lib/Threads.cs` runs a loop's iterations on every processor at once and returns
when all of them are done. The threads come from the system (pthreads on Linux and macOS, Windows
threads on Windows). The calling thread works too. On WebAssembly and DOS, which have no threads, the
same call runs the loop on one thread and gives the same answers. So does UEFI: a program there runs
on the one processor the firmware started it on, and `Parallel.ProcessorCount` is 1.
`samples/Threads.cs` runs every example below.

## Spread a loop

Give `For` the range, the state the iterations need, and a `static` lambda that takes both:

<!-- sample: samples/Threads.cs parallel-for -->
```csharp
// Each iteration writes its own element, so no two threads touch the
// same memory. The lambda is static: what it needs comes as state.
var squares = new Squares { Results = new long[2000] };
Parallel.For(0, 2000, squares,
    static (s, i) => s.Results[i] = SlowSquare(i));
```

The state is passed in rather than captured, so the call allocates nothing beyond the lambda, which
the compiler creates once. Starting the threads doesn't allocate either: the job they share lives on
the caller's stack.

Iterations are handed out in chunks from a shared counter, so a thread that finishes early takes more
work. On an 8-core Mac, a loop of uneven iterations ran 6.5 times faster than a plain `for`.

## Share a result

Iterations run at the same time, so they may only write memory that no other iteration touches, as
above, or go through `Interlocked`:

<!-- sample: samples/Threads.cs interlocked -->
```csharp
// A total that every iteration adds to needs Interlocked.
var counter = new Counter { Hits = new int[1] };
Parallel.For(0, 100000, counter,
    static (c, i) => Interlocked.Increment(ref c.Hits[0]));
```

## Give each thread its own arena

Arenas and `lib/`'s collections aren't thread-safe. Split the work into one part per processor and
let each part create its own arena. `AllocateObjectsHere` applies only to the thread that opens it,
so each part's ordinary allocations go to its own arena too:

<!-- sample: samples/Threads.cs arena-per-part -->
```csharp
// Arenas aren't thread-safe, so split the work into one part per
// processor and give each part an arena of its own. Inside it,
// AllocateObjectsHere sends that thread's ordinary allocations to the
// part's arena too: the setting belongs to the thread that makes it.
var parts = new Parts { Count = Parallel.ProcessorCount, PerPart = 500 };
parts.Sums = new long[parts.Count];
Action<Parts, int> sumPart = static (p, part) =>
{
    Arena arena = Arena.Create(4096);
    Arena.ObjectScope objects;
    // SAFETY: nothing allocated in the part outlives it.
    unsafe { objects = arena.AllocateObjectsHere(); }
    using (objects)
    {
        long sum = 0;
        for (int i = 0; i < p.PerPart; i++)
        {
            int[] scratch = new int[8]; // goes into this part's arena
            scratch[i % 8] = i;
            sum += scratch[i % 8];
        }
        p.Sums[part] = sum;
    }
    unsafe { arena.Destroy(); } // SAFETY: see above.
};
long heapBefore = GC.GetTotalAllocatedBytes(); // after creating the lambda
Parallel.For(0, parts.Count, parts, sumPart);
long heapGrowth = GC.GetTotalAllocatedBytes() - heapBefore;
```

## Give each thread its own variable

A `[ThreadStatic]` field has one copy per thread, starting at its default value on each, as in .NET:

<!-- sample: samples/Threads.cs thread-static -->
```csharp
// Each thread has its own copy of a [ThreadStatic] field, starting at the
// default value.
public static class PerThread
{
    [ThreadStatic] public static int Index;
    [ThreadStatic] public static string Name;
}
```

<!-- sample: samples/Threads.cs thread-static-loop -->
```csharp
// Each iteration stores its index, works, and finds it unchanged: no
// other thread writes this thread's copy. A plain static would be
// overwritten by the other threads now and then.
var clobbered = new Clobbered();
Parallel.For(0, 2000, clobbered, static (c, i) =>
{
    PerThread.Index = i;
    SlowSquare(i);
    if (PerThread.Index != i)
        Interlocked.Increment(ref c.Count);
});
```

Each thread's copies are allocated the first time it touches the type's thread-static fields, from
the system rather than from an arena (one reset would otherwise free them), and freed when the thread
ends. A static constructor still runs once, on whichever thread gets there first, so an initializer
on a `[ThreadStatic]` field sets only that thread's copy, as in .NET: set the value where each thread
starts instead.

## What isn't thread-safe

- **Arenas and collections**: one per thread, as above.
- **Output**: `Console` buffers, so write results after the loop rather than from inside it.

Allocation with `new`, interface calls, `Interlocked`, `Volatile` and static constructors are safe
on any thread: the first thread to reach a type runs its static constructor, and the others wait for
it to finish.
