// Test: the library's collections from F#, the parts
// docs/how-to/choose-a-collection.md shows next to Collections.cs's and
// Links.cs's. Exit code = number of failed checks. The SDK's tests
// (fsharp_doc_samples) build it as an F# zero project's Program.fs and compare
// what it prints with tests/expected/Collections.fs.txt.

open System
open System.Collections.Generic

let mutable failures = 0
let check (name: string) ok =
    if not ok then
        failures <- failures + 1
        printfn "  failed: %s" name

let arena = Arena.Create(4096un)

// docs:dict
let scores = Dict<int, int>.Create(arena)
scores.Set(1, 100) |> ignore
scores.Set(2, 250) |> ignore
scores.Set(3, 175) |> ignore
scores.Set(-42, 999) |> ignore
// docs:end
check "dict" (scores.Count = 4 && scores.TryGet(2) = (true, 250))

// docs:node
// One item of a doubly linked list: a struct can't contain itself, but it
// can hold pointers to others of its kind.
[<Struct>]
type Node =
    val mutable Number: int
    val mutable Prev: Ptr<Node>
    val mutable Next: Ptr<Node>
    new(number, prev) = { Number = number; Prev = prev; Next = Ptr<Node>() }
// docs:end

// docs:chain
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
// docs:end

let mutable chain = Chain(arena)
for i in 1 .. 4 do
    chain.AddLast(i)
chain.Remove(chain.First.Value.Next)
let chainText =
    let mutable node = chain.First
    let mutable text = ""
    while not node.IsNull do
        text <- text + string node.Value.Number
        node <- node.Value.Next
    text
check "chain" (chainText = "134")

[<Struct>]
type TreeNode =
    val mutable Key: int
    val mutable Left: Ptr<TreeNode>
    val mutable Right: Ptr<TreeNode>
    new(key) = { Key = key; Left = Ptr<TreeNode>(); Right = Ptr<TreeNode>() }

// docs:insert
// Recursion works as it would with classes.
let rec insert (arena: Arena) (node: Ptr<TreeNode>) (key: int) =
    if node.IsNull then arena.New(TreeNode(key))
    else
        let n = &node.Value
        if key < n.Key then n.Left <- insert arena n.Left key
        else n.Right <- insert arena n.Right key
        node
// docs:end

let rec inOrder (node: Ptr<TreeNode>) =
    if node.IsNull then ""
    else inOrder node.Value.Left + string node.Value.Key + inOrder node.Value.Right
let mutable tree = Ptr<TreeNode>()
for key in [ 5; 2; 8; 1; 9; 3 ] do
    tree <- insert arena tree key
check "tree" (inOrder tree = "123589")

// docs:task
// A task that waits for another: items in a pool point at each other with
// handles, which are plain structs too.
[<Struct>]
type Job =
    val mutable Id: int
    val mutable WaitsFor: Handle<Job>
    new(id, waitsFor) = { Id = id; WaitsFor = waitsFor }
    new(id) = Job(id, Handle<Job>())
// docs:end

let poolBasics () =
    // docs:pool-basics
    let jobs = Pool<Job>.Create(arena)
    let build = jobs.Add(Job(1))
    jobs[build].Id <- 10          // a ref: changes the job in place
    jobs.Remove(build) |> ignore  // its slot is free again
    let lint = jobs.Add(Job(5))   // reuses it
    let found = jobs.Contains(build)  // false: build is stale
    // jobs[build] here would stop the program.
    // docs:end
    jobs[lint].Id = 5 && not found
check "pool basics" (poolBasics ())

let poolLinks () =
    // docs:pool
    let jobs = Pool<Job>.Create(arena)
    let build = jobs.Add(Job(1))
    let test = jobs.Add(Job(2, build))
    let ship = jobs.Add(Job(3, test))
    jobs.Remove(test) |> ignore
    let docs = jobs.Add(Job(4))   // test's old slot
    // ship's WaitsFor is test's handle: it finds nothing, not docs.
    let waiting = jobs.Contains(jobs[ship].WaitsFor)   // false
    // docs:end
    not waiting && jobs[docs].Id = 4
check "pool links" (poolLinks ())

