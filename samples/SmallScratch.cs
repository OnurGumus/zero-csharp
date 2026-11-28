// Test: Arena.Scratch (lib/Scratch.cs) sized for a small machine, with
// Arena.ScratchSize and Arena.ScratchMaxCapacity, alone and in fibers.
// Scratch.cs checks more and doesn't fit in a Commodore 64 (./build.sh --c64).
// It runs on every target with fibers: not WebAssembly, which can't switch
// stacks. Exit code = number of failed checks.
// Build: ./build.sh SmallScratch

using System;

namespace SmallScratchSample;

public sealed class Log
{
    public short[] Entries = new short[4];
    public int Count;
    public void Add(int value) => Entries[Count++] = (short)value;
}

// What a fiber is given: a class, as in SmallFibers, so the program has one
// copy of Fiber.Spawn's code.
public sealed class Turn
{
    public Log Log;
    public int Id;
    public Turn(Log log, int id) { Log = log; Id = id; }
}

public static class Program
{
    static int s_failures;

    static void Check(string name, bool ok)
    {
        Out.Write(ok ? "PASS " : "FAIL ");
        Out.Line(name);
        if (!ok)
            s_failures++;
    }

    // Fills scratch memory with its id, yields while the other fiber does
    // the same in its own scratch arena, and logs its id if the memory still
    // holds it.
    static void FillYieldCheck(scoped ScratchScope scratch, Turn t)
    {
        Span<byte> mine = scratch.AllocSpan<byte>(32);
        mine.Fill((byte)t.Id);
        Fiber.Yield();
        bool intact = true;
        foreach (byte b in mine)
            intact &= b == t.Id;
        if (intact)
            t.Log.Add(t.Id);
    }

    public static int Main()
    {
        // Each thread and fiber that calls Arena.Scratch keeps a scratch
        // arena of its own: 64 bytes here instead of 2 KB, fixed at that.
        // On a C64 the three arenas come out of the few hundred bytes of
        // heap this program leaves, and any change that grows a fiber
        // program by more than what is spare fails the test with "Arena:
        // out of memory". With 128 that was 400 bytes, then a few dozen as
        // the runtime grew; 64 gives back about 200.
        Arena.ScratchSize = 64;
        Arena.ScratchMaxCapacity = 64;

        int sum = Arena.Scratch(static (scoped ScratchScope scratch) =>
        {
            Span<byte> bytes = scratch.AllocSpan<byte>(32);
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)i;
            int total = 0;
            foreach (byte b in bytes)
                total += b;
            return total;
        });
        Check("scratch memory", sum == 31 * 32 / 2);

        var log = new Log();
        Turn[] turns = [new Turn(log, 1), new Turn(log, 2)];
        for (int i = 0; i < turns.Length; i++)
            Fiber.Spawn(turns[i], static t => Arena.Scratch(t, FillYieldCheck),
                stackSize: Fiber.MinStackSize);
        Fiber.Run();
        Check("each fiber's own scratch memory", log.Count == 2
            && log.Entries[0] == 1 && log.Entries[1] == 2);

        return s_failures;
    }
}
