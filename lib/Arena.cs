// Growable arena (bump) allocator for zero mode.
// Compiled under the C# 15 updated memory safety rules (see build.sh).
//
// An arena hands out memory by moving a pointer forward and frees everything
// at once. Create one per lifetime: one for the whole run, one per request or
// frame, one for scratch work.
//
//   Arena arena = Arena.Create();
//   Point* p = arena.Alloc<Point>();
//   ...
//   unsafe { arena.Reset(); }      // everything allocated from it is gone
//
// - Arena is a handle (one pointer), so copies refer to the same arena, like
//   passing Arena* in C. A default(Arena) is "not created": every allocation
//   from it returns null.
// - Normal blocks grow in a Fibonacci sequence: 128, 128, 256, 384, 640, ...
// - An allocation that doesn't fit the next normal block gets a dedicated
//   block of its own and leaves that sequence alone.
// - Every allocation picks its alignment (any power of two, default 16).
// - Allocations fail by returning null; arena state is untouched on failure.
//   An arena created with stopOnOutOfMemory: true ends the program instead
//   when memory runs out, so nothing has to check for that.
// - Create(maxCapacity: n) caps the arena at n bytes of blocks: past that,
//   allocations fail as if memory had run out, even where malloc never
//   says no (Linux and macOS promise memory they don't have). For input you
//   don't control, and for giving each part of a small machine a budget.
// - FromBuffer() makes an arena in memory you provide (a static array,
//   stackalloc, a region of a microcontroller's RAM) and never calls malloc.
//   It can't grow: when the buffer is full, allocations fail.
// - Save() and Restore() free only what was allocated in between, for
//   temporary work in an arena that also holds longer-lived data. Don't let
//   an older collection grow in between (see Restore). OpenScope() pairs the
//   two with a using block, so they can't get out of order.
// - Reset() keeps the capacity by merging all blocks into one, so a
//   steady-state workload (one scratch arena per token, per frame, ...)
//   stops calling malloc after the first round.
// - AllocateObjectsHere() sends every ordinary allocation (new, closures,
//   strings, boxing) to the arena until the scope closes, so code that would
//   leak frees its objects with the arena. See it for the rules.
//   OpenObjectScope() also frees them when the scope closes, so objects
//   live exactly as long as a using block.
// - Not thread-safe: use one arena per thread.
// - Arena.DebugChecks = true (set it first thing in Main) makes arenas created
//   afterwards fill freed memory with 0xDD bytes, and makes List, Dict and
//   StrBuilder stop the program when used after their memory was freed. See
//   DebugChecks for what that does and doesn't catch.
// - For finding out what an arena does: give it a name in Create (failure
//   messages then say which arena), print its blocks with Dump(), and read
//   PeakUsed after a run to choose the first block's size.
//
// Allocating is safe: a pointer is harmless until it is dereferenced, and
// dereferencing needs an unsafe context anyway. Restore(), Reset() and
// Destroy() are the dangerous operations, so they are marked unsafe: the
// caller promises that nothing still points at the memory they free.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// F#'s arena { } builder (fsharp/control/Arena.fs) uses OpenedObjects.
[assembly: InternalsVisibleTo("FSharp.Control")]

public partial struct Arena
{
    public const nuint DefaultAlign = 16;

    // The core library's nuint has no MaxValue.
    static nuint MaxSize => ~(nuint)0;

    // 32 bytes, so block data starts 16-byte aligned behind malloc's pointer.
    struct Block
    {
        public Block* Next;
        public byte* Start;
        public byte* End;
        public nuint Size;
    }

    struct State
    {
        public Block* First;
        public Block* Current;
        public byte* Ptr;
        public nuint InitialSize;
        public nuint LastNormal;   // Fibonacci state for normal blocks
        public nuint PrevNormal;
        public int BlockCount;
        public uint Generation;    // bumped by Reset, to catch marks from before it
        public bool Poison;        // DebugChecks was on when the arena was created
        public bool StopOnOutOfMemory;
        public bool InBuffer;      // from FromBuffer: the caller owns the memory
        public nuint Used;         // bytes handed out, padding excluded
        public nuint Capacity;     // sum of all block sizes
        public nuint PeakUsed;     // the highest Used and Capacity so far;
        public nuint PeakCapacity; // Reset and Restore don't lower them
        public nuint MaxCapacity;  // 0: no limit
        public nint Name;          // address of the name string, or 0
        // Set once an object scope has sent objects here: runs before memory
        // is freed (a mark: Restore; null: everything, for Reset or Destroy).
        // A pointer, so programs without object scopes don't link the checks.
        public delegate*<State*, Mark*, bool, void> BeforeFree;
    }

    /// <summary>A point in an arena's history, from Save(). Restore() frees everything allocated after it.</summary>
    public struct Mark
    {
        internal void* Owner;
        internal void* Block;
        internal byte* Ptr;
        internal nuint Used;
        internal nuint LastNormal;
        internal nuint PrevNormal;
        internal uint Generation;
    }

    /// <summary>
    /// A Save() that restores itself: from OpenScope(), for a using block.
    /// Leaving the block, by any path, frees what was allocated inside it.
    /// </summary>
    public ref struct Scope : System.IDisposable
    {
        internal Arena Arena;
        internal Mark Mark;

        public void Dispose()
        {
            // SAFETY: OpenScope's caller promised that nothing allocated inside
            // the scope is used after it.
            unsafe { Arena.Restore(Mark); }
        }
    }

    State* _state;

