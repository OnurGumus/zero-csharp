---
title: Match text with regular expressions
index: 22
---

# Match text with regular expressions

`lib/Regex.cs` has regular expressions with .NET's syntax for the common cases and .NET's names:
`IsMatch`, `Match`, `NextMatch`, `Matches`, `Count`, `Groups[n].Value`. A pattern is compiled once,
into an arena, and matching it allocates nothing: `Value` and each group's `Value` are slices of the
text you searched.

## Compile once, match often

`Regex.Create` compiles the pattern into the arena you pass. Keep the `Regex` and match with it as
often as you like:

<!-- sample: samples/Patterns.cs match -->
```csharp
Regex date = Regex.Create(arena, @"(\d{4})-(\d\d)-(\d\d)");
RegexMatch m = date.Match("released on 2026-11-24, at last");
Check("a match and its groups", m.Success && m.Index == 12
    && Is(m.Value, "2026-11-24") && Is(m.Groups[1].Value, "2026")
    && Is(m.Groups[3].Value, "24"));
```

`foreach` goes over every match, left to right, without overlaps. After an empty match the search
moves on one character, so the loop always ends:

<!-- sample: samples/Patterns.cs matches -->
```csharp
int total = 0;
foreach (RegexMatch number in Regex.Create(arena, @"\d+").Matches("10 20 30"))
{
    Str.TryParse(number.Value, out int value);
    total += value;
}
Check("foreach over the matches", total == 60);
```

A match is a `ref struct` that refers to the text, so it lives only as long as that span does. To keep
a match's text, copy it, for example with `Str.Format(arena, $"{m.Value}")`.

## Replace and split

`Replace` takes .NET's replacement patterns, and writes the result as a new string in the arena you
pass:

<!-- sample: samples/Patterns.cs replace -->
```csharp
string swapped = date.Replace(arena,
    "on 2026-11-25 and 2025-01-02", "$3/$2/$1");
// "on 25/11/2026 and 02/01/2025"
```

| In the replacement | Stands for |
|---|---|
| `$1`, `${1}` | group 1 (empty if it didn't take part) |
| `${name}` | the group named `name` |
| `$&` | the whole match |
| `` $` ``, `$'` | the input before the match, after it |
| `$+` | the last group |
| `$_` | the whole input |
| `$$` | a `$` |

Anything else after a `$`, such as `$x` or a group the pattern doesn't have, is kept as it is, as
.NET does. A last argument limits how many matches are replaced. Other overloads append to a
`StrBuilder` instead, or to a `ScratchText` inside `Arena.Scratch`.

For a replacement that depends on the match, pass a function, as .NET's `MatchEvaluator`. It
appends what replaces each match to the builder it is given, so nothing is allocated per match:

<!-- sample: samples/Patterns.cs evaluator -->
```csharp
// A MatchEvaluator: appends what replaces each match.
static void Doubled(RegexMatch match, StrBuilder output)
{
    Str.TryParse(match.Value, out long number);
    output.Append(2 * number);
}
```

<!-- sample: samples/Patterns.cs evaluator-use -->
```csharp
string doubled = R(@"\d+").Replace(arena, "3 apples, 12 pears", &Doubled);
```

`Split` gives the pieces of the input between matches, as slices of it, so it allocates nothing. As
in .NET, the text of each group that took part in a match comes between the pieces around it, and a
count above 0 limits the number of pieces, the last one being the rest of the input:

<!-- sample: samples/Patterns.cs split -->
```csharp
int fields = 0;
foreach (ReadOnlySpan<char> field in R(@"\s*,\s*").Split("a , b,c,,d"))
    fields++;           // "a", "b", "c", "", "d"
```

## Name the groups

A group can have a name, which `Groups[...]` and `${...}` take in place of its number:

<!-- sample: samples/Patterns.cs named -->
```csharp
Regex stamp = R(@"(?<year>\d{4})-(?<month>\d\d)");
RegexMatch when = stamp.Match("since 2026-11");
ReadOnlySpan<char> year = when.Groups["year"].Value;     // "2026"
string swappedNamed = stamp.Replace(arena, "2026-11", "${month}/${year}");
```

Named groups are numbered as .NET numbers them: after all the unnamed ones, in the order their names
first appear, so in `(?<y>\d)(\d)` the unnamed group is 1 and `y` is 2. Two groups with the same name
are one group, which holds what the last of them to match captured. `GroupNumberFromName` and
`GroupNameFromNumber` go from one to the other; the second gives null for a group without a name,
where .NET gives its number as text.

## What the patterns can say

- Literals, and escapes: `\t \n \r \f \v \e \0 \xHH \uHHHH`, and `\` before punctuation.
- `.` (anything but `\n`), and classes: `[abc]`, `[a-z]`, `[^...]`, `\d \w \s` and `\D \W \S`.
  These are ASCII: `\d` is `[0-9]`, `\w` is `[A-Za-z0-9_]`.
- Anchors: `^`, `$` (also before a final `\n`), `\A`, `\z`, `\Z`, `\b`, `\B`.
- Groups: `(...)` captures, up to 15 of them; `(?:...)` doesn't. `(?<name>...)` and `(?'name'...)`
  capture with a name (ASCII letters, digits and `_`). `|` between alternatives.
- Quantifiers: `*`, `+`, `?`, `{n}`, `{n,}`, `{n,m}`, each lazy with a `?` after it. A `{` that
  doesn't start a count is a literal `{`, as in .NET.

Options, as a `RegexOptions` argument: `IgnoreCase` (ASCII letters), `Multiline` (`^` and `$` at every
line), `Singleline` (`.` matches `\n` too):

<!-- sample: samples/Patterns.cs options -->
```csharp
var hello = Regex.Create(arena, "hello", RegexOptions.IgnoreCase);
Check("IgnoreCase", hello.Match("Say HeLLo").Index == 4);
```

When several matches start at the same place, the one you get is the one .NET's engine finds first:
`a|ab` matches `a`, a greedy quantifier takes as much as it can and a lazy one as little.

## What they can't

Backreferences (`\1`), lookahead and lookbehind (`(?=...)`, `(?<=...)` and their negations), groups
named by a number (`(?<2>...)`), atomic groups and inline options (`(?i)`). Each is refused with a reason
rather than matched differently. `Regex.Create` ends the program for an invalid pattern, as .NET
throws; for a pattern from a user, `TryCreate` returns the reason and where it is:

<!-- sample: samples/Patterns.cs errors -->
```csharp
bool valid = Regex.TryCreate(arena, "(abc", RegexOptions.None,
    out Regex _, out string error, out int position);
Check("TryCreate gives the reason",
    !valid && error == "missing )" && position == 0);
```

## Why it can't hang

.NET's engine backtracks: when one way of matching fails it tries the next, and some patterns have
exponentially many ways. This one runs every way at once, one character at a time (a Pike VM), so a
match takes time proportional to the text's length times the pattern's size, whatever the pattern:

<!-- sample: samples/Patterns.cs linear -->
```csharp
// A backtracking engine tries 2^40 ways to split the a's before it
// gives up; this one looks at each character once per instruction.
string aaa = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
Check("no catastrophic backtracking",
    !Regex.Create(arena, "(a+)+b").IsMatch(aaa));
```

That's also why backreferences and lookaround are missing: they need backtracking.

The memory a match works in is part of the compiled `Regex`, so use one `Regex` from one thread at a
time. Give each thread its own, from its own arena.

See also: the [`Regex` reference](../reference/regex.html).
