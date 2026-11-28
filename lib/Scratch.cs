// Arena.Scratch: temporary memory whose lifetime the compiler checks.
//
//   int sum = Arena.Scratch(static (scoped ScratchScope s) =>
//   {
//       Span<int> buffer = s.AllocSpan<int>(1000);
//       ...
//       return total;
//   });                    // buffer's memory is reused from here on
//
// Everything allocated through the scope is freed when the callback returns,
// and the compiler makes sure nothing from it is still reachable then: the
// scope is a ref struct passed as a scoped parameter, so a span or ref it
// gives out can't be returned, stored in a field or a static, put in the
// state, or kept in a variable that outlives the callback. Each of those is a
// compile error, not a dangling pointer. No unsafe block is needed anywhere.
// The one case the compiler can't see, an outer scope allocating inside a
// nested Arena.Scratch, stops the program instead (see below).
//
// The memory comes from a scratch arena of the thread's own, not from one of
// yours, so a List or StrBuilder of yours that grows during the callback grows
// in its own arena and is unaffected (with Arena.OpenScope it would grow into
// memory the scope then frees). Scratch calls nest. Each thread keeps its
// scratch arena for its whole life, but only the arena's first block (16 KB,
// or 2 KB on a small machine): what a callback needs beyond that comes from
// malloc and goes back when the callback returns. Arena.ScratchSize sets the
// first block's size and Arena.ScratchMaxCapacity a limit, for the scratch
// arenas made afterwards.
//
// The overloads that take a Span<byte> first use that memory instead, for one
// call: an arena in the buffer stands in for the thread's scratch arena until
// the callback returns, nested Scratch calls included. Nothing comes from
// malloc, and a callback that needs more than the buffer holds stops the
// program. They are unsafe: nothing may use the buffer during the call.
// Arena.ScratchOnStack does the same in memory on its own stack frame, of a
// size you give: safe, since nothing but the scope sees that memory.
//
// To pass data in and out without a closure (which would allocate), use the
// overloads with a state passed by ref; it may be a ref struct holding spans
// of the caller's. For data that only goes in, or spans the callback fills,
// there are overloads with an input passed by value.
//
// Besides spans, the scope makes growable lists and text, dictionaries and
// sets, and pools (items found by a Handle<T>, removed one by one), that live
// as long:
//
//   ScratchList<int> evens = s.List<int>();
//   ScratchText line = s.Text();
//   line.Append($"{evens.Count} evens");
//
// They are ref structs too, so the same compile-time rules keep them inside
// the callback. One rule the compiler can't check: a scope, and the lists and
// text it made, allocate only while their callback is the innermost one
// running. Inside a nested Arena.Scratch (reached through a state passed by
// ref, or an input) the outer ones can still be read and written, but allocating from the
// scope, adding to a list or appending to text stops the program: that
// memory would be freed when the inner callback returns.

using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

// Each thread's scratch state: its arena and how many Arena.Scratch calls are
// running on it. It is the arena's first allocation, which no Restore frees.
internal struct ScratchThread
{
    public Arena Arena;
    public int Depth;

    // Stops the program unless the callback at depth is the innermost one
    // running, the only one allowed to allocate from the scratch arena.
    public static void CheckInnermost(ScratchThread* thread, int depth, string what)
    {
        if (thread == null)
            Environment.FailFast("Arena.Scratch: a ScratchScope, ScratchList or ScratchText not from Arena.Scratch");
        unsafe
        {
            // SAFETY: a non-null thread is the live scratch state of this
            // thread: scopes, lists and text can't leave its callback, and ref
            // structs can't reach another thread.
            if (thread->Depth != depth)
                Environment.FailFast(what);
        }
    }
}

/// <summary>Allocates memory that lives until the Arena.Scratch callback it was given to returns.</summary>
public ref struct ScratchScope
{
    readonly ScratchThread* _thread;
    readonly int _depth;

    internal ScratchScope(ScratchThread* thread)
    {
        _thread = thread;
        unsafe { _depth = thread->Depth; } // SAFETY: the live scratch state of this thread.
    }

    Arena Allocator()
    {
        ScratchThread.CheckInnermost(_thread, _depth,
            "Arena.Scratch: a scope allocated inside a nested Arena.Scratch, which would free the memory early");
        unsafe { return _thread->Arena; } // SAFETY: checked above.
    }

    /// <summary>A zeroed span of <paramref name="count"/> items, valid until the callback returns.</summary>
    public Span<T> AllocSpan<T>(int count) where T : unmanaged
    {
        Arena arena = Allocator();
        // SAFETY: the span points into the scratch arena, which Arena.Scratch
        // restores only after the callback returns; the compiler keeps the
        // span from outliving the callback (this scope is a scoped parameter),
        // and Allocator checked that no nested callback will free it sooner.
        unsafe { return arena.AllocSpan<T>(count); }
    }

    /// <summary>A zeroed T, valid until the callback returns.</summary>
    public ref T Alloc<T>() where T : unmanaged
    {
        Arena arena = Allocator();
        unsafe
        {
            // SAFETY: as in AllocSpan. The scratch arena stops the program
            // rather than return null.
            T* item = arena.Alloc<T>();
            return ref *item;
        }
    }

    /// <summary>An empty list whose items live until the callback returns.</summary>
    public ScratchList<T> List<T>(int initialCapacity = 16) where T : unmanaged
        => new ScratchList<T>(global::List<T>.Create(Allocator(), initialCapacity), _thread, _depth);

    /// <summary>An empty dictionary whose entries live until the callback returns.</summary>
    public ScratchDict<TKey, TValue> Dict<TKey, TValue>(int capacity = 32)
        where TKey : unmanaged
        where TValue : unmanaged
        => new ScratchDict<TKey, TValue>(global::Dict<TKey, TValue>.Create(Allocator(), capacity), _thread, _depth);

    /// <summary>An empty set whose items live until the callback returns.</summary>
    public ScratchHashSet<T> HashSet<T>(int capacity = 32) where T : unmanaged
        => new ScratchHashSet<T>(global::HashSet<T>.Create(Allocator(), capacity), _thread, _depth);

    /// <summary>An empty pool whose items live until the callback returns.</summary>
    public ScratchPool<T> Pool<T>(int chunkSize = 64) where T : unmanaged
        => new ScratchPool<T>(global::Pool<T>.Create(Allocator(), chunkSize), _thread, _depth);

    /// <summary>Empty text, for building a string that lives until the callback returns.</summary>
    public ScratchText Text(int initialCapacity = 64)
        => new ScratchText(StrBuilder.Create(Allocator(), initialCapacity), _thread, _depth);
}

