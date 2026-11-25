// Example: the smaller parts of the core library (zerocore): Math and MathF,
// Interlocked, Volatile, interface calls from one call site to many classes,
// extension members, C# 15 unions (zerocore/System/Exceptions.cs and Unions.cs
// explain what those two need from the library), caller-info attributes,
// indices and ranges, and NativeMemory.
// Exit code = number of failed checks.
// Build: ./build.sh Core

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace CoreSample;

public interface IShape
{
    int Sides { get; }
}

// docs:extension-members
public struct Vec
{
    public int X, Y;
}

public static class VecExtensions
{
    // Classic extension method: `this` on the first parameter.
    public static int Dot(this Vec a, Vec b) => a.X * b.X + a.Y * b.Y;

    // C# 14 extension block: properties and methods on a Vec value.
    extension(Vec v)
    {
        public int LengthSquared => v.Dot(v);
        public bool IsZero => v.X == 0 && v.Y == 0;
        public Vec Scale(int k) => new Vec { X = v.X * k, Y = v.Y * k };
    }

    // Static members, called as Vec.Zero and Vec.Of(1, 2).
    extension(Vec)
    {
        public static Vec Zero => default;
        public static Vec Of(int x, int y) => new Vec { X = x, Y = y };
    }

    // By reference: changes the caller's variable.
    extension(ref Vec v)
    {
        public void Translate(int dx, int dy)
        {
            v.X += dx;
            v.Y += dy;
        }
    }
}
// docs:end

// docs:union-declaration
public struct Parsed { public int Value; }
public struct BadChar { public int Position; }

// A ParseResult holds either a Parsed or a BadChar, and a switch on it must
// handle both.
public union ParseResult(Parsed, BadChar);
// docs:end

// Items of 3 and 24 bytes, for the span copy and fill checks.
public struct Three { public byte A, B, C; }
public struct Big { public long A, B, C; }

// docs:equatable-struct
public readonly struct Point : IEquatable<Point>
{
    public readonly int X, Y;
    public Point(int x, int y) { X = x; Y = y; }
    public bool Equals(Point other) => X == other.X && Y == other.Y;
    public override int GetHashCode() => HashCode.Combine(X, Y);
}
// docs:end

// docs:unscoped-ref
// [UnscopedRef] lets a struct hand out a ref to its own field.
public struct Counter
{
    int _value;
    [UnscopedRef] public ref int Value => ref _value;
}
// docs:end

public enum Unsigned : uint { Low = 1, High = 0x80000000 }
public enum Signed : sbyte { Negative = -1, Positive = 1 }

