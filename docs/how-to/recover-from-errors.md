---
title: Recover from errors
index: 31
---

# Recover from errors

A `throw` in zero mode ends the program, unless a `Catch.Run` is waiting for it. `Catch.Run`
(`lib/Catch.cs`) runs a callback and returns what a `throw` inside it threw, or `null` if the callback
returned. It's for failures a program should get past, such as a file that isn't there or a connection
that drops, and not for a program's own bugs. `samples/Catches.cs` and `samples/Network.cs` run the
examples below.

## Run, and look at what came back

The callback can be a lambda, as with any `Action`. Here both files are opened inside it, so either
one failing ends up in the returned exception:

<!-- sample: samples/Catches.cs copy -->
```csharp
// Copies a file. A failure is one to report and go on from.
static Exception Copy(string from, string to) => Catch.Run(() =>
{
    using var source = new FileStream(from, FileMode.Open, FileAccess.Read);
    using var target = new FileStream(to, FileMode.Create);
    source.CopyTo(target);
});
```

<div class="fs-alt"></div>

<!-- sample: samples/Catches.fs copy -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// Copies a file. A failure is one to report and go on from.
let copy (from: string) (``to``: string) =
    Catch.result (fun () ->
        use source = new FileStream(from, FileMode.Open, FileAccess.Read)
        use target = new FileStream(``to``, FileMode.Create)
        source.CopyTo(target))
```

The caller decides what to do with it. An exception it doesn't expect, it can throw again: that goes
to the next `Catch.Run` out, or, with none, ends the program with the message as before.

<!-- sample: samples/Catches.cs report -->
```csharp
Exception error = Copy("no-such-file", "copy.tmp");
if (error is IOException)
    failedCopies++;  // count it, report it, and go on
else if (error != null)
    throw error;     // not an I/O failure: pass it on
```

<div class="fs-alt"></div>

<!-- sample: samples/Catches.fs report -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
match copy "no-such-file" "copy.tmp" with
| Ok () -> ()
| Error (:? IOException) ->
    failedCopies <- failedCopies + 1  // count it, report it, and go on
| Error e -> raise e                  // not an I/O failure: pass it on
```

`Catch.Run` calls nest. There's also a version for a function,
`Catch.Run(() => Parse(text), out int value)`, where `value` is `default` if it threw.

## Nothing runs on the way back

`Catch.Run` isn't C#'s `try`/`catch`. A `throw` jumps straight back to it, as C's `longjmp` does:
nothing between the `throw` and `Catch.Run` runs, so no `catch` block, no `finally` block and no
`using` disposal of the methods in between.

But `Catch.Run` returns either way, so the code after it is the `finally` block. What the code around
can reach, it releases there: open it before `Catch.Run`, use it inside, close it after. A server does
that with each connection, in the connection's own fiber:

<!-- sample: samples/Network.cs serve-catch -->
```csharp
// The same with a Catch.Run of its own, which every fiber that should
// survive a throw needs: a throw while serving (here, for a message
// that starts with '!') ends this connection, not the program.
static bool ServeGuarded(TcpClient client)
{
    Exception error = Catch.Run(ref client, static (ref TcpClient c) =>
    {
        using NetworkStream stream = c.GetStream();
        Span<byte> buffer = stackalloc byte[512];
        int n;
        while ((n = stream.Read(buffer)) > 0)
        {
            if (buffer[0] == (byte)'!')
                throw new IOException("a bad request");
            stream.Write(buffer.Slice(0, n));
        }
    });
    client.Dispose(); // runs either way: Catch.Run always returns
    return error == null;
}
```

<div class="fs-alt"></div>

<!-- sample: samples/Catches.fs serve-catch -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
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
```

For what the callback acquires deep inside, where the code around can't reach, `Catch.Run` does three
things after a throw:

- **Files and sockets are closed.** A `FileStream`, a `File` handle (from `File.OpenRead`,
  `File.Create` and the like) or a `Socket` opened inside the callback and still open is closed, at
  the system's level: the file or the socket, not the object, whose `Dispose` never runs. The
  object, if something still has it, finds itself closed at its next use, and so does every copy of
  a `File` handle. What a `FileStream` still held in its buffer (up to 4096 bytes by default) is
  lost, as when a stream is never disposed: call `Flush` where a partial file matters, or open the
  file around `Catch.Run`. (A `File` handle's buffer is the C library's, which writes it out on
  closing.) A stream the callback returns, or leaves open when it returns, stays open.

  `Catch.Run` can't tell a stream left open by a `using` that was jumped over from one the callback
  meant to keep. A socket or file opened inside and stored for later (a log file, a pooled
  connection, a listener) is closed too, and its next use throws `ObjectDisposedException`. That
  holds through nesting: what an inner `Catch.Run` left open when it returned is closed if the one
  around it later catches a throw. So open what should outlive a failure outside any `Catch.Run`,
  or tell `Catch.Run` to leave it alone with `Catch.Keep` (below).
- **Scratch memory is freed**: that of the [`Arena.Scratch`](free-memory-yourself.html) calls jumped
  out of.
- **Objects go where they went before**: an `AllocateObjectsHere` scope opened inside is over.

In the copy at the top, that closes both files when the second one fails to open or the copy fails
halfway.

## Keeping something open

A file or socket that should outlive a failure, opened where a `Catch.Run` is running (a log file
opened at its first line, a connection made for a pool), is taken off the list with `Catch.Keep`.
A throw caught later leaves it open, and closing it is the program's job again:

<!-- sample: samples/Catches.cs keep -->
```csharp
// A log opened at its first line, wherever that is written from:
// kept, it stays open when the work around it fails.
FileStream log = null;
e = Catch.Run(() =>
{
    log = new FileStream("catches.log", FileMode.Create);
    Catch.Keep(log); // stored above: closing it is ours now
    log.WriteByte((byte)'1');
    throw new IOException("the work failed");
});
log.WriteByte((byte)'2'); // still open
log.Dispose();
```

<div class="fs-alt"></div>

<!-- sample: samples/Catches.fs keep -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
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
```

