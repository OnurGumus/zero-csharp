// List<T>: growable array backed by an Arena.
//
// List<T> is a handle (one pointer) to a header that lives in the arena, so
// copies of a List share the same contents, like passing List* in C.
// A default(List<T>) is "not created": reads see an empty list and Add fails.
//
// Growing extends the buffer in place while it is still the arena's most
// recent allocation; otherwise it copies into a fresh buffer and the old one
// stays allocated until the arena is reset. Out-of-range Get/Set fail fast.

using System;

public struct List<T> where T : unmanaged
{
    const int MinCapacity = 4;

    struct Header
    {
        public Arena Arena; // where the list lives and grows
        public T* Data;
        public int Count;
        public int Capacity;
    }

    Header* _header;

#if ZERO_DOTNET
    // The arena the list lives in (RefList's GC handles last as long as it holds them).
    internal Arena HomeArena { get { unsafe { return _header == null ? default : _header->Arena; } } } // SAFETY: a created list's header
#endif

    /// <summary>Returns a new list in <paramref name="arena"/>, or a not-created list if it is out of memory.</summary>
    public static List<T> Create(Arena arena, int initialCapacity = 16)
    {
        if (initialCapacity < MinCapacity)
            initialCapacity = MinCapacity;

        Header* header = arena.Alloc<Header>();
        T* data = arena.AllocArray<T>(initialCapacity, Arena.DefaultAlign, zero: false);
        if (header == null || data == null)
            return default;

        unsafe
        {
            // SAFETY: header was just allocated and zeroed.
            header->Arena = arena;
            header->Data = data;
            header->Capacity = initialCapacity;
        }
        return new List<T> { _header = header };
    }

    public bool IsCreated => _header != null;

    public int Count
    {
        get
        {
            if (_header == null)
                return 0;
            unsafe { return _header->Count; }
        }
    }

    public int Capacity
    {
        get
        {
            if (_header == null)
                return 0;
            unsafe { return _header->Capacity; }
        }
    }

    /// <summary>Appends an item. Returns false if the list isn't created or the arena is full.</summary>
    public bool Add(T item)
    {
        if (_header == null)
            return false;
        DebugCheck();

        unsafe
        {
            // SAFETY: _header points at a live header (the arena hasn't been reset;
            // Arena.Reset() is unsafe and its caller promises that).
            if (_header->Count == _header->Capacity && !Grow())
                return false;
            _header->Data[_header->Count++] = item;
        }
        return true;
    }

    public T Get(int index)
    {
        unsafe
        {
            CheckIndex(index);
            return _header->Data[index]; // SAFETY: CheckIndex validated header and index.
        }
    }

    public void Set(int index, T value)
    {
        unsafe
        {
            CheckIndex(index);
            _header->Data[index] = value; // SAFETY: CheckIndex validated header and index.
        }
    }

    /// <summary>Pointer to an element. Invalidated by Add (growth) and RemoveAt.</summary>
    public T* GetPtr(int index)
    {
        unsafe
        {
            CheckIndex(index);
            return &_header->Data[index];
        }
    }

    public bool RemoveAt(int index)
    {
        if (_header == null)
            return false;
        DebugCheck();

        unsafe
        {
            int count = _header->Count;
            if ((uint)index >= (uint)count)
                return false;

            T* data = _header->Data;
            for (int i = index; i < count - 1; i++)
                data[i] = data[i + 1];
            _header->Count = count - 1;
        }
        return true;
    }

    public void Clear()
    {
        if (_header == null)
            return;
        unsafe { _header->Count = 0; }
    }

    /// <summary>View of the current items.</summary>
    /// <safety>The span is invalidated by Add (growth), RemoveAt, Clear and resetting the arena.</safety>
    public unsafe Span<T> AsSpan()
    {
        if (_header == null)
            return default;
        DebugCheck();
        unsafe { return new Span<T>(_header->Data, _header->Count); }
    }

