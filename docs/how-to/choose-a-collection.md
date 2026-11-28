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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs dict -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let scores = Dict<int, int>.Create(arena)
scores.Set(1, 100) |> ignore
scores.Set(2, 250) |> ignore
scores.Set(3, 175) |> ignore
scores.Set(-42, 999) |> ignore
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs node -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// One item of a doubly linked list: a struct can't contain itself, but it
// can hold pointers to others of its kind.
[<Struct>]
type Node =
    val mutable Number: int
    val mutable Prev: Ptr<Node>
    val mutable Next: Ptr<Node>
    new(number, prev) = { Number = number; Prev = prev; Next = Ptr<Node>() }
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs chain -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// The list itself: its two ends, and the arena its items live in.
[<Struct>]
type Chain =
    val mutable Arena: Arena
    val mutable First: Ptr<Node>
    val mutable Last: Ptr<Node>
    new(arena) = { Arena = arena; First = Ptr<Node>(); Last = Ptr<Node>() }

    member this.AddLast(number: int) =
        let node = this.Arena.New(Node(number, this.Last))
        if this.Last.IsNull then this.First <- node
        else this.Last.Value.Next <- node
        this.Last <- node

    // Takes the node out by joining its neighbours to each other.
    member this.Remove(node: Ptr<Node>) =
        let n = &node.Value
        if n.Prev.IsNull then this.First <- n.Next
        else n.Prev.Value.Next <- n.Next
        if n.Next.IsNull then this.Last <- n.Prev
        else n.Next.Value.Prev <- n.Prev
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs insert -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Recursion works as it would with classes.
let rec insert (arena: Arena) (node: Ptr<TreeNode>) (key: int) =
    if node.IsNull then arena.New(TreeNode(key))
    else
        let n = &node.Value
        if key < n.Key then n.Left <- insert arena n.Left key
        else n.Right <- insert arena n.Right key
        node
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

Every member is in the [Pool reference](../reference/pool.html); this section explains what a pool
is for.

### The problem with removing

An arena frees its memory all at once. That fits items that live and die together, a tree you
build and then drop, but not items that come and go one at a time: jobs that finish, enemies in a
game, towns taken off a map. Each removed item would keep its space until the whole arena goes,
and a program that runs for long fills the arena with dead items:

```text
  arena:  [ build ][ test ][ ship ][ docs ][ lint ][ ...      ]
            done     done            done     done
          the dead jobs keep their space; only new space is used
```

So you want to free one item and give its space to the next. But items point at each other, and
the space being reused doesn't tell those pointers. Here `ship` waits for `test`:

```text
                     memory at one address
                    +----------+
  ship waits for    |   test   |  <--  ship.WaitsFor
  test is removed   |  (free)  |  <--  ship.WaitsFor   still points here
  docs is added     |   docs   |  <--  ship.WaitsFor   now reads docs
                    +----------+
```

`ship` now waits for `docs`, and nothing says so: no crash, no error, just a wrong answer, found
much later and far away. Clearing every pointer to `test` when it goes would fix it, but nothing
knows where all the copies are.

### How a pool and handles help

A `Pool<T>` keeps items in slots. `Remove` frees a slot and the next `Add` reuses it, so memory
stays the size of the items alive at once. That solves the first problem.

For the second, a pool gives you a `Handle<T>` instead of a pointer. A handle names the slot *and*
a generation, a count the slot moves on with every `Add` and `Remove`. An old handle carries an
old count, so it stops matching the moment its item goes, wherever its copies are:

```text
                     slot 1             ship.WaitsFor   finds
                    +-----------------+
  ship waits for    |  test     gen 1 |   slot 1/gen 1   test
  test is removed   | (free)    gen 2 |   slot 1/gen 1   nothing, 1 != 2
  docs is added     |  docs     gen 3 |   slot 1/gen 1   nothing, 1 != 3
                    +-----------------+
```

Nothing has to find and clear the copies: they go stale by themselves. A handle also carries its
pool's id, since every pool has a slot 1 and a handle must not find an item in another pool:

