---
title: Memory without a GC
index: 2
---

# Memory without a GC

In zero mode, `new` calls `calloc` and nothing ever frees the result. There is no garbage collector to
notice that an object is no longer used. That is fine for objects that live as long as the program,
and it is a leak for everything else. This page explains how programs here handle the "everything
else": arenas, and the rules that keep them safe.

## Which to use

There are three ways to give memory a lifetime, from the one the compiler checks to the one that
trusts you. Use the safest one that does the job, for each piece of code rather than the whole
program: one program often uses all three.

| | Use it for | Who makes sure nothing is used after it's freed |
|---|---|---|
| Structs and `Arena.Scratch` | Temporary work inside one call: buffers, intermediate lists, building a string | The compiler, plus a check when the program runs for one nesting case |
| An arena of yours with `List`, `Dict`, `StrBuilder`, `Ptr` | Data that lives for a while: a level, a request, a frame | You, when you call `Reset` or `Restore`, which are `unsafe`; `Arena.DebugChecks` catches mistakes while testing |
| Classes in `OpenObjectScope` | Class hierarchies, interfaces held as values, library code that calls `new` | You, entirely: nothing created inside may outlive the block |

Interfaces alone don't need the last row: a generic method constrained to the interface
(`where T : IShape`) calls a struct's methods directly, without boxing it. Classes are for when the
values really differ in type, like a `List<IShape>` holding circles and squares, and for code you
didn't write. And whatever lives as long as the program can simply be created with `new`.