    /// <summary>
    /// Debugging aid, off by default. While on:
    /// - Arenas created from then on fill memory with 0xDD bytes when it is freed
    ///   (Restore, Reset, Destroy) and when a block is first obtained. A stale
    ///   pointer then reads 0xDDDDDDDD instead of plausible old data, and so does
    ///   memory allocated with zero: false and never written.
    /// - List, Dict and StrBuilder check on every use that their buffer is still
    ///   allocated, all of it, and end the program with a message if it isn't.
    ///   That catches a collection used after its arena was reset, and one that
    ///   grew after a Save() (into a new buffer or in place) and was used after
    ///   the Restore().
    /// - For an arena an object scope has used, Reset, Restore and Destroy
    ///   first check that no static field points into what they free.
    /// It can't catch everything: once freed memory has been handed out again,
    /// a stale pointer into it looks valid. And it costs time: a fill on every
    /// free, a check on every collection operation.
    /// </summary>
    public static bool DebugChecks;

    const byte PoisonByte = 0xDD;

    // What a pointer read from poisoned memory looks like.
    static nuint PoisonPointer => sizeof(nuint) == 8 ? unchecked((nuint)0xDDDDDDDDDDDDDDDD) : 0xDDDDDDDD;

    [DllImport("libc")]
    static safe extern void* malloc(nuint size);

    [DllImport("libc")]
    static unsafe extern void free(void* ptr);

    [DllImport("libc")]
    static unsafe extern void* memset(void* destination, int value, nuint count);

    /// <summary>
    /// Returns a new arena with one block, or a not-created arena if malloc fails.
    /// <paramref name="name"/> is for Dump() and failure messages; the arena keeps
    /// the reference, so pass a string that outlives it, such as a literal.
    /// With <paramref name="stopOnOutOfMemory"/>, running out of memory ends the
    /// program with a message, here and in every later allocation, instead of
    /// returning null (and instead of List.Add and the like returning false).
    /// Use it wherever the program couldn't carry on anyway. Requests that are
    /// wrong in themselves (a bad alignment, a negative count) still return null.
    /// A nonzero <paramref name="maxCapacity"/> caps Capacity, the bytes of all
    /// blocks together (not counting a 32-byte header per block): an allocation
    /// that would need more fails as if memory had run out. Blocks shrink to
    /// fit under the cap, so it fails only when the request itself doesn't fit
    /// in what is left. A first block bigger than the cap is cut down to it.
    /// </summary>
    public static Arena Create(nuint blockSize = 4096, string name = null, bool stopOnOutOfMemory = false,
        nuint maxCapacity = 0)
    {
        State* state = (State*)malloc((nuint)sizeof(State));
        if (state == null)
        {
            if (stopOnOutOfMemory)
                Environment.FailFast("Arena.Create: out of memory");
            return default;
        }

        unsafe
        {
            // SAFETY: state holds sizeof(State) bytes.
            memset(state, 0, (nuint)sizeof(State));
            state->InitialSize = blockSize == 0 ? 4096 : blockSize;
            if (maxCapacity != 0 && state->InitialSize > maxCapacity)
                state->InitialSize = maxCapacity;
            state->MaxCapacity = maxCapacity;
            state->LastNormal = state->InitialSize;
            state->Poison = DebugChecks;
            state->Name = Unsafe.As<string, nint>(ref name);
            state->StopOnOutOfMemory = stopOnOutOfMemory;

            if (AddBlock(state, state->InitialSize) == null)
            {
                OutOfMemory(state);
                free(state);
                return default;
            }
        }
        return new Arena { _state = state };
    }

    /// <summary>
    /// Returns an arena that lives in <paramref name="size"/> bytes at
    /// <paramref name="memory"/> and never calls malloc or free: for a static
    /// array, stackalloc, or a region of RAM on a machine without a heap. The
    /// arena keeps its bookkeeping at the start of the buffer (about 150 bytes
    /// on 64-bit targets), and Capacity says what is left for allocations. It
    /// can't grow: an allocation
    /// that doesn't fit in what is left fails, as out of memory does (null, or
    /// the end of the program with <paramref name="stopOnOutOfMemory"/>).
    /// Reset, Restore and Save work as usual; Destroy just ends the handle.
    /// Returns a not-created arena if the buffer is too small for the
    /// bookkeeping.
    /// </summary>
    /// <safety>
    /// The <paramref name="size"/> bytes at <paramref name="memory"/> must be
    /// writable, used for nothing else, and outlive the arena and everything
    /// allocated from it. For stackalloc, that means the arena must not be used
    /// after the method that allocated the buffer returns.
    /// </safety>
    public static unsafe Arena FromBuffer(void* memory, nuint size, string name = null, bool stopOnOutOfMemory = false)
    {
        byte* start = (byte*)memory;
        byte* aligned = AlignUp(start, DefaultAlign);
        nuint header = (nuint)(aligned - start) + StateSize + (nuint)sizeof(Block);
        if (memory == null || aligned < start || size <= header)
        {
            if (stopOnOutOfMemory)
                Environment.FailFast("Arena.FromBuffer: the buffer is too small for the arena's bookkeeping");
            return default;
        }

        State* state = (State*)aligned;
        Block* block = (Block*)(aligned + StateSize);
        unsafe
        {
            // SAFETY: the caller promised size writable bytes at memory, and
            // the state, the block header and the data fit in them.
            memset(state, 0, (nuint)sizeof(State));
            nuint capacity = size - header;
            state->InitialSize = capacity;
            state->LastNormal = capacity;
            state->MaxCapacity = capacity;  // which also means no other block
            state->InBuffer = true;
            state->Poison = DebugChecks;
            state->Name = Unsafe.As<string, nint>(ref name);
            state->StopOnOutOfMemory = stopOnOutOfMemory;

            block->Next = null;
            block->Start = (byte*)block + sizeof(Block);
            block->End = block->Start + capacity;
            block->Size = capacity;
            Poison(state, block->Start, block->End);

            state->First = block;
            state->Current = block;
            state->Ptr = block->Start;
            state->BlockCount = 1;
            state->Capacity = capacity;
            state->PeakCapacity = capacity;
        }
        return new Arena { _state = state };
    }

