---
title: Strings without a GC
index: 3
---

# Strings without a GC

In .NET, most string operations allocate a new string and leave the old one to the garbage collector.
`Substring`, `Split`, `ToString`, `string.Format` and `+` all work that way. Zero mode has no collector,
so every one of those allocations would be a leak. The string support here is built around one idea:
**read through spans, which allocate nothing, and create a string only when you need one, in an arena
you chose**.

## What the core library's string has

zerocore's `string` has `Length`, an indexer, `==`, `!=`, `+`, `Equals`, `IsNullOrEmpty` and `switch` on
strings, and nothing else. String literals, static string fields and static string arrays work. `a + b`
works too, but it allocates with `calloc` and is never freed, like every allocation the core library
makes. Use it for strings built once; in a loop, use the arena versions below.

`lib/Str.cs`, `lib/StrBuilder.cs` and `lib/Console.cs` add the rest. The
[Str reference](../reference/str.html) has the full table of .NET calls and their zero-mode
counterparts.

## Spans: reading without allocating

`lib/Str.cs` defines the span operations .NET has in `System.MemoryExtensions`, with the same names:
`SequenceEqual`, `StartsWith`, `EndsWith`, `IndexOf`, `LastIndexOf`, `Contains`, `Trim` and `Split`. None
of them allocate. Where .NET would return a new string, these return a span over the original
characters: `s.AsSpan(i, n)` takes the place of `s.Substring(i, n)`, `Trim` returns a span, and `Split`
yields one span per part:

<!-- sample: samples/Strings.cs split -->
```csharp
int parts = 0;
foreach (ReadOnlySpan<char> part in "x,y,z".Split(','))
    parts++;
Check("Split count", parts == 3);
```

Defining `AsSpan(string)` also switches on C# 14's implicit conversion from `string` to
`ReadOnlySpan<char>`. So every string converts to a span, and any span parameter takes a string. A
null string acts as an empty one.

Parsing works on spans too. `Str.TryParse` reads `int`, `long`, `ulong` and `double`, and
`Str.TryParseHex` reads hex. Parsing is strict and invariant: no surrounding whitespace, so call `Trim()`
first, and no thousands separators. `TryParse` for `double` is correctly rounded. That combination is
how the tutorial's quiz reads an answer without allocating anything: the line goes into a stack buffer,
and the number is parsed from a slice of it.

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

## Console: formatting straight to stdout

`Console.Write` and `Console.WriteLine` write through `lib/Out.cs`, a buffered writer that encodes text
as UTF-8 and hands each line to `write()` in one call. `Console.WriteLine($"...")` uses a custom
interpolated string handler, so every hole is formatted straight into the output buffer and no string
is built:

<!-- sample: samples/Strings.cs table -->
```csharp
Console.WriteLine($"{"item",-8}|{"qty",5}|{"price",9}");
Console.WriteLine($"{"apple",-8}|{3,5}|{1.25,9:F2}");
Console.WriteLine($"{"melon",-8}|{12,5}|{0.5,9:F2}");
```

Holes take every number type, `bool`, `char`, strings and spans, with an optional alignment
(`{x,8}`, `{x,-8}`) and one of .NET's format strings (`{x:X4}`, `{x:F2}`, `{x:N0}`), with .NET's
output. [Format text](../how-to/format-text.html) covers the details.

## Strings in an arena

When you do need a string object, to store it or to pass it to something that takes `string`, build it
in an arena. `Str.Format(arena, $"...")` formats into the arena with the same holes as `Console.WriteLine`, and
`Str.Concat(arena, a, b)` joins spans. Both return null if the arena isn't created or is full.

These are real strings. A zero-mode string object is a method-table pointer, a length and the
characters, and `Str.Format` lays one out in arena memory, followed by a terminator like a literal's.
The result works anywhere a string does:

<!-- sample: samples/Strings.cs real-string -->
```csharp
string s = Str.Format(s_arena, $"n={5}");
Check("Length", s.Length == 3);
Check("indexer", s[0] == 'n' && s[2] == '5');
unsafe
{
    // SAFETY: s has Length chars plus a terminator.
    fixed (char* p = s)
        Check("null-terminated like a literal", p[3] == '\0');
}
```

For text built up in pieces, `StrBuilder.Create(arena)` is the counterpart of `StringBuilder`, with
`Append($"...")`. Like a `List`, it is a handle to a header in the arena, so copies share the same
contents, and it grows its buffer in place while that buffer is still the arena's newest allocation.
Its buffer is laid out like a string object, which is how `Str.Format` turns its finished buffer into
a string without copying. `sb.ToArenaString()` copies the text into a new string.

An arena string is freed with the rest of its arena, by `Restore`, `Reset` or `Destroy`. After that it
must not be used. This is the one rule that replaces the garbage collector for strings: **a string
lives exactly as long as its arena**.

## Keeping a string past its arena

That rule decides where a string should be built. If it is temporary, build it in `Arena.Scratch`
(a `ScratchText`), whose memory the compiler keeps you from using after the callback, or in an arena
you free yourself. If it has to be kept, build it in the arena that lives long enough, or copy it
there.

Copying is explicit. `Str.ToArenaString(keep, s)` copies any string or span into the arena `keep`, and
`ToArenaString(keep)` on a `ScratchText` or `StrBuilder` copies its text. A common shape is to do the
messy work in scratch memory and copy out only the result:

<!-- sample: samples/Strings.cs scratch -->
```csharp
// Or build in scratch memory and copy only the result out: the
// draft goes when the callback returns, the copy stays in s_arena.
string kept = Arena.Scratch(ref s_arena,
    static (scoped ScratchScope scratch, scoped ref Arena keep) =>
    {
        ScratchText draft = scratch.Text();
        for (int i = 0; i < 50; i++)
            draft.Append($"{i} ");
        return draft.ToArenaString(keep);
    });
```

The draft is freed when the callback returns, and `kept` still holds the text, in `s_arena`.

Collections of strings follow the same rule. `RefList<string>` and `RefDict` hold references, not
copies, so the strings they hold must live in an arena that outlives the collection's use of them. The
quiz keeps its mistakes this way, building each string directly in the long-lived arena:

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

`StrDict` is the exception: it copies each new key into its own arena, so it doesn't depend on where its
keys came from, and looking up a span that was just read allocates nothing.
[Keep a string](../how-to/keep-a-string.html) walks through the choices.

## Characters and encodings

`char`'s methods and `System.Text.Encoding` are in zerocore, with .NET's names and results.
`char.IsLetter`, `IsDigit`, `IsUpper`, `ToUpper`, `GetNumericValue`, `GetUnicodeCategory` and the others
know every character, not only ASCII, from tables generated from .NET 10's Unicode data (Unicode 16.0):

<!-- sample: samples/Unicode.cs char-methods -->
```csharp
Check("letters in any script",
    char.IsLetter('é') && char.IsLetter('ж') && char.IsLetter('中'));
Check("digits in any script",
    char.IsDigit('٣') && char.GetNumericValue('٣') == 3);
Check("numbers that aren't digits", char.IsNumber('½') && !char.IsDigit('½')
    && char.GetNumericValue('½') == 0.5 && char.GetNumericValue('Ⅻ') == 12);
Check("case", char.ToUpper('ж') == 'Ж' && char.ToLower('Σ') == 'σ'
    && char.IsUpper('Σ'));
Check("invariant case: one char to one",
    char.ToUpper('ß') == 'ß' && char.ToUpper('ı') == 'ı');
Check("categories",
    char.GetUnicodeCategory('€') == UnicodeCategory.CurrencySymbol
    && char.IsPunctuation('¿') && char.IsSymbol('+')
    && char.IsWhiteSpace('\u00A0'));
```

There are no cultures, so `ToUpper` and `ToLower` are `ToUpperInvariant` and `ToLowerInvariant`: each
char maps to one char, as in .NET's invariant mode. The tables are linked only into programs that use
them: categories take about 16 KB, casing 1.6 KB and numeric values 3 KB. `IsAsciiDigit`, `IsAsciiLetter`
and the other ASCII methods, `IsWhiteSpace` and `IsControl` are plain comparisons and need none, which
matters on a Commodore 64. `CharUnicodeInfo` (in `System.Globalization`) answers for code points above
U+FFFF too, and the `(string s, int index)` overloads read a surrogate pair as one character.

`Encoding.UTF8`, `ASCII`, `Latin1`, `Unicode` (UTF-16), `BigEndianUnicode` and `UTF32` convert text to
bytes and back. Input that doesn't fit becomes what .NET makes of it: U+FFFD for invalid bytes and lone
surrogates, `?` for what ASCII can't hold. The overloads that return a `byte[]`, `char[]` or `string`
allocate it and never free it, so in a loop use the span ones, which allocate nothing:

<!-- sample: samples/Unicode.cs encoding -->
```csharp
string text = "naïve ☕ 😀";
byte[] utf8 = Encoding.UTF8.GetBytes(text); // allocated
Check("UTF-8 bytes",
    utf8.Length == 15 && Encoding.UTF8.GetString(utf8) == text);

// Into a buffer: nothing allocated.
Span<byte> buffer = stackalloc byte[64];
int written = Encoding.UTF8.GetBytes(text, buffer);
Span<char> chars = stackalloc char[64];
int read = Encoding.UTF8.GetChars(buffer.Slice(0, written), chars);
Check("into a span and back", written == 15 && read == text.Length
    && Encoding.UTF8.GetByteCount(text) == 15);

// What doesn't fit becomes '?' in ASCII, a close letter in Latin-1.
byte[] ascii = Encoding.ASCII.GetBytes("naïve");
Check("ASCII", Encoding.ASCII.GetString(ascii) == "na?ve");
byte[] latin1 = Encoding.Latin1.GetBytes("Āb");
Check("Latin-1", Encoding.Latin1.GetString(latin1) == "Ab");
```

There are no `Encoder` or `Decoder` objects, no fallback objects and no other code pages.
`new UTF8Encoding(false, throwOnInvalidBytes: true)` ends the program at invalid input, which is what
an exception does here.

## What isn't there

There is nothing culture-aware. `"n=" + 5`, `ToString("F2")`, `string.Format` and a plain `$"..."`
work, with .NET's format strings in the invariant culture (see
[Format text](../how-to/format-text.html#nets-format-strings-tostring-string.format-and-tryformat)),
but each allocates a new string with calloc that is never freed: fine once, not in a loop. There,
write `Str.Format(arena, $"n={5}")`, or `TryFormat` into a span.