    /// <summary>
    /// Sorts the items in place. <paramref name="compare"/> returns a negative
    /// number if its first argument goes before its second, a positive one if
    /// after, and zero if either order will do:
    /// <code>
    /// static int ByAge(Person a, Person b) => a.Age - b.Age;
    /// people.Sort(&amp;ByAge);
    /// </code>
    /// Heapsort: no extra memory, and n log n comparisons however the items start
    /// out. Not stable: items that compare equal may end up in either order.
    /// </summary>
    public void Sort(delegate*<T, T, int> compare)
    {
        if (compare == null)
            return;
        Sort(new System.Collections.Generic.FunctionPointerComparer<T>(compare));
    }

    /// <summary>Sorts the items by T's IComparable&lt;T&gt; (<c>Comparer&lt;T&gt;.Default</c>).</summary>
    public void Sort() => Sort(new System.Collections.Generic.DefaultComparer<T>());

    /// <summary>
    /// Sorts the items by <paramref name="comparer"/>. A struct is fastest: the
    /// sort is compiled for it, and its Compare is a direct call.
    /// </summary>
    public void Sort<TComparer>(TComparer comparer) where TComparer : System.Collections.Generic.IComparer<T>
    {
        if (_header == null)
            return;
        DebugCheck();
        unsafe
        {
            // SAFETY: a live header; comparer only sees items of the list, and
            // nothing changes the list during the sort.
            System.Collections.Generic.Sorting.HeapSort(new Span<T>(_header->Data, _header->Count), comparer);
        }
    }

    /// <summary>
    /// Returns an independent copy in <paramref name="target"/> (this list's own
    /// arena or another one), with just enough capacity for the current items.
    /// Returns a not-created list if this one isn't created or the target is out
    /// of memory.
    /// </summary>
    /// <remarks>
    /// Items are copied as they are. An item that is, or contains, a pointer still
    /// points wherever it did: into the original's arena, if that's where it led.
    /// </remarks>
    public List<T> Clone(Arena target)
    {
        if (_header == null)
            return default;
        DebugCheck();

        int count = Count;
        List<T> copy = Create(target, count);
        if (copy._header == null)
            return default;

        unsafe
        {
            // SAFETY: both are live headers; copy has capacity for at least count items.
            T* source = _header->Data;
            T* destination = copy._header->Data;
            for (int i = 0; i < count; i++)
                destination[i] = source[i];
            copy._header->Count = count;
        }
        return copy;
    }

    // Removes the first count items, moving the rest to the front (Queue<T>).
    internal void RemoveFirst(int count)
    {
        if (_header == null || count <= 0)
            return;
        DebugCheck();
        unsafe
        {
            // SAFETY: a live header; count is clamped to Count.
            int total = _header->Count;
            if (count > total)
                count = total;
            T* data = _header->Data;
            for (int i = count; i < total; i++)
                data[i - count] = data[i];
            _header->Count = total - count;
        }
    }

    unsafe bool Grow()
    {
        unsafe
        {
            int oldCapacity = _header->Capacity;
            if (oldCapacity > int.MaxValue / 2)
                return false;

            int newCapacity = oldCapacity * 2;
            Arena arena = _header->Arena;
            T* oldData = _header->Data;

            // Nothing was allocated after the buffer: just make it longer.
            if (arena.TryExtend(oldData, (nuint)oldCapacity * (nuint)sizeof(T), (nuint)newCapacity * (nuint)sizeof(T)))
            {
                _header->Capacity = newCapacity;
                return true;
            }

            T* newData = arena.AllocArray<T>(newCapacity, Arena.DefaultAlign, zero: false);
            if (newData == null)
                return false;

            for (int i = 0; i < _header->Count; i++)
                newData[i] = oldData[i];

            _header->Data = newData;
            _header->Capacity = newCapacity;
        }
        return true;
    }

    // Under Arena.DebugChecks, stops the program if the list's memory was freed.
    void DebugCheck()
    {
        if (Arena.DebugChecks)
            unsafe { Arena.CheckLive(_header->Arena, _header->Data, (nuint)_header->Capacity * (nuint)sizeof(T)); } // SAFETY: callers checked _header != null.
    }

    unsafe void CheckIndex(int index)
    {
        unsafe
        {
            if (_header != null)
                DebugCheck();
            if (_header == null || (uint)index >= (uint)_header->Count)
                Environment.FailFast("List: index out of range");
        }
    }
}
