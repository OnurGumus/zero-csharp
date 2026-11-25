// Example: fibers (lib/Fibers.cs), green threads that take turns on one
// system thread, and channels between them. Exit code = number of failed checks.
// Build: ./build.sh Fibers
//
// `Fibers overflow` overflows a fiber's stack on purpose, and must be stopped:
// by the guard page (a crash) where there is one, and by Run's check of the
// stack's bottom, with a message, on UEFI. test.sh checks both.

using System;
using System.Runtime;

namespace FibersSample;

public sealed class Log
{
    public int[] Entries = new int[64];
    public int Count;
    public void Add(int value) => Entries[Count++] = value;
}

public sealed class Pipe
{
    public Channel<int> Numbers = new Channel<int>(4);
    public Channel<int> Squares = new Channel<int>();
    public long Total;
}

public sealed class PingPong
{
    public Channel<int> Ping = new Channel<int>();
    public Channel<int> Pong = new Channel<int>();
    public int Rounds;
}

public sealed class Isolation
{
    public bool SawOwnSetting;
    public bool OtherSawCalloc;
}

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void Section(string section)
    {
        Out.Line($"{(s_sectionFailures == 0 ? "PASS" : "FAIL")} {section} ({s_sectionChecks} checks)");
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
            Out.Line($"  failed: {name}");
        }
    }

    // docs:scratch-body
    static void FillYieldCheck(
        scoped ScratchScope scratch, (Log log, int id) s)
    {
        Span<int> mine = scratch.AllocSpan<int>(256);
        mine.Fill(s.id);
        Fiber.Yield();
        Arena.Scratch(static other => other.AllocSpan<int>(256).Fill(-1));
        Fiber.Yield();
        bool ok = true;
        foreach (int value in mine)
            ok &= value == s.id;
        if (ok)
            s.log.Add(s.id);
    }
    // docs:end

    // Uses about 18 KB of a 16 KB stack, then yields, so Run looks at the
    // stack's bottom. Without a guard page the overflow lands in the guard
    // page's memory, which is still the fiber's own.
    static void Overflow(int seed)
    {
        Span<byte> big = stackalloc byte[18 * 1024];
        big.Fill((byte)seed);
        Fiber.Yield();
        Out.Line("not stopped: ", big[seed]);
    }

    static int Depth(int n) => n == 0 ? 0 : 1 + Depth(n - 1);

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "overflow")
        {
            Fiber.Spawn(args.Length, static seed => Overflow(seed),
                stackSize: 16 * 1024);
            Fiber.Run();
            return 0;
        }

        // docs:yield
        // Three fibers take turns: each logs its number and yields, so the
        // log interleaves them, in the order they were spawned.
        var log = new Log();
        for (int id = 1; id <= 3; id++)
            Fiber.Spawn((log, id), static s =>
            {
                for (int round = 0; round < 3; round++)
                {
                    s.log.Add(s.id);
                    Fiber.Yield();
                }
            });
        Fiber.Run();
        // docs:end
        int[] expected = [1, 2, 3, 1, 2, 3, 1, 2, 3];
        Check("nine entries", log.Count == 9);
        for (int i = 0; i < expected.Length; i++)
            Check($"entry {i}", log.Entries[i] == expected[i]);
        Check("no fibers left", Fiber.Count == 0);
        Section("Yield");

        // docs:pipeline
        // A pipeline: one fiber produces numbers, one squares them, and one
        // adds them up. Receive returns false once its channel is closed and
        // drained, which ends each loop.
        var pipe = new Pipe();
        Fiber.Spawn(pipe, static p =>
        {
            for (int i = 1; i <= 100; i++)
                p.Numbers.Send(i);
            p.Numbers.Close();
        });
        Fiber.Spawn(pipe, static p =>
        {
            while (p.Numbers.Receive(out int n))
                p.Squares.Send(n * n);
            p.Squares.Close();
        });
        Fiber.Spawn(pipe, static p =>
        {
            while (p.Squares.Receive(out int square))
                p.Total += square;
        });
        Fiber.Run();
        // docs:end
        Check("sum of squares 1..100", pipe.Total == 338350);
        Section("Channels");

        // Unbuffered channels hand each value straight to a waiting fiber.
        var game = new PingPong();
        Fiber.Spawn(game, static g =>
        {
            for (int i = 0; i < 1000; i++)
            {
                g.Ping.Send(i);
                g.Pong.Receive(out int back);
                if (back == i + 1)
                    g.Rounds++;
            }
            g.Ping.Close();
        });
        Fiber.Spawn(game, static g =>
        {
            while (g.Ping.Receive(out int n))
                g.Pong.Send(n + 1);
        });
        Fiber.Run();
        Check("1000 round trips", game.Rounds == 1000);
        Section("Ping-pong");

        // docs:sleep
        // Sleeping fibers wake in the order of their deadlines, whatever the
        // order they were spawned in.
        var woke = new Log();
        foreach (int ms in (int[])[30, 10, 20])
            Fiber.Spawn((woke, ms), static s =>
            {
                Fiber.Sleep(s.ms);
                s.woke.Add(s.ms);
            });
        long start = Clock.Monotonic;
        Fiber.Run();
        long took = (Clock.Monotonic - start) / 1_000_000;
        // docs:end
        Check("10 first", woke.Entries[0] == 10);
        Check("20 second", woke.Entries[1] == 20);
        Check("30 third", woke.Entries[2] == 30);
        Check("30 ms or more in all", took >= 30 && took < 2000);
        Section("Sleep");

        // Many fibers at once, each on a small stack.
        var counter = new Log();
        for (int i = 0; i < 2000; i++)
            Fiber.Spawn(counter, static c =>
            {
                Fiber.Yield();
                c.Count++;
            }, stackSize: 16 * 1024);
        Check("2000 alive", Fiber.Count == 2000);
        Fiber.Run();
        Check("2000 ran", counter.Count == 2000);
        Section("Many fibers");

        // A fiber's stack is its own: deep recursion on one, while another
        // yields in between.
        var depths = new Log();
        Fiber.Spawn(depths, static d => { Fiber.Yield(); d.Add(Depth(5000)); },
            stackSize: 1024 * 1024);
        Fiber.Spawn(depths, static d => { Fiber.Yield(); d.Add(Depth(10)); });
        Fiber.Run();
        Check("deep", depths.Entries[0] == 5000);
        Check("shallow", depths.Entries[1] == 10);
        Section("Stacks");

        // Each fiber has its own ObjectAllocator setting: one fiber's
        // AllocateObjectsHere doesn't reach another that runs while it's open.
        var isolation = new Isolation();
        Fiber.Spawn(isolation, static iso =>
        {
            Arena arena = Arena.Create(4096);
            Arena.ObjectScope objects;
            unsafe { objects = arena.AllocateObjectsHere(); } // SAFETY: nothing from the arena is kept
            using (objects)
            {
                Fiber.Yield();
                unsafe
                {
                    ObjectAllocator.Get(out var allocate, out void* _);
                    iso.SawOwnSetting = allocate != null;
                }
            }
            unsafe { arena.Destroy(); } // SAFETY: as above
        });
        Fiber.Spawn(isolation, static iso =>
        {
            unsafe
            {
                ObjectAllocator.Get(out var allocate, out void* _);
                iso.OtherSawCalloc = allocate == null;
            }
        });
        Fiber.Run();
        unsafe
        {
            ObjectAllocator.Get(out var mainAllocate, out void* _);
            Check("main is back on calloc", mainAllocate == null);
        }
        Check("the fiber kept its arena", isolation.SawOwnSetting);
        Check("the other fiber didn't get it", isolation.OtherSawCalloc);
        Section("Allocator settings");

        // docs:scratch
        // Each fiber has its own Arena.Scratch arena, so a fiber can wait
        // inside Scratch: both fill a buffer, yield, and find their values
        // still there after the other has opened and closed a scope.
        var intact = new Log();
        for (int id = 1; id <= 2; id++)
            Fiber.Spawn((intact, id),
                static s => Arena.Scratch(s, FillYieldCheck));
        Fiber.Run();
        // docs:end
        Check("both buffers intact", intact.Count == 2);
        Section("Scratch");

        // How long a switch takes: two fibers yielding to each other.
        const int Yields = 1_000_000;
        long switchesBefore = Fiber.Switches;
        for (int f = 0; f < 2; f++)
            Fiber.Spawn(0, static _ =>
            {
                for (int i = 0; i < Yields; i++)
                    Fiber.Yield();
            });
        start = Clock.Monotonic;
        Fiber.Run();
        long nanos = Clock.Monotonic - start;
        long switches = Fiber.Switches - switchesBefore;
        Check("every yield switched", switches >= 2 * Yields);
        Out.Line($"  {switches} switches in {nanos / 1_000_000} ms: {nanos / switches} ns each, there and back");
        Section("Speed");

        return s_failures;
    }
}
