// Example: a Commodore 64 RAM Expansion Unit (C64.Reu), and the clock that
// keeps going while interrupts are off. Exit code = number of failed checks.
// Build: ./build.sh --c64 C64Reu
// Run:   C64_VICE_OPTIONS="-reu -reusize 512" ./run.sh --c64 C64Reu
// (./test.sh --c64 runs every C64 sample with a 512 KB REU.)
//
// An REU holds memory the 6502 can't address: C64.Reu.Read and Write copy
// between it and the C64's own, so a program can keep far more data than
// fits in 64 KB and bring in a piece at a time.

using System;

namespace C64ReuSample;

public static class Program
{
    static int s_checks, s_failures;

    static void Check(string name, bool ok)
    {
        s_checks++;
        if (!ok)
        {
            s_failures++;
            Console.WriteLine($"FAIL {name}");
        }
    }

    static bool Same(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    public static int Main()
    {
        int size = C64.Reu.Size;
        if (size == 0)
        {
            Console.WriteLine("No REU (VICE: x64sc -reu -reusize 512).");
            return 1;
        }
        Console.WriteLine($"REU: {size / 1024} KB");
        Check("the size is a power of two, 128 KB or more", size >= 128 * 1024 && (size & (size - 1)) == 0);

        // docs:reu
        Span<byte> bytes = stackalloc byte[300];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 7);
        // Across the boundary between the first and the second 64 KB.
        C64.Reu.Write(0xFF80, bytes);

        Span<byte> back = stackalloc byte[300];
        C64.Reu.Read(0xFF80, back);
        // docs:end
        Check("what was written reads back", Same(bytes, back));

        Span<byte> part = stackalloc byte[3];
        C64.Reu.Read(0xFF80 + 100, part);
        Check("a part of it, from the middle", part[0] == bytes[100] && part[2] == bytes[102]);

        // Measuring the size writes at 0 and at powers of two: it puts back
        // what was there.
        C64.Reu.Write(0, bytes.Slice(0, 4));
        C64.Reu.Write(0x10000, bytes.Slice(4, 4));
        int again = C64.Reu.Size;
        Span<byte> four = stackalloc byte[4];
        C64.Reu.Read(0, four);
        bool kept = Same(four, bytes.Slice(0, 4));
        C64.Reu.Read(0x10000, four);
        Check("measuring the size keeps the memory", again == size && kept && Same(four, bytes.Slice(4, 4)));

        // Past the end, the address wraps around to 0.
        C64.Reu.Write(size - 2, bytes.Slice(10, 4));
        C64.Reu.Read(0, four.Slice(0, 2));
        Check("past the end is the start", four[0] == bytes[12] && four[1] == bytes[13]);

        C64.Reu.Read(0, Span<byte>.Empty);
        C64.Reu.Write(0, ReadOnlySpan<byte>.Empty);
        Check("nothing to copy copies nothing", true);

        // The clock under Stopwatch, Environment.TickCount64 and Fiber.Sleep
        // goes on while the KERNAL's interrupt is off, and so does a sleep:
        // then it counts tenths of a second, on CIA 2's time-of-day clock.
        C64.KernalInterrupt = false;
        long started = Environment.TickCount64;
        uint ticks = C64.Ticks;
        for (int i = 0; i < 10; i++)
            C64.WaitFrame();
        long tenFrames = Environment.TickCount64 - started;
        long slept = Environment.TickCount64;
        Fiber.Sleep(300);
        slept = Environment.TickCount64 - slept;
        bool jiffiesStood = C64.Ticks == ticks;
        C64.KernalInterrupt = true;
        // Ten frames are 200 ms on a PAL machine (50 a second), 167 on NTSC,
        // here in tenths.
        Check("the clock runs with the KERNAL's interrupt off", tenFrames >= 100 && tenFrames <= 300);
        Check("a sleep ends with it off", slept >= 300 && slept <= 450);
        Check("C64.Ticks, the KERNAL's count, stood still", jiffiesStood);

        // And with it on again, the sixtieths: no time is counted twice.
        started = Environment.TickCount64;
        Fiber.Sleep(500);
        long sleptOn = Environment.TickCount64 - started;
        Check("a sleep with it on again", sleptOn >= 500 && sleptOn <= 540);

        if (s_failures == 0)
            Console.WriteLine($"All {s_checks} checks passed.");
        return s_failures;
    }
}
