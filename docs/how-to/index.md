---
title: Task guides
index: 0
---

# Task guides

Each guide reaches one goal: the steps, the code, and what to watch out for. If you haven't built a
program yet, start with [Get started](../get-started/index.html); for a longer walk through one program,
the [tutorial](../tutorial/index.html) builds a quiz step by step.

## Languages

- [Write a program in F#](write-fsharp.html): an `.fsproj` with the same SDK, `printf`, lists,
  sequences, maps and sets, libraries and native libraries, the memory to watch, and what's missing.

## Text and input

- [Read input from the user](read-input.html): read a line into a buffer with `In.ReadLine`, and handle
  the end of input, lines that don't fit, and numbers; or read raw bytes with `In.Read`.
- [Format text](format-text.html): `$"..."` with alignment and number formats, written to stdout, into an
  arena string or into a `StrBuilder`.
- [Keep a string](keep-a-string.html): copy a line or a scratch result into an arena that lives long
  enough, and collect such strings in a list or map.
- [Read and write files](read-and-write-files.html): whole files in one call, an open file read and
  written piece by piece, and what differs on each target.
- [Work with paths and directories](work-with-paths-and-directories.html): the parts of a path,
  `Path.Combine` and `Normalize`; creating, listing and deleting directories.
- [Use streams](use-streams.html): `Stream`, `MemoryStream` and `FileStream`, as in .NET, with
  buffers of a size you choose, from an arena.
- [Use the network](use-the-network.html): TCP and UDP sockets, `TcpListener`, `TcpClient` and
  `Dns`, as in .NET, with a fiber per connection.
- [Use cryptography](use-cryptography.html): SHA-2, HMAC, HKDF, ChaCha20-Poly1305, AES-GCM and
  X25519 with .NET's names, random bytes from the system, an encrypted stream over a connection, and
  checking signatures and certificate chains.
- [Connect with TLS (HTTPS)](use-https.html): `SslStream` over a connection, as in .NET: a TLS 1.3
  handshake that checks the server's certificate, then encrypted reads and writes.
- [Recover from errors](recover-from-errors.html): `Catch.Run` gets back what a `throw` threw, and
  closes the files and sockets left open, instead of the program ending.
- [Work with dates and times](work-with-dates-and-times.html): `DateTime`, `DateTimeOffset`,
  `DateOnly`, `TimeOnly`, `TimeSpan` and time zones, as in .NET, with local time from the system.
- [Use the clock and random numbers](use-the-clock-and-random-numbers.html): measure a duration, tell
  the date and time, and roll dice with numbers that are the same on every target.
- [Read environment variables and exit](use-the-environment.html): `Environment.GetEnvironmentVariable`,
  `Environment.Exit` from anywhere, the processor count and `NewLine`.

## Memory and collections

- [Choose a collection](choose-a-collection.html): `List`, `Dict`, `RefList`, `StrDict` or `RefDict`,
  keys for objects, and copying a collection into another arena.
- [Query data without allocating](query-data.html): `Where`, `Select`, `Sum` and query syntax over
  spans with `lib/Seq.cs`, tuples instead of anonymous types, and checking that a loop allocates
  nothing.
- [Match text with regular expressions](match-patterns.html): compile a pattern into an arena, find
  matches and groups without allocating, and what the subset can't say.
- [Pass structs by reference](pass-structs-by-ref.html): allocate a struct in an arena, pass it with
  `ref` and `in`, return a ref, and keep the ref from outliving a list's buffer or an arena scope.
- [Catch arena mistakes](catch-arena-mistakes.html): turn on `Arena.DebugChecks`, name your arenas, and
  see what the library stops the program for.
- [Free memory yourself](free-memory-yourself.html): `NativeMemory.Alloc` and `Free` for a block with
  its own lifetime, a struct that frees it with `using`, and what nothing checks.

## Targets

- [Build for WebAssembly](build-for-webassembly.html): the toolchain, building and running a WASI
  module, and what differs from the native build.
- [Build for DOS](build-for-dos.html): the toolchain, the emulators, memory, input from a file, and
  running a program on a real machine.
- [Draw on the VGA screen](draw-on-vga.html): graphics mode, colors, drawing a frame without flicker,
  and reading the keyboard under DOS.
- [Build for the Commodore 64](build-for-c64.html): the toolchain, what fits in 50 KB and what each
  feature costs, the screen and sprites, a game, and running a program in VICE.
- [Build for UEFI](build-for-uefi.html): programs the firmware starts with no operating system, running
  them in QEMU, and booting a real PC from a USB stick.
- [Build for microcontrollers](build-for-microcontrollers.html): the Raspberry Pi Pico 2 and the
  Arduino Uno R4, in QEMU; what fits in their RAM and flash, and why `float` beats `double` there.
- [Build for .NET](build-for-dotnet.html): the same project as an ordinary .NET program, for .NET's
  debugger and profilers, two modes for whose `Task` it gets, libraries any .NET project can use,
  and what differs.

## Windows and games

- [Make a game window](make-a-game-window.html): a window, drawing and the keyboard with SDL 3 from
  NuGet, shipping it, and bindings for another C library with `ZeroLinkLibrary`.

## Speed

- [Use SIMD](use-simd.html): `Vector128` and `Vector256` without run-time CPU detection, the `-m` flags,
  and the ready-made kernels in `lib/Kernels.cs`.
- [Run a loop in parallel](run-in-parallel.html): `Parallel.For` over every processor, sharing a total
  with `Interlocked`, and one arena per thread.
- [Work with tensors and matrices](use-tensors.html): `Tensor<T>` and `TensorSpan<T>` as in .NET,
  broadcasting, views that share memory, matrix products, and freeing the temporaries.
- [Run green threads](use-fibers.html): many fibers on one thread, each on its own stack, taking
  turns; channels between them, sleeping, and what they share.
- [Run code on the GPU](run-code-on-the-gpu.html): C# kernels on an Apple Silicon Mac's GPU through
  Metal, threadgroup memory and barriers, 2D grids, atomics, and when a launch is worth it.
- [Run a language model](run-a-language-model.html): a small Llama-style model, compiled into the
  program, telling stories on every target, DOS and UEFI included.

## Organizing code

- [Create a reusable library](create-a-library.html): a folder in `libs/` that every program can use,
  how it's built, and how to design its API for zero mode.
- [Build a native library](build-a-native-library.html): a `.so`, `.dylib` or `.dll` that .NET
  (`[DllImport]`) and C programs call, its exports, and the limits that carry over.

## Debugging

- [Debug a program](debug-a-program.html): breakpoints and variables in VS Code with CodeLLDB or on the
  command line, unoptimized Debug builds, and strings and spans shown as text.