/// <summary>
/// A growable list from <see cref="ScratchScope.List{T}"/>, freed when the
/// Arena.Scratch callback returns. Copies share the same items.
/// </summary>
public ref struct ScratchList<T> where T : unmanaged
{
    readonly List<T> _list;
    readonly ScratchThread* _thread;
    readonly int _depth;

    internal ScratchList(List<T> list, ScratchThread* thread, int depth)
    {
        _list = list;
        _thread = thread;
        _depth = depth;
    }

    /// <summary>How many items the list holds.</summary>
    public int Count => _list.Count;

    /// <summary>The item at <paramref name="index"/>; out of range stops the program.</summary>
    public ref T this[int index]
    {
        get
        {
            // SAFETY: GetPtr checks the index. The items stay allocated until
            // the callback returns, even after growth moves them: the old
            // buffer is freed only with the rest of the scratch memory.
            unsafe { return ref *_list.GetPtr(index); }
        }
    }

    /// <summary>Appends an item, growing the list if it is full.</summary>
    public void Add(T item)
    {
        CheckInnermost();
        _list.Add(item); // the scratch arena stops the program rather than fail
    }

    /// <summary>Appends each of <paramref name="items"/>.</summary>
    public void AddRange(ReadOnlySpan<T> items)
    {
        CheckInnermost();
        foreach (T item in items)
            _list.Add(item);
    }

    /// <summary>Removes the item at <paramref name="index"/>, moving the later ones down. False if out of range.</summary>
    public bool RemoveAt(int index) => _list.RemoveAt(index);

    /// <summary>Removes every item; the memory stays the list's.</summary>
    public void Clear() => _list.Clear();

    /// <summary>
    /// The items, valid until the callback returns. After Add, RemoveAt or
    /// Clear it may show the items as they were, but its memory stays valid.
    /// </summary>
    public Span<T> AsSpan()
    {
        // SAFETY: as in the indexer; the compiler keeps the span inside the
        // callback, since this list can't leave it.
        unsafe { return _list.AsSpan(); }
    }

    /// <summary>For foreach: the items, as AsSpan shows them.</summary>
    public Span<T>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();

    void CheckInnermost() => ScratchThread.CheckInnermost(_thread, _depth,
        "Arena.Scratch: a ScratchList added to inside a nested Arena.Scratch, which would free its memory early");
}

/// <summary>
/// A dictionary from <see cref="ScratchScope.Dict{TKey, TValue}"/>, freed when
/// the Arena.Scratch callback returns; keys are compared as Dict's are. Clone
/// copies it into an arena you keep. Copies share the same entries.
/// </summary>
public ref struct ScratchDict<TKey, TValue>
    where TKey : unmanaged
    where TValue : unmanaged
{
    readonly Dict<TKey, TValue> _map;
    readonly ScratchThread* _thread;
    readonly int _depth;

    internal ScratchDict(Dict<TKey, TValue> map, ScratchThread* thread, int depth)
    {
        _map = map;
        _thread = thread;
        _depth = depth;
    }

    /// <summary>How many keys it holds.</summary>
    public int Count => _map.Count;

    /// <summary>Adds the key or changes its value.</summary>
    public void Set(TKey key, TValue value)
    {
        CheckCanAdd(key);
        _map.Set(key, value); // the scratch arena stops the program rather than fail
    }

    /// <summary>Adds the key if it isn't there. False if it was.</summary>
    public bool TryAdd(TKey key, TValue value)
    {
        CheckCanAdd(key);
        return _map.TryAdd(key, value);
    }

    /// <summary>The key's value, if it is there.</summary>
    public bool TryGet(TKey key, out TValue value) => _map.TryGet(key, out value);

    /// <summary>Whether the key is there.</summary>
    public bool ContainsKey(TKey key) => _map.ContainsKey(key);

    /// <summary>Removes the key. False if it wasn't there.</summary>
    public bool Remove(TKey key) => _map.Remove(key);

    /// <summary>Removes every key.</summary>
    public void Clear() => _map.Clear();

    /// <summary>For foreach: every key and value, in no particular order.</summary>
    public Dict<TKey, TValue>.Enumerator GetEnumerator() => _map.GetEnumerator();

    /// <summary>A copy in <paramref name="target"/>, an arena you keep: how the result leaves the callback.</summary>
    public Dict<TKey, TValue> Clone(Arena target) => _map.Clone(target);

    // A new key may grow the table, which only the innermost callback may do;
    // changing a key that is there allocates nothing.
    void CheckCanAdd(TKey key)
    {
        unsafe
        {
            // SAFETY: as in ScratchThread.CheckInnermost, which this falls back on.
            if (_thread != null && _thread->Depth == _depth)
                return;
        }
        if (_thread == null || !_map.ContainsKey(key))
            ScratchThread.CheckInnermost(_thread, _depth,
                "Arena.Scratch: a ScratchDict added to inside a nested Arena.Scratch, which would free its memory early");
    }
}

