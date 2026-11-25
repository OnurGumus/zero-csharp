---
title: Zero mode
index: 1
---

# Zero mode

Zero mode is C# without .NET's runtime. The compiler turns the program into native code ahead of time
and links it into one self-contained executable that needs only the C library, and that program carries no garbage collector, no exception
handling, no reflection and almost no base class library. What is left is C#'s syntax, type safety
and generics, with binaries the size of a C program's. The repository's README puts it as "C with
proper generics".

## Why give up the runtime

A normal .NET program brings `System.Private.CoreLib` and a native runtime with it: megabytes of code
before your own. Zero mode drops both. The smallest sample, `samples/Tiny.cs`, and a hello world through
`puts` are both about 12 KB on Linux arm64, without debug information or a symbol table.

<!-- sample: samples/Tiny.cs file -->
```csharp
// Example: Smallest possible bflat binary (~12 KB).
// Build: ./build.sh Tiny   (build.sh adds -Os)

using System.Runtime.InteropServices;

namespace TinySample;

public static class Program
{
    [DllImport("libc")]
    static unsafe extern int puts(byte* s);

    public static int Main()
    {
        byte* msg = stackalloc byte[] { (byte)'H', (byte)'i', 0 };
        unsafe { puts(msg); } // SAFETY: msg is NUL-terminated.
        return 0;
    }
}
```

The price is what the runtime used to do. Nothing collects garbage, so memory is your job (see
[Memory without a GC](memory.html)). Nothing catches exceptions, so a `throw`, a failed cast or an
index out of range prints a message and ends the program. And most library types you know from .NET
are not there; `lib/` supplies the ones these programs need. The same program also builds as a
WebAssembly module and as a DOS program for a 386 (see [Targets](targets.html)).

## bflat and the fork

[bflat](https://flattened.net/) is a C# compiler that produces native programs directly. It has a zero mode of its own, `--stdlib:zero`, with a small core library
called zerolib.

This repository needs a fork of it, [OnurGumus/bflat](https://github.com/OnurGumus/bflat). The fork
matters here mostly as a compiler:

- **It compiles C# 15.** Its Roslyn is the one shipped with the .NET 11 SDK, and a
  `--compiler-feature` option turns on the [updated memory safety rules](safety-rules.html) that every
  sample is written under.
- **Instruction sets come only from `-m`.** Zero mode has no run-time CPU detection, so a check such
  as `Dp.IsSupported` is `false` unless the build names that instruction set with `-m`.
- **It ships the .NET 10 GA JIT**, which fixes static-field stores that the RC1 JIT in upstream bflat
  miscompiles on x64.

The fork also fixes and extends zerolib (strings, arrays, static fields, casts, SIMD). Those changes
only matter if you build with `--stdlib:zero`. The repository's `build.sh` doesn't: it uses its own core
library instead. [Install](../get-started/install.html) shows how to get and build the fork.

## Two libraries: zerocore and lib

Every C# program needs a core library: the assembly that defines `object`, `int` and `string`, and that
holds the code the compiler calls to allocate objects, check casts and set up static fields. In zero
mode that code is all the "runtime" there is.

**`zerocore/` is the core library.** It replaces zerolib, under the MIT license, in about 2,400 lines
of C# and three short assembly routines per architecture. `build.sh` compiles it from source and
passes it to bflat with `--stdlib:none`, so nothing AGPL-licensed ends up in the binaries. It contains:

- the types the C# compiler requires: primitives, `object`, arrays, delegates, `Span<T>` and
  `ReadOnlySpan<T>`;
- `string`, with `Length`, an indexer, `==`, `!=`, `Equals`, `IsNullOrEmpty` and concatenation, so
  `a + b` and `switch` on strings work;
- allocation: every `new`, boxing and string concatenation calls `calloc`, which zeroes memory as C#
  requires, and nothing is ever freed;
- casts: `is`, `as` and `(T)x` to classes and interfaces, and interface calls the compiler can't
  resolve at build time;
- startup: storage for static fields that hold references, static constructors and
  `Main(string[] args)`;
- `Math`, `MathF`, `Interlocked`, `Volatile`, and the SIMD types `lib/Kernels.cs` uses;
- `Half`, `BitOperations`, `BitArray` and `BitConverter`'s bit conversions;
- `NativeMemory`, on `calloc` and `free`, and the `SupportedOSPlatform`, `UnsupportedOSPlatform`
  and `ConstantExpected` attributes for code shared with .NET;
- `System.Numerics`: `Complex`, the vectors, `Matrix3x2`, `Matrix4x4`, `Quaternion` and `Plane`;
  `TensorPrimitives` for spans of `float`; `Tensor<T>`, `TensorSpan<T>` and the `Tensor` class,
  with math for `float`.

Much of it is a contract with the compiler, which looks types, methods and fields up by name. Those
names can't be changed. Compared with zerolib, zerocore adds casts to interfaces, interface calls the
compiler can't resolve at build time, boxing, `Main(string[] args)`, `BitOperations` and `BitArray`,
and leaves out `Console` (`lib/` has it).

**`lib/` is the library programs use.** It is ordinary C# built on top of zerocore, and it is where
zero mode gets what .NET's base class library would otherwise provide:

- `Arena`, the allocator for everything short-lived (see [Memory without a GC](memory.html));
- collections that live in an arena: `List`, `Dict`, `HashSet`, `Queue`, `Stack`, `PriorityQueue`,
  `RefList`, `StrDict`, `RefDict`;
- `Regex`, regular expressions matched in linear time;
- `Console`, `Str`, `StrBuilder`, `Out` and `In` for text, input and output (see
  [Strings without a GC](strings.html));
- `Kernels`, SIMD math, and `Dos`, the PC's screen, keyboard and speaker on DOS.

The split follows one rule: zerocore holds what the compiler requires, and `lib/` holds what programs
choose to use. Each is compiled once into an IL assembly, and each program references both with `-r`.
Because the assembly holds IL, not native code, every program's build compiles only the parts it uses,
instantiates generics for its own types, and generates code for its own `-m` flags.

## Where to go next

- [Memory without a GC](memory.html) explains the biggest change from .NET.
- [Limits](limits.html) lists what zero mode supports and what it doesn't.
- [Your first program](../get-started/first-program.html) builds and runs one.
