---
title: Build for DOS
index: 6
---

# Build for DOS

`./build.sh --dos` compiles the samples, `lib/` and zerocore to 32-bit DOS programs that run on a 386.
bflat's own compiler can make 32-bit x86 code, but only for a Pentium 4 or later, because it always
uses SSE2 for floating point. So the DOS build starts from the WebAssembly route's LLVM bitcode, which
isn't tied to a processor until the last step. [Run it on DOS](../get-started/dos.html) is the short
version of this guide.

## Get the tools

```bash
./setup.sh --dos
```

This downloads NativeAOT-LLVM (shared with `--wasm`), FreeDOS 1.4 with its mouse driver CuteMouse, and
the HX DOS extender 2.23 into `tools/`, about 75 MB. It also checks for what it can't download:

- **A clang with the x86 target.** The WASI SDK's clang doesn't have it. On macOS, Xcode's is used; on
  Linux, the system's. Set `DOS_CLANG` to choose another.
- **The .NET SDK** (10 or newer), to build `zero-bitcode` (`sdk/ZeroCSharp.Bitcode`), which turns
  the compiler's output into 386 sources.
- **Python 3**, for `dos/run.sh`, which makes the disk image the emulator starts.
- **An emulator:** Bochs or QEMU (`qemu-system-i386`). `brew install bochs` on macOS,
  `sudo apt install bochs` (or `qemu-system-x86`) on Ubuntu.

## Build

```bash
./build.sh --dos           # the DOS samples, into bin/dos/*.exe
./build.sh --dos Casts     # just one
```

Simd and Kernels aren't built: they benchmark SIMD instructions, which a 386 doesn't have. To build a
program of your own, add its name to `DOS_SAMPLES` in `build.sh`.

The build compiles the sample to LLVM bitcode as for WebAssembly, compiles that for a 386 with clang
(`-march=i386`), and links it with `dos/runtime.c` into a 32-bit Windows console program, which the HX
DOS extender runs under DOS. `runtime.c` is the C library there isn't.

After linking, `build.sh` disassembles each program and fails if it finds an instruction a 386 or 387
lacks. That needs `llvm-objdump`; without one it says the check was skipped.

## Run in an emulator

```bash
./run.sh --dos Casts                 # prints the output, exits with the program's code
./run.sh --dos --window Vga          # shows the DOS screen
./run.sh --dos Quiz < answers.txt    # standard input from a file
./test.sh --dos                      # every DOS sample, on an emulated 386 with 4 MB
```

`run.sh --dos` boots FreeDOS from one disk and gives the machine a second disk, `D:`, with the program
(as `PROGRAM.EXE`), the HX runtime and a batch file that runs it. Without `--window` there is no
screen: the output goes to a file on `D:`, which is printed afterwards, and `run.sh` exits with the
program's exit code. A run that hasn't finished after `DOS_TIMEOUT` seconds (default 120) is stopped
and exits with 125. With `--window`, the emulator's window shows the DOS screen and stays at the
prompt: type `PROGRAM` to run it again, and close the window to end.

Standard input from a file or a pipe goes onto `D:` as `IN.TXT`, and the program reads it with `<`.
With `--window`, the program reads the keyboard.

### Bochs or QEMU

Use Bochs to test. Its `i386` processor model refuses instructions a 386 doesn't have. QEMU's `486`
model refuses the Pentium Pro's and SSE instructions but runs the 486's own (`bswap`, `cmpxchg`,
`xadd`), so it can't tell a 386 program from a 486 one. `run.sh` and `test.sh` use Bochs when it is
installed. These variables change the machine:

| Variable | Default | What |
|---|---|---|
| `DOS_EMULATOR` | `bochs` if installed, else `qemu` | the emulator |
| `DOS_CPU` | `i386` (Bochs), `486` (QEMU) | the processor model |
| `DOS_MEMORY` | `4` | the machine's memory, in MB |
| `DOS_IPS` | `50000000` | Bochs only: instructions per second |
| `DOS_BOCHS_DISPLAY` | `sdl2` | Bochs's display library for `--window` |

Bochs's `i386` model limits which instructions run, not how fast. At the default it runs several
times faster than a real 386; `DOS_IPS=6000000` is about a 386DX-33.

## Memory

A program needs 2 MB or more. Each one reserves 256 KB for its stack and 256 KB for the shadow stack
NativeAOT-LLVM's code keeps. Most samples run with 2 MB; Strings needs more for its arenas and runs
with 4 MB. Memory really does run out here, so consider arenas created with
`stopOnOutOfMemory: true` (see [Catch arena mistakes](catch-arena-mistakes.html)).

## Run on a real machine or another emulator

Copy the program together with `HDPMI32.EXE`, `DPMILD32.EXE` and `DKRNL32.DLL` from
`tools/dos/hx-2.23`, and start it through the loader:

```text
DPMILD32 CASTS.EXE
```

Or load `HXLDR32.EXE` once; after that `CASTS.EXE` runs by its name. The mouse needs a mouse driver
loaded in DOS, such as CuteMouse.

The machine needs 2 MB of memory or more, and **a 386 with a 387 math coprocessor, or a 486DX or
later.** All floating point uses 387 instructions and there is no software fallback, so a 386 or 486SX
without a coprocessor isn't supported.

## What's different

- **Failure messages go to standard output,** since DOS's command line can't redirect anything else. A
  failed cast or an index out of range ends the program with exit code 134.
- **Floating point is the 387's,** set to 53-bit precision. `+ - * /` and `Sqrt` on doubles match other
  targets. A `long` above 2^53 converted to `double`, `float` arithmetic, and `Exp`, `Log`, `Pow`,
  `Sin` and the like can differ in the last digits.
- **32-bit pointers and one thread,** as on WebAssembly. [Fibers](use-fibers.html) do work: many
  functions taking turns on that thread, on stacks of 4 KB unless told otherwise.
- **C# can call only the C functions `dos/runtime.c` defines.** `[DllImport("libc")]` of anything else
  fails to link.

## Watch out for

- **Tested on macOS only,** with Xcode's clang (Apple clang 21) and Bochs 3.1. `build.sh --dos` is
  written to work on Linux too but hasn't been run there, and CI doesn't run it.
- **The compiler versions are a set,** as for WebAssembly: the DOS build compiles the same bitcode.

See also: [Draw on the VGA screen](draw-on-vga.html), [How the targets work](../concepts/targets.html),
and [Build for WebAssembly](build-for-webassembly.html).
