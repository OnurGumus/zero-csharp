// Example: Growable arena allocator (lib/Arena.cs) in zero mode.
// Build: ./build.sh Arena

using System;

namespace ArenaSample;

public struct Point
{
    public int X;
    public int Y;
    public Point* Next;
}

public struct Entity
{
    public int Id;
    public Point Position;
    public Point Velocity;
}

public static class Program
{
    public static int Main()
    {
        Console.WriteLine("=== Growable Arena Demo ===");

        // The name is optional: Dump() and failure messages show it.
        Arena arena = Arena.Create(128, "demo");
        if (!arena.IsCreated)
        {
            Console.WriteLine("Arena.Create failed");
            return 1;
        }
        Console.WriteLine($"Initial block size: {(long)arena.CurrentBlockSize}");

        // Allocation is safe; reading and writing through the pointers needs unsafe.
        Point* p1 = arena.Alloc<Point>();
        Point* p2 = arena.Alloc<Point>();
        Point* p3 = arena.Alloc<Point>();

        unsafe
        {
            p1->X = 10; p1->Y = 20; p1->Next = p2;
            p2->X = 30; p2->Y = 40; p2->Next = p3;
            p3->X = 50; p3->Y = 60; p3->Next = null;

            Console.WriteLine();
            Console.WriteLine("Walking linked points p1 -> p2 -> p3:");
            int index = 1;
            for (Point* current = p1; current != null; current = current->Next)
            {
                Console.Write("  Point ");
                Console.Write(index++);
                Console.Write(": (");
                Console.Write(current->X);
                Console.Write(", ");
                Console.Write(current->Y);
                Console.WriteLine(")");
            }
        }

        // docs:alloc-and-use
        Entity* e = arena.Alloc<Entity>();
        int* squares = arena.AllocArray<int>(10);
        unsafe
        {
            e->Id = 1;
            e->Position.X = 100;
            for (int i = 0; i < 10; i++)
                squares[i] = i * i;
            Console.WriteLine($"squares[5] = {squares[5]}");
        }
        // docs:end
        Console.WriteLine($"Used: {(long)arena.Used} bytes | Blocks: {arena.BlockCount}");

        // Start over with a single 128-byte block to show the growth sequence.
        unsafe { arena.Reset(keepCapacity: false); } // SAFETY: p1..p3, e and squares are not used below.

        Console.WriteLine();
        Console.WriteLine("Normal blocks grow like Fibonacci: 128, 128, 256, 384, 640, 1024");
        Console.WriteLine($"  Block 1: {(long)arena.CurrentBlockSize}");
        while (arena.BlockCount < 6)
        {
            int before = arena.BlockCount;
            if (arena.AllocArray<byte>(100) == null)
                return 1;
            if (arena.BlockCount != before)
                Console.WriteLine($"  Block {arena.BlockCount}: {(long)arena.CurrentBlockSize}");
        }

        // A large allocation gets its own block and doesn't disturb the sequence.
        nuint nextBefore = arena.NextBlockSize;
        byte* big = arena.AllocArray<byte>(4096);
        Console.WriteLine();
        Console.WriteLine($"4096-byte allocation -> dedicated block of {(long)arena.CurrentBlockSize}");
        Console.WriteLine($"Next normal block: before {(long)nextBefore}, after {(long)arena.NextBlockSize}");

        // Alignment is per allocation, e.g. 64 bytes for SIMD-friendly buffers.
        float* weights = arena.AllocArray<float>(16, align: 64);
        Console.WriteLine($"64-byte aligned buffer, address % 64 = {(long)((nuint)weights % 64)}");

        // Bad requests return null instead of corrupting memory. (So does malloc
        // failure; that path is easiest to see with `ulimit -v`, not in a demo.)
        Console.Write("Bad alignment (3) returns null: ");
        Console.Write(arena.Alloc(16, align: 3) == null);
        Console.WriteLine();
        Console.Write("Negative count returns null: ");
        Console.Write(arena.AllocArray<int>(-1) == null);
        Console.WriteLine();

        // Reset keeps the capacity: all blocks merge into one, so the next round won't malloc.
        nuint capacity = arena.Capacity;
        unsafe { arena.Reset(); } // SAFETY: big and weights are not used below.
        Console.WriteLine();
        Console.Write("After Reset: blocks ");
        Console.Write(arena.BlockCount);
        Console.Write(", capacity kept: ");
        Console.Write(arena.Capacity == capacity);
        Console.WriteLine();
        Console.WriteLine($"Capacity: {(long)arena.Capacity} bytes, used: {(long)arena.Used}");

        Point* p4 = arena.Alloc<Point>();
        unsafe
        {
            p4->X = 999;
            Console.WriteLine($"New point X: {p4->X}");
        }

        // Save/Restore frees only what came after the mark: temporary work in
        // an arena that also holds data to keep.
        nuint usedBefore = arena.Used;
        Arena.Mark mark = arena.Save();
        int* scratch = arena.AllocArray<int>(4000);
        unsafe { scratch[3999] = 7; }
        Console.WriteLine();
        Console.WriteLine($"Scratch work: used {(long)arena.Used} bytes in {arena.BlockCount} blocks");
        unsafe { arena.Restore(mark); } // SAFETY: scratch is not used below.
        Console.Write("After Restore: used as before: ");
        Console.Write(arena.Used == usedBefore);
        Console.Write(", blocks ");
        Console.Write(arena.BlockCount);
        unsafe
        {
            Console.Write(", p4 intact: ");
            Console.Write(p4->X == 999);
        }
        Console.WriteLine();

        // OpenScope is Save and Restore as a using block: leaving the block, by
        // any path, frees what was allocated inside it.
        Arena.Scope temp;
        unsafe { temp = arena.OpenScope(); } // SAFETY: nothing from inside the block is used after it.
        using (temp)
        {
            arena.AllocArray<int>(100);
            Console.WriteLine($"Inside the scope: used {(long)arena.Used} bytes");
        }
        Console.Write("After the scope: used as before: ");
        Console.Write(arena.Used == usedBefore);
        Console.WriteLine();

        // Dump shows where the memory is. The peak is the most the arena ever
        // held at once: a first block of that size would have been enough.
        arena.Dump();

        // The newest allocation can grow in place, so a buffer built up step by
        // step doesn't leave its smaller copies behind.
        int* growing = arena.AllocArray<int>(4);
        nuint usedSmall = arena.Used;
        bool extended = arena.TryExtend(growing, 4 * sizeof(int), 8 * sizeof(int));
        Console.Write("Extend newest allocation in place: ");
        Console.Write(extended && arena.Used == usedSmall + 4 * sizeof(int));
        Console.WriteLine();
        arena.Alloc<Point>();
        Console.Write("Extend after another allocation: ");
        Console.Write(arena.TryExtend(growing, 8 * sizeof(int), 16 * sizeof(int)));
        Console.WriteLine();

        // docs:second-arena
        // Arenas are independent: one per lifetime. Here a second arena holds a
        // list for a while and is dropped without touching the first.
        Arena frame = Arena.Create(256);
        List<int> numbers = List<int>.Create(frame);
        for (int i = 0; i < 100; i++)
            numbers.Add(i);
        // docs:end
        Console.WriteLine();
        Console.WriteLine($"Second arena: list of {numbers.Count} ints, {(long)frame.Used} bytes used");
        usedBefore = arena.Used;
        unsafe { frame.Destroy(); } // SAFETY: numbers is not used below.
        Console.Write("First arena untouched: ");
        Console.Write(arena.Used == usedBefore);
        Console.WriteLine();

        unsafe { arena.Destroy(); } // SAFETY: nothing points into the arena after this.
        Console.WriteLine("Arena destroyed.");

        // docs:object-scope
        // Ordinary C# allocates with calloc and never frees. Inside
        // AllocateObjectsHere the same code allocates in the arena, so
        // resetting it frees everything the round created.
        Arena rounds = Arena.Create(4096);
        long heapBefore = System.GC.GetTotalAllocatedBytes();
        int total = 0;
        for (int round = 0; round < 1000; round++)
        {
            Arena.ObjectScope objects;
            // SAFETY: nothing created in the round is kept past the Reset.
            unsafe { objects = rounds.AllocateObjectsHere(); }
            using (objects)
            {
                int[] cells = new int[16];            // an array
                int offset = round;
                System.Func<int, int> shift = x => x + offset; // closure, delegate
                string text = Echo("ab") + Echo("cd"); // a new string
                total += shift(cells.Length) + text.Length;
            }
            unsafe { rounds.Reset(); } // SAFETY: see above.
        }
        long heapGrowth = System.GC.GetTotalAllocatedBytes() - heapBefore;
        // docs:end
        Console.WriteLine();
        Console.WriteLine($"Objects in an arena, 1000 rounds: heap growth {heapGrowth} bytes, arena blocks {rounds.BlockCount}");
        Console.Write("Every round ran: ");
        Console.Write(total == 1000 * (16 + 4) + 999 * 1000 / 2);
        Console.WriteLine();
        unsafe { rounds.Destroy(); } // SAFETY: nothing points into it.

        // docs:object-scope-free
        // OpenObjectScope also frees what the block created when it closes,
        // so objects live exactly as long as the using block. The arena can
        // hold longer-lived data too: only what came after the scope opened
        // is freed.
        Arena scoped = Arena.Create(4096);
        int* kept = scoped.Alloc<int>();       // lives past every scope
        unsafe { *kept = 42; } // SAFETY: kept points at an int in scoped.
        nuint usedOutside = scoped.Used;
        long heapBeforeFrames = System.GC.GetTotalAllocatedBytes();
        int frameTotal = 0;
        for (int round = 0; round < 1000; round++)
        {
            Arena.ObjectScope objects;
            // SAFETY: nothing created inside is used after the block.
            unsafe { objects = scoped.OpenObjectScope(); }
            using (objects)
            {
                int[] cells = new int[16];
                string text = Echo("ab") + Echo("cd");
                frameTotal += cells.Length + text.Length;
            }   // cells and text are freed here
        }
        long frameHeapGrowth = System.GC.GetTotalAllocatedBytes() - heapBeforeFrames;
        // docs:end
        Console.WriteLine($"Objects freed with their scope, 1000 rounds: heap growth {frameHeapGrowth} bytes, arena blocks {scoped.BlockCount}");
        Console.Write("Only the scopes' objects freed: ");
        bool keptIntact;
        unsafe { keptIntact = *kept == 42; } // SAFETY: kept is still allocated.
        Console.Write(frameTotal == 1000 * 20 && scoped.Used == usedOutside && keptIntact);
        Console.WriteLine();
        unsafe { scoped.Destroy(); } // SAFETY: nothing points into it.

        // docs:max-capacity
        // A budget: past 64 KB of blocks, allocations fail as if memory had
        // run out, even on systems where malloc never says no.
        Arena request = Arena.Create(4096, "request", maxCapacity: 64 * 1024);
        int* small = request.AllocArray<int>(1000);      // fine
        int* huge = request.AllocArray<int>(100_000);    // null: over budget
        // docs:end
        Console.WriteLine();
        Console.WriteLine($"maxCapacity 65536: 4000 bytes {(small != null ? "allocated" : "refused")}, 400000 bytes {(huge != null ? "allocated" : "refused")}, capacity {request.Capacity}");
        unsafe { request.Destroy(); } // SAFETY: small isn't used after this.

        InBuffer();
        ObjectScopeEdges();
        WithGuardPages();

        // docs:debug-checks
        // Debugging aid: arenas created while DebugChecks is on fill freed memory
        // with 0xDD, so a stale pointer reads an obvious pattern instead of the
        // old value, and collections stop the program when used after a reset.
        Arena.DebugChecks = true;
        Arena debug = Arena.Create(256);
        int* stale = debug.AllocArray<int>(4);
        unsafe { stale[0] = 12345; }
        // docs:end
        Console.WriteLine();
        Console.Write("DebugChecks: pointer is live before Reset: ");
        Console.Write(debug.IsLive(stale));
        Console.WriteLine();
        // SAFETY: stale is read below on purpose. Its memory still belongs to the
        // arena's block, so the read is harmless; it shows what a bug would see.
        unsafe { debug.Reset(); }
        uint seen;
        unsafe { seen = (uint)stale[0]; }
        Console.WriteLine($"After Reset the stale pointer reads 0x{seen:X}, IsLive: {debug.IsLive(stale)}");
        unsafe { debug.Destroy(); } // SAFETY: stale is not used after this.
        Arena.DebugChecks = false;
        return 0;
    }