The sections below go through each: arenas first, then
[temporary memory the compiler checks](#temporary-memory-the-compiler-checks), then
[ordinary objects in an arena](#ordinary-objects-in-an-arena).

## `new` is for the long-lived

Classes, virtual methods and interfaces all work in zero mode. Each `new` just costs memory for the rest
of the run:

<!-- sample: samples/VirtualTest.cs new-leaks -->
```csharp
Console.WriteLine("--- 3. Virtual methods on classes (each `new` leaks) ---");
Animal dog = new Dog();
Animal cat = new Cat();
Console.WriteLine(dog.Sound());
Console.WriteLine(cat.Sound());
```

Two objects allocated once are not a problem. The same two allocated per request, per frame or per
line of input are: the program grows until memory runs out. The same goes for boxing and for `a + b` on
strings, which also call `calloc`.

`NativeMemory.Alloc` and `Free` also work, for a block you free yourself, as in C (see
[Limits](limits.html#nativememory)). They suit code shared with .NET that already uses them;
otherwise an arena is the better tool.

## Arenas free by lifetime

An arena, `lib/Arena.cs`, hands out memory by moving a pointer forward, and frees everything it handed
out at once. Freeing all at once is what makes it work without a GC: you don't track each object, you
track how long a group of objects lives.

So the rule is **one arena per lifetime**. A program typically has one arena that lives as long as the
program, one that is reset after every frame or request, and one for scratch work. You create each one
and pass it to whatever allocates; collections and strings live in the arena you give them. Arenas
are independent of each other:

<!-- sample: samples/Arena.cs second-arena -->
```csharp
// Arenas are independent: one per lifetime. Here a second arena holds a
// list for a while and is dropped without touching the first.
Arena frame = Arena.Create(256);
List<int> numbers = List<int>.Create(frame);
for (int i = 0; i < 100; i++)
    numbers.Add(i);
```

Destroying `frame` later frees the list without touching any other arena.

A few properties follow from the design:

- **An `Arena` is a handle**, one pointer. Passing or storing it is free, and copies refer to the same
  arena. `default(Arena)` is "not created": every allocation from it returns null, and so does
  everything built on it.
- **Allocation returns null on failure**, and leaves the arena as it was. `List.Add` and the like
  return false.
- **Blocks grow as needed.** Normal blocks grow in a Fibonacci sequence from the first block's size (for
  `Create(128)`: 128, 128, 256, 384, 640 and so on), and an allocation too
  big for the next block gets a block of its own. Every allocation picks its alignment.
- **Not thread-safe.** Use one arena per thread.

## Three ways to free

**`Destroy()`** returns all of an arena's memory to the system. **`Reset()`** frees every allocation but
keeps the memory: it merges the blocks into one, so a loop that resets each round stops calling
`malloc` after the first.

**`Save()` and `Restore(mark)`** free only what was allocated since the mark. They are for temporary
work in an arena that also holds data to keep:

<!-- sample: samples/Strings.cs restore -->
```csharp
// Temporary strings: free them with Restore and keep the rest.
nuint usedBefore = s_arena.Used;
Arena.Mark mark = s_arena.Save();
bool temporaryOk;
{   // temporary can't be named after this block, so not after the Restore
    string temporary = Str.Format(s_arena, $"{name} has {name.Length} letters");
    temporaryOk = temporary.EndsWith("3 letters");
}
unsafe { s_arena.Restore(mark); } // SAFETY: nothing from the block was kept.
```

Marks must be restored newest first. Restoring a mark from another arena, or from before a `Reset()`,
ends the program with a message, and so does restoring a mark that an older one already ended, unless
enough has been allocated since to cover its position. That last case can slip through, which is why
**`OpenScope()`** is the preferred form. It is `Save()` and `Restore()` as a `using` block: leaving the
block, by any path including an early return, frees what was allocated inside it, and the pairs can't
get out of order.

```csharp no-sample
Arena.Scope temp;
// SAFETY: nothing allocated inside the block is used after it.
unsafe { temp = arena.OpenScope(); }
using (temp)
{
    // temporary allocations
}
```

`Destroy`, `Reset`, `Restore` and `OpenScope` are marked `unsafe`, because calling them is what turns
other pointers into dangling ones. [The memory safety rules](safety-rules.html) explains what that
marking means.

## The Restore hazard

One mistake none of the above prevents. A `List`, `Dict` or `StrBuilder` created before the mark may
grow after it. Its new buffer is then allocated after the mark, so `Restore` frees it while the
collection still points at it. Later allocations overwrite it, and nothing reports that.

The rule: between `Save()` and `Restore()`, only add to collections created after the mark. When
temporary work has to add to long-lived collections, do it in **`Arena.Scratch`** instead (below),
whose memory is separate from your arenas, or in a separate arena of your own, and copy the result
out before that memory is freed. With strings that copy is
`sb.ToArenaString(target)` or `Str.ToArenaString(target, s)`; with collections it is
`list.Clone(arena)` or `dict.Clone(arena)`. Copies are always explicit: `var b = a` on a `List`, `Dict`
or `StrBuilder` shares the contents, it doesn't copy them. [Keep a string](../how-to/keep-a-string.html)
shows the pattern.

## Temporary memory the compiler checks

Everything above relies on you: `Restore` and `OpenScope` are `unsafe` because the compiler can't see
when an arena frees memory, so nothing stops a span from outliving it. **`Arena.Scratch`** is the
exception. It runs a callback with a `ScratchScope` to allocate from, frees what was allocated when the
callback returns, and the compiler makes sure nothing allocated is still reachable then (with one
exception checked when the program runs, below):

<!-- sample: samples/Scratch.cs scratch -->
```csharp
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
```

The scope is a `ref struct` passed as a `scoped` parameter, so C#'s own escape rules apply to
everything it hands out. Returning a span or a ref from the callback, putting it in the state, in a
field or a static, handing the scope itself out: each is a compile error, not a dangling pointer.
Leaving out `scoped` doesn't open a way out, since a lambda that could leak no longer matches the
delegate. `tests/must-not-compile/` holds one program per way, and `test.sh` checks that the compiler
refuses each. No `unsafe` is needed anywhere.

The memory comes from a scratch arena of the thread's own, not from one of yours, so the Restore hazard
can't happen: a `List` of yours that grows during the callback grows in its own arena. Calls nest, and
each thread keeps its scratch arena, sized to the most it ever needed at once, until it ends.

The price is the callback. Data goes in and out through a state passed by `ref` (it may be a
`ref struct` holding spans of yours) and through the callback's return value, so the lambda can be
`static` and allocates nothing after its first call.

When the data only goes in, or is a span the callback fills, pass it by value instead: the callback
gets a copy, which for a span still points at your items:

<!-- sample: samples/Scratch.cs input -->
```csharp
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
```

Besides spans, the scope makes lists and text that grow as needed, `ScratchList<T>` and
`ScratchText`. They are `ref struct`s too, so the same rules keep them inside the callback, and a
string to keep is copied out into an arena of yours:

<!-- sample: samples/Scratch.cs lists -->
```csharp
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
```

`scratch.Dict<TKey, TValue>()` and `scratch.HashSet<T>()` are the dictionary and set for scratch
work, compared as `Dict` and `HashSet` are. `Clone(arena)` copies one into an arena of yours, to keep
after the callback:

<!-- sample: samples/Scratch.cs dict -->
```csharp
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
```

A `ScratchPool<T>` from `scratch.Pool<T>()` holds items found by handles, as a `Pool<T>` does. Its
`MoveTo` moves an item into a pool of yours, which is how a result made in scratch memory is kept:

<!-- sample: samples/Links.cs scratch-pool -->
```csharp
// Work in a scratch pool, then move the one result to keep into a
// pool of yours; the rest goes with the scratch memory.
var results = Pool<Job>.Create(arena);
Handle<Job> best = Arena.Scratch(ref results,
    static (scoped ScratchScope scratch, scoped ref Pool<Job> keep) =>
    {
        ScratchPool<Job> tries = scratch.Pool<Job>();
        Handle<Job> top = default;
        for (int id = 1; id <= 20; id++)
        {
            Handle<Job> next = tries.Add(new Job { Id = id * 7 % 20 });
            if (top.IsNull || tries[next].Id > tries[top].Id)
                top = next;
        }
        return tries.MoveTo(keep, ref top);
    });
```

One rule is beyond the compiler. A state passed by `ref` to a nested `Arena.Scratch` can carry the
outer scope, or a list or text it made, and anything they allocated in there would be freed with the
inner call's memory. So they allocate only while their own callback is the innermost one running:
inside the nested call they can still be read and written, but allocating from the outer scope,
adding to the outer list or appending to the outer text stops the program with a message saying so.

### Types of your own

Make a type a plain struct unless it has to hold a span or a scratch list or text: only then does it
need to be a `ref struct`, which can't go in arrays, lists, class fields or lambdas.

What the scope can give out is what the compiler can follow: spans, refs and `ref struct`s. A type of
your own works with it in one of two shapes. A struct with only unmanaged fields (numbers, enums, other
such structs) comes from `scratch.Alloc<T>()` and is used through the `ref` it returns:

<!-- sample: samples/Scratch.cs own-struct -->
```csharp
// Only unmanaged fields, so the scope can allocate one.
public struct Particle
{
    public float X;
    public float Y;
    public float Speed;
}
```

Unlike `stackalloc`, that memory isn't on the stack: its size can be decided at run time and be as
large as the machine allows, and a helper called from the callback can allocate and return it, since
it stays valid until the callback returns:

<!-- sample: samples/Scratch.cs spawn -->
```csharp
// Scratch memory isn't on the stack: a helper can allocate it and hand it
// back, and it lives until the callback that called the helper returns.
static Span<Particle> Spawn(ScratchScope scratch, int count)
    => scratch.AllocSpan<Particle>(count);
```

A `ref struct` made of scratch pieces (spans, `ScratchList`, `ScratchText`) is an ordinary local filled
from the scope. Build a collection of your own the same way, on a `ScratchList` or a span from the
scope, and it gets the same guarantees with no `unsafe`, including the nested-call check, which the
list it holds makes:

<!-- sample: samples/Scratch.cs own-collection -->
```csharp
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
```

<!-- sample: samples/Scratch.cs own-types -->
```csharp
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
```

A class can't come from scratch memory, and neither can a struct holding a `string`, an array or any
other reference. A reference can be copied anywhere (a static, a field of another object, the return
value), so nothing can make sure it is gone when the callback returns. A `ref struct` that keeps an
`Arena` and makes a `List<T>` in it is no better: nothing ties that arena to the callback. For those,
and for code you didn't write that creates objects with `new`, use `AllocateObjectsHere`, below, and
its `unsafe` promise.

## Ordinary objects in an arena

Code you didn't write for arenas still allocates the ordinary way: `new`, a lambda that captures a
variable, a boxed struct, `a + b` on strings. Inside `AllocateObjectsHere`, all of that goes into the
arena instead of to `calloc`, so resetting the arena frees it. A loop that would leak on every round
then leaks nothing:

<!-- sample: samples/Arena.cs object-scope -->
```csharp
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
```

`GC.GetTotalAllocatedBytes()` counts only the memory that is never freed, so it is the way to check.
In this sample, a thousand rounds add nothing to it, and the arena keeps a single block.

The scope is `unsafe` for the same reason `Reset` is: objects made inside it die with the arena, so
nothing may keep a reference to one. The compiler can break that rule for you in two ways:

- **Cached lambdas.** A lambda that captures nothing is created the first time it runs and kept in a
  hidden static field. If that first time is inside the scope, the static field ends up pointing into
  the arena. Run such code once before opening a scope. With `Arena.DebugChecks` on when the arena
  was created, closing the scope checks every static field and stops the program if one points into
  the arena. Static constructors are safe: whatever one creates is allocated outside every scope, since
  it's kept for the rest of the program.
- **Captured variables from outside.** A lambda that captures a variable declared outside the scope is
  cached in the object that holds that variable, which the check can't see. Declare what a lambda
  captures inside the scope, or in a method called from it, as here:

<!-- sample: samples/Queries.cs capture-in-scope -->
```csharp
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
```

### Freeing objects when the block ends

`OpenObjectScope()` is `AllocateObjectsHere()` and `OpenScope()` in one: objects go into the arena while
the block runs, and closing it frees them, so they live exactly as long as the `using` block. Nothing
has to call `Reset`, and the arena can hold longer-lived data from before the scope, which stays:

<!-- sample: samples/Arena.cs object-scope-free -->
```csharp
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
```

Both sets of rules apply: nothing created inside may be kept past the block, and no collection created
before the scope may grow inside it.

A scope applies only to the thread that opens it, so each thread of a [parallel loop](../how-to/run-in-parallel.html)
can send its objects to an arena of its own.

## Running out of memory

By default, out of memory is a value you check: null from an allocation, false from `Add`. Where the
program couldn't carry on anyway, create the arena with `stopOnOutOfMemory: true`. It then ends the
program with a message instead, so nothing built on that arena has to check. That matters most on DOS
and WebAssembly, where memory really does run out.

## Seeing what an arena does

- **Names.** `Arena.Create(size, "frame")` names an arena, and failure messages then say which arena
  they are about. The arena keeps the reference, so pass a string that outlives it, such as a literal.
- **`Dump()`** prints how full the arena is and its blocks. Many small blocks mean the first block is
  too small for the workload.
- **`PeakUsed`** is the most the arena ever held at once. Run the workload, read it, and pass that as the
  first block's size, so the arena never has to grow.

## Catching stale pointers

By default, a stale pointer isn't detected. After `Reset()`, an old pointer still reads memory that
looks valid. `Arena.DebugChecks` is the debugging aid for that. Set it first thing in `Main`:

<!-- sample: samples/Arena.cs debug-checks -->
```csharp
// Debugging aid: arenas created while DebugChecks is on fill freed memory
// with 0xDD, so a stale pointer reads an obvious pattern instead of the
// old value, and collections stop the program when used after a reset.
Arena.DebugChecks = true;
Arena debug = Arena.Create(256);
int* stale = debug.AllocArray<int>(4);
unsafe { stale[0] = 12345; }
```

Arenas created afterwards fill freed memory with `0xDD` bytes, so a stale pointer reads `0xDDDDDDDD`
instead of plausible old data. `List`, `Dict` and `StrBuilder` also check on every use that their
memory is still allocated, and stop the program with a message if it isn't. That catches a collection
used after its arena was reset, and the Restore hazard above.

It can't catch a stale pointer into memory that has since been handed out again. And it costs a fill on
every free and a check on every collection operation, so leave it off in release builds.
[Catch arena mistakes](../how-to/catch-arena-mistakes.html) shows it in use, and the
[Arena reference](../reference/arena.html) lists every member.
