// Example: a program that uses a library of its own, libs/Geometry.
// Exit code = number of failed checks.
// Build: ./build.sh Shapes
//
// build.sh compiles libs/Geometry/ into bin/libs/Geometry.dll and passes it
// to every program with -r, so `using Geometry;` is all this file needs.

using System;
using Geometry;

namespace ShapesSample;

public static class Program
{
    static int s_failures;

    static void Check(string what, bool ok)
    {
        if (!ok)
        {
            Console.WriteLine($"FAIL {what}");
            s_failures++;
        }
    }

    public static int Main()
    {
        // docs:use
        Span<Rect> rooms = stackalloc Rect[]
        {
            new Rect(4, 3), new Rect(2.5, 2), new Rect(5, 4),
        };
        Console.WriteLine($"Floor area: {Measure.TotalArea<Rect>(rooms):F2}");

        var table = new Circle(0.5);
        Console.WriteLine($"Table: area {table.Area:F2}, "
            + $"perimeter {table.Perimeter:F2}");

        Arena arena = Arena.Create(1024, "shapes");
        List<Point> corners = Measure.Corners(rooms[0], arena);
        for (int i = 0; i < corners.Count; i++)
            Console.WriteLine(corners.Get(i).ToString());
        unsafe { arena.Destroy(); } // SAFETY: corners isn't used after this.
        // docs:end

        Check("total area", Measure.TotalArea<Rect>(rooms) == 12 + 5 + 20);
        Check("same area", Measure.SameArea(new Rect(2, 8), new Rect(4, 4)));
        Check("different area", !Measure.SameArea(new Rect(2, 8), new Circle(2)));
        Check("record equality across the library", new Point(1, 2) == new Point(1, 2));
        Console.WriteLine(s_failures == 0 ? "PASS Geometry" : "FAIL Geometry");
        return s_failures;
    }
}