/// <summary>
/// A set from <see cref="ScratchScope.HashSet{T}"/>, freed when the
/// Arena.Scratch callback returns; items are compared as HashSet's are. Clone
/// copies it into an arena you keep. Copies share the same items.
/// </summary>
public ref struct ScratchHashSet<T> where T : unmanaged
{
    readonly HashSet<T> _set;
    readonly ScratchThread* _thread;
    readonly int _depth;

    internal ScratchHashSet(HashSet<T> set, ScratchThread* thread, int depth)
    {
        _set = set;
        _thread = thread;
        _depth = depth;
    }

    /// <summary>How many items it holds.</summary>
    public int Count => _set.Count;

    /// <summary>Adds the item. False if it was already there.</summary>
    public bool Add(T item)
    {
        unsafe
        {
            // SAFETY: as in ScratchDict.CheckCanAdd.
            bool innermost = _thread != null && _thread->Depth == _depth;
            if (!innermost && (_thread == null || !_set.Contains(item)))
                ScratchThread.CheckInnermost(_thread, _depth,
                    "Arena.Scratch: a ScratchHashSet added to inside a nested Arena.Scratch, which would free its memory early");
        }
        return _set.Add(item); // the scratch arena stops the program rather than fail
    }

    /// <summary>Whether the item is there.</summary>
    public bool Contains(T item) => _set.Contains(item);

    /// <summary>Removes the item. False if it wasn't there.</summary>
    public bool Remove(T item) => _set.Remove(item);

    /// <summary>Removes every item.</summary>
    public void Clear() => _set.Clear();

    /// <summary>For foreach: every item, in no particular order.</summary>
    public HashSet<T, DefaultEqualityComparer<T>>.Enumerator GetEnumerator() => _set.GetEnumerator();

    /// <summary>A copy in <paramref name="target"/>, an arena you keep: how the result leaves the callback.</summary>
    public HashSet<T> Clone(Arena target) => _set.Clone(target);
}

/// <summary>
/// A pool from <see cref="ScratchScope.Pool{T}"/>, freed when the Arena.Scratch
/// callback returns: items found by a Handle&lt;T&gt;, removed one by one, and
/// moved out with MoveTo into a pool you keep. Copies share the same items.
/// </summary>
/// <remarks>
/// A handle is a plain struct and can outlive the callback, but it finds
/// nothing without its pool, which can't.
/// </remarks>
public ref struct ScratchPool<T> where T : unmanaged
{
    readonly Pool<T> _pool;
    readonly ScratchThread* _thread;
    readonly int _depth;

    internal ScratchPool(Pool<T> pool, ScratchThread* thread, int depth)
    {
        _pool = pool;
        _thread = thread;
        _depth = depth;
    }

    /// <summary>How many items the pool holds.</summary>
    public int Count => _pool.Count;

    /// <summary>Adds an item and returns its handle.</summary>
    public Handle<T> Add(T item)
    {
        ScratchThread.CheckInnermost(_thread, _depth,
            "Arena.Scratch: a ScratchPool added to inside a nested Arena.Scratch, which would free its memory early");
        return _pool.Add(item); // the scratch arena stops the program rather than fail
    }

    /// <summary>The item the handle finds; one removed or moved stops the program.</summary>
    public ref T this[Handle<T> handle] => ref _pool[handle];

    /// <summary>Whether the handle finds an item.</summary>
    public bool Contains(Handle<T> handle) => _pool.Contains(handle);

    /// <summary>The item the handle finds, if it finds one.</summary>
    public bool TryGet(Handle<T> handle, out T item) => _pool.TryGet(handle, out item);

    /// <summary>Removes the item the handle finds. False if it finds none.</summary>
    public bool Remove(Handle<T> handle) => _pool.Remove(handle);

    /// <summary>Gives the item a new handle; the old one and its copies are refused from then on.</summary>
    public Handle<T> Move(ref Handle<T> handle) => _pool.Move(ref handle);

    /// <summary>
    /// Moves the item into <paramref name="target"/>, a pool you keep, and
    /// returns its handle there: how a result leaves the callback.
    /// </summary>
    public Handle<T> MoveTo(Pool<T> target, ref Handle<T> handle) => _pool.MoveTo(target, ref handle);

    /// <summary>Removes every item.</summary>
    public void Clear() => _pool.Clear();

    /// <summary>For foreach: the handle of each item.</summary>
    public Pool<T>.Enumerator GetEnumerator() => _pool.GetEnumerator();
}

