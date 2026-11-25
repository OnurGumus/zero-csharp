---
title: Limits
index: 6
---

# Limits

Zero mode has no garbage collector, no exceptions you can catch, no async/await, no .NET LINQ, and almost no base class
library. A good deal of ordinary C# still works, because zerocore provides the parts the compiler
needs. This page lists both sides and the known issues, so you can tell early whether a design fits.

## What works

With zerocore, as `build.sh` builds it:

- **Classes, structs and generics**, virtual methods, and interfaces, including default interface
  methods. Every `new` calls `calloc` and is never freed (see [Memory without a GC](memory.html)).
- **Static abstract interface members** and constrained generic calls.
- **Casts**: `(T)x`, `is`, `as` and type patterns, to classes and interfaces. **Boxing**: a struct
  converted to `object` or to an interface it implements.
- **Arrays**: `new T[n]`, including arrays of strings, objects and interfaces, array initializers, and
  `Span<T>`.
- **Static fields of every type**, including strings, objects and arrays, and static constructors,
  which run once even when several threads reach a type at the same time.
- **Strings**: `==`, `+` (with numbers too: `"n=" + 5`), `switch`, `ToString()` and `string.Format`
  with .NET's standard format strings (`"X4"`, `"F2"`, `"N0"`, `"{0,8:E2}"`), plus `lib/` for the rest
  (see [Strings without a GC](strings.html)).
- **Records and anonymous types**: value equality, `with`, deconstruction and the generated `ToString`;
  `typeof` and `GetType()` for comparing types. With no reflection, a `Type` has no name, and a class
  without its own `ToString` prints `object`.
- **Delegates and lambdas** (they allocate), `try`/`finally` and `using`.
- **Function pointers, P/Invoke and `[UnmanagedCallersOnly]` callbacks**, and native libraries
  whose `[UnmanagedCallersOnly]` methods .NET and C programs call
  ([Build a native library](../how-to/build-a-native-library.html)).
- **Threads**: [`Parallel.For`](../how-to/run-in-parallel.html) runs a loop on every processor, with
  `Interlocked` and `Volatile` for shared state, and `[ThreadStatic]` fields for per-thread state.
  There is no `Thread` class or `lock`.
