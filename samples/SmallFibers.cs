// Test: fibers (lib/Fibers.cs), sized for a small machine: taking turns,
// sleeping, and stacks of their own. SmallChannels tests channels. Fibers.cs
// checks more and doesn't fit in a Commodore 64 (./build.sh --c64).
// It runs on every target with fibers: not WebAssembly, which can't switch
// stacks. Exit code = number of failed checks.
// Build: ./build.sh SmallFibers

using System;

namespace SmallFibersSample;

// 16-bit entries: arithmetic on them is cheaper for an 8-bit processor.
public sealed class Log
{
    public short[] Entries = new short[16];
    public int Count;
    public void Add(int value) => Entries[Count++] = (short)value;
}

// What a fiber is given. Every fiber here gets a class, so the program has
// one copy of Fiber.Spawn's code (each kind of struct would add another,
// 2 KB on a C64).
public sealed class Turn
{
    public Log Log;
    public int Id;
    public Turn(Log log, int id) { Log = log; Id = id; }
}

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void End(string section)
    {
        Out.Write(s_sectionFailures == 0 ? "PASS " : "FAIL ");
        Out.Write(section);
        Out.Write(" (");
        Out.Write(s_sectionChecks);
        Out.Line(" checks)");
        s_failures += s_sectionFailures;
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_sectionFailures++;
            Out.Write("  failed: ");
            Out.Line(name);
        }
    }

    // Recurses, yields at the bottom, and counts on the way back up: two
    // fibers waiting at the bottom at once must each find their own frames.
    static int Down(int n)
    {
        if (n == 0)
        {
            Fiber.Yield();
            return 0;
        }
        return 1 + Down(n - 1);
    }

    // Not recursive, so a compiler may give its locals a fixed place in
    // memory instead of a stack frame (llvm-mos does, for the C64) when it
    // can tell the function never runs twice at once. Two fibers wait inside
    // it at once; while one waits, the other overwrites the array, so each
    // must find its six values (too many for the registers a switch saves)
    // where it left them.
    static int ReadYieldSum(short[] shared, int seed)
    {
        for (int i = 0; i < 6; i++)
            shared[i] = (short)(seed + i);
        short a = shared[0], b = shared[1], c = shared[2];
        short d = shared[3], e = shared[4], f = shared[5];
        Fiber.Yield();
        return a + b + c + d + e + f;
    }

    public static int Main()
    {
        // What the fibers are given, made once: objects made between spawns
        // would stay between the memory each fiber frees, in pieces too
        // small for the next fibers on a machine with a few KB to spare.
        var log = new Log();
        Turn[] turns = [new Turn(log, 1), new Turn(log, 2), new Turn(log, 3)];

        // Three fibers take turns, in the order they were spawned.
        for (int id = 1; id <= 3; id++)
            Fiber.Spawn(turns[id - 1], static t =>
            {
                for (int round = 0; round < 3; round++)
                {
                    t.Log.Add(t.Id);
                    Fiber.Yield();
                }
            }, stackSize: Fiber.MinStackSize);
        Check("three alive", Fiber.Count == 3);
        Fiber.Run();
        bool order = log.Count == 9;
        for (int i = 0; i < log.Count; i++)
            order &= log.Entries[i] == i % 3 + 1;
        Check("turns in order", order);
        Check("none left", Fiber.Count == 0);
        End("Yield");

        // Sleepers wake in the order of their deadlines: spawned to sleep
        // 200 and 100 ms, they wake after 100 and 200. (A gap of 100 ms: a
        // C64 takes tens of milliseconds to start a fiber and read the clock.)
        log.Count = 0;
        for (int k = 0; k < 2; k++)
        {
            turns[k].Id = 200 - 100 * k;
            Fiber.Spawn(turns[k], static t =>
            {
                Fiber.Sleep(t.Id);
                t.Log.Add(t.Id);
            }, stackSize: Fiber.MinStackSize);
        }
        Fiber.Run();
        Check("woke in order", log.Count == 2 && log.Entries[0] == 100
            && log.Entries[1] == 200);
        End("Sleep");

        // Two fibers, each waiting deep inside its own recursion.
        log.Count = 0;
        for (int id = 1; id <= 2; id++)
        {
            turns[id - 1].Id = id;
            Fiber.Spawn(turns[id - 1], static t => t.Log.Add(Down(t.Id * 10)),
                stackSize: Fiber.MinStackSize);
        }
        Fiber.Run();
        Check("both stacks intact", log.Count == 2 && log.Entries[0] == 10
            && log.Entries[1] == 20);
        log.Count = 0;
        for (int id = 1; id <= 2; id++)
            Fiber.Spawn(turns[id - 1], static t => t.Log.Add(ReadYieldSum(t.Log.Entries, t.Id * 10)),
                stackSize: Fiber.MinStackSize);
        Fiber.Run();
        Check("both kept their locals", log.Count == 2 && log.Entries[0] == 75
            && log.Entries[1] == 135);
        End("Stacks");

        return s_failures;
    }
}
