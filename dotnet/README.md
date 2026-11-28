# Zero C# programs on .NET

A zero-mode program is compiled to native code against `zerocore/` and `lib/`, with no .NET
runtime. This folder builds the same source as an ordinary .NET program instead, running on
.NET's own runtime and core library. It exists for two reasons:

- **Checking zerocore against .NET.** The samples in `tests/expected` were written for zero mode.
  Running them on .NET shows every place where zerocore's `string`, numbers, dates, collections
  or `System.Numerics` answer differently from the real thing.
- **.NET's tools.** The debugger, profilers and test runners work on the .NET build as on any
  .NET program.

In the SDK it is `dotnet build -p:ZeroTarget=dotnet` (`sdk/ZeroCSharp.Sdk/Sdk/ZeroDotnet.targets`),
with `-p:ZeroDotnetTypes=Net` for the second of the two builds below.

## The two builds

```
dotnet/test.sh               lib/ compiled into the program      (dotnet/Faithful)
dotnet/test.sh --bcl-types   lib/ without what .NET has already  (dotnet/ZeroCompat, dotnet/Sample)
```

**The default** compiles `lib/` into each program as source. Its `Task`, timers, streams, sockets
and `Console` have the names of .NET's, and types in the program's own assembly take the place of
.NET's. So the program behaves as in zero mode: `Task.Run` starts a fiber, a socket lets the
other fibers run, and code with no locks stays correct. Those types are not .NET's, so they
can't be handed to a .NET library that expects a `System.Threading.Tasks.Task`.

Cryptography is the exception. `lib/`'s own hashes, HMAC, HKDF, ciphers (`ChaCha20Poly1305`,
`AesGcm`), random numbers, `RSA`, `ECDsa`, certificates and TLS stay out of this build: .NET's
are the platform's, and a program on .NET gets those. `X25519` and `EncryptedStream` are
`lib/`'s, because .NET has no types by those names. What follows from that:

- `SslStream` is .NET's TLS, running over `lib/`'s sockets (`Faithful/TlsBridge.cs`). It does
  more than `lib/`'s client: TLS 1.2 as well as 1.3, and other key shares. It trusts the
  system's roots, or only those in `SSL_CERT_FILE` when that is set, as `lib/`'s does.
  `scripts/tls-interop.sh --dotnet` runs it against OpenSSL's server.
- .NET's `AesGcm` on macOS takes 16-byte tags only. The Crypto and CryptoErrors samples use
  12-byte ones and stop there, so the list names them for macOS alone; on Linux they run and
  are checked like the rest.
- .NET's certificates, RSA and chain building are each system's own, and answer differently
  from `lib/`'s in places. The list names those checks of the Certificates sample with what
  .NET returned, some for macOS alone (`@macos`). Among them: .NET accepts certificates signed
  with SHA-1, and on Linux with RSA-PSS, which `lib/` refuses; a chain's elements are copies
  of the certificates in .NET.

**`--bcl-types`** (in the SDK, `ZeroDotnetTypes=Net`) leaves out every file of `lib/` that
re-declares a .NET type (`NetTypes.props` lists them), so `Task`, `Stream` and `Socket` are
.NET's. That is for a program that passes them to other .NET code. It is further from zero mode:

- `Task.Run` runs on the thread pool, in parallel with the program, and so do the callbacks of
  .NET's timers.
- An `await`'s continuation comes back to the program's own thread, but not onto a fiber: a call
  that waits as a fiber does (`Channel.Receive`) stops the program there. Do it from a fiber.
- `lib/`'s members that .NET's sockets lack (`NetworkStream.Error`, `TryConnect`) aren't there.

## How lib/ runs on .NET

`lib/`'s code is the same as in zero mode. A few things under it are replaced:

| In zero mode | On .NET |
|---|---|
| Fibers switch stacks (`ZeroFiberSwitch`) | Each fiber is a thread, and they take turns (`ZeroCompat/Fibers.cs`) |
| `throw` jumps back to `Catch.Run` | `Catch.Run`'s mark is a try/catch (`ZeroCompat/Shims.cs`) |
| Fibers of a thread share its thread-local state | That state travels from thread to thread with the turn |
| zerocore's internals (`ObjectAllocator`, exit hooks) | Stand-ins in `ZeroCompat/Shims.cs` |
| glibc's functions, linked in | Linux: glibc itself. macOS: `native/` (glibc's names over macOS's) |

Changes inside `lib/` for .NET are behind `#if ZERO_DOTNET`.

## What differs from zero mode

`known-differences.txt` names every check of the samples that fails on .NET, with the reason,
and the samples that don't run there at all. `dotnet/test.sh` fails when a sample does anything
else: a check that isn't listed fails, or a listed one passes. CI runs it on Linux (the `dotnet`
job of `.github/workflows/test.yml`), with a stress run of the samples that wait and do input
and output, and the TLS cases. In short:

- **Objects are the garbage collector's.** `Arena.AllocateObjectsHere` has no effect, so nothing
  measures "0 bytes on the heap", and the checks for objects used after their scope find nothing.
- **A throw unwinds.** Catch and finally blocks and `using` disposals between the throw and
  `Catch.Run` run on .NET; zero mode jumps over them.
- **`Catch.Run` catches more.** .NET can't tell `throw null` from a null dereference, so what
  zero mode ends the program for (a bad index, a failed cast) comes back as an exception.
- **A fiber that blocks in the system blocks them all.** `lib/`'s own sockets and files yield;
  a call that .NET or the system blocks in (`Thread.Sleep`) holds the turn.
- **Cultures: invariant mode by default, ICU for a program that names them.** A program runs in
  .NET's invariant globalization mode unless its project sets `InvariantGlobalization` to
  `false`. There strings compare by character code, as zerocore's invariant culture does, and no
  ICU is needed; but naming a culture (`new CultureInfo("de-DE")`) throws
  `CultureNotFoundException`. With ICU, named cultures behave as zerocore's (whose tables come
  from ICU's data), but the invariant culture compares strings by language rules ("apple" before
  "Banana"; zero mode puts "Banana" first), Linux machines need ICU installed (slim container
  images leave it out), and results can vary with its version. Either way the program starts in
  the invariant culture, as in zero mode, not the user's. The test builds turn ICU on only for
  the samples that name cultures: Cultures, Locales and Sorting.

## Objects move

In zero mode an object stays where it was made, so code may keep its address. .NET's collector
moves objects, and an address kept across a move points at other memory. Such code compiles and
usually runs; it fails rarely, and far from the cause.

`lib/` was read through for this and changed where it kept an address (records and lock state
now live in native memory, references kept in native memory are GC handles, buffers are pinned
for as long as native code uses them). The handles for `RefList` and `RefDict` items come from
their arena, which frees them with the memory that holds them (`Restore`, `Reset`, `Destroy`).
A removed or overwritten item's object is let go at once, and its handle is reused by the same
collection, so one never holds more handles than the most items it has held at once. A program's own code is its own to check. On .NET, don't:

- keep what `Unsafe.AsPointer` gives for a field of an object, or an array's element, past the
  statement that uses it;
- use a pointer from `fixed` after the `fixed` block;
- store a reference as a number (`Unsafe.As<T, nint>`) and turn it back later;
- give `Arena.FromBuffer` or `Llama.Create` a span over a plain array. A `stackalloc`,
  `NativeMemory`, a static struct's fixed buffer, or an array passed to `Arena.FromBuffer` as
  an array (which pins it) are fine.

Warning CS8500 (a pointer to something that holds references) points at one form of this, and
is left on for a project's own code.

