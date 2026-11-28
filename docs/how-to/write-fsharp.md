---
title: Write a program in F#
index: 22
---

# Write a program in F#

A zero project can be written in F#. It's an `.fsproj` with the same SDK, and builds the same kind of
small native program, for the same targets. Records, unions, pattern matching, closures, lists,
`seq { }`, `Map`, `Set`, `decimal` and `printf` work. The program has no .NET runtime and no garbage collector,
the same as a C# zero program.

This guide uses the .NET SDK packages ([Install with dotnet](../get-started/install-with-dotnet.html)),
from version 0.12.0: 0.11.0 and earlier have no F#. Its code comes from the SDK's tests, which
build each program and check what it prints against .NET's F#.

## Make a project

The templates make F# projects with `-lang F#`:

```bash
dotnet new zero -lang F# -o Hello
dotnet new zerolib -lang F# -o Shapes
```

```xml
<Project Sdk="ZeroCSharp.Sdk/0.16.0">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
  </ItemGroup>
</Project>
```

Build and run it as a C# zero project: `dotnet build`, `dotnet run`, `dotnet publish`, and
`-p:ZeroTarget=win-x64` (or `linux-arm64`, `osx-arm64`) for another target. The F# compiler is the
one in your .NET SDK; the project compiles against the package's own small FSharp.Core, not the
NuGet one.

## Write the program

Top-level code is the program, as in a script:

<!-- sample: sdk/test/fixtures/fstoplevel/Program.fs file -->
```fsharp no-check reason="a zero program: the SDK tests build and run it"
let greet name = "Hello, " + name + "!"

Out.Line(greet "top-level F#")
```

Or mark a function `[<EntryPoint>]`, which gets the arguments and returns the exit code. Types and
functions from earlier files in the project work as usual:

<!-- sample: sdk/test/fixtures/fsapp/Shapes.fs file -->
```fsharp no-check reason="a file of a zero project: the SDK tests build and run it"
module Shapes

type Shape =
    | Circle of radius: float
    | Rect of width: float * height: float

type Point = { X: int; Y: int }

let area shape =
    match shape with
    | Circle r -> 3.0 * r * r
    | Rect (w, h) -> w * h

let rec sum xs =
    match xs with
    | [] -> 0
    | x :: rest -> x + sum rest

let rec map f xs =
    match xs with
    | [] -> []
    | x :: rest -> f x :: map f rest
```

<!-- sample: sdk/test/fixtures/fsapp/Program.fs file -->
```fsharp no-check reason="a file of a zero project: the SDK tests build and run it"
module Program

open Shapes

[<EntryPoint>]
let main argv =
    Out.Line("args: ", int64 argv.Length)
    let tens = [ 1; 2; 3; 4 ] |> map (fun x -> x * 10)
    Out.Line("sum: ", int64 (sum tens))
    let same = { X = 1; Y = 2 } = { X = 1; Y = 2 }
    Out.Line(if same then "records equal" else "records differ")
    Out.Write "area: "
    Out.Write(area (Rect(2.0, 3.5)))
    Out.Line()
    Out.Line("F# " + "zero")
    7
```

`Out`, `In`, `File` and the rest of the library are there, as in C#. Records and unions compare and
hash by their contents, as in .NET.

## Print with printf

`printf`, `printfn`, `eprintfn`, `sprintf`, `failwithf` and interpolated strings work, with widths,
flags and precision:

<!-- sample: sdk/test/fixtures/fsprintf/Program.fs basics -->
```fsharp
let name = "zero"
let n = 42
printfn "Hello, %s! %d" name n
printfn "%5d|%-5d|%05d|%+d|%x|%X|%o|%B" 42 42 42 42 255 255 8 5
printfn "%d %x %u" -7 -1 3000000000u
printfn "%f %.2f %e %g %g" 3.14159 2.5 12345.678 0.0001 1e20
printfn "%c %b %O %s" 'z' true (box 3) (string 2.5)
let s = sprintf "%s-%i" "a" -7
printfn "%s" s
printfn $"interpolated {n} and %d{n * 2} and {name}"
```

