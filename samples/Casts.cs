// Example: runtime type checks and casts in zero mode.
// Build: ./build.sh Casts
//
// `is`, `as` and `(T)x` to classes and interfaces work as in .NET, except that
// a failed `(T)x` ends the process instead of throwing InvalidCastException.
// The checks cost a walk up the base types (classes) or over the type's
// interface list (interfaces); there's no cache.
//
// Also covered, since they share the same machinery: arrays of interfaces,
// delegates, and static constructors.
//
// Not supported, and reported at run time rather than answered wrongly:
// variance (IProducer<string> as IProducer<object>, string[] as object[]).

using System;

namespace CastsSample;

public interface INamed
{
    string Name { get; }
}

public interface IShape : INamed // implementing IShape brings INamed along
{
    int Area();
}

public interface IScalable
{
    void Scale(int factor);
}

public interface IUnrelated { }

public interface IBox<T>
{
    T Value { get; }
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

public class Square : Rect // IShape and IScalable through the base class
{
    public override string Name => "square";
}

public class Plain { }

public struct Big // returned through a hidden pointer, not in registers
{
    public long A, B, C, D;
}

public interface ICalc
{
    // Enough arguments to fill every integer and floating-point argument
    // register and spill onto the stack.
    double Mix(int a, long b, int c, long d, int e, long f, int g, long h, int i,
               double p, float q, double r, float s, double t, float u, double v, float w, double x);
    Big Make(long seed);
    int Describe() => 1000 + Id; // default implementation
    int Id { get; }
}

public sealed class Calc : ICalc
{
    public int Base;
    public double Mix(int a, long b, int c, long d, int e, long f, int g, long h, int i,
                      double p, float q, double r, float s, double t, float u, double v, float w, double x)
        => Base + a + b + c + d + e + f + g + h + i + p + q + r + s + t + u + v + w + x;
    public Big Make(long seed) => new Big { A = seed, B = seed * 2, C = seed * 3, D = seed * 4 + Base };
    public int Id => 7;
}

public class LoudCalc : ICalc
{
    public double Mix(int a, long b, int c, long d, int e, long f, int g, long h, int i,
                      double p, float q, double r, float s, double t, float u, double v, float w, double x) => -1;
    public Big Make(long seed) => default;
    public int Describe() => 2000 + Id; // replaces the default
    public virtual int Id => 8;
}

public class LouderCalc : LoudCalc
{
    public override int Id => 9;
}

public class Box<T> : IBox<T>
{
    public T Item;
    public T Value => Item;
}

public struct Point : INamed // boxed when converted to the interface
{
    public int X, Y;
    public string Name => "point";
}

public static class Registry
{
    public static readonly IShape[] Shapes;
    public static int Constructed;

    static Registry()
    {
        Shapes = new IShape[] { new Circle { Radius = 2 }, new Rect { W = 3, H = 4 }, new Square { W = 5, H = 5 } };
        Constructed++;
    }
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

    static int Twice(int x) => 2 * x;

    static int Apply(System.Func<int, int> f, int x) => f(x);

    // Hides an object's type from the optimizer, which would otherwise answer
    // most of these checks at build time and never call the runtime's code.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static object Opaque(object o) => o;

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
        // docs:interface-checks
        object circle = Opaque(new Circle { Radius = 2 });
        object rect = Opaque(new Rect { W = 3, H = 4 });
        object square = Opaque(new Square { W = 5, H = 5 });
        object plain = Opaque(new Plain());
        object nothing = Opaque(null);

        Check("class implementing it directly", circle is IShape);
        Check("second interface of a class", rect is IScalable);
        Check("not implemented", !(circle is IScalable));
        Check("unrelated class", !(plain is IShape));
        Check("unrelated interface", !(rect is IUnrelated));
        Check("null is never an instance", !(nothing is IShape));
        Check("through the base class", square is IShape && square is IScalable);
        Check("through another interface", circle is INamed && square is INamed);
        // docs:end
        Section("is");

