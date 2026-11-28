// The Commodore 64: its 40x25 character screen and 16 colors, sprites, the
// video chip's raster line, the keyboard, a joystick and a clock. Only for
// programs built with ./build.sh --c64; the functions are in c64/runtime.c,
// and a program for another target that calls them won't link.
//
//   C64.Clear(C64.Black);
//   C64.Border(C64.Black);
//   C64.Print(4, 2, "Hello from C#", C64.Yellow);     // column, row
//   while (C64.ReadKey() == 0)
//   {
//       C64.WaitFrame();                                // 50 times a second
//       C64.Poke(0xD020, frame++ & 15);                 // any address at all
//   }
//
// C64.Reu reads and writes a RAM Expansion Unit, when there is one.
//
// C64.KernalRom = false switches the KERNAL ROM out, for the 8 KB of RAM
// under it (C64.RamUnderKernal).
//
// Out.Line and In.ReadLine work as everywhere, through the C64's own screen
// editor: text scrolls, and a typed line is handed over at RETURN. Print
// puts characters at a position without moving that cursor.
//
// Everything here is a few Peeks and Pokes, and each of those compiles to
// the processor's own load or store: C64.Poke(0xD020, 0) is one STA $D020.
// (The functions are in C, and the build inlines them.) Print loops in C,
// at about 170 cycles a character, of the 20,000 in a frame.

using System;
using System.Runtime.InteropServices;

public static class C64
{
    public const int Columns = 40, Rows = 25;

    // The 16 colors.
    public const int Black = 0, White = 1, Red = 2, Cyan = 3, Purple = 4, Green = 5, Blue = 6, Yellow = 7;
    public const int Orange = 8, Brown = 9, LightRed = 10, DarkGray = 11, Gray = 12, LightGreen = 13;
    public const int LightBlue = 14, LightGray = 15;

    // Joystick: what Joystick returns, added up.
    public const int Up = 1, Down = 2, Left = 4, Right = 8, Fire = 16;

    // Where things are in the C64's memory.
    const int Screen = 0x0400;          // a character code per cell, row after row
    const int ColorMemory = 0xD800;     // and each cell's color
    const int SpritePointers = 0x07F8;  // which 64-byte block each sprite shows
    const int SpriteShapes = 0x0340;    // blocks 13-15: the cassette buffer, free
    const int Video = 0xD000;           // the video chip's registers
    const int JoystickPort2 = 0xDC00;

    [DllImport("libc")] static safe extern int c64_peek(int address);
    [DllImport("libc")] static safe extern void c64_poke(int address, int value);
    [DllImport("libc")] static safe extern void c64_clear();
    [DllImport("libc")] static unsafe extern void c64_print(int cell, char* text, int count, int color);
    [DllImport("libc")] static unsafe extern void c64_copy(int address, byte* source, int count);
    [DllImport("libc")] static safe extern void c64_wait_line(int line);
    [DllImport("libc")] static safe extern int c64_key();
    [DllImport("libc")] static safe extern int c64_matrix(int columns);
    [DllImport("libc")] static safe extern uint c64_ticks();
    [DllImport("libc")] static safe extern void c64_keep_time();
    [DllImport("libc")] static safe extern void c64_kernal_rom(int on);

    // ---- memory ------------------------------------------------------------------

    /// <summary>The byte at a memory address (0-65535).</summary>
    public static int Peek(int address) => c64_peek(address);

    /// <summary>Writes a byte to a memory address. Nothing stops it writing into the program itself.</summary>
    public static void Poke(int address, int value) => c64_poke(address, value);

    // ---- the screen ----------------------------------------------------------------

    /// <summary>Empties the screen, sets its background color, and puts Out's cursor in the top left corner.</summary>
    public static void Clear(int background = Blue)
    {
        c64_poke(Video + 0x21, background);
        c64_clear();
    }

    /// <summary>Sets the color of the frame around the screen.</summary>
    public static void Border(int color) => c64_poke(Video + 0x20, color);

    /// <summary>Writes text at a column (0-39) and row (0-24), without moving Out's cursor. Returns the column after it. Text that reaches the end of a row goes on in the next.</summary>
    public static int Print(int column, int row, string text, int color = White)
    {
        if (text == null || !InScreen(column, row, text.Length))
            return column;
        unsafe
        {
            // SAFETY: the pointer and length are the string's own, and
            // InScreen checked that the cells are on the screen.
            fixed (char* p = text)
                c64_print(row * Columns + column, p, text.Length, color);
        }
        return column + text.Length;
    }

    /// <summary>Writes one character at a column and row.</summary>
    public static void Print(int column, int row, char character, int color = White)
    {
        if (!InScreen(column, row, 1))
            return;
        unsafe
        {
            // SAFETY: one character, a local's; InScreen checked the cell.
            c64_print(row * Columns + column, &character, 1, color);
        }
    }

