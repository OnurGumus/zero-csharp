// Example: the mistakes lib/ ends the program for, and `throw`, which is a
// panic in zero mode: one per run.
// Build: ./build.sh Guards        Run: ./run.sh Guards <case>
//
// Zero mode has no exceptions, so a guard can only stop the program: a message
// on stderr and a non-zero exit code. Each case below makes one such mistake
// on purpose. test.sh runs them all and checks the message of each against
// tests/expected/Guards.fail, so a guard that stops working is noticed.
//
// Debug.Assert and Debug.Fail only exist where DEBUG is defined (the SDK
// defines it in Debug builds); build.sh doesn't, so this file does.
#define DEBUG

using System;

namespace GuardsSample;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: Guards <case>, where the case is one of:");
            Console.WriteLine("  restore-order        an older mark restored before a newer one");
            Console.WriteLine("  restore-other-arena  a mark restored in another arena");
            Console.WriteLine("  restore-after-reset  a mark from before a Reset()");
            Console.WriteLine("  list-after-reset     a list used after its arena was reset (DebugChecks)");
            Console.WriteLine("  list-grew-in-scope   an older list that grew inside a scope, used after it (DebugChecks)");
            Console.WriteLine("  list-grew-in-place   an older list that grew in place inside a scope, used after it (DebugChecks)");
            Console.WriteLine("  builder-grew-in-place  the same for a StrBuilder (DebugChecks)");
            Console.WriteLine("  list-index           a list index out of range");
            Console.WriteLine("  out-of-memory        an allocation the system can't satisfy (stopOnOutOfMemory)");
            Console.WriteLine("  arena-limit          an allocation past an arena's maxCapacity (stopOnOutOfMemory)");
            Console.WriteLine("  buffer-full          an allocation that doesn't fit in a FromBuffer arena (stopOnOutOfMemory)");
            Console.WriteLine("  lambda-in-scope      a lambda first run inside AllocateObjectsHere, cached in the arena, then Reset (DebugChecks)");
            Console.WriteLine("  lambda-in-open-scope the same inside OpenObjectScope, which frees it when the block ends (DebugChecks)");
            Console.WriteLine("  scope-not-created    AllocateObjectsHere on an arena that was never created");
            Console.WriteLine("  destroy-in-scope     an arena destroyed inside its own object scope");
            Console.WriteLine("  throw                a throw, inside a try with a matching catch");
            Console.WriteLine("  throw-null           throw null");
            Console.WriteLine("  throw-in-static-constructor  a throw out of a static constructor, inside Catch.Run");
            Console.WriteLine("  throw-in-parallel-for  a throw in a Parallel.For iteration, inside Catch.Run");
            Console.WriteLine("  union-no-case        a switch on a union that holds none of its cases");
            Console.WriteLine("  debug-assert         a Debug.Assert whose condition is false");
            Console.WriteLine("  debug-fail           a Debug.Fail");
            Console.WriteLine("  range-too-long       an array range that ends past the array");
            Console.WriteLine("  bad-base64           Convert.FromBase64String on text that isn't Base64");
            Console.WriteLine("  string-builder-range a StringBuilder.Remove past the end");
            Console.WriteLine("  bad-pattern          Regex.Create with a pattern that isn't valid");
            Console.WriteLine("  scratch-outer-scope  an Arena.Scratch scope allocating inside a nested one");
            Console.WriteLine("  scratch-outer-list   a ScratchList added to inside a nested Arena.Scratch");
            Console.WriteLine("  scratch-outer-text   a ScratchText appended to inside a nested Arena.Scratch");
            Console.WriteLine("  ptr-null             the Value of a null Ptr");
            Console.WriteLine("  ptr-after-reset      a Ptr used after its arena was reset (DebugChecks)");
            Console.WriteLine("  pool-removed         a Pool handle used after its item was removed");
            Console.WriteLine("  pool-moved           a copy of a Pool handle used after Move");
            Console.WriteLine("  scratch-pool-outer   a ScratchPool added to inside a nested Arena.Scratch");
            Console.WriteLine("  pool-other           a Pool handle used with another pool");
            Console.WriteLine("  scratch-dict-outer   a new key in a ScratchDict inside a nested Arena.Scratch");
            Console.WriteLine("  scratch-set-outer    a new item in a ScratchHashSet inside a nested Arena.Scratch");
            Console.WriteLine("  builder-index        a StrBuilder index out of range");
            Console.WriteLine("  builder-replace-empty  a StrBuilder Replace of an empty string");
            Console.WriteLine("  scratch-outer-edit   a ScratchText inserted into inside a nested Arena.Scratch");
            Console.WriteLine("  scratch-limit        an Arena.Scratch callback allocating past Arena.ScratchMaxCapacity");
            Console.WriteLine("  scratch-buffer-full  an Arena.Scratch callback allocating past the end of its buffer");
            Console.WriteLine("  scratch-stack-full   an Arena.ScratchOnStack callback allocating more than it asked for");
            Console.WriteLine("  scratch-stack-size   Arena.ScratchOnStack with a size that isn't positive");
            Console.WriteLine("  native-overflow      NativeMemory.Alloc with a count times size past the address space");
            Console.WriteLine("  native-zeroed-overflow  the same for NativeMemory.AllocZeroed");
            Console.WriteLine("  native-out-of-memory NativeMemory.Alloc of more than the system can give");
            Console.WriteLine("  native-bad-alignment NativeMemory.AlignedAlloc with an alignment that isn't a power of two");
            Console.WriteLine("  native-aligned-overflow  NativeMemory.AlignedAlloc of a size that overflows with the alignment");
            Console.WriteLine("  pages-pointer-after-reset  a pointer used after its arena was reset (GuardPages)");
            Console.WriteLine("  pages-object-after-scope   an object made in an object scope, used after it (GuardPages)");
            Console.WriteLine("  pages-field-after-scope    the same, kept in a field of an older object (GuardPages)");
            Console.WriteLine("  pages-span-after-scope     a span allocated in a scope, read after it (GuardPages)");
            Console.WriteLine("  pages-scratch-pointer      a pointer into Arena.Scratch's memory, used after the callback (GuardPages)");
            Console.WriteLine("  pages-after-destroy        memory used after its arena was destroyed (GuardPages)");
            Console.WriteLine("  pages-arena-after-destroy  a copy of an arena's handle used after Destroy (GuardPages)");
            return 0;
        }

        switch (args[0])
        {
            case "restore-order": RestoreOrder(); break;
            case "restore-other-arena": RestoreOtherArena(); break;
            case "restore-after-reset": RestoreAfterReset(); break;
            case "list-after-reset": ListAfterReset(); break;
            case "list-grew-in-scope": ListGrewInScope(); break;
            case "list-grew-in-place": ListGrewInPlace(); break;
            case "builder-grew-in-place": BuilderGrewInPlace(); break;
            case "list-index": ListIndex(); break;
            case "out-of-memory": OutOfMemory(); break;
            case "arena-limit": ArenaLimit(); break;
            case "buffer-full": BufferFull(); break;
            case "lambda-in-scope": LambdaInScope(); break;
            case "lambda-in-open-scope": LambdaInOpenScope(); break;
            case "scope-not-created": ScopeNotCreated(); break;
            case "destroy-in-scope": DestroyInScope(); break;
            case "throw": Throw(); break;
            case "throw-null": ThrowNull(); break;
            case "throw-in-static-constructor": ThrowInStaticConstructor(); break;
            case "throw-in-parallel-for": ThrowInParallelFor(); break;
            case "union-no-case": UnionNoCase(); break;
            case "debug-assert": DebugAssert(Opaque(0)); break;
            case "debug-fail": DebugFail(); break;
            case "range-too-long": RangeTooLong(Opaque(10)); break;
            case "bad-base64": System.Convert.FromBase64String("not base64!"); break;
            case "string-builder-range": new System.Text.StringBuilder("abc").Remove(2, 2); break;
            case "bad-pattern": Regex.Create(Arena.Create(4096), "(abc"); break;
            case "scratch-outer-scope": ScratchOuterScope(); break;
            case "scratch-outer-list": ScratchOuterList(); break;
            case "scratch-outer-text": ScratchOuterText(); break;
            case "ptr-null": PtrNull(); break;
            case "ptr-after-reset": PtrAfterReset(); break;
            case "custom-format": Console.WriteLine(Opaque(1.5).ToString("0.00")); break;
            case "bad-format-item": Console.WriteLine(string.Format("{0:F2", Opaque(1.5))); break;
            case "pool-removed": PoolRemoved(); break;
            case "pool-moved": PoolMoved(); break;
            case "scratch-pool-outer": ScratchPoolOuter(); break;
            case "pool-other": PoolOther(); break;
            case "scratch-dict-outer": ScratchDictOuter(); break;
            case "scratch-set-outer": ScratchSetOuter(); break;
            case "builder-index": BuilderIndex(); break;
            case "builder-replace-empty": BuilderReplaceEmpty(); break;
            case "scratch-outer-edit": ScratchOuterEdit(); break;
            case "scratch-limit": ScratchLimit(); break;
            case "scratch-buffer-full": ScratchBufferFull(); break;
            case "scratch-stack-full": ScratchStackFull(); break;
            case "scratch-stack-size": ScratchStackSize(Opaque(0) - 1); break;
            case "native-overflow": NativeOverflow(); break;
            case "native-zeroed-overflow": NativeZeroedOverflow(); break;
            case "native-out-of-memory": NativeOutOfMemory(); break;
            case "native-bad-alignment": NativeBadAlignment(); break;
            case "native-aligned-overflow": NativeAlignedOverflow(); break;
            case "pages-pointer-after-reset": PagesPointerAfterReset(); break;
            case "pages-object-after-scope": PagesObjectAfterScope(); break;
            case "pages-field-after-scope": PagesFieldAfterScope(); break;
            case "pages-span-after-scope": PagesSpanAfterScope(); break;
            case "pages-scratch-pointer": PagesScratchPointer(); break;
            case "pages-after-destroy": PagesAfterDestroy(); break;
            case "pages-arena-after-destroy": PagesArenaAfterDestroy(); break;
            default:
                Console.WriteLine("Unknown case");
                return 0;
        }

        // Every case above should have ended the program.
        Console.WriteLine("NOT STOPPED");
        return 0;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static double Opaque(double value) => value;

    // The unsafe blocks below break their promises on purpose: that is the test.

    static void RestoreOrder()
    {
        // docs:restore-order
        Arena arena = Arena.Create(256, "order");
        Arena.Mark outer = arena.Save();
        arena.AllocArray<byte>(64);
        Arena.Mark inner = arena.Save();
        arena.AllocArray<byte>(64);
        unsafe
        {
            arena.Restore(outer); // also ends `inner`
            arena.Restore(inner);
        }
        // docs:end
    }

    static void RestoreOtherArena()
    {
        Arena first = Arena.Create(256, "first");
        Arena second = Arena.Create(256, "second");
        Arena.Mark mark = first.Save();
        unsafe { second.Restore(mark); }
    }

    static void RestoreAfterReset()
    {
        Arena arena = Arena.Create(256, "reset");
        Arena.Mark mark = arena.Save();
        unsafe
        {
            arena.Reset();
            arena.Restore(mark);
        }
    }

    static void ListAfterReset()
    {
        // docs:list-after-reset
        Arena.DebugChecks = true;
        Arena arena = Arena.Create(256);
        List<int> list = List<int>.Create(arena);
        list.Add(1);
        unsafe { arena.Reset(); }
        list.Add(2);
        // docs:end
    }

    static void ListGrewInScope()
    {
        Arena.DebugChecks = true;
        Arena arena = Arena.Create(256, "lists");
        List<int> list = List<int>.Create(arena, 4);

        Arena.Scope temp;
        unsafe { temp = arena.OpenScope(); }
        using (temp)
        {
            arena.AllocArray<byte>(8); // the list is no longer last, so it can't grow in place
            for (int i = 0; i < 10; i++)
                list.Add(i);           // its new buffer is allocated inside the scope
        }
        list.Get(0);
    }

    static void ListGrewInPlace()
    {
        // docs:grew-in-place
        Arena.DebugChecks = true;
        Arena arena = Arena.Create(256, "in place");
        List<int> list = List<int>.Create(arena, 4);

        Arena.Scope temp;
        unsafe { temp = arena.OpenScope(); }
        using (temp)
        {
            // The list is still the arena's newest allocation, so its buffer
            // is made longer where it is: it starts before the scope's mark
            // and now ends after it.
            for (int i = 0; i < 10; i++)
                list.Add(i);
        }
        list.Get(0);
        // docs:end
    }

    static void BuilderGrewInPlace()
    {
        Arena.DebugChecks = true;
        Arena arena = Arena.Create(256, "builder");
        StrBuilder text = StrBuilder.Create(arena, 4);

        Arena.Scope temp;
        unsafe { temp = arena.OpenScope(); }
        using (temp)
        {
            text.Append("longer than four characters");
        }
        text.Append("x");
    }

    static void ListIndex()
    {
        Arena arena = Arena.Create(256);
        List<int> list = List<int>.Create(arena);
        list.Add(1);
        list.Get(1);
    }

    static void OutOfMemory()
    {
        // docs:out-of-memory
        Arena arena = Arena.Create(256, "small", stopOnOutOfMemory: true);
        // Half the address space: no system has that much to give.
        arena.Alloc(~(nuint)0 / 2);
        // docs:end
    }

    static void ArenaLimit()
    {
        // docs:arena-limit
        Arena arena = Arena.Create(256, "budget", stopOnOutOfMemory: true,
            maxCapacity: 1024);
        arena.Alloc(600);  // fits
        arena.Alloc(600);  // 1200 bytes in all: past the limit
        // docs:end
    }

    static void BufferFull()
    {
        Span<byte> memory = stackalloc byte[512];
        Arena arena;
        // SAFETY: the arena isn't used after this method returns.
        unsafe { arena = Arena.FromBuffer(memory, "buffer", stopOnOutOfMemory: true); }
        arena.Alloc(1000);
    }

    static void LambdaInScope()
    {
        Arena.DebugChecks = true;
        Arena frame = Arena.Create(1024, "frame");
        Arena.ObjectScope objects;
        unsafe { objects = frame.AllocateObjectsHere(); }
        using (objects)
        {
            // The first run of a lambda that captures nothing creates it and
            // caches it in a static field: here, inside the arena.
            System.Func<int, int> twice = static x => x * 2;
            twice(21);
        }
        // The check runs here, before the lambda's memory is freed.
        unsafe { frame.Reset(); }
    }

    static void LambdaInOpenScope()
    {
        Arena.DebugChecks = true;
        Arena frame = Arena.Create(1024, "frame");
        Arena.ObjectScope objects;
        unsafe { objects = frame.OpenObjectScope(); }
        using (objects)
        {
            System.Func<int, int> thrice = static x => x * 3;
            thrice(7);
        }   // Restore: the cached lambda is about to be freed
    }

    static void ScopeNotCreated()
    {
        Arena never = default;
        Arena.ObjectScope objects;
        unsafe { objects = never.AllocateObjectsHere(); }
        using (objects)
            Opaque(new int[4].Length);
    }

    static void DestroyInScope()
    {
        Arena frame = Arena.Create(1024, "frame");
        Arena.ObjectScope objects;
        unsafe { objects = frame.AllocateObjectsHere(); }
        using (objects)
        {
            unsafe { frame.Destroy(); }
            Opaque(new int[4].Length);
        }
    }

    static void Throw()
    {
        // docs:throw-panic
        try
        {
            throw new System.InvalidOperationException("the queue is empty");
        }
        catch (System.InvalidOperationException)
        {
            // Never runs: the throw has already ended the program.
            Console.WriteLine("caught");
        }
        // docs:end
    }

    static void ThrowNull()
    {
        throw null;
    }

    // A type whose static constructor throws.
    static class Settings
    {
        public static readonly int Port = Load();

        static int Load() => throw new System.InvalidOperationException("the settings are missing");
    }

    // Catch.Run gets a throw back, but not one out of a static constructor:
    // jumping out of it would leave the type half made for good.
    static void ThrowInStaticConstructor()
    {
        Exception error = Catch.Run(static () => Console.WriteLine(Settings.Port));
        Console.WriteLine(error == null ? "returned" : "caught");
    }

    // Nor one out of a Parallel.For iteration: the loop's other threads
    // would go on working for a loop that is gone. (One iteration, so that
    // it's the calling thread's, the one with the Catch.Run.)
    static void ThrowInParallelFor()
    {
        Exception error = Catch.Run(static () => Parallel.For(0, 1, 0,
            static (_, i) => throw new System.InvalidOperationException("thrown in a Parallel.For")));
        Console.WriteLine(error == null ? "returned" : "caught");
    }

    static int UnionNoCase()
    {
        // docs:union-no-case
        Choice choice = default;  // holds neither a Left nor a Right
        return choice switch
        {
            Left l => l.Value,
            Right r => r.Value,
        };
        // docs:end
    }

    // docs:debug-assert
    static int DebugAssert(int count)
    {
        System.Diagnostics.Debug.Assert(count > 0, "the queue is empty");
        return count - 1;
    }
    // docs:end

    static void DebugFail()
    {
        System.Diagnostics.Debug.Fail("unreachable state");
    }

    static int RangeTooLong(int end)
    {
        int[] numbers = { 1, 2, 3 };
        return numbers[1..end].Length;
    }

    // The compiler accepts these, as the state passed by ref may hold the outer
    // scope, list or text; what they allocate would be freed with the inner
    // call's memory, so it stops the program instead.
    static void ScratchOuterScope()
    {
        Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new OuterScratch { Scope = scratch };
            Arena.Scratch(ref outer, static (scoped ScratchScope inner, scoped ref OuterScratch state) =>
            {
                state.Scope.AllocSpan<int>(4);
            });
        });
    }

    static void ScratchOuterList()
    {
        Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new OuterScratch { Items = scratch.List<int>() };
            Arena.Scratch(ref outer, static (scoped ScratchScope inner, scoped ref OuterScratch state) =>
            {
                state.Items.Add(1);
            });
        });
    }

    static void ScratchOuterText()
    {
        Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new OuterScratch { Text = scratch.Text() };
            Arena.Scratch(ref outer, static (scoped ScratchScope inner, scoped ref OuterScratch state) =>
            {
                state.Text.Append($"{42}");
            });
        });
    }

    static void BuilderIndex()
    {
        StrBuilder text = StrBuilder.Create(Arena.Create(256));
        text.Append("abc");
        text.Insert(4, "d");
    }

    static void BuilderReplaceEmpty()
    {
        StrBuilder text = StrBuilder.Create(Arena.Create(256));
        text.Append("abc");
        text.Replace("", "x");
    }

    static void ScratchOuterEdit()
    {
        Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new OuterScratch { Text = scratch.Text() };
            outer.Text.Append("bc");
            Arena.Scratch(ref outer, static (scoped ScratchScope inner, scoped ref OuterScratch state) =>
            {
                state.Text.Insert(0, 'a');
            });
        });
    }

    static void ScratchLimit()
    {
        // docs:scratch-limit
        // First thing in Main, before anything calls Arena.Scratch:
        Arena.ScratchSize = 1024;         // a scratch arena's first block,
        Arena.ScratchMaxCapacity = 1024;  // and the most it may grow to

        Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            scratch.AllocSpan<byte>(600);  // fits
            scratch.AllocSpan<byte>(600);  // past the limit
        });
        // docs:end
    }

    static void ScratchBufferFull()
    {
        Span<byte> memory = stackalloc byte[512];
        // SAFETY: nothing else uses memory until the call returns.
        unsafe
        {
            Arena.Scratch(memory, static (scoped ScratchScope scratch) =>
            {
                scratch.AllocSpan<byte>(1000);
            });
        }
    }

    static void ScratchStackFull()
    {
        Arena.ScratchOnStack(512, static (scoped ScratchScope scratch) =>
        {
            scratch.AllocSpan<byte>(1000);
        });
    }

    static void ScratchStackSize(int bytes)
    {
        Arena.ScratchOnStack(bytes, static (scoped ScratchScope scratch) =>
        {
            scratch.AllocSpan<byte>(10);
        });
    }

    static void PtrNull()
    {
        Ptr<int> nothing = default;
        nothing.Value = 1;
    }

    static void PtrAfterReset()
    {
        Arena.DebugChecks = true;
        Arena arena = Arena.Create(256, "links");
        Ptr<int> item = arena.New(7);
        unsafe { arena.Reset(); } // SAFETY: none, on purpose: item is used below.
        item.Value = 8;
    }

    static void PoolRemoved()
    {
        var pool = Pool<int>.Create(Arena.Create(4096));
        Handle<int> item = pool.Add(1);
        pool.Remove(item);
        pool.Add(2); // the same slot, a new generation
        pool[item] = 3;
    }

    static void PoolMoved()
    {
        var pool = Pool<int>.Create(Arena.Create(4096));
        Handle<int> item = pool.Add(1);
        Handle<int> copy = item;
        pool.Move(ref item);
        pool[copy] = 2;
    }

    static void PoolOther()
    {
        Arena arena = Arena.Create(4096);
        var first = Pool<int>.Create(arena);
        var second = Pool<int>.Create(arena);
        second.Add(0);
        Handle<int> item = first.Add(1); // slot 0, generation 1, as second's item
        second[item] = 2;
    }

    static void ScratchDictOuter()
    {
        Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new OuterScratch { Dict = scratch.Dict<int, int>() };
            outer.Dict.Set(1, 1);
            Arena.Scratch(ref outer, static (scoped ScratchScope inner, scoped ref OuterScratch state) =>
            {
                state.Dict.Set(1, 2); // allowed: the key is there
                state.Dict.Set(2, 2); // a new key: stops
            });
        });
    }

    // Where .NET throws OutOfMemoryException, zero mode stops the program.
    static void NativeOverflow()
    {
        unsafe { System.Runtime.InteropServices.NativeMemory.Alloc(~(nuint)0 / 2, 4); }
    }

    static void NativeZeroedOverflow()
    {
        unsafe { System.Runtime.InteropServices.NativeMemory.AllocZeroed(3, ~(nuint)0 / 2); }
    }

    static void NativeOutOfMemory()
    {
        // More than half the address space: no system has that much to give.
        unsafe { System.Runtime.InteropServices.NativeMemory.Alloc((nuint)nint.MaxValue + 1); }
    }

    // .NET throws ArgumentException.
    static void NativeBadAlignment()
    {
        unsafe { System.Runtime.InteropServices.NativeMemory.AlignedAlloc(16, 24); }
    }

    static void NativeAlignedOverflow()
    {
        unsafe { System.Runtime.InteropServices.NativeMemory.AlignedAlloc(~(nuint)0 - 8, 64); }
    }

    // The GuardPages cases use freed memory in ways nothing else notices:
    // without guard pages each of them reads its old data and carries on.

    static void PagesPointerAfterReset()
    {
        // docs:pages-pointer-after-reset
        Arena.GuardPages = true;
        Arena frame = Arena.Create(4096, "frame");
        unsafe
        {
            int* count = frame.Alloc<int>();
            *count = 7;
            frame.Reset();
            Console.WriteLine(*count);   // stops here
        }
        // docs:end
    }

    sealed class Player
    {
        public string Name;
        public int Score;
    }

    static void PagesObjectAfterScope()
    {
        // docs:pages-object-after-scope
        Arena.GuardPages = true;
        Arena frame = Arena.Create(4096, "frame");
        Player best;
        Arena.ObjectScope objects;
        unsafe { objects = frame.OpenObjectScope(); }
        using (objects)
        {
            best = new Player { Name = "Ada", Score = Opaque(90) };
        }   // best's memory is freed here
        Console.WriteLine(ScoreOf(best));   // stops here
        // docs:end
    }

    // A call the compiler can't see through: reading best.Score in place, it
    // may still have the 90 at hand and never look at the freed memory.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int ScoreOf(Player player) => player.Score;

    sealed class Team
    {
        public Player Captain;
    }

    static void PagesFieldAfterScope()
    {
        Arena.GuardPages = true;
        Arena frame = Arena.Create(4096, "frame");
        var team = new Team();
        Arena.ObjectScope objects;
        unsafe { objects = frame.OpenObjectScope(); }
        using (objects)
        {
            team.Captain = new Player { Name = "Ada", Score = Opaque(90) };
        }
        Console.WriteLine(ScoreOf(team.Captain));
    }

    static void PagesSpanAfterScope()
    {
        Arena.GuardPages = true;
        Arena arena = Arena.Create(4096, "spans");
        Span<int> numbers;
        Arena.Scope temp;
        unsafe
        {
            temp = arena.OpenScope();
            using (temp)
            {
                numbers = arena.AllocSpan<int>(8);
                numbers[3] = Opaque(5);
            }
        }
        Console.WriteLine(numbers[3]);
    }

    static void PagesScratchPointer()
    {
        Arena.GuardPages = true;
        nint kept = 0;
        Arena.Scratch(ref kept, static (scoped ScratchScope scratch, scoped ref nint state) =>
        {
            Span<int> numbers = scratch.AllocSpan<int>(8);
            numbers[0] = Opaque(5);
            unsafe
            {
                fixed (int* first = numbers)
                    state = (nint)first;
            }
        });
        unsafe { Console.WriteLine(*(int*)kept); }
    }

    static void PagesAfterDestroy()
    {
        Arena.GuardPages = true;
        Arena arena = Arena.Create(4096, "short-lived");
        unsafe
        {
            int* count = arena.Alloc<int>();
            *count = 7;
            arena.Destroy();
            Console.WriteLine(*count);
        }
    }

    static void PagesArenaAfterDestroy()
    {
        Arena.GuardPages = true;
        Arena arena = Arena.Create(4096, "short-lived");
        Arena copy = arena;
        unsafe { arena.Destroy(); }
        Console.WriteLine((long)copy.Used);
    }

    static void ScratchSetOuter()
    {
        Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new OuterScratch { Set = scratch.HashSet<int>() };
            outer.Set.Add(1);
            Arena.Scratch(ref outer, static (scoped ScratchScope inner, scoped ref OuterScratch state) =>
            {
                state.Set.Add(1); // allowed: already there
                state.Set.Add(2); // a new item: stops
            });
        });
    }

    static void ScratchPoolOuter()
    {
        Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            var outer = new OuterScratch { Pool = scratch.Pool<int>() };
            Arena.Scratch(ref outer, static (scoped ScratchScope inner, scoped ref OuterScratch state) =>
            {
                state.Pool.Add(1);
            });
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int Opaque(int value) => value;
}

public ref struct OuterScratch
{
    public ScratchScope Scope;
    public ScratchList<int> Items;
    public ScratchText Text;
    public ScratchPool<int> Pool;
    public ScratchDict<int, int> Dict;
    public ScratchHashSet<int> Set;
}

public struct Left { public int Value; }
public struct Right { public int Value; }
public union Choice(Left, Right);
