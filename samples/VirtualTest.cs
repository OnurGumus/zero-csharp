// Example: Polymorphism in --stdlib:zero, three ways.
// Build: ./build.sh VirtualTest
//
// 1. Function-pointer "vtables" (C style). Zero allocations; calling through
//    a function pointer needs an unsafe block.
// 2. Static abstract interface members + generics. Resolved at compile time,
//    one specialized copy per struct type: zero allocations, no indirect call.
//    This is the pattern to use for hot loops (e.g. per-format kernels).
// 3. Classes with virtual methods and interfaces. These work in zero mode,
//    but every `new` mallocs and is never freed (there is no GC).

// ---- 1. Function-pointer vtable --------------------------------------------

using System;

namespace VirtualTestSample;

public struct Shape
{
    public delegate*<Shape*, int> GetArea;
    public int Param1;
    public int Param2;
}

public static class Shapes
{
    static int RectangleArea(Shape* s) { unsafe { return s->Param1 * s->Param2; } }
    static int CircleArea(Shape* s) { unsafe { return 3 * s->Param1 * s->Param1; } }

    public static Shape Rectangle(int w, int h) => new Shape { GetArea = &RectangleArea, Param1 = w, Param2 = h };
    public static Shape Circle(int r) => new Shape { GetArea = &CircleArea, Param1 = r };
}

// ---- 2. Static abstract interface members ------------------------------------

// docs:static-abstract
public interface IArea<TSelf> where TSelf : struct, IArea<TSelf>
{
    static abstract string Name { get; }
    static abstract int Area(in TSelf shape);
}

public struct Rect : IArea<Rect>
{
    public int W, H;
    public static string Name => "Rect";
    public static int Area(in Rect r) => r.W * r.H;
}
// docs:end

public struct Square : IArea<Square>
{
    public int Side;
    public static string Name => "Square";
    public static int Area(in Square s) => s.Side * s.Side;
}

// ---- 3. Classes with virtual methods ----------------------------------------

public abstract class Animal
{
    public abstract string Sound();
}

public sealed class Dog : Animal
{
    public override string Sound() => "Woof";
}

public sealed class Cat : Animal
{
    public override string Sound() => "Meow";
}

public static class Program
{
    public static int Main()
    {
        Console.WriteLine("=== Polymorphism in --stdlib:zero ===");
        Console.WriteLine();

        Console.WriteLine("--- 1. Function-pointer vtable ---");
        Shape rect = Shapes.Rectangle(10, 5);
        Shape circle = Shapes.Circle(7);
        unsafe
        {
            // SAFETY: GetArea was set by the factory to a function that reads only *s.
            Console.WriteLine($"Rectangle area: {rect.GetArea(&rect)}");
            Console.WriteLine($"Circle area: {circle.GetArea(&circle)}");
        }
        Console.WriteLine();

        Console.WriteLine("--- 2. Static abstract interface (compile-time dispatch) ---");
        PrintArea(new Rect { W = 3, H = 4 });
        PrintArea(new Square { Side = 6 });
        Console.WriteLine();

        // docs:new-leaks
        Console.WriteLine("--- 3. Virtual methods on classes (each `new` leaks) ---");
        Animal dog = new Dog();
        Animal cat = new Cat();
        Console.WriteLine(dog.Sound());
        Console.WriteLine(cat.Sound());
        // docs:end
        Console.WriteLine();

        Console.WriteLine("Done!");
        return 0;
    }

    // docs:generic-call
    static void PrintArea<T>(in T shape) where T : struct, IArea<T>
    {
        Console.Write(T.Name);
        Console.WriteLine($" area: {T.Area(in shape)}");
    }
    // docs:end
}
