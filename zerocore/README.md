# zerocore

A core library for C# programs compiled with [bflat](https://flattened.net/) that run without .NET's runtime: no garbage collector, no exceptions you can catch, no reflection. It takes the place of bflat's built-in `zerolib` (`--stdlib:zero`), under the MIT license, in about 2,400 lines of C# and under 100 lines of assembly.

A core library is the assembly that defines `object`, `int`, `string` and the other types C# itself is built on. It also has to contain the runtime code the native compiler expects to call: allocating objects, checking casts, calling interface methods, setting up static fields. With zerocore, that code is all a program gets linked with besides the C library.

## Using it

`../build.sh` does this for the samples. By hand, for Linux:

```bash
# 1. The library. bflat only accepts a custom core library under this name.
bflat build-il $(find zerocore -name '*.cs') -O --stdlib:none --langversion preview \
    -o System.Private.CoreLib.dll

# 2. Its assembly routines, for the target architecture (x64.S or arm64.S).
as zerocore/native/arm64.S -o zerocore-arm64.o

# 3. Your program.
bflat build Program.cs --stdlib:none -r System.Private.CoreLib.dll \
    --no-reflection -i libc --ldflags zerocore-arm64.o -o program
```

- `--stdlib:none` tells bflat not to add a library of its own.
- `--no-reflection` is required. bflat assumes a library named `System.Private.CoreLib` is .NET's and would otherwise look for its reflection support ("Failed to load type 'Internal.Metadata.NativeFormat.TypeDefinition'").
- `-i <library>` is needed for every library you `[DllImport]`, here `libc`. It binds the functions at link time. Without it bflat binds them when first called, which needs runtime support zerocore doesn't have ("Expected type 'Internal.Runtime.CompilerHelpers.InteropHelpers' not found").

For WebAssembly, see `build_wasm` in `../build.sh`: the library is built with `-d WASM`, and `../wasm/runtime.c` replaces the assembly file. DOS (`build_dos_*` there) uses the same build of the library, with `../dos/runtime.c`. UEFI (`build_uefi` there) uses the ordinary build with `--os uefi`, `native/win-x64.S` or `native/win-arm64.S`, and `../efi/` in place of the C library.

## What's in it