    /// <summary>FromBuffer over a span's memory.</summary>
    /// <safety>
    /// As for FromBuffer(void*, nuint): the span's memory must outlive the
    /// arena and everything allocated from it, and be used for nothing else.
    /// </safety>
    public static unsafe Arena FromBuffer(Span<byte> buffer, string name = null, bool stopOnOutOfMemory = false)
    {
        unsafe
        {
            // SAFETY: the pointer and length come from the span; nothing moves
            // memory in zero mode, so the pointer stays valid after `fixed`,
            // for as long as the caller promised the memory lives.
            fixed (byte* p = buffer)
                return FromBuffer(p, (nuint)buffer.Length, name, stopOnOutOfMemory);
        }
    }

    // The state's size rounded up, so a buffer arena's block header and data
    // stay 16-byte aligned behind it.
    static nuint StateSize => ((nuint)sizeof(State) + (DefaultAlign - 1)) & ~(DefaultAlign - 1);

    public bool IsCreated => _state != null;

    // SAFETY (for every `unsafe` block reading _state below): a non-null _state
    // points at a live State unless the arena was destroyed, and Destroy is
    // unsafe: its caller promises no copy of the handle is used afterwards.

    public int BlockCount
    {
        get
        {
            if (_state == null)
                return 0;
            unsafe { return _state->BlockCount; }
        }
    }

    /// <summary>Bytes handed out, padding excluded.</summary>
    public nuint Used
    {
        get
        {
            if (_state == null)
                return 0;
            unsafe { return _state->Used; }
        }
    }

    /// <summary>The sum of all block sizes.</summary>
    public nuint Capacity
    {
        get
        {
            if (_state == null)
                return 0;
            unsafe { return _state->Capacity; }
        }
    }

    /// <summary>The highest Used has been since the arena was created. A good first block size.</summary>
    public nuint PeakUsed
    {
        get
        {
            if (_state == null)
                return 0;
            unsafe { return _state->PeakUsed; }
        }
    }

    /// <summary>The highest Capacity has been since the arena was created.</summary>
    public nuint PeakCapacity
    {
        get
        {
            if (_state == null)
                return 0;
            unsafe { return _state->PeakCapacity; }
        }
    }

    /// <summary>
    /// The most Capacity may reach: maxCapacity from Create, or what a
    /// FromBuffer arena holds. 0 means no limit.
    /// </summary>
    public nuint MaxCapacity
    {
        get
        {
            if (_state == null)
                return 0;
            unsafe { return _state->MaxCapacity; }
        }
    }

    /// <summary>The name given to Create, or null.</summary>
    public string Name
    {
        get
        {
            if (_state == null)
                return null;
            unsafe { return NameOf(_state); }
        }
    }

    public nuint NextBlockSize => _state == null ? 0 : NextNormalSize(_state);

    public nuint CurrentBlockSize
    {
        get
        {
            if (_state == null)
                return 0;
            unsafe { return _state->Current->Size; }
        }
    }

    public nuint CurrentBlockFree
    {
        get
        {
            if (_state == null)
                return 0;
            unsafe { return (nuint)(_state->Current->End - _state->Ptr); }
        }
    }

    /// <summary>Returns <paramref name="size"/> bytes aligned to <paramref name="align"/>, or null.</summary>
    public void* Alloc(nuint size, nuint align = DefaultAlign, bool zero = true)
    {
        State* state = _state;
        if (state == null || align == 0 || (align & (align - 1)) != 0)
            return null;

        unsafe
        {
            byte* p = AlignUp(state->Ptr, align);
            if (!Fits(state, p, size))
            {
                if (size > MaxSize - align)
                    return OutOfMemory(state);

                nuint needed = size + align - 1; // worst-case padding in a fresh block
                nuint normal = NextNormalSize(state);
                // Under a limit, a normal block shrinks to what is left, so
                // the request fails only if it doesn't fit in that.
                if (state->MaxCapacity != 0 && normal > state->MaxCapacity - state->Capacity)
                    normal = state->MaxCapacity - state->Capacity;
                bool dedicated = needed > normal;

                nuint block = dedicated ? needed : normal;
                if (AddBlock(state, block) == null)
                    return OutOfMemory(state, block);

                if (!dedicated)
                {
                    state->PrevNormal = state->LastNormal;
                    state->LastNormal = normal;
                }
                p = AlignUp(state->Ptr, align);
            }

            state->Ptr = p + size;
            state->Used += size;
            if (state->Used > state->PeakUsed)
                state->PeakUsed = state->Used;

            if (zero && size != 0)
                memset(p, 0, size); // SAFETY: [p, p + size) lies inside the current block.

            return p;
        }
    }

    public T* Alloc<T>(nuint align = DefaultAlign) where T : unmanaged
        => (T*)Alloc((nuint)sizeof(T), align);

    /// <summary>Returns room for <paramref name="count"/> elements, or null on overflow or OOM.</summary>
    public T* AllocArray<T>(int count, nuint align = DefaultAlign, bool zero = true) where T : unmanaged
    {
        if (count < 0)
            return null;

        nuint elementSize = (nuint)sizeof(T);
        if ((nuint)count > MaxSize / elementSize)
            return null;

        return (T*)Alloc((nuint)count * elementSize, align, zero);
    }

