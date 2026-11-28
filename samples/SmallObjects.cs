// Test: classes, interfaces, generics, structs and delegates, sized for a
// small machine. Casts.cs and Core.cs check more and don't fit in a
// Commodore 64 (./build.sh --c64); this is the part every program leans on.
// It runs on every target. Exit code = number of failed checks.
// Build: ./build.sh SmallObjects

using System;
using System.Runtime.CompilerServices;

namespace SmallObjectsSample;

public interface IShape
{
    string Name { get; }
    int Area();
}

public interface IScalable
{
    void Scale(int factor);
}

public class Circle : IShape
{
    public int Radius;
    public string Name => "circle";
    public int Area() => 3 * Radius * Radius;
}

public class Rect : IShape, IScalable
{
    public int W, H;
    public virtual string Name => "rect";
    public int Area() => W * H;
    public void Scale(int factor) { W *= factor; H *= factor; }
}

public class Square : Rect // both interfaces through the base class
{
    public override string Name => "square";
}

public abstract class Counter
{
    public static int Created;      // a static field of a class
    protected int Count;
    protected Counter() { Created++; }
    public abstract int Step { get; }
    public int Next() => Count += Step;
}

public class ByOne : Counter { public override int Step => 1; }
public class ByTen : Counter { public override int Step => 10; }

public interface IBox<T> { T Value { get; } }

public class Box<T> : IBox<T>
{
    public T Item;
    public T Value => Item;
}

public struct Point
{
    public int X, Y;
    public Point(int x, int y) { X = x; Y = y; }
    public int Sum => X + Y;
    public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
}