**To test a program:** run it with `ZERO_GC_STRESS=1` in the environment. A thread then makes the
collector collect and compact every couple of milliseconds, so every object moves within
milliseconds of being made. `dotnet/test.sh --gc-stress` runs the samples that way. On `lib/`
as it was before its changes, that run crashed three samples (Catches, Network, Tasks) which pass
with them. It finds what the program's run exercises, not everything.

One thing still rests on .NET's behaviour rather than its promises: a string `lib/` builds in
arena memory (`Str.Concat(arena, ...)`, `StrBuilder`) is an object outside the collector's heap.
The collector leaves such objects alone, as it does .NET's own frozen ones.

## F#

An F# project builds for .NET with the same `-p:ZeroTarget=dotnet`, and F# libraries and the
programs that use them do too. They don't compile `lib/` in, as C# programs do: they reference it
as one assembly from a package, which a packed F# library then lists as a dependency.

| | lib/'s own `Task` (default) | `-p:ZeroDotnetTypes=Net` |
|---|---|---|
| Package | `ZeroCSharp.Dotnet` | `ZeroCSharp.Dotnet.Net` |
| `lib/` | `ZeroLib.dll` (`dotnet/ZeroLib`) | `ZeroLib.Net.dll` (`dotnet/ZeroLib.Net`) |
| `task { }`, `async { }`, `MailboxProcessor` | zero's, on `lib/`'s tasks and fibers (`FSharp.Control.dll`, `dotnet/ZeroLib.FSharp`) | FSharp.Core's, on .NET's tasks |
| `arena { }` | yes | not yet (it's built on zero's `async`) |
| A packed library works in | F# zero programs built the same way | any .NET project, C# included |
| C# zero projects | compile `lib/` in; no libraries | use the same package: C# libraries, and C# and F# zero libraries in one program |

F# takes a referenced assembly's type over .NET's of the same name, where C# calls it ambiguous
(CS0433). So with `lib/`'s own types, an F# library and the program using it share `lib/`'s
`Task`, but a C# project that installs the library finds two. With `Net` nothing of `lib/` has a
.NET type's name, so a library built that way is an ordinary .NET library that also has `lib/`'s
arenas, fibers and channels: build a library meant for any .NET project that way.

A program and the zero libraries it uses must be built the same way: one built otherwise would
bring the other `lib/`, with its own `Arena` and fiber scheduler, and the SDK stops the build
(ZERO016). C# projects with `lib/`'s own types have `lib/` compiled in, so they can't use these
libraries; with `Net` they reference the same `ZeroCSharp.Dotnet.Net` as F#, compile only a
small startup (their own `DllImport`s, and zero's rule for continuations), and can be libraries.

FSharp.Core is .NET's own. zero's two unsafe-code marks come with the package: `[<RequiresUnsafe>]`
as in zero mode, and `[<Safety "why">]` as an attribute of its own (zero's FSharp.Core makes it
`NoCompilerInlining` with the reason, which .NET's doesn't take; the ZERO007 check reads both).
F# projects here make no reference assemblies, so that switching `ZeroDotnetTypes` rebuilds what
uses a library. The SDK test `dotnet_fsharp` builds an F# library and program for zero mode, for
.NET and for .NET with `Net`, checks all three print the same, installs the `Net` library in a
plain C# project, checks that a program built the other way is refused, and builds a C# zero
program with `Net` that uses the F# library and a C# zero library.

## Not yet

- Windows (the functions `win/runtime.c` gives a zero-mode program would have to be a library
  for .NET to load).
- C# libraries, and C# projects that reference zero libraries, with `lib/`'s own types
  (SDK: ZERO016): there a C# program has `lib/` compiled in, so a library's `lib/` types would be
  other types. With `ZeroDotnetTypes=Net` they work.
- `arena { }` with `ZeroDotnetTypes=Net`.
- The Links sample: .NET's runtime can't load two structs that refer to each other through a
  generic struct (`Town` holds a `Handle<Road>`, `Road` a `Handle<Town>`).
