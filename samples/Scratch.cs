// Example: Arena.Scratch (lib/Scratch.cs), temporary memory whose lifetime the
// compiler checks. Exit code = number of failed checks.
// Build: ./build.sh Scratch
//
// Everything a ScratchScope gives out is freed when the callback returns, and
// the compiler refuses any way to keep it longer: tests/must-not-compile has
// one program per way, which test.sh checks the compiler rejects. The one way
// it can't see, an outer scope or list allocating inside a nested call, stops
// the program (the scratch-outer-* cases in Guards). So unlike
// Arena.OpenScope, nothing here is unsafe, except a call in a buffer of
// yours: nothing else may use the buffer while the call runs.

using System;
using System.Runtime.CompilerServices;

namespace ScratchSample;

// docs:state
// What Arena.Scratch passes to its callback by ref: the data going in.
public ref struct Job
{
    public ReadOnlySpan<int> Input;
}
// docs:end

public struct Growing
{
    public List<int> Items;
}

// docs:own-collection
// A collection of your own for scratch memory: a ref struct built on a
// ScratchList, so the compiler keeps it inside the callback too, and the
// list's own checks cover it in nested calls.
public ref struct ScratchStack<T> where T : unmanaged
{
    ScratchList<T> _items;

    public ScratchStack(ScratchScope scratch) => _items = scratch.List<T>();

    public int Count => _items.Count;

    public void Push(T item) => _items.Add(item);

    public T Pop()
    {
        int last = _items.Count - 1;
        T top = _items[last]; // an empty stack stops the program here
        _items.RemoveAt(last);
        return top;
    }
}
// docs:end

// docs:own-struct
// Only unmanaged fields, so the scope can allocate one.
public struct Particle
{
    public float X;
    public float Y;
    public float Speed;
}
// docs:end