```text
  Handle<Job>, 12 bytes
  +-----------+-----------+--------------+
  |  pool id  |   slot    |  generation  |
  +-----------+-----------+--------------+
    which pool   where to    which item in
                 look        that slot
```

Using a stale handle fails loudly, at the line that uses it, instead of reaching the wrong item:

- `pool[handle]` stops the program with `Pool: the handle's item was removed or moved`.
- `Contains(handle)` and `TryGet(handle, out item)` ask without stopping.

### In code

The examples store jobs:

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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs task -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// A task that waits for another: items in a pool point at each other with
// handles, which are plain structs too.
[<Struct>]
type Job =
    val mutable Id: int
    val mutable WaitsFor: Handle<Job>
    new(id, waitsFor) = { Id = id; WaitsFor = waitsFor }
    new(id) = Job(id, Handle<Job>())
```

`Add` returns a handle, `pool[handle]` is a `ref` to the item, and `Remove` frees its slot:

<!-- sample: samples/Links.cs pool-basics -->
```csharp
var jobs = Pool<Job>.Create(arena);
Handle<Job> build = jobs.Add(new Job { Id = 1 });
jobs[build].Id = 10;          // a ref: changes the job in place
jobs.Remove(build);           // its slot is free again
Handle<Job> lint = jobs.Add(new Job { Id = 5 });  // reuses it
bool found = jobs.Contains(build);    // false: build is stale
// jobs[build] here would stop the program.
```

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs pool-basics -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let jobs = Pool<Job>.Create(arena)
let build = jobs.Add(Job(1))
jobs[build].Id <- 10          // a ref: changes the job in place
jobs.Remove(build) |> ignore  // its slot is free again
let lint = jobs.Add(Job(5))   // reuses it
let found = jobs.Contains(build)  // false: build is stale
// jobs[build] here would stop the program.
```

### Items that point at each other

A handle is a plain unmanaged struct, so items can hold handles to each other, as `Job`'s
`WaitsFor` does. Here is the story from the top of this section in code:

<!-- sample: samples/Links.cs pool -->
```csharp
var jobs = Pool<Job>.Create(arena);
Handle<Job> build = jobs.Add(new Job { Id = 1 });
Handle<Job> test = jobs.Add(new Job { Id = 2, WaitsFor = build });
Handle<Job> ship = jobs.Add(new Job { Id = 3, WaitsFor = test });
jobs.Remove(test);
Handle<Job> docs = jobs.Add(new Job { Id = 4 });  // test's old slot
// ship.WaitsFor is test's handle: it finds nothing, not docs.
bool waiting = jobs.Contains(jobs[ship].WaitsFor);   // false
```

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs pool -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let jobs = Pool<Job>.Create(arena)
let build = jobs.Add(Job(1))
let test = jobs.Add(Job(2, build))
let ship = jobs.Add(Job(3, test))
jobs.Remove(test) |> ignore
let docs = jobs.Add(Job(4))   // test's old slot
// ship's WaitsFor is test's handle: it finds nothing, not docs.
let waiting = jobs.Contains(jobs[ship].WaitsFor)   // false
```

`test` was in slot 1 and `docs` took it over, but `ship`'s handle still has `test`'s
generation, so it finds nothing where a pointer would have found `docs`.

### Rules

- **A handle belongs to its pool.** A handle used with another pool is refused with
  `Pool: the handle is from another pool`.
- **Items don't move.** The pool grows by chunks and never copies items, so a `ref` from
  `pool[handle]` stays good while it grows. After a `Remove`, though, the next `Add` may reuse the
  slot: take the `ref` again rather than keep one across a `Remove`.
- **Removing while walking is fine.** `foreach` gives the handles, and may `Remove` any item, the
  current one included; an item added during the walk may or may not be visited:

  <!-- sample: samples/Links.cs remove-while-walking -->
  ```csharp
  foreach (Handle<int> number in numbers)
      if (numbers[number] % 2 == 1)
          numbers.Remove(number);      // allowed while walking
  ```
- **Freed with the arena,** like every collection: a pool and all its items go when the arena is
  reset, restored or destroyed.

### Handing an item over

`Move` gives an item a new handle and makes every copy of the old one stale, while the item stays
where it is. Use it when one part of the program hands an item to another and must not touch it
after: any copy it kept is refused.

<!-- sample: samples/Links.cs move -->
```csharp
Handle<Job> mine = jobs.Add(new Job { Id = 7 });
Handle<Job> copy = mine;
Handle<Job> theirs = jobs.Move(ref mine);
// mine is now null, and every copy of it is refused: only the new
// owner reaches the job.
```

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs remove-while-walking -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
for number in numbers do
    if numbers[number] % 2 = 1 then
        numbers.Remove(number) |> ignore  // allowed while walking
```

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs move -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let mutable mine = jobs.Add(Job(7))
let copy = mine
let theirs = jobs.Move(&mine)
// mine is now null, and every copy of it is refused: only the new owner
// reaches the job.
```

`MoveTo(other, ref handle)` does the same into another pool. Its main use is keeping one result
from [a scratch pool](../concepts/memory.html#temporary-memory-the-compiler-checks), whose memory
is gone when the scratch block ends.

### A linked list you can remove from

`LinkedList<T>` is a pool underneath, with .NET's names. Each `Add` returns the node's handle, so
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs linked-list -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let list = LinkedList<int>.Create(arena)
let two = list.AddLast(2)
list.AddFirst(1) |> ignore
let four = list.AddAfter(two, 4)
list.AddBefore(four, 3) |> ignore
list.AddLast(5) |> ignore
list.Remove(two) |> ignore
let mutable node = list.First
while not node.IsNull do
    list[node].Value <- list[node].Value * 10   // 10, 30, 40, 50
    node <- list[node].Next
```

