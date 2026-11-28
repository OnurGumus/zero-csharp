// Guard pages for arenas: Arena.GuardPages. Part of lib/Arena.cs's Arena.
//
// A pointer into memory an arena has freed is the mistake arenas make
// possible, and nothing says so when it happens: the old data is often
// still there, so the program carries on with it. Arena.DebugChecks fills
// freed memory with 0xDD, which shows only if what is read looks wrong, and
// only until the memory is handed out again.
//
// With guard pages on, freed memory is taken away instead. Every block is
// whole pages of its own, straight from the system, and freeing gives the
// pages back while keeping their addresses taken: reading or writing them
// is a fault, and no later allocation lands there. So the first use of
// freed memory stops the program, whatever holds the stale address.
//
//   a block, as mapped:
//
//   | Block header | unused |          the block's bytes          |
//   ^ page boundary                                               ^ page boundary
//
//   after Save(), with the arena here:
//                                  v
//   | Block header | unused | kept |..|   no access, for good     |
//                                     ^ the block ends at the next page boundary
//
// Save() ends the current block where the arena stands (the pages after it,
// not yet used, go back at once), so everything allocated after the mark is
// in newer blocks, and Restore takes those away whole. Reset and Destroy
// take away every block.
//
// Arena.cs calls this file only through pointers the GuardPages setter
// fills in, so a program that never sets it links none of this.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

public partial struct Arena
{
    /// <summary>
    /// Debugging aid, off by default, for finding memory that is used after it
    /// was freed. Set it first thing in Main while testing. Arenas created from
    /// then on, Arena.Scratch's among them, get their blocks as whole pages
    /// from the system, and what Restore, Reset, Destroy or the end of a scope
    /// frees is taken away for good: the pages can't be read or written, and
    /// their addresses are never handed out again. Any use of freed memory
    /// then stops the program at that access, whatever held on to it: a
    /// pointer, a span, a collection, an object made in an object scope, a
    /// static field, or the handle of a destroyed arena. The message names the
    /// arena, if it has a name:
    /// <code>
    /// Arena "frame":
    /// Arena.GuardPages: memory was used after Arena.Reset freed it
    /// </code>
    /// Under a debugger, the program stops at the line that used it.
    /// - It finds only mistakes that run: test the paths you care about.
    /// - A FromBuffer arena is left as it is (its memory is yours), and so is
    ///   memory from ordinary <c>new</c> outside object scopes, which is never
    ///   freed.
    /// - It is slow and wasteful: system calls for every block and every
    ///   scope, and at least a page (4 KB, or 16 KB on Apple hardware) per
    ///   block. Every Save() ends the current block, so Capacity, BlockCount
    ///   and Dump() show different numbers than without it, and memory is
    ///   never at the address it had before.
    /// - It works where the system can protect pages and has a 64-bit address
    ///   space: Linux, macOS, Windows, Android and iOS. Elsewhere setting it
    ///   does nothing, and it reads back false.
    /// It goes well with DebugChecks, which reports a stale collection with a
    /// message of its own before the collection touches the freed memory.
    /// </summary>
    public static bool GuardPages
    {
        get
        {
            unsafe { return s_guardMap != null; }
        }
        set
        {
            // A constant on each target: on a 32-bit or smaller machine the
            // rest is never compiled in.
            if (sizeof(nuint) != 8)
                return;
            unsafe
            {
                // SAFETY: the pointers are to this file's functions; the
                // pages MapPages returns are this file's to use.
                if (!value)
                {
#if !ZERO_GUARD_PAGES   // built to have them on throughout (build.sh)
                    // Arenas made so far stay guarded; new ones aren't.
                    s_guardMap = null;
#endif
                    return;
                }
                if (s_guardMap != null)
                    return;
                if (s_guardRetire == null)
                {
                    SystemKind system = (SystemKind)ZeroSystem();
                    s_mapAnonymous = system == SystemKind.MacOS ? 0x1000 : 0x20;
                    // A machine that can't protect pages reports small ones
                    // (see lib/Fibers.cs), or has no mmap that works.
                    nuint page = (nuint)getpagesize();
                    if (page < 4096)
                        return;
                    byte* ranges = MapPages(page);
                    byte* previous = MapPages(page);
                    if (ranges == null || previous == null)
                        return;
                    s_guardPage = page;
                    s_ranges = (FreedRange*)ranges;
                    s_rangeRoom = (int)(page / (nuint)sizeof(FreedRange));
                    s_previousActions = previous;
                    ReportFaults(system);
                    s_guardRetire = &Retire;
                }
                s_guardMap = &MapPages;
            }
        }
    }

