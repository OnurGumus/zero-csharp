// RefList<T>: a List<T> for references (string, classes, arrays), in an Arena.
//
//   RefList<string> names = RefList<string>.Create(keep);
//   names.Add(Str.ToArenaString(keep, line));   // the string itself must live on
//   foreach (string name in names) Console.WriteLine(name);
//
// List<T> takes only unmanaged types, because it stores items in arena memory
// through a T*. A reference is just an address in zero mode (there is no GC to
// move objects or to find them), so this stores each one as an nint in a
// List<nint> and converts it back when read. It has List's behavior: a handle
// whose copies share contents, safe to use when not created, growth in place.
//
// The list holds references, not the objects: the objects stay wherever they
// were allocated. Each one must outlive the list's use of it. A string built in
// a scratch arena dangles once that arena is reset, even if the list lives in
// another one, so copy it into a long-lived arena before adding it.

using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

public struct RefList<T> where T : class
{
    List<nint> _items;
#if ZERO_DOTNET
    SpareHandles* _spare;

    /// <summary>Returns a new list in <paramref name="arena"/>, or a not-created list if it is out of memory.</summary>
    public static RefList<T> Create(Arena arena, int initialCapacity = 16)
    {
        var list = new RefList<T> { _items = List<nint>.Create(arena, initialCapacity) };
        if (list._items.IsCreated)
            unsafe { list._spare = SpareHandles.Create(arena, list._items.Capacity); } // SAFETY: in the list's arena
        return list;
    }
#else

    /// <summary>Returns a new list in <paramref name="arena"/>, or a not-created list if it is out of memory.</summary>
    public static RefList<T> Create(Arena arena, int initialCapacity = 16)
        => new RefList<T> { _items = List<nint>.Create(arena, initialCapacity) };
#endif

    public bool IsCreated => _items.IsCreated;

    public int Count => _items.Count;

    public int Capacity => _items.Capacity;

    /// <summary>Appends an item (null is allowed). Returns false if the list isn't created or the arena is full.</summary>
#if ZERO_DOTNET
    // SAFETY (the unsafe blocks below): _spare is null or from SpareHandles.Create in the list's arena.
    public bool Add(T item)
    {
        if (!_items.IsCreated)
            return false;
        unsafe
        {
            nint handle = SpareHandles.Take(_spare, _items.HomeArena, item);
            if (!_items.Add(handle))
            {
                SpareHandles.Give(_spare, handle);
                return false;
            }
            SpareHandles.Grow(_spare, _items.HomeArena, _items.Capacity);
        }
        return true;
    }

    public T Get(int index) => FromAddress(_items.Get(index));

    public void Set(int index, T value)
    {
        if ((uint)index >= (uint)Count)
        {
            _items.Set(index, 0); // as List.Set: out of range ends the program
            return;
        }
        unsafe { _items.Set(index, SpareHandles.Replace(_spare, _items.HomeArena, _items.Get(index), value)); }
    }

    public bool RemoveAt(int index)
    {
        if ((uint)index < (uint)Count)
            unsafe { SpareHandles.Give(_spare, _items.Get(index)); }
        return _items.RemoveAt(index);
    }

    public void Clear()
    {
        for (int i = 0; i < Count; i++)
            unsafe { SpareHandles.Give(_spare, _items.Get(i)); }
        _items.Clear();
    }
#else
    public bool Add(T item) => _items.Add(ToAddress(item));

    public T Get(int index) => FromAddress(_items.Get(index));

    public void Set(int index, T value) => _items.Set(index, ToAddress(value));

    public bool RemoveAt(int index) => _items.RemoveAt(index);

    public void Clear() => _items.Clear();
#endif

    /// <summary>
    /// Sorts the items in place, like List.Sort: <paramref name="compare"/> returns
    /// a negative number if its first argument goes before its second, a positive
    /// one if after, and zero if either order will do. Heapsort, not stable.
    /// <code>
    /// static int ByName(string a, string b) => Str.Compare(a, b);
    /// names.Sort(&amp;ByName);
    /// </code>
    /// </summary>
    public void Sort(delegate*<T, T, int> compare)
    {
        if (compare == null)
            return;
        Sort(new System.Collections.Generic.FunctionPointerComparer<T>(compare));
    }

    /// <summary>Sorts the items by T's IComparable&lt;T&gt; (<c>Comparer&lt;T&gt;.Default</c>).</summary>
    public void Sort() => Sort(new System.Collections.Generic.DefaultComparer<T>());

    /// <summary>Sorts the items by <paramref name="comparer"/>. A struct is fastest.</summary>
    public void Sort<TComparer>(TComparer comparer) where TComparer : System.Collections.Generic.IComparer<T>
    {
        Span<nint> items;
        unsafe { items = _items.AsSpan(); } // SAFETY: nothing changes the list during the sort.
        System.Collections.Generic.Sorting.HeapSort(items, new AddressComparer<TComparer>(comparer));
    }

    // Compares two stored addresses by the objects they point at.
    struct AddressComparer<TComparer> : System.Collections.Generic.IComparer<nint>
        where TComparer : System.Collections.Generic.IComparer<T>
    {
        TComparer _comparer;

        public AddressComparer(TComparer comparer) => _comparer = comparer;

        public int Compare(nint x, nint y) => _comparer.Compare(FromAddress(x), FromAddress(y));
    }

