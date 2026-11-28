---
title: Build for .NET
index: 33
---

# Build for .NET

A zero program is a small native binary with no runtime, and that's also what makes some jobs hard.
There's no .NET debugger to step through it with, no profiler, no test runner, and no way to hand its
code to the much larger number of people who write ordinary .NET programs. `ZeroTarget=dotnet` builds
the same project as an ordinary .NET program instead, run by .NET's own runtime. The source doesn't
change, so you can debug and profile it with .NET's tools, check that it does what it does natively,
and ship a library that any .NET project can install.

```text
                    one project, one source
                   /                       \
   dotnet build (zero mode)          dotnet build -p:ZeroTarget=dotnet
          |                                   |
   native binary, no runtime          .NET program (net11.0)
   arenas, no garbage collector       .NET's runtime, debugger, profilers
   Linux, macOS, Windows, DOS,        macOS and Linux
   C64, UEFI, boards, WebAssembly     any .NET project can use its libraries
```

It needs the .NET SDK 11 and runs on macOS and Linux. Windows isn't supported yet.

## Build and run

```bash
dotnet build -p:ZeroTarget=dotnet
dotnet bin/Debug/net11.0/MyApp.dll
```

The target has to be known before the project's own properties are read, so pass it on the command
line, as here, or set it in a `Directory.Build.props` next to the project:

```xml
<Project>
  <PropertyGroup>
    <ZeroTarget>dotnet</ZeroTarget>
  </PropertyGroup>
</Project>
```

`dotnet run -p:ZeroTarget=dotnet` and the debugger in your editor work as for any .NET program.

## Two modes: whose `Task`

`lib/` has its own `Task`, streams, sockets, timers and `Console`, with the same names as .NET's,
because that's what lets zero programs use `async` and `await`, files and sockets. On .NET you choose
which ones a project gets:

```text
  ZeroDotnetTypes=Zero (the default)        ZeroDotnetTypes=Net
  ----------------------------------        -------------------
  lib/'s Task, streams, sockets             .NET's Task, streams, sockets
  Task.Run starts a fiber                   Task.Run uses the thread pool
  behaves as the native program does        works with any .NET library
  libraries: F# only, for zero programs     libraries: C# or F#, for anyone
```

- **`Zero`** is the default, and the one to debug with: the program runs as it does natively, with
  `Task.Run` starting a fiber, one fiber running at a time, and a socket letting the others run.
  Its types aren't .NET's, though, so it can't take a `Task` from another .NET library.
- **`Net`** gives the project .NET's own types and keeps the rest of `lib/`: arenas' API, fibers,
  channels, `Catch.Run`, collections. It's further from the native program (`Task.Run` runs in
  parallel, for one), and it's the mode for a library meant for other .NET code.

```bash
dotnet build -p:ZeroTarget=dotnet -p:ZeroDotnetTypes=Net
```

## Libraries

How `lib/` reaches the program decides what can be a library:

| | C# | F# |
|---|---|---|
| `Zero` | `lib/` is compiled into the program; no libraries | `lib/` is one assembly (`ZeroCSharp.Dotnet`); F# libraries for F# zero programs |
| `Net` | `lib/` is one assembly (`ZeroCSharp.Dotnet.Net`) | the same assembly |

C# only lets a project's own types take the place of .NET's types of the same name, so with `Zero`
a C# program compiles `lib/` in. F# takes a referenced assembly's type over .NET's, so F# projects
can share one copy. With `Net` nothing in `lib/` has a .NET type's name, and C# and F# projects
share one copy too.

The SDK adds the package itself. A library packed with `dotnet pack` lists it as a dependency, so a
project that installs the library gets it too:

```bash
dotnet pack -p:ZeroTarget=dotnet -p:ZeroDotnetTypes=Net
```

A library built with `Net` is an ordinary .NET library: a C# console app that doesn't use the zero
SDK at all can install it and call it. A program and the zero libraries it uses must be built in
the same mode. A library built the other way would bring a second copy of `lib/`, with its own
`Arena` type and fiber scheduler, so the SDK stops the build (error ZERO016).

In F#, with `Zero` the `task { }`, `async { }`, `MailboxProcessor` and `arena { }` builders are
zero's, on `lib/`'s tasks and fibers. With `Net` they're FSharp.Core's, on .NET's tasks (`arena { }`
isn't there yet). `[<Safety "why">]` and `[<RequiresUnsafe>]` work in both, and so does the
unsafe-call check (ZERO007).

## What's different on .NET

The program does the same things, with these exceptions. The repository's `dotnet/README.md` lists
every sample check that comes out differently, and why.

- **Arenas don't hold objects.** `AllocateObjectsHere` has no effect: objects go to .NET's garbage
  collector. Arena memory itself (spans, buffers, scratch) is still native memory, freed by the
  arena.
- **Memory problems don't show.** Zero mode never frees an object outside an arena, so a program can
  slowly run out of memory natively. On .NET the collector frees those objects and hides the
  problem. Check memory on the native targets, not here.
- **A throw unwinds.** `catch` and `finally` blocks and `using` disposals between a throw and
  `Catch.Run` run on .NET; zero mode jumps over them. `Catch.Run` also catches what zero mode ends the
  program for, such as an index out of range.
- **Fibers are threads.** Each fiber is a system thread, and only one runs at a time, so the
  program behaves the same. A switch costs about a microsecond, and a call that blocks in .NET or the
  system (`Thread.Sleep`) holds up the other fibers.
- **Cryptography and TLS are .NET's.** Hashes, ciphers, certificates and `SslStream` are the
  platform's own, not `lib/`'s. `SslStream` runs over `lib/`'s sockets.
- **Cultures need opting in.** A program runs in .NET's invariant globalization mode, where strings
  compare by character code as in zero mode. A program that names cultures sets
  `InvariantGlobalization` to `false` in its project, and on Linux then needs ICU installed.

## Objects move

In zero mode an object stays where it was made, so code may keep its address. .NET's collector moves
objects, and an address kept across a move points at other memory: the program compiles, and fails
rarely and far from the cause. `lib/` was changed where it did this. Your own code is yours to check.
On .NET, don't:

- keep a pointer from `Unsafe.AsPointer` to a field or an array element past the statement that uses
  it;
- use a pointer from `fixed` after the `fixed` block;
- store a reference as a number (`Unsafe.As<T, nint>`) and turn it back later.

To find such code, run the program with `ZERO_GC_STRESS=1` in the environment. The collector then
moves every object every few milliseconds, so a kept address shows itself quickly:

```bash
ZERO_GC_STRESS=1 dotnet bin/Debug/net11.0/MyApp.dll
```

See also: [Write F#](write-fsharp.html), [Create a reusable library](create-a-library.html), and
[Debug a program](debug-a-program.html).
