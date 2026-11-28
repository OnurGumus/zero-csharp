---
title: Handle bad input
index: 3
---

# Step 3: handle bad input

Step 2 treats anything that isn't the right number as a wrong answer, including a typo. This step asks
again instead, and lets an empty answer skip the question. In C#, the question's body becomes a loop
that ends when the answer has been dealt with. In F#, `ask` calls itself until the answer is one the
quiz can count, and says what it was:

<!-- sample: samples/QuizStep3.cs loop -->
```csharp
while (true)
{
    Console.Write($"Q{i}: {a} + {b} = ");
    int length = In.ReadLine(buffer);
    if (length < 0)
    {
        Console.WriteLine();
        Console.WriteLine("(end of input)");
        asked--;
        goto Done;
    }
    // A line longer than the buffer returns its full length; the
    // buffer holds only its start, so ask again.
    if (length > buffer.Length)
    {
        Console.Write($"that's {length} characters; ");
        Console.WriteLine($"answers are at most {buffer.Length}");
        continue;
    }

    ReadOnlySpan<char> answer = buffer.Slice(0, length).Trim();
    if (answer.Length == 0)
    {
        Console.WriteLine("skipped");
        break;
    }
    if (!Str.TryParse(answer, out long value))
    {
        Console.WriteLine($"\"{answer}\" isn't a number");
        continue;
    }

    if (value == a + b)
    {
        right++;
        Console.WriteLine("right");
    }
    else
    {
        Console.WriteLine($"no, it's {a + b}");
    }
    break;
}
```

<div class="fs-alt"></div>

<!-- sample: samples/QuizStep3.fs loop -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
type Outcome =
    | Right
    | Wrong
    | Skipped
    | EndOfInput

// Asks until the answer is a number, empty, or the input ends.
let rec ask i a b =
    printf "Q%d: %d + %d = " i a b
    let length = In.ReadLine(Span<char>(buffer))
    if length < 0 then
        printfn ""
        printfn "(end of input)"
        EndOfInput
    // A line longer than the buffer returns its full length; the buffer
    // holds only its start, so ask again.
    elif length > buffer.Length then
        printf "that's %d characters; " length
        printfn "answers are at most %d" buffer.Length
        ask i a b
    else
        let answer = ReadOnlySpan<char>(buffer, 0, length).Trim()
        let mutable value = 0L
        if answer.Length = 0 then
            printfn "skipped"
            Skipped
        elif not (Str.TryParse(answer, &value)) then
            printfn "\"%s\" isn't a number" (answer.ToString())
            ask i a b
        elif value = int64 (a + b) then
            printfn "right"
            Right
        else
            printfn "no, it's %d" (a + b)
            Wrong
```

Three cases are new:

- **Too long.** `In.ReadLine` returns the line's full length even when the buffer can't hold it, and
  skips the rest of the line, so the next read starts on the next line. `length > buffer.Length` is how
  the program knows. Returning the length instead of failing lets the caller decide: here, ask again.
- **Empty.** After `Trim()`, an empty span means the answer was blank: skip the question.
- **Not a number.** `Str.TryParse` returns false, and the message quotes the answer.

<div class="lang-text" data-lang="csharp">

The message quotes the answer with `{answer}`: a span can be a hole in `$"..."`, so the text goes from
the input buffer to the output buffer without a string in between.

`goto Done` leaves both loops at the end of input. C#'s `goto` is as unfashionable as ever, but it is
the plainest way out of two loops.

</div>

<div class="lang-text" data-lang="fsharp">

A span can't be a `printf` argument, so the message quotes `answer.ToString()`: a new string with the
span's characters, as in .NET. It stays on the heap, which is fine for the odd bad answer; `Console.Write`
takes the span itself, when a program must leave nothing behind.

F# has no `continue` or `goto`: `ask` calls itself to ask again, and `quiz` stops at `EndOfInput`,
which leaves both loops at once.

</div>

## Run it

The test suite feeds this step a file with every kind of bad input: letters, characters outside ASCII,
bytes that aren't valid UTF-8, a 40-character line, a Windows line ending, an empty line, and a last
line without a line ending.

Put [`samples/QuizStep3.cs`](https://github.com/OnurGumus/zero-csharp/blob/master/samples/QuizStep3.cs) in `Program.cs`, or [`samples/QuizStep3.fs`](https://github.com/OnurGumus/zero-csharp/blob/master/samples/QuizStep3.fs) in `Program.fs`, build, and give it the awkward
answers: spaces around a number, an emoji, bytes that aren't UTF-8, a 40-character line, a Windows
line ending and an empty line:

```bash
dotnet build
printf 'abc\n  46 \n\xc3\xa9\xf0\x9f\x98\x80\n\xff\xc3(\n7\n1234567890123456789012345678901234567890\n17\r\n\n35' \
  | bin/Debug/zero03/Quiz
```

In PowerShell, use the answers file instead ([Before you start](index.html#before-you-start)).

```text
6 questions. An empty answer skips one.
Q1: 38 + 8 = "abc" isn't a number
Q1: 38 + 8 = right
Q2: 5 + 0 = "é😀" isn't a number
Q2: 5 + 0 = "��(" isn't a number
Q2: 5 + 0 = no, it's 5
Q3: 12 + 5 = that's 40 characters; answers are at most 32
Q3: 12 + 5 = right
Q4: 2 + 43 = skipped
Q5: 27 + 8 = right
Q6: 38 + 36 = 
(end of input)
3 of 5 right
```

The invalid bytes came out as `�`, the Unicode replacement character: `In` decodes UTF-8 and replaces
what it can't decode, so a program never sees a broken character. `"é😀"` went in as UTF-8, became
UTF-16 `char`s in the span, and came back out as UTF-8 through `Console`.

Next: [keep the mistakes](keep-the-mistakes.html).