`foreach` is the easy way through, but you can follow the links yourself. `list.First` is the
first node's handle, and each node's `Next` is the handle of the one after it, which you look up
in the list again. `Previous` goes the other way:

```text
   list.First                                        list.Last
       |                                                 |
       v                                                 v
  +----------+     +----------+     +----------+     +----------+
  | Value 10 |     | Value 30 |     | Value 40 |     | Value 50 |
  | Next ----+---> | Next ----+---> | Next ----+---> | Next     |--> null
  | Previous | <---+ Previous | <---+ Previous | <---+ Previous |
  +----------+     +----------+     +----------+     +----------+
       |
       +--> null
```

The walk stops at the null handle after the last node:

<!-- sample: samples/Links.cs linked-list-walk -->
```csharp
// Each node's Next is the handle of the one after it; the last
// node's Next is the null handle, which ends the walk.
int total = 0;
for (var node = list.First; !node.IsNull; node = list[node].Next)
    total += list[node].Value;          // 10 + 30 + 40 + 50
```

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs linked-list-walk -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Each node's Next is the handle of the one after it; the last node's
// Next is the null handle, which ends the walk.
let mutable total = 0
let mutable node = list.First
while not node.IsNull do
    total <- total + list[node].Value          // 10 + 30 + 40 + 50
    node <- list[node].Next
```

A handle you walk with is checked like any other: if a node is removed while you hold its handle,
`list[node]` stops the program instead of following whatever took its slot.

## A larger example: a road map

This example puts the pieces together: towns joined by roads, where a town can be removed and the
shortest way is found around it. What it shows about pools is that a road left pointing at a removed
town is harmless, because its handle is stale.

The map, with distances in km:

```text
             230          150          150
  harbor --------- mill -------- ford -------- lake
     |                \                        /
  260|                 +---------- 330 -------+
     |
   hill

  harbor to lake:  530 through ford;  560 once ford is removed
```

Building it and asking for the shortest way, before and after `ford` is removed:

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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs map-use -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let map = RoadMap(arena)
let harbor, mill = map.AddTown(1), map.AddTown(2)
let ford, hill = map.AddTown(3), map.AddTown(4)
let lake = map.AddTown(5)
map.Join(harbor, mill, 230)
map.Join(mill, ford, 150)
map.Join(ford, lake, 150)
map.Join(mill, lake, 330)
map.Join(harbor, hill, 260)
let before = distance map harbor lake   // 530, through ford
map.RemoveTown(ford)
let after = distance map harbor lake    // 560, mill to lake
```