`%A` isn't there: it shows a value by reflection, which zero mode doesn't have, and the compiler
refuses it. `%O` calls `ToString`.

A function that takes a format and passes it on must be `inline`, as printf's own functions are.
The compiler then works out the arguments at each call:

<!-- sample: sdk/test/fixtures/fsprintf/Program.fs wrapper -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
// A wrapper taking the format: inline, as printf's own functions are.
let inline count format =
    Printf.ksprintf (fun (text: string) -> text.Length) format
printfn "length %d" (count "%s%s" "ab" "cd")
let inline log format =
    Printf.ksprintf (fun (text: string) -> Out.Line("log: " + text)) format
log "%d items, %s" 3 "ok"
```

### What printf leaves behind

Formatting a line takes memory: the values boxed, the pieces of text, the line itself. In .NET the
garbage collector frees it after the call. Zero mode has none, so printf does that work in the
thread's scratch arena and frees it all as the call returns:

```text
printfn "%d + %d" a b
  call site (heap)       format object + closures     about 100 bytes, kept
  printf (scratch)       boxes, digits, the line      freed on return
  Out's buffer           the line's bytes             written out
```

The heap keeps only what the F# compiler makes where printf is called: 40 bytes with no values,
about 100 with up to three, 200 with four and 600 with eight (a closure per value from the fourth
on). Building the line as a string, as .NET does, would leave about 2 KB. `sprintf`'s string is copied out of the scratch arena, so it lives
on the heap for good, as you'd expect. `Console.Write` leaves nothing at all, for a loop that prints
for hours.

A string you mean to keep for a while can go in an arena of yours, which frees it with everything
else it holds. `Arena.Sprintf` formats as `sprintf` does, into the arena. It's F#'s
`Str.Format(arena, $"...")`, whose C# interpolated string handler F# can't use:

<!-- sample: sdk/test/fixtures/fsprintf-scope/Program.fs arena-sprintf -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
// A string to keep: made in an arena of yours, which frees it with the
// rest of what it holds, rather than on the heap for good.
let keep = Arena.Create(4096un)
let s = keep.Sprintf "%d + %d = %d" 2 3 5
printfn "%s (%d characters, in the arena)" s s.Length
```

A value's own `ToString`, for `%O`, `%s` or a `{x}` hole, is your code, so printf runs it outside
the scratch arena: what it makes goes where it would have without printf, and a `ToString` that
keeps a cached string keeps a good one.

## Use lists, sequences, maps and sets

The `List`, `Array`, `Seq`, `Map`, `Set` and `String` modules have FSharp.Core's functions, with the
same results. Arrays and strings are sequences too:

<!-- sample: sdk/test/fixtures/fscollections/Program.fs inventory -->
```fsharp
type Item = { Name: string; Price: float; Qty: int }

let items =
    [ { Name = "apple"; Price = 0.5; Qty = 10 }
      { Name = "pear"; Price = 0.75; Qty = 4 }
      { Name = "plum"; Price = 0.25; Qty = 0 } ]

let inStock = items |> List.filter (fun i -> i.Qty > 0)
let worth = inStock |> List.sumBy (fun i -> i.Price * float i.Qty)
printfn "%d in stock, worth %.2f" inStock.Length worth

let byName = items |> List.map (fun i -> i.Name, i) |> Map.ofList
printfn "%s" (String.concat ", " (Map.keys byName))
match Map.tryFind "pear" byName with
| Some pear -> printfn "pears: %d" pear.Qty
| None -> printfn "no pears"
```

Sequence expressions work, lazily, with `yield!`, loops, `use` and `try ... finally`. So do list and
array comprehensions, and ranges of any number type and of `char`:

<!-- sample: sdk/test/fixtures/fscollections/Program.fs seq -->
```fsharp
type Resource(name: string) =
    interface System.IDisposable with
        member _.Dispose() = printfn "dispose %s" name

let rec countdown n = seq {
    if n > 0 then
        yield n
        yield! countdown (n - 1) }

let numbers = seq {
    yield 1
    yield! [ 2; 3 ]
    for i in 4 .. 5 do
        yield i
    let mutable i = 6
    while i <= 7 do
        yield i
        i <- i + 1
    use r = new Resource("r")
    try
        yield 8
    finally
        printfn "finally" }
```