    [DllImport("libc")]
    static unsafe extern void* mmap(void* address, nuint length, int protection, int flags, int file, long offset);

    [DllImport("libc")]
    static unsafe extern int sigaction(int signal, void* action, void* previous);

    [DllImport("*")]
    static safe extern int ZeroSystem();

    // mmap's arguments, the same on Linux, macOS and Android except for
    // MAP_ANONYMOUS (s_mapAnonymous). Windows has no mmap: win/runtime.c's
    // takes Linux's values and calls VirtualAlloc and VirtualFree.
    const int ProtectNone = 0, ProtectReadWrite = 3;
    const int MapPrivate = 2, MapFixed = 0x10;
    static int s_mapAnonymous;

    // size bytes (a multiple of the page size) of new, zeroed pages, or null.
    static byte* MapPages(nuint size)
    {
        void* pages;
        // SAFETY: asks for new memory; nothing that exists is touched.
        unsafe { pages = mmap(null, size, ProtectReadWrite, MapPrivate | s_mapAnonymous, -1, 0); }
        return (nint)pages == -1 ? null : (byte*)pages;
    }

    // Takes the pages in [from, to) away for good. Mapping new pages without
    // access over them does both halves: the old pages' memory goes back to
    // the system, and their addresses stay taken, so nothing the program
    // maps later lands there.
    static unsafe void TakeAway(byte* from, byte* to)
    {
        void* pages;
        // SAFETY: the caller owns these pages (they came from MapPages) and
        // gives them up.
        unsafe { pages = mmap(from, (nuint)(to - from), ProtectNone, MapPrivate | MapFixed | s_mapAnonymous, -1, 0); }
        if ((nint)pages == -1)
            Environment.FailFast("Arena.GuardPages: the system wouldn't take freed pages away (on Linux, the limit on mappings, vm.max_map_count, may have been reached)");
    }

    // s_guardRetire: an arena frees [from, to), whole pages.
    static unsafe void Retire(State* state, byte* from, byte* to, int what)
    {
        unsafe
        {
            // SAFETY: Arena.cs passes a live state and pages MapPages gave
            // it. The name is read first: the state's own page may be what
            // is freed.
            Remember(from, to, state->Name, what);
            TakeAway(from, to);
        }
    }

    // ---- what was freed, for the message ---------------------------------

    // Freed pages, so a fault can be told from any other and say whose
    // memory it was. One entry for each run of pages next to each other
    // that the same arena freed the same way; the system hands out
    // neighbouring addresses, so a loop of scopes makes few entries.
    struct FreedRange
    {
        public byte* From;
        public byte* To;
        public nint Name;   // the arena's name string, or 0
        public int What;    // a Freed* constant
    }

    static FreedRange* s_ranges;
    static int s_rangeCount;
    static int s_rangeRoom;
    static int s_rangeLock;

    static unsafe void Remember(byte* from, byte* to, nint name, int what)
    {
        // Arenas belong to one thread each, but this list to all of them.
        while (Interlocked.CompareExchange(ref s_rangeLock, 1, 0) != 0)
        {
        }
        unsafe
        {
            // SAFETY: s_ranges holds s_rangeRoom entries, s_rangeCount of
            // them filled; only the lock's holder writes them.
            FreedRange* last = s_rangeCount > 0 ? s_ranges + (s_rangeCount - 1) : null;
            if (last != null && last->Name == name && last->What == what && last->From == to)
            {
                last->From = from;
            }
            else if (last != null && last->Name == name && last->What == what && last->To == from)
            {
                last->To = to;
            }
            else
            {
                if (s_rangeCount == s_rangeRoom)
                    GrowRanges();
                FreedRange* range = s_ranges + s_rangeCount;
                range->From = from;
                range->To = to;
                range->Name = name;
                range->What = what;
                s_rangeCount++;
            }
        }
        Volatile.Write(ref s_rangeLock, 0);
    }

