---
title: Build a native library
index: 15
---

# Build a native library

A zero project can build a native shared library instead of a program: a `.so` on Linux, a `.dylib`
on macOS, a `.dll` on Windows. Other programs call its functions the way they'd call a library
written in C. That means a .NET application through `[DllImport]`, or a C program. The library has no
.NET runtime inside it: it loads instantly, and it's small.

This guide uses the .NET SDK packages ([Install with dotnet](../get-started/install-with-dotnet.html)).
Its code comes from the SDK's tests, which build the library for every target and call it from .NET and
from C.

## Make the library

Start from a library project and add one property, `NativeLib`, with NativeAOT's name for it:

```bash
dotnet new zerolib -o MathLib
```

```xml
<Project Sdk="ZeroCSharp.Sdk/0.16.0">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <NativeLib>Shared</NativeLib>
  </PropertyGroup>
</Project>
```

Without `NativeLib`, a library is a [zero library](create-a-library.html): IL that other zero
projects compile in, packed for NuGet. With it, the build makes native code, and the project isn't
packable.

## Export functions

A library exports each `static` method marked `[UnmanagedCallersOnly]` with an `EntryPoint`, under
that name:

<!-- sample: sdk/test/fixtures/nativelib/Exports.cs exports -->
```csharp
public static class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "nl_add")]
    public static int Add(int a, int b) => a + b;

    [UnmanagedCallersOnly(EntryPoint = "nl_mul_long")]
    public static long MulLong(long a, long b) => a * b;

    [UnmanagedCallersOnly(EntryPoint = "nl_scale")]
    public static double Scale(double x, double f) => x * f;

    [UnmanagedCallersOnly(EntryPoint = "nl_sum")]
    public static unsafe long Sum(int* values, int count)
    {
        long sum = 0;
        for (int i = 0; i < count; i++)
        {
            // SAFETY: the caller passes count readable ints.
            unsafe { sum += values[i]; }
        }
        return sum;
    }

    [UnmanagedCallersOnly(EntryPoint = "nl_offset")]
    public static Point Offset(Point p, int d)
        => new Point { X = p.X + d, Y = p.Y + d };
```

Nothing else is exported:
- a method without an `EntryPoint`;
- the zero libraries the project references, even if they have `[UnmanagedCallersOnly]` methods of
  their own (put the exports in the project itself);
- the runtime's own functions.

Only C-compatible types cross the boundary: numbers, pointers, and structs made of those. The
`Point` above is one:

<!-- sample: sdk/test/fixtures/nativelib/Exports.cs point -->
```csharp
public struct Point
{
    public int X;
    public int Y;
}
```

Strings, arrays, objects and `bool` don't cross. Pass text and arrays as a pointer and a length, and
a flag as an `int`.

Static fields and the heap work as in a program. The library sets up its static fields when it's
loaded, and objects made with `new` stay alive between calls:

<!-- sample: sdk/test/fixtures/nativelib/Exports.cs remember -->
```csharp
[UnmanagedCallersOnly(EntryPoint = "nl_remember")]
public static int Remember(int value)
{
    int[] old = History.Values ?? new int[0];
    int[] next = new int[old.Length + 1];
    for (int i = 0; i < old.Length; i++)
        next[i] = old[i];
    next[old.Length] = value;
    History.Values = next;
    return next.Length;
}
```

## Build it

```bash
dotnet build                               # bin/Debug/zero03/native/MathLib.so (or .dylib, .dll)
dotnet build -p:ZeroTarget=win-x64         # bin/Debug/zero03/win-x64/native/MathLib.dll
dotnet build -p:ZeroTarget=osx-arm64       # bin/Debug/zero03/osx-arm64/native/MathLib.dylib
dotnet publish                             # MathLib.so in the publish folder
```

The library goes in a `native/` folder, as NativeAOT puts it. On Windows that keeps it apart from
the IL `MathLib.dll` the compiler makes beside it. Every build machine builds for every target, as for
programs. `dotnet run` refuses a library.

## Call it from .NET

The library's build also writes its .NET bindings, `bin/MathLib.Interop.cs` (and the same in F#,
below). That's C# with a `[LibraryImport]` for each export, under its method's name, and a copy of
each struct and enum the exports use, with the same fields and layout. A .NET program compiles that file in and calls the library like
any class:

<!-- sample: sdk/test/fixtures/nativelib-app/App/Program.cs call -->
```csharp
public static void Main()
{
    Console.WriteLine($"add {MathLib.Add(2, 3)}");
    Point moved = MathLib.Offset(new Point { X = 1, Y = 12 }, 10);
    Console.WriteLine($"offset {moved.X},{moved.Y}");
}
```

The program's project file references the library and compiles the bindings:

```xml
<PropertyGroup>
  <!-- [LibraryImport] needs it. -->
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="../MathLib/MathLib.csproj"
                    ReferenceOutputAssembly="false"
                    SkipGetTargetFrameworkProperties="true"
                    Targets="ZeroNativeLibraryFiles"
                    OutputItemType="Content" />
  <Compile Include="../MathLib/bin/MathLib.Interop.cs" />
</ItemGroup>
```

The reference builds the library first and copies it next to the program, and `dotnet publish`
copies it into the publish folder too. .NET then finds it by name. The metadata does this:

- `ReferenceOutputAssembly="false"` keeps the library's IL out of the program.
- `SkipGetTargetFrameworkProperties="true"` lets a `net10.0` project reference a `zero03` one.
- `Targets="ZeroNativeLibraryFiles"` and `OutputItemType="Content"` bring back the native library as
  a file to copy.

Visual Studio builds the library itself and asks the reference only for its output file; the library
answers with its native file there, so the copy works the same.

`[LibraryImport]` has .NET's source generator write each call when the program compiles, which
NativeAOT and trimming prefer. The bindings use it on .NET 7 and later, and `[DllImport]` before
that. To have `[DllImport]` anyway, define `ZERO_INTEROP_DLLIMPORT` in the program
(`<DefineConstants>$(DefineConstants);ZERO_INTEROP_DLLIMPORT</DefineConstants>`); the program can then
leave out `AllowUnsafeBlocks` when none of the library's exports take pointers.

A zero project can't reference a native library this way: a reference that would compile its IL in is
an error.

The class takes the library's name: `MathLib` here, or `My.Native` as class `Native` in namespace
`My`. Set `ZeroInteropClass` in the library's project to name it yourself (`Shapes.Native`). Two
exports with the same name and parameters, from two classes, take their entry points' names instead.
The bindings are written only when they change, so an unchanged library doesn't rebuild the program,
and `ZeroInterop=false` turns them off.

The bindings copy structs with sequential or explicit layout, enums, fixed-size buffers and structs
that point at themselves. They declare exports that take function pointers (`delegate* unmanaged`)
too. An export that uses a struct from another assembly, such as one from `lib/`, is left out with
warning `ZERO008`: the library still builds and exports it, and you declare that one yourself.

### Call it from F#

The build writes the same bindings in F#, `bin/MathLib.Interop.fs`: a module of `extern` functions,
and the structs and enums as `[<Struct>]` types with `val mutable` fields. An F# program references
the library the same way and compiles the file before its own:

```xml
<ItemGroup>
  <ProjectReference Include="../MathLib/MathLib.csproj"
                    ReferenceOutputAssembly="false"
                    SkipGetTargetFrameworkProperties="true"
                    Targets="ZeroNativeLibraryFiles"
                    OutputItemType="Content" />
</ItemGroup>
<ItemGroup>
  <!-- Before the files that use it. -->
  <Compile Include="../MathLib/bin/MathLib.Interop.fs" />
  <Compile Include="Program.fs" />
</ItemGroup>
```

<!-- sample: sdk/test/fixtures/nativelib-fsapp/App/Program.fs fsharp-call -->
```fsharp no-check reason="uses the generated bindings: the SDK tests build and run it"
let mutable point = Point()
point.X <- 1
point.Y <- 12
let moved = MathLib.Offset(point, 10)
printfn "add %d" (MathLib.Add(2, 3))
printfn "offset %d,%d" moved.X moved.Y
```

F# has no fixed-size buffers or function-pointer types, so the F# bindings differ in two places:

- A fixed-size buffer is a struct of the buffer's size, `Name_BytesBuffer`, whose first element is
  `Element0`. Take its address for a pointer to the elements.
