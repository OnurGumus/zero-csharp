---
title: Targets
index: 5
---

# Targets

The same C# source builds for six targets: a native Linux program, a macOS program for Apple
Silicon, a 64-bit Windows program, a WebAssembly module, a DOS program for a 386, and a UEFI program
that the firmware starts with no operating system. WebAssembly and DOS need a different compiler for
their last step, but all six share the same first step and the same core library. This page explains how each path works and what changes
along it. A seventh target takes the programs that are small enough: the
[Commodore 64](#the-commodore-64), an 8-bit machine with 50 KB for a program. And on an Apple Silicon Mac,
a program's kernels can also run on [the GPU](#the-gpu-metal), and, experimentally, on
[NVIDIA GPUs](#nvidia-gpus-cuda) under Linux.

## The shared first step

Before any target, `zerocore/` and `lib/` are each compiled into an IL assembly, and every sample is
compiled against them with `-r`. The assembly's metadata describes the public API, so `-r` takes the
place of a header file. Because it holds IL rather than native code, the last step for each program
compiles only what that program uses: generics are instantiated for its types, calls are inlined across
the library boundary, and code is generated for its target and its `-m` flags.

`lib/` is the same assembly for every target. zerocore is built a second time with `-d WASM` for
WebAssembly, DOS and the Commodore 64, because that build finds static fields differently.

## Native Linux

The native target is bflat's own: `bflat build` compiles a sample, with `zerocore/` as its core library
(`--stdlib:none`), into one executable for Linux on x64 or arm64 that links only the system's C library.

```bash
./build.sh Arena      # builds one sample into ./bin
./run.sh Arena        # runs it (in the container on macOS)
```

bflat is a Linux program, so on a Mac it runs in a Linux container of the Mac's own architecture, which
is native on Apple Silicon. `BFLAT_ARCH=x64` or `arm64` cross-compiles.

A few things `build.sh` adds matter for what you get:

- **Instruction sets come only from `-m`.** There is no run-time CPU detection, so Simd and Kernels are
  built with `-m x86-64-v3` on x64 and `-m dotprod` on arm64. Without those flags, their fast paths
  report `IsSupported == false`.
- **4 KB pages on arm64.** The default 64 KB segment alignment would pad even Tiny to about 200 KB, so
  `build.sh` links with a 4 KB maximum page size. Such binaries don't load on kernels with 16 KB or
  64 KB pages; set `ARM64_MAX_PAGE_SIZE=65536` for those.
- **Tiny** is built with `-Os --no-pie` and stripped.

[Your first program](../get-started/first-program.html) walks through a native build.

## macOS on Apple Silicon

`./build.sh --mac` builds programs that run on the Mac itself, without the container. bflat still runs
in the container, but with `--os macos -c` it only compiles: it writes a Mach-O object file, and the
Mac's own clang links it with a few small files:

- `mac/start.c` holds `main`, and `mac/runtime.c` a `clock_gettime` that takes Linux's clock numbers,
  as `lib/Clock.cs` passes them. The rest of the C library calls have the same names on macOS.
- `zerocore/native/mac-arm64.S` is `arm64.S` as a Mach-O object.

```bash
./build.sh --mac Core     # bin/mac/Core
./run.sh --mac Core       # or just bin/mac/Core
```

zerocore is unchanged. The link maps Apple's names for the module section's bounds onto the
`__start___modules` and `__stop___modules` symbols that GNU ld provides on Linux. Programs start at
50 KB, because macOS arm64 aligns each of a program's four segments to 16 KB pages.

## iOS simulator

`./build.sh --ios` builds iOS programs and runs them in the iOS simulator, on an Apple Silicon Mac
with Xcode's iOS platform. They are console programs, started without an app bundle, the way test
runners start programs there; an app with a window and real devices are still to come.

The program's object is the one `--mac` builds: iOS runs the same arm64 code. But bflat compiles for
Apple only as macOS, the object's build-version load command says so, and Apple's linker won't put an
object built for macOS into an iOS program. `zero-bitcode macho-platform` marks a copy as built for the simulator
(iOS 17 and later; `IOS_MIN` changes it), and clang links it against the simulator's SDK with
`mac/`'s C files, compiled again for the simulator.

```bash
./build.sh --ios Core     # bin/ios/Core
./run.sh --ios Core       # in the booted simulator, or the first iPhone, which it boots
```

`./run.sh --ios` runs the program with `xcrun simctl spawn`. That starts programs in the simulated
device's data directory, and the simulator has no shell, so `ios/launch.c`, a small simulator
program, moves to the caller's directory and then becomes the program: relative paths mean what they
do on the Mac, which sees the same files. Standard input and output and the exit code pass through.
`IOS_SIMULATOR` picks a simulator by name or UDID. `./test.sh --ios` runs every sample there.

Passing there shows that the code and iOS's own C library, which the simulator loads, work together.
It doesn't show that a program will run on an iPhone. A simulator program is a Mac process, on the
Mac's kernel, with no sandbox and the Mac's stack size. On an iPhone only a signed app runs; it can
write only inside its own container, it gets a 1 MB main-thread stack, and iOS stops it if it uses
too much memory.

## Android

`./build.sh --android` builds command-line programs for Android on arm64, the kind `adb shell` runs;
an app with a window is still to come. An Android program is a Linux program linked against Android's
C library, Bionic, and started by Android's loader, so bflat builds it as for Linux, with
`--libc bionic`. Its layout carries what that link needs, so the Android NDK isn't needed. The
programs are position-independent, as Android requires, and aligned for 16 KB pages, which phones
with Android 15 and later may have.

lib/ calls the C library by glibc's names. Bionic has them all but three, which `android/runtime.c`
supplies: `__errno_location`, and `__xstat64` and `__lxstat64`, glibc's old names for `stat` and
`lstat` (Bionic's `struct stat` has glibc's arm64 layout). Its `ZeroSystem` says Android, because Bionic
answers `getaddrinfo` the BSD way, as macOS does: `ai_canonname` before `ai_addr`, and other error
numbers.

```bash
./build.sh --android Core     # bin/android/Core
./run.sh --android Core       # on the connected phone or emulator
```

`./run.sh --android` copies the program to `/data/local/tmp/zero` on the device, through adb, and runs
it there; standard input and output and the exit code pass through. With no device connected, it
starts the first virtual device Android Studio made (`ANDROID_AVD` picks another), without a window.
`ANDROID_SERIAL` picks one of several devices, as it does for adb. `./test.sh --android` runs every
sample there.

Without this repository, the SDK builds the same programs: `dotnet build -p:ZeroTarget=android-arm64`,
and `dotnet run` runs them through adb (see
[Install with dotnet](../get-started/install-with-dotnet.html#android)).

With `ANDROID_RUN=container`, the programs run in an arm64 Linux container instead, with only Android's
loader and C library, from `android/sysroot/` (copies from Android 15; its README says where they came
from). That is how CI runs the samples, on its arm64 Linux runner, without an emulator. It shows that
the programs and Bionic work together on a Linux kernel; the emulator also runs them on Android
itself, with its security rules.

## Windows x64

`./build.sh --win` uses the same bflat, with `--os windows --arch x64`, and the same zerocore and
`lib/`. A few small files supply what is Windows-specific:

- `win/start.c` is the entry point. Windows starts a program with no `argc` or `argv`, so it builds
  them from the command line, in UTF-8 as on Linux, and calls `Main`. `win/runtime.c` provides `write`,
  `read` and `clock_gettime` on top of kernel32, and `fseeko` and `ftello`, which the Universal CRT
  calls `_fseeki64` and `_ftelli64`. The rest of the C library comes from the Universal CRT.
- `zerocore/native/win-x64.S` is zerocore's assembly for the Windows calling convention, plus the stack
  probe that Windows needs for frames larger than a page.

```bash
./build.sh --win Core     # bin/win/Core.exe
./run.sh --win Core       # runs it under Wine, in an x64 container
```

The programs print what the native build prints. `puts` ends lines with `\r\n`, as Windows programs
do, and a program zerocore stops exits with code 3. Only x64 is supported.

## WebAssembly

bflat can't target WebAssembly, so `./build.sh --wasm` uses a different compiler for the last step:

1. **bflat** compiles each sample to IL, exactly as for the native build.
2. **[NativeAOT-LLVM](https://github.com/dotnet/runtimelab/tree/feature/NativeAOT-LLVM)**, an
   experimental NativeAOT compiler from dotnet/runtimelab, compiles the IL to LLVM bitcode. It uses
   zerocore, built with `-d WASM`, instead of .NET's runtime.
3. **The WASI SDK's clang** compiles the bitcode and links it with `wasm/runtime.c` and WASI's C
   library. The result runs under wasmtime.

NativeAOT-LLVM normally links a full runtime with a GC, exception handling and a type loader. Here
zerocore is the runtime: allocation, casts and interface calls are its C#, the same code as on Linux.
`wasm/runtime.c` holds only what can't be written in C#: the entry point, and a few functions whose
calling convention differs from a C# method's.

The detail that shapes it is the **shadow stack**. WebAssembly's own stack can't be addressed, so
managed code keeps some locals and arguments on a separate stack in linear memory. Native code entering
managed code hands it that stack, and every C# method takes it as a hidden first argument. Without a
GC, the transitions between native and managed code have nothing else to do.

What differs from the native build:

- **32-bit pointers**, so structs that hold pointers are smaller and arena byte counts are lower.
- **SIMD only from C.** NativeAOT-LLVM can't compile vector operations for WebAssembly (it turns
  them into a `PlatformNotSupportedException`), so the instruction sets report `IsSupported == false`
  and Simd takes its scalar path. `TensorPrimitives` and `lib/Kernels.cs` call simd128 kernels in
  `wasm/simd.c` instead, when `System.Runtime.Intrinsics.Wasm.PackedSimd.IsSupported`, which is true
  only in this build.
- **No callbacks from native code into C#** inside a running program.
- **One exact compiler version.** zerocore and `runtime.c` implement NativeAOT-LLVM's internal runtime
  functions, whose names and signatures aren't documented and were read from the compiler's output. A
  changed signature fails the link; a changed meaning only shows up when a program runs. Treat the
  versions pinned in `env.sh` as a set.

[Run on WebAssembly](../get-started/webassembly.html) and
[Build for WebAssembly](../how-to/build-for-webassembly.html) have the commands.

## DOS on a 386

bflat's own compiler can make 32-bit x86 programs, but only for a Pentium 4 or later, because its code
generator always uses SSE2 for floating point. The WebAssembly route doesn't have that problem: its
output isn't tied to a processor until the last step. So `./build.sh --dos` reuses it:

1. **bflat and NativeAOT-LLVM** compile each sample to LLVM bitcode, exactly as for WebAssembly, against
   zerocore's WebAssembly build, which is its single-threaded 32-bit build.
2. **`zero-bitcode wasm2i386`** rewrites the one WebAssembly-specific part. NativeAOT-LLVM writes the
   program's data (type descriptions, string literals, static fields) as a WebAssembly object file.
   The tool, a small .NET program in `sdk/ZeroCSharp.Bitcode`, turns it into assembly with the same bytes and translates its few tiny functions to C.
3. **clang with `-march=i386`** compiles the bitcode and those files to 386 code.
4. **bflat's linker** links it all with `dos/runtime.c` into a 32-bit Windows console program.

The [HX DOS extender](https://github.com/Baron-von-Riedesel/HX) runs such programs under DOS and supplies
the few Windows functions `runtime.c` calls. There is no C library, so `dos/runtime.c` is one: the entry
point, `calloc`, `write`, `clock_gettime`, `fopen` and the other file functions `lib/File.cs` calls,
the math functions and 64-bit division, plus what `wasm/runtime.c` holds for
WebAssembly. Managed code still passes its shadow stack as a hidden first argument, because it is the
same bitcode. `runtime.c` also gives C# the PC itself: the VGA's graphics mode, keyboard, mouse,
speaker, clock and files, which `lib/Dos.cs` wraps.

What the DOS target needs, and how it stays honest:

- **A 386 with a 387, or a 486DX or later.** All floating point uses 387 instructions, with no software
  fallback.
- **2 MB of memory or more.** Each program reserves 256 KB for its stack and 256 KB for the shadow stack.
- **A check that nothing newer than a 386 gets in.** After linking, `build.sh` disassembles each program
  and fails if it finds an instruction a 386 or 387 lacks. This is easy to break: clang's
  `-march=i386` still emits the 486's `cmpxchg` for an atomic operation. zerocore's `Interlocked`
  avoids that by being ordinary code in this build, and it was the first thing the check caught.
- **Bochs for testing.** Its `i386` processor model refuses instructions a 386 doesn't have. QEMU's
  `486` model runs the 486's own instructions, so it can't tell a 386 program from a 486 one.
- **Failure messages on standard output**, since DOS's command line can't redirect anything else. A
  failed cast or an index out of range ends the program with exit code 134.
- **One thread**, and the same exact-compiler-version caveat as WebAssembly.

```bash
./run.sh --dos Casts           # runs under FreeDOS in an emulator
./run.sh --dos --window Vga    # shows the DOS screen
```

[Run on DOS](../get-started/dos.html), [Build for DOS](../how-to/build-for-dos.html) and
[Draw on VGA](../how-to/draw-on-vga.html) go further.

## The Commodore 64

The 6502 is an 8-bit processor with three registers and 16-bit addresses, and a C64 has 64 KB of
memory. NativeAOT-LLVM can't compile for it: it only knows 32-bit and 64-bit machines, and every
object layout in its output assumes 4-byte pointers. But its output is bitcode, and
[llvm-mos](https://llvm-mos.org/) is an LLVM that generates 6502 code. `./build.sh --c64` joins the two:

1. **bflat and NativeAOT-LLVM** compile each sample to bitcode as for DOS, but optimizing for size
   (`--Os`), which among other things keeps one copy of each loop where `-O` makes two.
2. **`zero-bitcode ll2mos`** rewrites the bitcode, as text, for 2-byte pointers. Memory keeps the 32-bit
   layout: a pointer in memory stays a 4-byte slot, holding the 16-bit address and two zero bytes, so
   every offset the compiler worked out still holds. Only pointers in registers shrink. A store of a
   pointer becomes a store of the address widened to 32 bits; a load reads the low half; pointers inside
   struct types become integers. NativeAOT-LLVM only uses packed structs, so nothing else in a layout
   depends on the processor.
3. **`zero-bitcode wasm2mos`** does for the 6502 what `wasm2i386` does for the 386, with the same
   reader: the program's data becomes assembly, each address in it a 2-byte address and two zero bytes.
4. **llvm-mos** compiles it all with `c64/runtime.c` and optimizes the whole program at link time, into
   a `.prg` file that BASIC loads and starts.

Three things that are harmless on a 32-bit processor matter here:

- **A pointer and a 32-bit integer travel in different registers.** A function declared as returning
  one and defined as returning the other works on a 386 and returns garbage on a 6502. `ll2mos`
  refuses a program with such a pair, and the few functions NativeAOT-LLVM writes as WebAssembly get
  their real types from the bitcode that calls them.
- **C's `int` is 16 bits.** Every function C# imports is bound to a `cs_` name, which `c64/runtime.c`
  defines with 32-bit parameters, so C#'s `calloc(nuint, nuint)` and the C library's
  `calloc(size_t, size_t)` are two functions.
- **zerocore keeps flags in the low bits of pointers,** which needs allocations aligned to 4 bytes.
  llvm-mos's `malloc` aligns to nothing, so `runtime.c` aligns what it hands out.

The exception flag that compiled code checks after every call is never set in zero mode (a `throw`
ends the program), so `ll2mos` replaces each read of it with zero and the checks disappear. The
flag isn't defined anywhere: a program for which that were wrong wouldn't link.

What the C64 target needs, and how it stays honest:

- **Programs that fit in 50 KB**, with their heap and stack. Hello world is 7 KB; arithmetic on
  `double` alone is 25 KB of software, because the processor has none. The large samples don't fit, so
  the Small samples check the same ground in pieces that do.
- **Tests that the 6502 really runs.** A compiler that optimizes the whole program sees through a
  method that isn't inlined, and did the Small samples' arithmetic at build time until they were
  changed to read their inputs through a static field.
- **VICE for testing.** `run.sh --c64` runs the real `.prg` on an emulated C64 and reads its output and
  exit code from outside: the emulator's monitor logs each character the program prints, and a
  "debug cartridge" ends the run with the exit code.
- **One thread, no files, and the keyboard for input.**

```bash
./run.sh --c64 Refs                 # runs on an emulated C64
./run.sh --c64 --window C64Demo     # shows its screen
```

[Build for the Commodore 64](../how-to/build-for-c64.html) goes further.

## Microcontrollers

A Cortex-M is a 32-bit, little-endian Arm processor, which is what the WebAssembly bitcode already
assumes. So `./build.sh --pico2` (a Cortex-M33, the Raspberry Pi Pico 2's) and `./build.sh --r4` (a
Cortex-M4, the Arduino Uno R4's) take the bitcode as it is:

1. **bflat and NativeAOT-LLVM** compile each sample to bitcode, as for DOS.
2. **`zero-bitcode wasm2arm`** writes the program's data as assembly, as `wasm2i386` does, except that
   read-only segments and string literals go to `.rodata`, which stays in flash. RAM is the scarce
   memory on a microcontroller; flash is several times bigger.
3. **[Arm Toolchain for Embedded](https://github.com/arm/arm-toolchain)**, Arm's clang with picolibc,
   compiles it for the board's processor and links it with `arm/runtime.c`.

The programs run in QEMU, on a board with the same processor, laid out like the real chip. Output,
input, arguments, files and the exit code reach the host through semihosting, so they test like any
other target. The memory protection unit makes flash read-only, because in QEMU it is really RAM and a
write to it would otherwise pass unnoticed.

```bash
./run.sh --pico2 Llama              # the language model, on an emulated Cortex-M33
```

[Build for microcontrollers](../how-to/build-for-microcontrollers.html) goes further.

## UEFI, with no operating system

A UEFI program is a PE file with the Windows calling convention, which bflat already makes for
Windows, so `./build.sh --uefi` is bflat again (`--os uefi`), for x64 or arm64. The firmware loads the
file and calls its entry point with a table of its services. There is no operating system and no C
library, so `efi/` supplies the few C functions zerocore and `lib/` call, on top of those services:

- **`efi/start.c`**: the entry point. It takes the arguments from the UEFI Shell as UTF-8, switches to
  an 8 MB stack (the firmware only promises 128 KB), turns off the firmware's 5-minute watchdog, turns
  on AVX on x64 (the firmware leaves it off, and `-m x86-64-v3` code needs it), and returns `Main`'s
  result as the program's status.
- **`efi/runtime.c`**: memory from the firmware's pool, `write` and `read` (files when the shell
  redirects them, the console otherwise), the monotonic clock from the processor's counter and the
  wall clock from the firmware's, and threads that run to their end when they're created.
- **`efi/machine.c`**: what [`Uefi`](../reference/uefi.html) wraps: graphics, keyboard, files, the
  PC speaker, power. It has the same members as `Dos`, so the Vga game runs on both. It also has
  `fopen` and the other file functions [`File`](../how-to/read-and-write-files.html) calls.
- **`efi/libm/`**: the math functions, from musl. On Windows they come from the system's C library;
  here nothing else would provide them.

```bash
./run.sh --uefi Casts          # in QEMU, on OVMF, the UEFI firmware
./run.sh --uefi --window Vga   # shows the screen
```

[Build for UEFI](../how-to/build-for-uefi.html) covers running it and booting a real PC.

## The GPU (Metal)

`./build.sh --gpu` builds a macOS program whose kernels, its methods marked `[RuntimeExport("Name")]`,
also run on the GPU of an Apple Silicon Mac. The same C# is compiled twice:

1. **For the CPU**, the whole program, as `--mac` does. `lib/Gpu.cs` calls Metal through
   `gpu/metal.m`, which the link adds: buffers, launches, and the kernels' built-ins, which on the CPU
   stand in for the GPU's one thread at a time.
2. **For the GPU**, through the WebAssembly route: bflat and NativeAOT-LLVM compile the program to
   bitcode, with its `[RuntimeExport]` methods whether the program calls them or not.
   **`zero-bitcode ll2air`** turns those into AIR, the LLVM dialect Apple's Metal compiler reads. It
   drops what only the runtime needs (the shadow stack, null checks, GC polls, the throw paths), turns
   the `Gpu.*` calls into Metal's built-ins, barriers and atomics, and gives each pointer the address
   space it points into, since AIR is LLVM from before opaque pointers. Anything else that a GPU can't
   run, such as a call into the runtime or a `double`, is an error that names it. Xcode's `metal` and
   `metallib` then make a library of kernels, which the program carries inside it.

```bash
./build.sh --gpu Gpu     # bin/gpu/Gpu
./run.sh --gpu Gpu
```

It needs Xcode with its Metal Toolchain, and Docker for NativeAOT-LLVM, as on the Commodore 64. The
SDK builds the same way with `<ZeroGpu>Metal</ZeroGpu>`; it links with Xcode's own linker, because the
package's lld can't read the library descriptions in Xcode's newest SDK. AIR isn't documented by
Apple, so a new Xcode can break this target.

[Run code on the GPU](../how-to/run-code-on-the-gpu.html) shows how to write and launch kernels.

## NVIDIA GPUs (CUDA)

`./build.sh --cuda` builds the same kernels for NVIDIA GPUs, into a Linux program. It is experimental:
no kernel has yet run on an NVIDIA GPU. The bitcode is the same as for Metal; **`zero-bitcode
ll2ptx`** rewrites it for LLVM's NVPTX backend instead. The built-ins become NVIDIA's special
registers, the barrier `__syncthreads`, threadgroup memory CUDA's shared memory, and the atomics LLVM's
own. LLVM then compiles it to PTX, which `ptxas` checks, and the program carries it inside. There,
`gpu/cuda.c` gives `lib/Gpu.cs` the same C functions as `gpu/metal.m`, through CUDA's driver API,
which it opens when the program starts: building needs nothing from NVIDIA.

```bash
./build.sh --cuda-ptx Gpu   # the kernels as PTX, checked by ptxas
./build.sh --cuda Gpu       # bin/cuda/Gpu
```

It takes `llc`, `ptxas` and a C compiler from the `PATH`, or from a container
(`docker/cuda.Dockerfile`). The SDK builds it with `<ZeroGpu>Cuda</ZeroGpu>`, which needs only a clang
18 or later with the NVPTX backend: its `cuda.c` comes compiled, and zero-bitcode writes the object
that holds the PTX itself.
