// Memory lifetimes from F#: an arena with lib's collections, ordinary F#
// values (lists, closures, options) in an object scope, and Arena.Scratch
// (callbacks made once, inputs by value, a state by reference).
// The output is the same whatever fsc optimizes (sdk/test/run.sh builds it
// both ways), and DebugChecks stops the program if a static field is left
// pointing into an arena.
open System

Arena.DebugChecks <- true

// docs:arena
// One arena per lifetime: this one holds a list for as long as it lives.
let level = Arena.Create(1024un, "level")
let scores = List<int>.Create(level)
for s in [ 30; 10; 20 ] do
    scores.Add s |> ignore
printfn "%d scores, first %d" scores.Count (scores.Get 0)
// docs:end

// docs:object-scope
// Each round's lists, closures and options go into the frame arena, and
// closing the scope frees them: a thousand rounds leave nothing behind.
let frame = Arena.Create(4096un, "frame")
[<Safety "a round returns an int, and keeps nothing it made">]
let round (n: int) =
    use _ = frame.OpenObjectScope()
    let evens =
        [ 1 .. 100 ]
        |> List.map (fun x -> x * 2)
        |> List.filter (fun x -> x % 3 <> 0)
    let rotated =
        match evens with
        | [] -> []
        | first :: rest -> rest @ [ first ]
    let zero = Some n |> Option.map (fun r -> r - n) |> Option.defaultValue 0
    List.sum rotated + zero

let first = round 0
let before = GC.GetTotalAllocatedBytes()
let mutable total = 0
for n in 1 .. 1000 do
    total <- total + round n
let neverFreed = GC.GetTotalAllocatedBytes() - before
printfn "total %d, first %d, never freed: %d bytes" total first neverFreed
// docs:end

// docs:arena-builder
// The same round with arena { }: it opens the scope and frees it at the end,
// and the compiler lets out only what can't point into the arena.
[<Safety "a round returns an int, and keeps nothing it made">]
let roundIn (n: int) =
    arena frame {
        let evens =
            [ 1 .. 100 ]
            |> List.map (fun x -> x * 2)
            |> List.filter (fun x -> x % 3 <> 0)
        return List.sum evens + n
    }

// A string made inside is copied out as the scope ends.
[<Safety "only the joined string leaves, copied out">]
let describe (n: int) =
    arena frame {
        let parts = [ for i in 1 .. n -> string i ]
        return String.concat "-" parts
    }
// docs:end

let firstIn = roundIn 0
let beforeIn = GC.GetTotalAllocatedBytes()
let mutable totalIn = 0
for n in 1 .. 1000 do
    totalIn <- totalIn + roundIn n
let neverFreedIn = GC.GetTotalAllocatedBytes() - beforeIn
printfn "arena builder: total %d, first %d, never freed: %d bytes" totalIn firstIn neverFreedIn
let label1 = describe 1
let label3 = describe 3
for n in 1 .. 100 do
    roundIn n |> ignore
printfn "labels after more rounds: %s, %s" label1 label3

// docs:agent-arena
type Job =
    | Work of int
    | Describe of int * AsyncReplyChannel<string>
    | Total of AsyncReplyChannel<int>

let perMessage = Arena.Create(4096un, "message")

// What one message needs: a list, a wait, a sum.
let handle (n: int) =
    async {
        let squares = [ for i in 1 .. n -> i * i ]
        do! Async.Sleep 1
        return List.sum squares % 1000
    }

// Each message's work runs in its own scope of perMessage, across its waits,
// and is freed when the message is done.
[<Safety "a message's values stay in its scope; the agent keeps an int">]
let startWorker () =
    MailboxProcessor.Start(fun inbox ->
        let rec loop (total: int) : Async<unit> =
            async {
                let! job = inbox.Receive()
                match job with
                | Work n ->
                    let! part = Async.InArena(perMessage, handle n)
                    return! loop (total + part)
                | Describe(n, reply) ->
                    let numbers = async {
                        do! Async.Sleep 1
                        let parts = [ for i in 1 .. n -> string i ]
                        return String.concat "," parts }
                    let! text = Async.InArena(perMessage, numbers)
                    reply.Reply text
                    return! loop total
                | Total reply ->
                    reply.Reply total
                    return! loop total
            }
        loop 0)
// docs:end

// The same agent without the scope, to compare.
let startPlainWorker () =
    MailboxProcessor.Start(fun inbox ->
        let rec loop (total: int) : Async<unit> =
            async {
                let! job = inbox.Receive()
                match job with
                | Work n ->
                    let! part = handle n
                    return! loop (total + part)
                | Total reply ->
                    reply.Reply total
                    return! loop total
                | Describe(_, reply) ->
                    reply.Reply ""
                    return! loop total
            }
        loop 0)

