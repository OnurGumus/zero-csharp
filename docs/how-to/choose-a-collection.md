---
title: Choose a collection
index: 3
---

# Choose a collection

zerocore has no collections of its own. `lib/`'s are all built on an arena: most hold unmanaged
values, and three hold references. Which one you need depends on what you store and how you find it.

## Pick by what you store and how you look it up

| You store | You find it by | Use | Notes |
|---|---|---|---|
| numbers, structs (unmanaged) | position | `List<T>` | `Add`, `Get`, `Set`, `RemoveAt`, `Sort`, `AsSpan` |
| strings, objects, arrays | position | `RefList<T>` | same API as `List`, plus `foreach` |
| unmanaged values | an unmanaged key | `Dict<TKey, TValue>` | keys compared as .NET's `Dictionary` does |
| unmanaged values | whether it's there | `HashSet<T>` | `Add`, `Contains`, `UnionWith`, `IntersectWith`, `ExceptWith` |
| unmanaged values | first in, first out | `Queue<T>` | `Enqueue`, `TryDequeue`, `TryPeek` |
| unmanaged values | last in, first out | `Stack<T>` | `Push`, `TryPop`, `TryPeek` |
| unmanaged values | smallest priority | `PriorityQueue<TElement, TPriority>` | a binary heap, as .NET's |
| any value | a string's text | `StrDict<TValue>` | keys copied into the map's arena |
| strings, objects, arrays | an unmanaged key | `RefDict<TKey, TValue>` | values stored as references |
| structs that point at each other | following a link | `Ptr<T>` from `arena.New` | linked lists, trees, graphs; see below |
| structs removed one by one | a handle | `Pool<T>` and `Handle<T>` | reuses removed slots; refuses out-of-date handles |
| unmanaged values in order | a node's handle | `LinkedList<T>` | `AddFirst`, `AddLast`, `AddAfter`, `AddBefore`, `Remove`, `Find` |

`List<T>` and `Dict` take only unmanaged types because they keep items in arena memory through a `T*`.
A reference in zero mode is just an address (no garbage collector moves or tracks objects), so
`RefList` and `RefDict` store each one as an `nint` in a `List` or `Dict` underneath.

## Create it in an arena

Every collection is created in the arena you pass, and grows there:

<!-- sample: samples/Collections.cs dict -->
```csharp
var scores = Dict<int, int>.Create(arena);
scores.Set(1, 100);
scores.Set(2, 250);
scores.Set(3, 175);
scores.Set(-42, 999);
```

The same rules hold for all five:

- **A handle.** A collection is one pointer to a header in its arena. `var b = a` shares the contents,
  and so does passing it to a method: the callee's `Add`s are visible to the caller.
- **`default` is "not created".** Reads see an empty collection, lookups miss, and `Add` or `Set`
  return false. `IsCreated` tells you which you have.
- **Out of memory returns false.** `Add` and `Set` return false when the arena is full, unless the
  arena was created with `stopOnOutOfMemory: true`.
- **Out of range stops the program.** `Get` and `Set` on a `List` or `RefList` with a bad index end it
  with `List: index out of range`. `RemoveAt` returns false instead.
- **Freed with the arena.** `Reset`, `Restore` or `Destroy` on the arena frees the collection.

## Link items to each other

A struct can't contain itself, so a linked list, a tree or a graph needs pointers between its items.
`arena.New(value)` copies a struct into the arena and returns a `Ptr<T>` to it, and a `Ptr` can be a
field of the struct it points at:

<!-- sample: samples/Links.cs node -->
```csharp
// One item of a doubly linked list: a struct can't contain itself, but it
// can hold pointers to others of its kind.
public struct Node
{
    public int Number;
    public Ptr<Node> Prev;
    public Ptr<Node> Next;
}
```

`ptr.Value` is a `ref` to the item, to read or change in place, and no `unsafe` is needed. A
doubly linked list is then the usual code, cycles included:

<!-- sample: samples/Links.cs chain -->
```csharp
// The list itself: its two ends, and the arena its items live in.
public struct Chain
{
    public Arena Arena;
    public Ptr<Node> First;
    public Ptr<Node> Last;

    public void AddLast(int number)
    {
        Ptr<Node> node = Arena.New(new Node { Number = number, Prev = Last });
        if (Last.IsNull)
            First = node;
        else
            Last.Value.Next = node;
        Last = node;
    }

    // Takes the node out by joining its neighbours to each other.
    public void Remove(Ptr<Node> node)
    {
        ref Node n = ref node.Value;
        if (n.Prev.IsNull)
            First = n.Next;
        else
            n.Prev.Value.Next = n.Next;
        if (n.Next.IsNull)
            Last = n.Prev;
        else
            n.Next.Value.Prev = n.Prev;
        n.Prev = default;
        n.Next = default;
    }
}
```

