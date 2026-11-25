---
title: Use the clock and random numbers
index: 19
---

# Use the clock and random numbers

`lib/Clock.cs` measures durations and tells the date and time, and `lib/Random.cs` makes random numbers
with .NET's method names. Both work on every target. `samples/Dice.cs` runs the examples below.

## Measure how long something takes

`Clock.Monotonic` is a count of nanoseconds from an arbitrary starting point, on a clock that only
moves forward: changing the time of day doesn't affect it. Subtract two readings:

<!-- sample: samples/Dice.cs time -->
```csharp
long start = Clock.Monotonic;
long sum = 0;
for (int i = 0; i < 1_000_000; i++)
    sum += Opaque(i);
long elapsed = Clock.Monotonic - start; // nanoseconds
```

The resolution depends on the target: well under a microsecond on Linux, macOS, Windows and UEFI, and
about a millisecond on DOS. Measure something that takes long enough, or repeat it.

## Tell the date and time

`Clock.UnixSeconds` and `Clock.UnixMilliseconds` count from 1970-01-01 00:00 UTC. `Clock.UtcNow` turns
the time into a `CalendarTime`, with the year, month, day, time of day and day of the week as fields:

<!-- sample: samples/Dice.cs calendar -->
```csharp
CalendarTime now = Clock.UtcNow;
var text = StrBuilder.Create(arena);   // such as 2026-11-22 18:05:09
text.Append($"{now.Year}-{now.Month:D2}-{now.Day:D2} ");
text.Append($"{now.Hour:D2}:{now.Minute:D2}:{now.Second:D2}");
```

`CalendarTime.FromUnixMilliseconds` and `ToUnixMilliseconds` convert in both directions, for any time
from year 1 to 9999. There are no time zones: the time is UTC, except on DOS and UEFI, where it is
whatever the PC's clock says, which is usually local time.

## Roll dice

`new Random(seed)` gives the same numbers for the same seed, on every target:

<!-- sample: samples/Dice.cs dice -->
```csharp
var random = new Random(42);
Console.Write("Ten rolls:");
for (int i = 0; i < 10; i++)
    Console.Write($" {random.Next(1, 7)}");
Console.WriteLine();

// How often each face comes up in 60,000 rolls: about 10,000 each.
Span<int> counts = stackalloc int[7];
var dice = new Random(7);
for (int i = 0; i < 60_000; i++)
    counts[dice.Next(1, 7)]++;
for (int face = 1; face <= 6; face++)
    Console.WriteLine($"{face}: {counts[face]}");

Span<int> cards = stackalloc int[10];
for (int i = 0; i < cards.Length; i++)
    cards[i] = i + 1;
var shuffler = new Random(3);
shuffler.Shuffle(cards);
Console.Write("Shuffled:");
foreach (int card in cards)
    Console.Write($" {card}");
Console.WriteLine();
```

```text
Ten rolls: 1 3 5 6 6 5 5 6 5 4
1: 10094
2: 10190
3: 9784
4: 9876
5: 9953
6: 10103
Shuffled: 10 5 9 1 8 3 4 2 6 7
```

`Next(min, max)` excludes `max`, as in .NET, and every number in the range is equally likely.
`NextDouble`, `NextSingle`, `NextInt64`, `NextBytes` and `Shuffle` work as .NET's do.
`Random.FromClock()` seeds from the clock, for numbers that differ from run to run.

The generator is xoshiro256\*\*, which is fast and passes the usual statistical tests. It is not for
anything an attacker could gain from predicting: keys, tokens, passwords.

## Pass a Random by ref

`Random` is a struct that holds its state, unlike .NET's class. A copy carries on from the same point,
so it repeats the numbers the original gives next. Pass it with `ref`, and keep it in a field or local
that isn't `readonly`:

```csharp no-sample
static int Roll(ref Random random) => random.Next(1, 7);
```

`default(Random)` works, and gives the same numbers as `new Random(0)`.

See also: the [`Clock`](../reference/clock.html) and [`Random`](../reference/random.html) references.
