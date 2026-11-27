---
title: Install with dotnet
index: 1
---

# Install with dotnet

With the .NET SDK, you can build zero-mode programs without cloning this repository. Three NuGet
packages do it: a project SDK (`ZeroCSharp.Sdk`), the bflat compiler for your machine
(`ZeroCSharp.Bflat.linux-x64`, `linux-arm64`, `win-x64` or `osx-arm64`, downloaded by the SDK), and the project templates
(`ZeroCSharp.Templates`). `dotnet build` then makes a native program, and your editor understands
the code.

## What you need

- **The .NET 10 SDK, 10.0.400 or newer** (`dotnet --version`). Nothing else: the packages bring the
  compiler and the linker. Linux distributions' own `dotnet` packages (`apt install dotnet-sdk-10.0`
  on Ubuntu, Fedora's and RHEL's) are the older 10.0.1xx line, and the build stops with error
  `ZERO014`. Install Microsoft's SDK beside it and put it first on `PATH`:

  ```bash
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
  export PATH="$HOME/.dotnet:$PATH"
  ```
- **Linux (x64 or arm64), Windows (x64) or a Mac with Apple Silicon to build programs.** Each builds
  for all four targets: Linux x64, Linux arm64, Windows x64 and macOS on Apple Silicon. The packages are
  tested on GitHub's Windows and macOS runners, and on Ubuntu 22.04 and 24.04, Debian 12 and Fedora 41
  with nothing but the .NET SDK installed. No Xcode is needed on a Mac.
- **On an Intel Mac,** everything except the last step works: the editor, compiling, libraries and
  `dotnet pack`. Building a program stops with an error that says so. For WebAssembly and DOS,
  [install from the repository](install.html).

## Make a program

```bash
dotnet new install ZeroCSharp.Templates
dotnet new zero -o Hello
cd Hello
dotnet build
```

`dotnet new zero` makes a project, with a `.vscode` folder for debugging it in VS Code
([Debug a program](../how-to/debug-a-program.html)), and this program, which reads a name into a
buffer on the stack and greets it:

<!-- sample: sdk/ZeroCSharp.Templates/content/zero/Program.cs file -->
```csharp
// A zero-mode program: no .NET runtime and no garbage collector. Console.Write
// and In.ReadLine write and read without allocating; the name is read into a
// stack buffer.
using System;

Console.Write("What's your name? ");
Span<char> buffer = stackalloc char[64];
int length = In.ReadLine(buffer);
// length is -1 at the end of input, and more than the buffer holds when
// the line didn't fit: then keep what did.
ReadOnlySpan<char> name = length switch
{
    <= 0 => "world",
    _ when length > buffer.Length => buffer,
    _ => buffer.Slice(0, length),
};
Console.Write("Hello, ");
Console.Write(name);
Console.WriteLine("!");
```

`dotnet build` puts the native program in `bin/Debug/zero03/`, unoptimized for debugging
(`-c Release` optimizes it); `dotnet run` builds and runs it:

```bash
echo Ann | bin/Debug/zero03/Hello
echo Ann | dotnet run
```

On Windows, in PowerShell:

```powershell
echo Ann | .\bin\Debug\zero03\Hello.exe
```

```text
What's your name? Hello, Ann!
```

`zero03` is the project's target framework: zero mode, with the library format of SDK 0.3. It keeps
ordinary .NET packages out, because they need .NET's libraries, which a zero program doesn't have.
Libraries packed with 0.1 or 0.2 (`zero01`, `zero02`) need re-packing for 0.3. That's the last
forced one: from 0.3 on, the framework changes only when the library format breaks, not with every
release.

## Build for another target

The `ZeroTarget` property picks what to build for; by default it's the machine you build on. A
program for a chosen target goes in a folder named after it, as `dotnet build -r` does for .NET, so
programs for several targets sit side by side:

```bash
dotnet build -p:ZeroTarget=win-x64        # bin/Debug/zero03/win-x64/Hello.exe
dotnet build -p:ZeroTarget=linux-arm64    # bin/Debug/zero03/linux-arm64/Hello
dotnet build -p:ZeroTarget=linux-x64      # from Windows: bin\Debug\zero03\linux-x64\Hello
dotnet build -p:ZeroTarget=osx-arm64      # bin/Debug/zero03/osx-arm64/Hello, for Apple Silicon Macs
dotnet build -p:ZeroTarget=iossimulator-arm64  # bin/Debug/zero03/iossimulator-arm64/Hello, for the iOS simulator
dotnet build -p:ZeroTarget=android-arm64  # bin/Debug/zero03/android-arm64/Hello, for Android
dotnet build -p:ZeroTarget=uefi-x64       # bin/Debug/zero03/uefi-x64/Hello.efi, no OS needed
dotnet build -p:ZeroTarget=c64            # bin/Debug/zero03/c64/Hello.prg, for a Commodore 64
dotnet build -p:ZeroTarget=pico2          # bin/Debug/zero03/pico2/Hello.uf2, for a Raspberry Pi Pico 2
```

