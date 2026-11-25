// A number to print in hex, in a $"..." hole of Console.WriteLine, Out,
// Str.Format or StrBuilder, without .NET's formatting:
//
//   Console.WriteLine(
//       $"PEEK({Hex.Of(address, 4)}) = {Hex.Of(C64.Peek(address), 2)}");
//
// prints "PEEK(D020) = 0E". The text is what {x:X4} gives, but {x:X4} reads its
// format only as the program runs, so it brings .NET's whole number
// formatting with it: about 12 KB on a Commodore 64. Hex.Of prints with a
// loop of its own: about 1.7 KB there. A negative number is printed as its two's
// complement at its type's width, as {x:X} does: Hex.Of(-1) is "FFFFFFFF".

using System;

public readonly struct Hex
{
    readonly ulong _value;
    readonly int _digits;
    readonly bool _lower;

    Hex(ulong value, int digits, bool lower)
    {
        _value = value;
        _digits = digits;
        _lower = lower;
    }

    /// <summary>The value in upper-case hex, with at least <paramref name="digits"/> digits.</summary>
    public static Hex Of(int value, int digits = 1) => new Hex((uint)value, digits, false);
    public static Hex Of(uint value, int digits = 1) => new Hex(value, digits, false);
    public static Hex Of(long value, int digits = 1) => new Hex((ulong)value, digits, false);
    public static Hex Of(ulong value, int digits = 1) => new Hex(value, digits, false);
    public static Hex Of(short value, int digits = 1) => new Hex((ushort)value, digits, false);
    public static Hex Of(ushort value, int digits = 1) => new Hex(value, digits, false);
    public static Hex Of(sbyte value, int digits = 1) => new Hex((byte)value, digits, false);
    public static Hex Of(byte value, int digits = 1) => new Hex(value, digits, false);

    /// <summary>The same in lower case ("ff"), as {x:x} gives.</summary>
    public Hex Lower => new Hex(_value, _digits, true);

    /// <summary>Writes the digits into <paramref name="dest"/> (64 chars are always enough) and returns how many.</summary>
    // Not inlined: Out's and StrBuilder's holes share it. It reads the
    // value's bytes, two digits from each, so a small machine shifts nothing
    // wider than a byte (every target stores the low byte first).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public int Format(Span<char> dest)
    {
        int minimum = _digits < dest.Length ? _digits : dest.Length;
        int n = 0;
        for (; minimum > 16; minimum--)
            dest[n++] = '0';
        char ten = _lower ? 'a' : 'A';
        ulong value = _value;
        bool started = false;
        for (int i = 15; i >= 0; i--)
        {
            int b;
            unsafe
            {
                // SAFETY: i >> 1 is 0-7, a byte of the local ulong.
                b = ((byte*)&value)[i >> 1];
            }
            int d = (i & 1) != 0 ? b >> 4 : b & 0xF;
            if (d != 0 || started || i < minimum || i == 0)
            {
                started = true;
                dest[n++] = (char)(d < 10 ? '0' + d : ten + d - 10);
            }
        }
        return n;
    }

    /// <summary>A new string (never freed); in a loop, use a $"..." hole.</summary>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[Fmt.MaxChars];
        return new System.Text.StringBuilder().Append(buffer.Slice(0, Format(buffer))).ToString();
    }
}
