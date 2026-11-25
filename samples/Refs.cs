// Example: structs that live in an arena, passed and returned by reference
// with `ref`, `in` and ref returns. Exit code = number of failed checks.
// Build: ./build.sh Refs
//
// Without a garbage collector nothing moves, so a ref isn't there to follow
// the GC. It is a pointer that can't be null, needs no dereference, works for
// any T, and that the compiler checks: it rejects a ref that outlives the
// stack frame it points into. It doesn't know when an arena frees memory or
// when a List copies its items, which the last two sections show. That is why
// the members that hand out arena memory as a span are `unsafe`.

using System;

namespace RefsSample;

// docs:point
public struct Point
{
    public int X;
    public int Y;
}
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

    // docs:new-point
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
    // docs:end

    // docs:by-ref
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
    // docs:end

    static void CheckRefs(ref Point a, ref Point b)
    {
        Check("Move changed the point in the arena", a.X == 11);
        Check("the returned ref is a, not a copy", a.Y == 99);
        Check("b is unchanged", b.X == 5 && b.Y == 5);

        long before = GC.GetTotalAllocatedBytes();
        for (int i = 0; i < 1000; i++)
        {
            Move(ref a, 1, 0);
            Farther(ref a, ref b).X -= 1;
        }
        long allocated = GC.GetTotalAllocatedBytes() - before;
        Check("passing refs allocates nothing", allocated == 0);
        Check("and copies nothing back", a.X == 11);
    }

    public static int Main()
    {
        Arena arena = Arena.Create(4096);

        // docs:use
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
        // docs:end
        Section("refs into an arena");

        // docs:elements
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
        // docs:end
        Check("three points", points.Length == 3);
        Check("set through ref locals", points[2].Y == 4);
        Check("moved through foreach ref", points[0].X == 100 && points[2].X == 102);
        Section("refs to elements");

        // docs:stale-list
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
        // docs:end
        Check("the list didn't see the write", list.Get(0).X == 0);
        Section("a ref outlives a list's buffer");

        // docs:stale-scope
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
        // docs:end
        Check("the leaked ref sees the next point", leaked.X == 1 && leaked.Y == 1);
        Section("a ref outlives an arena scope");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine($"{s_failures} checks FAILED.");
        return s_failures;
    }
}
