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

zerocore's `string` has `Length`, an indexer, `==`, `!=`, `+`, `Equals`, `IsNullOrEmpty`, `switch` on
strings, and .NET's casing and comparing (see [Change case and compare](#change-case-and-compare)). String literals, static string fields and static string arrays work. `a + b`
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

<div class="fs-alt"></div>

<!-- sample: samples/Strings.fs split -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let mutable parts = 0
for part in "x,y,z".Split(',') do
    parts <- parts + 1
check "Split count" (parts = 3)
```

Defining `AsSpan(string)` also switches on C# 14's implicit conversion from `string` to
`ReadOnlySpan<char>`. So every string converts to a span, and any span parameter takes a string. A
null string acts as an empty one. F# converts a string to a span where a method takes one, through
the same conversion (`op_Implicit`), and `for part in text.Split(',')` loops over the parts there too.

Parsing works on spans too. `int.Parse`, `double.TryParse` and the other numbers' `Parse` and
`TryParse` take a `ReadOnlySpan<char>` as well as a string, and read what .NET reads in the invariant
culture (see [Read input](../how-to/read-input.html#parse-numbers)). `Str.TryParse` is a stricter
version for `int`, `long`, `ulong` and `double`, with no surrounding whitespace (call `Trim()` first)
and no thousands separators, and `Str.TryParseHex` reads hex. Both are correctly rounded for `double`.
Parsing from a span is how the tutorial's quiz reads an answer without allocating anything: the line
goes into a stack buffer, and the number is parsed from a slice of it.

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

## Console: formatting straight to stdout

`Console.Write` and `Console.WriteLine` write through `lib/Out.cs`, a buffered writer that encodes text
as UTF-8 and hands each line to `write()` in one call (`Console.Write` shows what it has at once, as in
.NET). In C#, `Console.WriteLine($"...")` uses a custom interpolated string handler, so every hole is
formatted straight into the output buffer and no string is built. F#'s `printf` formats in a small
buffer of its own that it takes back as it returns ([what printf leaves
behind](../how-to/write-fsharp.html#what-printf-leaves-behind)):

<!-- sample: samples/Strings.cs table -->
```csharp
Console.WriteLine($"{"item",-8}|{"qty",5}|{"price",9}");
Console.WriteLine($"{"apple",-8}|{3,5}|{1.25,9:F2}");
Console.WriteLine($"{"melon",-8}|{12,5}|{0.5,9:F2}");
```

<div class="fs-alt"></div>

<!-- sample: samples/Strings.fs table -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
printfn "%-8s|%5s|%9s" "item" "qty" "price"
printfn "%-8s|%5d|%9.2f" "apple" 3 1.25
printfn "%-8s|%5d|%9.2f" "melon" 12 0.5
```

Holes take every number type, `bool`, `char`, strings and spans, with an optional alignment
(`{x,8}`, `{x,-8}`) and one of .NET's format strings (`{x:X4}`, `{x:F2}`, `{x:N0}`), with .NET's
output. [Format text](../how-to/format-text.html) covers the details. `printf`'s specifiers take
the same widths: `%-8s`, `%5d`, `%9.2f`.

## Strings in an arena

When you do need a string object, to store it or to pass it to something that takes `string`, build it
in an arena. `Str.Format(arena, $"...")` formats into the arena with the same holes as `Console.WriteLine`, and
`Str.Concat(arena, a, b)` joins spans. Both return null if the arena isn't created or is full. In F#,
`arena.Sprintf "n=%d" 5` formats into the arena as `sprintf` would: C#'s handler isn't one F# can
call.

These are real strings. A zero-mode string object is a method-table pointer, a length and the
characters, and `Str.Format` (or `Sprintf`) lays one out in arena memory, followed by a terminator
like a literal's.
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

<div class="fs-alt"></div>

<!-- sample: samples/Strings.fs real-string -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let s = arena.Sprintf "n=%d" 5
check "Length" (s.Length = 3)
check "indexer" (s[0] = 'n' && s[2] = '5')
check "null-terminated like a literal" (terminator s = '\000')
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

<div class="fs-alt"></div>

