// Example: fibers (lib/Fibers.cs), green threads that take turns on one
// system thread, and channels and pipes between them. Exit code = number of failed checks.
// Build: ./build.sh Fibers
//
// `Fibers overflow` overflows a fiber's stack on purpose, and must be stopped:
// by the guard page (a crash) where there is one, and by Run's check of the
// stack's bottom, with a message, on UEFI and DOS. test.sh checks both.

using System;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace FibersSample;

public sealed class Log
{
    public int[] Entries = new int[64];
    public int Count;
    public void Add(int value) => Entries[Count++] = value;
}

public sealed class Stages
{
    public Channel<int> Numbers = new Channel<int>(4);
    public Channel<int> Squares = new Channel<int>();
    public long Total;
}

public sealed class Transfer
{
    public Pipe Pipe;
    public int Sent, Received, Wrong, MostBuffered;
    public bool WriteRefused;
    public System.IO.MemoryStream Copy;
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

    // Logs its id if two spans of 12 KB came out side by side, which only
    // one block gives: a block of its own starts behind a header.
    static void TwoSpans(scoped ScratchScope scratch, (Log log, int id) s)
    {
        Span<byte> first = scratch.AllocSpan<byte>(12_000);
        Span<byte> second = scratch.AllocSpan<byte>(12_000);
        bool adjacent;
        unsafe { adjacent = Unsafe.AreSame(ref Unsafe.Add(ref first[0], 12_000), ref second[0]); } // SAFETY: one past the end, compared and never read
        if (adjacent)
            s.log.Add(s.id);
    }

    // Fills scratch memory with its id, yields while the other fiber does the
    // same, and logs its id if the memory still holds it: small enough for a
    // microcontroller's fiber stack when the memory is on it.
    static void FillYieldSmall(scoped ScratchScope scratch, (Log log, int id) s)
    {
        Span<int> mine = scratch.AllocSpan<int>(64);
        mine.Fill(s.id);
        Fiber.Yield();
        bool ok = true;
        foreach (int value in mine)
            ok &= value == s.id;
        if (ok)
            s.log.Add(s.id);
    }

    // Uses an eighth more than the smallest stack (18 KB of 16, or 1.1 KB of
    // 1 on DOS or a microcontroller), then yields, so Run looks at the stack's
    // bottom. Without a guard page the overflow lands in the guard page's
    // memory (a page: 4 KB, or 1 KB), which is still the fiber's own.
    static void Overflow(int seed)
    {
        int size = Fiber.MinStackSize + Fiber.MinStackSize / 8;
        Span<byte> big = stackalloc byte[size];
        big.Fill((byte)seed);
        Fiber.Yield();
        Out.Line("not stopped: ", big[seed]);
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

    static int Depth(int n) => n == 0 ? 0 : 1 + Depth(n - 1);

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "overflow")
        {
            Fiber.Spawn(args.Length, static seed => Overflow(seed),
                stackSize: Fiber.MinStackSize);
            Fiber.Run();
            return 0;
        }

        if (args.Length > 0 && args[0] == "deadlock")
        {
            // Taken outside the fibers, then wanted by one: it can never be
            // left while the fiber waits, so the program must stop.
            var taken = new object();
            System.Threading.Monitor.Enter(taken);
            Fiber.Spawn(taken, static t => { lock (t) { } });
            Fiber.Run();
            return 0;
        }

        // A microcontroller has a few hundred KB of RAM, or less, and a DOS
        // machine a few MB: fewer fibers, on smaller stacks.
        bool small = Fiber.DefaultStackSize < 64 * 1024;

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
        var stages = new Stages();
        Fiber.Spawn(stages, static p =>
        {
            for (int i = 1; i <= 100; i++)
                p.Numbers.Send(i);
            p.Numbers.Close();
        });
        Fiber.Spawn(stages, static p =>
        {
            while (p.Numbers.Receive(out int n))
                p.Squares.Send(n * n);
            p.Squares.Close();
        });
        Fiber.Spawn(stages, static p =>
        {
            while (p.Squares.Receive(out int square))
                p.Total += square;
        });
        Fiber.Run();
        // docs:end
        Check("sum of squares 1..100", stages.Total == 338350);
        Section("Channels");