## Write computation expressions

Builders you write work as in .NET: `let!`, `and!`, `return`, `yield`, `for`, `while`, `use`,
`try ... finally` and custom operations become calls to your builder's methods, with no reflection:

<!-- sample: sdk/test/fixtures/fsbuilders/Program.fs option-builder -->
```fsharp
type OptionBuilder() =
    member _.Bind(x, f) = match x with Some v -> f v | None -> None
    member _.Return v = Some v
    member _.ReturnFrom(o: 'T option) = o
    member _.Zero() = Some()

let option = OptionBuilder()

let digit (c: char) =
    if c >= '0' && c <= '9' then Some(int c - int '0') else None

let addDigits a b = option {
    let! x = digit a
    let! y = digit b
    return x + y }
```

`addDigits '3' '4'` is `Some 7` and `addDigits '3' 'x'` is `None`. Each `let!` passes the rest of the
expression to `Bind` as a closure, an object like any other: in a loop that runs for long, open an
object scope around it ([Free F# values with a scope](#free-f-values-with-a-scope)).

## Wait without blocking: task, async and agents

`task { }`, `async { }` and `MailboxProcessor` work, on the library's fibers and tasks, the same
ones C#'s `async` and `await` use ([Use fibers](use-fibers.html#use-async-and-await)). A `task`
starts at once and gives a `Task`; `let!` and `do!` wait for a `Task`, a `Task<'T>`, an `Async`, a
`ValueTask` (a channel's `SendAsync`), `Task.Yield()` or what `ConfigureAwait` returns. .NET events
work too: `timer.Elapsed.Add(fun _ -> ...)` subscribes, as in .NET.

<!-- sample: sdk/test/fixtures/fsasync/Program.fs task -->
```fsharp
open System.Threading.Tasks

let addLater (a: int) (b: int) =
    task {
        do! Task.Delay 10
        return a + b
    }
```

An `async` computation runs when it's started, and passes its cancellation token on to what it
waits for:

<!-- sample: sdk/test/fixtures/fsasync/Program.fs async -->
```fsharp
let square (i: int) =
    async {
        do! Async.Sleep(i * 10)
        return i * i
    }
```

<!-- sample: sdk/test/fixtures/fsasync/Program.fs async-run -->
```fsharp
let squares =
    Async.Parallel [ for i in 1..5 -> square i ]
    |> Async.RunSynchronously
printfn "parallel: %s" (String.concat "," (Array.map string squares))
```

The five squares wait at the same time, so that takes 50 ms, not 150. `Async.Start`,
`StartImmediate`, `StartAsTask`, `StartChild`, `Sequential`, `Sleep`, `AwaitTask`,
`FromContinuations` and cancellation work as in .NET.

A `MailboxProcessor` takes the messages posted to it one at a time. `Receive`, `TryReceive`,
`Scan`, `PostAndReply` and `PostAndAsyncReply` work, with timeouts:

<!-- sample: sdk/test/fixtures/fsasync/Program.fs mailbox -->
```fsharp
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
```

`try ... with` catches what's thrown inside, before or after a wait, with zero mode's
`Catch.Run` ([Recover from errors](recover-from-errors.html)) underneath:

<!-- sample: sdk/test/fixtures/fsasync/Program.fs async-errors -->
```fsharp
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
```

`try ... finally` and `use` run when the body throws or is canceled, too. `Async.Catch` gives a
`Choice`, `RunSynchronously` throws what the computation didn't catch, and so does awaiting a child
from `StartChild`; when one of `Parallel`'s computations throws, the others are canceled and
`Parallel` throws it.

An agent waiting for a message takes about 1 KB (the agent, its closures and what waits), not a
thread or a fiber, so a program can have thousands. Where they differ from .NET:

- **One thread.** Computations take turns where they wait, and nothing started runs until the
  program waits: `RunSynchronously`, `PostAndReply`, `Task.Wait` or `Fiber.Run()`. When `Main`
  returns, what's still waiting ends, as .NET's thread pool work does.
- **Exceptions nothing catches.** In a `task` or a computation started with `StartAsTask`, one
  faults the `Task`, and awaiting it throws the exception itself (zero mode has no
  `AggregateException`, so `Async.AwaitTask` doesn't wrap it as .NET does). In `Async.Start`,
  `StartImmediate` or an agent, one ends the program. There's no `MailboxProcessor.Error`: catch
  inside the agent's loop.
- **No `let mutable` across a wait in `task`.** Use a `ref` cell, as `async` already needs.
- **Give an agent's loop its type** (`let rec loop n : Async<unit> = ...`) if you build with
  `Optimize` false: without optimizing, the compiler makes a loop that only ever calls itself a
  generic closure, which zero mode can't run. The default build is optimized and doesn't need it.
- **Not on WebAssembly**, which has no fibers.

## Delay a value with lazy

`lazy` makes a value the first time it's read, then keeps it. `Force()` and `.Value` read it, and
`IsValueCreated` says whether it's made yet:

<!-- sample: sdk/test/fixtures/fsbuilders/Program.fs lazy -->
```fsharp
let mutable made = 0

let expensive = lazy (
    made <- made + 1
    printfn "computing"
    6 * 7)

let useExpensive () =
    printfn "created: %b" expensive.IsValueCreated
    printfn "value: %d" (expensive.Force())
    printfn "again: %d, made %d time(s)" expensive.Value made
    printfn "created: %b" expensive.IsValueCreated
```

As in .NET, it's safe to read from several threads (`Parallel.For`): the first runs the expression,
and the others wait for its value. Reading a lazy value from inside its own expression ends the
program, where .NET would throw. C# programs have the same `Lazy<T>`.

## Count money with decimal

`decimal` works as in .NET, with the same results: 28 digits after the point, rounding half to even,
and the scale it keeps (`19.99M * 3M` is `59.97`, and `1.0M = 1.00M`). `%M` prints one, `%.2f`
rounds it:

<!-- sample: sdk/test/fixtures/fsdecimal/Program.fs decimal -->
```fsharp
let price = 19.99M
let total = price * 3M
printfn "%M %.2f %s" total total (string (total / 7M))
printfn "%M %M %M" (1M / 3M) (2M / 3M) (10M / 4M)
printfn "%M %M" (round 2.5M) (round 3.5M)
let amounts = [ 0.1M; 0.2M; 0.3M ]
printfn "%M %b" (List.sum amounts) (List.sum amounts = 0.6M)
printfn "%M" (List.average [ 1M; 2M; 4M ])
```

C# zero programs get the same `decimal`.

## Watch the memory

There's no garbage collector: memory a program allocates is never given back. F# allocates more
than you might think. Each list cell, closure, tuple, option, `seq` enumerator and `Map` update is an
object, and so is every string `sprintf` and `+` make. A program that runs and ends, as a command-line
tool does, can usually ignore this. One that loops for a long time, a game or a server, needs to give
its memory a lifetime. F# gets the same three ways as C#, which
[Memory](../concepts/memory.html) explains in full; this section shows each from F#. `[<Struct>]`
records, unions and tuples aren't objects, so they need none of it.

An arena holds memory for one lifetime and frees it all at once. Lib's `List`, `Dict` and
`StrBuilder` live in the arena you give them:

<!-- sample: sdk/test/fixtures/fsmemory/Program.fs arena -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
// One arena per lifetime: this one holds a list for as long as it lives.
let level = Arena.Create(1024un, "level")
let scores = List<int>.Create(level)
for s in [ 30; 10; 20 ] do
    scores.Add s |> ignore
printfn "%d scores, first %d" scores.Count (scores.Get 0)
```

### Free F# values with a scope

Ordinary F# code, with its lists, closures and options, allocates with `new`. Inside
`OpenObjectScope` those objects go into the arena instead, and leaving the `use` block frees them.
A loop that would leak on every round then leaks nothing:

<!-- sample: sdk/test/fixtures/fsmemory/Program.fs object-scope -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
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
```

That prints `never freed: 0 bytes`. Nothing may keep a reference to a value made inside the scope
once it closes: not a module-level value, not a ref cell or a collection from outside. Here `round`
returns an `int`, which is copied out. What the compiler keeps for you is safe, unlike in C#: fsc
keeps each lambda that captures nothing, and each union case without fields such as `[]`, in a
static field set by a static constructor, and static constructors allocate outside every scope. A
lambda that captures something is a new object each time, in the arena, and nothing outside points at
it. Set `Arena.DebugChecks <- true` while testing: closing a scope then stops the program if a static
field points into the arena.

`arena frame { ... }` does the same with the rule checked by the compiler where it can be: it opens
the scope, frees it when the body ends, and lets out only a value that can't point into the arena.
`return` takes a number, a `bool`, a `char`, an enum or a struct of those, which is copied out, or a
string, which the builder copies out of the arena before freeing it:

<!-- sample: sdk/test/fixtures/fsmemory/Program.fs arena-builder -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
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
```

Returning a list, a record, a closure or a struct with a string in it is a compile error (FS0041).
What the compiler can't see is storing something made inside into something from outside: a ref
cell, a collection or a mutable field (`let mutable` values can't be assigned from inside, as from
any closure). So `arena`, like `OpenObjectScope`, is unsafe to call, and the function using it says
why it's correct with `[<Safety>]` (next section). Write `arena frame { ... }` in one piece: the
scope opens when `arena frame` is evaluated, and keeping that in a value and using it twice stops
the program.

### Give each message a scope

`Async.InArena(frame, computation)` does the same for an `async` computation, across its waits:
each piece of it, from a wait to the next, allocates in `frame`, whatever else runs in between, and
the scope is freed when it ends. C# can't do this for an async method, whose continuations run
under the dispatcher's allocator. In an agent it gives each message a scope of its own:

<!-- sample: sdk/test/fixtures/fsmemory/Program.fs agent-arena -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
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
```

The result follows `arena { }`'s rule: a number, a struct of numbers or a string (copied out). Here
an agent left 1.1 KB behind per message instead of 3.1 KB: the list and its closures went into the
arena, and what's left is the agent's own work for each message, `Receive` and the loop. There's
no garbage collector, so an agent written this way still grows by that much per message, as any
`async` code does at each `let!`: for one that runs for long, use `StartScoped` (below). Some things are made outside the arena on purpose, because they can outlive the message: the
objects behind each wait, the timer behind `Sleep`, an agent's queue nodes, and the message and
reply channel `PostAndReply` makes. A string reply is copied to where the waiting computation
started. After a throw nothing is freed, so the exception stays readable until the arena is reset.
Cancellation runs the `finally` parts, then frees the scope.

`MailboxProcessor.StartScoped(frame, handler)` makes an agent that leaves nothing behind per
message. The loop is the library's, made once: it takes each message, opens a scope of `frame`,
calls `handler` with the message there (so the computation it returns, and every closure for its
steps, is in the arena), and frees the scope when the handler is done, across its waits. Queue
nodes are reused, and so is what the loop waits on. State lives outside the handler:

<!-- sample: sdk/test/fixtures/fsmemory/Program.fs scoped-agent -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
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
```

A thousand messages, each making a list and waiting once, leave 0 bytes behind. That's the agent's
side. The message itself is made by whoever posts it, usually outside any arena: a number or a
`[<Struct>]` union is copied into the queue and costs nothing, but an ordinary union case such as
`Add 5`, a record or a string is an object, 24 bytes or more per message, never freed. Make message
types `[<Struct>]` for an agent that runs for long. What the handler makes outside the arena counts
too: `Async.Sleep`'s timer, or the message and channel of a `PostAndReply`, which allocates per
call. A throw the handler doesn't catch ends the program, and canceling the
agent's token stops it.

`InArena` and `StartScoped` are unsafe to call, as `OpenObjectScope` is. Besides the usual rule, mind what you `Post`
from inside the scope. A message made inside it, such as `Add 5`, is in the arena, so the agent
must take it before the scope ends: wait for its reply inside the scope, or make the message
outside. A `[<Struct>]` message is copied, and `PostAndReply` makes its message outside. With
`Arena.DebugChecks` on, ending an `arena { }` or `InArena` scope stops the program if a message made
inside it is still in an agent's queue.

`OpenObjectScope` is one of the members C# marks `unsafe`: the compiler can't check that rule, so
a C# caller has to wrap the call in an `unsafe` block. F# has no such block, and `round` carries a
`[<Safety "...">]` attribute instead. The next section is about that.

### Say why an unsafe call is safe

When an F# function calls a member C# marks `unsafe`, such as an arena's `Reset`, `Restore`,
`Destroy`, `OpenScope` and `OpenObjectScope`, or an `unsafe` method of a C# zero library of yours,
the build warns:

```text
Program.fs(63,19): warning ZERO007: Program.unmarked calls Arena.Reset, which is unsafe: ...
```

Read the member's safety rules (the [Arena reference](../reference/arena.html) has them), then mark
the function with `[<Safety "...">]` and the reason this use is correct. It is the
[SAFETY comment](../concepts/safety-rules.html#safety-comments) C# code here writes on an `unsafe`
block, made part of the code:

<!-- sample: sdk/test/fixtures/fsunsafe/Program.fs safety -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
// OpenObjectScope is unsafe: nothing made inside it may be used after it.
// [<Safety>] says why this use is correct, as a SAFETY comment does in C#.
[<Safety "the round's values are summed into an int; nothing else is kept">]
let round (n: int) =
    use _ = frame.OpenObjectScope()
    [ 1 .. n ] |> List.map (fun x -> x * 2) |> List.sum
```

A function that can't promise that by itself passes the duty on with `[<RequiresUnsafe>]`. It is
then unsafe to call, as an `unsafe` method is in C#, and the build warns about its callers instead:

<!-- sample: sdk/test/fixtures/fsunsafe/Program.fs requires-unsafe -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
// [<RequiresUnsafe>] passes the duty on instead: the function is unsafe to
// call, as an `unsafe` method is in C#, and its callers are the ones to mark.
[<RequiresUnsafe>]
let endFrame () = frame.Reset()

[<Safety "nothing from the frame is in use between frames">]
let playFrame (n: int) =
    let total = round n
    endFrame ()
    total
```

How the marks work:

- **A mark covers the whole function**, with the lambdas and inner functions in it. It goes on a
  function, a member or a property, not on a type or a module. On a top-level `do`, it covers the
  file's top-level code.
- **The reason is required.** `[<Safety "">]` isn't a mark.
- **A marked function is never copied into its callers.** The F# compiler does that with small
  functions, which would put the unsafe call in a function nobody marked. `Safety` is F#'s
  `NoCompilerInlining` attribute with a reason, so it can't go on an `inline` function (FS3151). A
  small function with `[<RequiresUnsafe>]` may be copied, and the warning for its caller then names
  the member it calls.
- **It's a warning, and it stays.** The program builds and runs. The warning is shown in every build
  until the function is marked; `<WarningsAsErrors>ZERO007</WarningsAsErrors>` in the project makes
  it an error, and `<NoWarn>$(NoWarn);ZERO007</NoWarn>` turns it off.

As in C#, the mark is a claim: the build checks that it's there, not that it's true.
[Catch arena mistakes](catch-arena-mistakes.html) shows what catches a wrong one when the program
runs.

### Temporary memory F# checks

`Arena.Scratch` runs a callback with a scope to allocate from, and frees what it allocated when the
callback returns. What the callback works on goes in as an input, passed by value:

<!-- sample: sdk/test/fixtures/fsmemory/Program.fs scratch -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
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
```

That prints `never freed: 0 bytes` after its thousand calls. The scope's spans, lists and text are
`ref struct`s, and F#'s rules for them keep everything inside the callback: a closure can't capture
them, a field, a ref cell or a module-level value can't hold them, and the result can't be one.
`sdk/test/fixtures/fs-must-not-compile/` holds one program per way out, and the SDK's tests check
that the build refuses each.

**Make the callback once.** `medianOf` is a module-level value, so its delegate is created when the
program starts, as a C# `static` lambda's is on its first call. A lambda written at the call also
works, and F# picks the delegate type itself, but each call then makes a new delegate that is never
freed:

<!-- sample: sdk/test/fixtures/fsmemory/Program.fs scratch-inline -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
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
```

The input is a copy, so a span passed that way still points at your items, and the callback can
fill them:

<!-- sample: sdk/test/fixtures/fsmemory/Program.fs scratch-fill -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
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
```

Several results come back through a state passed by reference, with `&`. That form needs the
delegate type written out:

<!-- sample: sdk/test/fixtures/fsmemory/Program.fs scratch-state -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
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
```

Here is the one difference from C#. C# lets that state be a `ref struct` holding spans of yours, and
its `scoped` keyword stops the callback from storing scratch memory in it. F# ignores `scoped`, so the
build checks instead: a callback whose by-reference state is a `ref struct` (a `Span`, or a type
with `[<IsByRefLike>]`) is an error, ZERO006, that names the callback. Pass a `ref struct` by value,
as above, where a copy can't carry anything out; a state passed by reference is a struct or a record
of ordinary values.

Lists and text that grow come from the scope too. Text takes strings, chars and numbers; a string to
keep is copied out into an arena of yours:

<!-- sample: sdk/test/fixtures/fsmemory/Program.fs scratch-list -->
```fsharp no-check reason="uses the zero library: the SDK tests build and run it"
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
```

C#'s `text.Append($"...")`, which formats straight into the text, has no F# counterpart: append the
pieces, as here. `sprintf` works, but makes a string of its own first.

## Build for the Commodore 64

An F# project builds for the C64 as a C# one does, with `-p:ZeroTarget=c64`
([Build for the Commodore 64](build-for-c64.html)). `samples/Paddles.fs` is the paddle-and-ball game
from that guide, in F#: 38 KB, 2 KB more than the C# one. Its test match ends the same way, to the
frame, and allocates nothing while it plays. Struct unions do what C# has to write out by hand:

<!-- sample: samples/Paddles.fs unions -->
```fsharp no-check reason="part of samples/Paddles.fs: the SDK tests build it for the C64 and play its test match"
// Who moves a paddle, and how fast.
[<Struct>]
type Controller =
    | Human of humanSpeed: int
    | Computer of computerSpeed: int

// What a frame of play led to. A struct union keeps its case and fields in
// the value itself, so the 50 outcomes a second are never allocated (zero
// mode never frees memory). C# had to write this one out by hand.
[<Struct>]
type Outcome =
    | InPlay
    | HitWall
    | HitPaddle of paddle: Side
    | Scored of by: Side
```

Active patterns say where the ball is. Marked `[<return: Struct>]`, a partial active pattern returns
a `voption` instead of an `option`, an object, so a match on it allocates nothing:

<!-- sample: samples/Paddles.fs step -->
```fsharp no-check reason="part of samples/Paddles.fs: the SDK tests build it for the C64 and play its test match"
let step left right (m: Match) =
    m.LeftTop <- move m.LeftTop left Left m
    m.RightTop <- move m.RightTop right Right m
    m.Ball <- { m.Ball with X = m.Ball.X + m.Ball.Dx; Y = m.Ball.Y + m.Ball.Dy }

    let wall =
        match m.Ball with
        | OffWall ->
            let ball = m.Ball
            m.Ball <- { ball with Y = Court.clampBall ball.Y; Dy = -ball.Dy }
            HitWall
        | _ -> InPlay
    let hit =
        match m with
        | AtPaddle Left -> bounce Left m
        | AtPaddle Right -> bounce Right m
        | _ -> wall
    match m.Ball with
    | PointFor side -> Scored side
    | _ -> hit
```

The C64 has about 50 KB for a program and its memory, and some F# costs more there than it looks:

- **A lambda passed to a library function**, such as `String.iteri (fun i c -> ...)`, is a closure
  class. On the title screen alone that came to 2 KB; a `for` loop costs nothing extra.
- **`string` and `+` on numbers, and `printf`**, bring in number formatting. The game writes its
  report with `Out.Write`, piece by piece.
- **A function defined inside another one** that uses its values is a closure, made when the outer
  one runs. The game's frame loop is a function of its own, so that playing allocates nothing.

## Use libraries

An F# program can reference zero libraries written in C# or F#, with a `<ProjectReference>` or a
package, as in [Create a library](create-a-library.html):

<!-- sample: sdk/test/fixtures/fs-with-libs/App/Program.fs file -->
```fsharp no-check reason="uses zero libraries: the SDK tests build and run it"
// An F# program using a C# zero library and an F# one.
module Program

[<EntryPoint>]
let main _ =
    let stack = CsLib.IntStack(4)
    stack.Push 20
    stack.Push 22
    Out.Line("C# library: ", int64 (stack.Pop() + stack.Pop()))
    let tree = FsLib.Tree.ofList [ 5; 3; 8; 1 ]
    Out.Line("F# library: ", int64 (FsLib.Tree.sum tree))
    Out.Line(if FsLib.Tree.contains 8 tree then "found 8" else "no 8")
    0
```

Ordinary NuGet packages are a different matter: they're built against .NET's runtime, which a zero
program doesn't have, and restore refuses them (NU1202), as for C#.

## Build a native library

An F# library with `<NativeLib>Shared</NativeLib>` builds a native library, as a C# one does
([Build a native library](build-a-native-library.html)). Mark each function to export with
`[<UnmanagedCallersOnly>]` and an `EntryPoint`. It must be public and in a module:

<!-- sample: sdk/test/fixtures/fsnativelib/Exports.fs exports -->
```fsharp no-check reason="part of a module: the SDK tests build the library and call it"
[<UnmanagedCallersOnly(EntryPoint = "nl_add")>]
let add (a: int) (b: int) = a + b

[<UnmanagedCallersOnly(EntryPoint = "nl_mul_long")>]
let mulLong (a: int64) (b: int64) = a * b

[<UnmanagedCallersOnly(EntryPoint = "nl_scale")>]
let scale (x: float) (f: float) = x * f

[<UnmanagedCallersOnly(EntryPoint = "nl_sum")>]
let sum (values: nativeptr<int>) (count: int) : int64 =
    let mutable total = 0L
    for i in 0 .. count - 1 do
        total <- total + int64 (NativePtr.get values i)
    total
```

The same types cross the boundary as in C#: numbers, pointers (`nativeptr<'T>`), and structs of
those, which in F# are `[<Struct>]` types with `val mutable` fields:

<!-- sample: sdk/test/fixtures/fsnativelib/Exports.fs point -->
```fsharp
[<Struct>]
type Point =
    val mutable X: int
    val mutable Y: int
```

`bool` and `char` don't cross, and a function the build can't export (a private one, a generic one,
one with a `bool`) is an error, ZERO005, that names it.

## What's missing

- **`%A`**, as above, and anything else that needs reflection.
- **`MailboxProcessor.Error` and `AggregateException`**, and the other differences listed in
  [Wait without blocking](#wait-without-blocking-task-async-and-agents).
- **`asin`, `acos`, `atan`, `sinh`, `cosh` and `tanh`.** The other math functions are there.
- **Catching exceptions outside `async` and `task`.** There, `try ... with` compiles, but a raised
  exception ends the program with its message, as `throw` does in C# zero mode
  ([Limits](../concepts/limits.html)). `Catch.result` gets it back
  ([Recover from errors](recover-from-errors.html)). `try ... finally` and `use` work.
- **`dict`, `HashSet` and .NET's collections.** Use `Map` and `Set`, or the library's `Dict` and
  `List` ([Choose a collection](choose-a-collection.html)).
- **Boxing a span**, as `span.GetType()` does: the IL isn't valid, and the build stops with ZERO015
  rather than build a program that would fail when the method runs.
- **Casting an array of arrays to another array type**, such as `string[][]` to `obj[][]`, ends the
  program. (`string[]` to `obj[]` works, as in .NET.)
