---
title: Your first program
index: 3
---

# Your first program

The repository's programs live in `samples/`, one file each. `Hello` prints a line, and you don't
need the repository to build it. This is all of it:

<!-- sample: samples/Hello.cs file -->
```csharp
// Example: zero-mode "Hello World" through Console (~13 KB, no .NET runtime).
// Build: ./build.sh Hello
//
// lib/ puts Console in zero mode: Console.WriteLine buffers its text and
// writes it out as UTF-8, without the .NET runtime behind it.

using System;

namespace HelloSample;

public static class Program
{
    public static void Main()
    {
        Console.WriteLine("Hello from zero-mode!");
    }
}
```

`Console.WriteLine` is the call you know from .NET, but no .NET runtime runs behind it. Zero mode
has none: the program is compiled to machine code, and `lib/`, which every zero-mode program is built
with, supplies `Console`. It holds the text in a buffer, encodes it as UTF-8 and writes each line to
standard output in one call. [Coming from .NET](coming-from-dotnet.html) lists what else `Console`
has, and what it doesn't.

## Build and run it

With the .NET 10 SDK and the templates ([Install with dotnet](install-with-dotnet.html)), make a
project and put the program above in its `Program.cs`, in place of what's there:

```bash
dotnet new zero -o Hello
cd Hello
# Program.cs: the program above
dotnet build
bin/Debug/zero03/Hello
```

```text
Hello from zero-mode!
```

`dotnet build` compiles the program for the machine you're on, on Linux, Windows (where it's
`bin\Debug\zero03\Hello.exe`) or a Mac with Apple Silicon. `-p:ZeroTarget=` builds for another
one: `linux-x64`, `linux-arm64`, `win-x64` or `osx-arm64`.

With the [repository](install.html) instead, `./build.sh Hello` and `./run.sh Hello` do the same.
`build.sh` compiles the core library (`zerocore/`) and the library (`lib/`) from source first, and it
also builds for WebAssembly and DOS (next page).

## What you built

```bash
ls -l bin/Debug/zero03/Hello
```

A Debug build is unoptimized and carries debug information, for debugging. `dotnet build -c Release`
optimizes it and leaves out the debug information and the symbol table. Either needs nothing
installed to run: no .NET, no runtime, only the C library every system has. In the repository,
`./build.sh Hello` builds it optimized for size: 13 KB on Linux.

## Calling C directly

About 1 KB of that is `Console`'s buffer and its UTF-8 encoding. A program can do without them and
call the C library itself. `Minimal` prints the same line through `puts`:

<!-- sample: samples/Minimal.cs file -->
```csharp
// Example: Minimal zero-mode "Hello World" (~12 KB, no .NET runtime).
// Build: ./build.sh Minimal
//
// Under the updated memory safety rules, creating a pointer (stackalloc) is
// safe; only the call that reads through it needs an unsafe block.

using System.Runtime.InteropServices;

namespace MinimalSample;

public static class Program
{
    [DllImport("libc")]
    static unsafe extern int puts(byte* s);

    public static int Main()
    {
        byte* msg = stackalloc byte[] {
            (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o',
            (byte)' ', (byte)'f', (byte)'r', (byte)'o', (byte)'m',
            (byte)' ', (byte)'z', (byte)'e', (byte)'r', (byte)'o',
            (byte)'-', (byte)'m', (byte)'o', (byte)'d', (byte)'e', (byte)'!', 0
        };
        unsafe { puts(msg); } // SAFETY: msg is NUL-terminated.
        return 0;
    }
}
```

That's possible because a zero-mode program is a native program: `[DllImport("libc")]` binds to the C
function when the program is linked, with no marshalling layer in between. The message is a
`stackalloc` byte array, which is safe to create; only the call that reads through the pointer needs
an `unsafe` block, with a comment saying why it's safe. That is C# 15's
[updated memory safety rules](../concepts/safety-rules.html) at work. `Tiny`, which prints `Hi` the
same way, is the smallest sample: 12 KB.

Most programs use `Console` and the rest of `lib/`: arenas, strings and collections. The
[tutorial](../tutorial/index.html) starts from there.

## Adding a program of your own

`build.sh` builds the programs it knows by name. To add one:

1. Create `samples/Greet.cs`. Give it its own namespace (`namespace GreetSample;`): the editor
   project compiles all the samples together.
2. Add `Greet` to `ALL_SAMPLES` at the top of `build.sh`, and to `DOS_SAMPLES` if it should build
   for DOS.
3. `./build.sh Greet && ./run.sh Greet`.

Next: the same program [on WebAssembly](webassembly.html).