    // Twice the room, in new pages. The old ones go the way of freed memory.
    static unsafe void GrowRanges()
    {
        nuint oldSize = GuardPageUp((nuint)s_rangeRoom * (nuint)sizeof(FreedRange));
        FreedRange* bigger = (FreedRange*)MapPages(oldSize * 2);
        if (bigger == null)
            Environment.FailFast("Arena.GuardPages: out of memory");
        unsafe
        {
            // SAFETY: both hold at least s_rangeCount entries.
            for (int i = 0; i < s_rangeCount; i++)
                bigger[i] = s_ranges[i];
            FreedRange* old = s_ranges;
            s_ranges = bigger;
            s_rangeRoom = (int)(oldSize * 2 / (nuint)sizeof(FreedRange));
            TakeAway((byte*)old, (byte*)old + oldSize);
        }
    }

    // ---- the fault --------------------------------------------------------

    // The system's sigaction for each of the two signals, as it was before
    // ReportFaults: 256 bytes each. Put back when a fault isn't about freed
    // arena memory, so the program ends the way it would have.
    static byte* s_previousActions;
    const int ActionRoom = 256;

    static int s_busSignal;      // SIGBUS's number here
    static int s_addressOffset;  // where siginfo_t keeps the address

    const int SegmentationFault = 11;

    // Has OnFault called for an access to memory without access rights.
    // struct sigaction and siginfo_t differ between systems; win/runtime.c's
    // sigaction takes Linux's.
    static unsafe void ReportFaults(SystemKind system)
    {
        byte* action = stackalloc byte[ActionRoom];
        unsafe
        {
            // SAFETY: action and s_previousActions hold more than any of
            // these systems' struct sigaction (152 bytes at most).
            delegate* unmanaged<int, byte*, void*, void> handler = &OnFault;
            new Span<byte>(action, ActionRoom).Clear();
            if (system == SystemKind.MacOS)
            {
                // { handler; uint mask; int flags; }, SA_SIGINFO = 0x40.
                // macOS reports a page without access rights as a bus error.
                *(void**)action = handler;
                *(int*)(action + 12) = 0x40;
                s_busSignal = 10;
                s_addressOffset = 24;
            }
            else if (system == SystemKind.Android)
            {
                // Bionic, 64-bit: { int flags; handler; ulong mask; restorer; }.
                *(int*)action = 4;
                *(void**)(action + 8) = handler;
                s_busSignal = 7;
                s_addressOffset = 16;
            }
            else
            {
                // glibc: { handler; mask (128 bytes); int flags; restorer; },
                // SA_SIGINFO = 4.
                *(void**)action = handler;
                *(int*)(action + 136) = 4;
                s_busSignal = 7;
                s_addressOffset = 16;
            }
            sigaction(SegmentationFault, action, s_previousActions);
            sigaction(s_busSignal, action, s_previousActions + ActionRoom);
        }
    }

    // The signal handler. Ends the program with a message if the address is
    // in pages an arena freed; otherwise steps aside, and the access happens
    // again with the handler that was there before.
    [UnmanagedCallersOnly]
    static unsafe void OnFault(int signal, byte* information, void* context)
    {
        unsafe
        {
            // SAFETY: the system passes its siginfo_t. The list is read
            // without the lock: the thread that faulted may hold it, and an
            // entry only ever grows.
            byte* address = *(byte**)(information + s_addressOffset);
            FreedRange* ranges = s_ranges;
            for (int i = s_rangeCount - 1; i >= 0; i--)
            {
                if (address >= ranges[i].From && address < ranges[i].To)
                    FreedMemoryUsed(ranges[i].Name, ranges[i].What);
            }
            sigaction(signal, s_previousActions + (signal == SegmentationFault ? 0 : ActionRoom), null);
        }
    }

    static void FreedMemoryUsed(nint name, int what)
    {
        string arena;
        // SAFETY: 0 or the address of the string given to Create; see NameOf.
        unsafe { arena = Unsafe.As<nint, string>(ref name); }
        if (arena != null)
        {
            Out.Write("Arena \"");
            Out.Write(arena);
            Out.Line("\":");
        }
        Environment.FailFast(what switch
        {
            FreedByRestore => "Arena.GuardPages: memory was used after Arena.Restore, or the end of a scope, freed it",
            FreedByReset => "Arena.GuardPages: memory was used after Arena.Reset freed it",
            FreedByDestroy => "Arena.GuardPages: memory was used after Arena.Destroy freed it",
            NeverHandedOut => "Arena.GuardPages: memory past the end of an arena's block was used",
            _ => "Arena.GuardPages: an arena was used after Arena.Destroy",
        });
    }
}
