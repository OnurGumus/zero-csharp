// Test: channels between fibers (lib/Fibers.cs), sized for a small machine.
// SmallFibers tests the fibers themselves; Fibers.cs checks more and doesn't
// fit in a Commodore 64 (./build.sh --c64). It runs on every target with
// fibers: not WebAssembly, which can't switch stacks.
// Exit code = number of failed checks.
// Build: ./build.sh SmallChannels

using System;

namespace SmallChannelsSample;

public sealed class Pipe
{
    public Channel<int> Numbers = new Channel<int>(2);
    public Channel<int> Squares = new Channel<int>();
    public int Total;
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

    public static int Main()
    {
        // A pipeline: numbers, their squares, their sum, through a buffered
        // channel (room for two) and an unbuffered one.
        var pipe = new Pipe();
        Fiber.Spawn(pipe, static p =>
        {
            for (int i = 1; i <= 20; i++)
                p.Numbers.Send(i);
            p.Numbers.Close();
        }, stackSize: Fiber.MinStackSize);
        Fiber.Spawn(pipe, static p =>
        {
            while (p.Numbers.Receive(out int n))
                p.Squares.Send(n * n);
            p.Squares.Close();
        }, stackSize: Fiber.MinStackSize);
        Fiber.Spawn(pipe, static p =>
        {
            while (p.Squares.Receive(out int square))
                p.Total += square;
        }, stackSize: Fiber.MinStackSize);
        Fiber.Run();
        Check("sum of squares 1..20", pipe.Total == 2870);
        Check("both closed", pipe.Numbers.IsClosed && pipe.Squares.IsClosed);
        Check("nothing left", pipe.Numbers.Count == 0 && pipe.Squares.Count == 0);
        Check("no fibers left", Fiber.Count == 0);
        End("Channels");

        // Outside a fiber, TryReceive takes what's there without waiting.
        var buffer = new Channel<int>(3);
        Fiber.Spawn(buffer, static b =>
        {
            b.Send(7);
            b.Send(8);
        }, stackSize: Fiber.MinStackSize);
        Fiber.Run();
        Check("two buffered", buffer.Count == 2);
        Check("first in, first out", buffer.TryReceive(out int first) && first == 7
            && buffer.TryReceive(out int second) && second == 8);
        Check("then empty", !buffer.TryReceive(out _));
        End("TryReceive");

        return s_failures;
    }
}
