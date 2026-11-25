using System.Runtime;

namespace System
{
    // There is no garbage collector, but there is something to measure: every
    // `new`, boxing, closure, delegate and string concatenation allocates
    // memory that is never freed. GetTotalAllocatedBytes counts it, under the
    // name .NET uses, so a test can check that a loop allocates nothing:
    //
    //     long before = GC.GetTotalAllocatedBytes();
    //     ... the loop ...
    //     bool none = GC.GetTotalAllocatedBytes() == before;
    //
    // Arenas (lib/Arena.cs) get their blocks from malloc, not from here, so
    // they don't count.
    public static class GC
    {
        public static long GetTotalAllocatedBytes(bool precise = false)
            => System.Threading.Volatile.Read(ref RuntimeExports.s_allocatedBytes);

        // Nothing moves (there is no collector), so every array is pinned.
        public static T[] AllocateArray<T>(int length, bool pinned = false) => new T[length];

        public static T[] AllocateUninitializedArray<T>(int length, bool pinned = false) => new T[length];
    }
}