    /// <summary>Writes a number at a column and row. Returns the column after it.</summary>
    public static int Print(int column, int row, int value, int color = White)
    {
        Span<char> digits = stackalloc char[11];
        int at = digits.Length;
        uint rest = value < 0 ? (uint)(-value) : (uint)value;
        do
        {
            digits[--at] = (char)('0' + rest % 10);
            rest /= 10;
        } while (rest != 0);
        if (value < 0)
            digits[--at] = '-';

        int count = digits.Length - at;
        if (!InScreen(column, row, count))
            return column;
        unsafe
        {
            // SAFETY: at..at+count is inside digits, and InScreen checked
            // that the cells are on the screen.
            fixed (char* p = digits)
                c64_print(row * Columns + column, p + at, count, color);
        }
        return column + count;
    }

    static bool InScreen(int column, int row, int count)
        => column >= 0 && column < Columns && row >= 0 && row < Rows
            && row * Columns + column + count <= Columns * Rows;

    // ---- sprites -------------------------------------------------------------------

    /// <summary>Gives sprite 0-7 a shape and a color and shows it. The shape is 63 bytes: 21 rows of 24 pixels, three bytes a row, the leftmost pixel in the highest bit. There is room for three shapes (slot 0-2); sprites can share one.</summary>
    public static void Sprite(int sprite, int slot, ReadOnlySpan<byte> shape, int color)
    {
        if ((uint)sprite > 7 || (uint)slot > 2 || shape.Length != 63)
            return;
        unsafe
        {
            // SAFETY: the pointer and length come from the span; the 63 bytes
            // land in the slot's 64-byte block.
            fixed (byte* p = shape)
                c64_copy(SpriteShapes + slot * 64, p, shape.Length);
        }
        c64_poke(SpritePointers + sprite, SpriteShapes / 64 + slot);
        c64_poke(Video + 0x27 + sprite, color);
        c64_poke(Video + 0x15, c64_peek(Video + 0x15) | (1 << sprite));
    }

    /// <summary>Moves a sprite. Its top left corner is at the screen's top left corner when x is 24 and y is 50; x goes up to 343, y to 249.</summary>
    public static void MoveSprite(int sprite, int x, int y)
    {
        if ((uint)sprite > 7)
            return;
        int bit = 1 << sprite;
        int high = c64_peek(Video + 0x10);      // each sprite's ninth x bit
        c64_poke(Video + 0x10, x > 255 ? high | bit : high & ~bit);
        c64_poke(Video + sprite * 2, x);
        c64_poke(Video + sprite * 2 + 1, y);
    }

    /// <summary>Hides a sprite.</summary>
    public static void HideSprite(int sprite)
    {
        if ((uint)sprite <= 7)
            c64_poke(Video + 0x15, c64_peek(Video + 0x15) & ~(1 << sprite));
    }

    // ---- timing --------------------------------------------------------------------

    /// <summary>Waits until the video chip starts drawing a line of the picture (0-255; the screen's 200 lines are 51-250). Changing a color then changes it from that line down: that is how raster bars are made.</summary>
    public static void WaitLine(int line) => c64_wait_line(line);

    /// <summary>Waits for the picture's lower border: once per frame, 50 times a second (60 on an American machine), and the moment to move things without flicker.</summary>
    public static void WaitFrame() => c64_wait_line(251);

    /// <summary>Sixtieths of a second since the machine was switched on.</summary>
    public static uint Ticks => c64_ticks();

    // ---- keyboard and joystick -------------------------------------------------------

    /// <summary>The next key pressed, as its character ('a', 'A', '1', ' ', '\n' for RETURN), or 0 if no key waits. Keys without an ASCII character come as the C64's own codes (cursor down is 17, F1 is 133).</summary>
    public static int ReadKey()
    {
        int key = c64_key();
        if (key >= 0x41 && key <= 0x5A)
            return key + 0x20;          // unshifted letters
        if (key >= 0xC1 && key <= 0xDA)
            return key - 0x80;          // shifted letters
        return key == 13 ? '\n' : key;
    }

    // The keyboard's 8x8 matrix, column after column: the character of each
    // position; '\0' for keys without one, '_' for the left arrow.
    const string KeyMatrix =
        "\0\n\0\0\0\0\0\0" + "3wa4zse\0" + "5rd6cftx" + "7yg8bhuv" +
        "9ij0mkon" + "+pl-.:@," + "\0*;\0\0=\0/" + "1_\02 \0q\0";

    /// <summary>Whether a key is held down right now: a letter (either case), a digit, ' ' or a sign on the keyboard. Unlike ReadKey this doesn't wait for a press, so a game can move something for as long as a key is held, and it sees several held together (move and fire). It reads the keyboard itself, so it works with the KERNAL's interrupt off. A joystick in port 2 held over looks like keys to it, as it does to the KERNAL; read the joystick on its own first.</summary>
    public static bool KeyHeld(char key)
    {
        if (key >= 'A' && key <= 'Z')
            key = (char)(key + 32);
        int position = key == '\0' ? -1 : KeyMatrix.IndexOf(key);
        if (position < 0)
            return false;
        return (c64_matrix(1 << (position >> 3)) & (1 << (position & 7))) != 0;
    }