### Towns and roads

Towns and roads live in two pools. A town has any number of roads, so it holds a handle to its first
road, and each road holds a handle to the next. Here are `mill`'s roads; every arrow is a handle:

```text
  Towns pool         Roads pool
  +-------------+    +------------+    +------------+    +------------+
  | mill        |    | To: lake   |    | To: ford   |    | To: harbor |
  | FirstRoad --+--> | 330 km     |    | 150 km     |    | 230 km     |
  +-------------+    | Next ------+--> | Next ------+--> | Next: null |
                     +------------+    +------------+    +------------+
```

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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs map -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// A map: towns joined by roads, in two pools. A town holds a handle to the
// first of its roads and each road one to the next, so a town has as many
// roads as it needs, and every link is a plain struct.
[<Struct>]
type Town =
    val mutable Id: int
    val mutable FirstRoad: Handle<Road>
    new(id) = { Id = id; FirstRoad = Handle<Road>() }

and [<Struct>] Road =
    val mutable To: Handle<Town>
    val mutable Km: int
    val mutable Next: Handle<Road>
    new(``to``, km, next) = { To = ``to``; Km = km; Next = next }
```

`Map` keeps the two pools. Adding a road puts it in front of its town's list:

<!-- sample: samples/Links.cs map-join -->
```csharp
public Pool<Town> Towns;
public Pool<Road> Roads;

public static Map Create(Arena arena) => new Map
{
    Towns = Pool<Town>.Create(arena),
    Roads = Pool<Road>.Create(arena),
};

public Handle<Town> AddTown(int id) => Towns.Add(new Town { Id = id });

// A road both ways.
public void Join(Handle<Town> a, Handle<Town> b, int km)
{
    OneWay(a, b, km);
    OneWay(b, a, km);
}

// The new road goes in front of the town's chain of roads.
void OneWay(Handle<Town> from, Handle<Town> to, int km)
{
    ref Town town = ref Towns[from];
    town.FirstRoad = Roads.Add(
        new Road { To = to, Km = km, Next = town.FirstRoad });
}
```

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs map-join -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
[<Struct>]
type RoadMap =
    val Towns: Pool<Town>
    val Roads: Pool<Road>
    new(arena: Arena) =
        { Towns = Pool<Town>.Create(arena); Roads = Pool<Road>.Create(arena) }

    member this.AddTown(id) = this.Towns.Add(Town(id))

    // The new road goes in front of the town's chain of roads.
    member this.OneWay(from: Handle<Town>, ``to``: Handle<Town>, km) =
        let town = &this.Towns[from]
        town.FirstRoad <- this.Roads.Add(Road(``to``, km, town.FirstRoad))

    // A road both ways.
    member this.Join(a, b, km) =
        this.OneWay(a, b, km)
        this.OneWay(b, a, km)
```

### Removing a town

`RemoveTown` walks the town's own roads, removes each, then removes the town:

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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs map-remove -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// The town and its own roads go. Roads to it from other towns stay,
// but their handle no longer finds it.
member this.RemoveTown(town: Handle<Town>) =
    let mutable road = this.Towns[town].FirstRoad
    while not road.IsNull do
        let next = this.Roads[road].Next
        this.Roads.Remove(road) |> ignore
        road <- next
    this.Towns.Remove(town) |> ignore
```

It doesn't touch the roads *to* the town from other towns. After `RemoveTown(ford)`, `mill` still
has its road to `ford`:

```text
  +-------------+    +------------+    +------------+    +------------+
  | mill        |    | To: lake   |    | To: ford   |    | To: harbor |
  | FirstRoad --+--> | 330 km     |    | 150 km     |    | 230 km     |
  +-------------+    | Next ------+--> | Next ------+--> | Next: null |
                     +------------+    +------------+    +------------+
                                             |
                                     a stale handle: ford was removed
