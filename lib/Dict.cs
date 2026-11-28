// Dict<TKey, TValue>: open-addressing hash map backed by an Arena.
//
// Like List<T>, Dict is a handle to an arena header, so copies share contents,
// and default(Dict) is "not created" (lookups miss, Set fails).
//
// Keys are compared as .NET's Dictionary compares them, by
// EqualityComparer<TKey>.Default: a type's own Equals(T) if it implements
// IEquatable<T> (every primitive does, so 0.0 and -0.0 are one key), an
// enum's value, and a struct's bytes otherwise, all without boxing.
//
// Dict<TKey, TValue, TComparer> takes a comparer of your own: an unmanaged
// struct that implements IEqualityComparer<TKey>, kept in the map's header, so
// it may carry state. ByteEqualityComparer<TKey> compares raw bytes (FNV-1a),
// which is what Dict<TKey, TValue> did before:
//
//   var byLastDigit = Dict<int, int, Modulo>.Create(arena, comparer: new Modulo { By = 10 });

using System;
using System.Collections.Generic;

public struct Dict<TKey, TValue, TComparer>
    where TKey : unmanaged
    where TValue : unmanaged
    where TComparer : unmanaged, IEqualityComparer<TKey>
{
    const int MinCapacity = 8;

    struct Entry
    {
        public TKey Key;
        public TValue Value;
        public uint Hash;
        public bool Used;
    }

    struct Header
    {
        public Arena Arena; // where the map lives and grows
        public Entry* Entries;
        public int Capacity; // always a power of two
        public int Count;
        public TComparer Comparer;
    }

    Header* _header;

#if ZERO_DOTNET
    // The arena the map lives in (RefDict's GC handles last as long as it holds them).
    internal Arena HomeArena { get { unsafe { return _header == null ? default : _header->Arena; } } } // SAFETY: a created map's header
    internal int SlotCapacity { get { unsafe { return _header == null ? 0 : _header->Capacity; } } } // SAFETY: as above
#endif

    /// <summary>Returns a new map in <paramref name="arena"/>, or a not-created map if it is out of memory.</summary>
    public static Dict<TKey, TValue, TComparer> Create(Arena arena, int capacity = 32, TComparer comparer = default)
    {
        int rounded = MinCapacity;
        while (rounded < capacity && rounded <= int.MaxValue / 2)
            rounded *= 2;

        Header* header = arena.Alloc<Header>();
        Entry* entries = arena.AllocArray<Entry>(rounded);
        if (header == null || entries == null)
            return default;

        unsafe
        {
            // SAFETY: header was just allocated and zeroed.
            header->Arena = arena;
            header->Entries = entries;
            header->Capacity = rounded;
            header->Comparer = comparer;
        }
        return new Dict<TKey, TValue, TComparer> { _header = header };
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

    /// <summary>Inserts or overwrites. Returns false if the map isn't created or the arena is full.</summary>
    public bool Set(TKey key, TValue value)
    {
        if (_header == null)
            return false;
        DebugCheck();

        unsafe
        {
            // SAFETY: _header points at a live header (see List<T>.Add).
            // Keep the load factor at or below 3/4.
            if ((_header->Count + 1) * 4 > _header->Capacity * 3 && !Grow())
                return false;

            Insert(_header, key, value, Hash(ref _header->Comparer, key), overwrite: true);
        }
        return true;
    }

    /// <summary>
    /// Adds the key if it isn't there yet. Returns true if it was added; false
    /// if it was already there (its value unchanged), or the map isn't created
    /// or the arena is full.
    /// </summary>
    public bool TryAdd(TKey key, TValue value)
    {
        if (_header == null)
            return false;
        DebugCheck();

        unsafe
        {
            // SAFETY: as in Set.
            uint hash = Hash(ref _header->Comparer, key);
            if (Find(_header, key, hash) != null)
                return false;
            if ((_header->Count + 1) * 4 > _header->Capacity * 3 && !Grow())
                return false;
            Insert(_header, key, value, hash, overwrite: false);
        }
        return true;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        if (_header != null)
        {
            DebugCheck();
            unsafe
            {
                // SAFETY: a live header.
                Entry* entry = Find(_header, key, Hash(ref _header->Comparer, key));
                if (entry != null)
                {
                    value = entry->Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public bool ContainsKey(TKey key) => TryGet(key, out _);

    /// <summary>Removes a key. Returns false if it wasn't there.</summary>
    public bool Remove(TKey key)
    {
        if (_header == null)
            return false;
        DebugCheck();

        unsafe
        {
            // SAFETY: a live header; every index is masked into [0, Capacity).
            uint hash = Hash(ref _header->Comparer, key);
            Entry* entries = _header->Entries;
            int mask = _header->Capacity - 1;
            int hole = (int)hash & mask;
            while (true)
            {
                if (!entries[hole].Used)
                    return false;
                if (entries[hole].Hash == hash && _header->Comparer.Equals(entries[hole].Key, key))
                    break;
                hole = (hole + 1) & mask;
            }

            // A lookup walks from an entry's home slot until it finds the entry
            // or an empty slot, so just emptying this slot would hide the entries
            // that were pushed past it. Close the gap instead: move back each
            // following entry whose home slot is at or before the hole.
            int next = hole;
            while (true)
            {
                next = (next + 1) & mask;
                if (!entries[next].Used)
                    break;
                int home = (int)entries[next].Hash & mask;
                // Leave it if its home lies after the hole, up to where it is now
                // (the range wraps around the end of the table).
                bool homeAfterHole = hole <= next ? (hole < home && home <= next) : (hole < home || home <= next);
                if (homeAfterHole)
                    continue;
                entries[hole] = entries[next];
                hole = next;
            }

            entries[hole] = default;
            _header->Count--;
        }
        return true;
    }

    /// <summary>Removes every key. The table keeps its capacity.</summary>
    public void Clear()
    {
        if (_header == null)
            return;
        DebugCheck();
        unsafe
        {
            // SAFETY: a live header with Capacity entries.
            for (int i = 0; i < _header->Capacity; i++)
                _header->Entries[i] = default;
            _header->Count = 0;
        }
    }

    // Whether both handles share one table.
    internal bool SameAs(Dict<TKey, TValue, TComparer> other) => _header == other._header;

    // Removes each key that shouldRemove(state, key) says to, and returns how
    // many. Removing shifts later entries back into the hole, so the slot is
    // looked at again before moving on. An entry from the start of the table
    // can wrap around into a slot not yet reached and be looked at twice, so
    // shouldRemove must give the same answer for the same key.
    internal int RemoveWhere<TState>(TState state, delegate*<TState, TKey, bool> shouldRemove)
    {
        if (_header == null)
            return 0;
        DebugCheck();
        int removed = 0;
        unsafe
        {
            // SAFETY: a live header; i stays below Capacity. shouldRemove is
            // the caller's function, called with keys of the map.
            int i = 0;
            while (i < _header->Capacity)
            {
                Entry* entry = &_header->Entries[i];
                if (entry->Used && shouldRemove(state, entry->Key))
                {
                    Remove(entry->Key);
                    removed++;
                }
                else
                {
                    i++;
                }
            }
        }
        return removed;
    }

    /// <summary>A key with its value, as foreach yields them.</summary>
    public struct Pair
    {
        public TKey Key;
        public TValue Value;
    }

    /// <summary>
    /// For foreach: every key and value, in no particular order.
    /// <code>foreach (var pair in scores) total += pair.Value;</code>
    /// </summary>
    /// <remarks>Don't Set or Remove during the loop: entries move, so some may be skipped or seen twice.</remarks>
    public Enumerator GetEnumerator()
    {
        if (_header != null)
            DebugCheck();
        return new Enumerator(this);
    }

    public struct Enumerator
    {
        readonly Dict<TKey, TValue, TComparer> _dict;
        int _index;

        internal Enumerator(Dict<TKey, TValue, TComparer> dict)
        {
            _dict = dict;
            _index = -1;
        }

        public bool MoveNext()
        {
            Header* header = _dict._header;
            if (header == null)
                return false;
            unsafe
            {
                // SAFETY: a live header; _index stays below Capacity.
                while (++_index < header->Capacity)
                {
                    if (header->Entries[_index].Used)
                        return true;
                }
            }
            return false;
        }

        public Pair Current
        {
            get
            {
                unsafe
                {
                    // SAFETY: MoveNext returned true, so _index names a used entry.
                    Entry* entry = &_dict._header->Entries[_index];
                    return new Pair { Key = entry->Key, Value = entry->Value };
                }
            }
        }
    }

    // Under Arena.DebugChecks, stops the program if the map's memory was freed.
    void DebugCheck()
    {
        if (Arena.DebugChecks)
            unsafe { Arena.CheckLive(_header->Arena, _header->Entries, (nuint)_header->Capacity * (nuint)sizeof(Entry)); } // SAFETY: callers checked _header != null.
    }

    /// <summary>
    /// Returns an independent copy in <paramref name="target"/> (this map's own
    /// arena or another one). Returns a not-created map if this one isn't created
    /// or the target is out of memory.
    /// </summary>
    /// <remarks>
    /// Keys and values are copied as they are. One that is, or contains, a pointer
    /// still points wherever it did: into the original's arena, if that's where
    /// it led.
    /// </remarks>
    public Dict<TKey, TValue, TComparer> Clone(Arena target)
    {
        if (_header == null)
            return default;
        DebugCheck();

        unsafe
        {
            // SAFETY: _header is a live header with Capacity entries; the copy's
            // table has the same capacity, so every entry keeps its slot.
            int capacity = _header->Capacity;
            Header* header = target.Alloc<Header>();
            Entry* entries = target.AllocArray<Entry>(capacity, Arena.DefaultAlign, zero: false);
            if (header == null || entries == null)
                return default;

            Entry* source = _header->Entries;
            for (int i = 0; i < capacity; i++)
                entries[i] = source[i];

            header->Arena = target;
            header->Entries = entries;
            header->Capacity = capacity;
            header->Count = _header->Count;
            header->Comparer = _header->Comparer;
            return new Dict<TKey, TValue, TComparer> { _header = header };
        }
    }

    // The entry holding key, or null.
    static unsafe Entry* Find(Header* header, TKey key, uint hash)
    {
        unsafe
        {
            int mask = header->Capacity - 1;
            int index = (int)hash & mask;
            // The load factor guarantees at least one empty slot, so this terminates.
            while (header->Entries[index].Used)
            {
                Entry* entry = &header->Entries[index];
                if (entry->Hash == hash && header->Comparer.Equals(entry->Key, key))
                    return entry;
                index = (index + 1) & mask;
            }
            return null;
        }
    }

    // overwrite: false when the caller knows the key isn't there (growing,
    // TryAdd), which saves comparing it with the keys on its way.
    static unsafe void Insert(Header* header, TKey key, TValue value, uint hash, bool overwrite)
    {
        unsafe
        {
            int mask = header->Capacity - 1;
            int index = (int)hash & mask;
            while (header->Entries[index].Used)
            {
                Entry* entry = &header->Entries[index];
                if (overwrite && entry->Hash == hash && header->Comparer.Equals(entry->Key, key))
                {
                    entry->Value = value;
                    return;
                }
                index = (index + 1) & mask;
            }

            Entry* slot = &header->Entries[index];
            slot->Key = key;
            slot->Value = value;
            slot->Hash = hash;
            slot->Used = true;
            header->Count++;
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
            Entry* newEntries = _header->Arena.AllocArray<Entry>(newCapacity);
            if (newEntries == null)
                return false;

            Entry* oldEntries = _header->Entries;
            _header->Entries = newEntries;
            _header->Capacity = newCapacity;
            _header->Count = 0;

            for (int i = 0; i < oldCapacity; i++)
            {
                if (oldEntries[i].Used)
                    Insert(_header, oldEntries[i].Key, oldEntries[i].Value, oldEntries[i].Hash, overwrite: false);
            }
        }
        return true;
    }

    // The comparer's hash code, mixed so that its low bits, which pick the slot,
    // depend on all of its bits: int's hash code is the int itself, and keys
    // that are all multiples of 64 would otherwise share a slot.
    static uint Hash(ref TComparer comparer, TKey key)
    {
        uint hash = (uint)comparer.GetHashCode(key);
        hash ^= hash >> 16;
        hash *= 0x7FEB352D;
        hash ^= hash >> 15;
        hash *= 0x846CA68B;
        hash ^= hash >> 16;
        return hash;
    }
}

// Dict<TKey, TValue>: a Dict whose keys are compared by EqualityComparer<TKey>.Default.
public struct Dict<TKey, TValue>
    where TKey : unmanaged
    where TValue : unmanaged
{
    Dict<TKey, TValue, DefaultEqualityComparer<TKey>> _map;

    /// <summary>Returns a new map in <paramref name="arena"/>, or a not-created map if it is out of memory.</summary>
    public static Dict<TKey, TValue> Create(Arena arena, int capacity = 32)
        => new Dict<TKey, TValue> { _map = Dict<TKey, TValue, DefaultEqualityComparer<TKey>>.Create(arena, capacity) };

    public bool IsCreated => _map.IsCreated;

    public int Count => _map.Count;

    /// <summary>Inserts or overwrites. Returns false if the map isn't created or the arena is full.</summary>
    public bool Set(TKey key, TValue value) => _map.Set(key, value);

    /// <summary>
    /// Adds the key if it isn't there yet. Returns true if it was added; false
    /// if it was already there (its value unchanged), or the map isn't created
    /// or the arena is full.
    /// </summary>
    public bool TryAdd(TKey key, TValue value) => _map.TryAdd(key, value);

    public bool TryGet(TKey key, out TValue value) => _map.TryGet(key, out value);

    public bool ContainsKey(TKey key) => _map.ContainsKey(key);

    /// <summary>Removes a key. Returns false if it wasn't there.</summary>
    public bool Remove(TKey key) => _map.Remove(key);

    /// <summary>Removes every key. The table keeps its capacity.</summary>
    public void Clear() => _map.Clear();

    /// <summary>A key with its value, as foreach yields them.</summary>
    public struct Pair
    {
        public TKey Key;
        public TValue Value;
    }

    /// <summary>
    /// For foreach: every key and value, in no particular order.
    /// <code>foreach (var pair in scores) total += pair.Value;</code>
    /// </summary>
    /// <remarks>Don't Set or Remove during the loop: entries move, so some may be skipped or seen twice.</remarks>
    public Enumerator GetEnumerator() => new Enumerator(_map.GetEnumerator());

    public struct Enumerator
    {
        Dict<TKey, TValue, DefaultEqualityComparer<TKey>>.Enumerator _inner;

        internal Enumerator(Dict<TKey, TValue, DefaultEqualityComparer<TKey>>.Enumerator inner) => _inner = inner;

        public bool MoveNext() => _inner.MoveNext();

        public Pair Current
        {
            get
            {
                var pair = _inner.Current;
                return new Pair { Key = pair.Key, Value = pair.Value };
            }
        }
    }

    /// <summary>
    /// Returns an independent copy in <paramref name="target"/> (this map's own
    /// arena or another one). Returns a not-created map if this one isn't created
    /// or the target is out of memory.
    /// </summary>
    /// <remarks>
    /// Keys and values are copied as they are. One that is, or contains, a pointer
    /// still points wherever it did: into the original's arena, if that's where
    /// it led.
    /// </remarks>
    public Dict<TKey, TValue> Clone(Arena target) => new Dict<TKey, TValue> { _map = _map.Clone(target) };

#if ZERO_DOTNET
    internal Arena HomeArena => _map.HomeArena;
    internal int SlotCapacity => _map.SlotCapacity;
#endif
}

// Compares keys by their raw bytes, and hashes them with FNV-1a. Exact for
// integers and for structs without padding; not value equality for floats
// (0.0 and -0.0 differ) or for structs whose padding bytes may differ.
public struct ByteEqualityComparer<T> : IEqualityComparer<T> where T : unmanaged
{
    public bool Equals(T x, T y)
    {
        byte* pa = (byte*)&x;
        byte* pb = (byte*)&y;
        unsafe
        {
            // SAFETY: both point at local copies, sizeof(T) bytes long.
            for (int i = 0; i < sizeof(T); i++)
            {
                if (pa[i] != pb[i])
                    return false;
            }
        }
        return true;
    }

    public int GetHashCode(T value)
    {
        byte* p = (byte*)&value;
        uint hash = 2166136261;
        unsafe
        {
            // SAFETY: p points at the local copy of value, sizeof(T) bytes long.
            for (int i = 0; i < sizeof(T); i++)
                hash = (hash ^ p[i]) * 16777619;
        }
        return (int)hash;
    }
}

// Compares keys by EqualityComparer<T>.Default, as .NET's Dictionary and
// HashSet do: what Dict<TKey, TValue> and HashSet<T> use.
public struct DefaultEqualityComparer<T> : IEqualityComparer<T>
{
    public bool Equals(T x, T y) => EqualityComparer<T>.Default.Equals(x, y);

    public int GetHashCode(T value) => EqualityComparer<T>.Default.GetHashCode(value);
}