/// <summary>
/// Growable text from <see cref="ScratchScope.Text"/>, with $"..." support,
/// freed when the Arena.Scratch callback returns. Copies share the same text.
/// </summary>
public ref struct ScratchText
{
    readonly StrBuilder _builder;
    readonly ScratchThread* _thread;
    readonly int _depth;

    internal ScratchText(StrBuilder builder, ScratchThread* thread, int depth)
    {
        _builder = builder;
        _thread = thread;
        _depth = depth;
    }

    /// <summary>How many chars the text holds.</summary>
    public int Length => _builder.Length;

    /// <summary>The text so far, valid until the callback returns.</summary>
    public ReadOnlySpan<char> AsSpan() => _builder.AsSpan();

    /// <summary>Appends <paramref name="text"/>.</summary>
    public void Append(ReadOnlySpan<char> text)
    {
        CheckInnermost();
        _builder.Append(text); // the scratch arena stops the program rather than fail
    }

    /// <summary>Appends one char.</summary>
    public void Append(char value)
    {
        CheckInnermost();
        _builder.Append(value);
    }

    /// <summary>Appends a number in decimal.</summary>
    public void Append(long value)
    {
        CheckInnermost();
        _builder.Append(value);
    }

    /// <summary>Appends a number in decimal.</summary>
    public void Append(int value) => Append((long)value);

    /// <summary>Appends a number as $"{value}" writes it.</summary>
    public void Append(double value)
    {
        CheckInnermost();
        _builder.Append(value);
    }

    /// <summary>Appends True or False.</summary>
    public void Append(bool value)
    {
        CheckInnermost();
        _builder.Append(value);
    }

    /// <summary>Appends $"..." directly, without an intermediate string.</summary>
    public void Append([InterpolatedStringHandlerArgument("")] ref StrBuilder.InterpolatedHandler handler)
    {
    }

    /// <summary>The char at <paramref name="index"/>; stops the program if it is out of range.</summary>
    public char this[int index]
    {
        get => _builder[index];
        set { var builder = _builder; builder[index] = value; }
    }

    /// <summary>Puts <paramref name="text"/> before the char at <paramref name="index"/> (at the end when it is Length).</summary>
    public void Insert(int index, ReadOnlySpan<char> text)
    {
        CheckInnermostToEdit();
        _builder.Insert(index, text);
    }

    /// <summary>Puts one char before the char at <paramref name="index"/>.</summary>
    public void Insert(int index, char value)
    {
        CheckInnermostToEdit();
        _builder.Insert(index, value);
    }

    /// <summary>Puts a number, in decimal, before the char at <paramref name="index"/>.</summary>
    public void Insert(int index, long value)
    {
        CheckInnermostToEdit();
        _builder.Insert(index, value);
    }

    /// <summary>Puts a number, in decimal, before the char at <paramref name="index"/>.</summary>
    public void Insert(int index, int value) => Insert(index, (long)value);

    /// <summary>Removes <paramref name="length"/> chars from <paramref name="startIndex"/>.</summary>
    public void Remove(int startIndex, int length) => _builder.Remove(startIndex, length);

    /// <summary>Replaces every <paramref name="oldChar"/> with <paramref name="newChar"/>.</summary>
    public void Replace(char oldChar, char newChar) => _builder.Replace(oldChar, newChar);

    /// <summary>Replaces <paramref name="oldChar"/> with <paramref name="newChar"/> in the <paramref name="count"/> chars from <paramref name="startIndex"/>.</summary>
    public void Replace(char oldChar, char newChar, int startIndex, int count)
        => _builder.Replace(oldChar, newChar, startIndex, count);

    /// <summary>Replaces every <paramref name="oldValue"/> with <paramref name="newValue"/>, left to right.</summary>
    public void Replace(ReadOnlySpan<char> oldValue, ReadOnlySpan<char> newValue)
    {
        CheckInnermostToEdit();
        _builder.Replace(oldValue, newValue);
    }

    /// <summary>Replaces the <paramref name="oldValue"/>s that lie wholly within the <paramref name="count"/> chars from <paramref name="startIndex"/>.</summary>
    public void Replace(ReadOnlySpan<char> oldValue, ReadOnlySpan<char> newValue, int startIndex, int count)
    {
        CheckInnermostToEdit();
        _builder.Replace(oldValue, newValue, startIndex, count);
    }

    /// <summary>Empties the text; the memory stays the text's.</summary>
    public void Clear() => _builder.Clear();

    /// <summary>
    /// Copies the text into a new string in <paramref name="target"/>, to keep
    /// after the callback returns. Returns null if the target is out of memory.
    /// </summary>
    public string ToArenaString(Arena target) => _builder.ToArenaString(target);

    // For StrBuilder.InterpolatedHandler: the builder to append to.
    internal StrBuilder BuilderToAppendTo()
    {
        CheckInnermost();
        return _builder;
    }

    void CheckInnermost() => ScratchThread.CheckInnermost(_thread, _depth,
        "Arena.Scratch: a ScratchText appended to inside a nested Arena.Scratch, which would free its memory early");

    // Insert and Replace may need a bigger buffer, so they follow Append's rule.
    void CheckInnermostToEdit() => ScratchThread.CheckInnermost(_thread, _depth,
        "Arena.Scratch: a ScratchText changed by Insert or Replace inside a nested Arena.Scratch, which would free its memory early");
}

public delegate void ScratchAction(scoped ScratchScope scratch);

public delegate void ScratchAction<TState>(scoped ScratchScope scratch, scoped ref TState state)
    where TState : allows ref struct;

public delegate TResult ScratchFunc<TResult>(scoped ScratchScope scratch);

public delegate TResult ScratchFunc<TState, TResult>(scoped ScratchScope scratch, scoped ref TState state)
    where TState : allows ref struct;

// The same with an input passed by value: the callback gets a copy, so nothing
// it puts in the copy reaches the caller. That makes a ref struct input (a span
// of the caller's, or a struct of several) safe in F# too, which doesn't know
// `scoped` (the SDK refuses a ref struct as the by-ref state there: ZERO006).
public delegate void ScratchActionIn<TInput>(scoped ScratchScope scratch, TInput input)
    where TInput : allows ref struct;

public delegate TResult ScratchFuncIn<TInput, TResult>(scoped ScratchScope scratch, TInput input)
    where TInput : allows ref struct;

public partial struct Arena
{
    // Each thread's ScratchThread, behind a pthread key, whose destructor frees
    // its arena when the thread ends. Not [ThreadStatic]: that has no hook for
    // the end of a thread, so each thread's arena would leak (Parallel.For
    // starts new threads every call), and it is a pthread key underneath
    // anyway, so it wouldn't be faster.
    static nuint s_scratchKey;
    static int s_scratchKeyState; // 0 none, 1 being created, 2 ready

    [DllImport("libc")] static safe extern int getpagesize();

    /// <summary>
    /// The size in bytes of a scratch arena's first block, which is the memory
    /// each thread, and each fiber, that calls Scratch keeps until it ends.
    /// 0, the default, means 16 KB, or 2 KB on a microcontroller or a
    /// Commodore 64. A callback that needs more gets it from malloc and gives
    /// it back when it returns, on every call: set this to what your
    /// callbacks need at once and that stops. Set it lower where many fibers
    /// need a little scratch memory each. The arena keeps a few bytes of the
    /// block for itself. The setting applies to the scratch arenas made
    /// afterwards, and a thread or fiber makes its own on its first Scratch
    /// call, so set it first thing in Main.
    /// </summary>
    public static nuint ScratchSize;

    /// <summary>
    /// The most bytes a scratch arena may hold, its first block included: a
    /// callback that needs more ends the program with a message. 0, the
    /// default, means no limit. Set to the same as ScratchSize, each scratch
    /// arena is one block that never calls malloc again. It applies to the
    /// scratch arenas made afterwards, as ScratchSize does.
    /// </summary>
    public static nuint ScratchMaxCapacity;

    // A scratch arena's first block, unless ScratchSize says otherwise.
    // Scratch frees with Restore, so a scope that goes past the first block
    // mallocs and frees a block each time: 16 KB keeps that rare where memory
    // is plentiful. On a microcontroller or a C64 (whose runtimes report
    // pages of 1 KB and 256 bytes, as Fibers relies on too) 16 KB is most of
    // the heap, and every fiber has a scratch arena of its own, so the first
    // block is 2 KB and grows from there.
    static nuint ScratchBlockSize
        => ScratchSize != 0 ? ScratchSize : getpagesize() <= 1024 ? (nuint)(2 * 1024) : 16 * 1024;

