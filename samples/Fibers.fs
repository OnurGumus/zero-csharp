// Test: fibers, channels, pipes, timers and task { } from F#, the parts
// docs/how-to/use-fibers.md shows next to Fibers.cs's, Tasks.cs's and
// Timers.cs's (and the async file and Catch examples of the files and errors
// pages). Exit code = number of failed checks. The SDK's tests
// (fsharp_doc_samples) build it as an F# zero project's Program.fs and compare
// what it prints with tests/expected/Fibers.fs.txt.

open System
open System.IO
open System.Threading
open System.Threading.Tasks

let mutable failures = 0
let check (name: string) ok =
    if not ok then
        failures <- failures + 1
        printfn "  failed: %s" name

// docs:yield
// Three fibers take turns: each logs its number and yields, so the log
// interleaves them, in the order they were spawned.
let log = ref ([]: int list)
for id in 1 .. 3 do
    Fiber.Spawn(id, fun id ->
        for _ in 1 .. 3 do
            log.Value <- id :: log.Value
            Fiber.Yield()) |> ignore
Fiber.Run()
// docs:end
check "turns" (List.rev log.Value = [ 1; 2; 3; 1; 2; 3; 1; 2; 3 ])

// docs:pipeline
// A pipeline: one fiber produces numbers, one squares them, and one adds
// them up. Receive returns false once its channel is closed and drained,
// which ends each loop.
let numbers = Channel<int>()
let squares = Channel<int>()
let total = ref 0
Fiber.Spawn((), fun () ->
    for i in 1 .. 100 do
        numbers.Send(i)
    numbers.Close()) |> ignore
Fiber.Spawn((), fun () ->
    let mutable n = 0
    while numbers.Receive(&n) do
        squares.Send(n * n)
    squares.Close()) |> ignore
Fiber.Spawn((), fun () ->
    let mutable square = 0
    while squares.Receive(&square) do
        total.Value <- total.Value + square) |> ignore
Fiber.Run()
// docs:end
check "pipeline" (total.Value = 338350)

// docs:pipe
// A pipe streams bytes through a buffer of fixed size from an arena: here
// 64 bytes carry 10,000. The writer waits whenever the buffer is full, so it
// never runs more than 64 bytes ahead of the reader.
let arena = Arena.Create(4096un)
let pipe = Pipe(arena, 64)
let received = ref 0
Fiber.Spawn((), fun () ->
    let chunk: byte[] = Array.zeroCreate 100
    let mutable sent = 0
    while sent < 10_000 do
        let n = min chunk.Length (10_000 - sent)
        for i in 0 .. n - 1 do
            chunk[i] <- byte (sent + i)
        pipe.Write(ReadOnlySpan<byte>(chunk, 0, n)) |> ignore
        sent <- sent + n
    pipe.Close()) |> ignore
Fiber.Spawn((), fun () ->
    let buffer: byte[] = Array.zeroCreate 37
    let mutable n = pipe.Read(Span<byte>(buffer))
    while n > 0 do
        received.Value <- received.Value + n
        n <- pipe.Read(Span<byte>(buffer))) |> ignore
Fiber.Run()
// docs:end
check "pipe" (received.Value = 10_000)

// docs:pipe-stream
// AsStream makes the pipe a System.IO.Stream, for code that takes one:
// CopyTo here reads until the writer disposes its end.
let streamed = Pipe(arena, 16)
let copy = new MemoryStream()
Fiber.Spawn((), fun () ->
    use output = streamed.AsStream()
    for _ in 1 .. 10 do
        output.Write(ReadOnlySpan<byte>("0123456789"B))) |> ignore
Fiber.Spawn((), fun () -> streamed.AsStream().CopyTo(copy)) |> ignore
Fiber.Run()
// docs:end
check "pipe stream" (copy.Length = 100L)

// docs:sleep
// Sleeping fibers wake in the order of their deadlines, whatever the order
// they were spawned in.
let woke = ref ([]: int list)
for ms in [ 30; 10; 20 ] do
    Fiber.Spawn(ms, fun ms ->
        Fiber.Sleep(int64 ms)
        woke.Value <- ms :: woke.Value) |> ignore
let start = Clock.Monotonic
Fiber.Run()
let took = (Clock.Monotonic - start) / 1_000_000L
// docs:end
check "sleep order" (List.rev woke.Value = [ 10; 20; 30 ] && took >= 30L)

// docs:timer
// Fires every 20 ms, on a fiber of its own, until its handler stops it.
// Fiber.Run returns once every timer has stopped.
let timer = new System.Timers.Timer(20.0)
let ticks = ref 0
timer.Elapsed.Add(fun _ ->
    ticks.Value <- ticks.Value + 1
    printfn "tick %d" ticks.Value
    if ticks.Value = 5 then timer.Stop())
timer.Start()
Fiber.Run()
// docs:end

// docs:scratch
// Each fiber has its own Arena.Scratch arena, so a fiber can wait inside
// Scratch: both fill a buffer, yield, and find their values still there
// after the other has opened and closed a scope.
let intact = ref ([]: int list)
let clobber = ScratchAction(fun other -> other.AllocSpan<int>(256).Fill(-1))
let fillYieldCheck =
    ScratchActionIn<int>(fun scratch id ->
        let mine = scratch.AllocSpan<int>(256)
        mine.Fill(id)
        Fiber.Yield()
        Arena.Scratch(clobber)
        Fiber.Yield()
        let mutable ok = true
        for value in mine do
            ok <- ok && value = id
        if ok then intact.Value <- id :: intact.Value)