```

That road is harmless. Its `To` is stale, so `Towns.Contains(road.To)` is false, even after a new
town takes `ford`'s slot. Code that follows roads checks that and skips the road.

### Finding the shortest way

`Distance` does that check. The rest is an ordinary shortest-path search (Dijkstra's, with a scan
instead of a priority queue), whose tables are [scratch memory](../concepts/memory.html#temporary-memory-the-compiler-checks)
keyed by handle and gone when it returns. `Trip` is a struct carrying the map and both towns into
the scratch block:

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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs distance -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// The shortest way by road, in km, or -1 if there is none. The search's own
// tables are scratch memory, gone when it returns.
let shortest =
    ScratchFuncIn<Trip, int>(fun scratch trip ->
        let best = scratch.Dict<Handle<Town>, int>()
        let done' = scratch.HashSet<Handle<Town>>()
        best.Set(trip.From, 0) |> ignore
        let mutable result = -2
        while result = -2 do
            // The nearest town not done yet (a scan, to keep it short).
            let mutable here = Handle<Town>()
            let mutable km = Int32.MaxValue
            for entry in best do
                if not (done'.Contains entry.Key) && entry.Value < km then
                    here <- entry.Key
                    km <- entry.Value
            if here.IsNull then result <- -1
            elif here = trip.To then result <- km
            else
                done'.Add(here) |> ignore
                let mutable road = trip.Map.Towns[here].FirstRoad
                while not road.IsNull do
                    let r = trip.Map.Roads[road]
                    // A road to a removed town leads nowhere.
                    if trip.Map.Towns.Contains(r.To) then
                        let mutable known = 0
                        let found = best.TryGet(r.To, &known)
                        if not found || km + r.Km < known then
                            best.Set(r.To, km + r.Km) |> ignore
                    road <- r.Next
        result)

let distance (map: RoadMap) from ``to`` =
    Arena.Scratch(Trip(map, from, ``to``), shortest)
```

### Sweeping out dead roads

The roads left behind still take slots in the `Roads` pool. Where towns come and go often, sweep
them out now and then. A dead road is still in its town's list, so the sweep unlinks it before it
removes it; `link` is a `ref` to the handle that leads to the road, the town's `FirstRoad` or the
previous road's `Next`:

```text
  link refers to the Next that leads to the dead road:

  FirstRoad --> [ To: lake | Next ] --> [ To: ford | Next ] --> [ To: harbor ]
                             ^ link       (dead)

  link = Roads[road].Next makes the chain skip it, then Roads.Remove(road):

  FirstRoad --> [ To: lake | Next ] ------------------------> [ To: harbor ]
```

<div class="lang-text" data-lang="csharp">

<!-- sample: samples/Links.cs map-prune -->
```csharp
// Takes out the roads to removed towns, and returns how many. Each is
// unlinked from its town's chain first, so no chain leads to a road
// that is gone.
public int Prune()
{
    int pruned = 0;
    foreach (Handle<Town> town in Towns)
    {
        ref Handle<Road> link = ref Towns[town].FirstRoad;
        while (!link.IsNull)
        {
            Handle<Road> road = link;
            if (Towns.Contains(Roads[road].To))
            {
                link = ref Roads[road].Next;
                continue;
            }
            link = Roads[road].Next;    // the chain skips it now
            Roads.Remove(road);
            pruned++;
        }
    }
    return pruned;
}
```

</div>

<div class="lang-text" data-lang="fsharp">

This one has no F# version: it moves a `ref` along the chain (`link = ref Roads[road].Next`), and an F# byref can't be pointed elsewhere once made. In F#, walk the chain with the previous road's handle instead, and set its `Next` (or the town's `FirstRoad`) to skip the road.

</div>