    /// <summary>
    /// Runs <paramref name="action"/> with a scope to allocate temporary memory
    /// from, and frees it all when the action returns. The compiler stops
    /// anything allocated from the scope from escaping the action.
    /// </summary>
    public static void Scratch(ScratchAction action)
    {
        ScratchThread* thread = EnterScratch(out Mark mark);
        action(new ScratchScope(thread));
        LeaveScratch(thread, mark);
    }

    /// <summary>The same, with a state the action can read and change, passed by ref.</summary>
    public static void Scratch<TState>(ref TState state, ScratchAction<TState> action)
        where TState : allows ref struct
    {
        ScratchThread* thread = EnterScratch(out Mark mark);
        action(new ScratchScope(thread), ref state);
        LeaveScratch(thread, mark);
    }

    /// <summary>The same, returning the action's result, which can't come from the scope's memory.</summary>
    public static TResult Scratch<TResult>(ScratchFunc<TResult> func)
    {
        ScratchThread* thread = EnterScratch(out Mark mark);
        TResult result = func(new ScratchScope(thread));
        LeaveScratch(thread, mark);
        return result;
    }

    /// <summary>The same, with a state and a result.</summary>
    public static TResult Scratch<TState, TResult>(ref TState state, ScratchFunc<TState, TResult> func)
        where TState : allows ref struct
    {
        ScratchThread* thread = EnterScratch(out Mark mark);
        TResult result = func(new ScratchScope(thread), ref state);
        LeaveScratch(thread, mark);
        return result;
    }

    /// <summary>
    /// The same, with an input the action gets a copy of: for a span of yours,
    /// or a ref struct of several, that the action reads or fills. What it
    /// writes into a span's items the caller sees; a change to the copy itself
    /// it doesn't.
    /// </summary>
    public static void Scratch<TInput>(TInput input, ScratchActionIn<TInput> action)
        where TInput : allows ref struct
    {
        ScratchThread* thread = EnterScratch(out Mark mark);
        action(new ScratchScope(thread), input);
        LeaveScratch(thread, mark);
    }

    /// <summary>The same, returning the action's result.</summary>
    public static TResult Scratch<TInput, TResult>(TInput input, ScratchFuncIn<TInput, TResult> func)
        where TInput : allows ref struct
    {
        ScratchThread* thread = EnterScratch(out Mark mark);
        TResult result = func(new ScratchScope(thread), input);
        LeaveScratch(thread, mark);
        return result;
    }

    static ScratchThread* EnterScratch(out Mark mark)
    {
        if (Volatile.Read(ref s_scratchKeyState) != 2)
            CreateScratchKey();
        unsafe
        {
            // SAFETY: the key holds null or the ScratchThread of an arena this
            // thread created below and hasn't destroyed.
            ScratchThread* thread = (ScratchThread*)Platform.pthread_getspecific(s_scratchKey);
            if (thread == null)
            {
                // The arena stops the program rather than return null, here
                // too if ScratchMaxCapacity has no room for the ScratchThread.
                Arena arena = Create(ScratchBlockSize, "scratch", stopOnOutOfMemory: true,
                    maxCapacity: ScratchMaxCapacity);
                thread = arena.Alloc<ScratchThread>();
                thread->Arena = arena;
                // For Catch.Run: see ScratchForCatch.
                ScratchForCatch.s_instance = ArenaScratchForCatch.Instance;
                if (Platform.pthread_setspecific(s_scratchKey, thread) != 0)
                    Environment.FailFast("Arena.Scratch: can't keep the thread's scratch arena");
            }
            mark = thread->Arena.Save();
            thread->Depth++;
            return thread;
        }
    }

    static void CreateScratchKey()
    {
        if (Interlocked.CompareExchange(ref s_scratchKeyState, 1, 0) == 0)
        {

            nuint key = 0;
            unsafe
            {
                // SAFETY: DestroyScratch has the signature a pthread key destructor needs.
                if (Platform.pthread_key_create(&key, (void*)(delegate* unmanaged<void*, void>)&DestroyScratch) != 0)
                    Environment.FailFast("Arena.Scratch: no thread-local key");
            }
            s_scratchKey = key;
            Volatile.Write(ref s_scratchKeyState, 2);
        }
        while (Volatile.Read(ref s_scratchKeyState) != 2)
        {
            // another thread is creating it
        }
    }

    // Fibers (Fibers.cs) each have a scratch arena of their own: a fiber that
    // waits inside Scratch would otherwise share the thread's, and whichever
    // scope closed first would free the other's memory. The scheduler swaps
    // the thread's scratch state on every switch: this puts thread (null:
    // none yet, made on first use) in place and returns what was there.
    internal static void* SwapScratch(void* thread)
    {
        if (Volatile.Read(ref s_scratchKeyState) != 2)
        {
            if (thread == null)
                return null; // nobody has used Scratch yet: nothing to swap
            CreateScratchKey();
        }
        unsafe
        {
            // SAFETY: the key only ever holds null or a live ScratchThread.
            void* previous = Platform.pthread_getspecific(s_scratchKey);
            if (previous != thread && Platform.pthread_setspecific(s_scratchKey, thread) != 0)
                Environment.FailFast("Arena.Scratch: can't keep the thread's scratch arena");
            return previous;
        }
    }

    // A fiber's scratch arena, once the fiber has ended.
    internal static void FreeScratch(void* thread)
    {
        if (thread != null)
            DestroyArenaOf(thread);
    }

    // Called by the system when a thread with a scratch arena ends.
    [UnmanagedCallersOnly]
    static void DestroyScratch(void* thread)
    {
        DestroyArenaOf(thread);
    }

