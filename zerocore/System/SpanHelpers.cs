// Adapted from dotnet/runtime (src/coreclr/nativeaot/Test.CoreLib), which the
// .NET Foundation licenses under the MIT license.

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    // Clearing, filling and copying blocks of memory: for the compiler (zeroing
    // a large stackalloc, copying a large struct) and for Span's Clear, Fill
    // and CopyTo. Blocks up to SmallBlock bytes are done here, where a call
    // would cost more than the work; bigger ones go to the C library's memset
    // and memmove, which every target has and which copy many bytes at a time.
    internal static unsafe class SpanHelpers
    {
        const nuint SmallBlock = 16;

        [DllImport("*"), SuppressGCTransition]
        static extern void* memmove(void* destination, void* source, nuint count);

        [DllImport("*"), SuppressGCTransition]
        static extern void* memset(void* destination, int value, nuint count);

        [Intrinsic]
        public static void ClearWithoutReferences(ref byte dest, nuint len)
        {
            Fill(ref dest, 0, len);
        }

        // The ranges may overlap.
        [Intrinsic]
        internal static void Memmove(ref byte dest, ref byte src, nuint len)
        {
            if (len > SmallBlock)
            {
                memmove(Unsafe.AsPointer(ref dest), Unsafe.AsPointer(ref src), len);
                return;
            }
            // The direction depends on which range starts first.
            if ((nuint)Unsafe.AsPointer(ref dest) - (nuint)Unsafe.AsPointer(ref src) >= len)
            {
                for (nuint i = 0; i < len; i++)
                    Unsafe.Add(ref dest, (nint)i) = Unsafe.Add(ref src, (nint)i);
            }
            else
            {
                for (nuint i = len; i > 0; i--)
                    Unsafe.Add(ref dest, (nint)(i - 1)) = Unsafe.Add(ref src, (nint)(i - 1));
            }
        }

        internal static void Fill(ref byte dest, byte value, nuint len)
        {
            if (len > SmallBlock)
            {
                memset(Unsafe.AsPointer(ref dest), value, len);
                return;
            }
            for (nuint i = 0; i < len; i++)
                Unsafe.Add(ref dest, (nint)i) = value;
        }

        // Span<T>.Fill: a T of one byte, or one whose bytes are all zero, is a
        // memset. Otherwise the first item is written, then copied onto the
        // rest in doubling blocks, so a big span takes a few memmove calls.
        internal static void Fill<T>(ref T dest, T value, nuint count)
        {
            nuint size = (nuint)Unsafe.SizeOf<T>();
            if (size == 1)
            {
                Fill(ref Unsafe.As<T, byte>(ref dest), Unsafe.As<T, byte>(ref value), count);
                return;
            }
            if (count * size <= SmallBlock * 4)
            {
                for (nuint i = 0; i < count; i++)
                    Unsafe.Add(ref dest, (nint)i) = value;
                return;
            }
            if (IsZero(ref Unsafe.As<T, byte>(ref value), size))
            {
                Fill(ref Unsafe.As<T, byte>(ref dest), 0, count * size);
                return;
            }
            dest = value;
            nuint done = 1;
            while (done < count)
            {
                nuint block = done <= count - done ? done : count - done;
                memmove(Unsafe.AsPointer(ref Unsafe.Add(ref dest, (nint)done)), Unsafe.AsPointer(ref dest), block * size);
                done += block;
            }
        }

        static bool IsZero(ref byte value, nuint size)
        {
            for (nuint i = 0; i < size; i++)
            {
                if (Unsafe.Add(ref value, (nint)i) != 0)
                    return false;
            }
            return true;
        }
    }
}