    // Correct code must run the same with guard pages on: what a scope frees
    // is taken away, and everything older stays readable. (On a system
    // without page protection GuardPages stays off, and this checks the
    // same things without it.) Guards.cs has the mistakes it catches.
    static void WithGuardPages()
    {
        // docs:guard-pages
        // Debugging aid: arenas created while GuardPages is on give freed
        // memory back to the system for good, so using it stops the program.
        Arena.GuardPages = true;
        Arena guarded = Arena.Create(256, "guarded");
        // docs:end

        // Data from before a scope survives it, at every nesting level, while
        // the arena grows by several blocks inside.
        bool intact = true;
        int* outer = guarded.AllocArray<int>(8);
        unsafe { for (int i = 0; i < 8; i++) outer[i] = 100 + i; }
        for (int round = 0; round < 50; round++)
        {
            Arena.Scope first;
            unsafe { first = guarded.OpenScope(); } // SAFETY: nothing from inside is used after it.
            using (first)
            {
                int* middle = guarded.AllocArray<int>(300);
                unsafe { middle[0] = round; middle[299] = -round; }
                Arena.Scope second;
                unsafe { second = guarded.OpenScope(); } // SAFETY: as above.
                using (second)
                {
                    byte* big = guarded.AllocArray<byte>(40_000);
                    unsafe { big[0] = 1; big[39_999] = 2; }
                }
                unsafe { intact &= middle[0] == round && middle[299] == -round; }
            }
            unsafe { for (int i = 0; i < 8; i++) intact &= outer[i] == 100 + i; }
        }

        // Objects: made and freed a thousand times, a list growing inside.
        long total = 0;
        for (int round = 0; round < 1000; round++)
        {
            Arena.ObjectScope objects;
            unsafe { objects = guarded.OpenObjectScope(); } // SAFETY: only total, a number, leaves.
            using (objects)
            {
                int[] cells = new int[16];
                cells[round % 16] = round;
                int offset = round;
                Func<int, int> shift = x => x + offset;
                string text = Opaque("round ") + round.ToString();
                var list = List<int>.Create(guarded);
                for (int i = 0; i < 40; i++)
                    list.Add(i);
                total += shift(cells[round % 16]) + text.Length + list.Get(39);
            }
        }
        unsafe { for (int i = 0; i < 8; i++) intact &= outer[i] == 100 + i; }

        // Reset, both ways, then use again.
        unsafe { guarded.Reset(); } // SAFETY: outer isn't used again.
        int* again = guarded.AllocArray<int>(2000);
        unsafe { again[1999] = 7; guarded.Reset(keepCapacity: false); } // SAFETY: nor is again.
        again = guarded.AllocArray<int>(10);
        unsafe { again[9] = 9; intact &= again[9] == 9; }

        // A budget still means the memory in use, not the pages given back.
        Arena budget = Arena.Create(4096, "budget", maxCapacity: 64 * 1024);
        bool withinBudget = true;
        for (int round = 0; round < 200; round++)
        {
            Arena.Scope temp;
            unsafe { temp = budget.OpenScope(); } // SAFETY: nothing from inside is used after it.
            using (temp)
                withinBudget &= budget.AllocArray<byte>(20_000) != null;
        }

        // Arena.Scratch, nested.
        int scratchSum = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            Span<int> a = scratch.AllocSpan<int>(100);
            a[99] = 40;
            int inner = Arena.Scratch(static (scoped ScratchScope nested) =>
            {
                Span<int> b = nested.AllocSpan<int>(5000);
                b[4999] = 2;
                return b[4999];
            });
            return a[99] + inner;
        });

        unsafe { guarded.Destroy(); budget.Destroy(); } // SAFETY: nothing of theirs is used again.
        Arena.GuardPages = false;

        Console.WriteLine();
        Console.WriteLine($"With guard pages: older data intact: {intact}, object rounds total {total}, budget kept: {withinBudget}, scratch {scratchSum}");
    }

    static int[] s_kept;

    // What object scopes must allow: keeping an object in a static from an
    // arena that is never freed, and disposing a scope twice.
    static void ObjectScopeEdges()
    {
        Arena.DebugChecks = true;
        Arena forever = Arena.Create(1024, "forever");
        Arena frame = Arena.Create(1024, "frame");
        Arena.DebugChecks = false;

        Arena.ObjectScope first;
        unsafe { first = forever.AllocateObjectsHere(); } // SAFETY: never freed.
        s_kept = new int[4];
        first.Dispose();

        Arena.ObjectScope second;
        unsafe { second = frame.AllocateObjectsHere(); } // SAFETY: freed below.
        first.Dispose(); // again: must leave the newer scope in place
        nuint before = frame.Used;
        Opaque(new int[4]);
        bool stillInFrame = frame.Used > before;
        second.Dispose();
        unsafe { frame.Reset(); } // SAFETY: nothing from frame is kept.

        Console.WriteLine();
        Console.WriteLine($"Static kept from a scope on a lasting arena: {s_kept.Length == 4}, a second Dispose left the newer scope: {stillInFrame}");
        unsafe { frame.Destroy(); } // SAFETY: as above.

        // zerocore's own caches (the default comparers, the encodings) made for
        // the first time inside a scope: they come from static constructors, so
        // they live outside it, and DebugChecks finds no static pointing in.
        Arena.DebugChecks = true;
        Arena checkedFrame = Arena.Create(1024, "checked");
        Arena.DebugChecks = false;
        bool cachesWork;
        Arena.ObjectScope third;
        unsafe { third = checkedFrame.OpenObjectScope(); } // SAFETY: nothing made inside is kept.
        using (third)
        {
            cachesWork = SameByDefault(new Point { X = 1, Y = 2 }, new Point { X = 1, Y = 2 })
                && OrderByDefault("a", "b") < 0
                && System.Text.Encoding.UTF8.GetByteCount("\u00e9") == 2;
        }
        unsafe { checkedFrame.Destroy(); } // SAFETY: as above.
        Console.WriteLine($"Comparers and encodings first made inside a scope: {cachesWork}");
    }

    // Shared generic code, where the compiler can't tell T: it reads Default itself.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool SameByDefault<T>(T a, T b) => System.Collections.Generic.EqualityComparer<T>.Default.Equals(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int OrderByDefault<T>(T a, T b) => System.Collections.Generic.Comparer<T>.Default.Compare(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static object Opaque(object value) => value;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static string Opaque(string value) => value;

    static void InBuffer()
    {
        // docs:from-buffer
        // An arena in memory you provide: here 1 KB of stack. It never calls
        // malloc and never grows; when the buffer is full, allocations fail.
        Span<byte> memory = stackalloc byte[1024];
        Arena local;
        // SAFETY: local isn't used after this method returns.
        unsafe { local = Arena.FromBuffer(memory, "local"); }
        Point* p = local.Alloc<Point>();
        // docs:end
        unsafe { p->X = 3; } // SAFETY: p points at a Point in the buffer.
        Console.WriteLine($"FromBuffer 1024: capacity {local.Capacity}, 2000 bytes {(local.Alloc(2000) != null ? "allocated" : "refused")}");
        local.Dump();
    }

    // Returns its argument, out of the compiler's sight, so that `+` in Main
    // builds a new string at run time instead of joining two literals.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static string Echo(string text) => text;
}