        // docs:pipe
        // A pipe streams bytes through a buffer of fixed size from an arena:
        // here 64 bytes carry 10,000. The writer waits whenever the buffer is
        // full, so it never runs more than 64 bytes ahead of the reader.
        Arena arena = Arena.Create(4096);
        var transfer = new Transfer { Pipe = new Pipe(arena, 64) };
        Fiber.Spawn(transfer, static t =>
        {
            Span<byte> chunk = stackalloc byte[100];
            while (t.Sent < 10_000)
            {
                int n = Math.Min(chunk.Length, 10_000 - t.Sent);
                for (int i = 0; i < n; i++)
                    chunk[i] = (byte)(t.Sent + i);
                t.Pipe.Write((ReadOnlySpan<byte>)chunk.Slice(0, n));
                t.Sent += n;
            }
            t.Pipe.Close();
        });
        Fiber.Spawn(transfer, static t =>
        {
            Span<byte> buffer = stackalloc byte[37];
            int n;
            while ((n = t.Pipe.Read(buffer)) > 0)
            {
                for (int i = 0; i < n; i++)
                    if (buffer[i] != (byte)(t.Received + i))
                        t.Wrong++;
                t.Received += n;
                t.MostBuffered = Math.Max(t.MostBuffered, t.Pipe.Count + n);
            }
        });
        Fiber.Run();
        // docs:end
        Check("all 10,000 bytes", transfer.Received == 10_000);
        Check("in order", transfer.Wrong == 0);
        Check("never more than 64 held", transfer.MostBuffered <= 64);

        // Text goes in as UTF-8. A reader that closes the pipe early makes
        // the writer's next Write return false instead of waiting forever.
        var text = new Transfer { Pipe = new Pipe(arena, 8) };
        Fiber.Spawn(text, static t =>
        {
            t.Pipe.Write("h\u00e9llo");
            for (int i = 0; i < 100 && !t.WriteRefused; i++)
                t.WriteRefused = !t.Pipe.Write("more");
        });
        Fiber.Spawn(text, static t =>
        {
            Span<byte> word = stackalloc byte[6];
            int got = 0;
            while (got < 6)
                got += t.Pipe.Read(word.Slice(got));
            if (Same(word, "h\u00e9llo"u8))
                t.Received = got;
            t.Pipe.Close();
        });
        Fiber.Run();
        Check("UTF-8 text", text.Received == 6);
        Check("Write refused after Close", text.WriteRefused);
        Check("closed", text.Pipe.IsClosed);
        // docs:pipe-stream
        // AsStream makes the pipe a System.IO.Stream, for code that takes one:
        // CopyTo here reads until the writer disposes its end.
        var streamed = new Transfer
        {
            Pipe = new Pipe(arena, 16),
            Copy = new System.IO.MemoryStream(),
        };
        Fiber.Spawn(streamed, static t =>
        {
            using System.IO.Stream output = t.Pipe.AsStream();
            for (int i = 0; i < 10; i++)
                output.Write("0123456789"u8);
        });
        Fiber.Spawn(streamed, static t => t.Pipe.AsStream().CopyTo(t.Copy));
        Fiber.Run();
        // docs:end
        Check("streamed 100 bytes", streamed.Copy.Length == 100 && streamed.Copy.AsSpan()[99] == (byte)'9');
        unsafe { arena.Destroy(); } // SAFETY: the pipes are done with
        Section("Pipe");

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

        // Many fibers at once, each on the smallest stack: 2000, or 6 on DOS
        // or a microcontroller (the Uno R4 has 32 KB of RAM).
        var counter = new Log();
        int many = small ? 6 : 2000;
        for (int i = 0; i < many; i++)
            Fiber.Spawn(counter, static c =>
            {
                Fiber.Yield();
                c.Count++;
            }, stackSize: Fiber.MinStackSize);
        Check("all alive", Fiber.Count == many);
        Fiber.Run();
        Check("all ran", counter.Count == many);
        Section("Many fibers");

