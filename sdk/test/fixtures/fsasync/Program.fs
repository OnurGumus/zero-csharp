// task { }, async { } and MailboxProcessor, against .NET's output.
open System
open System.Threading
open System.Threading.Tasks

// ---- task

// docs:task
let addLater (a: int) (b: int) =
    task {
        do! Task.Delay 10
        return a + b
    }
// docs:end

// Many binds that are done already: one stack mustn't run them all.
let sumTo (n: int) =
    task {
        let total = ref 0
        for i in 1..n do
            let! v = Task.FromResult i
            total.Value <- total.Value + v
        return total.Value
    }

type Noisy(name: string) =
    interface IDisposable with
        member _.Dispose() = printfn "disposed %s" name

let cleanUp () =
    task {
        use _ = new Noisy "resource"
        try
            do! Task.Delay 5
            printfn "body"
        finally
            printfn "finally"
        return 1
    }

printfn "addLater: %d" (addLater 2 3).Result
printfn "sumTo: %d" (sumTo 100000).Result
let both = Task.WhenAll(addLater 1 1, addLater 2 2)
printfn "whenAll: %s" (both.Result |> Array.map string |> String.concat ",")
printfn "cleanUp: %d" (cleanUp ()).Result

let fromAsync =
    task {
        let! a = async { return 20 }
        let! b = addLater a 1
        return b * 2
    }
printfn "task over async: %d" fromAsync.Result

let loops =
    task {
        let i = ref 0
        while i.Value < 3 do
            do! Task.Delay 1
            i.Value <- i.Value + 1
        return i.Value
    }
printfn "while: %d" loops.Result

// ---- async

// docs:async
let square (i: int) =
    async {
        do! Async.Sleep(i * 10)
        return i * i
    }
// docs:end

let rec count n acc =
    async {
        if n = 0 then return acc
        else return! count (n - 1) (acc + 1)
    }

// docs:async-run
let squares =
    Async.Parallel [ for i in 1..5 -> square i ]
    |> Async.RunSynchronously
printfn "parallel: %s" (String.concat "," (Array.map string squares))
// docs:end
printfn "sequential: %s" (Async.Sequential [ for i in 1..3 -> square i ] |> Async.RunSynchronously |> Array.map string |> String.concat ",")
printfn "count: %d" (count 100000 0 |> Async.RunSynchronously)

let withChild =
    async {
        let! child = Async.StartChild(square 4)
        let! mine = square 3
        let! theirs = child
        return mine + theirs
    }
printfn "child: %d" (Async.RunSynchronously withChild)

let syncLoop =
    async {
        let total = ref 0
        for i in 1..100000 do
            let! v = async { return i % 3 }
            total.Value <- total.Value + v
        return total.Value
    }
printfn "for: %d" (Async.RunSynchronously syncLoop)

printfn "awaitTask: %d" (Async.RunSynchronously(Async.AwaitTask(addLater 4 5)))

let fromCallback =
    Async.FromContinuations(fun (ok, _, _) -> ok 42)
printfn "fromContinuations: %d" (Async.RunSynchronously fromCallback)

let cts = new CancellationTokenSource()
let slow = Async.StartAsTask(Async.Sleep 10000, cancellationToken = cts.Token)
cts.CancelAfter 20
Task.WhenAny(slow :> Task).Wait()
printfn "canceled: %b" slow.IsCanceled

let ticks = ref 0
let cts2 = new CancellationTokenSource()
let ticker =
    async {
        while true do
            do! Async.Sleep 5
            ticks.Value <- ticks.Value + 1
    }
let tickerTask = Async.StartAsTask(ticker, cancellationToken = cts2.Token)
Async.RunSynchronously(Async.Sleep 60)
cts2.Cancel()
Task.WhenAny(tickerTask :> Task).Wait()
printfn "ticker ran: %b, canceled: %b" (ticks.Value > 2) tickerTask.IsCanceled

// ---- exceptions