- **`Math` and `MathF`**, and `Main(string[] args)` and top-level statements.
- **`System.Numerics`**: `Complex`, `Vector2`, `Vector3`, `Vector4`, `Matrix3x2`, `Matrix4x4`,
  `Quaternion` and `Plane`, with .NET's members, on every target (see
  [below](#system.numerics)).
- **`TensorPrimitives`** (`System.Numerics.Tensors`): sums, dot products, softmax and the rest on
  whole spans of `float`, on NEON, SSE2 or AVX where the target has them (see
  [below](#tensorprimitives)).
- **`Tensor<T>` and `TensorSpan<T>`** (`System.Numerics.Tensors`): n-dimensional arrays and
  matrices with views, broadcasting and the `Tensor` class's math, for `float` (see
  [Work with tensors and matrices](../how-to/use-tensors.html)).
- **`Half`, `BitOperations` and `BitArray`**, with .NET's names and results: half-precision
  numbers, bit counts and scans on the processor's own instructions where it has them, and arrays
  of bits.
- **`BinaryPrimitives`, hex and Base64**: numbers in a given byte order, `Convert.ToHexString`,
  `ToBase64String` and the rest, and `System.Buffers.Text.Base64` for UTF-8.
- **Extension methods**, and C# 14 **extension members**: properties, static members and
  `ref` receivers in `extension(...)` blocks.
- **Indices and ranges**: `span[^1]`, `span[1..^1]`, and on strings and arrays, where a range
  makes a copy.
- **`NativeMemory`**: `Alloc`, `AllocZeroed` and `Free`, for memory you free yourself, and the
  `SupportedOSPlatform`, `UnsupportedOSPlatform` and `ConstantExpected` attributes, so code
  shared with .NET builds compiles (see [below](#nativememory)).
- **`char`'s methods and `System.Text.Encoding`**, with .NET's results for every character:
  `char.IsLetter`, `IsDigit`, `ToUpper`, `GetUnicodeCategory` and the rest (Unicode 16.0), and UTF-8,
  ASCII, Latin-1, UTF-16 and UTF-32 encodings (see [Strings without a GC](strings.html#characters-and-encodings)).
- **Comparers and hashing**: `IComparable<T>`, `IComparer<T>`, `IEqualityComparer<T>`,
  `EqualityComparer<T>.Default` and `Comparer<T>.Default` without boxing, and `HashCode`.
- **Caller information** (`[CallerMemberName]`, `[CallerLineNumber]`, `[CallerFilePath]`,
  `[CallerArgumentExpression]`) and **`Debug.Assert`/`Debug.Fail`** in Debug builds.

Records work as in .NET, including the generated `ToString`:

<!-- sample: samples/Records.cs records -->
```csharp
public record struct Point(int X, int Y);

public record Person(string Name, int Age);

public record Employee(string Name, int Age, string Team) : Person(Name, Age);
```

<!-- sample: samples/Records.cs record-use -->
```csharp
var a = new Point(1, 2);
var b = new Point(Opaque(1), 2);
Point moved = a with { Y = 5 };
Console.WriteLine(a.ToString());     // Point { X = 1, Y = 2 }
Console.WriteLine(moved.ToString()); // Point { X = 1, Y = 5 }
```

Extension members compile to ordinary static methods, so they cost nothing at run time:

<!-- sample: samples/Core.cs extension-members -->
```csharp
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
```

<!-- sample: samples/Core.cs extension-calls -->
```csharp
Vec v = Vec.Of(Opaque(3), Opaque(4));
Check("extension method", v.Dot(Vec.Of(1, 1)) == 7);
Check("extension property", v.LengthSquared == 25);
Check("extension method in a block", v.Scale(2).LengthSquared == 100);
Check("static extension members", Vec.Zero.IsZero && !v.IsZero);
v.Translate(-3, -4);
Check("by-reference extension", v.IsZero);
```

C# 15 **unions** work too. A union holds a value of one of the types it lists, and a `switch` on it
must handle each of them, which makes it the natural way to return an error:

<!-- sample: samples/Core.cs union-declaration -->
```csharp
public struct Parsed { public int Value; }
public struct BadChar { public int Position; }

// A ParseResult holds either a Parsed or a BadChar, and a switch on it must
// handle both.
public union ParseResult(Parsed, BadChar);
```

<!-- sample: samples/Core.cs union-function -->
```csharp
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
```

A union keeps its value in a field of type `object`, so a struct value is boxed: each `Parsed` or
`BadChar` returned above is a heap allocation, about 24 bytes, that is never freed. That is fine for
an error that happens once, but not for a function called in a loop. There, write the union out by
hand: a struct marked `[Union]` that keeps its cases in its own fields, with a constructor and a
`TryGetValue` for each case. The compiler treats it as a union (`return new BadChar { ... }`
converts to it, and a `switch` takes it apart) without allocating anything.
[Build for the Commodore 64](../how-to/build-for-c64.html#a-game) has one: a game's `Outcome`, made 50
times a second.

Casts to interfaces work as in .NET, except that a failed `(T)x` ends the program instead of throwing:

<!-- sample: samples/Casts.cs interface-checks -->
```csharp
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
```

`Half`, `BitOperations` and `BitArray` work as in .NET. A `Half` rounds to the nearest value it can
hold, does its arithmetic in `float`, and prints the shortest text that reads back as the same value:

<!-- sample: samples/Bits.cs half -->
```csharp
Half pi = (Half)Opaque(3.14159f);
Check("float to Half rounds to nearest", (float)pi == 3.140625f);
Check("shortest text that reads back", pi.ToString() == "3.14");
Half product = (Half)Opaque(1.5f) * (Half)2.5f;
Check("arithmetic, rounded to Half", (float)product == 3.75f);
```

`BitOperations` uses the processor's own instructions where the target has them: clz, rbit and cnt
on arm64, bsr and bsf on x64, and lzcnt, tzcnt and popcnt with `-m x86-64-v3`. A `BitArray` packs 32
bits into each `int`; `foreach` over one boxes each `bool`, so index it in a hot loop:

<!-- sample: samples/Bits.cs bitarray -->
```csharp
// A sieve of Eratosthenes: bit i says whether i is prime.
var primes = new BitArray(100, true);
primes[0] = primes[1] = false;
for (int i = 2; i * i < 100; i++)
    if (primes[i])
        for (int j = i * i; j < 100; j += i)
            primes[j] = false;
int count = 0;
foreach (bool isPrime in primes)
    if (isPrime)
        count++;
Check("25 primes below 100", count == 25 && primes[97] && !primes[91]);

var odd = new BitArray(100);
for (int i = 1; i < 100; i += 2)
    odd[i] = true;
var oddPrimes = new BitArray(primes).And(odd);
Check("And", !oddPrimes[2] && oddPrimes[3] && !oddPrimes[9]);
```

### System.Numerics

`Complex`, `Vector2`, `Vector3`, `Vector4`, `Matrix3x2`, `Matrix4x4`, `Quaternion` and `Plane` have
.NET's members and conventions: row vectors (a point is transformed as `v * M`, so `A * B` applies
`A` first), right-handed projections with depth from 0 to 1, and .NET's `ToString` formats.

<!-- sample: samples/Numerics.cs camera -->
```csharp
// A camera at z = 5 looking at the origin sees it 5 units ahead,
// which is -Z in a right-handed view space.
var eye = new Vector3(0, 0, F(5));
var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
Check("CreateLookAt",
    Near(Vector3.Transform(Vector3.Zero, view), 0, 0, -5));

// A 90° field of view, square, drawing from 1 to 100 units away:
// the far plane lands at depth 1.
var projection = Matrix4x4.CreatePerspectiveFieldOfView(
    F(MathF.PI / 2), 1, 1, 100);
var clip = Vector4.Transform(new Vector3(0, 0, -100), projection);
Check("CreatePerspectiveFieldOfView",
    Near(clip.Z / clip.W, 1, 1e-6) && projection.M34 == -1);
```

`Complex` has the arithmetic, `Abs`, `Sqrt`, `Exp`, `Log`, `Pow` and the trigonometric and
hyperbolic functions, with .NET's branch cuts:

<!-- sample: samples/Numerics.cs complex -->
```csharp
var a = new Complex(D(1), D(2));
var b = new Complex(D(3), D(-4));
Check("+ - * /", a + b == new Complex(4, -2)
    && a * b == new Complex(11, 2) && Near(a / b, -0.2, 0.4));
Check("Sqrt of a negative", Complex.Sqrt(-4) == new Complex(0, 2));
// e^(iπ) = -1, up to the rounding of π.
Check("Euler", Near(Complex.Exp(Complex.ImaginaryOne * Math.PI), -1, 0));
Check("ToString", a.ToString() == "<1; 2>");
```

They differ from .NET in three ways:

- **They are plain structs.** .NET hands the vectors to the processor's SIMD registers; zero mode's
  compiler does that only for types marked `[Intrinsic]`, so here each operation is scalar code,
  which works on every target. Use `lib/Kernels.cs` for SIMD loops.
- **`Complex.Tan`, `Tanh` and `Atan` are more accurate.** .NET's formulas cancel near `Tan`'s poles
  (where its results are off in the fourth digit) and for `Atan` of small values; zerocore's don't,
  and agree with a 40-digit reference. Elsewhere the results agree with .NET's to within a few units
  in the last place. At infinity `Atan` gives the limit, (±π/2, 0), where .NET 10 gives NaN.
- **`ToString` ignores the culture**, as everything in zero mode does: `<1, 2.5>`, never `<1; 2,5>`.

`Math` also gained `Asin`, `Acos`, `Atan`, the hyperbolic functions and their inverses, `Cbrt`,
`Log(a, newBase)`, `IEEERemainder`, `Sign` and `CopySign`. They are built on the C math functions
every target already has (`atan2`, `exp`, `log`, `sqrt`, `pow`), to within 3 units in the last place
of .NET's. `scripts/numerics-fuzz` compares all of it with .NET on random values.

### TensorPrimitives

`System.Numerics.Tensors.TensorPrimitives` works on whole spans of `float`: element-wise arithmetic
(`Add`, `Multiply`, `MultiplyAdd`, ...), the math functions (`Exp`, `Sqrt`, `Tanh`, `Sigmoid`, ...),
reductions (`Sum`, `Dot`, `Norm`, `Max`, `IndexOfMax`, ...), `SoftMax`, `CosineSimilarity` and
conversions to and from `Half`. The names, overloads and argument checks are those of .NET's
System.Numerics.Tensors package, for `float`; .NET's generic overloads (`double`, the integers) are
not there.

<!-- sample: samples/Tensors.cs similarity -->
```csharp
// How alike two embeddings are: 1 for the same direction, 0 for
// unrelated, -1 for opposite.
float[] cat = { 0.9f, 0.1f, 0.4f }, kitten = { 0.8f, 0.2f, 0.5f };
float[] car = { -0.1f, 0.9f, -0.3f };
float close = TensorPrimitives.CosineSimilarity(cat, kitten);
float far = TensorPrimitives.CosineSimilarity(cat, car);
Check("CosineSimilarity", close > 0.98f && far < 0);
```

The destination can be an input, which does the operation in place, but it can't overlap one partly:

<!-- sample: samples/Tensors.cs inplace -->
```csharp
// The destination may be an input: this scales x in place.
float[] v = { 1, 2, 3, 4, 5 };
TensorPrimitives.Multiply(v, 10, v);
Check("in place", v[0] == 10 && v[4] == 50);
```

Element-wise arithmetic, the sums and the functions neural networks spend their time in (`Exp`,
`Sigmoid`, `Tanh` and `SoftMax`) work on 4 or 8 values at once: with NEON on arm64, and on x64 with
SSE2, which every x64 processor has, or AVX in a program built for it; WebAssembly, DOS and the
microcontrollers run a scalar loop. Run on 1M floats, the four functions take 1.0–1.5× the time of
.NET's on arm64, and 1.2–1.7× with SSE2 (an x64 build under Rosetta, against .NET's own SSE code).

**On x64, AVX doubles the width.** Zero mode doesn't check the processor at run time, so it uses AVX
only in a program built for it: `-m x86-64-v3` with bflat, or with the SDK

```xml
<ZeroInstructionSet Condition="'$(ZeroTarget)' == 'linux-x64' or '$(ZeroTarget)' == 'win-x64'">x86-64-v3</ZeroInstructionSet>
```

Such a program needs AVX2 and FMA (Haswell, 2013, or newer); see [Use SIMD](../how-to/use-simd.html).
Without it, the SSE2 code runs. arm64 needs nothing: NEON is always there.

- **Arithmetic gives the same bits** as .NET's and the plain loop's: each element is still one IEEE
  operation. Sums can differ in the last digits, since they add in another order, as .NET's do from
  one processor to another.
- **`Exp`, `Sigmoid`, `Tanh` and `SoftMax` are a few ulps from exact**, like .NET's, but exact near
  0, where .NET's `Tanh` is off by 6e-8 (`Tanh(-1e-30)` gives +5.96e-8). A block holding a NaN or a
  value whose result overflows or is subnormal goes to the scalar function, so those come out
  exactly.
- **The other functions** (`Log`, `Sin`, `Sinh`, ...) are `MathF`'s, one element at a time: as
  accurate as the target's C math library, within an ulp of exact with glibc, musl or Apple's, but
  about 3× slower than .NET's vectorized ones, which are up to 4 ulps off (and its `Sinh(-0)` gives
  -5.96e-8).
- **`SoftMax` subtracts the largest value first**, so large scores don't overflow (.NET's gives NaN
  above 88), and corrects for the rounding of that subtraction, so small probabilities keep their
  digits:

<!-- sample: samples/Tensors.cs softmax -->
```csharp
// Scores to probabilities. Large scores are fine: the largest is
// subtracted first, so e^x doesn't overflow.
float[] scores = { 1000, 1001, 1002 };
var p = new float[3];
TensorPrimitives.SoftMax(scores, p);
Check("SoftMax", Near(p[0], 0.09003057f) && Near(p[2], 0.66524096f)
    && Near(TensorPrimitives.Sum(p), 1));
```

`scripts/tensor-fuzz` compares all of it with .NET's package, on spans of every length up to 70.

The rest of .NET's float functions are there too, one element at a time with float's own formulas:
`SinPi`, `CosPi`, `TanPi`, `AcosPi` and the other `Pi` forms, `Exp2`, `Exp10`, `ExpM1`, `LogP1` and
their variants, `DegreesToRadians`, `Ieee754Remainder`, `ILogB`, `RootN`, `Average`, and
`MaxNumber`, `MinNumber` and their magnitude forms, which skip NaNs. `ExpM1`, `Exp2M1` and
`Exp10M1` work in double, so they keep their digits near 0, where `Exp(x) - 1` cancels.

`Tensor<T>`, `TensorSpan<T>` and the `Tensor` class build on these for tensors of any rank: see
[Work with tensors and matrices](../how-to/use-tensors.html).

### Binary formats, hex and Base64

`BinaryPrimitives` reads and writes numbers in a byte span in a given byte order, as file formats
and protocols store them. The span needn't be aligned. Every target is little-endian, so the
`LittleEndian` methods are a plain load or store; the `BigEndian` ones add a byte swap, one
instruction where the processor has it:

<!-- sample: samples/Bits.cs binary-primitives -->
```csharp
// A BMP file starts with "BM" and its size, little-endian.
ReadOnlySpan<byte> header = [0x42, 0x4D, 0x36, 0x00, 0x0C, 0x00];
Check("little-endian",
    BinaryPrimitives.ReadUInt16LittleEndian(header) == 0x4D42
    && BinaryPrimitives.ReadUInt32LittleEndian(header[2..]) == 0x000C0036);
Span<byte> packet = stackalloc byte[8];
BinaryPrimitives.WriteInt64BigEndian(packet, 0x0102030405060708);
Check("big-endian", packet[0] == 1 && packet[7] == 8);
Check("too short: TryRead says so",
    !BinaryPrimitives.TryReadInt32LittleEndian(header[..3], out _));
```

`Convert` has .NET's hex and Base64 methods. The ones that return a string or an array allocate it;
the `Try` ones write into a span you provide and allocate nothing. Text that isn't valid ends the
program where .NET would throw, or makes a `Try` method return false:

<!-- sample: samples/Bits.cs hex-base64 -->
```csharp
byte[] data = { 0xDE, 0xAD, 0xBE, 0xEF };
Check("hex", Convert.ToHexString(data) == "DEADBEEF"
    && Convert.ToHexStringLower(data) == "deadbeef");
Check("from hex",
    Convert.FromHexString("deadBEEF") is [0xDE, 0xAD, 0xBE, 0xEF]);
Check("Base64", Convert.ToBase64String(data) == "3q2+7w==");
// Into a span: nothing allocated; whitespace is skipped.
Span<byte> decoded = stackalloc byte[16];
Check("Base64 into a span",
    Convert.TryFromBase64String("3q2+\n7w==", decoded, out int written)
    && written == 4 && decoded[3] == 0xEF);
```

`System.Buffers.Text.Base64` encodes to and decodes from UTF-8 bytes, a piece at a time if the data
comes in pieces (`isFinalBlock: false`), and says how far it got with an `OperationStatus`.

### Comparers

`EqualityComparer<T>.Default` and `Comparer<T>.Default` work as in .NET, and the compiler picks
each `T`'s comparer when it builds the program, so they need no reflection. A struct that implements
`IEquatable<T>` or `IComparable<T>` is compared by a direct call to its own method, without being
boxed (which in zero mode would allocate memory that is never freed):

<!-- sample: samples/Core.cs equatable-struct -->
```csharp
public readonly struct Point : IEquatable<Point>
{
    public readonly int X, Y;
    public Point(int x, int y) { X = x; Y = y; }
    public bool Equals(Point other) => X == other.X && Y == other.Y;
    public override int GetHashCode() => HashCode.Combine(X, Y);
}
```

<!-- sample: samples/Core.cs default-comparers -->
```csharp
// The comparer for Point calls Point.Equals(Point): no boxing.
var points = EqualityComparer<Point>.Default;
long before = GC.GetTotalAllocatedBytes();
bool same = points.Equals(new Point(1, 2), new Point(1, 2));
bool different = points.Equals(new Point(1, 2), new Point(2, 1));
Check("IEquatable<T> struct, compared without allocating",
    same && !different && GC.GetTotalAllocatedBytes() == before);
```

Every primitive and `string` implements `IComparable<T>`; strings compare by character code (see
[Coming from .NET](../get-started/coming-from-dotnet.html#what-compiles-but-behaves-differently)).
Enums compare by their value, and `Nullable<T>` puts null first. Spans, `List<T>` and `RefList<T>`
sort with these comparers ([Choose a collection](../how-to/choose-a-collection.html#sort-and-iterate)).

### Caller information and Debug.Assert

The caller-info attributes work as in .NET. The compiler passes constants from the call site, so
they cost nothing at run time:

<!-- sample: samples/Core.cs caller-info -->
```csharp
// The compiler fills in caller-info parameters with constants.
string member = CallerMember();
int first = CallerLine();
int second = CallerLine();
Check("CallerMemberName", member == "Main");
Check("CallerLineNumber", first > 0 && second == first + 1);
Check("CallerFilePath", CallerFile().EndsWith("Core.cs"));
Check("CallerArgumentExpression", ArgumentText(first + 1) == "first + 1");
```

`Debug.Assert` and `Debug.Fail` are built on them. Where `DEBUG` is defined (the SDK defines it in
Debug builds), a failed assert ends the program with its condition and where it is:

<!-- sample: samples/Guards.cs debug-assert -->
```csharp
static int DebugAssert(int count)
{
    System.Diagnostics.Debug.Assert(count > 0, "the queue is empty");
    return count - 1;
}
```

```text
Debug.Assert failed: count > 0: the queue is empty
  in DebugAssert at Guards.cs:237
```

Where `DEBUG` isn't defined, as in Release builds, the compiler removes each call along with its
arguments. That is decided where the call is compiled, so the asserts are only in your own code:
`lib/` and zerocore come compiled without `DEBUG`, and their own checks are always on (see
[Memory without a GC](memory.html) for `Arena.DebugChecks`).

### Indices and ranges

`^1` counts from the end and `a..b` is a range, as in .NET. On a `Span<T>` or `ReadOnlySpan<T>` a
range is a `Slice`, with nothing copied. On a string or an array it allocates a copy, which in
zero mode is never freed (see [Memory without a GC](memory.html)), so slice `text.AsSpan()` instead
where you can:

<!-- sample: samples/Core.cs index-range -->
```csharp
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
```

`lib/`'s `List<T>` has no indexer, so `list[^1]` doesn't compile; `list.AsSpan()[^1]` does.

### NativeMemory

`NativeMemory.Alloc`, `AllocZeroed` and `Free` work as in .NET, on the target's `calloc` and `free`,
so they work on every target, the C64 included (where one block can't be larger than 65,520 bytes).
It is the one way to free memory by hand, for code shared with .NET that already does: a buffer
handed to C, or one whose size is known only when the program runs. In new code, prefer an
[arena](memory.html), which frees many blocks at once and catches mistakes with `Arena.DebugChecks`.

<!-- sample: samples/Core.cs native-memory -->
```csharp
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
```

`calloc` zeroes what it returns, so `Alloc` hands out zeroed memory too, unlike .NET's. `Alloc(0)`
returns a valid pointer that `Free` takes, and `Free(null)` does nothing. Where .NET throws
`OutOfMemoryException`, for a size the system can't give or a count times size that overflows, zero
mode ends the program:

```text
NativeMemory: the size overflows
```

Nothing checks a block after it is freed: using it then, or freeing it twice, is the same mistake
it is in C.

`[SupportedOSPlatform]`, `[UnsupportedOSPlatform]` and `[ConstantExpected]` are there for code
shared with .NET builds, whose analyzers read them. In zero mode they compile and do nothing.

## Choosing how to dispatch

Interfaces and virtual methods both work, but they don't cost the same. An interface call the compiler
can't resolve at build time goes through a small cache per call site: a little slower than a virtual
call, and slower again at a site that sees more than four classes.

Generics with static abstract members avoid the question. They are resolved at compile time, one
specialized copy per struct type, with no allocation and no indirect call. That suits hot loops such as
per-format compute kernels:

<!-- sample: samples/VirtualTest.cs static-abstract -->
```csharp
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
```

<!-- sample: samples/VirtualTest.cs generic-call -->
```csharp
static void PrintArea<T>(in T shape) where T : struct, IArea<T>
{
    Console.Write(T.Name);
    Console.WriteLine($" area: {T.Area(in shape)}");
}
```

## What doesn't work

- **Freeing memory.** Nothing allocated with `new`, boxing or `+` is ever freed. Use
  [arenas](memory.html) for anything short-lived, or [`NativeMemory`](#nativememory) for a
  block you free yourself.
- **Catching exceptions.** `throw` is a panic, like Rust's `panic!` built with `panic = "abort"`:
  it prints the exception's message and ends the program. Nothing unwinds, so no `catch` block runs,
  and neither do the `finally` blocks and `using` disposals between the `throw` and `Main`. A failure
  that would throw in .NET, such as a failed cast, an index out of range or an overflow in `checked`
  code, ends the program the same way. A null dereference is a plain segmentation fault. `lib/`
  follows the same rule: when it catches a mistake, it can only end the program with a message.
  Return errors as values instead, for example with a union (see above).
- **Variance.** Casting or dispatching through it (`IEnumerable<string>` as `IEnumerable<object>`,
  `string[]` as `object[]`) aborts with a message saying so, instead of answering wrongly.
- **Most of the base class library.** No .NET collections (use
  [`lib/`'s](../how-to/choose-a-collection.html)), no LINQ (use [`lib/Seq.cs`](../how-to/query-data.html)),
  no `System.IO` (use [`File`](../how-to/read-and-write-files.html)), `DateTime`, `Stopwatch` or
  `Random` (use [`Clock` and `Random`](../how-to/use-the-clock-and-random-numbers.html)), no
  reflection (a `Type` only compares), async, `Thread`, or boxing of `Nullable<T>`.
- **A struct's default `Equals` compares bytes.** A reference field compares by reference, where .NET
  would call its `Equals`. Records and `IEquatable<T>` compare field by field. `EqualityComparer<T>`,
  and so `Dict` and `HashSet` keys, compare such a struct by its bytes even if it overrides
  `Equals(object)`: implement `IEquatable<T>` to have yours used.
- **Reflection.** Builds need `--no-reflection`.
- **Run-time CPU detection.** Instruction sets beyond the baseline are available only when the build
  names them with `-m`.
- **Some platforms.** zerocore runs on Linux (x64, arm64), macOS on Apple Silicon, Windows x64,
  WebAssembly, DOS, and UEFI (x64, arm64). Not on Windows on arm64 or Intel Macs. The repository's
  `build.sh` builds on Linux and macOS; building with it *on* Windows (`build.ps1`) is untested and
  uses bflat's zerolib. The .NET SDK packages
  ([Install with dotnet](../get-started/install-with-dotnet.html)) build on Linux (x64, arm64),
  Windows x64 and Apple Silicon Macs, for Linux x64, Linux arm64, Windows x64, macOS on Apple
  Silicon, and UEFI x64 and arm64; WebAssembly and DOS need the repository. Their macOS programs can
  call macOS's C library only, unless you add another library's `.tbd` or `.dylib` yourself.

`zerocore/README.md` has the full list.

## Known issues and workarounds

These are the issues you may hit with zerocore, as `build.sh` builds:

| Issue | Workaround |
|---|---|
| `[DllImport]` of a library not passed with `-i` fails to compile ("Expected type 'Internal.Runtime.CompilerHelpers.InteropHelpers' not found") | Pass `-i <library>`; `build.sh` passes `-i libc -i libm.so.6` |
| Building without `--no-reflection` fails ("Failed to load type 'Internal.Metadata.NativeFormat.TypeDefinition'") | Pass `--no-reflection`; `build.sh` does |
| Non-baseline instruction sets (AVX2, FMA, dotprod) report `IsSupported == false` | Build with `-m x86-64-v3` or `-m dotprod`; `build.sh` does this for Simd and Kernels |
| No `Thread` | `Parallel.For` (`lib/Threads.cs`) for threads |
| `"n=" + 5`, `ToString()` and `string.Format` allocate a string that's never freed | Fine once; in a loop, `Str.Format(arena, $"n={5}")` |
| Casts and interface calls that need variance abort at run time | Not supported |
| Windows: x64 only, built from Linux or macOS | `./build.sh --win`; building on Windows (`build.ps1`) is untested |
| macOS: Apple Silicon only, built on a Mac | `./build.sh --mac` |

Two of these come from the core library having to be named `System.Private.CoreLib`. bflat assumes a
library with that name is .NET's and looks for its reflection support, hence `--no-reflection`. And
without `-i`, bflat binds a `[DllImport]` function when it is first called, which needs runtime support
zerocore doesn't have; with `-i`, it is bound at link time.

The other targets add limits of their own: no SIMD and no callbacks from native code on WebAssembly,
one thread and a 387 for floating point on DOS, one thread and no C library on UEFI.
[Targets](targets.html) covers them.

## Errors from the compiler

Most of zerocore is a contract with the compiler, which looks types, methods and fields up by name
(`System.Runtime.TypeCast.CheckCastClass`, `String._stringLength`, ...). When one it needs is missing,
the build fails with "Expected type ... not found" or "Failed to load type ...", not with a C# error.
Those names can't be changed. If you see such a message, check the first two rows of the table above
before anything else.