    // The ScratchThread lives in its own arena's first block, and Destroy
    // clears the Arena it's called on after freeing the blocks: called on
    // thread->Arena, that write lands in freed memory (Windows' heap noticed
    // and stopped Fibers.exe; glibc's and macOS's let it pass). So it's
    // called on a copy.
    static void DestroyArenaOf(void* thread)
    {
        // SAFETY: the fiber or thread has ended; nothing of it uses its
        // scratch memory, the ScratchThread included.
        unsafe
        {
            Arena arena = ((ScratchThread*)thread)->Arena;
            arena.Destroy();
        }
    }

    // Catch.Run (Catch.cs) notes where the thread's Scratch calls are when it
    // starts, and after a throw jumps past some of them, frees what they
    // allocated, as their return would have. null and 0 when there's no
    // scratch arena yet. It calls the two through ScratchForCatch, at the end
    // of this file.
    internal static void* ScratchBeforeCatch(out Mark mark, out int depth)
    {
        mark = default;
        depth = 0;
        if (Volatile.Read(ref s_scratchKeyState) != 2)
            return null;
        unsafe
        {
            // SAFETY: the key holds null or this thread's live ScratchThread.
            ScratchThread* thread = (ScratchThread*)Platform.pthread_getspecific(s_scratchKey);
            if (thread != null)
            {
                mark = thread->Arena.Save();
                depth = thread->Depth;
            }
            return thread;
        }
    }

    internal static void ScratchAfterCatch(void* before, Mark mark, int depth)
    {
        if (Volatile.Read(ref s_scratchKeyState) != 2)
            return;
        unsafe
        {
            // SAFETY: as above. The callbacks the throw jumped out of are
            // gone, and with them everything that could point into what
            // this frees (the compiler kept it inside them).
            ScratchThread* thread = (ScratchThread*)Platform.pthread_getspecific(s_scratchKey);
            if (thread == null || thread->Depth <= depth)
                return;
            if (thread == before)
            {
                thread->Arena.Restore(mark);
                thread->Depth = depth;
                return;
            }
            // Made inside the Catch.Run, with no mark from before: the next
            // Scratch call makes a new one.
            if (Platform.pthread_setspecific(s_scratchKey, null) != 0)
                Environment.FailFast("Arena.Scratch: can't keep the thread's scratch arena");
            DestroyArenaOf(thread);
        }
    }

    /// <summary>
    /// Arena.Scratch in your memory: the scope, and every Scratch call nested
    /// inside the action, allocate from <paramref name="buffer"/> instead of
    /// the thread's scratch arena, for this call only. Nothing comes from
    /// malloc, and an allocation that doesn't fit in what is left of the
    /// buffer ends the program (the arena keeps about 150 bytes of the buffer
    /// for itself on 64-bit targets, 100 on 32-bit ones).
    /// </summary>
    /// <safety>
    /// Nothing may read or write <paramref name="buffer"/> until the action
    /// returns: not the caller, and not the action through a state or input
    /// that holds the same memory. The arena's bookkeeping lives in it. The
    /// compiler still keeps what the scope gives out inside the action.
    /// </safety>
    public static unsafe void Scratch(Span<byte> buffer, ScratchAction action)
    {
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        action(new ScratchScope(thread));
        LeaveBuffer(thread, previous);
    }

    /// <summary>The same, with a state the action can read and change, passed by ref.</summary>
    /// <safety>As for Scratch(Span&lt;byte&gt;, ScratchAction).</safety>
    public static unsafe void Scratch<TState>(Span<byte> buffer, ref TState state, ScratchAction<TState> action)
        where TState : allows ref struct
    {
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        action(new ScratchScope(thread), ref state);
        LeaveBuffer(thread, previous);
    }

    /// <summary>The same, returning the action's result, which can't come from the scope's memory.</summary>
    /// <safety>As for Scratch(Span&lt;byte&gt;, ScratchAction).</safety>
    public static unsafe TResult Scratch<TResult>(Span<byte> buffer, ScratchFunc<TResult> func)
    {
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        TResult result = func(new ScratchScope(thread));
        LeaveBuffer(thread, previous);
        return result;
    }

    /// <summary>The same, with a state and a result.</summary>
    /// <safety>As for Scratch(Span&lt;byte&gt;, ScratchAction).</safety>
    public static unsafe TResult Scratch<TState, TResult>(Span<byte> buffer, ref TState state, ScratchFunc<TState, TResult> func)
        where TState : allows ref struct
    {
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        TResult result = func(new ScratchScope(thread), ref state);
        LeaveBuffer(thread, previous);
        return result;
    }

    /// <summary>The same, with an input the action gets a copy of.</summary>
    /// <safety>As for Scratch(Span&lt;byte&gt;, ScratchAction).</safety>
    public static unsafe void Scratch<TInput>(Span<byte> buffer, TInput input, ScratchActionIn<TInput> action)
        where TInput : allows ref struct
    {
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        action(new ScratchScope(thread), input);
        LeaveBuffer(thread, previous);
    }

    /// <summary>The same, returning the action's result.</summary>
    /// <safety>As for Scratch(Span&lt;byte&gt;, ScratchAction).</safety>
    public static unsafe TResult Scratch<TInput, TResult>(Span<byte> buffer, TInput input, ScratchFuncIn<TInput, TResult> func)
        where TInput : allows ref struct
    {
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        TResult result = func(new ScratchScope(thread), input);
        LeaveBuffer(thread, previous);
        return result;
    }

    /// <summary>
    /// Arena.Scratch in <paramref name="bytes"/> of stack: the scope, and
    /// every Scratch call nested inside the action, allocate from memory on
    /// this call's own stack frame instead of the thread's scratch arena.
    /// Nothing comes from malloc, and an allocation that doesn't fit ends the
    /// program (the arena keeps about 150 bytes for itself on 64-bit
    /// targets, 100 on 32-bit ones). Unlike Scratch with a buffer of yours,
    /// it is safe: nothing but the scope ever sees the memory.
    /// The bytes come out of the thread's or fiber's stack, so keep them well
    /// under its size: a fiber's stack is 256 KB by default, but 32 KB on
    /// DOS, 4 KB on a microcontroller and 2 KB on a C64. Too many overflow
    /// the stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ScratchOnStack(int bytes, ScratchAction action)
    {
        Span<byte> buffer = stackalloc byte[StackBytes(bytes)];
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        action(new ScratchScope(thread));
        LeaveBuffer(thread, previous);
    }