Call it once the object is stored where the program will find it: a throw between opening and
`Catch.Keep` still closes it, and one after `Catch.Keep` never does. It takes a `FileStream`, a
`File` handle or a `Socket`; for a `TcpClient`, a `TcpListener` or a `UdpClient`, keep its socket
(`Client` or `Server`).

## Arena memory is yours to free

`Catch.Run` frees no arena memory. What the callback allocated in an arena of yours stays until you
free it, an `Arena.OpenScope` jumped out of included: restoring an older mark, or `Reset`, ends it.
So put the scope, or the `Reset`, around `Catch.Run`, where it runs either way:

<!-- sample: samples/Catches.cs arena -->
```csharp
// The Reset comes after Catch.Run, so it runs whether the work
// returned or threw, and frees what either left in the arena.
Arena work = Arena.Create();
int failures = 0;
for (int attempt = 0; attempt < 3; attempt++)
{
    Exception failure = Catch.Run(ref work, static (ref Arena a) =>
    {
        Span<int> numbers;
        // SAFETY: not used after the Reset below
        unsafe { numbers = a.AllocSpan<int>(1000); }
        numbers.Fill(1);
        throw new IOException("the disk is full");
    });
    if (failure != null)
        failures++; // look at the exception here, before the Reset
    unsafe { work.Reset(); } // SAFETY: nothing from the arena is kept
}
```

<div class="fs-alt"></div>

<!-- sample: samples/Catches.fs arena -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
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
```

An arena created inside the callback and not destroyed is lost, and so is `NativeMemory` not freed:
create and free those around `Catch.Run` too. The exception is an object like any other: made while
objects went to an arena, it lives as long as that arena's memory, so look at it before the `Reset`.

## A loop that keeps failing

Nothing frees what `new` makes, and a `throw` makes an exception, often with a message built for it.
Before `Catch.Run` that didn't matter, since the program ended. A program that catches and tries
again keeps every failure's objects, so a loop that fails for days grows all the while. Send the
attempt's objects to an arena, inside the callback, and `Reset` it after `Catch.Run`:

<!-- sample: samples/Catches.cs retry -->
```csharp
static void Connect(string host)
    => throw new IOException(string.Concat("no answer from ", host));

// Tries up to limit times. What a failed attempt news, the exception
// and its message included, goes to the arena, and the Reset after the
// attempt frees it: nothing else ever would.
static int Retry(Arena attempts, int limit)
{
    int failed = 0;
    while (failed < limit)
    {
        Exception error = Catch.Run(ref attempts, static (ref Arena a) =>
        {
            // SAFETY: nothing made in the scope is used after the Reset
            unsafe
            {
                using (a.AllocateObjectsHere())
                    Connect("example.org");
            }
        });
        if (error == null)
            break;
        failed++; // error and its Message are the arena's: use them here
        unsafe { attempts.Reset(); } // SAFETY: nothing from it is kept
    }
    return failed;
}
```

<div class="fs-alt"></div>

<!-- sample: samples/Catches.fs retry -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
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
```

