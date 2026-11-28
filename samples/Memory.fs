// Test: arenas, scratch memory and object scopes from F#, the parts
// docs/concepts/memory.md shows next to Arena.cs's, Scratch.cs's and the
// others'. Exit code = number of failed checks. The SDK's tests
// (fsharp_doc_samples) build it as an F# zero project's Program.fs and compare
// what it prints with tests/expected/Memory.fs.txt.
#nowarn "9" // reads and writes arena memory through pointers

open System
open Microsoft.FSharp.NativeInterop

let mutable failures = 0
let check (name: string) ok =
    if not ok then
        failures <- failures + 1
        printfn "  failed: %s" name

// docs:new-leaks
[<AbstractClass>]
type Animal() =
    abstract Sound: unit -> string

type Dog() =
    inherit Animal()
    override _.Sound() = "Woof"

type Cat() =
    inherit Animal()
    override _.Sound() = "Meow"

// Each object is on the heap for good: fine for the few that live as long
// as the program.
let dog: Animal = Dog()
let cat: Animal = Cat()
printfn "%s %s" (dog.Sound()) (cat.Sound())
// docs:end

// docs:second-arena
// Arenas are independent: one per lifetime. Here a second arena holds a
// list for a while and is dropped without touching the first.
let frame = Arena.Create(256un)
let numbers = List<int>.Create(frame)
for i in 0 .. 99 do
    numbers.Add(i) |> ignore
// docs:end
check "a list in its own arena" (numbers.Count = 100 && numbers.Get(99) = 99)

let names = Arena.Create(4096un)
let name = "Ada"

// docs:restore
// Temporary strings: free them with Restore and keep the rest.
[<Safety "nothing made after the mark is kept: only a bool leaves">]
let endsRight (arena: Arena) =
    let mark = arena.Save()
    let temporary = arena.Sprintf "%s has %d letters" name name.Length
    let ok = temporary.EndsWith("3 letters")
    arena.Restore(mark)
    ok
// docs:end
let usedBefore = names.Used
check "temporary string" (endsRight names)
check "Restore freed it" (names.Used = usedBefore)

// docs:scratch
// The data going in, by value: a span of the caller's array. The callback
// is made once, a module-level value, as C#'s static lambda is.
let medianOf =
    ScratchFuncIn<ReadOnlySpan<int>, int>(fun scratch input ->
        // A copy to sort, so the caller's array keeps its order.
        let copy = scratch.AllocSpan<int>(input.Length)
        input.CopyTo(copy)
        copy.Sort()
        copy[copy.Length / 2])

let scores = [| 5; 3; 8; 1 |]
let median = Arena.Scratch(ReadOnlySpan<int>(scores), medianOf)
// docs:end
check "median" (median = 5 && scores[0] = 5)

// docs:input
// An input passed by value: the callback gets a copy of the span, which
// points at the same items, so it can fill them.
let squaresInto =
    ScratchActionIn<Span<int>>(fun scratch output ->
        let work = scratch.List<int>()
        for i in 1 .. output.Length do
            work.Add(i * i)
        work.AsSpan().CopyTo(output))

let squares: int[] = Array.zeroCreate 4
Arena.Scratch(Span<int>(squares), squaresInto)
// docs:end
check "filled by the callback" (squares = [| 1; 4; 9; 16 |])

// docs:growing
// A list of yours that grows during the callback grows in its own arena,
// so the scratch memory going away doesn't touch it. A List is a handle:
// the callback's copy adds to the same list.
let fillList =
    ScratchActionIn<List<int>>(fun scratch items ->
        let temp = scratch.AllocSpan<int>(100)
        for i in 0 .. temp.Length - 1 do
            items.Add(i) |> ignore)

let yours = Arena.Create(4096un)
let growing = List<int>.Create(yours, 4)
Arena.Scratch(growing, fillList)
check "your list is intact" (growing.Count = 100 && growing.Get(99) = 99)
// docs:end

// docs:lists
// Lists and text from the scope grow as needed and go away with the rest
// of the scratch memory. The string to keep is copied out into an arena of
// yours.
let evensReport =
    ScratchFuncIn<Arena, string>(fun scratch target ->
        let evens = scratch.List<int>(4)
        for i in 18 .. -2 .. 0 do
            evens.Add(i)
        evens.AsSpan().Sort()

        let text = scratch.Text()
        text.Append(evens.Count)
        text.Append(" evens:")
        for even in evens do
            text.Append(' ')
            text.Append(even)
        text.ToArenaString(target))