// A scratch list passed to a nested Arena.Scratch.
public ref struct Outer
{
    public ScratchList<int> Items;
    public ScratchDict<int, int> Counts;
    public ScratchText Text;
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
            Console.WriteLine($"  FAIL: {name}");
        }
    }

    // Where a span's memory is, as a number: enough to compare two of them.
    static nint Address(Span<int> span)
    {
        unsafe { return (nint)Unsafe.AsPointer(ref span[0]); } // SAFETY: the address is only compared.
    }

    static nint Address(Span<byte> span)
    {
        unsafe { return (nint)Unsafe.AsPointer(ref span[0]); } // SAFETY: the address is only compared.
    }

    // Whether a span of the scope, and one of a Scratch call nested in it,
    // both lie in [range.Start, range.End).
    static bool BothInBuffer(scoped ScratchScope scratch, (nint Start, nint End) range)
    {
        nint mine = Address(scratch.AllocSpan<int>(16));
        nint nested = Arena.Scratch(static (scoped ScratchScope inner) => Address(inner.AllocSpan<int>(16)));
        return mine >= range.Start && mine < range.End && nested >= range.Start && nested < range.End;
    }

    // docs:spawn
    // Scratch memory isn't on the stack: a helper can allocate it and hand it
    // back, and it lives until the callback that called the helper returns.
    static Span<Particle> Spawn(ScratchScope scratch, int count)
        => scratch.AllocSpan<Particle>(count);
    // docs:end

    static int ScratchLength()
        => Arena.Scratch(static (scoped ScratchScope scratch) => scratch.AllocSpan<int>(10).Length);

    public static int Main()
    {
        // docs:scratch
        int[] scores = { 5, 3, 8, 1 };
        var job = new Job { Input = scores };
        int median = Arena.Scratch(ref job,
            static (scoped ScratchScope scratch, scoped ref Job state) =>
            {
                // A copy to sort, so the caller's array keeps its order.
                Span<int> copy = scratch.AllocSpan<int>(state.Input.Length);
                state.Input.CopyTo(copy);
                copy.Sort();
                return copy[copy.Length / 2];
            });
        // docs:end
        Check("a sorted scratch copy", median == 5 && scores[0] == 5);
        int written = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            ref int value = ref scratch.Alloc<int>();
            bool zeroed = value == 0;
            value = 42;
            return zeroed ? value : -1;
        });
        Check("Alloc gives a zeroed T to write through", written == 42);
        // docs:input
        // An input passed by value: the callback gets a copy of the span,
        // which points at the same items, so it can fill them.
        Span<int> squares = stackalloc int[4];
        Arena.Scratch(squares,
            static (scoped ScratchScope scratch, Span<int> output) =>
            {
                ScratchList<int> work = scratch.List<int>();
                for (int i = 1; i <= output.Length; i++)
                    work.Add(i * i);
                work.AsSpan().CopyTo(output);
            });
        // docs:end
        Check("a span passed by value is filled", squares[0] == 1 && squares[3] == 16);
        Section("using it");

        nint firstAddress = Arena.Scratch(static (scoped ScratchScope scratch) => Address(scratch.AllocSpan<int>(64)));
        nint secondAddress = Arena.Scratch(static (scoped ScratchScope scratch) => Address(scratch.AllocSpan<int>(64)));
        Check("the memory is reused", firstAddress == secondAddress);
        int outer = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            Span<int> mine = scratch.AllocSpan<int>(4);
            mine[0] = 7;
            Arena.Scratch(static (scoped ScratchScope inner) => { inner.AllocSpan<int>(1000).Fill(9); });
            return mine[0];
        });
        Check("calls nest", outer == 7);
        // The lambda's delegate is made on its first call and kept; after
        // that, a call allocates nothing.
        int sum = ScratchLength();
        long before = GC.GetTotalAllocatedBytes();
        for (int i = 0; i < 100; i++)
            sum += ScratchLength();
        Check("nothing allocated", sum == 1010 && GC.GetTotalAllocatedBytes() == before);
        Section("its memory");

        // docs:growing
        // A list of yours that grows during the callback grows in its own
        // arena, so the scratch memory going away doesn't touch it.
        Arena arena = Arena.Create(4096);
        Arena.DebugChecks = true;
        var growing = new Growing { Items = List<int>.Create(arena, 4) };
        Arena.Scratch(ref growing,
            static (scoped ScratchScope scratch, scoped ref Growing state) =>
            {
                Span<int> temp = scratch.AllocSpan<int>(100);
                for (int i = 0; i < temp.Length; i++)
                    state.Items.Add(i);
            });
        Check("your list is intact",
            growing.Items.Count == 100 && growing.Items.Get(99) == 99);
        // docs:end
        Arena.DebugChecks = false;
        Section("your arenas");

        // docs:buffer
        // Memory of yours instead of the thread's scratch arena, for one
        // call and the Scratch calls nested in it: no malloc, and a fixed
        // size (needing more than the buffer holds stops the program).
        Span<byte> memory = stackalloc byte[2048];
        int total;
        // SAFETY: nothing else uses memory until the call returns.
        unsafe
        {
            total = Arena.Scratch(memory,
                static (scoped ScratchScope scratch) =>
                {
                    Span<int> numbers = scratch.AllocSpan<int>(100);
                    for (int i = 0; i < numbers.Length; i++)
                        numbers[i] = i;
                    int sum = 0;
                    foreach (int n in numbers)
                        sum += n;
                    return sum;
                });
        }
        // docs:end
        Check("a buffer of yours", total == 4950);
        nint start = Address(memory);
        bool inBuffer;
        // SAFETY: nothing else uses memory until the call returns.
        unsafe { inBuffer = Arena.Scratch(memory, (start, start + memory.Length), BothInBuffer); }
        Check("its memory, nested calls included", inBuffer);
        nint after = Arena.Scratch(static (scoped ScratchScope scratch) => Address(scratch.AllocSpan<int>(16)));
        Check("the thread's own afterwards", after < start || after >= start + memory.Length);
        // A scope from outside the call allocates in its own arena, which
        // the call doesn't free: allowed, unlike in a nested ordinary call.
        int outerAdded = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new Outer { Items = scratch.List<int>() };
            Span<byte> mine = stackalloc byte[1024];
            // SAFETY: nothing else uses mine until the call returns.
            unsafe
            {
                Arena.Scratch(mine, ref outer, static (scoped ScratchScope inner, scoped ref Outer state) =>
                {
                    for (int i = 0; i < 100; i++)
                        state.Items.Add(i);
                });
            }
            return outer.Items.Count + outer.Items[99];
        });
        Check("an outer list grows inside it", outerAdded == 199);
        // docs:on-stack
        // The same in memory on ScratchOnStack's own stack frame: safe,
        // since nothing but the scope sees it.
        int sumOfSquares = Arena.ScratchOnStack(1024,
            static (scoped ScratchScope scratch) =>
            {
                ScratchList<int> items = scratch.List<int>(8);
                for (int i = 1; i <= 10; i++)
                    items.Add(i * i);
                int sum = 0;
                foreach (int item in items)
                    sum += item;
                return sum;
            });
        // docs:end
        Check("on the stack", sumOfSquares == 385);
        int nestedOnStack = Arena.ScratchOnStack(1024, static (scoped ScratchScope scratch) =>
        {
            Span<int> mine = scratch.AllocSpan<int>(4);
            mine[0] = 7;
            int inner = Arena.Scratch(static (scoped ScratchScope s) => s.AllocSpan<int>(32).Length);
            return mine[0] + inner;
        });
        Check("a call nested on the stack", nestedOnStack == 39);
        int loops = 0;
        for (int i = 0; i < 10_000; i++)
            loops += Arena.ScratchOnStack(4096, static (scoped ScratchScope scratch) => scratch.AllocSpan<byte>(100).Length);
        Check("its stack is freed each call", loops == 1_000_000);
        Section("a buffer of yours");

        // docs:lists
        // Lists and text from the scope grow as needed and go away with the
        // rest of the scratch memory. The string to keep is copied out into
        // an arena of yours.
        Arena keep = Arena.Create(4096);
        string report = Arena.Scratch(ref keep,
            static (scoped ScratchScope scratch, scoped ref Arena target) =>
            {
                ScratchList<int> evens = scratch.List<int>(4);
                for (int i = 18; i >= 0; i -= 2)
                    evens.Add(i);
                evens.AsSpan().Sort();

                ScratchText text = scratch.Text();
                text.Append($"{evens.Count} evens:");
                foreach (int even in evens)
                    text.Append($" {even}");
                return text.ToArenaString(target);
            });
        // docs:end
        Check("a list and text", report == "10 evens: 0 2 4 6 8 10 12 14 16 18");
        int edited = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            ScratchList<int> items = scratch.List<int>();
            items.AddRange(stackalloc int[] { 1, 2, 3, 4 });
            items[0] = 10;
            items[1]++;
            items.RemoveAt(2);
            return items.Count * 100 + items[0] + items[1] + items[2];
        });
        Check("indexing, AddRange, RemoveAt", edited == 300 + 10 + 3 + 4);
        nint firstList = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            ScratchList<int> items = scratch.List<int>();
            for (int i = 0; i < 1000; i++)
                items.Add(i);
            return Address(items.AsSpan());
        });
        nint secondList = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            ScratchList<int> items = scratch.List<int>();
            for (int i = 0; i < 1000; i++)
                items.Add(i);
            return Address(items.AsSpan());
        });
        Check("their memory is reused", firstList == secondList);
        // An outer list can be read and changed in a nested call, though not
        // added to there.
        int nested = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new Outer { Items = scratch.List<int>() };
            outer.Items.Add(5);
            Arena.Scratch(ref outer, static (scoped ScratchScope inner, scoped ref Outer state) =>
            {
                ScratchList<int> mine = inner.List<int>();
                mine.Add(state.Items[0]);
                state.Items[0] = mine[0] * 2;
            });
            outer.Items.Add(1);
            return outer.Items[0] + outer.Items[1];
        });
        Check("an outer list in a nested call", nested == 11);
        bool textEdited = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            ScratchText text = scratch.Text();
            text.Append("2026-11-25");
            text.Replace("-", " / ");
            text.Insert(0, "on ");
            text.Remove(0, 3);
            text[0] = '3';
            return text.AsSpan().SequenceEqual("3026 / 11 / 25");
        });
        Check("text: Replace, Insert, Remove, indexer", textEdited);
        // Remove and a char Replace never need more memory, so an outer text
        // takes them in a nested call; Insert and Replace with text don't.
        bool outerText = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new Outer { Text = scratch.Text() };
            outer.Text.Append("a.b.c!");
            Arena.Scratch(ref outer, static (scoped ScratchScope inner, scoped ref Outer state) =>
            {
                state.Text.Remove(5, 1);
                state.Text.Replace('.', '/');
            });
            return outer.Text.AsSpan().SequenceEqual("a/b/c");
        });
        Check("an outer text in a nested call", outerText);
        Section("lists and text");

        // docs:own-types
        int popped = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            // An unmanaged struct comes from the scope, used through a ref.
            ref Particle particle = ref scratch.Alloc<Particle>();
            particle.Speed = 2;

            // A ref struct of scratch pieces is an ordinary local.
            var stack = new ScratchStack<int>(scratch);
            for (int i = 1; i <= 3; i++)
                stack.Push(i * (int)particle.Speed);
            return stack.Pop() * 10 + stack.Count;
        });
        // docs:end
        Check("a struct and a stack of your own", popped == 62);
        int moving = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            // 120 KB: far past the scratch arena's first 16 KB block, and
            // still well inside a Raspberry Pi Pico 2's 520 KB of RAM.
            Span<Particle> swarm = Spawn(scratch, 10_000);
            swarm[^1].Speed = 3;
            return swarm.Length + (int)swarm[^1].Speed;
        });
        Check("a helper's scratch memory", moving == 10_003);
        Section("types of your own");

        // docs:dict
        // Count each roll and keep only the counts: the dictionary and the set
        // go with the scratch memory, the copy stays in an arena of yours.
        int[] rolls = { 3, 6, 3, 1, 6, 3 };
        var dice = new Job { Input = rolls };
        Arena results = Arena.Create(4096);
        int distinct = Arena.Scratch(ref dice,
            static (scoped ScratchScope scratch, scoped ref Job state) =>
            {
                ScratchDict<int, int> counts = scratch.Dict<int, int>();
                ScratchHashSet<int> seen = scratch.HashSet<int>();
                foreach (int roll in state.Input)
                {
                    counts.TryGet(roll, out int count);
                    counts.Set(roll, count + 1);
                    seen.Add(roll);
                }
                return seen.Count;
            });
        // docs:end
        Check("counted", distinct == 3);
        Dict<int, int> kept = Arena.Scratch(ref results,
            static (scoped ScratchScope scratch, scoped ref Arena target) =>
            {
                ScratchDict<int, int> squares = scratch.Dict<int, int>(4);
                for (int i = 0; i < 100; i++)
                    squares.Set(i, i * i);
                squares.Remove(50);
                return squares.Clone(target);
            });
        Check("Clone keeps a copy", kept.Count == 99 && kept.TryGet(9, out int nine) && nine == 81 && !kept.ContainsKey(50));
        int changed = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new Outer { Counts = scratch.Dict<int, int>() };
            outer.Counts.Set(1, 10);
            // In a nested call, a key that is there can change; a new one
            // can't be added (that stops the program).
            Arena.Scratch(ref outer, static (scoped ScratchScope inner, scoped ref Outer state) =>
            {
                state.Counts.Set(1, 11);
            });
            outer.Counts.TryGet(1, out int value);
            return value;
        });
        Check("an outer key changed in a nested call", changed == 11);
        bool setWorks = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            ScratchHashSet<long> set = scratch.HashSet<long>(4);
            for (long i = 0; i < 50; i++)
                set.Add(i % 20);
            bool removed = set.Remove(7) && !set.Remove(7);
            long sum = 0;
            foreach (long item in set)
                sum += item;
            return removed && set.Count == 19 && !set.Contains(7) && sum == 190 - 7;
        });
        Check("a set", setWorks);
        Section("dictionaries and sets");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine($"{s_failures} checks FAILED.");
        return s_failures;
    }
}