    /// <summary>Like AllocArray, wrapped in a span. Returns an empty span on failure.</summary>
    /// <safety>The span points into the arena. Don't use it after the memory is freed.</safety>
    public unsafe Span<T> AllocSpan<T>(int count, nuint align = DefaultAlign) where T : unmanaged
    {
        T* p = AllocArray<T>(count, align);
        if (p == null)
            return default;
        unsafe { return new Span<T>(p, count); }
    }

    /// <summary>
    /// Grows an allocation in place from <paramref name="oldSize"/> to
    /// <paramref name="newSize"/> bytes. This only works for the arena's most
    /// recent allocation, and only while its block has room; otherwise it returns
    /// false and changes nothing. The added bytes are not zeroed.
    /// </summary>
    /// <remarks>Lets a buffer that is still last in the arena grow without leaving its old copy behind.</remarks>
    public bool TryExtend(void* allocation, nuint oldSize, nuint newSize)
    {
        State* state = _state;
        if (state == null || allocation == null || newSize < oldSize)
            return false;

        unsafe
        {
            // Comparing addresses only; nothing is dereferenced through allocation.
            byte* end = (byte*)allocation + oldSize;
            nuint extra = newSize - oldSize;
            if (end != state->Ptr || extra > (nuint)(state->Current->End - end))
                return false;

            state->Ptr = end + extra;
            state->Used += extra;
            if (state->Used > state->PeakUsed)
                state->PeakUsed = state->Used;
        }
        return true;
    }

    /// <summary>
    /// Whether <paramref name="pointer"/> points into memory this arena has
    /// handed out and not freed since. Approximate: padding between allocations
    /// counts as handed out.
    /// </summary>
    public bool IsLive(void* pointer) => IsLive(pointer, 1);

    /// <summary>
    /// Like IsLive(pointer), for all <paramref name="size"/> bytes from
    /// <paramref name="pointer"/>: false too if only the end of that range was
    /// freed, as happens to a buffer that grew in place after a Save() once the
    /// mark is restored.
    /// </summary>
    public bool IsLive(void* pointer, nuint size)
    {
        State* state = _state;
        if (state == null || pointer == null)
            return false;
        if (size == 0)
            size = 1;

        unsafe
        {
            // Comparing addresses only; nothing is dereferenced through pointer.
            byte* p = (byte*)pointer;
            for (Block* block = state->First; block != null; block = block->Next)
            {
                byte* end = block == state->Current ? state->Ptr : block->End;
                if (p >= block->Start && p < end)
                    return size <= (nuint)(end - p);
            }
        }
        return false;
    }

    /// <summary>
    /// For collections: under DebugChecks, ends the program if any of the
    /// <paramref name="size"/> bytes of <paramref name="buffer"/>, which the
    /// collection got from <paramref name="arena"/>, have been freed since.
    /// </summary>
    internal static void CheckLive(Arena arena, void* buffer, nuint size)
    {
        if (!DebugChecks)
            return;
        // The collection's own header was freed and filled: its arena field is poison.
        if ((nuint)arena._state == PoisonPointer)
            Environment.FailFast("Arena.DebugChecks: collection used after its arena was reset or restored");
        if (arena._state != null && !arena.IsLive(buffer, size))
        {
            // SAFETY: the state is live; see the note above BlockCount.
            unsafe { Fail(arena._state, "Arena.DebugChecks: collection's buffer was freed (it grew after Save() and was used after Restore(), or its arena was reset)"); }
        }
    }

    /// <summary>Remembers the arena's current position, for Restore().</summary>
    public Mark Save()
    {
        State* state = _state;
        if (state == null)
            return default;

        unsafe
        {
            return new Mark
            {
                Owner = state,
                Block = state->Current,
                Ptr = state->Ptr,
                Used = state->Used,
                LastNormal = state->LastNormal,
                PrevNormal = state->PrevNormal,
                Generation = state->Generation,
            };
        }
    }

    /// <summary>
    /// Frees everything allocated since <paramref name="mark"/> was saved, and
    /// nothing from before it. Marks must be restored newest first: restoring a
    /// mark also ends every mark saved after it. A mark from another arena, from
    /// before a Reset(), or one that points past what the arena now holds (it was
    /// ended by restoring an older mark) ends the program. That last check can
    /// miss an ended mark once enough has been allocated again to cover its
    /// position; OpenScope() rules the mistake out instead.
    /// </summary>
    /// <safety>
    /// Every pointer, span and collection allocated since the mark becomes
    /// dangling. That includes the buffer of an older List, Dict or StrBuilder
    /// that grew after the mark: its new buffer is freed here while the
    /// collection still points at it, and later allocations overwrite it without
    /// any error. So between Save() and Restore(), only add to collections
    /// created after the mark. For temporary work that adds to long-lived
    /// collections, use a separate scratch arena instead.
    /// </safety>
    public unsafe void Restore(Mark mark)
    {
        State* state = _state;
        if (state == null)
            return;

        unsafe
        {
            if (mark.Owner != state || mark.Generation != state->Generation)
                Fail(state, "Arena.Restore: the mark doesn't belong to this arena's current contents");

            // Comparing addresses only: the mark's block is not read until it
            // has been found in the chain.
            Block* keep = state->First;
            while (keep != null && keep != (Block*)mark.Block)
                keep = keep->Next;
            if (keep == null || mark.Used > state->Used || (keep == state->Current && mark.Ptr > state->Ptr))
                Fail(state, "Arena.Restore: the mark was already freed by restoring an older one (restore marks newest first)");

            if (state->BeforeFree != null)
                state->BeforeFree(state, &mark, false);

            // SAFETY: keep is in the chain; the blocks after it were added
            // since the mark and are freed once.
            Block* block = keep->Next;
            while (block != null)
            {
                Block* next = block->Next;
                state->BlockCount--;
                state->Capacity -= block->Size;
                Poison(state, block->Start, block->End);
                free(block);
                block = next;
            }
            keep->Next = null;
            Poison(state, mark.Ptr, keep == state->Current ? state->Ptr : keep->End);

            state->Current = keep;
            state->Ptr = mark.Ptr;
            state->Used = mark.Used;
            state->LastNormal = mark.LastNormal;
            state->PrevNormal = mark.PrevNormal;
        }
    }