`uefi-x64` and `uefi-arm64` make a program the PC's firmware starts by itself, with no operating
system: [Build for UEFI](../how-to/build-for-uefi.html) shows how to run one in QEMU or boot a PC
with it.

`c64` makes a program for the Commodore 64. Its first build downloads two compilers into a cache in
your user folder, and on a Mac it needs Docker: see
[Build for the Commodore 64](../how-to/build-for-c64.html#with-dotnet-build).

`pico2` and `r4` make a program for a microcontroller, a Raspberry Pi Pico 2 or an Arduino Uno R4,
the same way, and `dotnet run` runs it in QEMU. `dotnet new zeropico` starts such a project: see
[Build for microcontrollers](../how-to/build-for-microcontrollers.html#with-dotnet-build).

`iossimulator-arm64` makes a console program for the iOS simulator on an Apple Silicon Mac, and
`dotnet run` runs it there: see [The iOS simulator](#the-ios-simulator) below.

`android-arm64` makes a command-line program for Android on arm64, and `dotnet run` runs it on a
phone or emulator through adb: see [Android](#android) below.

`dotnet clean` removes all of them.

A macOS program needs no Mac and no Xcode to build: bflat compiles it, and the package's lld links it
against a list of macOS's C functions that the SDK carries (`libSystem.tbd`). It runs on macOS 12 or
newer on Apple Silicon, with an ad-hoc signature, which is what a Mac needs to run a program built
there or copied over without a quarantine flag (a browser download gets one: `xattr -d
com.apple.quarantine Hello` clears it). It can call macOS's C library (libc, libm, pthreads): the
list has what zerocore and `lib/` use and the C functions programs commonly import (stdio, stdlib,
string, time, unistd, dlfcn, math). A C function it lacks fails the link with
`undefined symbol: _name`. List it in a `.tbd` of your own, with libSystem's name, and add that to the
link with `ZeroNativeInput`:

```yaml
--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-macos ]
install-name:    '/usr/lib/libSystem.B.dylib'
exports:
  - targets:     [ arm64-macos ]
    symbols:     [ _getline, _wcslen ]
...
```

```xml
<ItemGroup>
  <ZeroNativeInput Include="more-libc.tbd" />
</ItemGroup>
```

Other libraries work the same way, with their own `.tbd` or `.dylib`. On every target, a project can
also name a C library with a `ZeroLinkLibrary` item, which programs using the project inherit, packed
or not: see [Make a game window](../how-to/make-a-game-window.html), which uses SDL 3 that way.

| Property | Values | Default |
|---|---|---|
| `ZeroTarget` | `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`, `iossimulator-arm64`, `android-arm64`, `uefi-x64`, `uefi-arm64`, `c64`, `pico2`, `r4` | the build machine's |
| `ZeroIosMinimum` | the oldest iOS an `iossimulator-arm64` program starts on | `17.0` |
| `ZeroIosSimulator` | the simulator `dotnet run` runs an `iossimulator-arm64` program in, by name (`iPhone 16`) or UDID | the booted one, else the first iPhone |
| `ZeroAdb` | the adb `dotnet run` runs an `android-arm64` program through | `ANDROID_HOME`'s, else Android Studio's, else `adb` on `PATH` |
| `ZeroOptimizeFor` | `None` (bflat's `-O0`), `Speed`, or `Size` (bflat's `-Os`) | `None` in Debug, `Speed` otherwise |
| `ZeroStripSymbols` | `true`: no debug information and no symbol table, about half of a Linux or macOS program; `false` keeps them for a debugger | `false` in Debug, `true` otherwise |
| `ZeroInstructionSet` | passed to bflat as `-m`, e.g. `x86-64-v3` or `dotprod` (see [Use SIMD](../how-to/use-simd.html)) | none |
| `ZeroDllImports` | more libraries whose `[DllImport]`s are bound when linking, besides `libc` and `libm.so.6`, which always are | none |
| `ZeroLibraryPath` | folders, separated by `;`, searched first for the C libraries `ZeroLinkLibrary` names (see [Make a game window](../how-to/make-a-game-window.html)) | none |
| `NativeLib` | `Shared`, with `OutputType` `Library`: a native library in `native/` instead of a zero library (see [Build a native library](../how-to/build-a-native-library.html)) | none |

The build log shows bflat's arguments on a `bflat arguments:` line (`dotnet build -v:n`).

Set them on the command line with `-p:`, or in the project file. A Debug build (what `dotnet build`
and `dotnet run` make) isn't optimized, so a debugger stops on every line and shows every variable
([Debug a program](../how-to/debug-a-program.html)); `-c Release` builds an optimized program, the one
to time and ship. `dotnet publish` builds Release, and copies the program to the publish folder. It
needs nothing next to it, unless a C library it links with has a file for run time, like SDL's
`SDL3.dll` on Windows: that's copied too.

## Call C functions

`[DllImport]` and `[LibraryImport]` both call C functions, bound when the program links. A
`[LibraryImport]` method is `static partial`, as in .NET, and the SDK's source generator writes it as
a `[DllImport]` of the same function:

<!-- sample: sdk/test/fixtures/libraryimport/Program.cs imports -->
```csharp
[LibraryImport("libc", EntryPoint = "abs")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
public static partial int Abs(int value);

[LibraryImport("libc")]
internal static safe partial nuint strlen(byte* s);
```

There is no marshalling: what the method takes and returns is what C sees. That's numbers other than
`bool` and `char`, pointers, function pointers, enums, and structs of these. A
`string`, `bool` or `ref` parameter, `SetLastError` or `[MarshalAs]` is an error, `ZERO013`; a generic
method, or one in a type that isn't `partial`, is `ZERO012`. A method marked neither `safe` nor
`unsafe` is safe to call, as in .NET.

## The iOS simulator

An `iossimulator-arm64` program is built the same way as a macOS one, on any machine: iOS runs the
same arm64 code, and the package's lld links it against the same list of C functions, which names
both systems. It's a console program, with no app bundle, which the simulator runs the way test runners
start programs there. On an Apple Silicon Mac with Xcode's iOS platform, `dotnet run` runs it: in the
simulator that is booted, or else in the first iPhone simulator, which it boots, through `xcrun
simctl spawn`. The program starts in the project's folder, as with the other targets, gets its
arguments, and its exit code is `dotnet run`'s:

```bash
dotnet run -p:ZeroTarget=iossimulator-arm64 -- some arguments
```

A simulator program is a Mac process with iOS's C library: passing there shows that the code runs on
iOS, not that it fits an iPhone, where only a signed app runs, inside its own folder, with a 1 MB
stack on the main thread. Programs only: no native libraries (`NativeLib=Shared`) and no `ZeroGpu`.

## Android

An `android-arm64` program is built on any machine, as a Linux one is: bflat links it against
Android's C library, Bionic, which the bflat package carries, so the Android NDK isn't needed. It's a
command-line program, the kind `adb shell` runs, with no app around it; it's position-independent, as
Android requires, and aligned for the 16 KB pages of newer phones. `dotnet run` runs it on the phone
(with USB debugging on) or emulator that adb finds, after copying it to `/data/local/tmp/zero` there,
where adb may run programs. Its arguments, standard input and output, and exit code pass through:

```bash
dotnet run -p:ZeroTarget=android-arm64 -- some arguments
```

The program runs in `/data/local/tmp/zero` on the device, so a relative path names a file there, not
in the project. adb comes from `ANDROID_HOME`, or else the folder Android Studio installs it in, or
else `PATH`; `ZeroAdb` names another, and adb's own `ANDROID_SERIAL` picks one of several devices.
Without adb or a device, `dotnet run` says which is missing. On Windows, `dotnet run` passes an
argument with a space in it as two. Programs only: no native libraries (`NativeLib=Shared`) and no
`ZeroGpu`.

## What dotnet build does

It compiles your code twice. First the C# compiler compiles it against zerocore and `lib/`, the way
your editor sees it, into an IL `.dll`: this is where C# errors come from, and `DateTime` or any other
.NET API that zero mode doesn't have is an error here. Then bflat compiles the same sources into the
native program, with zerocore, `lib/` and any [zero libraries](../how-to/create-a-library.html) the
project references. Both compile with the project's `LangVersion`, `Features` and `DefineConstants`.
bflat runs again only when a source, a referenced library or a property changed.

The target framework is always the SDK's: a project that sets `TargetFramework` gets error ZERO001.

The compiler is pinned to the one bflat uses (Roslyn 5.12), so C# 15's unions and the
[memory safety rules](../concepts/safety-rules.html) work in `dotnet build` whatever your SDK's own
compiler is.

## Editors

Editors check code with the compiler they bundle, not the pinned one, so an editor whose compiler is
older than Roslyn 5.12 can mark C# 15 features as errors that `dotnet build` accepts, and a newer one
may treat preview features a little differently. For example, the C# extension for VS Code, version
11.9.41, bundles Roslyn 5.14.

VS Code with the C# extension has been checked on a Mac and works with these projects, including the
[debugging setup](../how-to/debug-a-program.html) the templates bring. Other editors,
and VS Code on Linux and Windows, haven't been checked yet. If yours disagrees with `dotnet build`,
`dotnet build` is right.

## Next

- [Your first program](first-program.html) goes through what a zero-mode program can and can't do,
  with the repository's build.
- [Create a reusable library](../how-to/create-a-library.html): `dotnet new zerolib`, references and
  packages.
- [Write a program in F#](../how-to/write-fsharp.html): `dotnet new zero -lang F#` makes an F#
  project, built the same way.
- [Run a language model](../how-to/run-a-language-model.html): `dotnet new zerollama` makes a
  program that tells stories with a small language model compiled into it.
