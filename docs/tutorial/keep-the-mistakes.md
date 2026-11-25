---
title: Keep the mistakes
index: 4
---

# Step 4: keep the mistakes

The last step lists the wrong answers at the end. That is the first time the program has to keep
text: each mistake is known when it's made, and printed only after the last question. The buffer
can't hold it, since the next answer overwrites it. A string made with `new` would never be freed.
The program needs memory that lives exactly as long as the list of mistakes.

That is what an arena is for. An arena hands out memory from blocks it allocates as needed, and frees
it all at once. One arena per lifetime is the pattern: here, one for the data that lasts until the end
of the program.

<!-- sample: samples/Quiz.cs keep -->
```csharp
// Lives until the end: the mistakes are kept here, the list and its strings.
Arena keep = Arena.Create(4096);
RefList<string> mistakes = RefList<string>.Create(keep);
if (!mistakes.IsCreated)
{
    Console.WriteLine("Arena.Create failed");
    return 1;
}
```

`Arena.Create` makes the arena, with a first block of 4 KB that grows if needed.
`RefList<string>.Create(keep)` makes an empty list in it. The list holds references, which the plain
`List<T>` can't: `List<T>` stores items by value and only takes types without references.

If the arena couldn't be created, nothing built on it can be either, so checking the list is enough.

## Record a mistake

<!-- sample: samples/Quiz.cs record -->
```csharp
else
{
    Console.WriteLine($"no, it's {a + b}");
    // Kept until the end, so the string is built in `keep`, not in
    // the buffer the next answer overwrites.
    mistakes.Add(Str.Format(keep, $"  {a} + {b} = {a + b}, not {value}"));
}
```

`Str.Format(keep, $"...")` is the one place in the program that builds a string. It formats the line,
like `Console.WriteLine` would, into a new string allocated in `keep`. The string lives as long as the arena,
and the list holds a reference to it.

Both must live in an arena that outlives their use. The list could live in `keep` while its strings
lived in a scratch arena that was reset: the list would then point at freed memory. Keeping the list
and its strings in the same arena avoids that.

## Report

<!-- sample: samples/Quiz.cs report -->
```csharp
Console.WriteLine($"{right} of {asked} right");
if (mistakes.Count > 0)
{
    Console.WriteLine("Mistakes:");
    foreach (string mistake in mistakes)
        Console.WriteLine(mistake);
}
```

`foreach` works on `RefList`, as it does on `Dict`, `StrDict` and `RefDict`; a `List` is looped over
with `Count` and `Get`. When `Main` returns, the program ends
and the operating system takes back its memory, arena included. A program that kept running would
free it once it no longer needs the mistakes, with `unsafe { keep.Destroy(); }`, or call `Reset()`
the same way to reuse the memory for the next round. Both are `unsafe`: they leave every pointer into
the arena dangling, so the call needs a block and a comment saying why nothing uses the mistakes
afterwards ([the memory safety rules](../concepts/safety-rules.html)).

## Run it

Put [`samples/Quiz.cs`](https://github.com/OnurGumus/zero-csharp/blob/master/samples/Quiz.cs) in `Program.cs`, build, and give it step 3's answers:

```bash
dotnet build
printf 'abc\n  46 \n\xc3\xa9\xf0\x9f\x98\x80\n\xff\xc3(\n7\n1234567890123456789012345678901234567890\n17\r\n\n35' \
  | bin/Debug/zero03/Quiz
```

In PowerShell, use the answers file instead ([Before you start](index.html#before-you-start)).

The output ends with the list:

```text
3 of 5 right
Mistakes:
  5 + 0 = 5, not 7
```

## Where next

- [Memory without a garbage collector](../concepts/memory.html) explains arenas and lifetimes,
  including how to free temporary work inside a longer-lived arena.
- [Keep a string](../how-to/keep-a-string.html) and [choose a collection](../how-to/choose-a-collection.html)
  cover the other ways to hold on to text.
- The [reference](../reference/index.html) lists everything in `In`, `Out`, `Arena` and `RefList`.