public struct Big // returned through a hidden pointer, not in registers
{
    public long A, B, C;
    public int D;
}

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void End(string section)
    {
        Console.Write(s_sectionFailures == 0 ? "PASS " : "FAIL ");
        Console.Write(section);
        Console.Write(" (");
        Console.Write(s_sectionChecks);
        Console.WriteLine(" checks)");
        s_failures += s_sectionFailures;
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_sectionFailures++;
            Console.Write("  failed: ");
            Console.WriteLine(name);
        }
    }

    // Hides an object's type from the optimizer, which would otherwise answer
    // most of these checks at build time.
    // Always false: see SmallIntegers.cs. It keeps a compiler that optimizes
    // the whole program at once from seeing through the method below.
    static bool s_never;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static object Opaque(object o) => s_never ? null : o;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Opaque(int value) => s_never ? 0 : value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Big MakeBig(int seed) => new Big { A = seed, B = seed * 2L, C = seed * 3L, D = seed + 100 };

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Mix(int a, long b, short c, long d, byte e, int f, long g) => a + b + c + d + e + f + g;

    static void Double(ref Point p) { p.X *= 2; p.Y *= 2; }

    static ref int Largest(int[] values)
    {
        int at = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > values[at])
                at = i;
        }
        return ref values[at];
    }

    static T Pick<T>(bool first, T a, T b) => first ? a : b;

    static int Twice(int x) => 2 * x;

    static int Apply(Func<int, int> f, int x) => f(x);

    static int TotalArea(object[] items)
    {
        int total = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is IShape shape)
                total += shape.Area();
        }
        return total;
    }

    public static int Main()
    {
        s_never = false;
        object circle = Opaque(new Circle { Radius = 2 });
        object rect = Opaque(new Rect { W = 3, H = 4 });
        object square = Opaque(new Square { W = 5, H = 5 });
        object nothing = Opaque(null);

        Check("is, implemented directly", circle is IShape && rect is IScalable);
        Check("is, not implemented", !(circle is IScalable) && !(circle is Rect));
        Check("is, through the base class", square is IShape && square is IScalable && square is Rect);
        Check("null is never an instance", !(nothing is IShape));
        IShape shape = rect as IShape;
        Check("as, then a call", shape != null && shape.Area() == 12 && shape.Name == "rect");
        Check("as gives null", (circle as Rect) == null && (nothing as IShape) == null);
        ((IScalable)square).Scale(2);
        Check("a call that changes the object", ((IShape)square).Area() == 100);
        Check("virtual property, overridden", ((Rect)square).Name == "square" && ((IShape)square).Name == "square");
        Check("interface back to class", ((Rect)(IShape)rect).W == 3);
        object numbers = Opaque(new int[3]);
        Check("an array is an Array", numbers is Array array && array.Length == 3
            && !(Opaque(circle) is Array) && !(numbers is ValueType));
        object words = Opaque(new string[] { "a", "b" });
        Check("array covariance", words is object[] all && all.Length == 2
            && words is IComparable[] && !(words is IShape[]) && !(numbers is object[]));
        End("interfaces and casts");

        Counter one = (Counter)Opaque(new ByOne());
        Counter ten = (Counter)Opaque(new ByTen());
        Check("abstract method", one.Next() == 1 && one.Next() == 2 && ten.Next() == 10 && ten.Next() == 20);
        Check("base constructor and static field", Counter.Created == 2);
        object[] items = { circle, rect, square, Opaque("text"), null };
        Check("array of objects", items.Length == 5 && TotalArea(items) == 124);
        IShape[] shapes = new IShape[2];
        shapes[0] = (IShape)circle; // storing checks the element type
        shapes[1] = new Square { W = 1, H = 1 };
        Check("array of an interface", shapes[0].Area() + shapes[1].Area() == 13);
        int total = 0;
        for (int i = 0; i < 100; i++)
            total += (i % 2 == 0 ? one : ten).Step;
        Check("same call site, alternating types", total == 550);
        End("classes");

        object intBox = Opaque(new Box<int> { Item = 42 });
        object stringBox = Opaque(new Box<string> { Item = "hi" });
        Check("generic interface", intBox is IBox<int> b && b.Value == 42 && !(intBox is IBox<long>));
        Check("generic class of a reference type", stringBox is Box<string> s && s.Value == "hi"
            && !(stringBox is Box<int>));
        Check("generic method", Pick(Opaque(1) == 1, 1, 2) == 1 && Pick(Opaque(1) == 2, "a", "b") == "b");
        object boxed = Opaque((object)7);
        Check("boxing and unboxing", boxed is int && (int)boxed == 7 && !(boxed is long));
        object boxedPoint = Opaque(new Point(1, 2));
        Check("boxed struct", boxedPoint is Point p && p.Sum == 3);
        End("generics and boxing");

        Point point = new Point(Opaque(3), Opaque(4));
        Point copy = point;
        copy.X = 30;
        Check("a struct is copied", point.X == 3 && copy.X == 30);
        Double(ref point);
        Check("ref parameter", point.X == 6 && point.Y == 8);
        Check("operator", (point + copy).Sum == 48);
        int[] values = { 5, Opaque(9), 2 };
        Largest(values) = 0;
        Check("ref return", values[0] == 5 && values[1] == 0 && values[2] == 2);
        Big big = MakeBig(Opaque(10));
        Check("large struct result", big.A == 10 && big.B == 20 && big.C == 30 && big.D == 110);
        Check("many arguments of mixed sizes", Mix(Opaque(1), 20000000000, -3, 4, 200, -6, Opaque(7)) == 20000000203);
        End("structs");

        int offset = Opaque(10);
        Func<int, int> twice = Twice;
        Func<int, int> addOffset = x => x + offset;
        Check("delegate to a static method", Apply(twice, 21) == 42);
        Check("lambda capturing a local", Apply(addOffset, 5) == 15);
        offset = 20;
        Check("captured local is shared", addOffset(5) == 25);
        Func<int> area = ((Circle)circle).Area;
        Check("delegate to an instance method", area() == 12);
        End("delegates");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
        {
            Console.Write(s_failures);
            Console.WriteLine(" checks FAILED.");
        }
        return s_failures;
    }
}