let leftBy (worker: MailboxProcessor<Job>) =
    worker.PostAndReply Total |> ignore // started, waiting
    let before = GC.GetTotalAllocatedBytes()
    for _ in 1 .. 200 do
        worker.Post(Work 50)
    let total = worker.PostAndReply Total
    total, GC.GetTotalAllocatedBytes() - before

let worker = startWorker ()
let plain = startPlainWorker ()
let scopedTotal, scopedBytes = leftBy worker
let plainTotal, plainBytes = leftBy plain
printfn "agent: totals %d %d, the scope keeps %b of the work" scopedTotal plainTotal (scopedBytes * 2L < plainBytes)
let described = worker.PostAndReply(fun reply -> Describe(5, reply))
for _ in 1 .. 50 do
    worker.Post(Work 50)
worker.PostAndReply Total |> ignore
printfn "reply made in the scope, after 50 more: %s" described

// A throw inside the scope: caught outside it, with its message intact (the
// scope's memory stays until the arena is reset).
[<Safety "the exception is read before the arena is reset">]
let failInside () =
    async {
        try
            let! _ = Async.InArena(perMessage, async {
                do! Async.Sleep 1
                failwith (String.concat " " [ "bad"; "message" ])
                return 0 })
            return "not here"
        with e ->
            return "caught: " + e.Message
    }
printfn "%s" (Async.RunSynchronously(failInside ()))

// A throw right after a scope that ended without waiting: its handler
// allocates outside the arena, not in the scope just freed.
[<Safety "the scope returns an int">]
let failAfter () =
    async {
        try
            let! v = Async.InArena(perMessage, async { return 5 })
            if v = 5 then failwith "after the scope"
            return false
        with _ ->
            let before = GC.GetTotalAllocatedBytes()
            let text = String.replicate 10 "x"
            return text.Length = 10 && GC.GetTotalAllocatedBytes() > before
    }
printfn "handler after a scope allocates outside it: %b" (Async.RunSynchronously(failAfter ()))

// docs:scoped-agent
// The library runs this agent's loop: each message's handler gets a scope of
// perMessage, across its waits, and the loop makes nothing per message.
// State lives outside the handler.
let seen = ref 0

[<Safety "a message's values stay in its scope; only an int is kept">]
let startSummer () =
    MailboxProcessor<int>.StartScoped(perMessage, fun n ->
        async {
            let squares = [ for i in 1 .. n -> i * i ]
            do! Async.SwitchToThreadPool() // a wait inside the scope
            seen.Value <- seen.Value + List.sum squares % 1000
        })
// docs:end

let summer = startSummer ()
for _ in 1 .. 3 do // the first messages make the loop's objects
    summer.Post 50
    Fiber.Run()
let beforeScoped = GC.GetTotalAllocatedBytes()
for _ in 1 .. 1000 do
    summer.Post 50
    Fiber.Run()
let leftScoped = GC.GetTotalAllocatedBytes() - beforeScoped
printfn "scoped agent: %d, left behind for 1000 messages: %d bytes" seen.Value leftScoped

// Canceling its token stops a scoped agent: later messages stay queued.
let handled = ref 0
let stopScoped = new System.Threading.CancellationTokenSource()
[<Safety "the handler keeps only an int">]
let startStoppable () =
    MailboxProcessor<int>.StartScoped(perMessage, (fun n -> async { handled.Value <- handled.Value + n }), stopScoped.Token)
let stoppable = startStoppable ()
stoppable.Post 1
Fiber.Run()
stopScoped.Cancel()
stoppable.Post 1000
Fiber.Run()
printfn "stopped scoped agent: handled %d, %d still queued" handled.Value stoppable.CurrentQueueLength

// Canceled inside the scope: its finally runs, then the scope is freed.
[<Safety "the scope returns an int">]
let sleepy () =
    Async.InArena(perMessage, async {
        try
            do! Async.Sleep 10000
        finally
            printfn "finally in the scope"
        return 1 })
let stop = new System.Threading.CancellationTokenSource()
let sleeping = Async.StartAsTask(sleepy (), cancellationToken = stop.Token)
stop.CancelAfter 10
System.Threading.Tasks.Task.WhenAny(sleeping :> System.Threading.Tasks.Task).Wait()
printfn "canceled in the scope: %b" sleeping.IsCanceled

// A reply that times out inside the scope and comes after it has ended: the
// message, the timer and the reply were made outside the arena.
let slow =
    MailboxProcessor.Start(fun (inbox: MailboxProcessor<AsyncReplyChannel<string>>) ->
        async {
            let! channel = inbox.Receive()
            do! Async.Sleep 50
            channel.Reply(String.replicate 2 "late")
        })