The sample runs it a thousand times and checks that not a byte stays. Open the scope inside the
callback, as here, not around `Catch.Run`: a lambda's delegate is made the first time it's used, and
one first used while objects go to the arena would be freed with it (see
[Ordinary objects in an arena](../concepts/memory.html#ordinary-objects-in-an-arena)).

## State without a closure

Changes the callback made before the `throw` stay. With a state passed by `ref` and a `static` lambda,
nothing is allocated:

<!-- sample: samples/Catches.cs state -->
```csharp
var counter = new Counter();
e = Catch.Run(ref counter, static (ref Counter c) =>
{
    c.Calls++;
    c.Result = 7;
    throw new InvalidOperationException("after the changes");
});
// counter.Calls is 1 and counter.Result 7: they stay as they were
```

<div class="fs-alt"></div>

<!-- sample: samples/Catches.fs state -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
// What the code changed before it threw stays changed: here, a ref cell's
// value, as C#'s state passed by ref.
let calls = ref 0
let changed: Result<unit, exn> =
    Catch.result (fun () ->
        calls.Value <- calls.Value + 1
        raise (InvalidOperationException("after the changes")))
// calls.Value is 1: it stays as it was
```

<div class="lang-text" data-lang="fsharp">

F#'s lambdas can't take a byref, so there is no `ref` state: the F# version captures a ref cell. Each
call then makes a closure, and the cell, never freed; in a loop that runs often, open an object scope
around it, as the retry above does.

</div>

## Each fiber and each thread has its own

A `throw` comes back to a `Catch.Run` on its own stack. A `Catch.Run` around `Fiber.Run()` catches
nothing thrown inside the [fibers](use-fibers.html): each fiber that should survive a throw needs one
of its own, as each connection's has above. The same goes for timer callbacks, which run on fibers,
and for the bodies of `Parallel.For`, which run on other threads. A fiber's `Catch.Run` still works
after the fiber has waited.

A file or socket opened inside a `Catch.Run` may be closed on another thread: each is closed once,
whichever thread gets to it. But don't leave one in use on another thread when the `Catch.Run` it
was opened in can still catch a throw, since that closes it under the other thread's hands. Keep it
first (`Catch.Keep`), or open it on the thread that uses it.

## Async code

Each step of an async method, from its start or an `await` to its next `await` or its end, runs
as a `Catch.Run` of its own. A throw in a step faults the method's task, as in .NET, after closing
what the step opened. Awaiting the task, `Wait()` or `.Result` throws the exception again, so it
climbs the awaits until it reaches a `Catch.Run` or ends the program from an async `Main`. Inside an
async method, where an `await` can't sit in a `Catch.Run` callback, use `Catch.RunAsync`:

<!-- sample: samples/Tasks.cs run-async -->
```csharp
Exception error = await Catch.RunAsync(() => FailLate("late"));
// error is the IOException the method threw after its await
```

An `OperationCanceledException` cancels the task rather than faulting it, and `Catch.RunAsync`
gives it back. `Task.Exception` is the exception itself, not an `AggregateException`, and
`WhenAll` faults with the first one. An `async void` method that throws ends the program, as in
.NET.

A task that faults and that nothing awaits, waits for, or asks about (`IsFaulted`, `Exception`,
`Status`) is reported on stderr when `Main` returns or `Environment.Exit` ends the program, with
the count and the first such exception's message. .NET keeps quiet about it. The exit code
doesn't change. A program that never ends, such as a server in `Fiber.Run()`, never gets there,
so await what you start in it, or look at `IsFaulted`. Awaiting a task
after it faulted counts, and so does awaiting it with `ConfigureAwaitOptions.SuppressThrowing`,
which says you chose to ignore the fault:

```
Unobserved task exception: a task faulted and nothing awaited it: Connection refused
```

A step closes what it opened itself. A file opened before an `await` and still open when a later
step throws isn't closed by that throw, since the step that opened it had already returned. Open
such a file around the method, in its caller, and close it there.

## In F#

`raise`, `failwith` and `invalidArg` throw as in .NET, and `Catch.result` turns the outcome into a
`Result`:

<!-- sample: sdk/test/fixtures/fscatch/Program.fs result -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
match Catch.result (fun () -> download url path) with
| Ok size -> printfn "%d bytes" size
| Error (:? IOException as e) -> printfn "couldn't: %s" e.Message
| Error e -> raise e
```

`Catch.Run` works from F# too, and returns the exception or `null`. F#'s own `try ... with` and
`try ... finally` behave as C#'s do here: the `with` part never runs, and a `finally` part runs only
when nothing was thrown. `use` is a `using`: after a raise, `Catch.result` closes the files and
sockets that were opened inside it.

Inside `async { }` and `task { }` they work as in .NET: the builders run their pieces under
`Catch.Run`, so `with` gets what's thrown and `finally` runs either way
([Wait without blocking](write-fsharp.html#wait-without-blocking-task-async-and-agents)).

## What it doesn't catch

- **Bugs.** An index out of range, a failed cast, a null dereference or an overflow in `checked`
  code still ends the program at once. They aren't thrown, so there's nothing to come back.
- **A throw out of a static constructor** ends the program even inside `Catch.Run`: jumping out
  would leave the type half made. (A `Lazy<T>` whose factory throws is left as it was, and its next
  read runs the factory again.)
- **A throw in a `Parallel.For` iteration** ends the program, whichever thread it runs on and
  also when the loop is inside `Catch.Run`: the loop's other threads would be left working for a
  loop that is gone. A `Catch.Run` inside the iteration catches as usual.
- **A throw in an interrupt handler** (a microcontroller's `Board.StartTicks`) ends the program,
  also when the code it interrupted is inside `Catch.Run`. A `Catch.Run` in the handler itself
  works.
- **WebAssembly.** Nothing can jump back up the stack there, so a `throw` ends the program even
  inside `Catch.Run`.
- **C code.** A `throw` from a C# callback that C code called (`[UnmanagedCallersOnly]`) jumps over
  the C code too, which then never finishes what it was doing.

A program that never calls `Catch.Run` has none of its code in it.
