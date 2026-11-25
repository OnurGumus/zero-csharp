---
title: Read environment variables and exit
index: 20
---

# Read environment variables and exit

`System.Environment` has the members of .NET's that make sense without a runtime:
`GetEnvironmentVariable`, `Exit`, `ProcessorCount`, `TickCount64` and `NewLine`. It is part of zerocore,
so it works in every program, on every target. `samples/Env.cs` runs the examples below.

## Read an environment variable

<!-- sample: samples/Env.cs variable -->
```csharp
string value = Environment.GetEnvironmentVariable(args[0]);
if (value == null)
    Console.WriteLine($"{args[0]} isn't set");
else
    Console.WriteLine($"{args[0]}={value}");
```

`GetEnvironmentVariable` returns `null` when the variable isn't set. The value is a new string, allocated
like the result of `a + b`: by default it is never freed, and inside `arena.AllocateObjectsHere()` it goes
in that arena. Read a variable once, at the start, rather than in a loop. The value is decoded from UTF-8;
an invalid byte becomes U+FFFD.

What a program sees on each target:

| Target | Variables |
|---|---|
| Linux, macOS | The process's environment |
| Windows | The process's environment, read as UTF-16, so any character comes through |
| WebAssembly | Only what the runtime passes: none under `./run.sh --wasm`; `wasmtime run --env NAME=value` passes one |
| DOS | DOS's environment (`SET NAME=value`), in the code page, so only ASCII is reliable |
| UEFI | The UEFI Shell's variables (`set name value`); none without the shell |

## End the program from anywhere

<!-- sample: samples/Env.cs exit -->
```csharp
// No newline: Exit writes what Console still holds before it ends.
Console.Write(s_failures == 0 ? "PASS" : "FAIL");
Environment.Exit(s_failures);
```

`Environment.Exit(code)` ends the program as returning `code` from `Main` would, from any depth. What
`Console` holds that isn't written yet (a last `Console.Write` without a newline) is written first, and so are
files the C library buffers. Nothing else runs: arenas aren't freed and `using` blocks don't end, which
is fine when the process is about to end anyway. Exit codes are 0 to 255 everywhere, as on Linux.

## The rest

- **`Environment.ProcessorCount`**: the number of processors, at least 1; 1 on DOS and UEFI. It isn't
  capped, unlike `Parallel.ProcessorCount`, which is the number of threads `Parallel.For` uses.
- **`Environment.TickCount64`**: milliseconds from the monotonic clock, counted from an arbitrary
  start: use it for differences only. Under wasmtime the clock starts with the program, so it can read
  0 at first, where .NET counts from boot. For finer durations, use
  `Clock.Monotonic`, in nanoseconds ([Use the clock](use-the-clock-and-random-numbers.html)).
- **`Environment.NewLine`**: `"\n"` on every target, the line ending `Console.WriteLine` writes. In .NET it is
  `"\r\n"` on Windows; here a program writes the same bytes everywhere, and Windows programs and consoles
  read `"\n"` fine.

See also: [Read input from the user](read-input.html), [Coming from .NET](../get-started/coming-from-dotnet.html).
