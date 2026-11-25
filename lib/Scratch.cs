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
// scratch arena for its whole life, sized to the most it ever needed at once.
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
                Arena arena = Create(16 * 1024, "scratch", stopOnOutOfMemory: true);
                thread = arena.Alloc<ScratchThread>();
                thread->Arena = arena;
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
        // SAFETY: the fiber has ended; nothing uses its scratch memory.
        if (thread != null)
            unsafe { ((ScratchThread*)thread)->Arena.Destroy(); }
    }

    // Called by the system when a thread with a scratch arena ends.
    [UnmanagedCallersOnly]
    static void DestroyScratch(void* thread)
    {
        // SAFETY: the thread has ended; nothing of it uses its scratch memory,
        // the ScratchThread included (it lives in the arena).
        unsafe { ((ScratchThread*)thread)->Arena.Destroy(); }
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
}
