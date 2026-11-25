// Example: a small game for DOS, in the VGA's 320x200, 256-color mode (lib/Dos.cs),
// that also runs under UEFI with no operating system (lib/Uefi.cs).
// Steer the ship with the left and right arrow keys, or the mouse if a mouse
// driver is loaded, and stay clear of the falling rocks; Esc quits. The numbers
// at the top are the score and the best score, which is kept in VGA.DAT. A
// crash sounds on the PC speaker.
// Build: ./build.sh --dos Vga    Run: ./run.sh --dos --window Vga
//        ./build.sh --uefi Vga   Run: ./run.sh --uefi --window Vga
//
// "Vga test" plays 300 frames by itself and prints what it saw, so test.sh
// can check the graphics mode, the keyboard and the clock without anyone
// watching.
//
// The picture is drawn into a buffer and copied to the screen once per frame,
// right after WaitFrame, which keeps a half-drawn frame from showing.

using System;

// The same game runs under UEFI, where lib/Uefi.cs has the members lib/Dos.cs
// has. (bflat defines UEFI when it builds for UEFI.)
#if UEFI
using Machine = Uefi;
#else
using Machine = Dos;
#endif

namespace VgaSample;

public static class Program
{
    const int W = Machine.Width, H = Machine.Height;

    // Color numbers; SetPalette gives them their colors.
    const byte Black = 0, DimStar = 1, BrightStar = 2, Hull = 3, Cockpit = 4, Rock0 = 5, Red = 13, Green = 14, White = 15;

    const int ShipW = 17, ShipH = 10, ShipY = H - 16, ShipSpeed = 3;

    struct Star { public int X, Y, Speed; }
    struct Rock { public int X, Y, Size, Speed; public bool Live; }

    static readonly Star[] s_stars = new Star[70];
    static readonly Rock[] s_rocks = new Rock[14];
    static uint s_seed = 12345;
    static int s_shipX, s_frame, s_score, s_best, s_hits;
    static bool s_mouse;
    static int s_mouseX;

    const string BestFile = "VGA.DAT";

    // The best score, as four bytes, lowest first.
    static int LoadBest()
    {
        nint file = Machine.FileOpen(BestFile, Machine.ForReading);
        if (file == 0)
            return 0;
        Span<byte> bytes = stackalloc byte[4];
        int read = Machine.FileRead(file, bytes);
        Machine.FileClose(file);
        int value = bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24;
        return read == 4 && value > 0 ? value : 0;
    }

    static void SaveBest(int value)
    {
        nint file = Machine.FileOpen(BestFile, Machine.ForWriting);
        if (file == 0)
            return;
        Span<byte> bytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            bytes[i] = (byte)(value >> (i * 8));
        Machine.FileWrite(file, bytes);
        Machine.FileClose(file);
    }

    // A falling tone, about a second in all.
    static void CrashSound()
    {
        for (uint pitch = 400; pitch >= 80; pitch -= 40)
        {
            Machine.Sound(pitch);
            Machine.Sleep(60);
        }
        Machine.Sound(0);
        Machine.Sleep(600);
    }

    // Each digit is 3 pixels wide and 5 high: 15 bits, top row first.
    static readonly ushort[] s_digits = { 0x7B6F, 0x2492, 0x73E7, 0x73CF, 0x5BC9, 0x79CF, 0x79EF, 0x7249, 0x7BEF, 0x7BCF };

    static int Random(int below)
    {
        s_seed = s_seed * 1664525 + 1013904223;
        return (int)((s_seed >> 8) % (uint)below);
    }

    static void SetPalette()
    {
        // docs:palette
        Machine.SetColor(Black, 0, 0, 4);
        Machine.SetColor(DimStar, 20, 20, 28);
        Machine.SetColor(BrightStar, 50, 50, 63);
        Machine.SetColor(Hull, 10, 50, 60);
        Machine.SetColor(Cockpit, 63, 55, 10);
        // docs:end
        for (int i = 0; i < 8; i++)                     // rocks: eight browns, light to dark
            Machine.SetColor(Rock0 + i, 48 - i * 4, 36 - i * 3, 24 - i * 2);
        Machine.SetColor(Red, 63, 8, 8);
        Machine.SetColor(Green, 10, 60, 20);
        Machine.SetColor(White, 63, 63, 63);
    }