    /// <summary>
    /// Save() for a using block: leaving the block restores the mark, so the
    /// two can't be left unpaired or get out of order.
    /// <code>
    /// Arena.Scope temp;
    /// unsafe { temp = arena.OpenScope(); } // SAFETY: nothing from inside is used after the block
    /// using (temp)
    /// {
    ///     ... temporary allocations ...
    /// }
    /// </code>
    /// </summary>
    /// <safety>
    /// As for Restore: everything allocated inside the block dangles after it,
    /// including the buffer of an older collection that grew inside it.
    /// </safety>
    public unsafe Scope OpenScope() => new Scope { Arena = this, Mark = Save() };

    /// <summary>
    /// Until the returned scope is disposed, every object the program creates
    /// (new, boxing, closures, delegates, arrays, string concatenation) is
    /// allocated in this arena instead of with calloc, so resetting the arena
    /// frees it: ordinary C# stops leaking.
    /// <code>
    /// Arena frame = Arena.Create(64 * 1024);
    /// ObjectScope objects;
    /// unsafe { objects = frame.AllocateObjectsHere(); } // SAFETY: see below
    /// using (objects)
    /// {
    ///     ... code that allocates ...
    /// }
    /// unsafe { frame.Reset(); } // SAFETY: nothing from the frame is kept
    /// </code>
    /// Scopes nest: disposing one puts back whatever was in effect before it.
    /// The arena must outlive the scope. The scope applies only to the thread
    /// that opens it: other threads keep allocating as before.
    /// </summary>
    /// <safety>
    /// The objects die with the arena (Reset, Restore, Destroy), so no
    /// reference to one may be kept past that: not in a static field, not in an
    /// object from outside the scope. That includes what the compiler keeps
    /// for you: in C#, a lambda that captures nothing is created the first
    /// time it runs and then cached in a static field. If that happens first
    /// inside the scope, the cache points into the arena. Run such code once
    /// before opening the scope. (Static constructors are no concern: what
    /// one creates is allocated outside every scope, so F#'s cached lambdas,
    /// made by static constructors, are too.) With Arena.DebugChecks on when the
    /// arena was created, Reset, Restore and Destroy check every static field
    /// and stop the program if one points into the memory they are about to
    /// free. Statics pointing into memory that stays are fine: an object kept
    /// in a static from an arena that is never freed is no mistake.
    /// The compiler also caches delegates outside static fields: a lambda that
    /// captures a variable declared outside the scope is cached in the closure
    /// that holds that variable, an object from outside, and the check can't
    /// see that. Declare what a lambda inside the scope captures inside the
    /// scope too, or in a method called from it.
    /// </safety>
    public unsafe ObjectScope AllocateObjectsHere()
    {
        if (_state == null)
            Environment.FailFast("Arena.AllocateObjectsHere: the arena was not created");
        ObjectScope scope = default;
        scope.State = _state;
        scope.Open = true;
        unsafe
        {
            // SAFETY: ObjectAllocator only stores the pointers; AllocateObject
            // is given this arena's live state, which the caller keeps alive.
            _state->BeforeFree = &CheckObjectsBeforeFree;
            System.Runtime.ObjectAllocator.Get(out scope.PreviousAllocate, out scope.PreviousContext);
            System.Runtime.ObjectAllocator.Set(&AllocateObject, _state);
        }
        return scope;
    }

    /// <summary>
    /// AllocateObjectsHere() and OpenScope() in one: until the returned scope
    /// is disposed, every object the program creates is allocated in this
    /// arena, and disposing it frees them all, along with anything else
    /// allocated from the arena inside the block. Objects then live exactly
    /// as long as the using block, like stack variables:
    /// <code>
    /// Arena.ObjectScope objects;
    /// unsafe { objects = frame.OpenObjectScope(); } // SAFETY: see below
    /// using (objects)
    /// {
    ///     var items = new int[16];
    ///     string s = a + b;
    /// }   // items and s are freed here
    /// </code>
    /// The arena may hold longer-lived data from before the scope; only what
    /// was allocated inside it is freed.
    /// </summary>
    /// <safety>
    /// AllocateObjectsHere's rules and OpenScope's together: no reference to
    /// anything created inside may outlive the block (not in a static field,
    /// not in an object or closure from outside, not a C# lambda run
    /// for the first time inside), and no collection created
    /// before the scope may grow inside it.
    /// </safety>
    public unsafe ObjectScope OpenObjectScope()
    {
        if (_state == null)
            Environment.FailFast("Arena.OpenObjectScope: the arena was not created");
        Mark mark = Save();
        ObjectScope scope;
        // SAFETY: as in AllocateObjectsHere.
        unsafe { scope = AllocateObjectsHere(); }
        scope.Mark = mark;
        scope.FreeOnDispose = true;
        return scope;
    }

    // OpenObjectScope for F#'s arena { } (fsharp/control/Arena.fs). A builder
    // can't hold a ref struct (fsc refuses byref-like builders), and the
    // scope has to open when `arena frame` is evaluated, before the body's
    // closure is made (unoptimized fsc makes one: made first, it would be
    // outside the arena on every call). So the state goes from the builder to
    // Run as this plain struct, which Run turns back into the ObjectScope.
    internal struct OpenedObjects
    {
        internal delegate*<void*, nuint, void*> PreviousAllocate;
        internal void* PreviousContext;
        internal void* State;
        internal Mark Mark;
    }

