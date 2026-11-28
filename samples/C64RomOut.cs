// Example: the 8 KB of RAM under the Commodore 64's KERNAL ROM, with the ROM
// switched out (C64.KernalRom), while printing, reading a typed line, the
// clock and sleeping go on working. Exit code = number of failed checks.
// Build: ./build.sh --c64 C64RomOut    Run: ./run.sh --c64 C64RomOut
// (./test.sh --c64 types the line in tests/input/C64RomOut.txt.)

using System;

namespace C64RomOutSample;

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

    static byte Pattern(int i) => (byte)(i * 13 + 1);

    public static int Main()
    {
        Check("the KERNAL ROM is in at the start", C64.KernalRom);
        Span<byte> ram = C64.RamUnderKernal;
        Check("8,065 bytes, $E000-$FF80", ram.Length == 8065);

        // Writing reaches the RAM with the ROM in; reading gives the ROM.
        for (int i = 0; i < ram.Length; i++)
            ram[i] = Pattern(i);
        int differ = 0;
        for (int i = 0; i < ram.Length; i++)
            if (ram[i] != Pattern(i))
                differ++;
        Check("with the ROM in, reading gives the ROM", differ > ram.Length / 2);

        // docs:rom-out
        C64.KernalRom = false;
        // Now the RAM is there, to read and write like any other memory.
        bool same = true;
        for (int i = 0; i < ram.Length; i++)
            same &= ram[i] == Pattern(i);
        Console.WriteLine("Printed with the KERNAL ROM out.");
        // docs:end
        Check("with the ROM out, reading gives the RAM", same);
        Check("KernalRom says so", !C64.KernalRom);

        // The KERNAL's interrupt goes on: it counts Ticks.
        uint ticks = C64.Ticks;
        for (int i = 0; i < 5; i++)
            C64.WaitFrame();
        Check("Ticks go on", C64.Ticks - ticks >= 4);
        long started = Environment.TickCount64;
        Fiber.Sleep(100);
        Check("a sleep ends", Environment.TickCount64 - started >= 100);

        // A typed line, through the KERNAL's screen editor.
        Console.Write("Type a word: ");
        string? line = Console.ReadLine();
        Check("a typed line arrives", line == "hello");

        // In and out again: the RAM keeps what it holds.
        C64.KernalRom = true;
        ram[0] = 42;
        C64.KernalRom = false;
        Check("in and out again, the RAM kept", ram[0] == 42 && ram[ram.Length - 1] == Pattern(ram.Length - 1));

        // The program ends with the ROM out: the end switches it back in.
        if (s_failures == 0)
            Console.WriteLine($"All {s_checks} checks passed.");
        return s_failures;
    }
}