for id in 1 .. 2 do
    Fiber.Spawn(id, fun id -> Arena.Scratch(id, fillYieldCheck)) |> ignore
Fiber.Run()
// docs:end
check "scratch per fiber" (List.sort intact.Value = [ 1; 2 ])

// task { } runs on the same fibers. Each step's line goes to the log.
let mutable steps = ""

// docs:order
let twice (name: string) value (delay: int) =
    task {
        steps <- steps + name + "1 "
        do! Task.Delay(delay)
        steps <- steps + name + "2 "
        return value * 2
    }
// docs:end

// docs:run-on-thread
// Busy for ms milliseconds without waiting: on a fiber this would stop
// every other fiber; on another thread it doesn't.
let spin ms =
    let finish = Clock.Monotonic + int64 ms * 1_000_000L
    let mutable turns = 0
    while Clock.Monotonic < finish do
        turns <- turns + 1
    turns

let spinElsewhere () = task { return! Task.RunOnThread(fun () -> spin 50) }
// docs:end

// docs:thread-completion
// Runs on another thread: completes the source made on this one.
let completeElsewhere (source: ThreadCompletionSource<int>) =
    spin 20 |> ignore
    source.SetResult(42)
// docs:end

// docs:channel-async
// Sends 1..10 and closes; the reader's loop ends there.
let produce (channel: Channel<int>) =
    task {
        for i in 1 .. 10 do
            do! channel.SendAsync(i)
        channel.Close()
    }

let consume (channel: Channel<int>) =
    task {
        let mutable sum = 0
        let mutable more = true
        while more do
            let! waiting = channel.WaitToReceiveAsync()
            let mutable value = 0
            if waiting && channel.TryReceive(&value) then sum <- sum + value
            else more <- waiting
        return sum
    }
// docs:end

// docs:semaphore
// Reads the total, waits, writes it back: without the gate, another call
// could run in the wait and its update would be lost.
let gate = new SemaphoreSlim(1)
let mutable shared = 0

let addOne () =
    task {
        do! gate.WaitAsync()
        try
            let seen = shared
            do! Task.Yield() // others run here
            shared <- seen + 1
        finally
            gate.Release() |> ignore
    }
// docs:end

// docs:cancel-loop
// Works until canceled, checking the token between steps; returns how many
// steps it did.
let work (token: CancellationToken) =
    task {
        let mutable count = 0
        while not token.IsCancellationRequested do
            do! Task.Delay(5)
            count <- count + 1
        return count
    }
// docs:end

let failLate (message: string) : Task =
    task {
        do! Task.Delay(5)
        raise (IOException(message))
    }

let main =
    task {
        // docs:periodic-async
        // Ticks every 10 ms without a fiber of its own; Dispose ends the loop.
        use timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10.0))
        let mutable ticked = 0
        let mutable running = true
        while running do
            let! tick = timer.WaitForNextTickAsync()
            if tick then
                ticked <- ticked + 1
                if ticked = 3 then timer.Dispose()
            else running <- false
        printfn "ticks: %d" ticked
        // docs:end

        // docs:await
        // Each call runs until its first wait, then returns, and the caller
        // goes on: a and b wait at the same time, on fibers.
        let a = twice "a" 1 30
        let b = twice "b" 2 10
        steps <- steps + "main "
        // docs:end
        let! a2 = a
        let! b2 = b
        printfn "%s= %d %d" steps a2 b2

        // docs:when-all
        // Three delays of 50 ms side by side take about 50 ms, not 150.
        let! (all: int[]) = Task.WhenAll(twice "x" 1 50, twice "y" 2 50, twice "z" 3 50)
        // docs:end
        printfn "when all: %d %d %d" all[0] all[1] all[2]

        let! turns = spinElsewhere ()
        check "spun on a thread" (turns > 0)

        // docs:thread-completion-await
        let one = ThreadCompletionSource<int>()
        Task.RunOnThread(fun () -> completeElsewhere one) |> ignore
        let! answer = one.Task
        // docs:end
        printfn "from another thread: %d" answer

        let channel = Channel<int>()
        let sent = produce channel
        let! sum = consume channel
        do! sent
        printfn "channel sum: %d" sum

        do! Task.WhenAll(Array.init 10 (fun _ -> addOne ()))
        printfn "total: %d" shared

        // docs:cancel
        // A delay of 10 s, canceled after 20 ms: SuppressThrowing makes the
        // wait return (a plain one would end the program).
        use cts = new CancellationTokenSource(20)
        let pause = Task.Delay(10_000, cts.Token)
        do! pause.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)
        printfn "canceled: %b" pause.IsCanceled
        // docs:end

        use stop = new CancellationTokenSource(30)
        let! worked = work stop.Token
        check "worked until canceled" (worked > 0)

        // docs:files
        // The write and the read run on another thread; this task waits, and
        // the others run meanwhile.
        do! File.WriteAllBytesAsync("tasks.tmp", "0123456789"B)
        let! back = File.ReadAllBytesAsync("tasks.tmp")
        // docs:end
        check "async files" (back = "0123456789"B)
        File.Delete("tasks.tmp") |> ignore

        // docs:run-async
        let! (error: exn) = Catch.RunAsync(fun () -> failLate "late")
        // error is the IOException the task threw after its wait
        // docs:end
        check "caught late" (error :? IOException && error.Message = "late")
    }
main.Wait()

printfn "%s" (if failures = 0 then "All checks passed." else sprintf "%d checks FAILED." failures)
exit failures