| File | What |
|---|---|
| `System/Primitives.cs`, `Object.cs`, `Array.cs`, `Attributes.cs`, `Collections.cs` | The types and attributes the C# compiler requires: primitives, `object`, `ValueType`, `Enum`, `Nullable<T>`, `Array`, delegates (`Action`, `Func`), `ValueTuple` (2 to 7 elements), `IDisposable`, `IEnumerable`. Arrays (through `Array<T>`) are `IEnumerable<T>`, as in .NET. Also attributes code shared with .NET uses and nothing reads here: `SupportedOSPlatform`, `UnsupportedOSPlatform`, `ConstantExpected`. `LibraryImport` and `UnmanagedCallConv`, which the SDK's generator reads (`sdk/ZeroCSharp.LibraryImport`) |
| `Runtime/ObjectAllocator.cs` | Where each thread's objects come from: `calloc` by default, or a function a library sets for that thread (`../lib/Arena.cs`'s `AllocateObjectsHere` sends them to an arena); and a scan of static fields for debug checks |
| `System/Equality.cs` | `IEquatable<T>`, `IComparable<T>`, `IEqualityComparer<T>`, `IComparer<T>`, and `EqualityComparer<T>.Default` and `Comparer<T>.Default`, which the compiler fills in for each T (its `[Intrinsic] Create()`) with the class that fits it: `Generic…`, `Enum…`, `Nullable…`, `Object…` or `StringEqualityComparer`, so structs aren't boxed. Every primitive and `string` (ordinal) implements `IComparable<T>`, and the non-generic `IComparable`, which F# needs for its `comparison` constraint |
| `System/Char.cs`, `System/Globalization/` | `char`'s methods with .NET's results: `IsLetter`, `IsDigit`, `IsWhiteSpace`, `ToUpper`, `GetNumericValue`, `GetUnicodeCategory`, the `IsAscii…` family, surrogates and `ConvertFromUtf32`/`ConvertToUtf32`; `CharUnicodeInfo` and `UnicodeCategory`. Casing is invariant (no cultures). `UnicodeData.cs` holds the tables, generated from .NET 10's data (Unicode 16.0) by `../scripts/gen-unicode`: categories (16 KB), casing (1.6 KB), numeric values (3 KB), each linked only if used |
| `System/Text/Encoding.cs`, `UTF8Encoding.cs`, `UnicodeEncoding.cs`, `UTF32Encoding.cs`, `ASCIIEncoding.cs` | `Encoding.UTF8`, `ASCII`, `Latin1`, `Unicode`, `BigEndianUnicode`, `UTF32` and `Default`, with .NET's members and replacements for invalid input (U+FFFD, `?`, Latin-1's best fit). The span and pointer overloads allocate nothing; those returning arrays or strings allocate them. No `Encoder`, `Decoder` or fallback objects |
| `System/Lazy.cs` | `Lazy<T>` (what F#'s `lazy` makes): thread-safe as .NET's default, the factory runs once and other threads spin until it's done; reading `Value` from its own factory throws, as in .NET (a `[ThreadStatic]` thread id tells the two apart) |
| `System/EventArgs.cs` | `EventArgs`, `EventHandler` and `EventHandler<T>`. Delegates are single-target (no `Delegate.Combine`), so an event needs its own `add` and `remove`, as `../lib/Timers.cs`'s does |
| `System/TimeSpan.cs` | `TimeSpan`: ticks, parts and totals, `From*`, arithmetic and comparisons with .NET's overflow checks, and the constant (`"c"`) text form. No parsing or custom formats |
| `System/HashCode.cs` | `HashCode.Combine` and `Add`/`ToHashCode`, .NET's algorithm with a fixed seed |
| `System/Type.cs`, `System/Number.cs`, `System/Text/StringBuilder.cs` | What records, anonymous types and `"n=" + 5` need: `object`'s `Equals`, `GetHashCode` and `ToString` (overridden by every primitive, `string`, structs and enums), `string.Format` and `StringBuilder.AppendFormat` (.NET's `{0,-8:X2}` holes), `StringBuilder`, and `Type` for `typeof` and `GetType()`, one per type, compared by identity |
| `System/Number.Formatting.cs` | .NET's standard numeric format strings (`D`, `X`, `B`, `F`, `N`, `E`, `G`, `P`, `C`, `R`) for `ToString(format)`, `TryFormat`, `IFormattable` and `ISpanFormattable`, invariant culture only. Binary floating point is printed exactly (Dragon4 on stack bignums), and without a format as the shortest text that reads back, as .NET Core 3.0+ does. `../scripts/format-check` compares it with .NET's over millions of values |
| `System/GC.cs` | `GC.GetTotalAllocatedBytes()`: every byte allocated so far, so a test can check that a loop allocates nothing |
| `System/String.cs` | `string`: `Length`, indexer, `==`, `!=`, `Equals`, `IsNullOrEmpty`, `Concat`, `Substring` (so `a + b` and `switch` on strings work); a string is an `IEnumerable<char>` |
| `System/Span.cs` | `Span<T>` and `ReadOnlySpan<T>`: indexing, `Slice`, `CopyTo`, `Fill`, `Clear`, `foreach`, `fixed` |
| `System/Exceptions.cs` | `Exception` and a few standard subclasses (`InvalidOperationException`, `ArgumentException`, `OverflowException`, …), for `throw` as a panic and for code the compiler generates |
| `System/Unions.cs` | `IUnion` and `UnionAttribute`, which C# 15 `union` declarations need |
| `System/Range.cs` | `Index` and `Range`, for `^1` and `a..b`, and `RuntimeHelpers.GetSubArray` for `array[a..b]` (`string.Substring` is in `String.cs`) |
| `System/Debug.cs` | `Debug.Assert` and `Debug.Fail`, for builds with `DEBUG` defined: print the condition, member, file and line, then `FailFast`. They use the caller-info attributes in `System/Attributes.cs` (`CallerMemberName`, `CallerLineNumber`, `CallerFilePath`, `CallerArgumentExpression`) |
| `System/Environment.cs` | `Environment.FailFast`, which is what every failure ends in; `Exit`, `GetEnvironmentVariable`, `ProcessorCount`, `TickCount64` and `NewLine` (`"\n"`) |
| `Runtime/RuntimeExports.cs` | Allocation (`calloc`, never freed), boxing and unboxing; `throw` (back to `Catch.Run`, or print the message and abort) |
| `Runtime/Catch.cs` | The marks `throw` jumps back to, for lib/'s `Catch.Run`: `ZeroCatchCall` and `ZeroCatchJump`, in assembly next to the fiber switch (`native/`), or with `setjmp` in the C runtimes |
| `Runtime/TypeCast.cs` | `is`, `as` and casts to classes, interfaces and arrays (an array of a different element type matches only by covariance, which ends the program as unsupported); type-checked stores into arrays |
| `Runtime/InterfaceDispatch.cs` | Calling an interface method on an object whose class isn't known at build time, with a small cache per call site |
| `System/Math.cs` | `Math` and `MathF`: the processor's instruction where there is one, the C math library otherwise. The inverse and hyperbolic functions, `Cbrt`, `IEEERemainder` and `Log(a, newBase)` are built on `atan2`, `exp`, `log`, `sqrt` and `pow`, so every target's C library is enough |
| `System/Numerics/Complex.cs`, `Vector2.cs`, `Vector3.cs`, `Vector4.cs`, `Matrix3x2.cs`, `Matrix4x4.cs`, `Quaternion.cs`, `Plane.cs` | `System.Numerics` with .NET's members, as plain structs (no `[Intrinsic]`, so scalar code on every target). `Complex.Tan`, `Tanh` and `Atan` use formulas that don't cancel where .NET's do. `../scripts/numerics-fuzz` compares all of it with .NET |
| `System/Decimal.cs` | `decimal`, with .NET's layout and results: arithmetic, comparison, rounding (`Round`, `Floor`, `Ceiling`, `Truncate`), conversions, `Parse` and `ToString`, checked against .NET's by fuzzing (`scripts/decimal-fuzz/fuzz-decimal.sh`). No allocation; 32-bit targets need no 128-bit arithmetic. `Parse` also takes an exponent (`1.5e3`), which .NET's default refuses |
| `System/Half.cs` | `Half` (IEEE half precision, as in .NET: conversions round to nearest even, `ToString` gives the shortest text that reads back) and `BitConverter`'s bit conversions for `Half`, `float` and `double` |
| `System/Buffers/BinaryPrimitives.cs`, `System/Buffers/Base64.cs`, `System/Convert.cs` | `BinaryPrimitives` (numbers in either byte order, `ReverseEndianness`), `Base64` for UTF-8 and `OperationStatus`, and `Convert`'s hex and Base64 methods. `Unsafe.ReadUnaligned`/`WriteUnaligned` (in `Runtime/Unsafe.cs`) do the loads and stores |
| `System/Numerics/Tensors/TensorPrimitives.cs` | `TensorPrimitives` for `float`, with the System.Numerics.Tensors package's members and argument checks. Element-wise arithmetic, sums and the fast functions (`Exp`, `Sigmoid`, `Tanh`, `SoftMax`, on Cephes' `expf`) on NEON (arm64), SSE2 (x64) and AVX2+FMA (x64 with `-m x86-64-v3`), through operator structs with static abstract members as in .NET; the other functions are `MathF`'s, one element at a time. `SoftMax` subtracts the maximum and corrects for its rounding. `../scripts/tensor-fuzz` compares it with the package |
| `System/Numerics/Tensors/Tensor*.cs`, `ReadOnlyTensorSpan_1.cs`, `TensorShape.cs`, ..., `System/Buffers/NIndex.cs`, `NRange.cs` | `Tensor<T>`, `TensorSpan<T>`, `ReadOnlyTensorSpan<T>`, the dimension spans, `NIndex`, `NRange`, `TensorMarshal` and the `Tensor` class, ported from the System.Numerics.Tensors package (dotnet/runtime, MIT; 11.0.0) with its layout, views, broadcasting and messages. The containers and shape operations are generic; the math (`TensorOperation.Ops.cs`, `Tensor.cs`'s second half, `Tensor.op_*.cs`) was turned from generic math into `float` by a script, and calls `TensorPrimitives` on contiguous runs and `ScalarF.cs` (float's own `Single.cs` formulas) on single elements. `Tensor.Generic.cs` (from `../scripts/tensor-twins.py`) gives each float method .NET's generic signature. `../scripts/tensor-type-fuzz` compares it all with the package |
| `System/Numerics/BitOperations.cs` | `BitOperations`: `PopCount`, `LeadingZeroCount`, `TrailingZeroCount`, `Log2`, rotates, `IsPow2`. clz, rbit and cnt on arm64; bsr and bsf on x64, lzcnt, tzcnt and popcnt with `-m x86-64-v3`; portable code on WebAssembly and DOS |
| `System/Collections/BitArray.cs` | `BitArray`, with .NET's members (`And`, `Or`, `Xor`, `Not`, shifts, `HasAllSet`, `CopyTo`, ...) |
| `System/Threading.cs` | `Interlocked` (atomic instructions) and `Volatile` |
| `System/Runtime/InteropServices/NativeMemory.cs` | `NativeMemory.Alloc`, `AllocZeroed` and `Free`, on `calloc` and `free`; a failure aborts with a message where .NET throws |
| `Runtime/ThreadStatics.cs` | `[ThreadStatic]` fields: each thread's copies, made from calloc on its first access and kept behind a pthread key, freed when the thread ends |
| `Runtime/Startup.cs` | What runs before `Main`: storage for static fields that hold references, `string[] args`. Also static constructors |
| `Runtime/MethodTable.cs` | The layout of the type descriptions the compiler emits |
| `Runtime/Unsafe.cs` | `Unsafe`, `RuntimeHelpers`, `MemoryMarshal` |
| `Runtime/Platform.cs` | Everything used from outside: `calloc`, `write`, `abort` |
| `Intrinsics/*.cs` | `Vector64/128/256<T>` and the NEON, SSE, SSE2, AVX, AVX2 and FMA operations `../lib/Kernels.cs` and `TensorPrimitives` use, and the AVX2 integer ones `../samples/Avx2Ops.cs` checks; the bit instructions `BitOperations` uses |
| `native/arm64.S`, `native/x64.S` | Three routines per architecture that need a calling convention C# can't express: two for storing object references, one for interface calls; and on x64 the stack probe the compiler calls for frames larger than a few pages (arm64 probes inline). Also the stack switch `../lib/Fibers.cs` uses: save the callee-saved registers, change stacks, restore them |
| `native/mac-arm64.S` | `arm64.S` as a Mach-O object, for macOS |
| `native/win-x64.S` | The same for Windows x64's calling convention, plus the stack probe Windows code calls for frames larger than a page. Its stack switch also swaps the thread's stack bounds, which Windows keeps in the thread information block; UEFI has none, and its build defines `ZERO_UEFI` to leave that out. With `../win/start.c` and `../win/runtime.c` (entry point, `write`, `read`) it is all a Windows build adds. UEFI x64 uses it too |
| `native/win-arm64.S` | `arm64.S` as a COFF object, for UEFI on arm64, stack switch included. Windows' arm64 calling convention agrees with Linux's for every register these routines use |

