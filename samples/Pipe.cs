// Example: standard input as bytes (lib/In.cs's Read). Reads a text line with
// ReadLine, then the rest of standard input as it is, and checks every byte.
// test.sh feeds it tests/input/Pipe.txt: the line "pipe test\r\n", every byte
// value from 0 to 255 twice over (so "\r", "\n", NUL and Ctrl-Z among them,
// and more than In's 256-byte buffer holds), then "end" without a newline.
// Exit code = number of failed checks.
// Build: ./build.sh Pipe

using System;

namespace PipeSample;

public static class Program
{
    static int s_failures;

    static int s_total, s_wrong = -1;
    static uint s_hash = 2166136261; // FNV-1a

    static void Take(byte b)
    {
        if (b != Expected(s_total) && s_wrong < 0)
            s_wrong = s_total;
        s_hash = (s_hash ^ b) * 16777619;
        s_total++;
    }

    static void Check(string name, bool ok)
    {
        if (!ok)
        {
            s_failures++;
            Console.WriteLine($"  FAIL: {name}");
        }
    }

    public static int Main()
    {
        Span<char> line = stackalloc char[32];
        int length = In.ReadLine(line);
        Check("the first line is text", length == 9);
        if (length >= 0 && length <= line.Length)
            Console.WriteLine(line.Slice(0, length));

        // The bytes after the line, read in small pieces: the first pieces come
        // from what ReadLine read ahead, the later ones straight from read().
        // docs:read
        Span<byte> chunk = stackalloc byte[7];
        int count;
        while ((count = In.Read(chunk)) > 0)
        {
            for (int i = 0; i < count; i++)
                Take(chunk[i]);
        }
        // docs:end

        Console.WriteLine($"{s_total} bytes, FNV-1a {s_hash:X8}");
        if (s_wrong >= 0)
            Console.WriteLine($"first wrong byte at {s_wrong}");
        Check("every byte arrived", s_total == 512 + 3);
        Check("every byte as it was sent", s_wrong < 0);
        Check("0 again after the end", In.Read(chunk) == 0);
        Check("0 for an empty buffer", In.Read(Span<byte>.Empty) == 0);
        Check("ReadLine at the end", In.ReadLine(line) == -1);

        Console.WriteLine(s_failures == 0 ? "PASS" : "FAIL");
        return s_failures;
    }

    static byte Expected(int index) =>
        index < 512 ? (byte)index : (byte)"end"[index - 512];
}