[<Safety "the scope returns an int">]
let askInScope () =
    Async.InArena(perMessage, async {
        let! answer = slow.PostAndTryAsyncReply((fun channel -> channel), 10)
        return (match answer with None -> 1 | Some _ -> 2) })
printfn "timed out in the scope: %d" (Async.RunSynchronously(askInScope ()))
Async.RunSynchronously(Async.Sleep 100)
for _ in 1 .. 20 do
    worker.Post(Work 50)
printfn "after the late reply: %d" (worker.PostAndReply Total)

// docs:scratch
// Temporary memory F# checks: nothing from the scope can leave the callback.
// The callback is made once, as a C# static lambda is, so a call allocates
// nothing. What it works on goes in as the input, by value: here a span.
let medianOf =
    ScratchFuncIn<ReadOnlySpan<int>, int>(fun scratch input ->
        // A copy to sort, so the caller's items keep their order.
        let copy = scratch.AllocSpan<int>(input.Length)
        input.CopyTo(copy)
        copy.Sort()
        copy[copy.Length / 2])

let median (values: ReadOnlySpan<int>) = Arena.Scratch(values, medianOf)

let values = [| 5; 3; 8; 1; 9 |]
// One call first: it sets up what the later calls reuse.
let firstMedian = median (ReadOnlySpan<int>(values))
let scratchBefore = GC.GetTotalAllocatedBytes()
let mutable medians = 0
for _ in 1 .. 1000 do
    medians <- medians + median (ReadOnlySpan<int>(values))
let scratchNeverFreed = GC.GetTotalAllocatedBytes() - scratchBefore
printfn "median %d, first still %d, never freed: %d bytes"
    (medians / 1000 + firstMedian - 5) values[0] scratchNeverFreed
// docs:end

// docs:scratch-fill
// A span of yours that the callback fills: it gets a copy of the span,
// which points at the same items.
let squaresInto =
    ScratchActionIn<Span<int>>(fun scratch output ->
        let work = scratch.List<int>()
        for i in 1 .. output.Length do
            work.Add(i * i)
        work.AsSpan().CopyTo(output))

let squares: int[] = Array.zeroCreate 4
Arena.Scratch(Span<int>(squares), squaresInto)
printfn "squares %d %d %d %d" squares[0] squares[1] squares[2] squares[3]
// docs:end

// docs:scratch-state
// Several results come back through a state passed by reference. It can't
// be a ref struct (ZERO006), so what goes in with it is an array.
[<Struct>]
type Stats =
    { Values: int[]
      mutable Sum: int
      mutable Largest: int }

let statsOf =
    ScratchAction<Stats>(fun scratch st ->
        let sorted = scratch.AllocSpan<int>(st.Values.Length)
        Span<int>(st.Values).CopyTo(sorted)
        sorted.Sort()
        for v in sorted do
            st.Sum <- st.Sum + v
        st.Largest <- sorted[sorted.Length - 1])

let mutable stats = { Values = values; Sum = 0; Largest = 0 }
Arena.Scratch(&stats, statsOf)
printfn "sum %d, largest %d" stats.Sum stats.Largest
// docs:end

// docs:scratch-list
// Lists and text that grow, from the scope; the string to keep is copied
// out into an arena of yours.
let reportOf =
    ScratchFunc<Arena, string>(fun scratch target ->
        let evens = scratch.List<int>(4)
        for i in 18 .. -2 .. 0 do
            evens.Add i
        evens.AsSpan().Sort()
        let text = scratch.Text()
        text.Append(evens.Count)
        text.Append(" evens:")
        for e in evens do
            text.Append(' ')
            text.Append(e)
        text.Append(", half of them ")
        text.Append(float evens.Count / 2.0)
        text.ToArenaString(target))

let mutable keep = Arena.Create(1024un, "keep")
printfn "%s" (Arena.Scratch(&keep, reportOf))
// docs:end

// docs:scratch-inline
// A lambda written at the call is a new delegate each time: 48 bytes that
// are never freed, and more if it captures something. Fine for a call or
// two; in a loop, make the callback once, or free them with an object scope.
let inlineBefore = GC.GetTotalAllocatedBytes()
let mutable lengths = 0
for _ in 1 .. 100 do
    let length = Arena.Scratch(fun scratch -> scratch.AllocSpan<int>(8).Length)
    lengths <- lengths + length
let inlineNeverFreed = GC.GetTotalAllocatedBytes() - inlineBefore
printfn "inline: %d, something never freed: %b" lengths (inlineNeverFreed > 0L)
// docs:end
