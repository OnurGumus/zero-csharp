---
title: Print the questions
index: 1
---

# Step 1: print the questions

The quiz asks six additions. They should be the same every run, so that a file of answers fits them.
A small generator with a fixed seed gives that:

<!-- sample: samples/QuizStep1.cs numbers -->
```csharp
const int Questions = 6;

static uint s_seed = 2026;

// The same questions every run, so a file of answers fits them.
static int Next(int limit)
{
    s_seed = s_seed * 1103515245 + 12345;
    return (int)((s_seed >> 16) % (uint)limit);
}
```

`s_seed` is a static field, and statics work as usual. So does the arithmetic: `uint` overflow wraps,
as it does in any C# program.

## Formatting without strings

`Main` prints a line per question:

<!-- sample: samples/QuizStep1.cs main -->
```csharp
public static int Main()
{
    Console.WriteLine($"{Questions} questions:");
    for (int i = 1; i <= Questions; i++)
    {
        int a = Next(50), b = Next(50);
        // Each hole is formatted straight into the output buffer: no string is
        // built. {a,2} pads to two characters, so the columns line up.
        Console.WriteLine($"Q{i}: {a,2} + {b,2} = {a + b,3}");
    }
    return 0;
}
```

`Console.WriteLine($"...")` reads as it does in .NET, but no string is built. In .NET, `$"..."`
makes a string on the heap, and the garbage collector frees it later. Zero mode has no garbage
collector: a string made with `new` is never freed. So `Console.WriteLine` takes the interpolated
string through a handler that formats each hole straight into its output buffer. The
literal parts are copied in, `{a,2}` is formatted into the buffer padded to two characters, and the
whole line goes to the C library's `write` when the line ends (or sooner, in pieces, if it doesn't fit
the 256-byte buffer).

Holes take numbers, `bool`, `char`, strings and spans, with an alignment (`{x,5}`, `{x,-5}`) and a
format (`{x:D4}`, `{x:X}`, `{price:F2}`). [Format text](../how-to/format-text.html) lists them.

## Run it

Put [`samples/QuizStep1.cs`](https://github.com/OnurGumus/zero-csharp/blob/master/samples/QuizStep1.cs) in `Program.cs`, then:

```bash
dotnet build
bin/Debug/zero03/Quiz
```

```text
6 questions:
Q1: 38 +  8 =  46
Q2:  5 +  0 =   5
Q3: 12 +  5 =  17
Q4:  2 + 43 =  45
Q5: 27 +  8 =  35
Q6: 38 + 36 =  74
```

`Console` comes from `lib/`, which every zero project compiles against (the SDK carries it, and
`build.sh` uses it in the repository). It's in the `System` namespace, as in .NET. `lib/`'s own types,
such as `In` in the next step, are in the global namespace and need no `using`.

Next: [read the answers](read-answers.html).
