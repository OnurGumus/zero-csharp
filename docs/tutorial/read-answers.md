---
title: Read the answers
index: 2
---

# Step 2: read the answers

Now the quiz asks each question and waits for an answer. It needs somewhere to put the answer. In
.NET, `Console.ReadLine()` returns a new string each time. Here that string would never be freed, and
a long-running program would grow with every line it read. Instead, the program reads into a buffer
that it reuses for every answer. In C# the buffer is on the stack. In F# it's an array made once:
a span can't be a module-level value, and F#'s `stackalloc` gives a native pointer, not a span:

<!-- sample: samples/QuizStep2.cs buffer -->
```csharp
// Each answer is read into this; nothing is allocated per question.
Span<char> buffer = stackalloc char[32];
int right = 0, asked = 0;
```

<div class="fs-alt"></div>

<!-- sample: samples/QuizStep2.fs buffer -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Each answer is read into this, made once: nothing is allocated per
// question. A span can't be a module-level value, so the array is.
let buffer: char[] = Array.zeroCreate 32
```

## Ask and wait

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

<div class="fs-alt"></div>

<!-- sample: samples/QuizStep2.fs ask -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// The answer to one question, or None at the end of the input.
let ask i a b =
    // printf shows the prompt at once, before the program waits.
    printf "Q%d: %d + %d = " i a b
    let length = In.ReadLine(Span<char>(buffer))
    if length < 0 then
        printfn ""
        printfn "(end of input)"
        None
    else
        Some(min length buffer.Length)
```

`Console.Write` and `printf` write the prompt without ending the line, and it shows at once, as in
.NET, so it is on the screen while the program waits.

`In.ReadLine(buffer)` (F# passes `Span<char>(buffer)`) reads one line into the buffer and returns its length. The line ending (`\n` or
`\r\n`) isn't included, and the input is decoded as UTF-8. At the end of input it returns -1: when a
file of answers runs out, or when you press Ctrl-D at a terminal.

## Check the answer

<!-- sample: samples/QuizStep2.cs check -->
```csharp
ReadOnlySpan<char> answer = buffer.Slice(0, Math.Min(length, buffer.Length));
if (Str.TryParse(answer.Trim(), out long value) && value == a + b)
{
    right++;
    Console.WriteLine("right");
}
else
{
    Console.WriteLine($"no, it's {a + b}");
}
```

<div class="fs-alt"></div>

<!-- sample: samples/QuizStep2.fs check -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Whether the answer in the buffer is a + b. Says which, as it checks.
let check length a b =
    let answer = ReadOnlySpan<char>(buffer, 0, length)
    let mutable value = 0L
    if Str.TryParse(answer.Trim(), &value) && value = int64 (a + b) then
        printfn "right"
        true
    else
        printfn "no, it's %d" (a + b)
        false

let rec quiz i right asked =
    if i > Questions then
        right, asked
    else
        let a = next 50
        let b = next 50
        match ask i a b with
        | None -> right, asked
        | Some length ->
            let gained = if check length a b then 1 else 0
            quiz (i + 1) (right + gained) (asked + 1)
```

`buffer.Slice(0, length)` (in F#, `ReadOnlySpan<char>(buffer, 0, length)`) is the answer: a span over
the buffer, not a copy. `Trim()` returns a
narrower span, and `Str.TryParse` reads a number from it. None of this allocates. The span is only
valid until the next `In.ReadLine` overwrites the buffer, which is fine here: the answer is checked and
forgotten before the next question.

`Math.Min` (`min` in F#) guards against a line longer than the buffer. When that happens, `In.ReadLine` returns the
line's full length, and only its start is in the buffer. Step 3 handles it properly.

<div class="lang-text" data-lang="fsharp">

F# has no `break`, so the F# program loops by recursion instead: `quiz` asks question `i` and calls
itself for the next, carrying the counts, until the questions or the input run out. `Str.TryParse`
takes `&value` for C#'s `out long value`: the overloads differ only in that parameter's type, so
F#'s usual tuple return can't pick one.

</div>


## Run it

Type the answers, or give a file of them:

Put [`samples/QuizStep2.cs`](https://github.com/OnurGumus/zero-csharp/blob/master/samples/QuizStep2.cs) in `Program.cs`, or [`samples/QuizStep2.fs`](https://github.com/OnurGumus/zero-csharp/blob/master/samples/QuizStep2.fs) in `Program.fs`, build, and give it four answers:

```bash
dotnet build
printf '46\n5\nseventeen\n45\n' | bin/Debug/zero03/Quiz
```

```text
Q1: 38 + 8 = right
Q2: 5 + 0 = right
Q3: 12 + 5 = no, it's 17
Q4: 2 + 43 = right
Q5: 27 + 8 = 
(end of input)
3 of 4 right
```

With answers from a file, they aren't echoed, so each result follows its question on the same line.
At a terminal, you see what you type.

Next: [handle bad input](handle-bad-input.html).