    /// <summary>The same, with a state the action can read and change, passed by ref.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ScratchOnStack<TState>(int bytes, ref TState state, ScratchAction<TState> action)
        where TState : allows ref struct
    {
        Span<byte> buffer = stackalloc byte[StackBytes(bytes)];
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        action(new ScratchScope(thread), ref state);
        LeaveBuffer(thread, previous);
    }

    /// <summary>The same, returning the action's result, which can't come from the scope's memory.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static TResult ScratchOnStack<TResult>(int bytes, ScratchFunc<TResult> func)
    {
        Span<byte> buffer = stackalloc byte[StackBytes(bytes)];
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        TResult result = func(new ScratchScope(thread));
        LeaveBuffer(thread, previous);
        return result;
    }

    /// <summary>The same, with a state and a result.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static TResult ScratchOnStack<TState, TResult>(int bytes, ref TState state, ScratchFunc<TState, TResult> func)
        where TState : allows ref struct
    {
        Span<byte> buffer = stackalloc byte[StackBytes(bytes)];
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        TResult result = func(new ScratchScope(thread), ref state);
        LeaveBuffer(thread, previous);
        return result;
    }

    /// <summary>The same, with an input the action gets a copy of.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ScratchOnStack<TInput>(int bytes, TInput input, ScratchActionIn<TInput> action)
        where TInput : allows ref struct
    {
        Span<byte> buffer = stackalloc byte[StackBytes(bytes)];
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        action(new ScratchScope(thread), input);
        LeaveBuffer(thread, previous);
    }

    /// <summary>The same, returning the action's result.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static TResult ScratchOnStack<TInput, TResult>(int bytes, TInput input, ScratchFuncIn<TInput, TResult> func)
        where TInput : allows ref struct
    {
        Span<byte> buffer = stackalloc byte[StackBytes(bytes)];
        ScratchThread* thread = EnterBuffer(buffer, out void* previous);
        TResult result = func(new ScratchScope(thread), input);
        LeaveBuffer(thread, previous);
        return result;
    }

    // ScratchOnStack's size, checked: a negative one would be a stackalloc
    // error, and one too small for the arena's bookkeeping stops the program
    // in FromBuffer. The overloads aren't inlined, so each stackalloc is in
    // a frame of its own, freed when the call returns, even in a loop.
    static int StackBytes(int bytes)
    {
        if (bytes <= 0)
            Environment.FailFast("Arena.ScratchOnStack: the size must be positive");
        return bytes;
    }

    // An arena in the buffer, with its ScratchThread as its first allocation,
    // made the thread's scratch state for the call: nested Scratch calls find
    // it as EnterScratch finds the thread's own. A fiber switch saves and
    // puts back whichever is in place (SwapScratch), so a fiber can wait
    // inside the call. previous is what to put back when it returns. Not
    // inlined: on a C64 it is 3 KB, which each overload used would copy.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static ScratchThread* EnterBuffer(Span<byte> buffer, out void* previous)
    {
        unsafe
        {
            // SAFETY: the caller promised the buffer to this call alone, and
            // the arena is used only until LeaveBuffer, before the call returns.
            Arena arena = FromBuffer(buffer, "scratch buffer", stopOnOutOfMemory: true);
            ScratchThread* thread = arena.Alloc<ScratchThread>();
            thread->Arena = arena;
            thread->Depth = 1;
            previous = SwapScratch(thread);
            return thread;
        }
    }

    // Puts the thread's own scratch state back. The buffer's arena needs no
    // Restore or Destroy: nothing of it is used again, and it owns no memory.
    static void LeaveBuffer(ScratchThread* thread, void* previous)
    {
        unsafe
        {
            // SAFETY: thread lives in the buffer, which the call still holds;
            // previous was the thread's scratch state, null or still alive.
            thread->Depth = 0;
            SwapScratch(previous);
        }
    }

    static void LeaveScratch(ScratchThread* thread, Mark mark)
    {
        // SAFETY: only the scope and its lists and text allocate from the
        // scratch arena, each only while its callback is the innermost one;
        // the compiler kept everything they gave out inside the callback,
        // which has returned. Nested Scratch calls restore in reverse order.
        unsafe
        {
            thread->Depth--;
            thread->Arena.Restore(mark);
        }
    }

    // ---- For F#'s printf (fsharp/printf/Printf.fs) ----

    // Each thread's format buffer (OpenFormatScope): objects are bumped in
    // [Start, End); Outer is where they went before the scope, for a
    // ToString of the program's and for what doesn't fit. Made on a thread's
    // first printf, from malloc, and kept: a thread that ends leaves its
    // buffer behind, as it would its own heap objects.
    internal struct FormatBuffer
    {
        internal byte* Start;
        internal byte* Ptr;
        internal byte* End;
        internal AllocatorSetting Outer;
        internal int Depth;
    }

    [ThreadStatic] static FormatBuffer* t_formatBuffer;

    // 4 KB holds what a line of printf makes; 1 KB on a microcontroller or a
    // Commodore 64 (page sizes of 1 KB and 256 bytes, as ScratchBlockSize).
    static nuint FormatBufferSize => getpagesize() <= 1024 ? (nuint)1024 : 4096;

    // ObjectAllocator's function inside a format scope: zeroed and 8-byte
    // aligned, as every object. What doesn't fit goes where objects went
    // before: it outlives the scope, as it would have without one.
    static unsafe void* AllocateFormatObject(void* context, nuint size)
    {
        unsafe
        {
            // SAFETY: OpenFormatScope set this thread's live buffer as the
            // context; Ptr stays within [Start, End).
            FormatBuffer* buffer = (FormatBuffer*)context;
            nuint rounded = (size + 7) & ~(nuint)7;
            if ((nuint)(buffer->End - buffer->Ptr) >= rounded)
            {
                byte* at = buffer->Ptr;
                buffer->Ptr = at + rounded;
                System.Runtime.InteropServices.NativeMemory.Clear(at, size);
                return at;
            }
            return buffer->Outer.Allocate(buffer->Outer.Context, size);
        }
    }

