using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Buffers.Binary
{
    // Numbers in a byte span in a given byte order, as file formats and
    // network protocols store them, with .NET's names:
    //
    //   uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
    //   BinaryPrimitives.WriteInt16BigEndian(packet[2..], port);
    //
    // Read and Write end the program if the span is too short (.NET throws);
    // TryRead and TryWrite return false instead. The span needn't be aligned
    // (see Load and Store for the 32-bit targets that care).
    // Every target is little-endian (BitConverter.IsLittleEndian), so the
    // LittleEndian methods are a plain load or store and the BigEndian ones
    // add a byte swap.
    public static unsafe class BinaryPrimitives
    {
#if WASM
        private static int s_zero; // never written: always 0
#endif

        // The compiler turns these into the processor's byte-swap instruction
        // (bswap, rev). Not in the WASM build, which DOS shares: LLVM's bswap
        // is a 486 instruction, and DOS programs must run on a 386.
#if !WASM
        [Intrinsic]
#endif
        public static byte ReverseEndianness(byte value) => value;

#if !WASM
        [Intrinsic]
#endif
        public static sbyte ReverseEndianness(sbyte value) => value;

#if !WASM
        [Intrinsic]
#endif
        public static ushort ReverseEndianness(ushort value) => (ushort)((value >> 8) + (value << 8));

#if !WASM
        [Intrinsic]
#endif
        public static short ReverseEndianness(short value) => (short)ReverseEndianness((ushort)value);

#if !WASM
        [Intrinsic]
#endif
        public static uint ReverseEndianness(uint value)
        {
#if WASM
            // LLVM recognizes a byte swap written any usual way and emits
            // bswap. A shift count it can't see as a constant stops that: a
            // volatile read of a static field (one of a local is optimized
            // into a plain value before LLVM sees it).
            // Every shift needs it: from (value << 24) | (value >> 24) alone,
            // LLVM still makes a bswap of the outer bytes.
            int eight = 8 + Volatile.Read(ref s_zero);
            int twentyFour = 3 * eight;
            return (value << twentyFour) | ((value & 0xFF00u) << eight) | ((value >> eight) & 0xFF00u) | (value >> twentyFour);
#else
            return BitOperations.RotateRight(value & 0x00FF00FFu, 8) + BitOperations.RotateLeft(value & 0xFF00FF00u, 8);
#endif
        }

#if !WASM
        [Intrinsic]
#endif
        public static int ReverseEndianness(int value) => (int)ReverseEndianness((uint)value);

#if !WASM
        [Intrinsic]
#endif
        public static ulong ReverseEndianness(ulong value)
            => ((ulong)ReverseEndianness((uint)value) << 32) + ReverseEndianness((uint)(value >> 32));

#if !WASM
        [Intrinsic]
#endif
        public static long ReverseEndianness(long value) => (long)ReverseEndianness((ulong)value);

        /// <summary>Reverses the byte order of each item, in place.</summary>
        public static void ReverseEndianness(Span<ushort> values)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = ReverseEndianness(values[i]);
        }

        public static void ReverseEndianness(Span<uint> values)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = ReverseEndianness(values[i]);
        }

        public static void ReverseEndianness(Span<ulong> values)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = ReverseEndianness(values[i]);
        }

        public static short ReadInt16LittleEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(short))
                TooShort();
            short value = Load<short>(source);
            return value;
        }

        public static bool TryReadInt16LittleEndian(ReadOnlySpan<byte> source, out short value)
        {
            if (source.Length < sizeof(short))
            {
                value = default;
                return false;
            }
            value = Load<short>(source);
            return true;
        }

        public static void WriteInt16LittleEndian(Span<byte> destination, short value)
        {
            if (destination.Length < sizeof(short))
                TooShort();
            Store(destination, value);
        }

        public static bool TryWriteInt16LittleEndian(Span<byte> destination, short value)
        {
            if (destination.Length < sizeof(short))
                return false;
            Store(destination, value);
            return true;
        }

        public static short ReadInt16BigEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(short))
                TooShort();
            short value = Load<short>(source);
            return ReverseEndianness(value);
        }

        public static bool TryReadInt16BigEndian(ReadOnlySpan<byte> source, out short value)
        {
            if (source.Length < sizeof(short))
            {
                value = default;
                return false;
            }
            value = Load<short>(source);
            value = ReverseEndianness(value);
            return true;
        }

        public static void WriteInt16BigEndian(Span<byte> destination, short value)
        {
            if (destination.Length < sizeof(short))
                TooShort();
            Store(destination, ReverseEndianness(value));
        }

        public static bool TryWriteInt16BigEndian(Span<byte> destination, short value)
        {
            if (destination.Length < sizeof(short))
                return false;
            Store(destination, ReverseEndianness(value));
            return true;
        }

        public static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(ushort))
                TooShort();
            ushort value = Load<ushort>(source);
            return value;
        }

        public static bool TryReadUInt16LittleEndian(ReadOnlySpan<byte> source, out ushort value)
        {
            if (source.Length < sizeof(ushort))
            {
                value = default;
                return false;
            }
            value = Load<ushort>(source);
            return true;
        }

        public static void WriteUInt16LittleEndian(Span<byte> destination, ushort value)
        {
            if (destination.Length < sizeof(ushort))
                TooShort();
            Store(destination, value);
        }

        public static bool TryWriteUInt16LittleEndian(Span<byte> destination, ushort value)
        {
            if (destination.Length < sizeof(ushort))
                return false;
            Store(destination, value);
            return true;
        }

        public static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(ushort))
                TooShort();
            ushort value = Load<ushort>(source);
            return ReverseEndianness(value);
        }

        public static bool TryReadUInt16BigEndian(ReadOnlySpan<byte> source, out ushort value)
        {
            if (source.Length < sizeof(ushort))
            {
                value = default;
                return false;
            }
            value = Load<ushort>(source);
            value = ReverseEndianness(value);
            return true;
        }

        public static void WriteUInt16BigEndian(Span<byte> destination, ushort value)
        {
            if (destination.Length < sizeof(ushort))
                TooShort();
            Store(destination, ReverseEndianness(value));
        }

        public static bool TryWriteUInt16BigEndian(Span<byte> destination, ushort value)
        {
            if (destination.Length < sizeof(ushort))
                return false;
            Store(destination, ReverseEndianness(value));
            return true;
        }

        public static int ReadInt32LittleEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(int))
                TooShort();
            int value = Load<int>(source);
            return value;
        }

        public static bool TryReadInt32LittleEndian(ReadOnlySpan<byte> source, out int value)
        {
            if (source.Length < sizeof(int))
            {
                value = default;
                return false;
            }
            value = Load<int>(source);
            return true;
        }

        public static void WriteInt32LittleEndian(Span<byte> destination, int value)
        {
            if (destination.Length < sizeof(int))
                TooShort();
            Store(destination, value);
        }

        public static bool TryWriteInt32LittleEndian(Span<byte> destination, int value)
        {
            if (destination.Length < sizeof(int))
                return false;
            Store(destination, value);
            return true;
        }

        public static int ReadInt32BigEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(int))
                TooShort();
            int value = Load<int>(source);
            return ReverseEndianness(value);
        }

        public static bool TryReadInt32BigEndian(ReadOnlySpan<byte> source, out int value)
        {
            if (source.Length < sizeof(int))
            {
                value = default;
                return false;
            }
            value = Load<int>(source);
            value = ReverseEndianness(value);
            return true;
        }

        public static void WriteInt32BigEndian(Span<byte> destination, int value)
        {
            if (destination.Length < sizeof(int))
                TooShort();
            Store(destination, ReverseEndianness(value));
        }

        public static bool TryWriteInt32BigEndian(Span<byte> destination, int value)
        {
            if (destination.Length < sizeof(int))
                return false;
            Store(destination, ReverseEndianness(value));
            return true;
        }

        public static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(uint))
                TooShort();
            uint value = Load<uint>(source);
            return value;
        }

        public static bool TryReadUInt32LittleEndian(ReadOnlySpan<byte> source, out uint value)
        {
            if (source.Length < sizeof(uint))
            {
                value = default;
                return false;
            }
            value = Load<uint>(source);
            return true;
        }

        public static void WriteUInt32LittleEndian(Span<byte> destination, uint value)
        {
            if (destination.Length < sizeof(uint))
                TooShort();
            Store(destination, value);
        }

        public static bool TryWriteUInt32LittleEndian(Span<byte> destination, uint value)
        {
            if (destination.Length < sizeof(uint))
                return false;
            Store(destination, value);
            return true;
        }

        public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(uint))
                TooShort();
            uint value = Load<uint>(source);
            return ReverseEndianness(value);
        }

        public static bool TryReadUInt32BigEndian(ReadOnlySpan<byte> source, out uint value)
        {
            if (source.Length < sizeof(uint))
            {
                value = default;
                return false;
            }
            value = Load<uint>(source);
            value = ReverseEndianness(value);
            return true;
        }

        public static void WriteUInt32BigEndian(Span<byte> destination, uint value)
        {
            if (destination.Length < sizeof(uint))
                TooShort();
            Store(destination, ReverseEndianness(value));
        }

        public static bool TryWriteUInt32BigEndian(Span<byte> destination, uint value)
        {
            if (destination.Length < sizeof(uint))
                return false;
            Store(destination, ReverseEndianness(value));
            return true;
        }

        public static long ReadInt64LittleEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(long))
                TooShort();
            long value = Load<long>(source);
            return value;
        }

        public static bool TryReadInt64LittleEndian(ReadOnlySpan<byte> source, out long value)
        {
            if (source.Length < sizeof(long))
            {
                value = default;
                return false;
            }
            value = Load<long>(source);
            return true;
        }

        public static void WriteInt64LittleEndian(Span<byte> destination, long value)
        {
            if (destination.Length < sizeof(long))
                TooShort();
            Store(destination, value);
        }

        public static bool TryWriteInt64LittleEndian(Span<byte> destination, long value)
        {
            if (destination.Length < sizeof(long))
                return false;
            Store(destination, value);
            return true;
        }

        public static long ReadInt64BigEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(long))
                TooShort();
            long value = Load<long>(source);
            return ReverseEndianness(value);
        }

        public static bool TryReadInt64BigEndian(ReadOnlySpan<byte> source, out long value)
        {
            if (source.Length < sizeof(long))
            {
                value = default;
                return false;
            }
            value = Load<long>(source);
            value = ReverseEndianness(value);
            return true;
        }

        public static void WriteInt64BigEndian(Span<byte> destination, long value)
        {
            if (destination.Length < sizeof(long))
                TooShort();
            Store(destination, ReverseEndianness(value));
        }

        public static bool TryWriteInt64BigEndian(Span<byte> destination, long value)
        {
            if (destination.Length < sizeof(long))
                return false;
            Store(destination, ReverseEndianness(value));
            return true;
        }

        public static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(ulong))
                TooShort();
            ulong value = Load<ulong>(source);
            return value;
        }

        public static bool TryReadUInt64LittleEndian(ReadOnlySpan<byte> source, out ulong value)
        {
            if (source.Length < sizeof(ulong))
            {
                value = default;
                return false;
            }
            value = Load<ulong>(source);
            return true;
        }

        public static void WriteUInt64LittleEndian(Span<byte> destination, ulong value)
        {
            if (destination.Length < sizeof(ulong))
                TooShort();
            Store(destination, value);
        }

        public static bool TryWriteUInt64LittleEndian(Span<byte> destination, ulong value)
        {
            if (destination.Length < sizeof(ulong))
                return false;
            Store(destination, value);
            return true;
        }

        public static ulong ReadUInt64BigEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(ulong))
                TooShort();
            ulong value = Load<ulong>(source);
            return ReverseEndianness(value);
        }

        public static bool TryReadUInt64BigEndian(ReadOnlySpan<byte> source, out ulong value)
        {
            if (source.Length < sizeof(ulong))
            {
                value = default;
                return false;
            }
            value = Load<ulong>(source);
            value = ReverseEndianness(value);
            return true;
        }

        public static void WriteUInt64BigEndian(Span<byte> destination, ulong value)
        {
            if (destination.Length < sizeof(ulong))
                TooShort();
            Store(destination, ReverseEndianness(value));
        }

        public static bool TryWriteUInt64BigEndian(Span<byte> destination, ulong value)
        {
            if (destination.Length < sizeof(ulong))
                return false;
            Store(destination, ReverseEndianness(value));
            return true;
        }

        public static float ReadSingleLittleEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(float))
                TooShort();
            float value = Load<float>(source);
            return value;
        }

        public static bool TryReadSingleLittleEndian(ReadOnlySpan<byte> source, out float value)
        {
            if (source.Length < sizeof(float))
            {
                value = default;
                return false;
            }
            value = Load<float>(source);
            return true;
        }

        public static void WriteSingleLittleEndian(Span<byte> destination, float value)
        {
            if (destination.Length < sizeof(float))
                TooShort();
            Store(destination, value);
        }

        public static bool TryWriteSingleLittleEndian(Span<byte> destination, float value)
        {
            if (destination.Length < sizeof(float))
                return false;
            Store(destination, value);
            return true;
        }

        public static float ReadSingleBigEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(float))
                TooShort();
            float value = Load<float>(source);
            return BitConverter.Int32BitsToSingle(ReverseEndianness(BitConverter.SingleToInt32Bits(value)));
        }

        public static bool TryReadSingleBigEndian(ReadOnlySpan<byte> source, out float value)
        {
            if (source.Length < sizeof(float))
            {
                value = default;
                return false;
            }
            value = Load<float>(source);
            value = BitConverter.Int32BitsToSingle(ReverseEndianness(BitConverter.SingleToInt32Bits(value)));
            return true;
        }

        public static void WriteSingleBigEndian(Span<byte> destination, float value)
        {
            if (destination.Length < sizeof(float))
                TooShort();
            Store(destination, BitConverter.Int32BitsToSingle(ReverseEndianness(BitConverter.SingleToInt32Bits(value))));
        }

        public static bool TryWriteSingleBigEndian(Span<byte> destination, float value)
        {
            if (destination.Length < sizeof(float))
                return false;
            Store(destination, BitConverter.Int32BitsToSingle(ReverseEndianness(BitConverter.SingleToInt32Bits(value))));
            return true;
        }

        public static double ReadDoubleLittleEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(double))
                TooShort();
            double value = Load<double>(source);
            return value;
        }

        public static bool TryReadDoubleLittleEndian(ReadOnlySpan<byte> source, out double value)
        {
            if (source.Length < sizeof(double))
            {
                value = default;
                return false;
            }
            value = Load<double>(source);
            return true;
        }

        public static void WriteDoubleLittleEndian(Span<byte> destination, double value)
        {
            if (destination.Length < sizeof(double))
                TooShort();
            Store(destination, value);
        }

        public static bool TryWriteDoubleLittleEndian(Span<byte> destination, double value)
        {
            if (destination.Length < sizeof(double))
                return false;
            Store(destination, value);
            return true;
        }

        public static double ReadDoubleBigEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(double))
                TooShort();
            double value = Load<double>(source);
            return BitConverter.Int64BitsToDouble(ReverseEndianness(BitConverter.DoubleToInt64Bits(value)));
        }

        public static bool TryReadDoubleBigEndian(ReadOnlySpan<byte> source, out double value)
        {
            if (source.Length < sizeof(double))
            {
                value = default;
                return false;
            }
            value = Load<double>(source);
            value = BitConverter.Int64BitsToDouble(ReverseEndianness(BitConverter.DoubleToInt64Bits(value)));
            return true;
        }

        public static void WriteDoubleBigEndian(Span<byte> destination, double value)
        {
            if (destination.Length < sizeof(double))
                TooShort();
            Store(destination, BitConverter.Int64BitsToDouble(ReverseEndianness(BitConverter.DoubleToInt64Bits(value))));
        }

        public static bool TryWriteDoubleBigEndian(Span<byte> destination, double value)
        {
            if (destination.Length < sizeof(double))
                return false;
            Store(destination, BitConverter.Int64BitsToDouble(ReverseEndianness(BitConverter.DoubleToInt64Bits(value))));
            return true;
        }

        public static Half ReadHalfLittleEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(Half))
                TooShort();
            Half value = Load<Half>(source);
            return value;
        }

        public static bool TryReadHalfLittleEndian(ReadOnlySpan<byte> source, out Half value)
        {
            if (source.Length < sizeof(Half))
            {
                value = default;
                return false;
            }
            value = Load<Half>(source);
            return true;
        }

        public static void WriteHalfLittleEndian(Span<byte> destination, Half value)
        {
            if (destination.Length < sizeof(Half))
                TooShort();
            Store(destination, value);
        }

        public static bool TryWriteHalfLittleEndian(Span<byte> destination, Half value)
        {
            if (destination.Length < sizeof(Half))
                return false;
            Store(destination, value);
            return true;
        }

        public static Half ReadHalfBigEndian(ReadOnlySpan<byte> source)
        {
            if (source.Length < sizeof(Half))
                TooShort();
            Half value = Load<Half>(source);
            return BitConverter.Int16BitsToHalf(ReverseEndianness(BitConverter.HalfToInt16Bits(value)));
        }

        public static bool TryReadHalfBigEndian(ReadOnlySpan<byte> source, out Half value)
        {
            if (source.Length < sizeof(Half))
            {
                value = default;
                return false;
            }
            value = Load<Half>(source);
            value = BitConverter.Int16BitsToHalf(ReverseEndianness(BitConverter.HalfToInt16Bits(value)));
            return true;
        }

        public static void WriteHalfBigEndian(Span<byte> destination, Half value)
        {
            if (destination.Length < sizeof(Half))
                TooShort();
            Store(destination, BitConverter.Int16BitsToHalf(ReverseEndianness(BitConverter.HalfToInt16Bits(value))));
        }

        public static bool TryWriteHalfBigEndian(Span<byte> destination, Half value)
        {
            if (destination.Length < sizeof(Half))
                return false;
            Store(destination, BitConverter.Int16BitsToHalf(ReverseEndianness(BitConverter.HalfToInt16Bits(value))));
            return true;
        }

        // A value from or to the start of a span (its length already
        // checked), at any address. In the WASM build, which DOS, the C64
        // and the microcontrollers share, NativeAOT-LLVM gives
        // Unsafe.ReadUnaligned and WriteUnaligned the type's natural
        // alignment, so LLVM is free to use instructions that need it: on a
        // Cortex-M a ulong at an odd address became an LDRD/STRD, and a
        // float a VLDR/VSTR, which fault. Copying bytes tells LLVM the
        // alignment is 1; it still loads whole words where that's safe.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T Load<T>(ReadOnlySpan<byte> source) where T : unmanaged
        {
#if WASM
            T value;
            byte* to = (byte*)&value;
            for (int i = 0; i < sizeof(T); i++)
                to[i] = source[i];
            return value;
#else
            return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(source));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Store<T>(Span<byte> destination, T value) where T : unmanaged
        {
#if WASM
            byte* from = (byte*)&value;
            for (int i = 0; i < sizeof(T); i++)
                destination[i] = from[i];
#else
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
#endif
        }

        private static void TooShort() => Environment.FailFast("BinaryPrimitives: the span is too short for the value");
    }
}