    internal OpenedObjects OpenObjectsForBuilder()
    {
        ObjectScope scope;
        // SAFETY: the builder's Run closes it (ObjectsOpenedForBuilder), and
        // lets out only values that hold nothing from the arena.
        unsafe { scope = OpenObjectScope(); }
        return new OpenedObjects
        {
            PreviousAllocate = scope.PreviousAllocate,
            PreviousContext = scope.PreviousContext,
            State = scope.State,
            Mark = scope.Mark,
        };
    }

    // The scope OpenObjectsForBuilder opened, to close. Stops the program if
    // it isn't the innermost one open (a builder kept and used again, or
    // after its scope closed).
    internal static ObjectScope ObjectsOpenedForBuilder(OpenedObjects opened)
    {
        bool current;
        unsafe
        {
            // SAFETY: only compares the allocator in effect with this scope's.
            System.Runtime.ObjectAllocator.Get(out var allocate, out var context);
            current = allocate == (delegate*<void*, nuint, void*>)&AllocateObject && context == opened.State;
        }
        if (!current)
            Environment.FailFast("arena { }: its scope isn't the one open; write `arena frame { ... }` in one piece, without keeping `arena frame` in a value");
        ObjectScope scope = default;
        scope.PreviousAllocate = opened.PreviousAllocate;
        scope.PreviousContext = opened.PreviousContext;
        scope.State = opened.State;
        scope.Mark = opened.Mark;
        scope.FreeOnDispose = true;
        scope.Open = true;
        return scope;
    }

    // Whether value, a reference, is an object allocated in the scope
    // OpenObjectsForBuilder opened: one closing that scope frees. For
    // Arena.DebugChecks in F#'s agents (fsharp/control): a message made in a
    // scope and still queued when it ends. A struct: false (it was copied;
    // references inside it aren't looked at).
    internal static bool IsInScope<T>(OpenedObjects scope, T value)
    {
        if (default(T) != null)
            return false; // a value type
        object obj = value;
        if (obj == null)
            return false;
        unsafe
        {
            // SAFETY: compares addresses only. The scope's mark is the newest
            // still open (the caller is closing it), so its block is in the
            // chain, and the blocks after it were added since.
            byte* p = *(byte**)Unsafe.AsPointer(ref obj);
            State* state = (State*)scope.State;
            Block* block = (Block*)scope.Mark.Block;
            byte* start = scope.Mark.Ptr;
            while (block != null)
            {
                byte* end = block == state->Current ? state->Ptr : block->End;
                if (p >= start && p < end)
                    return true;
                block = block->Next;
                if (block != null)
                    start = block->Start;
            }
        }
        return false;
    }

    // After a throw inside F#'s Async.InArena: puts back the allocator from
    // before the scope but frees nothing, so the exception and what it points
    // at stay until the arena is reset (as with Catch.Run).
    internal static void LeaveObjectsForBuilder(OpenedObjects opened)
    {
        // SAFETY: the allocator in effect before the scope.
        unsafe { System.Runtime.ObjectAllocator.Set(opened.PreviousAllocate, opened.PreviousContext); }
    }

    // The object allocator in effect, as a value F#'s async code keeps across
    // waits (fsharp/control/Control.fs): a computation in Async.InArena
    // allocates in the arena in each of its pieces, and the objects for its
    // waits come from where it started.
    internal struct AllocatorSetting
    {
        internal delegate*<void*, nuint, void*> Allocate;
        internal void* Context;
    }

    internal static AllocatorSetting CurrentAllocator()
    {
        AllocatorSetting setting = default;
        // SAFETY: only reads the setting.
        unsafe { System.Runtime.ObjectAllocator.Get(out setting.Allocate, out setting.Context); }
        return setting;
    }

    // Puts a setting from CurrentAllocator back.
    internal static void SetAllocator(AllocatorSetting setting)
    {
        // SAFETY: a setting CurrentAllocator read, whose arena (if any) the
        // caller keeps alive while it's in effect.
        unsafe { System.Runtime.ObjectAllocator.Set(setting.Allocate, setting.Context); }
    }

    // A copy of text made by that allocator.
    internal static string CopyStringWith(AllocatorSetting setting, string text)
    {
        if (text == null)
            return null;
        AllocatorSetting current = CurrentAllocator();
        SetAllocator(setting);
        string copy = string.Concat(text, null); // a new string, always
        SetAllocator(current);
        return copy;
    }

    // ObjectAllocator's function: objects are zeroed and 8-byte aligned (a
    // long or double in an object needs it on 32-bit targets).
    static unsafe void* AllocateObject(void* context, nuint size)
        => new Arena { _state = (State*)context }.Alloc(size, 8, zero: true);