    /// <summary>
    /// Whether the KERNAL's interrupt runs, 60 times a second. It scans the keyboard for
    /// ReadKey and In.ReadLine, counts Ticks and blinks the cursor, and takes about a tenth
    /// of the processor's time. A game that reads keys with KeyHeld can switch it off while
    /// it plays: ReadKey then gets nothing and Ticks stands still. The program's end
    /// switches it back on.
    /// </summary>
    /// <remarks>
    /// Clock, Stopwatch, Environment.TickCount64, Fiber.Sleep and the timers go on while it
    /// is off, a tenth of a second at a time, on CIA 2's time-of-day clock (switching it
    /// off the first time starts that clock at 0; CIA 2's timers are left alone).
    /// </remarks>
    public static bool KernalInterrupt
    {
        get => !s_kernalOff;
        set
        {
            if (!value)
                c64_keep_time();
            s_kernalOff = !value;
            c64_poke(0xDC0D, value ? 0x81 : 0x7F);     // the timer's interrupt, enabled or not
        }
    }

    static bool s_kernalOff;

    /// <summary>
    /// Whether the KERNAL ROM is at $E000-$FFFF, as when the program starts. Switched out
    /// (false), the RAM under it is there instead, about 8 KB more for the program:
    /// RamUnderKernal. Whatever calls the KERNAL goes on working, Console and In among
    /// them (each call switches the ROM in for as long as it takes), and so does its
    /// interrupt: the keyboard, Ticks, the cursor. The program's end switches it back in.
    /// </summary>
    public static bool KernalRom
    {
        get => !s_kernalRomOff;
        set
        {
            c64_kernal_rom(value ? 1 : 0);
            s_kernalRomOff = !value;
        }
    }

    static bool s_kernalRomOff;

    /// <summary>
    /// The RAM under the KERNAL ROM, $E000-$FF80: 8,065 bytes beyond the 50 KB a program
    /// has, for data. Read it with KernalRom false: with the ROM in, reading gives the
    /// ROM's bytes, though writing still reaches the RAM. (The KERNAL's jump table and
    /// vectors above it are the program's while the ROM is out.)
    /// </summary>
    public static Span<byte> RamUnderKernal
    {
        get
        {
            unsafe
            {
                // SAFETY: $E000-$FF80 is RAM on every C64, used by nothing else.
                return new Span<byte>((void*)0xE000, 0xFF81 - 0xE000);
            }
        }
    }

    /// <summary>The joystick in port 2: Up, Down, Left, Right and Fire, added up; 0 when it is at rest.</summary>
    public static int Joystick => ~c64_peek(JoystickPort2) & 31;

    // ---- the RAM Expansion Unit -------------------------------------------------------

    /// <summary>
    /// A RAM Expansion Unit: the 1700 (128 KB), 1764 (256 KB) or 1750 (512 KB), or the
    /// C64 Ultimate's (up to 16 MB). The processor can't address its memory; Read and
    /// Write copy between it and the C64's, by DMA, a byte a cycle (a KB in about a
    /// millisecond), so data far bigger than the C64 can be kept there and brought in
    /// a piece at a time. In VICE: <c>x64sc -reu -reusize 512</c>, and
    /// <c>-reuimage file</c> for memory that starts with a file's bytes.
    /// </summary>
    /// <example>
    /// <code>
    /// if (C64.Reu.Size == 0) { Console.WriteLine("This needs an REU."); return; }
    /// Span&lt;byte&gt; row = stackalloc byte[64];
    /// C64.Reu.Read(0x10000, row);       // 64 bytes from 64 KB into the REU
    /// C64.Reu.Write(0x10000, row);      // and back
    /// </code>
    /// </example>
    public static class Reu
    {
        [DllImport("libc")] static unsafe extern void c64_reu_copy(int toReu, int address, byte* c64, int count);
        [DllImport("libc")] static safe extern int c64_reu_size();

        /// <summary>
        /// The REU's size in bytes, 0 without one. Measured each time: it writes a byte at
        /// nine addresses and puts back what was there.
        /// </summary>
        public static int Size => c64_reu_size();

        /// <summary>Copies bytes from the REU at <paramref name="address"/> into <paramref name="into"/>. The address is 24 bits, as the REU's: past 16 MB, or past the end of a smaller REU, it wraps around to 0.</summary>
        public static void Read(int address, Span<byte> into)
        {
            unsafe
            {
                // SAFETY: the DMA writes into.Length bytes, all inside into.
                fixed (byte* p = into)
                    c64_reu_copy(0, address, p, into.Length);
            }
        }

        /// <summary>Copies <paramref name="bytes"/> into the REU at <paramref name="address"/> (24 bits, wrapping as Read's).</summary>
        public static void Write(int address, ReadOnlySpan<byte> bytes)
        {
            unsafe
            {
                // SAFETY: the DMA reads bytes.Length bytes, all inside bytes.
                fixed (byte* p = bytes)
                    c64_reu_copy(1, address, p, bytes.Length);
            }
        }
    }
}