Most of the library is a contract with the compiler: it looks types, methods and fields up by name (`System.Runtime.TypeCast.CheckCastClass`, `String._stringLength`, `Object.m_pEEType`, `System.Runtime.CompilerServices.RawData`, ...) and fails with "Expected type ... not found" when one is missing. Those names can't be changed.

## What works

- Classes, structs, generics, virtual methods, interfaces (including default methods and static abstract members), delegates and lambdas
- Records (`record`, `record struct`) and anonymous types; `ToString()`, `Equals` and `GetHashCode` on every type; `"n=" + 5`; `typeof` and `GetType()` for comparing types
- `is`, `as` and casts to classes and interfaces; pattern matching on types; boxing
- Arrays of any type, `Span<T>`, `stackalloc`, function pointers, P/Invoke
- Strings: comparison, concatenation, `switch`; literals; UTF-8 literals (`"..."u8`)
- `char.IsLetter`, `char.ToUpper` and the rest of `char`, for every Unicode character; `Encoding.UTF8` and the other standard encodings
- Static fields of every type, static constructors, array initializers (`new[] { 1, 2, 3 }`)
- `Math`, `MathF`, `Interlocked`, `Volatile`
- `Main()`, `Main(string[] args)` and top-level statements
- `try`/`finally` and `using` (but see exceptions below)
- Extension methods and C# 14 extension members (properties, static members, `ref` receivers)
- C# 15 unions (`union Result(Ok, Err);`) and `switch` on them; a struct case is boxed, and never freed
- `throw`, as a panic: it prints the exception's message to stderr and aborts, unless lib/'s `Catch.Run` is waiting for it