<!-- sample: samples/Strings.fs scratch -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Build in scratch memory and copy only the result out: the draft goes
// when the callback returns, the copy stays in `arena`.
let numbersInto =
    ScratchFuncIn<Arena, string>(fun scratch keep ->
        let draft = scratch.Text()
        for i in 0 .. 49 do
            draft.Append(i)
            draft.Append(' ')
        draft.ToArenaString(keep))
let kept = Arena.Scratch(arena, numbersInto)
```

The draft is freed when the callback returns, and `kept` still holds the text, in `s_arena`. The F#
version takes the arena by value (`ScratchFuncIn`): an `Arena` is a handle, so the copy allocates in
the same arena. Its callback is a module-level value, made once, as the C# one is a `static` lambda.

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

<div class="fs-alt"></div>

<!-- sample: samples/Quiz.fs record -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Kept until the end, so the line is made in `keep`, not on the heap for
// good (sprintf's) or in the buffer the next answer overwrites.
let remember a b (answer: int64) =
    let line = keep.Sprintf "  %d + %d = %d, not %d" a b (a + b) answer
    mistakes.Add(line) |> ignore
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

<div class="fs-alt"></div>

<!-- sample: samples/Unicode.fs char-methods -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
check "letters in any script"
    (Char.IsLetter 'é' && Char.IsLetter 'ж' && Char.IsLetter '中')
check "digits in any script"
    (Char.IsDigit '٣' && Char.GetNumericValue '٣' = 3.0)
check "numbers that aren't digits"
    (Char.IsNumber '½' && not (Char.IsDigit '½')
     && Char.GetNumericValue '½' = 0.5 && Char.GetNumericValue 'Ⅻ' = 12.0)
check "case"
    (Char.ToUpper 'ж' = 'Ж' && Char.ToLower 'Σ' = 'σ' && Char.IsUpper 'Σ')
check "invariant case: one char to one"
    (Char.ToUpper 'ß' = 'ß' && Char.ToUpper 'ı' = 'ı')
check "categories"
    (Char.GetUnicodeCategory '€' = UnicodeCategory.CurrencySymbol
     && Char.IsPunctuation '¿' && Char.IsSymbol '+'
     && Char.IsWhiteSpace ' ')
```

