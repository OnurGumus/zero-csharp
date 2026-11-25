// HashSet<T>: a set of unmanaged values, backed by an Arena.
//
//   var seen = HashSet<int>.Create(arena);
//   if (!seen.Add(id)) Console.WriteLine($"duplicate: {id}");
//
// A Dict without values underneath, so it behaves like one: a handle whose
// copies share contents, default(HashSet) is "not created", and items are
// compared as .NET's HashSet compares them (EqualityComparer<T>.Default).
// HashSet<T, TComparer> takes a comparer of your own, as
// Dict<TKey, TValue, TComparer> does; ByteEqualityComparer<T> compares raw bytes.

using System;
using System.Collections.Generic;

public struct HashSet<T, TComparer>
    where T : unmanaged
    where TComparer : unmanaged, IEqualityComparer<T>
{
    Dict<T, byte, TComparer> _map;

    /// <summary>Returns a new set in <paramref name="arena"/>, or a not-created set if it is out of memory.</summary>
    public static HashSet<T, TComparer> Create(Arena arena, int capacity = 32, TComparer comparer = default)
        => new HashSet<T, TComparer> { _map = Dict<T, byte, TComparer>.Create(arena, capacity, comparer) };

    public bool IsCreated => _map.IsCreated;

    public int Count => _map.Count;

    /// <summary>
    /// Adds the item. Returns true if it was added; false if it was already in
    /// the set, or the set isn't created or the arena is full (Count tells which).
    /// </summary>
    public bool Add(T item) => _map.TryAdd(item, 0);

    public bool Contains(T item) => _map.ContainsKey(item);

    /// <summary>Removes the item. Returns false if it wasn't in the set.</summary>
    public bool Remove(T item) => _map.Remove(item);

    public void Clear() => _map.Clear();

    /// <summary>Adds every item of <paramref name="other"/>. Returns false if the arena filled up first.</summary>
    public bool UnionWith(HashSet<T, TComparer> other)
    {
        foreach (T item in other)
        {
            if (!_map.TryAdd(item, 0) && !_map.ContainsKey(item))
                return false;
        }
        return true;
    }

    /// <summary>Keeps only the items that are also in <paramref name="other"/>.</summary>
    public void IntersectWith(HashSet<T, TComparer> other)
    {
        unsafe { _map.RemoveWhere(other, &IsMissingFrom); }
    }

    /// <summary>Removes every item that is in <paramref name="other"/>.</summary>
    public void ExceptWith(HashSet<T, TComparer> other)
    {
        // Removing while looping over the same table would skip items.
        if (_map.SameAs(other._map))
        {
            Clear();
            return;
        }
        foreach (T item in other)
            _map.Remove(item);
    }

    static bool IsMissingFrom(HashSet<T, TComparer> other, T item) => !other.Contains(item);

    /// <summary>For foreach: every item, in no particular order. Don't Add or Remove during the loop.</summary>
    public Enumerator GetEnumerator() => new Enumerator(_map.GetEnumerator());

    public struct Enumerator
    {
        Dict<T, byte, TComparer>.Enumerator _inner;

        internal Enumerator(Dict<T, byte, TComparer>.Enumerator inner) => _inner = inner;

        public bool MoveNext() => _inner.MoveNext();

        public T Current => _inner.Current.Key;
    }

    /// <summary>
    /// Returns an independent copy in <paramref name="target"/>, or a not-created
    /// set if this one isn't created or the target is out of memory.
    /// </summary>
    public HashSet<T, TComparer> Clone(Arena target) => new HashSet<T, TComparer> { _map = _map.Clone(target) };
}

// HashSet<T>: a HashSet whose items are compared by EqualityComparer<T>.Default.
public struct HashSet<T> where T : unmanaged
{
    HashSet<T, DefaultEqualityComparer<T>> _set;

    /// <summary>Returns a new set in <paramref name="arena"/>, or a not-created set if it is out of memory.</summary>
    public static HashSet<T> Create(Arena arena, int capacity = 32)
        => new HashSet<T> { _set = HashSet<T, DefaultEqualityComparer<T>>.Create(arena, capacity) };

    public bool IsCreated => _set.IsCreated;

    public int Count => _set.Count;

    /// <summary>
    /// Adds the item. Returns true if it was added; false if it was already in
    /// the set, or the set isn't created or the arena is full (Count tells which).
    /// </summary>
    public bool Add(T item) => _set.Add(item);

    public bool Contains(T item) => _set.Contains(item);

    /// <summary>Removes the item. Returns false if it wasn't in the set.</summary>
    public bool Remove(T item) => _set.Remove(item);

    public void Clear() => _set.Clear();

    /// <summary>Adds every item of <paramref name="other"/>. Returns false if the arena filled up first.</summary>
    public bool UnionWith(HashSet<T> other) => _set.UnionWith(other._set);

    /// <summary>Keeps only the items that are also in <paramref name="other"/>.</summary>
    public void IntersectWith(HashSet<T> other) => _set.IntersectWith(other._set);

    /// <summary>Removes every item that is in <paramref name="other"/>.</summary>
    public void ExceptWith(HashSet<T> other) => _set.ExceptWith(other._set);

    /// <summary>For foreach: every item, in no particular order. Don't Add or Remove during the loop.</summary>
    public HashSet<T, DefaultEqualityComparer<T>>.Enumerator GetEnumerator() => _set.GetEnumerator();

    /// <summary>
    /// Returns an independent copy in <paramref name="target"/>, or a not-created
    /// set if this one isn't created or the target is out of memory.
    /// </summary>
    public HashSet<T> Clone(Arena target) => new HashSet<T> { _set = _set.Clone(target) };
}