    /// <summary>
    /// For F#'s printf: until the returned scope is disposed, the objects the
    /// program creates go in a small buffer of this thread's, and disposing
    /// the scope takes them all back. printf boxes its values, formats and
    /// writes inside one, so a printfn leaves nothing behind but what its call
    /// site made. Text that must outlive the scope (sprintf's) leaves it
    /// through CopyOut. Code of the program's that printf calls, a value's
    /// ToString, runs outside it, through ToStringOutsideFormatScope, so
    /// whatever that code keeps stays. What doesn't fit in the buffer goes
    /// where objects went before, and stays.
    /// </summary>
    /// <safety>
    /// Nothing created inside may outlive the scope: not in a static field,
    /// not in an object from outside. Only printf's own code runs inside.
    /// </safety>
    public static unsafe FormatScope OpenFormatScope()
    {
        FormatScope scope = default;
        unsafe
        {
            // SAFETY: the buffer is this thread's alone; scopes open and close
            // in reverse order, each putting back what it found.
            FormatBuffer* buffer = t_formatBuffer;
            if (buffer == null)
            {
                nuint size = FormatBufferSize;
                buffer = (FormatBuffer*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)sizeof(FormatBuffer) + size);
                if (buffer == null)
                    Environment.FailFast("Arena.OpenFormatScope: out of memory");
                buffer->Start = (byte*)(buffer + 1);
                buffer->Ptr = buffer->Start;
                buffer->End = buffer->Start + size;
                buffer->Depth = 0;
                t_formatBuffer = buffer;
            }
            // The outermost scope starts from an empty buffer. (After a throw
            // that jumped out of a scope, Depth stays above 0: objects then
            // pile up, and go where they went before once the buffer is full.)
            if (buffer->Depth == 0)
                buffer->Ptr = buffer->Start;
            buffer->Depth++;
            scope.Buffer = buffer;
            scope.Mark = buffer->Ptr;
            scope.Outer = buffer->Outer;
            scope.Before = CurrentAllocator();
            // Opened inside another (not from a ToString, which runs outside):
            // what doesn't fit still goes where it went before them both.
            delegate*<void*, nuint, void*> format = &AllocateFormatObject;
            if (scope.Before.Allocate != format)
                buffer->Outer = scope.Before;
            System.Runtime.ObjectAllocator.Set(format, buffer);
        }
        scope.Open = true;
        return scope;
    }

    /// <summary>
    /// value.ToString(), with the objects it creates allocated where they
    /// went before the innermost format scope: a ToString is the program's
    /// code, and may keep what it makes (a cache in a field, a static).
    /// Outside a format scope, the same as value.ToString().
    /// </summary>
    public static string ToStringOutsideFormatScope(object value)
    {
        if (value == null)
            return null;
        AllocatorSetting inside = CurrentAllocator();
        unsafe
        {
            delegate*<void*, nuint, void*> format = &AllocateFormatObject;
            if (inside.Allocate != format)
                return value.ToString();
            // SAFETY: inside a format scope, the context is this thread's buffer.
            SetAllocator(((FormatBuffer*)inside.Context)->Outer);
        }
        string text = value.ToString();
        SetAllocator(inside);
        return text;
    }

    /// <summary>From OpenFormatScope, for a using block (F#'s use).</summary>
    public ref struct FormatScope : System.IDisposable
    {
        internal FormatBuffer* Buffer;
        internal byte* Mark;
        internal AllocatorSetting Outer;
        internal AllocatorSetting Before;
        internal bool Open;

        /// <summary>
        /// A copy of <paramref name="text"/> made where objects went before
        /// the scope, so it outlives it. Null stays null.
        /// </summary>
        public readonly string CopyOut(string text)
        {
            if (!Open)
                Environment.FailFast("Arena.FormatScope.CopyOut: the scope is closed");
            return CopyStringWith(Before, text);
        }

        /// <summary>
        /// Puts back where objects went before, and takes back everything made
        /// inside. A second Dispose does nothing.
        /// </summary>
        public void Dispose()
        {
            if (!Open)
                return;
            Open = false;
            SetAllocator(Before);
            unsafe
            {
                // SAFETY: the buffer is this thread's, and this the innermost
                // scope: nothing made after Mark is used again.
                Buffer->Ptr = Mark;
                Buffer->Outer = Outer;
                Buffer->Depth--;
            }
        }
    }
}

// What Catch.Run (Catch.cs) asks of Arena.Scratch, as virtual methods of an
// object that exists once a thread has called Scratch. Virtual on purpose:
// the compiler leaves a virtual method out of a program unless something
// creates its class and something calls it, so Arena.ScratchBeforeCatch and
// ScratchAfterCatch (5 KB on a C64) are only in programs that use both
// Catch.Run and Arena.Scratch. A direct call from Catch.Run would bring them
// into every program that catches, and a pointer to them set here, into
// every program that uses Scratch.
internal abstract class ScratchForCatch
{
    // Null until a thread calls Arena.Scratch.
    internal static ScratchForCatch s_instance;

    internal abstract unsafe void* Before(out Arena.Mark mark, out int depth);
    internal abstract unsafe void After(void* before, Arena.Mark mark, int depth);
}

internal sealed class ArenaScratchForCatch : ScratchForCatch
{
    // Made by the static constructor, so with calloc, whatever arena objects
    // are going to at the time.
    internal static readonly ArenaScratchForCatch Instance = new ArenaScratchForCatch();

    internal override unsafe void* Before(out Arena.Mark mark, out int depth)
    {
        unsafe { return Arena.ScratchBeforeCatch(out mark, out depth); } // SAFETY: this thread's own scratch state
    }

    internal override unsafe void After(void* before, Arena.Mark mark, int depth)
    {
        unsafe { Arena.ScratchAfterCatch(before, mark, depth); } // SAFETY: before, mark and depth are Before's
    }
}
