// Test: an arena, spans and NativeMemory, sized for a small machine. Arena.cs checks more
// and doesn't fit in a Commodore 64 (./build.sh --c64). The arena here is
// 1 KB, because the machine this is for has about 10 KB of heap once the
// program is loaded. It runs on every target.
// Exit code = number of failed checks.
// Build: ./build.sh SmallArena

using System;
using System.Runtime.InteropServices;

namespace SmallArenaSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void End(string section)
    {
        Console.Write(s_sectionFailures == 0 ? "PASS " : "FAIL ");
        Console.Write(section);
        Console.Write(" (");
        Console.Write(s_sectionChecks);
        Console.WriteLine(" checks)");
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
            Console.Write("  failed: ");
            Console.WriteLine(name);
        }
    }

    static int Sum(ReadOnlySpan<int> values)
    {
        int sum = 0;
        foreach (int value in values)
            sum += value;
        return sum;
    }

    // NativeMemory sits on the target's calloc and free. The blocks are small:
    // a C64 has about 10 KB of heap.
    static bool NativeAllocAndFree()
    {
        unsafe
        {
            int* items = (int*)NativeMemory.Alloc(50, sizeof(int));
            for (int i = 0; i < 50; i++)
                items[i] = i * 3;
            short* zeroed = (short*)NativeMemory.AllocZeroed(50, sizeof(short));
            bool ok = items != null && zeroed != null;
            for (int i = 0; i < 50; i++)
                ok &= items[i] == i * 3 && zeroed[i] == 0;
            NativeMemory.Free(zeroed);
            NativeMemory.Free(items);
            return ok;
        }
    }

    static bool NativeZeroesReusedMemory()
    {
        unsafe
        {
            byte* dirty = (byte*)NativeMemory.Alloc(300);
            for (int i = 0; i < 300; i++)
                dirty[i] = 0xAB;
            NativeMemory.Free(dirty);
            byte* clean = (byte*)NativeMemory.AllocZeroed(300);
            bool zero = clean != null;
            for (int i = 0; i < 300; i++)
                zero &= clean[i] == 0;
            NativeMemory.Free(clean);
            return zero;
        }
    }

    static bool NativeEmpty()
    {
        unsafe
        {
            void* empty = NativeMemory.Alloc(0);
            void* noElements = NativeMemory.AllocZeroed(0, 8);
            bool valid = empty != null && noElements != null;
            NativeMemory.Free(noElements);
            NativeMemory.Free(empty);
            NativeMemory.Free(null);
            return valid;
        }
    }

    // A block bigger than the arena's 1 KB, freed and allocated again: on a
    // C64 this only works if Free gives the memory back.
    static bool NativeFreeGivesBack()
    {
        unsafe
        {
            bool ok = true;
            for (int round = 0; round < 20; round++)
            {
                byte* block = (byte*)NativeMemory.Alloc(2048);
                ok &= block != null;
                block[0] = (byte)round;
                block[2047] = (byte)round;
                ok &= block[0] == round && block[2047] == round;
                NativeMemory.Free(block);
            }
            return ok;
        }
    }

    public static int Main()
    {
        Arena arena = Arena.Create(1024);
        Check("created, empty", arena.IsCreated && arena.Used == 0 && arena.BlockCount == 1);
        Span<int> numbers;
        unsafe { numbers = arena.AllocSpan<int>(10); } // SAFETY: used before the arena is reset.
        Check("AllocSpan is zeroed", numbers.Length == 10 && Sum(numbers) == 0);
        for (int i = 0; i < numbers.Length; i++)
            numbers[i] = i * i;
        Check("span of ints", numbers[9] == 81 && Sum(numbers) == 285 && Sum(numbers.Slice(2, 3)) == 29);
        nuint used = arena.Used;
        Check("Used grows", used >= 40);
        Arena.Mark mark = arena.Save();
        unsafe { arena.AllocSpan<long>(20); } // SAFETY: not used.
        Check("more is used", arena.Used > used);
        unsafe { arena.Restore(mark); } // SAFETY: nothing allocated after the mark is used again.
        Check("Restore gives it back", arena.Used == used && numbers[9] == 81);
        unsafe { arena.AllocSpan<byte>(3000); } // SAFETY: not used.
        Check("a big request gets a block of its own", arena.BlockCount == 2);
        unsafe { arena.Reset(); } // SAFETY: `numbers` isn't used after this.
        Check("Reset", arena.Used == 0);
        Span<byte> stack = stackalloc byte[16];
        stack.Fill(7);
        stack.Slice(4, 4).Clear();
        int stackSum = 0;
        foreach (byte value in stack)
            stackSum += value;
        Check("stackalloc, Fill, Clear", stackSum == 84);
        End("arena and spans");

        Check("NativeMemory.Alloc, AllocZeroed, Free", NativeAllocAndFree());
        Check("AllocZeroed zeroes a freed block's memory", NativeZeroesReusedMemory());
        Check("Alloc(0) is a valid pointer, Free(null) does nothing", NativeEmpty());
        Check("Free gives the memory back", NativeFreeGivesBack());
        End("NativeMemory");

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
        {
            Console.Write(s_failures);
            Console.WriteLine(" checks FAILED.");
        }
        return s_failures;
    }
}