    /// <summary>
    /// From AllocateObjectsHere or OpenObjectScope, for a using block: disposing
    /// it puts back the allocator that was in effect before, and for
    /// OpenObjectScope frees what was allocated inside.
    /// </summary>
    public ref struct ObjectScope : System.IDisposable
    {
        internal delegate*<void*, nuint, void*> PreviousAllocate;
        internal void* PreviousContext;
        internal void* State; // the arena's State*
        internal Mark Mark;
        internal bool FreeOnDispose; // from OpenObjectScope
        internal bool Open;          // until the first Dispose

        /// <summary>
        /// A copy of <paramref name="text"/> made outside the scope, by the
        /// allocator in effect before it, so it outlives the scope: how a
        /// string made inside gets out. Null stays null.
        /// <code>
        /// string name;
        /// using (objects) { name = objects.CopyOut(first + " " + last); }
        /// </code>
        /// </summary>
        public readonly string CopyOut(string text)
        {
            if (!Open)
                Environment.FailFast("Arena.ObjectScope.CopyOut: the scope is closed");
            if (text == null)
                return null;
            unsafe
            {
                // SAFETY: the allocator before the scope, then this scope's
                // again, as Dispose and AllocateObjectsHere set them.
                System.Runtime.ObjectAllocator.Get(out var allocate, out var context);
                System.Runtime.ObjectAllocator.Set(PreviousAllocate, PreviousContext);
                string copy = string.Concat(text, null); // a new string, always
                System.Runtime.ObjectAllocator.Set(allocate, context);
                return copy;
            }
        }

        /// <summary>
        /// Puts back the previous allocator. From OpenObjectScope, then frees
        /// everything allocated inside. A second Dispose, or one of a scope
        /// that was never opened, does nothing.
        /// </summary>
        public void Dispose()
        {
            if (!Open)
                return;
            Open = false;
            // SAFETY: puts back what AllocateObjectsHere found.
            unsafe { System.Runtime.ObjectAllocator.Set(PreviousAllocate, PreviousContext); }
            if (FreeOnDispose)
            {
                // SAFETY: OpenObjectScope's caller promised that nothing created
                // inside the scope is used after it.
                unsafe { new Arena { _state = (State*)State }.Restore(Mark); }
            }
        }
    }

    // State.BeforeFree for an arena an object scope has used, before Restore
    // frees what came after `mark` (null: Reset or Destroy, everything).
    // Destroy: stops the program if this thread's open scope still sends
    // objects here. With debug checks on: stops it if a static field points
    // into the memory about to be freed. Only an object created inside an
    // object scope can make that happen; statics that point into memory that
    // stays are fine.
    static unsafe void CheckObjectsBeforeFree(State* state, Mark* mark, bool destroying)
    {
        unsafe
        {
            string name = mark != null ? "Arena.Restore" : destroying ? "Arena.Destroy" : "Arena.Reset";
            if (destroying)
            {
                System.Runtime.ObjectAllocator.Get(out _, out void* current);
                if (current == state)
                    Fail(state, name + ": the arena is in use by an object scope that is still open on this thread");
            }
            if (!state->Poison)
                return;
            // SAFETY: Restore has checked that the mark's block is in the
            // chain and its pointer inside it.
            Block* block = mark != null ? (Block*)mark->Block : state->First;
            byte* from = mark != null ? mark->Ptr : null;
            for (; block != null; block = block->Next, from = null)
            {
                byte* start = from != null ? from : block->Start;
                byte* end = block == state->Current ? state->Ptr : block->End;
                if (start < end && System.Runtime.ObjectAllocator.StaticsReferInto(start, end))
                    Fail(state, name + ": a static field points at an object it is about to free. "
                        + "A C# lambda that captures nothing probably ran for the first time inside an object scope; run it once before.");
            }
        }
    }

    /// <summary>
    /// Prints the arena's name, how full it is and its blocks to standard output:
    /// <code>
    /// Arena "frame": 920 of 1152 bytes used (peak 920 of 1152), 3 blocks
    ///   block 1: 128 bytes
    ///   block 2: 256 bytes
    ///   block 3: 768 bytes, 88 free (current)
    /// </code>
    /// Many small blocks mean the first block is too small for the workload;
    /// PeakUsed is the size that would have held everything.
    /// </summary>
    public void Dump()
    {
        State* state = _state;
        if (state == null)
        {
            Out.Line("Arena: not created");
            return;
        }

        unsafe
        {
            // SAFETY: the state is live (see above) and its blocks are read, not written.
            string name = NameOf(state);
            Out.Write("Arena");
            if (name != null)
            {
                Out.Write(" \"");
                Out.Write(name);
                Out.Write("\"");
            }
            Out.Write(": ");
            Out.Write((long)state->Used);
            Out.Write(" of ");
            Out.Write((long)state->Capacity);
            Out.Write(" bytes used (peak ");
            Out.Write((long)state->PeakUsed);
            Out.Write(" of ");
            Out.Write((long)state->PeakCapacity);
            Out.Write("), ");
            Out.Write(state->BlockCount);
            Out.Write(state->BlockCount == 1 ? " block" : " blocks");
            if (state->InBuffer)
                Out.Write(", in a buffer");
            else if (state->MaxCapacity != 0)
            {
                Out.Write(", limit ");
                Out.Write((long)state->MaxCapacity);
            }
            Out.Line();

            int index = 1;
            for (Block* block = state->First; block != null; block = block->Next)
            {
                Out.Write("  block ");
                Out.Write(index++);
                Out.Write(": ");
                Out.Write((long)block->Size);
                Out.Write(" bytes");
                if (block == state->Current)
                {
                    Out.Write(", ");
                    Out.Write((long)(block->End - state->Ptr));
                    Out.Write(" free (current)");
                }
                Out.Line();
            }
        }
    }