    static void Reset()
    {
        s_shipX = (W - ShipW) / 2;
        s_score = 0;
        s_frame = 0;
        for (int i = 0; i < s_rocks.Length; i++)
            s_rocks[i].Live = false;
    }

    static void FillRect(Span<byte> picture, int x, int y, int width, int height, byte color)
    {
        // docs:fill-rect
        int left = Math.Max(x, 0), right = Math.Min(x + width, W);
        int top = Math.Max(y, 0), bottom = Math.Min(y + height, H);
        if (left >= right)
            return;
        for (int row = top; row < bottom; row++)
            Machine.Fill(picture.Slice(row * W + left, right - left), color);
        // docs:end
    }

    // A rock: a square with its corners cut off, lit from the top.
    static void DrawRock(Span<byte> picture, in Rock rock)
    {
        int half = rock.Size / 2;
        for (int dy = -half; dy <= half; dy++)
        {
            int cut = Math.Max(0, Math.Abs(dy) - half / 2);
            byte color = (byte)(Rock0 + (dy + half) * 7 / rock.Size);
            FillRect(picture, rock.X - half + cut, rock.Y + dy, rock.Size - 2 * cut, 1, color);
        }
    }

    static void DrawShip(Span<byte> picture, byte hull)
    {
        for (int row = 0; row < ShipH; row++)           // a triangle, nose up
        {
            int halfWidth = row * (ShipW / 2) / (ShipH - 1);
            FillRect(picture, s_shipX + ShipW / 2 - halfWidth, ShipY + row, 2 * halfWidth + 1, 1, hull);
        }
        FillRect(picture, s_shipX + ShipW / 2 - 1, ShipY + 4, 3, 3, Cockpit);
    }

    // Draws a number, each digit 6 by 10 pixels, with its last digit ending at x.
    static void DrawNumber(Span<byte> picture, int value, int x, int y, byte color)
    {
        do
        {
            x -= 8;
            ushort shape = s_digits[value % 10];
            for (int bit = 0; bit < 15; bit++)
                if ((shape & (0x4000 >> bit)) != 0)
                    FillRect(picture, x + bit % 3 * 2, y + bit / 3 * 2, 2, 2, color);
            value /= 10;
        } while (value > 0);
    }

    // Moves everything by one frame. steer: -1 left, 1 right. Returns true if a rock hit the ship.
    static bool Step(int steer)
    {
        s_frame++;
        if (s_frame % 7 == 0)
            s_score++;
        s_shipX = Math.Clamp(s_shipX + steer * ShipSpeed, 0, W - ShipW);

        for (int i = 0; i < s_stars.Length; i++)
        {
            ref Star star = ref s_stars[i];
            star.Y += star.Speed;
            if (star.Y >= H)
            {
                star.Y = 0;
                star.X = Random(W);
            }
        }

        // A new rock every so often, and more often as the game goes on.
        int every = Math.Max(6, 24 - s_frame / 200);
        if (s_frame % every == 0)
        {
            for (int i = 0; i < s_rocks.Length; i++)
            {
                ref Rock rock = ref s_rocks[i];
                if (rock.Live)
                    continue;
                rock.Size = 9 + Random(14);
                rock.X = Random(W);
                rock.Y = -rock.Size;
                rock.Speed = 1 + Random(3);
                rock.Live = true;
                break;
            }
        }

        bool hit = false;
        for (int i = 0; i < s_rocks.Length; i++)
        {
            ref Rock rock = ref s_rocks[i];
            if (!rock.Live)
                continue;
            rock.Y += rock.Speed;
            int half = rock.Size / 2;
            if (rock.Y - half >= H)
                rock.Live = false;
            else if (rock.Y + half >= ShipY + 2 && rock.Y - half < ShipY + ShipH
                     && rock.X + half >= s_shipX + 3 && rock.X - half < s_shipX + ShipW - 3)
                hit = true;
        }
        return hit;
    }