let keep = Arena.Create(4096un)
let report = Arena.Scratch(keep, evensReport)
// docs:end
check "report" (report = "10 evens: 0 2 4 6 8 10 12 14 16 18")

// docs:dict
// Count each roll in a dictionary that goes away with the scratch memory;
// only the most common roll comes out.
let mostCommonOf =
    ScratchFuncIn<ReadOnlySpan<int>, int>(fun scratch rolls ->
        let counts = scratch.Dict<int, int>()
        let mutable best = 0
        let mutable bestCount = 0
        for roll in rolls do
            let mutable count = 0
            counts.TryGet(roll, &count) |> ignore
            count <- count + 1
            counts.Set(roll, count) |> ignore
            if count > bestCount then
                best <- roll
                bestCount <- count
        best)

let rolls = [| 3; 6; 3; 1; 6; 3 |]
let mostCommon = Arena.Scratch(ReadOnlySpan<int>(rolls), mostCommonOf)
// docs:end
check "most common" (mostCommon = 3)

// docs:dict-clone
// Clone copies a scratch dictionary into an arena of yours, to keep after
// the callback.
let squaresTable =
    ScratchFuncIn<Arena, Dict<int, int>>(fun scratch target ->
        let table = scratch.Dict<int, int>(4)
        for i in 0 .. 99 do
            table.Set(i, i * i) |> ignore
        table.Remove(50) |> ignore
        table.Clone(target))

let results = Arena.Create(4096un)
let kept = Arena.Scratch(results, squaresTable)
// docs:end
check "cloned" (kept.Count = 99 && not (kept.ContainsKey 50) && kept.TryGet(9) = (true, 81))

// docs:scratch-pool
// A job that waits for another: items in a pool point at each other with
// handles, which are plain structs too.
[<Struct>]
type Job =
    val mutable Id: int
    val mutable WaitsFor: Handle<Job>
    new(id) = { Id = id; WaitsFor = Unchecked.defaultof<Handle<Job>> }

// Work in a scratch pool, then move the one result to keep into a pool of
// yours; the rest goes with the scratch memory.
let bestJob =
    ScratchFuncIn<Pool<Job>, Handle<Job>>(fun scratch keep ->
        let tries = scratch.Pool<Job>()
        let mutable top = Unchecked.defaultof<Handle<Job>>
        for id in 1 .. 20 do
            let next = tries.Add(Job(id * 7 % 20))
            if top.IsNull || tries[next].Id > tries[top].Id then
                top <- next
        tries.MoveTo(keep, &top))

let jobs = Pool<Job>.Create(yours)
let best = Arena.Scratch(jobs, bestJob)
// docs:end
check "best job kept" (jobs[best].Id = 19)

// docs:buffer
// Memory of yours instead of the thread's scratch arena, for one call and
// the Scratch calls nested in it: no malloc, and a fixed size (needing more
// than the buffer holds stops the program).
let sumOfHundred =
    ScratchFunc<int>(fun scratch ->
        let numbers = scratch.AllocSpan<int>(100)
        for i in 0 .. numbers.Length - 1 do
            numbers[i] <- i
        let mutable sum = 0
        for n in numbers do
            sum <- sum + n
        sum)

[<Safety "nothing else uses memory until the call returns">]
let sumInBuffer (memory: byte[]) =
    Arena.Scratch(Span<byte>(memory), sumOfHundred)

let total = sumInBuffer (Array.zeroCreate 2048)
// docs:end
check "in a buffer" (total = 4950)

// docs:on-stack
// The same in memory on ScratchOnStack's own stack frame: safe, since
// nothing but the scope sees it.
let squaresSum =
    ScratchFunc<int>(fun scratch ->
        let items = scratch.List<int>(8)
        for i in 1 .. 10 do
            items.Add(i * i)
        let mutable sum = 0
        for item in items do
            sum <- sum + item
        sum)

let sumOfSquares = Arena.ScratchOnStack(1024, squaresSum)
// docs:end
check "on the stack" (sumOfSquares = 385)

// docs:own-types
// Only unmanaged fields, so the scope can allocate one.
[<Struct>]
type Particle =
    val mutable X: float32
    val mutable Y: float32
    val mutable Speed: float32