    /// <summary>
    /// Frees every allocation at once. With <paramref name="keepCapacity"/> the blocks are
    /// merged into a single block of the same total size; otherwise the arena shrinks back
    /// to one initial-size block and the growth sequence restarts.
    /// </summary>
    /// <safety>Every pointer, span and collection that points into the arena becomes dangling.</safety>
    public unsafe void Reset(bool keepCapacity = true)
    {
        State* state = _state;
        if (state == null)
            return;

        unsafe
        {
            if (state->BeforeFree != null)
                state->BeforeFree(state, null, false);
            state->Used = 0;
            state->Generation++;

            if (state->BlockCount == 1 && (keepCapacity || state->Capacity == state->InitialSize))
            {
                Poison(state, state->First->Start, state->Ptr);
                state->Ptr = state->First->Start;
                if (!keepCapacity)
                {
                    state->LastNormal = state->InitialSize;
                    state->PrevNormal = 0;
                }
                return;
            }

            nuint total = state->Capacity;
            FreeAllBlocks(state);

            if (keepCapacity && AddBlock(state, total) != null)
            {
                state->LastNormal = total;
                state->PrevNormal = 0;
                return;
            }

            // Shrink back (or fall back when the merged block can't be allocated).
            state->LastNormal = state->InitialSize;
            state->PrevNormal = 0;
            if (AddBlock(state, state->InitialSize) == null)
                Fail(state, "Arena.Reset: out of memory");
        }
    }

    /// <summary>Returns all memory to the system. This handle becomes not-created.</summary>
    /// <safety>
    /// Every pointer, span and collection that points into the arena becomes
    /// dangling, and so does every other copy of this handle.
    /// </safety>
    public unsafe void Destroy()
    {
        State* state = _state;
        if (state == null)
            return;

        unsafe
        {
            if (state->BeforeFree != null)
                state->BeforeFree(state, null, true);
            if (state->InBuffer)
            {
                // The memory is the caller's: nothing to free, but a stale
                // pointer into it should still stand out.
                Poison(state, state->First->Start, state->Ptr);
            }
            else
            {
                // SAFETY: the state and its blocks come from malloc and are freed exactly once here.
                FreeAllBlocks(state);
                free(state);
            }
        }
        _state = null;
    }

    static unsafe void FreeAllBlocks(State* state)
    {
        unsafe
        {
            // SAFETY: blocks come from malloc in AddBlock and are freed exactly once here.
            Block* block = state->First;
            while (block != null)
            {
                Block* next = block->Next;
                Poison(state, block->Start, block == state->Current ? state->Ptr : block->End);
                free(block);
                block = next;
            }
            state->First = null;
            state->Current = null;
            state->Ptr = null;
            state->BlockCount = 0;
            state->Capacity = 0;
        }
    }

    static unsafe Block* AddBlock(State* state, nuint size)
    {
        if (size > MaxSize - (nuint)sizeof(Block))
            return null;
        unsafe
        {
            // SAFETY: callers pass a live state.
            if (state->MaxCapacity != 0 && size > state->MaxCapacity - state->Capacity)
                return null; // over the limit: leave the arena exactly as it was
        }

        byte* memory = (byte*)malloc((nuint)sizeof(Block) + size);
        if (memory == null)
            return null; // leave the arena exactly as it was

        Block* block = (Block*)memory;
        unsafe
        {
            // SAFETY: memory holds sizeof(Block) + size bytes.
            block->Next = null;
            block->Start = memory + sizeof(Block);
            block->End = block->Start + size;
            block->Size = size;
            Poison(state, block->Start, block->End);

            if (state->Current == null)
                state->First = block;
            else
                state->Current->Next = block;

            state->Ptr = block->Start;
            state->Current = block;
            state->BlockCount++;
            state->Capacity += size;
            if (state->Capacity > state->PeakCapacity)
                state->PeakCapacity = state->Capacity;
        }
        return block;
    }

    static unsafe string NameOf(State* state)
    {
        // SAFETY: Name holds null or the address of the string given to Create,
        // which the caller keeps alive. In zero mode a reference is its address.
        unsafe { return Unsafe.As<nint, string>(ref state->Name); }
    }

    // What a failed allocation returns: null, unless the arena was created to
    // end the program instead. block is the size of the block that couldn't
    // be added, if any: the message blames maxCapacity only if that block
    // was over it, not when malloc said no.
    static unsafe void* OutOfMemory(State* state, nuint block = 0)
    {
        unsafe
        {
            // SAFETY: callers pass a live state.
            if (state->StopOnOutOfMemory)
            {
                if (state->InBuffer)
                    Fail(state, "Arena: out of memory (its buffer is full)");
                if (block != 0 && state->MaxCapacity != 0 && block > state->MaxCapacity - state->Capacity)
                    Fail(state, "Arena: out of memory (it reached its maxCapacity)");
                Fail(state, "Arena: out of memory");
            }
        }
        return null;
    }

    // Ends the program, saying first which arena it was if that one has a name.
    static unsafe void Fail(State* state, string message)
    {
        string name;
        unsafe { name = NameOf(state); } // SAFETY: callers pass a live state.
        if (name != null)
        {
            Out.Write("Arena \"");
            Out.Write(name);
            Out.Line("\":");
        }
        Environment.FailFast(message);
    }

    // Fills [start, end) so that stale and uninitialized reads stand out.
    static unsafe void Poison(State* state, byte* start, byte* end)
    {
        unsafe
        {
            // SAFETY: callers pass a range inside one of the arena's blocks.
            if (state->Poison && end > start)
                memset(start, PoisonByte, (nuint)(end - start));
        }
    }

    static nuint NextNormalSize(State* state)
    {
        unsafe
        {
            nuint next = state->LastNormal + state->PrevNormal;
            if (next < state->LastNormal) // overflow
                next = state->LastNormal;
            return next == 0 ? state->InitialSize : next;
        }
    }

    static unsafe bool Fits(State* state, byte* p, nuint size)
    {
        byte* end;
        unsafe { end = state->Current->End; }
        return p <= end && size <= (nuint)(end - p);
    }

    static byte* AlignUp(byte* p, nuint align)
        => (byte*)(((nuint)p + (align - 1)) & ~(align - 1));
}
