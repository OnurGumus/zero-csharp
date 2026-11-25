// Example: random numbers (lib/Random.cs) and the clock (lib/Clock.cs).
// A seeded Random gives the same numbers on every target, so the output below
// is the same everywhere; the clock is checked, not printed.
// Exit code = number of failed checks.
// Build: ./build.sh Dice

using System;
using System.Runtime.CompilerServices;

namespace DiceSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void Section(string section)
    {
        Console.WriteLine($"{(s_sectionFailures == 0 ? "PASS" : "FAIL")} {section} ({s_sectionChecks} checks)");
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_failures++;
            s_sectionFailures++;
            Console.WriteLine($"  FAIL: {name}");
        }
    }

    // Hides a value from the optimizer, so the work happens at run time.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Opaque(long value) => value;

    public static int Main()
    {
        Rolls();
        RandomChecks();
        ClockChecks();
        CalendarChecks();
        return s_failures;
    }

    static void Rolls()
    {
        // docs:dice
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
        // docs:end

        var bits = new Random(0);
        Console.WriteLine($"Random(0): {bits.NextUInt64():X16} {bits.NextUInt64():X16}");
    }

    static void RandomChecks()
    {
        var a = new Random(123);
        var b = new Random(123);
        bool same = true;
        for (int i = 0; i < 100; i++)
            same &= a.NextUInt64() == b.NextUInt64();
        Check("the same seed gives the same numbers", same);

        // A struct: the copy goes on from the same point.
        var original = new Random(5);
        original.Next();
        Random copy = original;
        Check("a copy repeats the original", copy.Next() == original.Next());

        Random unseeded = default;
        var zero = new Random(0);
        Check("default(Random) is Random(0)", unseeded.NextUInt64() == zero.NextUInt64());

        var r = new Random(99);
        bool inRange = true;
        for (int i = 0; i < 10_000; i++)
        {
            int n = r.Next(-3, 4);
            double d = r.NextDouble();
            float f = r.NextSingle();
            long l = r.NextInt64(-5_000_000_000, 5_000_000_000);
            inRange &= n >= -3 && n < 4 && d >= 0 && d < 1 && f >= 0 && f < 1;
            inRange &= l >= -5_000_000_000 && l < 5_000_000_000;
            inRange &= r.Next() >= 0 && r.Next(10) < 10;
        }
        Check("Next, NextDouble, NextSingle, NextInt64 stay in range", inRange);
        Check("an empty range gives its start", r.Next(5, 5) == 5 && r.Next(0) == 0 && r.NextInt64(9, 9) == 9);
        Check("the whole int range", r.Next(int.MinValue, int.MaxValue) < int.MaxValue);

        Span<byte> bytes = stackalloc byte[13];
        r.NextBytes(bytes);
        int nonzero = 0;
        foreach (byte x in bytes)
            if (x != 0)
                nonzero++;
        Check("NextBytes fills every byte", nonzero > 8);

        var timed = Random.FromClock();
        Check("FromClock", timed.Next(1, 7) is >= 1 and <= 6);
        Section("random");
    }

    static void ClockChecks()
    {
        // docs:time
        long start = Clock.Monotonic;
        long sum = 0;
        for (int i = 0; i < 1_000_000; i++)
            sum += Opaque(i);
        long elapsed = Clock.Monotonic - start; // nanoseconds
        // docs:end
        Check("the loop ran", sum == 499_999_500_000);
        Check("time doesn't go backwards", elapsed >= 0);

        bool forward = true;
        long previous = Clock.Monotonic;
        for (int i = 0; i < 1000; i++)
        {
            long now = Clock.Monotonic;
            forward &= now >= previous;
            previous = now;
        }
        Check("Monotonic never decreases", forward);

        // 2026-01-01 00:00 UTC. A machine whose clock is earlier is wrong.
        Check("UnixSeconds is after 2026", Clock.UnixSeconds >= 1_767_225_600);
        long ms = Clock.UnixMilliseconds;
        Check("UnixMilliseconds agrees", ms / 1000 >= 1_767_225_600 && ms / 1000 <= Clock.UnixSeconds);
        Section("clock");
    }

    static void CalendarChecks()
    {
        CalendarTime epoch = CalendarTime.FromUnixMilliseconds(0);
        Check("1970-01-01 00:00, a Thursday", epoch.Year == 1970 && epoch.Month == 1 && epoch.Day == 1 && epoch.Hour == 0 && epoch.DayOfWeek == 4);

        CalendarTime leap = CalendarTime.FromUnixMilliseconds(951_827_696_789);
        Check("2000-02-29 12:34:56.789, a Tuesday", leap.Year == 2000 && leap.Month == 2 && leap.Day == 29 && leap.Hour == 12 && leap.Minute == 34 && leap.Second == 56 && leap.Millisecond == 789 && leap.DayOfWeek == 2);

        CalendarTime before = CalendarTime.FromUnixMilliseconds(-1);
        Check("a millisecond before 1970", before.Year == 1969 && before.Month == 12 && before.Day == 31 && before.Hour == 23 && before.Minute == 59 && before.Second == 59 && before.Millisecond == 999 && before.DayOfWeek == 3);

        CalendarTime last = CalendarTime.FromUnixMilliseconds(253_402_300_799_999);
        Check("9999-12-31 23:59:59.999, a Friday", last.Year == 9999 && last.Month == 12 && last.Day == 31 && last.Millisecond == 999 && last.DayOfWeek == 5);

        bool roundTrip = true;
        for (long t = -5_000_000_000_000; t < 5_000_000_000_000; t += 7_777_777_777)
            roundTrip &= CalendarTime.FromUnixMilliseconds(Opaque(t)).ToUnixMilliseconds() == t;
        Check("ToUnixMilliseconds undoes FromUnixMilliseconds", roundTrip);

        Arena arena = Arena.Create(1024);
        // docs:calendar
        CalendarTime now = Clock.UtcNow;
        var text = StrBuilder.Create(arena);   // such as 2026-11-22 18:05:09
        text.Append($"{now.Year}-{now.Month:D2}-{now.Day:D2} ");
        text.Append($"{now.Hour:D2}:{now.Minute:D2}:{now.Second:D2}");
        // docs:end
        Check("UtcNow", now.Year >= 2026 && now.Month is >= 1 and <= 12 && text.Length == 19);
        Section("calendar");
    }
}