let speeds =
    ScratchFunc<int>(fun scratch ->
        // An unmanaged struct comes from the scope, used through a byref.
        let particle = &scratch.Alloc<Particle>()
        particle.Speed <- 2.0f
        let stack = scratch.List<int>()
        for i in 1 .. 3 do
            stack.Add(i * int particle.Speed) |> ignore
        let top = stack[stack.Count - 1]
        stack.RemoveAt(stack.Count - 1) |> ignore
        top * 10 + stack.Count)

let popped = Arena.Scratch(speeds)
// docs:end
check "own types" (popped = 62)

let echo (s: string) = String(s.AsSpan())

// docs:object-scope
// Ordinary F# allocates with calloc and never frees. Inside an object
// scope the same code allocates in the arena, so resetting it frees
// everything the round created: the array, the closure, the string.
let rounds = Arena.Create(4096un)

[<Safety "nothing created in a round is kept past the Reset">]
let round (n: int) =
    let result =
        use _ = rounds.AllocateObjectsHere()
        let cells: int[] = Array.zeroCreate 16
        let shift = fun x -> x + n
        let text = echo "ab" + echo "cd"
        shift cells.Length + text.Length
    rounds.Reset()
    result

let heapBefore = GC.GetTotalAllocatedBytes()
let mutable rounded = 0
for i in 0 .. 999 do
    rounded <- rounded + round i
let heapGrowth = GC.GetTotalAllocatedBytes() - heapBefore
// docs:end
check "rounds" (rounded = 1000 * 20 + 999 * 1000 / 2)
check "nothing left on the heap" (heapGrowth = 0L)

// docs:object-scope-free
// arena frame { } frees what its body created when it ends, so objects
// live exactly as long as the body; only a number, a struct of numbers or
// a string (copied out) comes back. The arena can hold longer-lived data
// too: only what came after the scope opened is freed.
let scoped = Arena.Create(4096un)
let longLived = scoped.Alloc<int>()
NativePtr.write longLived 42

[<Safety "the body returns an int and stores nothing outside">]
let frameRound () =
    arena scoped {
        let cells: int[] = Array.zeroCreate 16
        let text = echo "ab" + echo "cd"
        return cells.Length + text.Length
    }

let usedOutside = scoped.Used
let mutable frameTotal = 0
for _ in 1 .. 1000 do
    frameTotal <- frameTotal + frameRound ()
// docs:end
check "frames" (frameTotal = 20000 && scoped.Used = usedOutside)
check "kept past every scope" (NativePtr.read longLived = 42)

// docs:max-capacity
// A budget: past 64 KB of blocks, allocations fail as if memory had run
// out, even on systems where malloc never says no.
let request = Arena.Create(4096un, "request", maxCapacity = 65536un)
let small = request.AllocArray<int>(1000)       // fine
let huge = request.AllocArray<int>(100_000)     // null: over budget
// docs:end
check "budget" (not (NativePtr.isNullPtr small) && NativePtr.isNullPtr huge)

// docs:from-buffer
// An arena in memory you provide: here an array made once. It never calls
// malloc and never grows; when the buffer is full, allocations fail.
let memory: byte[] = Array.zeroCreate 1024

[<Safety "the arena is used only inside this function">]
let fitsInBuffer () =
    let local = Arena.FromBuffer(Span<byte>(memory), "local")
    let point = local.Alloc<Particle>()
    let tooBig = local.AllocArray<byte>(2048)
    not (NativePtr.isNullPtr point) && NativePtr.isNullPtr tooBig
// docs:end
check "from a buffer" (fitsInBuffer ())

// docs:debug-checks
// Debugging aid: arenas created while DebugChecks is on fill freed memory
// with 0xDD, so a stale pointer reads an obvious pattern instead of the
// old value, and collections stop the program when used after a reset.
Arena.DebugChecks <- true
let debug = Arena.Create(256un)
let stale = debug.AllocArray<int>(4)
NativePtr.write stale 12345

[<Safety "a test of reading after Reset, on purpose">]
let resetDebug () = debug.Reset()
// docs:end
resetDebug ()
check "poisoned after Reset" (NativePtr.read stale = 0xDDDDDDDD)
Arena.DebugChecks <- false

printfn "%s" (if failures = 0 then "All checks passed." else sprintf "%d checks FAILED." failures)
exit failures