    static void Draw(Span<byte> picture, bool hit)
    {
        Machine.Fill(picture, Black);
        for (int i = 0; i < s_stars.Length; i++)
            picture[s_stars[i].Y * W + s_stars[i].X] = s_stars[i].Speed > 1 ? BrightStar : DimStar;
        for (int i = 0; i < s_rocks.Length; i++)
            if (s_rocks[i].Live)
                DrawRock(picture, in s_rocks[i]);
        DrawShip(picture, hit ? Red : Hull);
        DrawNumber(picture, s_score, 60, 4, hit ? Red : Green);
        DrawNumber(picture, s_best, W - 6, 4, White);
    }

    // The game moves 70 steps a second by the clock. A machine that draws 70
    // frames a second takes one step per frame; a slower one takes several
    // between frames, so the game is as fast there, only less smooth.
    static void Play(Span<byte> picture)
    {
        Reset();
        uint start = Machine.Ticks;
        int steps = 0;
        while (true)
        {
            // docs:input
            // Esc is taken from the key presses, not from what is held down
            // right now: pressed and released during the pause after a crash,
            // it would otherwise go unnoticed.
            Machine.PollKeys();
            for (int key = Machine.ReadKey(); key != 0; key = Machine.ReadKey())
                if (key >> 8 == Machine.Esc)
                    return;
            int steer = (Machine.KeyDown(Machine.Right) ? 1 : 0)
                        - (Machine.KeyDown(Machine.Left) ? 1 : 0);
            // docs:end

            // The mouse takes over while it moves: the ship goes where it points.
            if (s_mouse)
            {
                Machine.Mouse(out int mouseX, out _);
                if (mouseX != s_mouseX && steer == 0)
                    s_shipX = Math.Clamp(mouseX - ShipW / 2, 0, W - ShipW);
                s_mouseX = mouseX;
            }

            // At least one step, and at most eight if the machine fell far behind.
            int due = (int)((Machine.Ticks - start) * 7 / 100);
            bool farBehind = due > steps + 8;
            if (farBehind)
                due = steps + 8;
            bool hit = false;
            do
            {
                hit = Step(steer);
                steps++;
            } while (!hit && steps < due);
            if (farBehind || steps > due)
            {
                start = Machine.Ticks;      // far behind, or ahead of the clock: count from here
                steps = 0;
            }

            if (hit && s_score > s_best)
                s_best = s_score;
            // docs:frame
            Draw(picture, hit);
            Machine.WaitFrame();
            Machine.Show(picture);
            // docs:end

            if (hit)
            {
                CrashSound();
                Reset();
                start = Machine.Ticks;
                steps = 0;
            }
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

    // Plays 300 frames with fixed steering and reports, for test.sh.
    static int SelfTest(Span<byte> picture)
    {
        Reset();
        for (int frame = 0; frame < 300; frame++)
        {
            int steer = frame < 60 ? -1 : frame < 200 ? 1 : 0;
            bool hit = Step(steer);
            if (hit)
                s_hits++;
            Draw(picture, hit);
        }

        // The screen has to show the picture: write it, then read it back.
        Machine.WaitFrame();
        Machine.Show(picture);
        Span<byte> screen = Machine.Screen;
        int different = 0;
        uint checksum = 2166136261;
        for (int i = 0; i < picture.Length; i++)
        {
            if (screen[i] != picture[i])
                different++;
            checksum = (checksum ^ picture[i]) * 16777619;
        }

        // 20 frames at 70 a second are 286 ms; allow for a coarse clock.
        uint start = Machine.Ticks;
        for (int i = 0; i < 20; i++)
            Machine.WaitFrame();
        uint frames = Machine.Ticks - start;
        start = Machine.Ticks;
        Machine.Sleep(200);
        uint slept = Machine.Ticks - start;
        int keys = Machine.PollKeys();

        // The speaker: bits 0 and 1 of port 0x61 connect it; nobody is listening here.
        Machine.Sound(440);
        bool speakerOn = (Machine.In(0x61) & 3) == 3;
        Machine.Sound(0);
        bool speakerOff = (Machine.In(0x61) & 3) == 0;
#if UEFI
        // An arm64 machine has no PC speaker to check, so the check passes there.
        if (!Machine.HasSpeaker)
            speakerOn = speakerOff = true;
#endif

        // A file: write it, add to it, read it back, delete it.
        const string name = "VGATEST.TMP";
        Span<byte> data = stackalloc byte[1000];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 7);
        bool files = true;
        nint file = Machine.FileOpen(name, Machine.ForWriting);
        files &= file != 0 && Machine.FileWrite(file, data) == 1000;
        Machine.FileClose(file);
        file = Machine.FileOpen(name, Machine.ForAppending);
        files &= file != 0 && Machine.FileWrite(file, data.Slice(0, 24)) == 24 && Machine.FileSize(file) == 1024;
        Machine.FileClose(file);
        file = Machine.FileOpen(name, Machine.ForReading);
        Span<byte> back = stackalloc byte[1100];
        int read = file != 0 ? Machine.FileRead(file, back) : -1;
        files &= read == 1024 && Same(back.Slice(0, 1000), data) && back[1023] == data[23];
        files &= Machine.FileSeek(file, -24, Machine.FromEnd) == 1000 && Machine.FileRead(file, back.Slice(0, 8)) == 8 && back[7] == data[7];
        files &= Machine.FileRead(file, back) == 16 && Machine.FileRead(file, back) == 0;
        Machine.FileClose(file);
        files &= Machine.FileDelete(name) && Machine.FileOpen(name, Machine.ForReading) == 0 && !Machine.FileDelete(name);

        // The mouse: there may or may not be a driver; asking must be safe either way.
        int buttons = Machine.MouseStart();
        if (buttons > 0)
            Machine.Mouse(out _, out _);

        Machine.Text();
        Console.WriteLine($"frames played: {s_frame}");
        Console.WriteLine($"score: {s_score}");
        Console.WriteLine($"frames with a rock on the ship: {s_hits}");
        Console.WriteLine($"picture checksum: {checksum}");
        Console.WriteLine($"pixels the screen shows differently: {different}");
        Console.WriteLine(frames >= 150 && frames <= 600 ? "20 frames took about 286 ms: yes" : "20 frames took about 286 ms: NO");
        Console.WriteLine(slept >= 100 && slept <= 500 ? "Sleep(200) took about 200 ms: yes" : "Sleep(200) took about 200 ms: NO");
        Console.WriteLine($"key presses waiting: {keys}");
        Console.WriteLine(speakerOn && speakerOff ? "speaker switches on and off: yes" : "speaker switches on and off: NO");
        Console.WriteLine(files ? "file written, appended, read back, deleted: yes" : "file written, appended, read back, deleted: NO");
        Console.WriteLine(buttons >= 0 && buttons <= 3 ? "mouse driver asked: yes" : "mouse driver asked: NO");
        bool ok = different == 0 && frames >= 150 && frames <= 600 && slept >= 100 && slept <= 500
                  && speakerOn && speakerOff && files;
        return ok ? 0 : 1;
    }

    public static int Main(string[] args)
    {
        for (int i = 0; i < s_stars.Length; i++)
        {
            s_stars[i].X = Random(W);
            s_stars[i].Y = Random(H);
            s_stars[i].Speed = 1 + Random(2);
        }
        // docs:setup
        Span<byte> picture = new byte[W * H];

        Machine.Graphics();
        SetPalette();
        // docs:end
        if (args.Length > 0 && args[0] == "test")
            return SelfTest(picture);

        s_best = LoadBest();
        int bestBefore = s_best;
        s_mouse = Machine.MouseStart() > 0;
        if (s_mouse)
            Machine.Mouse(out s_mouseX, out _);

        // docs:back-to-text
        Play(picture);
        Machine.Sound(0);
        Machine.Text();
        if (s_best > bestBefore)
            SaveBest(s_best);
        Console.WriteLine($"Best score: {s_best}");
        // docs:end
        return 0;
    }
}