`char.ToUpper` and `ToLower` follow the current culture (see [Cultures](#cultures)), which is the
invariant one unless you set it: each char maps to one char, as in .NET. The tables are linked only into programs that use
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

<div class="fs-alt"></div>

<!-- sample: samples/Unicode.fs encoding -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let text = "naïve ☕ 😀"
let utf8 = Encoding.UTF8.GetBytes(text) // allocated
check "UTF-8 bytes"
    (utf8.Length = 15 && Encoding.UTF8.GetString(utf8) = text)

// Into buffers made once: the span overloads allocate nothing.
let bytes: byte[] = Array.zeroCreate 64
let chars: char[] = Array.zeroCreate 64
let written = Encoding.UTF8.GetBytes(text, Span<byte>(bytes))
let read =
    Encoding.UTF8.GetChars(
        ReadOnlySpan<byte>(bytes, 0, written), Span<char>(chars))
check "into a span and back"
    (written = 15 && read = text.Length
     && Encoding.UTF8.GetByteCount(text) = 15)

// What doesn't fit becomes '?' in ASCII, a close letter in Latin-1.
let ascii = Encoding.ASCII.GetBytes("naïve")
check "ASCII" (Encoding.ASCII.GetString(ascii) = "na?ve")
let latin1 = Encoding.Latin1.GetBytes("Āb")
check "Latin-1" (Encoding.Latin1.GetString(latin1) = "Ab")
```

There are no `Encoder` or `Decoder` objects, no fallback objects and no other code pages.
`new UTF8Encoding(false, throwOnInvalidBytes: true)` ends the program at invalid input, which is what
an exception does here.

## Change case and compare

Comparing text people typed usually has to ignore case: a file called `README.md` or `readme.MD`, an
HTTP header written `Content-Type` or `content-type`. .NET does that with `ToUpper`, `ToLower` and the
`StringComparison` overloads, and they are all here, with .NET's results:

<!-- sample: samples/Casing.cs casing -->
```csharp
Check("ToUpper", "Grüße, Ελλάδα!".ToUpper() == "GRÜßE, ΕΛΛΆΔΑ!");
Check("ToLower", "ÇAĞRI".ToLowerInvariant() == "çağri");
Check("one char to one: ß stays", "ß".ToUpperInvariant() == "ß");
Check("surrogate pairs", "𐐨".ToUpperInvariant() == "𐐀");
string done = "ALREADY";
Check("unchanged is the same string",
    ReferenceEquals(done.ToUpperInvariant(), done));
```

Casing maps one character to one, surrogate pairs included, as .NET does in invariant mode, so `ß`
stays `ß` instead of becoming `SS`. A string that doesn't change comes back as itself; one that does
is a new string, which like every string the core library makes is never freed. `TextInfo.ToTitleCase`
follows .NET's rules, which keep acronyms and don't start a word after an apostrophe:

<!-- sample: samples/Casing.cs title -->
```csharp
Check("title case", text.ToTitleCase("the lord of the rings")
    == "The Lord Of The Rings");
Check("acronyms stay",
    text.ToTitleCase("NASA and the ESA") == "NASA And The ESA");
Check("apostrophes", text.ToTitleCase("it's o'neil") == "It's O'neil");
```

`Equals`, `Compare`, `StartsWith`, `EndsWith`, `Contains`, `IndexOf`, `LastIndexOf` and
`GetHashCode` take a `StringComparison`, and `CompareInfo` takes `CompareOptions`:

<!-- sample: samples/Casing.cs compare -->
```csharp
Check("Equals ignoring case", string.Equals("README.md", "readme.MD",
    StringComparison.OrdinalIgnoreCase));
Check("StartsWith", "Content-Type: text/html"
    .StartsWith("content-type", StringComparison.OrdinalIgnoreCase));
Check("IndexOf", "Hello World"
    .IndexOf("WORLD", StringComparison.OrdinalIgnoreCase) == 6);
Check("Contains",
    "Zero C#".Contains("c#", StringComparison.CurrentCultureIgnoreCase));
Check("Compare ignoring case", string.Compare("apple", "APPLE",
    StringComparison.InvariantCultureIgnoreCase) == 0);
// The culture compares by character code, as .NET's invariant mode.
Check("capitals first", string.Compare("Banana", "apple") < 0);
```

```text
  StringComparison                   compares
  ---------------------------------  ----------------------------------------
  Ordinal                            UTF-16 code units
  OrdinalIgnoreCase                  their upper cases
  CurrentCulture, InvariantCulture   the same as Ordinal, as in .NET's
                                     invariant mode ("Banana" before "apple")
  ...IgnoreCase                      the same as OrdinalIgnoreCase
```

To change case without allocating, `MemoryExtensions.ToUpperInvariant` and its siblings write into a
span you pass, and the span overloads compare in place:

<!-- sample: samples/Casing.cs spans -->
```csharp
Span<char> buffer = stackalloc char[32];
ReadOnlySpan<char> word = "straße";
int length = word.ToUpperInvariant(buffer);
Check("into a buffer", buffer.Slice(0, length).SequenceEqual("STRAßE"));
Check("too small", word.ToUpperInvariant(buffer.Slice(0, 3)) == -1);
Check("span comparison",
    word.Equals("STRASSE", StringComparison.OrdinalIgnoreCase) == false
    && word.StartsWith("STR", StringComparison.OrdinalIgnoreCase));
```

`System.Text.Rune` reads, writes, cases and classifies a whole code point. `scripts/text-check`
compares all of this with .NET's over every code point and millions of strings.

## Cultures

A program that formats a number for a person and the same number for a file has to know which rules
each one uses: in German, 1234.5 is written `1.234,5`. .NET handles this with cultures, and code
that must not depend on where it runs passes `CultureInfo.InvariantCulture` everywhere. Zero mode has
both: the invariant culture, which costs nothing, and about 80 common cultures built from .NET's own
data, which a program carries only if it asks for one by name.

The invariant culture works as in .NET, and `CurrentCulture` starts as the invariant culture:

<!-- sample: samples/Cultures.cs invariant -->
```csharp
CultureInfo invariant = CultureInfo.InvariantCulture;
Check("format", 1234.5.ToString("N1", invariant) == "1,234.5");
Check("parse", double.Parse("1,234.5", invariant) == 1234.5);
Check("string.Format",
    string.Format(invariant, "{0:F2}", 2.5) == "2.50");
Check("the current culture is the invariant one",
    CultureInfo.CurrentCulture.Name == ""
    && CultureInfo.CurrentCulture.Equals(invariant));
Check("its numbers",
    NumberFormatInfo.CurrentInfo.NumberDecimalSeparator == "."
    && invariant.NumberFormat.NumberGroupSeparator == ",");
```

### Built-in cultures

`new CultureInfo("de-DE")` and `CultureInfo.GetCultureInfo("de-DE")` give the culture's numbers,
dates, names and casing, as .NET gives them with ICU:

<!-- sample: samples/Locales.cs numbers -->
```csharp
var german = CultureInfo.GetCultureInfo("de-DE");
var hindi = CultureInfo.GetCultureInfo("hi-IN");
Is("German", 1234567.891.ToString("N2", german), "1.234.567,89");
Is("German euros", 1234.5m.ToString("C", german), "1.234,50 €");
Is("Indian grouping", 12345678.ToString("N0", hindi), "1,23,45,678");
Check("parse German", double.Parse("1.234,5", german) == 1234.5);
```

<!-- sample: samples/Locales.cs dates -->
```csharp
Is("German date", day.ToString("D", german), "Donnerstag, 5. März 2026");
Is("Japanese date", day.ToString("D", japanese), "2026年3月5日 木曜日");
Is("French short", day.ToString("d", french), "05/03/2026");
var american = CultureInfo.GetCultureInfo("en-US");
Is("US", day.ToString("g", american), "3/5/2026 2:07\u202FPM");
```

The table has about 80 cultures: English, German, French, Spanish, Italian, Portuguese, Dutch, the
Nordic languages, Polish, Czech, Slovak, Hungarian, Romanian, Bulgarian, Greek, Turkish,
Azerbaijani, Russian, Ukrainian, Hebrew, Arabic (Egypt), Hindi, Japanese, Korean, Chinese,
Indonesian, Vietnamese and Malay, in their main countries, and their neutral cultures (`de`,
`zh-Hans`). `CultureInfo.GetCultures` lists them. A name that isn't in the table throws
`CultureNotFoundException`:

<!-- sample: samples/Cultures.cs other -->
```csharp
CultureInfo german = new CultureInfo("de-DE");
Check("a culture from the table", german.EnglishName == "German (Germany)"
    && 1234.5.ToString("N1", german) == "1.234,5");
string message = Thrown(() => new CultureInfo("@@"));
Check("a name it doesn't know throws", message == "Culture is not supported."
    + " (Parameter 'name')\n@@ is an invalid culture identifier.");
```

Turkish and Azerbaijani have their own casing: the dotted and dotless i are separate letters, so
`i` becomes `İ`. Code that lowercases a keyword with the current culture breaks in Turkey for that
reason, which is why `ToUpperInvariant` exists:

<!-- sample: samples/Locales.cs turkish -->
```csharp
var turkish = CultureInfo.GetCultureInfo("tr-TR");
Is("Turkish upper case", "istanbul".ToUpper(turkish), "İSTANBUL");
Is("Turkish lower case", "ISPARTA".ToLower(turkish), "ısparta");
Is("invariant upper case", "istanbul".ToUpperInvariant(), "ISTANBUL");
```

Making a culture current makes it the default for `ToString()`, `Parse` and `ToUpper()`, on that
thread:

<!-- sample: samples/Locales.cs current -->
```csharp
CultureInfo.CurrentCulture = turkish;
Is("ToUpper() follows the current culture", "i".ToUpper(), "İ");
Is("so does char.ToUpper", char.ToUpper('i').ToString(), "İ");
Is("and number text", 1.5.ToString(), "1,5");
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
```

```text
  what you write                      what zero mode does
  ----------------------------------  ---------------------------------------
  CultureInfo.InvariantCulture        the invariant culture
  CultureInfo.CurrentCulture          the invariant culture, until you set it
                                      (not the system's, as .NET would)
  new CultureInfo("de-DE")            German, from the table (writable)
  CultureInfo.GetCultureInfo("de-DE") German, read-only, the same object
                                      each time
  new CultureInfo("sw-KE")            CultureNotFoundException: not in the
                                      table (.NET with ICU knows it)
  x.ToString(provider)                the provider's formats
  "i".ToUpper(turkish)                "İ"
  string.Compare(a, b, culture)       by character code, for every culture
```

What zero mode doesn't have is collation: comparing and sorting are ordinal for every culture, as in
.NET's invariant mode, so `string.Compare("a", "B", german)` puts `"B"` first. Only the Gregorian
calendar exists, so cultures whose default calendar is another one (Saudi Arabia, Thailand, Iran)
aren't in the table. `scripts/cultures-check/check-cultures.sh` compares every culture in the table
with .NET's, and `scripts/gen-cultures` regenerates it.

### Formats of your own

Any culture's formats can also be your own. A `NumberFormatInfo` you make starts as a copy of the
invariant one, and every property can be set: separators, signs, group sizes, the currency and
percent symbols and patterns. `ToString`, `TryFormat` and `Parse` given it use them,
with .NET's results:

<!-- sample: samples/CustomFormats.cs numbers -->
```csharp
var turkish = new NumberFormatInfo
{
    NumberDecimalSeparator = ",",
    NumberGroupSeparator = ".",
    CurrencySymbol = "₺",
    CurrencyDecimalSeparator = ",",
    CurrencyGroupSeparator = ".",
    CurrencyPositivePattern = 2,
    CurrencyNegativePattern = 1,
};
Is("N", 1234567.891.ToString("N2", turkish), "1.234.567,89");
Is("C", 1234.5m.ToString("C", turkish), "₺ 1.234,50");
Is("negative C", (-5m).ToString("C", turkish), "-₺5,00");
Check("Parse", double.Parse("1.234,5", NumberStyles.Number, turkish) == 1234.5);
```

A culture you make with `new CultureInfo("")` or `Clone()` has writable formats of its own (a
`DateTimeFormatInfo` for dates too), and once it is `CurrentCulture`, `ToString()` and `Parse`
without a provider follow it. As in .NET, `CurrentCulture` belongs to each thread; fibers on one
thread share it.

<!-- sample: samples/CustomFormats.cs culture -->
```csharp
CultureInfo mine = (CultureInfo)CultureInfo.InvariantCulture.Clone();
mine.NumberFormat.NumberDecimalSeparator = ",";
mine.DateTimeFormat.ShortDatePattern = "dd.MM.yyyy";
CultureInfo.CurrentCulture = mine;
Is("ToString follows the current culture", 3.25.ToString(), "3,25");
Is("so does a date", new DateTime(2026, 11, 28).ToString("d"), "28.11.2026");
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Is("and back", 3.25.ToString(), "3.25");
```

A program that never makes a writable `NumberFormatInfo` or `DateTimeFormatInfo` doesn't carry the
code that reads them: making one is what links it. `Console.WriteLine($"{n}")` with an integer and no
format writes the digits itself, with `-`, whatever the current culture; give it a format
(`{n:D}`) or call `n.ToString()` for the culture's negative sign.
`scripts/culture-check/check-culture.sh` compares formats of your own with .NET's over millions of
values.

## What allocates

`"n=" + 5`, `ToString("F2")`, `string.Format` and a plain `$"..."` work, with .NET's format strings
(see
[Format text](../how-to/format-text.html#nets-format-strings-tostring-string.format-and-tryformat)),
but each allocates a new string with calloc that is never freed: fine once, not in a loop. There,
write `Str.Format(arena, $"n={5}")`, or `TryFormat` into a span. In F#, `sprintf` and an interpolated
string are the same: a string on the heap for good; `arena.Sprintf` puts it in an arena instead.