    /// <summary>
    /// Returns an independent copy of the list in <paramref name="target"/>, or a
    /// not-created list if this one isn't created or the target is out of memory.
    /// </summary>
    /// <remarks>
    /// Only the references are copied: both lists point at the same objects, so
    /// the copy outlives the original's arena but not the objects' arenas.
    /// </remarks>
#if ZERO_DOTNET
    // Handles of the target's own: it may outlive this list's arena.
    public RefList<T> Clone(Arena target)
    {
        var copy = new RefList<T> { _items = _items.Clone(target) };
        if (!copy._items.IsCreated)
            return copy;
        unsafe { copy._spare = SpareHandles.Create(target, copy._items.Capacity); } // SAFETY: in the copy's arena
        for (int i = 0; i < copy.Count; i++)
            copy._items.Set(i, target.KeepObject(FromAddress(copy._items.Get(i))));
        return copy;
    }
#else
    public RefList<T> Clone(Arena target) => new RefList<T> { _items = _items.Clone(target) };
#endif

    /// <summary>For foreach: every item, in order.</summary>
    /// <remarks>Don't Add or RemoveAt during the loop.</remarks>
    public Enumerator GetEnumerator() => new Enumerator(this);

    public struct Enumerator
    {
        readonly RefList<T> _list;
        int _index;

        internal Enumerator(RefList<T> list)
        {
            _list = list;
            _index = -1;
        }

        public bool MoveNext() => ++_index < _list.Count;

        public T Current => _list.Get(_index);
    }

#if ZERO_DOTNET
    // Real .NET's GC moves objects and frees what nothing it can see refers
    // to, so an address won't do: each item is a GC handle from the list's
    // arena (Arena.KeepObject), freed with the arena's memory. An overwrite
    // points the slot's handle at the new object; a removed item's object is
    // let go at once, and its handle kept for the next item (SpareHandles).
    static T FromAddress(nint address)
    {
        if (address == 0)
            return default;
        unsafe { return (T)GCHandle.FromIntPtr(address).Target; } // SAFETY: every nonzero address came from ToAddress
    }
#else
    static nint ToAddress(T item) => Unsafe.As<T, nint>(ref item);

    static T FromAddress(nint address) => Unsafe.As<nint, T>(ref address);
#endif
}

#if ZERO_DOTNET
// On .NET, a RefList's or RefDict's GC handles (Arena.KeepObject), and those
// of its removed items, kept for reuse: in the collection's own arena memory,
// sized to its capacity, so a removal never allocates and a collection never
// holds more handles than the most items it has held at once. (Reuse stays
// inside one collection: a handle taken before an arena mark and reused after
// it could otherwise be freed by Restore while older memory still uses it.)
struct SpareHandles
{
    nint* _handles;
    int _count, _capacity;

    // SAFETY (all members): spare is null or from Create, in the collection's arena.
    internal static SpareHandles* Create(Arena arena, int capacity)
    {
        SpareHandles* spare = arena.Alloc<SpareHandles>();
        if (spare == null)
            return null;
        unsafe
        {
            spare->_handles = arena.AllocArray<nint>(capacity);
            spare->_capacity = spare->_handles == null ? 0 : capacity;
        }
        return spare;
    }

    /// <summary>A handle for value: a spare one if there is one, else a new one from the arena. 0 for null.</summary>
    internal static nint Take(SpareHandles* spare, Arena arena, object value)
    {
        if (value is null)
            return 0;
        unsafe
        {
            if (spare != null && spare->_count > 0)
            {
                nint handle = spare->_handles[--spare->_count];
                GCHandle kept = GCHandle.FromIntPtr(handle); // SAFETY: from KeepObject, freed only with the arena
                kept.Target = value;
                return handle;
            }
        }
        return arena.KeepObject(value);
    }

    /// <summary>A removed item's handle: lets go of its object and keeps the handle for the next Take.</summary>
    internal static void Give(SpareHandles* spare, nint handle)
    {
        if (handle == 0)
            return;
        Arena.LetGo(handle);
        unsafe
        {
            // There's room: the collection's handles and the spare ones are never more than its capacity.
            if (spare != null && spare->_count < spare->_capacity)
                spare->_handles[spare->_count++] = handle;
        }
    }

    /// <summary>Points the slot's own handle at value (an overwrite), or gives or takes one.</summary>
    internal static nint Replace(SpareHandles* spare, Arena arena, nint old, object value)
    {
        if (old != 0 && value is not null)
        {
            GCHandle kept;
            unsafe { kept = GCHandle.FromIntPtr(old); } // SAFETY: from KeepObject, freed only with the arena
            kept.Target = value;
            return old;
        }
        Give(spare, old);
        return Take(spare, arena, value);
    }

    /// <summary>After the collection grew: room for as many spare handles as its capacity.</summary>
    internal static void Grow(SpareHandles* spare, Arena arena, int capacity)
    {
        unsafe
        {
            if (spare == null || capacity <= spare->_capacity)
                return;
            nint* handles = arena.AllocArray<nint>(capacity);
            if (handles == null)
                return;
            for (int i = 0; i < spare->_count; i++)
                handles[i] = spare->_handles[i];
            spare->_handles = handles;
            spare->_capacity = capacity;
        }
    }
}
#endif