- A `delegate* unmanaged` parameter is a `nativeint`. Pass `Marshal.GetFunctionPointerForDelegate`'s
  result, and keep the delegate alive while the library may call it.

Pointers are `nativeptr<_>` in fields and `T*` in the `extern` declarations, as F# writes them.

An F# library gets both files the same way, made from its IL.

### Declare the exports yourself

Without the reference (a library built elsewhere, or loaded from a path), declare the exports with
`[DllImport]`, with the same signatures:

<!-- sample: sdk/test/fixtures/nativelib-caller/Program.cs imports -->
```csharp
[StructLayout(LayoutKind.Sequential)]
public struct Point
{
    public int X;
    public int Y;
}

public static unsafe class Native
{
    private const string Lib = "nativelib";

    [DllImport(Lib)] public static extern int nl_add(int a, int b);
    [DllImport(Lib)] public static extern long nl_mul_long(long a, long b);
    [DllImport(Lib)] public static extern double nl_scale(double x, double f);
    [DllImport(Lib)] public static extern long nl_sum(int* values, int count);
    [DllImport(Lib)] public static extern Point nl_offset(Point p, int d);
    [DllImport(Lib)] public static extern void nl_negate(Point* p);
    [DllImport(Lib)] public static extern int nl_remember(int value);
    [DllImport(Lib)] public static extern int nl_cctor_value();
    [DllImport(Lib)] public static extern int nl_slow_value();
    [DllImport(Lib)] public static extern int nl_slow_runs();
}
```

.NET looks for the library by name next to the application: `nativelib.so` or `libnativelib.so`
(`.dylib` on macOS, `.dll` on Windows). To load it from elsewhere, give .NET its path with
`NativeLibrary.SetDllImportResolver`.

## Call it from C

Write the prototypes yourself; the types map like this:

| C# | C |
|---|---|
| `int`, `uint` | `int32_t`, `uint32_t` |
| `long`, `ulong` | `int64_t`, `uint64_t` |
| `short`, `byte` | `int16_t`, `uint8_t` |
| `double`, `float` | `double`, `float` |
| `nint` | `intptr_t` |
| `T*` | `T*` |
| a struct of these | a struct with the same fields in the same order |
| `bool` | don't: use `int` |

Load the library at run time:

```c
#include <dlfcn.h>      /* Windows: LoadLibraryA and GetProcAddress */
#include <stdint.h>
#include <stdio.h>

int main(void)
{
    void *library = dlopen("./MathLib.so", RTLD_NOW);
    int32_t (*add)(int32_t, int32_t) = (int32_t (*)(int32_t, int32_t))dlsym(library, "nl_add");
    printf("%d\n", add(2, 3));
    return 0;
}
```

Or link against it. The library names itself `MathLib.so` (on macOS, `@rpath/MathLib.dylib`), so
give the program an rpath to its folder:

```bash
cc main.c path/to/MathLib.so -Wl,-rpath,path/to         # Linux
cc main.c path/to/MathLib.dylib -Wl,-rpath,path/to      # macOS
```

## Threads

A host program may call the library from several threads at once. Static constructors are safe
there: the first thread to reach a type runs its constructor, and the others wait until it has
finished. Waiting threads give up the processor while they wait, but they still wait, so keep static
constructors short. Your own static fields, arenas and collections are as thread-safe as the code that
uses them, as in a program.

## Limits

These come from zero mode itself:

- **A panic ends the host program.** A failed check calls `abort`, which ends the whole process, not
  just the call. Return errors as values.
- **Memory from `new` is never freed.** A host that keeps calling should have each call do its work
  in an [arena](../concepts/memory.html) and reset it.
- **Each library has its own runtime.** Two zero libraries in one process, or a zero library and a
  zero program, keep apart: their objects can't be passed between them.
- **No unloading.** `dlclose` or `FreeLibrary` doesn't give back the memory the library allocated.
- **No `[ModuleInitializer]`.** It doesn't run in zero mode, in libraries or programs.

WebAssembly, DOS and UEFI have no shared libraries of this kind: `NativeLib` works for `linux-x64`,
`linux-arm64`, `win-x64` and `osx-arm64`, and the build stops with an error for `uefi-x64` and
`uefi-arm64`.
