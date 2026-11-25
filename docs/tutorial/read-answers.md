---
title: Read the answers
index: 2
---

# Step 2: read the answers

Now the quiz asks each question and waits for an answer. It needs somewhere to put the answer. In
.NET, `Console.ReadLine()` returns a new string each time. Here that string would never be freed, and
a long-running program would grow with every line it read. Instead, the program reads into a buffer
on the stack, which it reuses for every answer:

<!-- sample: samples/QuizStep2.cs buffer -->
```csharp
// Each answer is read into this; nothing is allocated per question.
Span<char> buffer = stackalloc char[32];
int right = 0, asked = 0;
```

## Ask and wait

<!-- sample: samples/QuizStep2.cs ask -->
```csharp
// Console.Write holds the prompt in a buffer; In.ReadLine writes it
// out first, so it shows before the program waits for the answer.
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

`Console.Write` writes the prompt without ending the line. `Console` sends its buffer to the terminal
only when a line ends, so a prompt would normally sit in the buffer while the program waits.
`In.ReadLine` writes the buffer out before it reads, so the prompt shows.

`In.ReadLine(buffer)` reads one line into the buffer and returns its length. The line ending (`\n` or
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

`buffer.Slice(0, length)` is the answer: a span over the buffer, not a copy. `Trim()` returns a
narrower span, and `Str.TryParse` reads a number from it. None of this allocates. The span is only
valid until the next `In.ReadLine` overwrites the buffer, which is fine here: the answer is checked and
forgotten before the next question.

`Math.Min` guards against a line longer than the buffer. When that happens, `In.ReadLine` returns the
line's full length, and only its start is in the buffer. Step 3 handles it properly.

## Run it

Type the answers, or give a file of them:

Put [`samples/QuizStep2.cs`](https://github.com/OnurGumus/zero-csharp/blob/master/samples/QuizStep2.cs) in `Program.cs`, build, and give it four answers:

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
