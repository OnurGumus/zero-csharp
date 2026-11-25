// RefDict<TKey, TValue>: a Dict whose values are references (string, classes,
// arrays), in an Arena. Keys are unmanaged, as in Dict; for string keys, use
// StrDict (lib/StrDict.cs).
//
//   RefDict<int, string> names = RefDict<int, string>.Create(keep);
//   names.Set(42, Str.ToArenaString(keep, line));
//   if (names.TryGet(42, out string name)) Console.WriteLine(name);
//
// Each value is stored as its address in a Dict<TKey, nint>, as RefList does;
// see RefList for why that works. The map holds references, not the objects:
// each one must outlive the map's use of it, so copy a string out of a scratch
// arena before storing it.

using System;
using System.Runtime.CompilerServices;

public struct RefDict<TKey, TValue>
    where TKey : unmanaged
    where TValue : class
{
    Dict<TKey, nint> _map;

    /// <summary>Returns a new map in <paramref name="arena"/>, or a not-created map if it is out of memory.</summary>
    public static RefDict<TKey, TValue> Create(Arena arena, int capacity = 32)
        => new RefDict<TKey, TValue> { _map = Dict<TKey, nint>.Create(arena, capacity) };

    public bool IsCreated => _map.IsCreated;

    public int Count => _map.Count;

    /// <summary>Inserts or overwrites (null is allowed). Returns false if the map isn't created or the arena is full.</summary>
    public bool Set(TKey key, TValue value) => _map.Set(key, ToAddress(value));

    public bool TryGet(TKey key, out TValue value)
    {
        bool found = _map.TryGet(key, out nint address);
        value = FromAddress(address); // null when not found: address is 0
        return found;
    }

    public bool ContainsKey(TKey key) => _map.ContainsKey(key);

    /// <summary>Removes a key. Returns false if it wasn't there.</summary>
    public bool Remove(TKey key) => _map.Remove(key);

    /// <summary>
    /// Returns an independent copy of the map in <paramref name="target"/>, or a
    /// not-created map if this one isn't created or the target is out of memory.
    /// </summary>
    /// <remarks>Only the references are copied: both maps point at the same objects.</remarks>
    public RefDict<TKey, TValue> Clone(Arena target) => new RefDict<TKey, TValue> { _map = _map.Clone(target) };

    /// <summary>A key with its value, as foreach yields them.</summary>
    public struct Pair
    {
        public TKey Key;
        public TValue Value;
    }

    /// <summary>For foreach: every key and value, in no particular order.</summary>
    /// <remarks>Don't Set or Remove during the loop: entries move, so some may be skipped or seen twice.</remarks>
    public Enumerator GetEnumerator() => new Enumerator(_map.GetEnumerator());

    public struct Enumerator
    {
        Dict<TKey, nint>.Enumerator _inner;

        internal Enumerator(Dict<TKey, nint>.Enumerator inner) => _inner = inner;

        public bool MoveNext() => _inner.MoveNext();

        public Pair Current
        {
            get
            {
                Dict<TKey, nint>.Pair pair = _inner.Current;
                return new Pair { Key = pair.Key, Value = FromAddress(pair.Value) };
            }
        }
    }

    static nint ToAddress(TValue value) => Unsafe.As<TValue, nint>(ref value);

    static TValue FromAddress(nint address) => Unsafe.As<nint, TValue>(ref address);
}
