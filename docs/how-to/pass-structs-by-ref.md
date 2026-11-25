---
title: Pass structs by reference
index: 11
---

# Pass structs by reference

A struct is copied every time you pass it or return it. For a small struct that costs little, but
the copy is a different value: changes to it don't reach the original. `ref` passes the address
instead, so the method works on the caller's struct, wherever it lives: on the stack, in an array or
in an arena. This guide allocates a struct in an arena, passes it with `ref` and `in`, returns it by
reference, and shows the two ways such a ref can outlive its memory.

The code comes from [`samples/Refs.cs`](https://github.com/OnurGumus/zero-csharp/blob/master/samples/Refs.cs). Put it in a zero project's `Program.cs`
and `dotnet build`, or in the repository, `./build.sh Refs` and `./run.sh Refs`.

## Why `ref` when nothing moves

In .NET, a ref is how code keeps hold of an object the garbage collector may move. Zero mode has no
garbage collector, so nothing moves, and a pointer would work just as well. A ref still has advantages:

- It works for any `T`. A pointer `T*` needs `T : unmanaged`, so it can't point at a struct that holds
  a string or at a generic `T`.
- It can't be null, and there's no arithmetic on it.
- Reading and writing through it needs no `*` and no `unsafe` block.
- The compiler tracks where it points. A ref to a local can't be returned or stored past the local's
  method.

After compiling, a ref is an address, the same machine code as a pointer.

## Allocate the struct and return a ref to it

Here is the struct:

<!-- sample: samples/Refs.cs point -->
```csharp
public struct Point
{
    public int X;
    public int Y;
}
```

`Arena.AllocSpan<T>(1)` returns a span of one element, and `ref span[0]` is a ref to that element.
A method can return that ref, because the memory isn't in its stack frame:

<!-- sample: samples/Refs.cs new-point -->
```csharp
// Allocates a point in the arena and returns a ref to it, not a copy.
// `unsafe` because the ref dangles once the arena frees the memory,
// which the compiler can't see: callers opt in, as for AllocSpan.
static unsafe ref Point NewPoint(Arena arena, int x, int y)
{
    Span<Point> one;
    unsafe { one = arena.AllocSpan<Point>(1); }
    // An empty span if the arena is out of memory: [0] then stops
    // the program.
    ref Point p = ref one[0];
    p.X = x;
    p.Y = y;
    return ref p;
}
```

`NewPoint` is marked `unsafe` for the same reason `AllocSpan` is. The compiler checks that a ref doesn't
outlive the stack frame it points into, but it treats arena memory as if it lived forever. It can't
tell when `Reset`, `Restore` or the end of a scope frees it. Marking the method `unsafe` makes each
caller opt in and state why the arena outlives the ref. See
[the memory safety rules](../concepts/safety-rules.html).

`Arena.Alloc<T>()` returns a `Point*` instead. `return ref *arena.Alloc<Point>();` does the same thing,
but only for an unmanaged `T`, with the dereference in an `unsafe` block.

## Pass it with `ref` or `in`, and return a ref you were given

`ref` lets a method change the caller's struct. `in` passes the address too, but the method can only
read through it. A method can also return one of the refs it was given:

<!-- sample: samples/Refs.cs by-ref -->
```csharp
// Changes the caller's point in place.
static void Move(ref Point p, int dx, int dy)
{
    p.X += dx;
    p.Y += dy;
}

// Reads the caller's point without copying it, and can't change it.
static int LengthSquared(in Point p) => p.X * p.X + p.Y * p.Y;

// Returns one of the two refs it was given, not a copy.
static ref Point Farther(ref Point a, ref Point b)
{
    if (LengthSquared(in a) >= LengthSquared(in b))
        return ref a;
    return ref b;
}
```

None of these three methods is `unsafe`: they work on refs their caller already holds, and the
compiler checks that `Farther` only returns a ref that lives as long as its arguments.

The caller writes `ref` or `in` at the call too, and keeps the result in a `ref` local. Assigning
to `far` writes to `a`'s memory in the arena:

<!-- sample: samples/Refs.cs use -->
```csharp
// SAFETY: the arena isn't reset or destroyed inside this block.
unsafe
{
    ref Point a = ref NewPoint(arena, 1, 2);
    ref Point b = ref NewPoint(arena, 5, 5);
    Move(ref a, 10, 0);                // a is (11, 2) now
    ref Point far = ref Farther(ref a, ref b);
    far.Y = 99;                        // writes to a, in the arena
    Console.WriteLine($"a = ({a.X}, {a.Y}), b = ({b.X}, {b.Y})");
    CheckRefs(ref a, ref b);
}
```

It prints `a = (11, 99), b = (5, 5)`. Without `ref` on the local, `Point far = Farther(...)` would
copy the point, and the write would change only the copy. The sample also checks that a thousand
calls to `Move` and `Farther` allocate nothing.

If you pass a struct with `in` and call a method on it, mark the struct `readonly struct` when you can.
Otherwise the compiler can't be sure the method leaves the struct unchanged, and it calls the method on
a hidden copy, which costs the copy that `in` was meant to avoid. Reading fields, as `LengthSquared`
does, never makes a copy.

A struct's own method or property can't return a ref to one of its fields, because the struct might
be a local about to go away. Mark it `[UnscopedRef]` (`System.Diagnostics.CodeAnalysis`) and the
compiler allows it, checking instead that the caller's struct outlives the ref:

<!-- sample: samples/Core.cs unscoped-ref -->
```csharp
// [UnscopedRef] lets a struct hand out a ref to its own field.
public struct Counter
{
    int _value;
    [UnscopedRef] public ref int Value => ref _value;
}
```

## Work on elements in place

The same works for a span of structs. `ref points[i]` is a ref to the element, and `foreach (ref ...)`
gives each element by reference:

<!-- sample: samples/Refs.cs elements -->
```csharp
Span<Point> points;
// SAFETY: the span isn't used after the arena is reset.
unsafe { points = arena.AllocSpan<Point>(3); }
for (int i = 0; i < points.Length; i++)
{
    ref Point p = ref points[i];
    p.X = i;
    p.Y = i * i;
}
// `foreach (ref ...)` changes the elements in place too.
foreach (ref Point p in points)
    Move(ref p, 100, 0);
```

## Don't keep a ref past the memory it points at

The compiler stops a ref from outliving a stack frame. It doesn't stop the two mistakes below,
because both are about memory the library frees or moves. Both blocks are wrong on purpose, and the
sample checks that the mistake happens as described.

**A list moves its items when it grows.** `List<T>.AsSpan()` is a view of the list's buffer. When the
list is full and its buffer isn't the arena's last allocation, `Add` copies the items to a new buffer.
A ref taken before then still points at the old buffer:

<!-- sample: samples/Refs.cs stale-list -->
```csharp
List<Point> list = List<Point>.Create(arena, 4);
for (int i = 0; i < 4; i++)
    list.Add(new Point { X = i });
// WRONG, on purpose: the ref outlives the list's buffer.
unsafe
{
    ref Point first = ref list.AsSpan()[0];
    arena.Alloc<int>();     // the list's buffer isn't last any more,
    list.Add(new Point());  // so growing copies it somewhere new
    first.X = 42;           // and this writes to the old copy
    Console.WriteLine($"first.X = {first.X}, list[0].X = {list.Get(0).X}");
}
```

It prints `first.X = 42, list[0].X = 0`: the write went to memory the list no longer uses. So
"nothing moves" holds for the garbage collector, not for your own data structures. Take a ref to a
list element after the last `Add`, `RemoveAt` or `Clear`, and don't keep it across one.

**A scope frees what was allocated inside it.** When a `using` block over `arena.OpenScope()` ends, the
arena hands the memory out again. A ref local declared outside the block can still be assigned a ref
from inside it:

<!-- sample: samples/Refs.cs stale-scope -->
```csharp
Point outside = default;
ref Point leaked = ref outside;
Arena.Scope temp;
// WRONG, on purpose: `leaked` is used after the scope frees it.
unsafe
{
    temp = arena.OpenScope();
    using (temp)
        leaked = ref NewPoint(arena, 7, 7);
    // The scope gave the memory back; the next point gets it.
    ref Point next = ref NewPoint(arena, 1, 1);
    Console.WriteLine($"leaked = ({leaked.X}, {leaked.Y})");
}
```

It prints `leaked = (1, 1)`: `leaked` now reads the next point. Nothing stops the program, because
the memory is still the arena's. With `Arena.DebugChecks` on, the freed bytes are overwritten with a
pattern first, which makes such reads easier to spot; see
[Catch arena mistakes](catch-arena-mistakes.html).

The rule for both: a ref into an arena or a collection is good only while that memory is. Keep it
inside the block whose `SAFETY` comment says so, and don't store it where it can outlive that block.