// docs:async-errors
let fetch (name: string) =
    async {
        do! Async.Sleep 5
        if name = "" then failwith "no name"
        return name.Length
    }

let safeFetch name =
    async {
        try
            return! fetch name
        with e ->
            printfn "failed: %s" e.Message
            return -1
    }
// docs:end

printfn "safeFetch: %d" (Async.RunSynchronously(safeFetch "zero"))
printfn "safeFetch: %d" (Async.RunSynchronously(safeFetch ""))

let afterTry =
    async {
        try
            try
                do! Async.Sleep 1
            with _ ->
                printfn "inner handler: wrong"
            failwith "after the inner try"
            return "not here"
        with e ->
            return "outer got: " + e.Message
    }
printfn "%s" (Async.RunSynchronously afterTry)

let withFinally =
    async {
        try
            try
                do! Async.Sleep 1
                failwith "boom"
            finally
                printfn "finally ran"
            return "not here"
        with e ->
            return "caught " + e.Message
    }
printfn "%s" (Async.RunSynchronously withFinally)

let usesResource =
    async {
        try
            use _ = new Noisy "async resource"
            do! Async.Sleep 1
            failwith "inside use"
        with e ->
            printfn "use: %s" e.Message
    }
Async.RunSynchronously usesResource

match Async.RunSynchronously(Async.Catch(fetch "")) with
| Choice1Of2 n -> printfn "catch: %d" n
| Choice2Of2 e -> printfn "catch: error %s" e.Message

let parallelFails =
    async {
        try
            let! all = Async.Parallel [ fetch "a"; fetch ""; fetch "abc" ]
            return all.Length
        with e ->
            printfn "parallel: %s" e.Message
            return 0
    }
printfn "parallelFails: %d" (Async.RunSynchronously parallelFails)

let childFails =
    async {
        let! child = Async.StartChild(fetch "")
        try
            let! n = child
            return n
        with e ->
            printfn "child: %s" e.Message
            return 0
    }
printfn "childFails: %d" (Async.RunSynchronously childFails)

let callbackFails =
    async {
        try
            let! n = Async.FromContinuations(fun (_, fail, _) -> fail (Exception "from callback"))
            return n
        with e ->
            return e.Message.Length
    }
printfn "callbackFails: %d" (Async.RunSynchronously callbackFails)

let taskFails =
    task {
        try
            do! Task.Delay 5
            failwith "task boom"
            return "not here"
        with e ->
            return "task caught " + e.Message
    }
printfn "%s" taskFails.Result

let taskFinally =
    task {
        try
            try
                let! n = fetch ""
                return n
            finally
                printfn "task finally ran"
        with e ->
            printfn "task: %s" e.Message
            return 0
    }
printfn "taskFinally: %d" taskFinally.Result

let cts3 = new CancellationTokenSource()
let cleanUpOnCancel =
    async {
        try
            do! Async.Sleep 10000
        finally
            printfn "finally on cancel"
    }
let canceledTask = Async.StartAsTask(cleanUpOnCancel, cancellationToken = cts3.Token)
cts3.CancelAfter 10
Task.WhenAny(canceledTask :> Task).Wait()
printfn "canceled with finally: %b" canceledTask.IsCanceled

// ---- faulted tasks

let failingTask () =
    task {
        do! Task.Delay 1
        if true then failwith "task fault"
        return 0
    }

let faulted = failingTask ()
Task.WhenAny(faulted :> Task).Wait()
printfn "task faulted: %b" faulted.IsFaulted

let awaitFaulted =
    async {
        try
            let! n = Async.AwaitTask(failingTask ())
            return string n
        with e ->
            // .NET wraps it in an AggregateException; zero mode has none.
            return "awaited, message has it: " + (if e.Message.Contains "task fault" then "yes" else "no")
    }
printfn "%s" (Async.RunSynchronously awaitFaulted)

let failingAsync =
    async {
        do! Async.Sleep 1
        if true then failwith "async fault"
        return 1
    }
