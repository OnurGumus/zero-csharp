---
title: Read input from the user
index: 1
---

# Read input from the user

`Console.ReadLine()` works, but it returns a new string each time, and nothing frees it. `lib/In.cs`
reads a line into a `Span<char>` you pass instead. Nothing is allocated per line, which matters when
nothing is ever freed. This guide reads answers the way `samples/Quiz.cs` does, and then
[keys one at a time](#read-keys-one-at-a-time) with `Console.ReadKey`.

## Give the line somewhere to go

A buffer on the stack is enough for short answers. `stackalloc` is safe code under the updated memory
safety rules; only dereferencing a pointer needs `unsafe`. In F#, make an array once and pass
`Span<char>(buffer)`: a span can't be a module-level value, and F#'s `stackalloc` gives a pointer.

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

The buffer's size is the longest line you accept, in UTF-16 characters. Pick it for your input: the
quiz takes numbers, so 32 is plenty.

## Prompt, then read

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

`Console.Write` shows the prompt at once, as in .NET. `Out.Write`, the faster one for output in bulk,
keeps text in a buffer until a newline, a full buffer or `Out.Flush()`; `In.ReadLine` writes that out
before it reads too, so a prompt is on the screen before the program waits either way.

`In.ReadLine(buffer)` returns one of three things:

| Return value | Meaning | The buffer holds |
|---|---|---|
| `-1` | the end of input: Ctrl-D in a terminal, or the end of a file | nothing new |
| `0` to `buffer.Length` | a line of that length | the whole line |
| more than `buffer.Length` | a line too long for the buffer | its first `buffer.Length` characters |

A last line without a newline at the end is still returned as a line; the next call returns `-1`.

## Handle every case

The quiz's final loop asks again when the answer doesn't fit or isn't a number, and lets an empty
answer skip the question:

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

- **Too long:** `In` has already read and dropped the rest of the line, so the next call starts on a
  fresh line. The return value is the full length, so you can say how long the line was.
- **The line itself** is `buffer.Slice(0, length)` (`ReadOnlySpan<char>(buffer, 0, length)` in F#). Only slice after checking both `length < 0` and
  `length > buffer.Length`; the tutorial's step 2 takes `Math.Min(length, buffer.Length)` instead, which
  accepts the start of a long line.
- **Whitespace:** `Trim()` on a span returns a span; nothing is copied.

## Parse numbers

`int.Parse`, `double.TryParse` and the rest work as in .NET, on every integer type, `double`, `float`
and `Half`, with every overload: a `string` or a `ReadOnlySpan<char>` (so the slice goes straight in),
a `NumberStyles`, and an `IFormatProvider`, which is ignored. The culture is always the invariant one:
the decimal point is `.` and the group separator `,`. In F#, `Int32.TryParse("12a")` returns the
result and the value as a pair, `(false, 0)`, as it does in .NET.

<!-- sample: samples/Parsing.cs integers -->
```csharp
int answer = int.Parse("42");
Check("int.Parse", answer == 42);
Check("whitespace and a sign", int.Parse(" -17 ") == -17);
Check("thousands", int.Parse("1,234,567", NumberStyles.Number) == 1234567);
Check("hex", int.Parse("ff", NumberStyles.HexNumber) == 255);
Check("hex is the bits", sbyte.Parse("FF", NumberStyles.HexNumber) == -1);

Check("TryParse", int.TryParse("12345", out int parsed) && parsed == 12345);
Check("not a number", !int.TryParse("12a", out parsed) && parsed == 0);
Check("too large", !int.TryParse("2147483648", out parsed));
Check("null", !int.TryParse((string)null, out parsed));
```

<div class="fs-alt"></div>

<!-- sample: samples/Parsing.fs integers -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let answer = Int32.Parse("42")
check "Int32.Parse" (answer = 42)
check "whitespace and a sign" (Int32.Parse(" -17 ") = -17)
check "thousands" (Int32.Parse("1,234,567", NumberStyles.Number) = 1234567)
check "hex" (Int32.Parse("ff", NumberStyles.HexNumber) = 255)
check "hex is the bits" (SByte.Parse("FF", NumberStyles.HexNumber) = -1y)

// TryParse's out value comes back with the result, as a pair.
check "TryParse"
    (match Int32.TryParse("12345") with
     | true, parsed -> parsed = 12345
     | false, _ -> false)
check "not a number" (Int32.TryParse("12a") = (false, 0))
check "too large" (not (fst (Int32.TryParse("2147483648"))))
check "null" (not (fst (Int32.TryParse(null: string))))
```

`double`, `float` and `Half` are correctly rounded from every digit given, as in .NET, so the text
`ToString` writes reads back to the same value:

<!-- sample: samples/Parsing.cs doubles -->
```csharp
double pi = double.Parse("3.14159");
Check("double.Parse", pi == 3.14159);
Check("exponent", double.Parse("6.02e23") == 6.02e23);
Check("thousands by default", double.Parse("1,000.5") == 1000.5);
Check("too large is Infinity",
    double.Parse("1e400") == double.PositiveInfinity);
Check("too small is 0", double.Parse("1e-400") == 0);
Check("symbols", double.IsNaN(double.Parse("NaN"))
    && double.Parse("-Infinity") == double.NegativeInfinity);
Check("TryParse", double.TryParse("2.5", out double half) && half == 2.5);
Check("not a number", !double.TryParse("2.5.1", out half));
```

<div class="fs-alt"></div>

<!-- sample: samples/Parsing.fs doubles -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let pi = Double.Parse("3.14159")
check "Double.Parse" (pi = 3.14159)
check "exponent" (Double.Parse("6.02e23") = 6.02e23)
check "thousands by default" (Double.Parse("1,000.5") = 1000.5)
check "too large is Infinity" (Double.Parse("1e400") = infinity)
check "too small is 0" (Double.Parse("1e-400") = 0.0)
check "symbols"
    (Double.IsNaN(Double.Parse("NaN"))
     && Double.Parse("-Infinity") = -infinity)
check "TryParse" (Double.TryParse("2.5") = (true, 2.5))
check "not a number" (not (fst (Double.TryParse("2.5.1"))))
```

Text that isn't a number makes `Parse` throw `FormatException`, and a number too large for the type
`OverflowException`, which end the program unless a [`Catch.Run`](recover-from-errors.html) is waiting
for them. So for input, use `TryParse`: it returns `false` and sets the value to 0. Only a
`NumberStyles` .NET refuses, such as `AllowHexSpecifier` for a `double`, makes it throw.

`Str.TryParse` is the stricter choice, for `int`, `long`, `ulong` and `double`: no whitespace around
the number (call `Trim()` first) and no thousands separators. `Str.TryParseHex`
reads hex digits with an optional `0x`. `scripts/parse-check/check-parsing.sh` compares the parsing
with .NET's over millions of strings.

## What reaches the program

- **UTF-8.** Input is decoded as UTF-8. An invalid byte becomes U+FFFD (`�`). A character outside the
  Basic Multilingual Plane, such as an emoji, takes two `char`s in the buffer, so it counts as 2 towards
  the length.
- **Line endings.** Both `\n` and `\r\n` end a line, and neither is stored. A file written on Windows
  reads the same as one written on Linux.
- **Input from a file.** Every target passes standard input through. A program built with the SDK
  reads it like any other: `bin/Debug/zero03/Quiz < answers.txt`. In the repository:
  `./run.sh Quiz < answers.txt`, `./run.sh --wasm Quiz < answers.txt`, and on DOS
  `./run.sh --dos Quiz < answers.txt` (see [Build for DOS](build-for-dos.html)).

`test.sh` feeds Quiz `tests/input/Quiz.txt`, which holds a word, bad UTF-8, an over-long line, a CRLF
line, an empty line and a last line without a newline:

```bash
./run.sh Quiz < tests/input/Quiz.txt
```

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
Mistakes:
  5 + 0 = 5, not 7
```

The answers from the file aren't echoed, which is why each prompt runs into the reply.

## Read bytes instead of lines

For input that isn't text, or that you decode yourself, `In.Read` fills a `Span<byte>` with the bytes
as they arrived and returns how many it put there: 0 at the end of input. It may return fewer than the
span holds before the end, so loop until it returns 0. `samples/Pipe.cs` reads standard input in
pieces of 7 bytes:

<!-- sample: samples/Pipe.cs read -->
```csharp
Span<byte> chunk = stackalloc byte[7];
int count;
while ((count = In.Read(chunk)) > 0)
{
    for (int i = 0; i < count; i++)
        Take(chunk[i]);
}
```

<div class="fs-alt"></div>

<!-- sample: samples/Pipe.fs read -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let chunk: byte[] = Array.zeroCreate 7
let mutable count = In.Read(Span<byte>(chunk))
while count > 0 do
    for i in 0 .. count - 1 do
        take chunk[i]
    count <- In.Read(Span<byte>(chunk))
```

`Out.WriteUtf8` writes bytes out the same way, without encoding them as `Console` does, so a program can sit in a pipe:
`./run.sh Pipe < tests/input/Pipe.txt`. Nothing is decoded or translated, on any target: a `\r` or a
NUL byte arrives as it was sent. The one exception is typing into a Windows console, which hands over
`\r\n` at the end of each line and so has its `\r` dropped; a file or a pipe on Windows is passed
through untouched.

`ReadLine` and `Read` can be mixed: `Read` starts with whatever `ReadLine` had read ahead of the line
it returned.

## Read keys one at a time

`Console.ReadKey` returns a key as soon as it is pressed, without waiting for Enter, as a
`ConsoleKeyInfo`: its character, its `ConsoleKey` and its modifiers. `ReadKey(intercept: true)` doesn't
show the key; `Console.KeyAvailable` says whether one waits, without waiting:

<!-- sample: samples/Keys.cs readkey -->
```csharp
while (true)
{
    ConsoleKeyInfo key = Console.ReadKey(intercept: true);
    if (key.KeyChar == 'q')
        break;
    Console.WriteLine($"key {(int)key.Key,3} char {(int)key.KeyChar,4} "
        + $"shift {Has(key, ConsoleModifiers.Shift)} "
        + $"alt {Has(key, ConsoleModifiers.Alt)} "
        + $"ctrl {Has(key, ConsoleModifiers.Control)}");
}
```

<div class="fs-alt"></div>

<!-- sample: samples/Keys.fs readkey -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let rec readKeys () =
    let key = Console.ReadKey(true)
    if key.KeyChar <> 'q' then
        printfn "key %3d char %4d shift %d alt %d ctrl %d"
            (int key.Key) (int key.KeyChar)
            (has key ConsoleModifiers.Shift) (has key ConsoleModifiers.Alt)
            (has key ConsoleModifiers.Control)
        readKeys ()
readKeys ()
```

It works on every target. In a terminal on Linux and macOS it puts the terminal in raw mode for the one
read; Windows and DOS read the console's key events, UEFI the firmware's keyboard, and the C64 the
KERNAL's keyboard buffer. Arrows, Home, End, Page Up and Down, Insert, Delete and F1-F12 come as their
`ConsoleKey`, with Shift, Alt and Control where the system reports them (not on UEFI or the C64).
On DOS, reading from the keyboard is untested: the tests feed it input from a file, which works as
below.

When standard input isn't a terminal (a pipe or a file, as in `./test.sh`), a key is the next character
of the input, and a terminal's escape sequences in it (`ESC [ A`) are keys too, as on .NET for Unix.
At the end of that input, `ReadKey` ends the program with `InvalidOperationException`. WebAssembly and
the microcontrollers have no terminal, so they always read keys this way.

## Watch out for

- **The line lives only until the next read.** The next `In.ReadLine` into the same buffer overwrites
  it. To keep a line, copy it into an arena: `Str.ToArenaString(arena, line)`. See
  [Keep a string](keep-a-string.html).
- **Read standard input only through `In`.** `In` reads it 256 bytes at a time into a buffer of its
  own, so a direct `read()` call elsewhere would miss the bytes `In` has already taken. For raw
  bytes, use `In.Read`.

See also: [`In` reference](../reference/in.html), [`Str` reference](../reference/str.html),
[Strings without a garbage collector](../concepts/strings.html), and the tutorial pages
[Read the answers](../tutorial/read-answers.html) and
[Handle bad input](../tutorial/handle-bad-input.html).
