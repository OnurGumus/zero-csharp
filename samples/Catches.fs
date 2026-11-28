// Test: recovering from a throw in F# with Catch.result (lib/Catch.cs's
// Catch.Run as F# has it), the parts docs/how-to/recover-from-errors.md
// shows next to Catches.cs's and Network.cs's. Exit code = number of failed
// checks. The SDK's tests (fsharp_doc_samples) build it as an F# zero
// project's Program.fs and compare what it prints with
// tests/expected/Catches.fs.txt.

open System
open System.IO
open System.Net.Sockets

let mutable failures = 0
let check (name: string) ok =
    if not ok then
        failures <- failures + 1
        printfn "  failed: %s" name

// docs:copy
// Copies a file. A failure is one to report and go on from.
let copy (from: string) (``to``: string) =
    Catch.result (fun () ->
        use source = new FileStream(from, FileMode.Open, FileAccess.Read)
        use target = new FileStream(``to``, FileMode.Create)
        source.CopyTo(target))
// docs:end

let mutable failedCopies = 0
// docs:report
match copy "no-such-file" "copy.tmp" with
| Ok () -> ()
| Error (:? IOException) ->
    failedCopies <- failedCopies + 1  // count it, report it, and go on
| Error e -> raise e                  // not an I/O failure: pass it on
// docs:end
check "copy failed, and went on" (failedCopies = 1)

// docs:serve-catch
// Every fiber that should survive a throw needs a Catch of its own: a throw
// while serving (here, for a message that starts with '!') ends this
// connection, not the program.
let serveGuarded (client: TcpClient) =
    let served =
        Catch.result (fun () ->
            use stream = client.GetStream()
            let buffer: byte[] = Array.zeroCreate 512
            let mutable n = stream.Read(Span<byte>(buffer))
            while n > 0 do
                if buffer[0] = byte '!' then
                    raise (IOException("a bad request"))
                stream.Write(ReadOnlySpan<byte>(buffer, 0, n))
                n <- stream.Read(Span<byte>(buffer)))
    client.Dispose() // runs either way: Catch.result always returns
    Result.isOk served
// docs:end
check "a server to call" (not (obj.ReferenceEquals(box serveGuarded, null)))

// docs:keep
// A log opened at its first line, wherever that is written from: kept, it
// stays open when the work around it fails. A ref cell, since a closure
// can't change a let mutable from outside.
let log = ref (null: FileStream)
let kept: Result<unit, exn> =
    Catch.result (fun () ->
        log.Value <- new FileStream("catches.log", FileMode.Create)
        Catch.Keep(log.Value) // stored above: closing it is ours now
        log.Value.WriteByte(byte '1')
        raise (IOException("the work failed")))
log.Value.WriteByte(byte '2') // still open
log.Value.Dispose()
// docs:end
let texts = Arena.Create()
check "kept open" (Result.isError kept && File.TryReadAllText(texts, "catches.log") = (true, "12"))

// docs:arena
// The Reset comes after Catch.result, so it runs whether the work returned
// or threw, and frees what either left in the arena.
let work = Arena.Create()

[<Safety "nothing from the arena is used after the Reset">]
let attempt () =
    let failure: Result<unit, exn> =
        Catch.result (fun () ->
            let numbers = work.AllocSpan<int>(1000)
            numbers.Fill(1)
            raise (IOException("the disk is full")))
    let failed = Result.isError failure // look at it here, before the Reset
    work.Reset()
    failed

let mutable attemptsFailed = 0
for _ in 1 .. 3 do
    if attempt () then attemptsFailed <- attemptsFailed + 1
// docs:end
check "three failures, arena reset" (attemptsFailed = 3 && work.Used = 0un)

// docs:retry
let connect (host: string) : unit =
    raise (IOException(String.Concat("no answer from ", host)))

// Tries up to limit times. What a failed attempt makes, the exception and
// its message included, goes to the arena, and the Reset after the attempt
// frees it: nothing else ever would.
[<Safety "nothing made in the scope is used after the Reset">]
let retry (attempts: Arena) limit =
    let mutable failed = 0
    let mutable connected = false
    while not connected && failed < limit do
        let result =
            Catch.result (fun () ->
                use _ = attempts.AllocateObjectsHere()
                connect "example.org")
        match result with
        | Ok () -> connected <- true
        | Error _ ->
            failed <- failed + 1 // the error is the arena's: use it here
            attempts.Reset()
    failed
// docs:end
check "retried" (retry (Arena.Create()) 5 = 5)

// docs:state
// What the code changed before it threw stays changed: here, a ref cell's
// value, as C#'s state passed by ref.
let calls = ref 0
let changed: Result<unit, exn> =
    Catch.result (fun () ->
        calls.Value <- calls.Value + 1
        raise (InvalidOperationException("after the changes")))
// calls.Value is 1: it stays as it was
// docs:end
check "state kept" (calls.Value = 1 && Result.isError changed)

File.Delete("catches.log") |> ignore
printfn "%s" (if failures = 0 then "All checks passed." else sprintf "%d checks FAILED." failures)
exit failures
