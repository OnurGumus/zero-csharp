# Working on this repository

How the samples, `lib/` and `zerocore/` are built and tested. To write programs with the NuGet
packages instead, see the [README](README.md).

## Setup

You need the bflat fork, [OnurGumus/bflat](https://github.com/OnurGumus/bflat) (branch `master`),
built next to this repository. It has no release to download; the
[Install](https://onurgumus.github.io/zero-csharp/get-started/install.html) page shows how to build it.
`build.sh` looks for it in `../bflat/layouts/linux-glibc-<arch>`, or wherever `BFLAT_DIR` points.

- **macOS:** Docker. bflat is a Linux program, so it runs in a Linux container of your Mac's own
  architecture. `./setup.sh` builds the image. `./build.sh --mac` also needs Xcode or its command line
  tools.
- **Linux:** an assembler for zerocore's assembly routines (`sudo apt install binutils`). Cross-compiling
  needs the target's assembler too (`binutils-x86-64-linux-gnu` or `binutils-aarch64-linux-gnu`).
  Layouts built before 2026-11-21, when the fork switched to LLVM 20's lld, need `libtinfo.so.5`:
  rebuild them.
- **WebAssembly:** `./setup.sh --wasm` downloads NativeAOT-LLVM, the WASI SDK and wasmtime into
  `tools/wasm` (about 900 MB). Their versions are pinned in `env.sh` and only work as a set.
- **DOS:** `./setup.sh --dos` downloads FreeDOS and the HX DOS extender into `tools/` and checks for
  clang, the .NET SDK, Python 3 and an emulator. Use Bochs (`brew install bochs`): its `i386` processor refuses
  instructions a 386 doesn't have, and QEMU's `486` doesn't.
- **Commodore 64:** `./setup.sh --c64` downloads llvm-mos, a compiler for the 6502, into `tools/c64`
  (about 750 MB) and checks for the .NET SDK, Python 3 and VICE, the emulator (`brew install vice`).
- **UEFI:** QEMU with the OVMF firmware (`brew install qemu` includes it; on Ubuntu
  `qemu-system-x86 ovmf`, or `qemu-system-arm qemu-efi-aarch64` for arm64), a clang with the Windows
  targets (as for `--win`), and Python 3. `UEFI_FIRMWARE` points at another firmware file.

```bash
./build.sh                    # every sample, for this machine, into bin/
./build.sh Arena              # one sample
./run.sh Arena                # run it (in the container on macOS)

./build.sh --mac              # macOS programs, bin/mac/
./build.sh --win              # Windows x64 programs, bin/win/; ./run.sh --win runs them under Wine
./build.sh --wasm             # bin/wasm/; ./run.sh --wasm runs them with wasmtime
./build.sh --dos              # bin/dos/; ./run.sh --dos runs them under FreeDOS in an emulator
./run.sh --dos --window Vga   # the DOS game, on screen: arrow keys steer, Esc quits
./build.sh --c64              # bin/c64/*.prg, the samples that fit; ./run.sh --c64 runs them in VICE
./run.sh --c64 --window C64Demo  # a C64's screen: text, a sprite, raster bars; any key ends it
./build.sh --uefi             # bin/uefi/*.efi; ./run.sh --uefi runs them in QEMU on OVMF
./run.sh --uefi --window Vga  # the same game, started by the firmware
```

`BFLAT_ARCH=x64` or `arm64` cross-compiles the Linux build. `build.ps1` and `setup.ps1` (building on
Windows) are untested and still use zerolib instead of zerocore.

## How build.sh builds

`zerocore/` and `lib/` are each compiled once into an IL assembly, and each sample references them:

```bash
F="--langversion preview --compiler-feature updated-memory-safety-rules"
bflat build-il $(find zerocore -name '*.cs') -O --stdlib:none --langversion preview \
    -o bin/System.Private.CoreLib.dll
as zerocore/native/arm64.S -o bin/zerocore-arm64.o
bflat build-il lib/*.cs -O --stdlib:none -r bin/System.Private.CoreLib.dll $F \
    --target Shared -o bin/BflatLib.dll
bflat build samples/Arena.cs -r bin/BflatLib.dll -r bin/System.Private.CoreLib.dll \
    --stdlib:none --no-reflection -i libc $F --ldflags bin/zerocore-arm64.o -o bin/Arena
```

The assemblies hold IL, so each sample's build compiles only the parts it uses, for its own `-m` flags:
one `BflatLib.dll` gives NEON code on arm64 and AVX2 code on x64. Folders in `libs/` are built the same
way into `bin/libs/<folder>.dll` and referenced by every sample.

- Pass `-O` to `build-il`. Unlike `bflat build`, it doesn't optimize unless asked.
- A library and the programs using it must be built against the same core library.
- The core library must be named `System.Private.CoreLib`, and builds against it need `--no-reflection`
  and an `-i` for each `[DllImport]` library. [`zerocore/README.md`](zerocore/README.md) explains why.

On top of that, `build.sh` passes `-Os --no-pie` and strips Tiny, gives Simd, Kernels and Llama
`-m x86-64-v3` (x64) or `-m dotprod` (arm64), since zero mode has no run-time CPU detection, and links
arm64 programs with 4 KB pages (`ARM64_MAX_PAGE_SIZE=65536` for 16 KB and 64 KB page kernels such as
Asahi Linux).

## What the fork changes

`build.sh` uses the fork only as a compiler. Compared with upstream bflat 10.0.0-rc.1, it:

- compiles C# 15 (Roslyn 5.12.0), with `--compiler-feature` to turn on the updated memory safety rules
- binds SIMD intrinsics: `AdvSimd` and `Dp` on arm64, `Sse`, `Avx`, `Avx2` and `Fma` on x64
- treats instruction sets beyond the baseline as unsupported unless they're given with `-m`, where
  upstream failed to link on arm64
- writes Mach-O objects with `--os macos -c`, which a Mac's clang links
- ships the .NET 10 GA JIT, since the RC1 JIT miscompiles some static-field stores on x64
- ships LLVM 20's lld, which needs no `libtinfo.so.5`

The fork's zerolib also gained strings, arrays, static reference fields, class casts, `Interlocked`,
`Half` and a WASI build. Those only matter with `--stdlib:zero`, which `build.sh` doesn't use.

## Tests

`./test.sh` builds every sample and checks it. Each must exit with 0 (Casts, Core, Kernels and Strings
exit with their number of failed checks), and those whose output doesn't vary must print exactly what
`tests/expected/` holds. A sample with a `tests/input/<Sample>.txt` reads it as standard input.

```bash
./test.sh            # this machine
./test.sh --wasm     # WebAssembly
./test.sh --dos      # DOS, on an emulated 386 with 4 MB
./test.sh --c64      # Commodore 64, in VICE: the samples that fit in it
./test.sh --mac      # macOS
./test.sh --win      # Windows, under Wine
./test.sh --uefi     # UEFI, in QEMU on OVMF, for BFLAT_ARCH (x64 or arm64)
./test.sh --all      # all but UEFI, plus the other Linux architecture under emulation
./test.sh --update   # rewrite tests/expected after an intended change in output
```

Simd and Kernels print timings, so only their exit codes are checked. So are Arena's and Collections'
on WebAssembly and DOS, where 32-bit pointers change the byte counts they print.

The Small samples (`SmallStrings`, `SmallIntegers`, `SmallObjects`, ...) are tests sized for a machine
with little memory: each covers a piece of what the large samples check and fits in a Commodore 64,
which has 50 KB for a program, its heap and its stack. They run on every target. When you add one,
check that the C64 really does its work: a compiler that optimizes the whole program computes at build
time whatever it can see, so the samples read their inputs through a static field (`s_never`), and
`llvm-nm bin/c64/<Sample>.prg.elf` shows whether the routines you meant to test are in the program.

The guards are tested the other way round. `lib/` stops the program when it catches a mistake, and
`samples/Guards.cs` makes one mistake per run (`./run.sh Guards restore-order`). `test.sh` runs each
case in `tests/expected/Guards.fail` and requires a non-zero exit code and the message on that line. A
new guard needs a case in the sample and a line in that file.

The docs have their own checks: `scripts/check-doc-snippets.py` compares the excerpts in `docs/` with
the regions they quote from `samples/`, and `scripts/build-docs.py` builds the site into `output/`.

For pushes to `master` and for pull requests, CI (`.github/workflows/test.yml`) runs `./test.sh` on
Linux x64 and arm64, Windows under Wine, UEFI x64 and arm64 in QEMU, macOS (compiled on Linux, linked
and run on an Apple Silicon runner) and WebAssembly. It runs the SDK's tests on every host
(`sdk.yml`).

### The samples on .NET

CI's `dotnet` job builds the same samples as ordinary .NET programs and checks them the same way
(`dotnet/README.md` says what that is for). It needs only the .NET SDK 11, no bflat:

```bash
dotnet/test.sh                    # with lib/'s own Task, streams and sockets
dotnet/test.sh --bcl-types        # with .NET's own
dotnet/test.sh --gc-stress Tasks  # one sample, with the GC moving every object
```

A change here can turn that job red without breaking anything in zero mode:

- **A new or changed check answers differently on .NET.** zerocore and `lib/` are meant to answer
  as .NET does, so first see what .NET returns: often the difference is a bug here, and the fix is
  in `lib/`. Where .NET is the one that differs, or can't do it (objects in arenas, a platform's
  cryptography), add a line to `dotnet/known-differences.txt` (and `-bcl.txt`): the sample, the
  check's name, and the reason, with what .NET returned. The file's entries show the forms,
  including a check that differs on one system only.
- **`lib/` code that only .NET's compiler refuses.** .NET 11 wants `Unsafe.As`, `Unsafe.Add` and
  `MemoryMarshal.CreateSpan` inside an `unsafe` block; zerocore doesn't yet.
- **`lib/` code that keeps an object's address.** Zero mode's objects never move and .NET's do.
  The stress step fails for it, with a crash or a wrong answer in a sample that waits or does input
  and output. It moves objects at moments it doesn't choose, so it can pass on one run and fail on
  the next: a failure there is a real bug in how `lib/` runs on .NET, though not always one the
  pull request made. Run the sample again a few times with `--gc-stress` to see it, and look for
  `Unsafe.AsPointer`, a pointer used after its `fixed` block, or a reference kept in native
  memory (`dotnet/README.md`, "Objects move").

## Editor setup

The `.csproj` files and `BflatTest.slnx` exist only for IntelliSense; bflat ignores them.
`Directory.Build.props` compiles every project against zerocore with the same C# 15 settings as
`build.sh`, so completion offers only what zero mode has and the editor reports the same memory safety
errors bflat does. `dotnet build BflatTest.slnx` type-checks everything without bflat.

Each sample has its own namespace (`ArenaSample`, `TinySample`, ...). Avoid names like
`Samples.Arena`: inside that namespace, `Arena` means the namespace, not the `Arena` type.
