namespace System.Runtime.InteropServices
{
    // Where .NET throws, this aborts with a message.
    public static unsafe class NativeMemory
    {
        public static void* Alloc(nuint byteCount) => Allocate(byteCount);

        public static void* Alloc(nuint elementCount, nuint elementSize) => Allocate(Size(elementCount, elementSize));

        public static void* AllocZeroed(nuint byteCount) => Allocate(byteCount);

        public static void* AllocZeroed(nuint elementCount, nuint elementSize) => Allocate(Size(elementCount, elementSize));

        public static void Free(void* ptr)
        {
            if (ptr != null)
                Platform.free(ptr);
        }

        private static nuint Size(nuint elementCount, nuint elementSize)
        {
            if (elementSize != 0 && elementCount > ~(nuint)0 / elementSize)
                Environment.FailFast("NativeMemory: the size overflows");
            return elementCount * elementSize;
        }

        private static void* Allocate(nuint byteCount)
        {
            // C allows no object larger than PTRDIFF_MAX; some systems (Rosetta)
            // crash on such a request instead of returning null.
            void* block = byteCount > (nuint)nint.MaxValue ? null : Platform.calloc(byteCount == 0 ? 1 : byteCount, 1);
            if (block == null)
                Environment.FailFast("NativeMemory: out of memory");
            return block;
        }
    }
}