Recursion works as it does with classes:

<!-- sample: samples/Links.cs insert -->
```csharp
// Recursion works as it would with classes.
static Ptr<TreeNode> Insert(Arena arena, Ptr<TreeNode> node, int key)
{
    if (node.IsNull)
        return arena.New(new TreeNode { Key = key });
    ref TreeNode n = ref node.Value;
    if (key < n.Key)
        n.Left = Insert(arena, n.Left, key);
    else
        n.Right = Insert(arena, n.Right, key);
    return node;
}
```

- **`default` is null.** `IsNull` tells you; `Value` on a null `Ptr` stops the program with
  `Ptr: Value of a null Ptr` instead of crashing it.
- **`==` compares the pointers,** so a walk round a ring can tell when it is back where it started.
- **Items live as long as the arena.** There is no freeing one item: removing a node from a list
  unlinks it, and its memory goes with the arena's.
- **The arena's rules apply.** Like a collection, a `Ptr` is valid until its arena is reset,
  destroyed or restored to before the item was made. Under `Arena.DebugChecks`, a `Ptr` used after
  that stops the program. A `ref` from `Value` kept in a local isn't checked, so take `Value` again
  after anything that frees memory.

## Remove items one by one

A `Ptr`'s item lives as long as its arena. When items come and go, a graph whose nodes are deleted or
jobs that finish, use a `Pool<T>`: `Add` returns a `Handle<T>`, `pool[handle]` is a `ref` to the item,
and `Remove` frees the slot for the next `Add`. Items can hold handles to each other, as with `Ptr`:

<!-- sample: samples/Links.cs task -->
```csharp
// A task that waits for another: items in a pool point at each other with
// handles, which are plain structs too.
public struct Job
{
    public int Id;
    public Handle<Job> WaitsFor;
}
```

A handle remembers the slot's generation, which `Remove` changes, so a handle kept after its item was
removed finds nothing, even once the slot holds a new item. `pool[handle]` then stops the program
with `Pool: the handle's item was removed or moved`; `Contains` and `TryGet` ask without stopping:

<!-- sample: samples/Links.cs pool -->
```csharp
var jobs = Pool<Job>.Create(arena);
Handle<Job> build = jobs.Add(new Job { Id = 1 });
Handle<Job> test = jobs.Add(new Job { Id = 2, WaitsFor = build });
Handle<Job> ship = jobs.Add(new Job { Id = 3, WaitsFor = test });
jobs.Remove(test);
// ship still holds the handle, but it no longer finds anything,
// even once the slot is used again for another job.
Handle<Job> docs = jobs.Add(new Job { Id = 4 });
bool waiting = jobs.Contains(jobs[ship].WaitsFor);   // false
```

`Move` hands an item to a new owner: the item stays where it is, but it gets a new handle and every
copy of the old one is refused from then on. `MoveTo` does the same into another pool:

<!-- sample: samples/Links.cs move -->
```csharp
Handle<Job> mine = jobs.Add(new Job { Id = 7 });
Handle<Job> copy = mine;
Handle<Job> theirs = jobs.Move(ref mine);
// mine is now null, and every copy of it is refused: only the new
// owner reaches the job.
```

- **A handle belongs to its pool.** Each pool has an id of its own, and a handle used with another
  pool is refused with `Pool: the handle is from another pool`.
- **Items don't move.** The pool grows by chunks and never copies items, so a `ref` from
  `pool[handle]` stays good while it grows. After a `Remove`, though, the next `Add` may reuse the
  slot: take the `ref` again rather than keep one across a `Remove`.
- **A handle is 12 bytes:** the pool's id, the slot and its generation.

`LinkedList<T>` is built on a pool, with .NET's names. Each `Add` returns the node's handle, so
`AddAfter`, `AddBefore` and `Remove` take O(1), and a removed node's handle is refused rather than
unlinking whatever took its slot. `list[node]` is the node: its `Value`, `Previous` and `Next`.