let asTask = Async.StartAsTask failingAsync
Task.WhenAny(asTask :> Task).Wait()
printfn "StartAsTask faulted: %b" asTask.IsFaulted

let taskAwaitsTask =
    task {
        try
            let! n = failingTask ()
            return string n
        with e ->
            return "task awaited: " + e.Message
    }
printfn "%s" taskAwaitsTask.Result

// ---- MailboxProcessor

// docs:mailbox
type Msg =
    | Add of int
    | Get of AsyncReplyChannel<int>

let counter =
    MailboxProcessor.Start(fun inbox ->
        let rec loop total : Async<unit> =
            async {
                let! msg = inbox.Receive()
                match msg with
                | Add n -> return! loop (total + n)
                | Get reply ->
                    reply.Reply total
                    return! loop total
            }
        loop 0)

for i in 1..100 do
    counter.Post(Add i)
printfn "counter: %d" (counter.PostAndReply Get)
// docs:end
for i in 1..100000 do
    counter.Post(Add 1)
printfn "counter: %d" (counter.PostAndReply Get)

// Scan takes the message it wants first, leaving the others queued.
let picky =
    MailboxProcessor.Start(fun (inbox: MailboxProcessor<string * AsyncReplyChannel<string>>) ->
        async {
            let! (first, r1) = inbox.Scan(fun (m, r) -> if m = "a" then Some(async { return (m, r) }) else None)
            let! (second, r2) = inbox.Receive()
            r1.Reply(first + " first")
            r2.Reply(second + " second")
        })
let rb = picky.PostAndAsyncReply(fun r -> ("b", r)) |> Async.StartAsTask
let ra = picky.PostAndAsyncReply(fun r -> ("a", r)) |> Async.StartAsTask
printfn "scan: %s, %s" ra.Result rb.Result

let patient =
    MailboxProcessor.Start(fun (inbox: MailboxProcessor<AsyncReplyChannel<string>>) ->
        async {
            let! nothing = inbox.TryReceive 20
            let! asker = inbox.Receive()
            asker.Reply(if nothing.IsNone then "timed out" else "got one")
        })
Async.RunSynchronously(Async.Sleep 50)
printfn "tryReceive: %s" (patient.PostAndReply id)

let impatient =
    MailboxProcessor.Start(fun (inbox: MailboxProcessor<AsyncReplyChannel<string>>) ->
        async {
            let! first =
                async {
                    try
                        let! _ = inbox.Receive 10
                        return "got one"
                    with _ ->
                        return "receive timed out"
                }
            let! asker = inbox.Receive()
            asker.Reply first
        })
Async.RunSynchronously(Async.Sleep 40)
printfn "impatient: %s" (impatient.PostAndReply id)

// Two agents passing a ball back and forth.
type Ball = Ball of int * AsyncReplyChannel<int>

let rec makePlayer (other: MailboxProcessor<Ball> ref) =
    MailboxProcessor.Start(fun inbox ->
        let rec loop () : Async<unit> =
            async {
                let! (Ball(n, done')) = inbox.Receive()
                if n = 0 then done'.Reply 0 else other.Value.Post(Ball(n - 1, done'))
                return! loop ()
            }
        loop ())

let pongRef = ref Unchecked.defaultof<MailboxProcessor<Ball>>
let pingRef = ref Unchecked.defaultof<MailboxProcessor<Ball>>
let ping = makePlayer pongRef
let pong = makePlayer pingRef
pongRef.Value <- pong
pingRef.Value <- ping
printfn "ping-pong: %d" (ping.PostAndReply(fun r -> Ball(10000, r)))

// Many agents, each waiting for one message.
let agents =
    [| for i in 1..1000 ->
        MailboxProcessor.Start(fun (inbox: MailboxProcessor<AsyncReplyChannel<int>>) ->
            async {
                let! r = inbox.Receive()
                r.Reply i
            }) |]
let replies = agents |> Array.map (fun a -> a.PostAndAsyncReply id) |> Async.Parallel |> Async.RunSynchronously
printfn "agents: %d" (Array.sum replies)
