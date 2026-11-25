// Example: a microcontroller's "hello world": the board's LED blinking, from
// a C# method the timer interrupt calls (lib/Board.cs). For the Pico 2 and the
// Uno R4 only: ./build.sh --pico2 Blink, then ./run.sh --pico2 Blink, or
// ./run.sh --pico2 --board Blink on a real Pico 2.
//
// It also checks itself, as the tests do: that the handler ran as often as
// asked, that the LED followed, and that a hardware register reads back.

using System;
using System.Runtime.InteropServices;

namespace BlinkSample;

public static class Program
{
    // Changed by the interrupt handler, read by Main.
    static int s_ticks;
    static int s_toggles;

    // docs:handler
    // 20 times a second; every fifth tick turns the LED over: two blinks
    // a second.
    [UnmanagedCallersOnly]
    static void OnTick()
    {
        s_ticks++;
        if (s_ticks % 5 == 0)
        {
            Board.Led(!Board.LedIsOn);
            s_toggles++;
        }
    }
    // docs:end

    // For the second test, a handler that keeps references of its own on
    // its stack, through a few calls, as one that does real work would.
    [UnmanagedCallersOnly]
    static void OnBusyTick()
    {
        s_ticks++;
        s_spilled += Spill(s_others[0], s_others[1], 40);
    }

    static int s_spilled;
    static readonly string[] s_others = { "one", "two" };

    static int Spill(string x, string y, int depth) =>
        depth == 0 ? x.Length : Spill(y, x, depth - 1) + y.Length;

    static int s_failures;
    static readonly string[] s_words = { "alpha", "beta", "gamma" };

    struct Three
    {
        public string A, B, C;
    }

    // Passes three references down, rotated one more place at each level,
    // and checks after each call that its own are still the ones it got.
    // They are in a struct passed by ref, so they live on the stack managed
    // code keeps references on, and are read back from there.
    static bool Rotate(ref Three mine, int depth)
    {
        Three next = new Three { A = mine.B, B = mine.C, C = mine.A };
        bool ok = depth == 0 || Rotate(ref next, depth - 1);
        int r = 12 - depth; // how far this level's are rotated
        return ok && Rotated(in mine, r) && Rotated(in next, r + 1);
    }

    static bool Rotated(in Three three, int r) =>
        (object)three.A == s_words[r % 3] && (object)three.B == s_words[(r + 1) % 3]
        && (object)three.C == s_words[(r + 2) % 3];

    static void Check(string what, bool ok)
    {
        if (!ok)
        {
            Console.Write("  FAIL: ");
            Console.WriteLine(what);
            s_failures++;
        }
    }

    public static int Main()
    {
        Console.WriteLine("Blinking for 2.5 seconds...");
        Board.Led(false);

        // docs:blink
        unsafe { Check("the timer starts", Board.StartTicks(20, &OnTick)); }
        while (s_ticks < 50)
            Board.WaitForInterrupt();
        Board.StopTicks();
        // docs:end

        Check("50 ticks", s_ticks == 50);
        Check("10 toggles", s_toggles == 10);
        Check("the LED is off after an even number", !Board.LedIsOn);
        Board.Led(true);
        Check("the LED turns on", Board.LedIsOn);
        Board.Led(false);

        // SysTick's control register, the same on every Cortex-M: stopped now,
        // so its enable bit reads 0; its reload register reads back a value.
        const nuint SysTickControl = 0xE000E010, SysTickReload = 0xE000E014;
        Check("SysTick stopped", (Board.Read(SysTickControl) & 1) == 0);
        Board.Write(SysTickReload, 12345);
        Check("a register reads back", Board.Read(SysTickReload) == 12345);

        Console.WriteLine($"{s_ticks} ticks, {s_toggles} toggles");

        // The handler also interrupts C# code in the middle of its work.
        // Managed code keeps the references in its locals on a stack of its
        // own; the handler's must be another one, or these would be
        // overwritten. 1000 ticks a second, while the main program recurses.
        s_ticks = 0;
        int rounds = 0;
        bool intact = true;
        unsafe { Check("the fast timer starts", Board.StartTicks(1000, &OnBusyTick)); }
        while (s_ticks < 200)
        {
            Three first = new Three { A = s_words[0], B = s_words[1], C = s_words[2] };
            intact &= Rotate(ref first, depth: 12);
            rounds++;
        }
        Board.StopTicks();
        Check("the handler did its work", s_spilled > 0);
        Board.Led(false);
        Check("references intact under interrupts", intact);
        Check("interrupted many times", rounds > 200);
        Console.WriteLine(intact
            ? "200 ticks during C# work, references intact"
            : "200 ticks during C# work, references OVERWRITTEN");
        Console.WriteLine(s_failures == 0 ? "PASS" : "FAILED");
        return s_failures;
    }
}