        IShape asShape = rect as IShape;
        Check("as gives the same object", (object)asShape == rect);
        Check("call through the result", asShape.Area() == 12);
        Check("as gives null when not implemented", (plain as IShape) == null);
        Check("as on null", (nothing as IShape) == null);
        INamed named = square as INamed;
        Check("virtual property through an inherited interface", named.Name == "square");
        Section("as");

        IScalable scalable = (IScalable)square;
        scalable.Scale(2);
        Check("(T)x, then a call that changes the object", ((IShape)square).Area() == 100);
        Check("(T)null is null", (IShape)nothing == null);
        Check("interface to interface", ((INamed)(IShape)circle).Name == "circle");
        Check("interface back to class", ((Rect)(IShape)rect).W == 3);
        Check("interface to a class it isn't", ((IShape)circle as Rect) == null);
        Section("(T)x");

        object intBox = Opaque(new Box<int> { Item = 42 });
        object stringBox = Opaque(new Box<string> { Item = "hi" });
        Check("generic interface, same type argument", intBox is IBox<int> b && b.Value == 42);
        Check("generic interface, other type argument", !(intBox is IBox<long>));
        Check("generic interface of a reference type", stringBox is IBox<string> s && s.Value == "hi");
        Check("generic class", stringBox is Box<string> && !(stringBox is Box<int>));
        Section("generics");

        object point = Opaque(new Point { X = 1, Y = 2 });
        Check("boxed struct is its interface", point is INamed n && n.Name == "point");
        Check("boxed struct isn't another", !(point is IShape));
        Check("unboxing", point is Point p && p.Y == 2);
        Section("structs");

        Check("static constructor ran once", Registry.Shapes.Length == 3 && Registry.Constructed == 1);
        Check("interface call per element", Registry.Shapes[0].Area() + Registry.Shapes[1].Area() + Registry.Shapes[2].Area() == 49);
        object[] mixed = { circle, plain, rect, null, "text", point };
        Check("pattern matching over object[]", TotalArea(mixed) == 24);
        object array = Opaque(Registry.Shapes);
        Check("array is its own type", array is IShape[] shapes && shapes.Length == 3);
        Check("non-array isn't an array type", !(circle is IShape[]));
        IShape[] target = new IShape[2];
        target[0] = (IShape)circle; // storing checks the element type
        target[1] = new Square { W = 1, H = 1 };
        Check("storing into an interface array", target[0].Area() + target[1].Area() == 13);
        Section("arrays");

        ICalc calc = (ICalc)Opaque(new Calc { Base = 100 });
        ICalc loud = (ICalc)Opaque(new LoudCalc());
        ICalc louder = (ICalc)Opaque(new LouderCalc());
        Check("many arguments, in registers and on the stack",
            calc.Mix(1, 2, 3, 4, 5, 6, 7, 8, 9, 0.5, 1.5f, 2.5, 3.5f, 4.5, 5.5f, 6.5, 7.5f, 8.5) == 185.5);
        Big big = calc.Make(10);
        Check("large struct result", big.A == 10 && big.B == 20 && big.C == 30 && big.D == 140);
        Check("method of a sealed class", calc.Id == 7);
        Check("default interface method", calc.Describe() == 1007);
        Check("class replacing the default", loud.Describe() == 2008);
        Check("override in a derived class", louder.Id == 9 && louder.Describe() == 2009);
        int total = 0;
        for (int i = 0; i < 1000; i++)
            total += (i % 2 == 0 ? calc : louder).Id;
        Check("same call site, alternating types", total == 8000);
        Section("interface calls");

        int offset = 10;
        System.Func<int, int> twice = Twice;
        System.Func<int, int> addOffset = x => x + offset;
        Check("delegate to a static method", Apply(twice, 21) == 42);
        Check("lambda capturing a local", Apply(addOffset, 5) == 15);
        offset = 20;
        Check("captured local is shared", addOffset(5) == 25);
        Section("delegates");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine($"{s_failures} checks FAILED.");
        return s_failures;
    }
}
