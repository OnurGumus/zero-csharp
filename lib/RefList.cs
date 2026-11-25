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
using System.Runtime.CompilerServices;

public struct RefList<T> where T : class
{
    List<nint> _items;

    /// <summary>Returns a new list in <paramref name="arena"/>, or a not-created list if it is out of memory.</summary>
    public static RefList<T> Create(Arena arena, int initialCapacity = 16)
        => new RefList<T> { _items = List<nint>.Create(arena, initialCapacity) };

    public bool IsCreated => _items.IsCreated;

    public int Count => _items.Count;

    public int Capacity => _items.Capacity;

    /// <summary>Appends an item (null is allowed). Returns false if the list isn't created or the arena is full.</summary>
    public bool Add(T item) => _items.Add(ToAddress(item));

    public T Get(int index) => FromAddress(_items.Get(index));

    public void Set(int index, T value) => _items.Set(index, ToAddress(value));

    public bool RemoveAt(int index) => _items.RemoveAt(index);

    public void Clear() => _items.Clear();

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
    public RefList<T> Clone(Arena target) => new RefList<T> { _items = _items.Clone(target) };

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

    static nint ToAddress(T item) => Unsafe.As<T, nint>(ref item);

    static T FromAddress(nint address) => Unsafe.As<nint, T>(ref address);
}
