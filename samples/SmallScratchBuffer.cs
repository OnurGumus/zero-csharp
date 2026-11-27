// Test: Arena.Scratch in a buffer of the program's, and
// Arena.ScratchOnStack (lib/Scratch.cs), sized for a small machine: the
// scope and a Scratch call nested in it use that memory, and afterwards
// Scratch uses the thread's own arena again. Fibers.cs
// tests fibers that wait inside such a call; SmallScratch tests the arenas
// Scratch makes itself. They don't all fit in a Commodore 64.
// Exit code = number of failed checks.
// Build: ./build.sh SmallScratchBuffer

using System;
using System.Runtime.CompilerServices;

namespace SmallScratchBufferSample;

public static class Program
{
    static int s_failures;

    static void Check(string name, bool ok)
    {
        Out.Write(ok ? "PASS " : "FAIL ");
        Out.Line(name);
        if (!ok)
            s_failures++;
    }

    // Where a span's memory is, as a number: enough to compare.
    static nint Address(Span<byte> span)
    {
        unsafe { return (nint)Unsafe.AsPointer(ref span[0]); } // SAFETY: the address is only compared.
    }

    static bool Inside(nint address, (nint Start, nint End) range)
        => address >= range.Start && address < range.End;

    // Sums 0..99 from the scope's memory, and adds where a nested call's
    // memory was, as the distance from the scope's.
    static int SumAndNested(scoped ScratchScope scratch, (nint Start, nint End) buffer)
    {
        Span<byte> bytes = scratch.AllocSpan<byte>(100);
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)i;
        int total = 0;
        foreach (byte b in bytes)
            total += b;
        nint nested = Arena.Scratch(static (scoped ScratchScope inner) => Address(inner.AllocSpan<byte>(50)));
        bool both = Inside(Address(bytes), buffer) && Inside(nested, buffer);
        return both ? total : -1;
    }

    public static int Main()
    {
        // 512 bytes: the arena keeps about 150 of them for itself on 64-bit
        // targets.
        byte[] buffer = new byte[512];
        int sum;
        nint start = Address(buffer);
        (nint, nint) range = (start, start + buffer.Length);
        // SAFETY: nothing but the call uses the buffer until it returns.
        unsafe { sum = Arena.Scratch(buffer, range, SumAndNested); }
        Check("the buffer's memory, nested calls included", sum == 99 * 100 / 2);

        nint after = Arena.Scratch(static (scoped ScratchScope scratch) => Address(scratch.AllocSpan<byte>(50)));
        Check("the thread's own arena afterwards", !Inside(after, range));

        // The same on ScratchOnStack's own stack frame: safe, nothing but
        // the scope sees the memory.
        int onStack = Arena.ScratchOnStack(400, static (scoped ScratchScope scratch) =>
        {
            Span<byte> bytes = scratch.AllocSpan<byte>(100);
            bytes.Fill(2);
            int total = Arena.Scratch(static (scoped ScratchScope inner) => inner.AllocSpan<byte>(50).Length);
            foreach (byte b in bytes)
                total += b;
            return total;
        });
        Check("on the stack, and a call nested in it", onStack == 250);

        return s_failures;
    }
}