        // A fiber's stack is its own: deep recursion on one, while another
        // yields in between.
        var depths = new Log();
        int deep = small ? 60 : 5000;
        Fiber.Spawn((depths, deep),
            static d => { Fiber.Yield(); d.depths.Add(Depth(d.deep)); },
            stackSize: small ? 4 * 1024 : 1024 * 1024);
        Fiber.Spawn(depths, static d => { Fiber.Yield(); d.Add(Depth(10)); });
        Fiber.Run();
        Check("deep", depths.Entries[0] == deep);
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
        // A fiber's scratch arena keeps one block, 16 KB unless told otherwise
        // (2 KB on a small machine): two spans of 12 KB don't both fit in it.
        // In the 32 KB that Arena.ScratchSize asks for, they do.
        var together = new Log();
        Fiber.Spawn((together, 1), static s => Arena.Scratch(s, TwoSpans));
        Fiber.Run();
        Arena.ScratchSize = 32 * 1024;
        Fiber.Spawn((together, 2), static s => Arena.Scratch(s, TwoSpans));
        Fiber.Run();
        Arena.ScratchSize = 0;
        Fiber.Spawn((together, 3), static s => Arena.Scratch(s, TwoSpans));
        Fiber.Run();
        // (With Arena.GuardPages on, each Scratch call gets blocks of its own.)
        Check("ScratchSize sets the first block",
            (together.Count == 1 && together.Entries[0] == 2) || Arena.GuardPages);
        // Each fiber waits inside a Scratch call in a buffer of its own,
        // which stands in for its scratch arena until the call returns; then
        // it does the same in its own arena.
        var buffered = new Log();
        for (int id = 1; id <= 2; id++)
            Fiber.Spawn((buffered, id), static s =>
            {
                byte[] buffer = new byte[4096];
                // SAFETY: nothing else uses buffer.
                unsafe { Arena.Scratch(buffer, s, FillYieldCheck); }
                Arena.Scratch(s, FillYieldCheck);
            });
        Fiber.Run();
        Check("a buffer of each fiber's", buffered.Count == 4);
        var onStack = new Log();
        for (int id = 1; id <= 2; id++)
            Fiber.Spawn((onStack, id), static s => Arena.ScratchOnStack(1024, s, FillYieldSmall));
        Fiber.Run();
        Check("each fiber's own stack", onStack.Count == 2);
        Section("Scratch");

        // lock between fibers (lib/Monitor.cs): a fiber that holds it and
        // yields keeps the others out; they yield until it's left.
        var gate = new object();
        var order = new Log();
        for (int id = 1; id <= 3; id++)
            Fiber.Spawn((gate, order, id), static s =>
            {
                lock (s.gate)
                {
                    s.order.Add(s.id);
                    Fiber.Yield(); // the others run, and wait
                    s.order.Add(s.id);
                }
            });
        Fiber.Run();
        int[] pairs = [1, 1, 2, 2, 3, 3];
        bool paired = order.Count == 6;
        for (int i = 0; paired && i < pairs.Length; i++)
            paired = order.Entries[i] == pairs[i];
        Check("each fiber's two entries together", paired);
        var fiberLock = new System.Threading.Lock();
        bool reentered = false, othersOut = true;
        Fiber.Spawn(fiberLock, static l =>
        {
            l.Enter();
            Fiber.Yield();
            Fiber.Yield();
            l.Exit();
        });
        Fiber.Spawn((fiberLock, 0), static s =>
        {
            // Spawned second: finds it taken, and can't get it in time.
            if (s.fiberLock.TryEnter(0))
                s.fiberLock.Exit();
        });
        Fiber.Run();
        lock (fiberLock)
            lock (fiberLock)
                reentered = fiberLock.IsHeldByCurrentThread;
        othersOut = !fiberLock.IsHeldByCurrentThread;
        Check("taken again by its holder, then left", reentered && othersOut);
        Section("lock");

        // How long a switch takes: two fibers yielding to each other.
        int yields = small ? 100_000 : 1_000_000;
        long switchesBefore = Fiber.Switches;
        for (int f = 0; f < 2; f++)
            Fiber.Spawn(yields, static count =>
            {
                for (int i = 0; i < count; i++)
                    Fiber.Yield();
            });
        start = Clock.Monotonic;
        Fiber.Run();
        long nanos = Clock.Monotonic - start;
        long switches = Fiber.Switches - switchesBefore;
        Check("every yield switched", switches >= 2 * yields);
        Out.Line($"  {switches} switches in {nanos / 1_000_000} ms: {nanos / switches} ns each, there and back");
        Section("Speed");

        return s_failures;
    }
}