<!-- sample: samples/Links.cs linked-list -->
```csharp
var list = LinkedList<int>.Create(arena);
var two = list.AddLast(2);
list.AddFirst(1);
var four = list.AddAfter(two, 4);
list.AddBefore(four, 3);
list.AddLast(5);
list.Remove(two);
foreach (ref int item in list)
    item *= 10;                         // 10, 30, 40, 50
```

### A graph whose nodes come and go

Handles can link items in several pools, and an item can have as many links as it needs: here a
town holds a handle to its first road, and each road one to the next, so the roads form a list per
town:

<!-- sample: samples/Links.cs map -->
```csharp
// A map: towns joined by roads, in two pools. A town holds a handle to the
// first of its roads and each road one to the next, so a town has as many
// roads as it needs, and every link is a plain struct.
public struct Town
{
    public int Id;
    public Handle<Road> FirstRoad;
}

public struct Road
{
    public Handle<Town> To;
    public int Km;
    public Handle<Road> Next;
}
```

`Map` keeps the two pools and adds a road both ways. Removing a town removes it and its own roads,
and leaves the roads that lead to it from other towns:

<!-- sample: samples/Links.cs map-remove -->
```csharp
// The town and its own roads go. Roads to it from other towns stay,
// but their handle no longer finds it.
public void RemoveTown(Handle<Town> town)
{
    Handle<Road> road = Towns[town].FirstRoad;
    while (!road.IsNull)
    {
        Handle<Road> next = Roads[road].Next;
        Roads.Remove(road);
        road = next;
    }
    Towns.Remove(town);
}
```

Those roads are safe to keep: their `To` belongs to a removed town, so `Contains` says false, even
after the slot holds a new town, where a pointer to the slot would read the new town as the old.
A search skips them, and keeps its own tables in scratch memory, keyed by handle:

<!-- sample: samples/Links.cs distance -->
```csharp
// The shortest way by road, in km, or -1 if there is none. The search's
// own tables are scratch memory, gone when it returns.
static int Distance(Map map, Handle<Town> from, Handle<Town> to)
{
    var trip = new Trip { Map = map, From = from, To = to };
    return Arena.Scratch(ref trip,
        static (scoped ScratchScope scratch, scoped ref Trip trip) =>
        {
            var best = scratch.Dict<Handle<Town>, int>();
            var done = scratch.HashSet<Handle<Town>>();
            best.Set(trip.From, 0);
            while (true)
            {
                // The nearest town not done yet (a scan, to keep it short).
                Handle<Town> here = default;
                int km = int.MaxValue;
                foreach (var entry in best)
                    if (!done.Contains(entry.Key) && entry.Value < km)
                    {
                        here = entry.Key;
                        km = entry.Value;
                    }
                if (here.IsNull)
                    return -1;
                if (here == trip.To)
                    return km;
                done.Add(here);

                Handle<Road> road = trip.Map.Towns[here].FirstRoad;
                for (; !road.IsNull; road = trip.Map.Roads[road].Next)
                {
                    Road r = trip.Map.Roads[road];
                    if (!trip.Map.Towns.Contains(r.To))
                        continue;           // a road to a removed town
                    int via = km + r.Km;
                    if (!best.TryGet(r.To, out int known) || via < known)
                        best.Set(r.To, via);
                }
            }
        });
}
```

<!-- sample: samples/Links.cs map-use -->
```csharp
Map map = Map.Create(arena);
Handle<Town> harbor = map.AddTown(1), mill = map.AddTown(2);
Handle<Town> ford = map.AddTown(3), hill = map.AddTown(4);
Handle<Town> lake = map.AddTown(5);
map.Join(harbor, mill, 230);
map.Join(mill, ford, 150);
map.Join(ford, lake, 150);
map.Join(mill, lake, 330);
map.Join(harbor, hill, 260);
int before = Distance(map, harbor, lake);   // 530, through ford
map.RemoveTown(ford);
int after = Distance(map, harbor, lake);    // 560, mill to lake
```

The roads left behind still take their slots in the pool. Where towns come and go often, remove
them too: when a town is removed, follow each of its roads to the town at the other end and take out
the road back, or now and then sweep for roads whose `To` is gone.

## Key by an id, not by an object

`Dict` and `RefDict` keys must be unmanaged, and zerocore's `object` has no `Equals` or `GetHashCode`,
so an object can't be a key. Give the class an `int Id` and key by that: `RefDict<int, T>` to find
the object from its id, `Dict<int, TValue>` for data you keep about it.

