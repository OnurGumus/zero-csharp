// Example: C# on a Commodore 64. Text and colors on the 40x25 screen, a
// sprite that bounces around it, and raster bars in the border, until a key
// is pressed. Exit code = number of failed checks.
// Build: ./build.sh --c64 C64Demo    Run: ./run.sh --c64 --window C64Demo
//
// The objects, the interface, the list and the lambda are here to show that
// this is ordinary C#, compiled for an 8-bit processor with 64 KB of memory.
// lib/C64.cs has the machine: the screen, the video chip, the keyboard.
//
// At the end the program reads back what it put on the screen and in the
// video chip, and reports through Console, which is how ./test.sh --c64 checks it
// (it types a key for the program to stop at).

using System;

namespace C64DemoSample;

public interface IShape
{
    string Name { get; }
    int Area();
}

public class Rect : IShape
{
    public int W, H;
    public string Name => "Rect";
    public int Area() => W * H;
}

public class Circle : IShape
{
    public int Radius;
    public string Name => "Circle";
    public int Area() => 355 * Radius * Radius / 113;
}

public static class Program
{
    static int s_failures;
    static int s_checks;

    // The checks look at the screen, so their report waits until the screen
    // has been cleared: a failed check's name is kept, not printed.
    static string s_firstFailure;

    static void Check(string name, bool ok)
    {
        s_checks++;
        if (!ok)
        {
            s_failures++;
            s_firstFailure ??= name;
        }
    }

    // docs:sprite
    // A ball, 24 pixels wide and 21 high: three bytes a row.
    static ReadOnlySpan<byte> Ball => new byte[]
    {
        0x00, 0x7E, 0x00,  0x03, 0xFF, 0xC0,  0x07, 0xFF, 0xE0,
        0x1F, 0xFF, 0xF8,  0x1F, 0xFF, 0xF8,  0x3F, 0xFF, 0xFC,
        0x7F, 0xFF, 0xFE,  0x7F, 0xFF, 0xFE,  0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF,  0xFF, 0xFF, 0xFF,  0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF,  0x7F, 0xFF, 0xFE,  0x7F, 0xFF, 0xFE,
        0x3F, 0xFF, 0xFC,  0x1F, 0xFF, 0xF8,  0x1F, 0xFF, 0xF8,
        0x07, 0xFF, 0xE0,  0x03, 0xFF, 0xC0,  0x00, 0x7E, 0x00,
    };
    // docs:end

    static int Apply(Func<int, int> f, int x) => f(x);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int Opaque(int value) => value;

    // docs:text
    static void Draw()
    {
        C64.Clear(C64.Black);
        C64.Border(C64.Black);

        string title = "C# on the Commodore 64";
        for (int i = 0; i < title.Length; i++)
            C64.Print(9 + i, 2, title[i], C64.Red + i % 6);
        C64.Print(4, 4, "zero-mode .NET, no runtime, 6502", C64.LightGray);

        // Interface calls on objects from the heap.
        IShape[] shapes = { new Rect { W = 6, H = 7 }, new Circle { Radius = 10 } };
        for (int i = 0; i < shapes.Length; i++)
        {
            int column = C64.Print(4, 7 + i, shapes[i].Name, C64.Yellow);
            column = C64.Print(column, 7 + i, " area = ", C64.Yellow);
            C64.Print(column, 7 + i, shapes[i].Area());
        }

        // A generic list in an arena, filled with Fibonacci numbers.
        Arena arena = Arena.Create(1024);
        List<int> fib = List<int>.Create(arena, 16);
        int a = 0, b = 1;
        for (int i = 0; i < 16; i++)
        {
            fib.Add(a);
            int next = a + b;
            a = b;
            b = next;
        }
        int x = C64.Print(4, 11, "Fib: ", C64.LightGreen);
        for (int i = 0; i < fib.Count && x < 36; i++)
            x = C64.Print(x, 11, fib.Get(i)) + 1;

        // A lambda that captures a local.
        int offset = 1900;
        Func<int, int> year = n => n + offset;
        x = C64.Print(4, 13, "Lambda: 82 + 1900 = ", C64.LightBlue);
        C64.Print(x, 13, Apply(year, 82));

        x = C64.Print(4, 15, "32-bit int: 123456 * 789 = ", C64.LightRed);
        C64.Print(4, 16, 123456 * Opaque(789));

        C64.Print(4, 22, "Press a key to stop", C64.Gray);
    }
    // docs:end

    // docs:loop
    // One frame: color bars down the border, then move the ball while the
    // video chip is below the screen. Returns the ball's new position.
    static void Frame(int frame, ref int x, ref int y, ref int dx, ref int dy)
    {
        for (int line = 56; line < 248; line += 8)
        {
            C64.WaitLine(line);
            C64.Border(((line >> 3) + frame) & 15);
        }
        C64.WaitFrame();
        C64.Border(C64.Black);

        x += dx;
        y += dy;
        if (x <= 24 || x >= 320)
            dx = -dx;
        if (y <= 50 || y >= 229)
            dy = -dy;
        C64.MoveSprite(0, x, y);
    }
    // docs:end

    public static int Main()
    {
        Draw();
        C64.Sprite(0, 0, Ball, C64.White);

        int x = 100, y = 120, dx = 2, dy = 1, frames = 0;
        uint started = C64.Ticks;
        // The first second runs whatever is typed (a key pressed while the
        // program was loading waits in the keyboard's buffer).
        while (frames < 50 || C64.ReadKey() == 0)
            Frame(frames++, ref x, ref y, ref dx, ref dy);

        // What is on the screen and in the video chip now, read back.
        Check("title, first letter", C64.Peek(0x0400 + 2 * 40 + 9) == 'C');
        Check("title, a small letter", C64.Peek(0x0400 + 2 * 40 + 12) == 'o' - 'a' + 1);
        Check("its color", (C64.Peek(0xD800 + 2 * 40 + 9) & 15) == C64.Red);
        Check("number through an interface", C64.Peek(0x0400 + 7 * 40 + 16) == '4'
            && C64.Peek(0x0400 + 7 * 40 + 17) == '2');
        Check("32-bit product", C64.Peek(0x0400 + 16 * 40 + 4) == '9'
            && C64.Peek(0x0400 + 16 * 40 + 11) == '4');
        Check("printing off the screen does nothing", C64.Print(38, 24, "abc") == 38);

        Check("sprite 0 is shown", (C64.Peek(0xD015) & 1) == 1);
        Check("its shape was copied", C64.Peek(0x0340 + 1) == 0x7E && C64.Peek(0x0340 + 62) == 0);
        Check("it is where the last frame put it", C64.Peek(0xD001) == y
            && C64.Peek(0xD000) == (x & 255) && (C64.Peek(0xD010) & 1) == (x > 255 ? 1 : 0));
        Check("the border is black between frames", (C64.Peek(0xD020) & 15) == C64.Black);

        Check("frames were drawn", frames > 0);
        Check("the clock ran", C64.Ticks > started);
        Check("the joystick is at rest", C64.Joystick == 0);

        // Leave the machine as BASIC expects it.
        C64.HideSprite(0);
        C64.Border(C64.LightBlue);
        C64.Clear(C64.Blue);
        if (s_failures == 0)
            Console.WriteLine($"All {s_checks} checks passed.");
        else
            Console.WriteLine($"{s_failures} of {s_checks} checks FAILED, the first: {s_firstFailure}");
        return s_failures;
    }
}
