# Zero C#

![Debugging a zero-mode program in VS Code](bflat.png)

C# compiled to small native programs with no .NET runtime and no garbage collector. On Linux a hello
world is 12 KB. It builds on [bflat](https://flattened.net/)'s zero mode, uses C# 15 with the updated
memory safety rules, and runs on Linux, macOS, Windows, WebAssembly, DOS on a 386, and UEFI with no
operating system at all. Programs that are small enough run on a Commodore 64.

You keep C#'s syntax, generics, spans, interfaces and lambdas. You lose the garbage collector,
catchable exceptions, reflection and almost all of the base class library. Memory you want back goes in
an arena and is freed all at once.

Documentation: <https://onurgumus.github.io/zero-csharp/>

## Try it

With the .NET 10 SDK on Linux, Windows or an Apple Silicon Mac:

```bash
dotnet new install ZeroCSharp.Templates
dotnet new zero -o Hello
cd Hello
dotnet build
```

That gives you a native program in `bin/Debug/zero03/`. The template's `Program.cs`:

```csharp
Out.Write("What's your name? ");
Span<char> buffer = stackalloc char[64];
int length = In.ReadLine(buffer);
ReadOnlySpan<char> name = length switch
{
    <= 0 => "world",
    _ when length > buffer.Length => buffer,
    _ => buffer.Slice(0, length),
};
Out.Write("Hello, ");
Out.Write(name);
Out.Line("!");
```

Add `-p:ZeroTarget=win-x64` (or `linux-x64`, `linux-arm64`, `osx-arm64`, `uefi-x64`, `uefi-arm64`) to
build for another system, or `c64`, `pico2` (Raspberry Pi Pico 2) or `r4` (Arduino Uno R4) for a
machine without one; `dotnet new zeropico` starts a microcontroller project.
A library project with `<NativeLib>Shared</NativeLib>` builds a `.so`, `.dylib` or `.dll` that .NET and
C programs can call. The project comes with a VS Code setup for debugging.
On an Apple Silicon Mac, `dotnet new zerogpu` makes a program whose C# kernels run on the GPU, through
Metal (`<ZeroGpu>Metal</ZeroGpu>`; it needs Xcode, its Metal Toolchain and Docker).

More: [Coming from .NET](https://onurgumus.github.io/zero-csharp/get-started/coming-from-dotnet.html),
[Install with dotnet](https://onurgumus.github.io/zero-csharp/get-started/install-with-dotnet.html),
[the tutorial](https://onurgumus.github.io/zero-csharp/tutorial/index.html),
[Build a native library](https://onurgumus.github.io/zero-csharp/how-to/build-a-native-library.html),
[Debug a program](https://onurgumus.github.io/zero-csharp/how-to/debug-a-program.html).

## Targets

| Target | Build from this repo | Program sizes |
|---|---|---|
| Linux x64 and arm64 | `./build.sh` | 12 KB (Tiny) to 84 KB (Collections) |
| macOS, Apple Silicon | `./build.sh --mac`, on a Mac | from 50 KB |
| macOS with C# kernels on the GPU (Metal), experimental | `./build.sh --gpu`, on a Mac | 118 KB (the Gpu sample) |
| Windows x64 | `./build.sh --win`, from Linux or macOS | 9 KB to 68 KB |
| WebAssembly (WASI) | `./build.sh --wasm` | 16 KB to 80 KB |
| DOS, 386 with a 387 | `./build.sh --dos` | 16 KB to 93 KB |
| UEFI x64 and arm64, no OS | `./build.sh --uefi` | 10 KB to 72 KB |
| Commodore 64 | `./build.sh --c64` | 7 KB to 49 KB, of the 50 KB there are |
| Raspberry Pi Pico 2 (Cortex-M33), in QEMU | `./build.sh --pico2` | 9 KB to 88 KB of flash |
| Arduino Uno R4 (Cortex-M4, 32 KB of RAM), in QEMU | `./build.sh --r4` | 9 KB to 62 KB of flash |

The sizes leave out Llama, which carries its model and is about 1.1 MB everywhere. WebAssembly, DOS
and the Commodore 64 go through [NativeAOT-LLVM](https://github.com/dotnet/runtimelab/tree/feature/NativeAOT-LLVM)
instead of bflat's own code generator. The DOS build runs under the HX DOS extender and is tested on
an emulated 386 with 4 MB. A UEFI program is started by the PC's firmware, from a USB stick or in QEMU,
and has no operating system or C library under it.
[Build for UEFI](https://onurgumus.github.io/zero-csharp/how-to/build-for-uefi.html) shows how. On the
Commodore 64 the same bitcode is rewritten for 16-bit addresses and compiled by
[llvm-mos](https://llvm-mos.org/); the samples that fit in its memory run there, in the VICE emulator
([Build for the Commodore 64](https://onurgumus.github.io/zero-csharp/how-to/build-for-c64.html)). The
microcontrollers take the same bitcode to Arm's own clang, and run in QEMU for now
([Build for microcontrollers](https://onurgumus.github.io/zero-csharp/how-to/build-for-microcontrollers.html)).
The GPU target compiles a program twice: whole, for macOS, and its kernels (C# methods marked
`[RuntimeExport("Name")]`) through NativeAOT-LLVM's bitcode into AIR, the LLVM dialect Apple's
Metal compiler reads (`zero-bitcode ll2air`); `lib/Gpu.cs` launches them. It needs Apple's Metal Toolchain
(`xcodebuild -downloadComponent MetalToolchain`). AIR isn't documented by Apple, so a new Xcode can break
it. A kernel can use buffers and values, 2D grids, threadgroup memory, barriers and atomics, and nothing
that needs the runtime: no allocation, strings, `double` or calls into other methods.
[Targets](https://onurgumus.github.io/zero-csharp/concepts/targets.html) explains how each one is built.

## What works

- Classes, structs, interfaces (with default methods), virtual calls, generics, and static abstract
  interface members
- `Span<T>`, arrays, `stackalloc`, pointers and function pointers
- Strings with `==`, `+` and `switch`; `lib/` adds formatting, parsing and span operations
- Casts (`is`, `as`, `(T)x`), boxing, records, anonymous types, tuples
- Lambdas and delegates, `try`/`finally`, `using`
- Static fields and static constructors, `Main(string[] args)`, top-level statements
- P/Invoke and `[UnmanagedCallersOnly]` callbacks
- Files, a monotonic clock and the date, and seeded random numbers, from `lib/`, on every target
- SIMD intrinsics (NEON, AVX2, FMA), `Interlocked`, `Volatile`, threads through `Parallel.For`, and green threads (fibers) with channels

Every `new` is a `calloc` that is never freed. There is no async/await, no LINQ from .NET (`lib/Seq.cs`
has `Where`, `Select` and friends without allocating), no culture-aware formatting, and casts that need
variance stop the program. [Limits](https://onurgumus.github.io/zero-csharp/concepts/limits.html) has
the full list.

## Memory

Short-lived data goes in an arena: memory handed out by moving a pointer forward and freed in one call.
`lib/Arena.cs` is the allocator, and the collections and strings in `lib/` take the arena to allocate
in:

```csharp
Arena app = Arena.Create();                  // lives as long as the program
Arena frame = Arena.Create(64 * 1024);       // reset after every frame or request

var names = List<int>.Create(app);           // collections live in the arena you give them
string line = Str.Format(frame, $"{x}, {y}");

unsafe { frame.Reset(); }                    // SAFETY: line is not used after this
```

Freeing is `unsafe`, because it is what can leave a pointer dangling. `OpenScope()` frees what a
`using` block allocated, and `OpenObjectScope()` does the same for ordinary `new`, closures and
string concatenation inside the block. `Arena.DebugChecks` catches collections used after their memory
was freed. See [Memory without a GC](https://onurgumus.github.io/zero-csharp/concepts/memory.html) and
[Strings without a GC](https://onurgumus.github.io/zero-csharp/concepts/strings.html).

## In this repository

| Folder | What |
|---|---|
| [`zerocore/`](zerocore/README.md) | The core library: `object`, `string`, casts, allocation. About 2,400 lines of C#, MIT licensed |
| [`lib/`](lib/) | Arenas, collections, strings, input and output, files, the clock, random numbers, SIMD kernels, `Parallel.For`, fibers (green threads) and channels, DOS and UEFI graphics, the Commodore 64's screen and sprites, a microcontroller board's LED, registers and timer interrupt, GPU buffers and kernel launches ([reference](https://onurgumus.github.io/zero-csharp/reference/index.html)) |
| [`samples/`](samples/) | The programs below |
| [`sdk/`](sdk/) | The `ZeroCSharp.*` NuGet packages |
| [`docs/`](docs/) | The documentation site |
| `mac/`, `win/`, `wasm/`, `dos/`, `efi/`, `c64/`, `arm/` | Each target's entry point and the C functions it needs (`efi/libm/` is from musl, MIT) |
| `gpu/` | The GPU target: Metal behind the C functions `lib/Gpu.cs` calls (`metal.m`); `sdk/ZeroCSharp.Bitcode/Air.cs` turns C# kernels into AIR |

The programs don't use bflat's own zero-mode library, zerolib, which is AGPL. zerocore replaces it, so
nothing AGPL is linked into them.

## Samples

| Sample | Shows | Size |
|---|---|---|
| `Tiny` | The smallest program (`-Os`) | 12 KB |
| `Minimal` | Hello world through `puts` | 12 KB |
| `Arena` | The arena allocator: growth, alignment, `Save`/`Restore`, scopes, objects in an arena, debug checks | 35 KB |
| `Collections` | `List`, `Dict`, `RefList`, `StrDict`, `RefDict`: word counts, sorting, `foreach`, `Clone` | 84 KB |
| `VirtualTest` | Polymorphism three ways: function-pointer vtables, static abstract interfaces, virtual methods | 15 KB |
| `Casts` | 41 checks of `is`, `as`, `(T)x`, boxing, interface calls and delegates | 32 KB |
| `Core` | 32 checks of `Math`, `MathF`, `Interlocked`, `Volatile` and interface dispatch | 48 KB |
| `Simd` | A dot product with NEON or AVX and FMA, 12x faster than scalar on Apple Silicon | 18 KB |
| `Kernels` | `lib/Kernels.cs`: exp, softmax, SiLU, RMSNorm, matrix-vector, int8 dot product; softmax is 7x faster than scalar libm on Apple Silicon | 24 KB |
| `Strings` | 140 checks of span operations, parsing, `$"..."` formatting, arena strings and `StrBuilder` | 67 KB |
| `Quiz`, `QuizStep1` to `QuizStep3` | A quiz that reads answers with `In.ReadLine`, and the tutorial's steps towards it | 16 to 25 KB |
| `Guards` | One mistake per run that `lib/` stops the program for, such as marks restored out of order | 56 KB |
| `Queries` | LINQ-style queries with `lib/Seq.cs`, checked to allocate nothing | 44 KB |
| `Threads` | `Parallel.For` with one arena per thread | 28 KB |
| `Fibers` | `lib/Fibers.cs`: green threads taking turns, channels, sleeping, 2,000 fibers at once (6 on DOS or a microcontroller), and the cost of a switch | 52 KB |
| `Files` | `lib/File.cs`: whole files, and an open file read, written and moved through | 31 KB |
| `Dice` | `lib/Random.cs` and `lib/Clock.cs`: seeded dice rolls that match on every target, durations, the date | 29 KB |
| `Env` | `System.Environment`: variables, `Exit` from anywhere (writing what `Out` holds), the processor count | 20 KB |
| `Pipe` | `In.Read`: standard input as bytes, checked byte for byte through a pipe | 19 KB |
| `Records` | `record struct` and `record class`, `with`, `ToString`, `typeof`, anonymous types | 34 KB |
| `Refs` | Structs in an arena passed and returned by `ref` | 24 KB |
| `Llama` | Karpathy's stories260K language model, compiled into the program; it tells the same story on every target, DOS and UEFI included | 1.1 MB |
| `Vga` | DOS and UEFI only: a small game in 320x200 with 256 colors | 36 KB |
| `SmallStrings`, `SmallIntegers`, `SmallObjects` and seven more | Tests sized for a small machine: what Strings, Casts, Collections and the others check, in pieces that each fit in a Commodore 64 | 17 to 36 KB |
| `C64Demo` | Commodore 64 only: text, a sprite and raster bars, with objects, a list and a lambda behind them | 36 KB |
| `Paddles` | Commodore 64 only: a paddle-and-ball game against the computer, with sprites, SID sound, records and unions | 34 KB |
| `Gpu` | GPU only (`--gpu`): kernels for adding, Collatz, SAXPY, a reduction, matrix multiply (plain and tiled), histograms and float atomics, each checked against the CPU and timed against a plain C# loop on one core | 118 KB (macOS) |
| `Blink` | Microcontrollers only: the board's LED, blinked by a C# interrupt handler, and a check that a busy handler leaves the interrupted code's references alone | 18 KB of flash |

Sizes are for Linux arm64. `build.sh` leaves out debug information and the symbol table and turns off
position-independent code, which halves most programs; `KEEP_SYMBOLS=1 ./build.sh` keeps them, for gdb
or perf.

To build them, clone this repository and build the bflat fork next to it
([Install](https://onurgumus.github.io/zero-csharp/get-started/install.html)), then:

```bash
./setup.sh            # checks prerequisites; on macOS, builds the container image bflat runs in
./build.sh            # every sample, into bin/
./run.sh Arena        # one of them
./test.sh             # build and check everything
```

[CONTRIBUTING.md](CONTRIBUTING.md) covers the build, the tests and editor setup.

## License

MIT ([`LICENSE`](LICENSE)), including `zerocore/` and the `ZeroCSharp.Sdk` and `ZeroCSharp.Templates`
packages. zerocore wasn't written in a clean room: read [its README](zerocore/README.md) before
shipping a commercial product on it.

bflat and [the fork](https://github.com/OnurGumus/bflat) are AGPL-3.0, and so are the
`ZeroCSharp.Bflat.*` packages that carry it. They are used as a compiler, and nothing from them is
linked into the programs. Each program does link the C library of its target system, under that
library's license.