Removing the road without unlinking it would leave the list leading to a removed road, and the next
walk along it would stop the program at `Roads[road]`. Since roads here go both ways, `RemoveTown`
could instead follow each of its roads to the other end and remove the road back, and no sweep
would be needed.

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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs refdict -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let byId = RefDict<int, string>.Create(arena)
byId.Set(7, "seven") |> ignore
byId.Set(3, arena.Sprintf "three is %d" 3) |> ignore
byId.Set(9, null) |> ignore
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs modulo -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// A comparer with state: keys that leave the same remainder are one key.
[<Struct>]
type Modulo =
    val By: int
    new(by) = { By = by }
    interface IEqualityComparer<int> with
        member m.Equals(a, b) = a % m.By = b % m.By
        member m.GetHashCode(value) = value % m.By
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs dict-comparer -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// As in .NET, 0.0 and -0.0 are one key; by raw bytes, they're two.
let byValue = Dict<float, int>.Create(more)
let byBytes = Dict<float, int, ByteEqualityComparer<float>>.Create(more)
byBytes.Set(0.0, 1) |> ignore
byBytes.Set(-0.0, 2) |> ignore
byValue.Set(0.0, 1) |> ignore
byValue.Set(-0.0, 2) |> ignore
printfn "By bytes: %d keys; by value: %d" byBytes.Count byValue.Count

let lastDigit = Dict<int, int, Modulo>.Create(more, comparer = Modulo(10))
lastDigit.Set(13, 1) |> ignore
lastDigit.Set(23, 2) |> ignore  // the same key as 13
let _, atThree = lastDigit.TryGet(3)
printfn "Modulo 10: %d key, [3] = %d" lastDigit.Count atThree
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs hashset -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let seen = HashSet<int>.Create(more)
let mutable duplicates = 0
for id in [| 3; 1; 4; 1; 5; 9; 2; 6; 5; 3; 5 |] do
    if not (seen.Add id) then
        duplicates <- duplicates + 1
printfn "%d distinct, %d duplicates" seen.Count duplicates

let odds = HashSet<int>.Create(more)
for i in 1 .. 2 .. 9 do
    odds.Add(i) |> ignore
let both = seen.Clone(more)
both.IntersectWith(odds) |> ignore    // in seen and odds: 1, 3, 5, 9
let evens = seen.Clone(more)
evens.ExceptWith(odds) |> ignore      // in seen, not odds: 2, 4, 6
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs queue -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let queue = Queue<int>.Create(more)
for i in 1 .. 40 do
    queue.Enqueue(i) |> ignore
let mutable expected = 1
let mutable inOrder = true
for _ in 1 .. 30 do   // past half: the rest move to the front
    let _, item = queue.TryDequeue()
    inOrder <- inOrder && item = expected
    expected <- expected + 1
for i in 41 .. 50 do
    queue.Enqueue(i) |> ignore
let _, front = queue.TryPeek()
let mutable item = 0
while queue.TryDequeue(&item) do
    inOrder <- inOrder && item = expected
    expected <- expected + 1
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs priority-queue -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let tasks = PriorityQueue<char, int>.Create(more)
tasks.Enqueue('c', 3) |> ignore
tasks.Enqueue('a', 1) |> ignore
tasks.Enqueue('d', 4) |> ignore
tasks.Enqueue('b', 2) |> ignore
printf "By priority:"
let mutable task = ' '
let mutable priority = 0
while tasks.TryDequeue(&task, &priority) do
    printf " %c%d" task priority
printfn ""
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs descending -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// A comparer as a struct: Sort is compiled for it; Compare is a direct call.
[<Struct>]
type Descending =
    interface IComparer<int> with
        member _.Compare(a, b) = b.CompareTo(a)
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs sort-comparers -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
unsorted.Sort()                   // by int's IComparable<int>
printf "Sort():     "
printList unsorted
unsorted.Sort(Descending())       // by a struct: an IComparer<int>
printf "Descending: "
printList unsorted

// Spans sort in place too.
let search (values: int[]) =
    let span = Span<int>(values)
    span.Sort()
    printfn "BinarySearch(19): %d, (20) would go at %d"
        (span.BinarySearch(19)) (~~~(span.BinarySearch(20)))
search values
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

<div class="fs-alt"></div>

<!-- sample: samples/Collections.fs clone -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let keep = Arena.Create(4096un)
let countsCopy = counts.Clone(keep)   // keys are copied into keep too
let snapshot = numbers.Clone(keep)
let scoresCopy = scores.Clone(keep)
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