A failure that would throw in .NET (a failed cast, an index out of range, an arithmetic overflow in `checked` code) prints its name to stderr and aborts. A null dereference is a plain segmentation fault. Integer division by zero aborts with a message on arm64 and is a hardware trap on x64.

## What doesn't

- **Freeing memory.** Every `new`, boxing and string concatenation calls `calloc`, and nothing is ever freed. Use it for objects that live as long as the program, and an arena (`../lib/Arena.cs`) for the rest.
- **Catching exceptions.** `throw` compiles, and `catch` compiles, but nothing unwinds: `RhpThrowEx` jumps back to the innermost `Catch.Run` (lib/Catch.cs, `Runtime/Catch.cs`) if there is one, or prints `Unhandled exception: ` and the message, then aborts. No `catch` block ever runs, and neither do the `finally` blocks and `using` disposals between the `throw` and `Main` (or `Catch.Run`). `try`/`finally` and `using` work on the normal path: the `finally` block runs when the `try` block ends. Without reflection there's no type name to print, so the standard exception types use their name as the default message.
- **Variance.** Casting or dispatching through it (`IEnumerable<string>` as `IEnumerable<object>`, `string[]` as `object[]`) aborts with a message saying so instead of answering wrongly. Arrays don't implement `IList<T>` or `IEnumerable<T>`.
- **Interface calls cost a little more than virtual calls.** When the compiler can't tell which class is behind an interface, the call goes through a cache of the last four classes seen at that call site: about 1.6 ns on an Apple M-series core against 0.9 ns for a virtual call, and 3 to 4 ns when the site alternates between two to four classes. A site that sees more than four pays for a full search (about 5 ns) on each of the extras. In a hot loop, generics with a `struct` constraint or static abstract members are still faster: they're resolved at build time.
- **Most of the base class library.** No `Console` (use `../lib/Out.cs`), reflection (a `Type` only compares; a class without its own `ToString` prints `object`), collections, LINQ (see `../lib/Seq.cs`), async (see `../lib/Fibers.cs` for green threads), `Thread`, or `Nullable<T>` boxing.
- **SIMD operations beyond the ones declared.** To add one, declare it in `Intrinsics/` with the name and signature it has in .NET; the compiler does the rest.
- **SIMD without the hardware.** In .NET, `Vector256<T>.One`, `Vector256.Create` and the other `Vector256` helpers work on any machine, in plain code where the instructions are missing. Here they have no plain-code version: on a target without the instructions (any `Vector256` on arm64, or on x64 built without `-m x86-64-v3`), calling one of them stops the program with `PlatformNotSupportedException`, as calling `Avx2.Add` does in both. Check `Avx.IsSupported` or `Avx2.IsSupported` first, as `../samples/Avx2Ops.cs` does.
- **Windows on arm64 and Intel Macs.** Linux (x64, arm64), macOS on Apple Silicon, Windows x64, WebAssembly (WASI), 32-bit DOS on a 386 (see the main README; it uses the WebAssembly build of this library) and UEFI (x64, arm64, with no operating system) only.
- **A threading library.** There's no `Thread` or `lock`; `../lib/Threads.cs` has `Parallel.For`, on pthreads. Allocation, interface calls and `Interlocked` are safe to use from several threads (tested with eight on arm64). So are static constructors: the first thread to touch a type runs its constructor, and the others wait for it to finish. The single-threaded build (`-d WASM`: WebAssembly, DOS, the C64) runs a constructor without that bookkeeping, which saves about 3 KB on a C64.