public class Triangle : IShape { public int Sides => 3; }
public class Square : IShape { public int Sides => 4; }
public class Pentagon : IShape { public int Sides => 5; }
public class Hexagon : IShape { public int Sides => 6; }
public class Heptagon : IShape { public int Sides => 7; }
public class Octagon : IShape { public int Sides => 8; }

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

    // Hides a value from the optimizer, which would otherwise do the math at
    // build time and never run the code being tested.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static double Opaque(double value) => value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static float Opaque(float value) => value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Opaque(int value) => value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static object Opaque(object value) => value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Opaque(string value) => value;

    static string CallerMember([CallerMemberName] string member = null) => member;
    static int CallerLine([CallerLineNumber] int line = 0) => line;
    static string CallerFile([CallerFilePath] string file = null) => file;
    static string ArgumentText(int value, [CallerArgumentExpression(nameof(value))] string text = null) => text;

    static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-12;

    static bool Near(float actual, float expected) => MathF.Abs(actual - expected) < 1e-5f;

    // One call site, whichever class each element has.
    static int TotalSides(IShape[] shapes)
    {
        int total = 0;
        for (int i = 0; i < shapes.Length; i++)
            total += shapes[i].Sides;
        return total;
    }

    // docs:union-function
    static ParseResult ParseNumber(string text)
    {
        int value = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < '0' || c > '9')
                return new BadChar { Position = i };
            value = value * 10 + (c - '0');
        }
        return new Parsed { Value = value };
    }

    static int ValueOrMinusOne(string text) => ParseNumber(text) switch
    {
        Parsed p => p.Value,
        BadChar => -1,
    };
    // docs:end

    // Every overlapping copy of up to 40 bytes, both ways, against copying
    // through a temporary buffer.
    static bool CopiesLikeALoop()
    {
        Span<byte> actual = stackalloc byte[96];
        Span<byte> expected = stackalloc byte[96];
        Span<byte> temp = stackalloc byte[40];
        for (int length = 0; length <= 40; length++)
        {
            for (int shift = -20; shift <= 20; shift += 1)
            {
                for (int i = 0; i < 96; i++)
                    actual[i] = expected[i] = (byte)(i * 7 + 1);
                int from = 28;
                int to = 28 + shift;
                for (int i = 0; i < length; i++)
                    temp[i] = expected[from + i];
                for (int i = 0; i < length; i++)
                    expected[to + i] = temp[i];
                actual.Slice(from, length).CopyTo(actual.Slice(to, length));
                if (!SameBytes(actual, expected))
                    return false;
            }
        }
        return true;
    }

    static bool SameBytes(Span<byte> left, Span<byte> right)
    {
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }
        return true;
    }

    // Fill (or Clear, for a zero value) of 0 to 40 items: each one holds the
    // value's bytes, and the items around them are untouched.
    static bool FillsLikeALoop<T>(T value) where T : unmanaged
    {
        Span<T> items = stackalloc T[42];
        int size = Unsafe.SizeOf<T>();
        bool zero = true;
        for (int b = 0; b < size; b++)
            zero &= ByteOf(ref value, b) == 0;
        for (int count = 0; count <= 40; count++)
        {
            Span<byte> all = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref items[0]), 42 * size);
            for (int i = 0; i < all.Length; i++)
                all[i] = 0xAB;
            Span<T> middle = items.Slice(1, count);
            if (zero && count % 2 == 0)
                middle.Clear();
            else
                middle.Fill(value);
            for (int i = 0; i < all.Length; i++)
            {
                int item = i / size;
                byte want = item >= 1 && item <= count ? ByteOf(ref value, i % size) : (byte)0xAB;
                if (all[i] != want)
                    return false;
            }
        }
        return true;
    }

    static byte ByteOf<T>(ref T value, int index) => Unsafe.Add(ref Unsafe.As<T, byte>(ref value), index);

    // docs:native-memory
    // The attributes are for .NET's analyzers; zero mode ignores them.
    [SupportedOSPlatform("linux"), SupportedOSPlatform("windows")]
    [UnsupportedOSPlatform("browser")]
    static bool AllocatesAndFrees(int count)
    {
        unsafe
        {
            // SAFETY: items is freed once, after its last use.
            int* items =
                (int*)NativeMemory.Alloc((nuint)count, sizeof(int));
            for (int i = 0; i < count; i++)
                items[i] = i * 3;
            bool kept = true;
            for (int i = 0; i < count; i++)
                kept &= items[i] == i * 3;
            NativeMemory.Free(items);
            return kept;
        }
    }
    // docs:end

    static bool AllocatesZeroed([ConstantExpected(Min = 1)] int count)
    {
        unsafe
        {
            long* items = (long*)NativeMemory.AllocZeroed((nuint)count, sizeof(long));
            bool zero = true;
            for (int i = 0; i < count; i++)
                zero &= items[i] == 0;
            NativeMemory.Free(items);
            byte* bytes = (byte*)NativeMemory.AllocZeroed((nuint)count);
            for (int i = 0; i < count; i++)
                zero &= bytes[i] == 0;
            NativeMemory.Free(bytes);
            return zero;
        }
    }

    static bool AllocatesNothing()
    {
        unsafe
        {
            void* empty = NativeMemory.Alloc(0);
            NativeMemory.Free(empty);
            NativeMemory.Free(null);
            return empty != null;
        }
    }

    // A zero element count or size is a zero-byte block: still a valid pointer.
    static bool AllocatesNoElements()
    {
        unsafe
        {
            void* noElements = NativeMemory.Alloc(0, 8);
            void* noSize = NativeMemory.AllocZeroed(8, 0);
            bool valid = noElements != null && noSize != null;
            NativeMemory.Free(noElements);
            NativeMemory.Free(noSize);
            return valid;
        }
    }

    // AllocZeroed must zero memory that a freed block left dirty, not only
    // fresh pages from the system.
    static bool ZeroesReusedMemory(int count)
    {
        unsafe
        {
            bool zero = true;
            for (int round = 0; round < 4; round++)
            {
                byte* dirty = (byte*)NativeMemory.Alloc((nuint)count);
                for (int i = 0; i < count; i++)
                    dirty[i] = 0xAB;
                NativeMemory.Free(dirty);
                byte* clean = (byte*)NativeMemory.AllocZeroed((nuint)count, 1);
                for (int i = 0; i < count; i++)
                    zero &= clean[i] == 0;
                NativeMemory.Free(clean);
            }
            return zero;
        }
    }

    // Blocks alive at once don't overlap, and freeing one leaves the other.
    static bool KeepsBlocksApart(int count)
    {
        unsafe
        {
            int* first = (int*)NativeMemory.Alloc((nuint)count, sizeof(int));
            int* second = (int*)NativeMemory.AllocZeroed((nuint)count, sizeof(int));
            for (int i = 0; i < count; i++)
            {
                first[i] = i;
                second[i] = -i - 1;
            }
            bool apart = first != second;
            for (int i = 0; i < count; i++)
                apart &= first[i] == i;
            NativeMemory.Free(second);
            for (int i = 0; i < count; i++)
                apart &= first[i] == i;
            NativeMemory.Free(first);
            return apart;
        }
    }

    // Nothing reads these attributes, but code shared with .NET puts them on
    // every kind of member they allow, and may read their properties.
    [SupportedOSPlatform("macos")]
    sealed class PlatformOnly
    {
        [UnsupportedOSPlatform("browser")]
        public int Value { get; set; }

        [SupportedOSPlatform("linux"), UnsupportedOSPlatform("windows")]
        public static int Twice([ConstantExpected(Min = 0, Max = 8)] int shift) => 2 << shift >> 1;
    }

    static bool AttributesKeepTheirValues()
    {
        var supported = new SupportedOSPlatformAttribute("linux");
        var unsupported = new UnsupportedOSPlatformAttribute("browser");
        var expected = new ConstantExpectedAttribute { Min = 1, Max = 16 };
        return supported.PlatformName == "linux" && unsupported.PlatformName == "browser"
            && expected.Min is int min && min == 1 && expected.Max is int max && max == 16
            && PlatformOnly.Twice(Opaque(3)) == 8 && new PlatformOnly { Value = 5 }.Value == 5;
    }

    public static int Main()
    {
        Check("Sqrt", Math.Sqrt(Opaque(2.0)) * Math.Sqrt(Opaque(2.0)) > 1.9999999999 && Math.Sqrt(Opaque(144.0)) == 12);
        Check("Abs", Math.Abs(Opaque(-3)) == 3 && Math.Abs(Opaque(-2.5)) == 2.5 && Math.Abs(-7L) == 7);
        Check("Min, Max", Math.Min(Opaque(3), 5) == 3 && Math.Max(Opaque(3), 5) == 5 && Math.Max(Opaque(-1.5), -2.5) == -1.5);
        double nan = Opaque(0.0) / Opaque(0.0);
        Check("Min and Max of NaN are NaN", Math.Min(nan, 1) != Math.Min(nan, 1) && Math.Max(1, nan) != Math.Max(1, nan));
        Check("Clamp", Math.Clamp(Opaque(15), 0, 10) == 10 && Math.Clamp(Opaque(-5), 0, 10) == 0 && Math.Clamp(Opaque(0.5), 0, 1) == 0.5);
        Check("Floor, Ceiling", Math.Floor(Opaque(-1.5)) == -2 && Math.Ceiling(Opaque(-1.5)) == -1 && Math.Floor(Opaque(2.9)) == 2);
        Check("Truncate", Math.Truncate(Opaque(-1.9)) == -1 && Math.Truncate(Opaque(1.9)) == 1);
        Check("Round: halves go to even", Math.Round(Opaque(2.5)) == 2 && Math.Round(Opaque(3.5)) == 4 && Math.Round(Opaque(-0.4)) == 0);
        Check("Pow, Exp, Log", Math.Pow(Opaque(2.0), 10) == 1024 && Near(Math.Log(Math.Exp(Opaque(3.0))), 3) && Near(Math.Log2(Opaque(8.0)), 3) && Near(Math.Log10(Opaque(1000.0)), 3));
        Check("Sin, Cos, Tan, Atan2", Near(Math.Sin(Opaque(Math.PI / 2)), 1) && Near(Math.Cos(Opaque(Math.PI)), -1) && Near(Math.Tan(Opaque(Math.PI / 4)), 1) && Near(Math.Atan2(Opaque(1.0), 1), Math.PI / 4));
        Section("Math");

        Check("Sqrt, Abs", MathF.Sqrt(Opaque(81f)) == 9 && MathF.Abs(Opaque(-1.5f)) == 1.5f);
        Check("Floor, Ceiling, Truncate, Round", MathF.Floor(Opaque(1.5f)) == 1 && MathF.Ceiling(Opaque(1.5f)) == 2 && MathF.Truncate(Opaque(-1.5f)) == -1 && MathF.Round(Opaque(2.5f)) == 2);
        Check("Min, Max", MathF.Min(Opaque(1f), 2f) == 1 && MathF.Max(Opaque(1f), 2f) == 2);
        Check("Pow, Exp, Log", MathF.Pow(Opaque(3f), 2) == 9 && Near(MathF.Log(MathF.Exp(Opaque(2f))), 2));
        Check("Sin, Cos, Tanh", Near(MathF.Sin(Opaque(0f)), 0) && Near(MathF.Cos(Opaque(0f)), 1) && Near(MathF.Tanh(Opaque(0f)), 0));
        Section("MathF");

        double zero = Opaque(0.0);
        float zeroF = Opaque(0f);
        Check("NaN", double.IsNaN(zero / zero) && double.IsNaN(double.NaN) && !double.IsNaN(Opaque(1.0)) && float.IsNaN(zeroF / zeroF) && float.IsNaN(float.NaN));
        Check("infinities", double.IsPositiveInfinity(1 / zero) && double.IsNegativeInfinity(-1 / zero) && double.IsInfinity(Opaque(double.NegativeInfinity)) && float.IsPositiveInfinity(1 / zeroF) && float.IsInfinity(Opaque(float.NegativeInfinity)));
        Check("IsFinite", double.IsFinite(Opaque(double.MaxValue)) && !double.IsFinite(Opaque(double.NaN)) && !double.IsFinite(1 / zero) && float.IsFinite(Opaque(float.MinValue)) && !float.IsFinite(1 / zeroF));
        // The outer Opaque stores each result as a double or float: on DOS the
        // 387 computes with a wider exponent, so MaxValue * 2 only overflows,
        // and Epsilon / 2 only rounds to 0, once stored.
        Check("MaxValue overflows to infinity", double.IsPositiveInfinity(Opaque(Opaque(double.MaxValue) * 2)) && float.IsPositiveInfinity(Opaque(Opaque(float.MaxValue) * 2)));
        Check("Epsilon is the smallest subnormal", Opaque(Opaque(double.Epsilon) / 2) == 0 && double.IsSubnormal(Opaque(double.Epsilon)) && Opaque(Opaque(float.Epsilon) / 2) == 0 && float.IsSubnormal(Opaque(float.Epsilon)));
        Check("IsNormal, IsSubnormal", double.IsNormal(Opaque(1.0)) && !double.IsNormal(zero) && !double.IsSubnormal(zero) && float.IsNormal(Opaque(1f)) && !float.IsNormal(Opaque(float.Epsilon)));
        Check("IsNegative sees -0", double.IsNegative(Opaque(double.NegativeZero)) && !double.IsNegative(zero) && float.IsNegative(Opaque(float.NegativeZero)) && float.IsNegative(Opaque(-1f)));
        Check("Pi, E, Tau", double.Pi == Math.PI && double.E == Math.E && double.Tau == 2 * Math.PI && float.Pi == MathF.PI && float.Tau == 2 * float.Pi);
        Section("float and double");

        int counter = Opaque(10);
        Check("Increment returns the new value", Interlocked.Increment(ref counter) == 11 && counter == 11);
        Check("Decrement", Interlocked.Decrement(ref counter) == 10 && counter == 10);
        Check("Add", Interlocked.Add(ref counter, 32) == 42 && counter == 42);
        Check("Exchange returns the old value", Interlocked.Exchange(ref counter, 7) == 42 && counter == 7);
        Check("CompareExchange stores on a match", Interlocked.CompareExchange(ref counter, 8, 7) == 7 && counter == 8);
        Check("CompareExchange leaves a mismatch alone", Interlocked.CompareExchange(ref counter, 99, 7) == 8 && counter == 8);
        long big = 1L << 40;
        Check("64-bit Add and Increment", Interlocked.Add(ref big, 1L << 40) == 1L << 41 && Interlocked.Increment(ref big) == (1L << 41) + 1);
        Check("64-bit Exchange and CompareExchange", Interlocked.Exchange(ref big, -1) == (1L << 41) + 1 && Interlocked.CompareExchange(ref big, 5, -1) == -1 && big == 5);
        nint pointer = 100;
        Check("pointer-sized", Interlocked.CompareExchange(ref pointer, 200, 100) == 100 && Interlocked.Exchange(ref pointer, 300) == 200 && pointer == 300);
        Interlocked.MemoryBarrier();
        Section("Interlocked");

        int flag = 0;
        long wide = 0;
        bool done = false;
        nint address = 0;
        Volatile.Write(ref flag, 5);
        Volatile.Write(ref wide, 1L << 50);
        Volatile.Write(ref done, true);
        Volatile.Write(ref address, 77);
        Check("int", Volatile.Read(ref flag) == 5);
        Check("long", Volatile.Read(ref wide) == 1L << 50);
        Check("bool", Volatile.Read(ref done));
        Check("pointer-sized", Volatile.Read(ref address) == 77);
        Section("Volatile");

        // The runtime remembers a few classes per call site. Six classes here
        // go past that, so some of these calls take the cached path and some
        // the full search; the answers have to agree either way, however the
        // classes are ordered and repeated.
        IShape[] shapes =
        {
            (IShape)Opaque(new Triangle()), (IShape)Opaque(new Square()), (IShape)Opaque(new Pentagon()),
            (IShape)Opaque(new Hexagon()), (IShape)Opaque(new Heptagon()), (IShape)Opaque(new Octagon()),
        };
        Check("six classes through one call site", TotalSides(shapes) == 33);
        Check("again, now that the site has a cache", TotalSides(shapes) == 33);
        IShape[] reversed = new IShape[12];
        for (int i = 0; i < 6; i++)
        {
            reversed[i] = shapes[5 - i];
            reversed[6 + i] = shapes[5 - i];
        }
        Check("reversed and repeated", TotalSides(reversed) == 66);
        long many = 0;
        for (int round = 0; round < 10000; round++)
            many += TotalSides(shapes);
        Check("ten thousand rounds", many == 330000);
        Section("interface calls to many classes");

        // docs:extension-calls
        Vec v = Vec.Of(Opaque(3), Opaque(4));
        Check("extension method", v.Dot(Vec.Of(1, 1)) == 7);
        Check("extension property", v.LengthSquared == 25);
        Check("extension method in a block", v.Scale(2).LengthSquared == 100);
        Check("static extension members", Vec.Zero.IsZero && !v.IsZero);
        v.Translate(-3, -4);
        Check("by-reference extension", v.IsZero);
        // docs:end
        Section("extension members");

        // docs:union-calls
        Check("union holding the first case", ParseNumber(Opaque("123")) is Parsed { Value: 123 });
        Check("union holding the second case", ParseNumber(Opaque("12x")) is BadChar { Position: 2 });
        Check("switch on a union", ValueOrMinusOne(Opaque("42")) == 42 && ValueOrMinusOne(Opaque("4.2")) == -1);
        // docs:end
        Section("unions");

        // docs:caller-info
        // The compiler fills in caller-info parameters with constants.
        string member = CallerMember();
        int first = CallerLine();
        int second = CallerLine();
        Check("CallerMemberName", member == "Main");
        Check("CallerLineNumber", first > 0 && second == first + 1);
        Check("CallerFilePath", CallerFile().EndsWith("Core.cs"));
        Check("CallerArgumentExpression", ArgumentText(first + 1) == "first + 1");
        // docs:end
        // This file doesn't define DEBUG, so the call and its arguments are gone.
        int evaluated = 0;
        System.Diagnostics.Debug.Assert(++evaluated < 0);
        Check("Debug.Assert without DEBUG is removed", evaluated == 0);
        Section("caller info");

        // docs:index-range
        int[] numbers = { 10, 20, 30, 40, 50 };
        Span<int> span = numbers;
        Check("^1 is the last element", span[^1] == 50 && span[^5] == 10);
        Check("a span's range is a Slice", span[1..^1].Length == 3);
        Check("open ends", span[..2][1] == 20 && span[3..][0] == 40);
        int[] copy = numbers[1..3];       // a new array
        copy[0] = 0;
        Check("an array's range copies", copy.Length == 2 && numbers[1] == 20);
        string text = Opaque("hello world");
        Check("a string's range copies", text[6..] == "world");
        Check("a span of a string doesn't",
            text.AsSpan()[..5].SequenceEqual("hello"));
        Index last = ^1;
        Range middle = 1..^1;
        Check("Index and Range print as written",
            last.ToString() == "^1" && middle.ToString() == "1..^1");
        Check("GetOffsetAndLength", middle.GetOffsetAndLength(5) == (1, 3));
        // docs:end
        Section("indices and ranges");

        // docs:default-comparers
        // The comparer for Point calls Point.Equals(Point): no boxing.
        var points = EqualityComparer<Point>.Default;
        long before = GC.GetTotalAllocatedBytes();
        bool same = points.Equals(new Point(1, 2), new Point(1, 2));
        bool different = points.Equals(new Point(1, 2), new Point(2, 1));
        Check("IEquatable<T> struct, compared without allocating",
            same && !different && GC.GetTotalAllocatedBytes() == before);
        // docs:end
        var signedEquality = EqualityComparer<Signed>.Default;
        before = GC.GetTotalAllocatedBytes();
        Check("enum equality without allocating",
            signedEquality.Equals(Signed.Negative, (Signed)Opaque(-1))
            && GC.GetTotalAllocatedBytes() == before);
        Check("Comparer<int>.Default",
            Comparer<int>.Default.Compare(Opaque(1), 2) < 0);
        Check("unsigned enum order",
            Comparer<Unsigned>.Default.Compare(Unsigned.High, Unsigned.Low) > 0);
        Check("signed enum order",
            Comparer<Signed>.Default.Compare(Signed.Negative, Signed.Positive) < 0);
        Check("Nullable: null first",
            Comparer<int?>.Default.Compare(null, 0) < 0
            && EqualityComparer<int?>.Default.Equals(3, Opaque(3)));
        Check("strings compare by code unit",
            Comparer<string>.Default.Compare("B", Opaque("a")) < 0
            && "abc".CompareTo(Opaque("abd")) < 0);
        Check("NaN goes first",
            Comparer<double>.Default.Compare(double.NaN, double.NegativeInfinity) < 0);
        Check("Comparer.Create",
            Comparer<int>.Create((a, b) => b - a).Compare(Opaque(1), 2) > 0);
        Section("comparers");

        var builder = new HashCode();
        builder.Add(1);
        builder.Add(2);
        Check("Combine is Add then ToHashCode",
            HashCode.Combine(1, 2) == builder.ToHashCode());
        builder.Add(3);
        builder.Add(4);
        Check("four or more values, likewise",
            HashCode.Combine(1, 2, 3, 4) == builder.ToHashCode());
        Check("order matters", HashCode.Combine(1, 2) != HashCode.Combine(2, 1));
        Check("null hashes as zero", HashCode.Combine<string>(null) == HashCode.Combine(0));
        before = GC.GetTotalAllocatedBytes();
        int pointHash = HashCode.Combine(new Point(1, 2), Opaque(3));
        Check("a struct's hash code without allocating",
            pointHash != 0 && GC.GetTotalAllocatedBytes() == before);
        Section("HashCode");

        var clicks = new Counter();
        clicks.Value++;
        ref int count = ref clicks.Value;
        count += 2;
        Check("a ref to a struct's own field", clicks.Value == 3);
        Section("UnscopedRef");

        Check("CopyTo, overlapping either way", CopiesLikeALoop());
        Check("Fill and Clear, bytes", FillsLikeALoop<byte>(0x5A) && FillsLikeALoop<byte>(0));
        Check("Fill and Clear, short and int", FillsLikeALoop<short>(-2) && FillsLikeALoop(0x01020304) && FillsLikeALoop(0));
        Check("Fill and Clear, 3-byte struct", FillsLikeALoop(new Three { A = 1, B = 2, C = 3 }) && FillsLikeALoop(new Three()));
        Check("Fill and Clear, long and 24-byte struct",
            FillsLikeALoop(-1L) && FillsLikeALoop(new Big { A = 1, B = 0, C = 3 }) && FillsLikeALoop(new Big()));
        Span<long> sevens = stackalloc long[300];
        sevens.Fill(7);
        bool allSeven = true;
        foreach (long item in sevens)
            allSeven &= item == 7;
        sevens.Slice(10, 280).Clear();
        Check("big Fill and Clear", allSeven && sevens[9] == 7 && sevens[10] == 0 && sevens[289] == 0 && sevens[290] == 7);
        Section("span copy and fill");

        Check("Alloc, then Free", AllocatesAndFrees(Opaque(1000)));
        Check("AllocZeroed", AllocatesZeroed(Opaque(256))); // 2 KB of longs: the Uno R4 has 32 KB of RAM
        Check("Alloc(0) is a valid pointer, Free(null) does nothing", AllocatesNothing());
        Check("Alloc(0, n) and AllocZeroed(n, 0) are valid pointers", AllocatesNoElements());
        Check("AllocZeroed zeroes a freed block's memory", ZeroesReusedMemory(Opaque(4096)));
        Check("blocks alive at once are apart", KeepsBlocksApart(Opaque(256)));
        Check("platform and ConstantExpected attributes", AttributesKeepTheirValues());
        Section("NativeMemory");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
            Console.WriteLine($"{s_failures} checks FAILED.");
        return s_failures;
    }
}