let removeWhileWalking () =
    let numbers = Pool<int>.Create(arena)
    for i in 1 .. 10 do
        numbers.Add(i) |> ignore
    // docs:remove-while-walking
    for number in numbers do
        if numbers[number] % 2 = 1 then
            numbers.Remove(number) |> ignore  // allowed while walking
    // docs:end
    numbers.Count = 5
check "remove while walking" (removeWhileWalking ())

let move () =
    let jobs = Pool<Job>.Create(arena)
    // docs:move
    let mutable mine = jobs.Add(Job(7))
    let copy = mine
    let theirs = jobs.Move(&mine)
    // mine is now null, and every copy of it is refused: only the new owner
    // reaches the job.
    // docs:end
    mine.IsNull && not (jobs.Contains copy) && jobs[theirs].Id = 7
check "move" (move ())

let linkedList () =
    // docs:linked-list
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
    // docs:end
    // docs:linked-list-walk
    // Each node's Next is the handle of the one after it; the last node's
    // Next is the null handle, which ends the walk.
    let mutable total = 0
    let mutable node = list.First
    while not node.IsNull do
        total <- total + list[node].Value          // 10 + 30 + 40 + 50
        node <- list[node].Next
    // docs:end
    total
check "linked list" (linkedList () = 130)

// docs:map
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
// docs:end

// docs:map-join
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
// docs:end

    // docs:map-remove
    // The town and its own roads go. Roads to it from other towns stay,
    // but their handle no longer finds it.
    member this.RemoveTown(town: Handle<Town>) =
        let mutable road = this.Towns[town].FirstRoad
        while not road.IsNull do
            let next = this.Roads[road].Next
            this.Roads.Remove(road) |> ignore
            road <- next
        this.Towns.Remove(town) |> ignore
    // docs:end

[<Struct>]
type Trip =
    val Map: RoadMap
    val From: Handle<Town>
    val To: Handle<Town>
    new(map, from, ``to``) = { Map = map; From = from; To = ``to`` }

// docs:distance
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
// docs:end

let roads () =
    // docs:map-use
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
    // docs:end
    before, after
check "roads" (roads () = (530, 560))

// docs:refdict
let byId = RefDict<int, string>.Create(arena)
byId.Set(7, "seven") |> ignore
byId.Set(3, arena.Sprintf "three is %d" 3) |> ignore
byId.Set(9, null) |> ignore
// docs:end
check "refdict" (byId.Count = 3 && byId.TryGet(3) = (true, "three is 3"))

// docs:modulo
// A comparer with state: keys that leave the same remainder are one key.
[<Struct>]
type Modulo =
    val By: int
    new(by) = { By = by }
    interface IEqualityComparer<int> with
        member m.Equals(a, b) = a % m.By = b % m.By
        member m.GetHashCode(value) = value % m.By
// docs:end

let more = Arena.Create(4096un)

// docs:dict-comparer
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
// docs:end

// docs:hashset
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
// docs:end
check "set operations" (both.Count = 4 && evens.Count = 3 && evens.Contains 4)

let queueInOrder () =
    // docs:queue
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
    // docs:end
    inOrder && front = 31 && expected = 51
check "queue" (queueInOrder ())

// docs:priority-queue
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
// docs:end

// docs:descending
// A comparer as a struct: Sort is compiled for it; Compare is a direct call.
[<Struct>]
type Descending =
    interface IComparer<int> with
        member _.Compare(a, b) = b.CompareTo(a)
// docs:end

let printList (list: List<int>) =
    for i in 0 .. list.Count - 1 do
        printf "%d " (list.Get(i))
    printfn ""

let values = [| 42; 7; 19; 3; 25 |]
let unsorted = List<int>.Create(more)
for v in values do
    unsorted.Add(v) |> ignore

// docs:sort-comparers
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
// docs:end

let counts = StrDict<int>.Create(more)
counts.Set("apple", 3) |> ignore
let numbers = List<int>.Create(more)
numbers.Add(1) |> ignore

// docs:clone
let keep = Arena.Create(4096un)
let countsCopy = counts.Clone(keep)   // keys are copied into keep too
let snapshot = numbers.Clone(keep)
let scoresCopy = scores.Clone(keep)
// docs:end
check "clones" (countsCopy.Count = 1 && snapshot.Count = 1 && scoresCopy.Count = 4)

printfn "%s" (if failures = 0 then "All checks passed." else sprintf "%d checks FAILED." failures)
exit failures