## Where the code comes from, and the license

zerocore is MIT-licensed (see `LICENSE`). Its purpose is to let programs built with bflat avoid linking zerolib, which is AGPL-3.0.

It was written for this repository against two MIT-licensed sources: the .NET runtime's own NativeAOT libraries ([dotnet/runtime](https://github.com/dotnet/runtime), `src/coreclr/nativeaot`, mainly `Test.CoreLib` and `Runtime.Base`) for the data layouts and helper signatures, and the compiler's error messages for what else it requires. `System/SpanHelpers.cs` is adapted from dotnet/runtime directly and says so. So are `System/Buffers/NIndex.cs`, `NRange.cs` and the tensor types in `System/Numerics/Tensors/`, ported from the System.Numerics.Tensors package; three of their float functions (`SinPi`, `CosPi`, `TanPi`) are based on AMD's aocl-libm-ose, under the BSD 3-Clause license. [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) has both licenses' texts. A program that uses those parts carries their code, so distributing it in binary form means passing those notices on (the BSD license asks for its notice in the documentation); the SDK package includes the file.

One caveat, stated plainly: this is not a clean-room implementation. It was written with AI assistance in a session that had earlier read and modified zerolib's source. Care was taken to write each part from the MIT sources and the compiler's contract instead, and the required names are the compiler's, not zerolib's.

On 2026-11-28 the two were compared mechanically. zerocore, `../lib` and the C and assembly runtimes were checked against zerolib as it stood upstream (fork commit `5c2a614`, before this project's own changes to it), and lines that .NET's MIT NativeAOT sources also contain were set aside, since both libraries draw on them:

- Of about 15,000 meaningful lines, 35 match zerolib but not .NET. All of them are declarations the compiler or the .NET API dictates, such as `[RuntimeExport("RhpNewFast")]`, attribute constructors and `public struct Nullable<T> where T : struct`.
- Comparing token sequences finds longer shared stretches in two files only. `System/Span.cs` shares .NET's span constructors with `throw` replaced by `Environment.FailFast`, which any library without catchable exceptions has to do. `System/SpanHelpers.cs` shares element-by-element copy loops.
- Allocation, casts, startup, `string` and `object` share nothing longer than a few tokens.

A text comparison shows what was copied, but it can't prove how something was written, so it supports the MIT license rather than guaranteeing it. If your company reviews the licenses of what it ships, this section and the notices file are what to give the reviewer. This isn't legal advice.

bflat itself (the compiler driver) remains AGPL-3.0. Using it to compile a program doesn't put its code in the output; zerolib was the part that ended up in every binary.
