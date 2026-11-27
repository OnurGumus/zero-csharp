---
title: Coming from .NET
index: 7
---

# Coming from .NET

If you write C# for .NET, the language here is the one you know: classes, structs, generics,
interfaces, records, pattern matching, spans, lambdas, C# 15. What's gone is the runtime under it
and nearly all of the base class library. This page maps what you reach for in .NET to what you use
instead, and lists what compiles as usual but behaves differently.

## What to use instead

| In .NET | Here | More |
|---|---|---|
| `Console.Write`, `Console.WriteLine` | The same; `Console.WriteLine($"...")` writes each hole straight to stdout, without building a string | [Format text](../how-to/format-text.html) |
| `Console.ReadLine()` | The same, a new string never freed; in a loop, `In.ReadLine(buffer)` into a `Span<char>` you provide | [Read input](../how-to/read-input.html) |
| `Console.ReadKey`, `Console.KeyAvailable` | The same, on every target, the C64 and DOS included | [Read input](../how-to/read-input.html#read-keys-one-at-a-time) |
| `Console.OpenStandardInput().Read` | `In.Read(bytes)` into a `Span<byte>` you provide | [Read input](../how-to/read-input.html#read-bytes-instead-of-lines) |
| `int.Parse`, `double.TryParse` | `Str.TryParse(span, out value)` | [Read input](../how-to/read-input.html) |
| `string.Format`, `$"..."` kept as a string | The same, with .NET's format strings, each string never freed; in a loop, `Str.Format(arena, $"...")`, `StrBuilder`, or `TryFormat` into a span | [Format text](../how-to/format-text.html#nets-format-strings-tostring-string.format-and-tryformat) |
| `StringBuilder` | The same: `Append`, `AppendFormat`, `Insert`, `Remove`, `Replace`, the indexer, `Length` and `ToString`; it grows with `new` and is never freed, so for text built over and over use `StrBuilder` in an arena or `ScratchText` in `Arena.Scratch`, which have the same edits | [Format text](../how-to/format-text.html) |
| `char.IsLetter`, `char.ToUpper`, `Encoding.UTF8.GetBytes` | The same, with .NET's results; casing is invariant, and the overloads that return an array or string allocate it, so in a loop encode into a span | [Strings without a GC](../concepts/strings.html#characters-and-encodings) |
| `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, `PriorityQueue<TElement, TPriority>` | `List<T>`, `Dict<TKey, TValue>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, `PriorityQueue<TElement, TPriority>`, `StrDict<TValue>`, `RefList<T>`, `RefDict<TKey, TValue>`, each created in an arena; `TryDequeue` and `TryPop` in place of `Dequeue` and `Pop` | [Choose a collection](../how-to/choose-a-collection.html) |
| `Regex`, `Regex.Match`, `Matches` | `Regex.Create(arena, pattern)`, then the same names; a subset without backreferences or lookaround, matched in linear time | [Match text with regular expressions](../how-to/match-patterns.html) |
| LINQ | `lib/Seq.cs`: `Where`, `Select`, `Sum`, query syntax, without allocating | [Query data](../how-to/query-data.html) |
| The garbage collector | Arenas, freed all at once | [Memory without a GC](../concepts/memory.html) |
| `try`/`catch` | A union or a status in the return value | [Limits](../concepts/limits.html) |
| `Task`, `async`/`await`, `CancellationToken`, `TimeProvider` | The same, on fibers: one thread runs them all, with no thread pool | [Use async and await](../how-to/use-fibers.html#use-async-and-await) |
| `Thread`, `Parallel.For` | `Parallel.For`; fibers for work that waits | [Run a loop in parallel](../how-to/run-in-parallel.html) |
| `File.ReadAllText`, `File.WriteAllText`, `FileStream` | `File`, with the same names for whole files (strings and bytes read go in an arena), and `FileStream`; their async methods run on an I/O thread | [Read and write files](../how-to/read-and-write-files.html) |
| `Stopwatch`, `DateTime.UtcNow` | `Clock.Monotonic` (nanoseconds), `Clock.UtcNow` (a `CalendarTime`) | [Use the clock and random numbers](../how-to/use-the-clock-and-random-numbers.html) |
| `Random` | `Random`, a struct with .NET's method names: pass it by `ref` | [Use the clock and random numbers](../how-to/use-the-clock-and-random-numbers.html) |
| `Environment.Exit`, `GetEnvironmentVariable`, `ProcessorCount`, `TickCount64`, `NewLine` | The same, in zerocore; `NewLine` is `"\n"` on every target | [Read environment variables and exit](../how-to/use-the-environment.html) |
| `Vector128.IsHardwareAccelerated`, `Avx2.IsSupported` at run time | Decided when building, by `ZeroInstructionSet` (`-m`) | [Use SIMD](../how-to/use-simd.html) |
| NuGet packages | Zero libraries, packed for `zero03`; ordinary .NET packages are refused | [Create a library](../how-to/create-a-library.html) |
| `[DllImport]`, `[LibraryImport]` | Work, bound when linking; `ZeroDllImports` names libraries besides `libc` and `libm.so.6`. No marshalling: numbers, pointers, enums and structs of them | [Install with dotnet](install-with-dotnet.html#call-c-functions) |
| `[UnmanagedCallersOnly]` exports, NativeAOT's `NativeLib=Shared` | The same | [Build a native library](../how-to/build-a-native-library.html) |

There is no `System.IO` beyond what `File` does (no directories, streams or paths), no `DateTime`
arithmetic or time zones, no reflection and no `System.Text.Json`. For anything else the C library has,
call it yourself with `[DllImport]`.

## Output and input

`Console` buffers what you write and encodes it as UTF-8. For input, `In.ReadLine` reads into a
buffer you own, so reading a line allocates nothing; it returns the length, or a negative number at
the end of input:

<!-- sample: samples/QuizStep2.cs ask -->
```csharp
// Console.Write shows the prompt at once, before the program
// waits for the answer.
Console.Write($"Q{i}: {a} + {b} = ");
int length = In.ReadLine(buffer);
if (length < 0)
{
    Console.WriteLine();
    Console.WriteLine("(end of input)");
    break;
}
asked++;
```

`Console.ReadLine` works too, but it returns a new string that is never freed: fine once, not in a
loop. `Console` has no `Out`, `Error`, colors or cursor. What's written without a newline is written
out when the program ends, before `In.ReadLine` reads, or on `Out.Flush()`.

## Memory

`new` still works, and so do boxing, `+` on strings and lambdas that capture. But each of them calls
`calloc`, and nothing frees it: there is no garbage collector. That is fine for what lives as long as
the program. Anything short-lived goes in an arena, which hands out memory by moving a pointer and
frees everything in it at once.

An arena can also take over ordinary `new`. Inside an object scope, arrays, strings and closures are
allocated in the arena and freed when the `using` block ends:

<!-- sample: samples/Arena.cs object-scope-free -->
```csharp
// OpenObjectScope also frees what the block created when it closes,
// so objects live exactly as long as the using block. The arena can
// hold longer-lived data too: only what came after the scope opened
// is freed.
Arena scoped = Arena.Create(4096);
int* kept = scoped.Alloc<int>();       // lives past every scope
unsafe { *kept = 42; } // SAFETY: kept points at an int in scoped.
nuint usedOutside = scoped.Used;
long heapBeforeFrames = System.GC.GetTotalAllocatedBytes();
int frameTotal = 0;
for (int round = 0; round < 1000; round++)
{
    Arena.ObjectScope objects;
    // SAFETY: nothing created inside is used after the block.
    unsafe { objects = scoped.OpenObjectScope(); }
    using (objects)
    {
        int[] cells = new int[16];
        string text = Echo("ab") + Echo("cd");
        frameTotal += cells.Length + text.Length;
    }   // cells and text are freed here
}
long frameHeapGrowth = System.GC.GetTotalAllocatedBytes() - heapBeforeFrames;
```

Freeing is `unsafe`, because it is what can leave a reference pointing at freed memory: the `SAFETY`
comment says why it can't here. [Memory without a GC](../concepts/memory.html) explains arenas, and
[The memory safety rules](../concepts/safety-rules.html) explains `unsafe`.

## Collections

`lib/`'s collections are structs holding one pointer, created in an arena and freed with it. `List<T>`
and `Dict<TKey, TValue>` hold unmanaged values (numbers, structs of numbers); `RefList`, `StrDict` and
`RefDict` hold references and strings:

<!-- sample: samples/Collections.cs dict -->
```csharp
var scores = Dict<int, int>.Create(arena);
scores.Set(1, 100);
scores.Set(2, 250);
scores.Set(3, 175);
scores.Set(-42, 999);
```

The names are .NET's, but the API is smaller and shaped by arenas: `Create(arena)` instead of `new`,
`Get` and `Set` instead of an indexer, `TryGet` returning `false` for a missing key instead of throwing.
`new List<int>()` compiles, because `List` is a struct, but gives a list that was never created: its
`Add` returns `false`.

## LINQ

`lib/Seq.cs` has LINQ's operators over arrays, spans and lists, with the same names, and
`from ... where ... select` works too. Each query is a chain of structs, so it allocates nothing:

<!-- sample: samples/Queries.cs operators -->
```csharp
// Operators chain like LINQ's. `static` lambdas are allocated once,
// the first time they run, and reused after that.
int evenSquares = numbers.Where(static x => x % 2 == 0)
                         .Select(static x => x * x)
                         .Sum();
int big = numbers.Where(static x => x > 4).Count();
```

A `static` lambda is allocated once and reused. One that captures a local allocates a closure each
time it is created, which in a loop is memory never given back;
[Query data](../how-to/query-data.html) shows how to pass state instead.

## Errors

`throw` compiles, and so does `catch`. But nothing unwinds: a `throw` prints the exception's message
and ends the program, like Rust's `panic!`. No `catch` block ever runs, and neither do the `finally`
blocks between the `throw` and `Main`. A failed cast, an index out of range or an overflow in `checked`
code ends the program the same way; a null dereference is a segmentation fault.

To get past a failure that comes as an exception, such as an `IOException` from a file or a socket,
run the code in [`Catch.Run`](../how-to/recover-from-errors.html): the `throw` comes back there as a
value, and the files and sockets opened inside are closed.

So an error the caller should handle is a return value. A C# 15 union says which outcomes there are,
and a `switch` on it must handle each:

<!-- sample: samples/Core.cs union-declaration -->
```csharp
public struct Parsed { public int Value; }
public struct BadChar { public int Position; }

// A ParseResult holds either a Parsed or a BadChar, and a switch on it must
// handle both.
public union ParseResult(Parsed, BadChar);
```

<!-- sample: samples/Core.cs union-function -->
```csharp
static ParseResult ParseNumber(string text)
{
    int value = 0;
    for (int i = 0; i < text.Length; i++)
    {
        char c = text[i];
        if (c < '0' || c > '9')
            return new BadChar { Position = i };
        value = value * 10 + (c - '0');
    }
    return new Parsed { Value = value };
}

static int ValueOrMinusOne(string text) => ParseNumber(text) switch
{
    Parsed p => p.Value,
    BadChar => -1,
};
```

## Threads

There is no `Thread`. `Task` and `async`/`await` work, on fibers on one thread (see
[Use async and await](../how-to/use-fibers.html#use-async-and-await)). For work spread over processors,
`Parallel.For` runs a loop on every processor and returns when it is done, with `lock`, `Interlocked`
and `Volatile` for shared state (see [Lock shared data](../how-to/run-in-parallel.html#lock-shared-data)):

<!-- sample: samples/Threads.cs parallel-for -->
```csharp
// Each iteration writes its own element, so no two threads touch the
// same memory. The lambda is static: what it needs comes as state.
var squares = new Squares { Results = new long[2000] };
Parallel.For(0, 2000, squares,
    static (s, i) => s.Results[i] = SlowSquare(i));
```

Arenas and `lib/`'s collections aren't thread-safe, so give each thread its own;
[Run a loop in parallel](../how-to/run-in-parallel.html) shows how.

## What compiles but behaves differently

- **A `catch` block never runs**, and a `throw` skips the `finally` blocks on its way out (above);
  `Catch.Run` is how to get it back.
- **Nothing you `new` is freed**, nor boxed values, concatenated strings or closures, unless an object
  scope is open.
- **A struct's default `Equals` compares bytes.** A reference field compares by reference, where .NET
  calls its `Equals`. Records and `IEquatable<T>` compare field by field. `Dict` and `HashSet` keys
  follow the same rule, and a struct that overrides `Equals(object)` without implementing
  `IEquatable<T>` is compared by its bytes there too.
- **A `Type` has no name.** `typeof` and `GetType()` work for comparing types, and a class without its
  own `ToString` prints `object`.
- **Variance aborts.** Using an `IEnumerable<string>` as an `IEnumerable<object>`, or a `string[]` as
  an `object[]`, ends the program with a message instead of working.
- **`[ModuleInitializer]` doesn't run.** Use a static constructor, which does, once, even with several
  threads.
- **`IsSupported` is a constant.** `Avx2.IsSupported` is `true` only when the build names the
  instruction set, and the program then assumes the processor has it.
- **Arrays aren't `IList<T>` or `IEnumerable<T>`.** Pass a span instead.
- **Strings sort by character code.** `string.CompareTo`, `Comparer<string>.Default`, and so
  `Sort()` on strings, compare UTF-16 code units, as `string.CompareOrdinal` does: zero mode has no
  cultures to follow. Every capital letter goes before every lowercase one, so `"Banana"` sorts
  before `"apple"`, where .NET puts `"apple"` first. To ignore case, sort with your own comparer.
- **`HashCode` gives the same result on every run.** .NET seeds `HashCode.Combine` with a random
  number in each process; zero mode has nothing random at startup, so its seed is fixed. Don't save
  or send hash codes in either case.

[Limits](../concepts/limits.html) has the full list and the known issues.

## Next

- [Install with dotnet](install-with-dotnet.html), if you haven't: `dotnet new zero` and
  `dotnet build`.
- The [tutorial](../tutorial/index.html) builds a small program step by step with `Console`, `In` and
  `Str.TryParse`.
- [Zero mode](../concepts/zero-mode.html) explains what replaced the runtime.
