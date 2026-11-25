// Buffered stdout writer for zero mode.
//
// bflat's zerolib has a Console that makes a syscall per character. This
// writer buffers output, encodes it as UTF-8, prints numbers, bools and spans,
// and hands each line to libc write() in one call. Console.WriteLine
// (lib/Console.cs) writes through it.
//
// Out.Line($"x = {x}, name = {name,-8}, ratio = {r:F2}") formats every hole
// straight into the buffer: no string is built. See Fmt (lib/Str.cs) for the
// format strings.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public static class Out
{
    [DllImport("libc")]
    static unsafe extern nint write(int fd, byte* buffer, nuint count);

    const int Capacity = 256;

    struct Buffer
    {
        public fixed byte Bytes[Capacity];
    }

    static Buffer s_buffer;
    static int s_length;

    public static void Write(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x80)
            {
                Put((byte)c);
            }
            else if (c < 0x800)
            {
                Put((byte)(0xC0 | (c >> 6)));
                Put((byte)(0x80 | (c & 0x3F)));
            }
            else if (c >= 0xD800 && c <= 0xDBFF && i + 1 < text.Length && text[i + 1] >= 0xDC00 && text[i + 1] <= 0xDFFF)
            {
                int codePoint = 0x10000 + ((c - 0xD800) << 10) + (text[++i] - 0xDC00);
                Put((byte)(0xF0 | (codePoint >> 18)));
                Put((byte)(0x80 | ((codePoint >> 12) & 0x3F)));
                Put((byte)(0x80 | ((codePoint >> 6) & 0x3F)));
                Put((byte)(0x80 | (codePoint & 0x3F)));
            }
            else
            {
                if (c >= 0xD800 && c <= 0xDFFF)
                    c = (char)0xFFFD; // unpaired surrogate
                Put((byte)(0xE0 | (c >> 12)));
                Put((byte)(0x80 | ((c >> 6) & 0x3F)));
                Put((byte)(0x80 | (c & 0x3F)));
            }
        }
    }

    public static void Write(string text) => Write(text.AsSpan());

    /// <summary>Writes bytes that are already UTF-8 (or any bytes), as they are.</summary>
    public static void WriteUtf8(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            Put(bytes[i]);
    }

    public static void Write(char value)
    {
        Span<char> one = stackalloc char[1];
        one[0] = value;
        Write(one);
    }

    public static void Write(long value)
    {
        Span<char> buffer = stackalloc char[Fmt.MaxChars];
        Write(buffer.Slice(0, Fmt.Int(value, buffer)));
    }

    /// <summary>
    /// As .NET's ToString(format): by default the shortest text that reads
    /// back as the same double ("0.1", "1E+20"); "F2" etc. as in .NET.
    /// </summary>
    public static void Write(double value, string format = null)
    {
        Span<char> buffer = stackalloc char[Fmt.MaxChars];
        int length = Fmt.Format(value, format, buffer);
        if (length >= 0)
            Write(buffer.Slice(0, length));
        else
            Write(value.ToString(format, null)); // longer than the buffer: a string, never freed
    }

    public static void Write(bool value) => Write(Fmt.Bool(value));

    /// <summary>Writes $"..." straight into the buffer.</summary>
    public static void Write(ref InterpolatedHandler handler)
    {
    }

    public static void Line()
    {
        Put((byte)'\n');
        Flush();
    }

    public static void Line(ReadOnlySpan<char> text)
    {
        Write(text);
        Line();
    }

    public static void Line(string text)
    {
        Write(text);
        Line();
    }

    /// <summary>Writes $"..." straight into the buffer, then a newline.</summary>
    public static void Line(ref InterpolatedHandler handler) => Line();

    public static void Line(string text, long value)
    {
        Write(text);
        Write(value);
        Line();
    }

    public static void Line(string text1, long value, string text2)
    {
        Write(text1);
        Write(value);
        Write(text2);
        Line();
    }

    public static void Line(string text1, long value1, string text2, long value2)
    {
        Write(text1);
        Write(value1);
        Write(text2);
        Write(value2);
        Line();
    }

    public static void Flush()
    {
        if (s_length == 0)
            return;

        unsafe
        {
            // SAFETY: s_length <= Capacity, so write() reads only initialized buffer bytes.
            fixed (byte* p = s_buffer.Bytes)
                write(1, p, (nuint)s_length);
        }
        s_length = 0;
    }

    static void Put(byte b)
    {
        if (s_length == Capacity)
            Flush();
        else if (s_length == 0)
            unsafe { Environment.s_beforeExit = &Flush; } // written at the program's end

        unsafe
        {
            // SAFETY: Flush() above guarantees s_length < Capacity.
            // (Upstream bflat 10.0.0-rc.1 miscompiles this store on x64; the fork
            // ships the .NET 10 GA JIT, which fixes it.)
            s_buffer.Bytes[s_length++] = b;
        }
    }

    static void Pad(int count)
    {
        for (; count > 0; count--)
            Put((byte)' ');
    }

    // alignment > 0 pads on the left (right-aligns), < 0 pads on the right.
    static void Aligned(ReadOnlySpan<char> text, int alignment)
    {
        int padding = (alignment < 0 ? -alignment : alignment) - text.Length;
        if (alignment > 0)
            Pad(padding);
        Write(text);
        if (alignment < 0)
            Pad(padding);
    }

    /// <summary>
    /// Lets Out.Write and Out.Line take $"...". Each part is written as the
    /// compiler visits it, so nothing is buffered beyond Out's own buffer.
    /// Supports {x,alignment} and {x:format}.
    /// </summary>
    [InterpolatedStringHandler]
    public ref struct InterpolatedHandler
    {
        public InterpolatedHandler(int literalLength, int formattedCount)
        {
        }

        public void AppendLiteral(string value) => Write(value);

        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string format = null) => Aligned(value, alignment);

        public void AppendFormatted(char value, int alignment = 0)
        {
            Span<char> one = stackalloc char[1];
            one[0] = value;
            Aligned(one, alignment);
        }

        // Integers without a format: the small path (see Fmt). Overloads for
        // each type and without optional parameters, as .NET's handler has
        // them, so the compiler prefers these to the generic ones below.
        public void AppendFormatted(sbyte value) => Whole(value, 0);
        public void AppendFormatted(sbyte value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(byte value) => Whole(value, 0);
        public void AppendFormatted(byte value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(short value) => Whole(value, 0);
        public void AppendFormatted(short value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(ushort value) => Whole(value, 0);
        public void AppendFormatted(ushort value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(int value) => Whole(value, 0);
        public void AppendFormatted(int value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(uint value) => Whole(value, 0);
        public void AppendFormatted(uint value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(long value) => Whole(value, 0);
        public void AppendFormatted(long value, int alignment) => Whole(value, alignment);
        public void AppendFormatted(ulong value) => Whole(value, 0);
        public void AppendFormatted(ulong value, int alignment) => Whole(value, alignment);

        // Hex.Of(x, 4): hex without .NET's formatting (see lib/Hex.cs).
        public void AppendFormatted(Hex value) => AppendFormatted(value, 0);

        public void AppendFormatted(Hex value, int alignment)
        {
            Span<char> buffer = stackalloc char[Fmt.MaxChars];
            Aligned(buffer.Slice(0, value.Format(buffer)), alignment);
        }

        // Any number with a format, {x:X4} or {x,8:F2}, and double, float,
        // Half and decimal with or without one: .NET's format strings.
        public void AppendFormatted<T>(T value) where T : ISpanFormattable => Formatted(value, 0, null);

        public void AppendFormatted<T>(T value, int alignment) where T : ISpanFormattable => Formatted(value, alignment, null);

        public void AppendFormatted<T>(T value, string format) where T : ISpanFormattable => Formatted(value, 0, format);

        public void AppendFormatted<T>(T value, int alignment, string format) where T : ISpanFormattable => Formatted(value, alignment, format);

        static void Formatted<T>(T value, int alignment, string format) where T : ISpanFormattable
        {
            Span<char> buffer = stackalloc char[Fmt.MaxChars];
            int length = Fmt.Format(value, format, buffer);
            if (length >= 0)
                Aligned(buffer.Slice(0, length), alignment);
            else
                Aligned(value.ToString(format, null), alignment); // longer than the buffer: a string, never freed
        }

        static void Whole(long value, int alignment)
        {
            Span<char> buffer = stackalloc char[Fmt.MaxChars];
            Aligned(buffer.Slice(0, Fmt.Int(value, buffer)), alignment);
        }

        static void Whole(ulong value, int alignment)
        {
            Span<char> buffer = stackalloc char[Fmt.MaxChars];
            Aligned(buffer.Slice(0, Fmt.UInt(value, buffer)), alignment);
        }

        public void AppendFormatted(bool value, int alignment = 0) => Aligned(Fmt.Bool(value), alignment);
    }
}