<!-- sample: samples/Collections.cs refdict -->
```csharp
var byId = RefDict<int, string>.Create(arena);
byId.Set(7, "seven");
byId.Set(3, Str.Format(arena, $"three is {3}"));
byId.Set(9, null);
```

Hand out each id once and never reuse it. A map entry left behind for an object that is gone would
otherwise answer for the new object with the same id.

## Compare keys your way

`Dict` and `HashSet` compare keys as .NET's `Dictionary` and `HashSet` do, with
`EqualityComparer<T>.Default`: a type's own `Equals(T)` if it implements `IEquatable<T>` (every
primitive does, so `0.0` and `-0.0` are one key), an enum's value, and otherwise a struct's bytes.
None of it boxes. A struct that overrides `Equals(object)` without implementing `IEquatable<T>` is
still compared by its bytes: implement `IEquatable<T>` to have your `Equals` used.

For other rules, name a comparer: `Dict<TKey, TValue, TComparer>` and `HashSet<T, TComparer>` take an
unmanaged struct that implements `IEqualityComparer<T>`. It is kept with the collection, so it can
carry state, and its methods are called directly. `ByteEqualityComparer<T>` compares raw bytes:

<!-- sample: samples/Collections.cs modulo -->
```csharp
// A comparer with state: keys that leave the same remainder are one key.
public struct Modulo : IEqualityComparer<int>
{
    public int By;
    public bool Equals(int a, int b) => a % By == b % By;
    public int GetHashCode(int value) => value % By;
}
```

<!-- sample: samples/Collections.cs dict-comparer -->
```csharp
// As in .NET, 0.0 and -0.0 are one key; by raw bytes, they're two.
var byValue = Dict<double, int>.Create(more);
var byBytes =
    Dict<double, int, ByteEqualityComparer<double>>.Create(more);
byBytes.Set(0.0, 1);
byBytes.Set(-0.0, 2);
byValue.Set(0.0, 1);
byValue.Set(-0.0, 2);
Console.WriteLine($"By bytes: {byBytes.Count} keys; by value: {byValue.Count}");

var lastDigit =
    Dict<int, int, Modulo>.Create(more, comparer: new Modulo { By = 10 });
lastDigit.Set(13, 1);
lastDigit.Set(23, 2);  // the same key as 13
lastDigit.TryGet(3, out int atThree);
Console.WriteLine($"Modulo 10: {lastDigit.Count} key, [3] = {atThree}");
```

A `HashSet` is a `Dict` without values. `Add` returns false for an item that is already there:

<!-- sample: samples/Collections.cs hashset -->
```csharp
var seen = HashSet<int>.Create(more);
int duplicates = 0;
foreach (int id in new[] { 3, 1, 4, 1, 5, 9, 2, 6, 5, 3, 5 })
{
    if (!seen.Add(id))
        duplicates++;
}
Console.WriteLine($"{seen.Count} distinct, {duplicates} duplicates");

var odds = HashSet<int>.Create(more);
for (int i = 1; i < 10; i += 2)
    odds.Add(i);
var both = seen.Clone(more);
both.IntersectWith(odds);      // in seen and odds: 1, 3, 5, 9
var evens = seen.Clone(more);
evens.ExceptWith(odds);        // in seen, not odds: 2, 4, 6
```

## Queues, stacks and priorities

`Queue<T>`, `Stack<T>` and `PriorityQueue<TElement, TPriority>` have .NET's names, with `Try` in front
of the methods that take an item out, since there is no exception to say the collection is empty.
A queue reuses the memory of items already dequeued, so one that is fed as fast as it is drained
doesn't keep growing:

<!-- sample: samples/Collections.cs queue -->
```csharp
var queue = Queue<int>.Create(more);
for (int i = 1; i <= 40; i++)
    queue.Enqueue(i);
int expected = 1;
bool inOrder = true;
for (int i = 0; i < 30; i++)   // past half: the rest move to the front
{
    queue.TryDequeue(out int item);
    inOrder &= item == expected++;
}
for (int i = 41; i <= 50; i++)
    queue.Enqueue(i);
queue.TryPeek(out int front);
while (queue.TryDequeue(out int item))
    inOrder &= item == expected++;
```

A `PriorityQueue` gives the smallest priority first, by its `IComparable<T>`, or by a comparer struct
named as a third type argument (`PriorityQueue<TElement, TPriority, TComparer>`). Elements with equal
priorities come out in no particular order:

<!-- sample: samples/Collections.cs priority-queue -->
```csharp
var tasks = PriorityQueue<char, int>.Create(more);
tasks.Enqueue('c', 3);
tasks.Enqueue('a', 1);
tasks.Enqueue('d', 4);
tasks.Enqueue('b', 2);
Console.Write("By priority:");
while (tasks.TryDequeue(out char task, out int priority))
    Console.Write($" {task}{priority}");
Console.WriteLine();
```

## Sort and iterate

- `Sort()` on a `List`, a `RefList` or a span orders the items by their `IComparable<T>`, as
  `Comparer<T>.Default` does: numbers by value, strings by character code (capitals first).
- `Sort(comparer)` takes anything that implements `IComparer<T>`. Make it a struct and the sort is
  compiled for it, with a direct call to its `Compare`. A lambda (on a span) or the address of a
  static method (`list.Sort(&Compare)`) works too, at the cost of an indirect call per comparison.
- All of them are a heapsort: no extra memory, and not stable. On a sorted span, `BinarySearch`
  finds an item, or the bitwise complement of where it would go.
- `foreach` works on `RefList`, `Dict`, `StrDict` and `RefDict`. Maps yield their pairs in no
  particular order. Don't `Set` or `Remove` during the loop: entries move, so some may be skipped or
  seen twice. For a `List`, loop over `Count` with `Get`, or over `AsSpan()`.

<!-- sample: samples/Collections.cs descending -->
```csharp
// A comparer as a struct: Sort is compiled for it; Compare is a direct call.
public struct Descending : IComparer<int>
{
    public int Compare(int a, int b) => b.CompareTo(a);
}
```

<!-- sample: samples/Collections.cs sort-comparers -->
```csharp
unsorted.Sort();                  // by int's IComparable<int>
Console.Write("Sort():     ");
PrintList(unsorted);
unsorted.Sort(new Descending());  // by a struct: an IComparer<int>
Console.Write("Descending: ");
PrintList(unsorted);

Span<int> span = values;          // spans sort in place too
span.Sort();
Console.WriteLine($"BinarySearch(19): {span.BinarySearch(19)}, "
    + $"(20) would go at {~span.BinarySearch(20)}");
```

## Copy into another arena

`var b = a` doesn't copy. `Clone(target)` makes an independent copy, in the same arena or another one.
Use it to keep a result past the life of a scratch arena:

<!-- sample: samples/Collections.cs clone -->
```csharp
Arena keep = Arena.Create(4096);
StrDict<int> countsCopy = counts.Clone(keep); // keys are copied into keep too
List<int> snapshot = numbers.Clone(keep);
Dict<int, int> scoresCopy = scores.Clone(keep);
```

After this, the sample changes the originals, destroys their arena, and keeps using the clones. A
cloned `List` has just enough capacity for its items.

What a clone copies:

| Collection | Copied into the target arena | Shared with the original |
|---|---|---|
| `List`, `Dict` | the items, byte for byte | whatever a pointer inside an item points at |
| `StrDict` | the entries and the key strings | reference values |
| `RefList`, `RefDict` | the references | the objects themselves |

So a `RefList<string>` clone still points at the original strings. Copy them too, with
`Str.ToArenaString(target, s)`, if their arena goes away.

## Watch out for

- **Pointers into a list move.** `GetPtr` and `AsSpan` are invalidated when the list grows, by
  `RemoveAt`, and by resetting the arena.
- **Growth leaves old buffers behind.** A collection that grows extends its buffer in place while it is
  the arena's newest allocation; otherwise the old buffer stays until the arena is reset. A capacity
  passed to `Create` avoids that.
- **Don't grow an older collection inside a `Save`/`Restore` or a scope.** Its new buffer is freed by
  the `Restore` while the collection still points at it. `Arena.DebugChecks` catches that; see
  [Catch arena mistakes](catch-arena-mistakes.html).

See also: [Memory and arenas](../concepts/memory.html), and the reference pages for
[`List`](../reference/list.html), [`Dict`](../reference/dict.html),
[`HashSet`](../reference/hashset.html), [`Queue`, `Stack` and `PriorityQueue`](../reference/queue.html),
[`RefList`](../reference/reflist.html), [`StrDict`](../reference/strdict.html) and
[`RefDict`](../reference/refdict.html).
