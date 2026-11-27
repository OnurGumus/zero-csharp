// Calls from F# to members C# marks `unsafe`. The build warns (ZERO007)
// unless the function says why its use is correct. sdk/test/run.sh checks
// that the lines marked "warns" below are the ones reported, whichever way
// fsc optimizes, and that the program builds and runs all the same.
open System

let frame = Arena.Create(4096un, "frame")

// docs:safety
// OpenObjectScope is unsafe: nothing made inside it may be used after it.
// [<Safety>] says why this use is correct, as a SAFETY comment does in C#.
[<Safety "the round's values are summed into an int; nothing else is kept">]
let round (n: int) =
    use _ = frame.OpenObjectScope()
    [ 1 .. n ] |> List.map (fun x -> x * 2) |> List.sum
// docs:end

// docs:requires-unsafe
// [<RequiresUnsafe>] passes the duty on instead: the function is unsafe to
// call, as an `unsafe` method is in C#, and its callers are the ones to mark.
[<RequiresUnsafe>]
let endFrame () = frame.Reset()

[<Safety "nothing from the frame is in use between frames">]
let playFrame (n: int) =
    let total = round n
    endFrame ()
    total
// docs:end

// A marked function stays in one piece however small it is, so its callers
// need no mark (fsc would otherwise copy the Reset into them).
[<Safety "the frame holds nothing when this is called">]
let small () = frame.Reset()

let callsSmall () = small ()

// Lambdas and inner functions are covered by the function around them.
[<Safety "each item's scope is closed before the next one opens">]
let nested (n: int) =
    let inner k =
        use _ = frame.OpenScope()
        k + n
    [ 1; 2 ]
    |> List.map (fun a ->
        [ 3; 4 ]
        |> List.map (fun b ->
            use _ = frame.OpenScope()
            a * b + inner 1)
        |> List.sum)
    |> List.sum

type Game() =
    [<Safety "the frame holds nothing between calls">]
    member _.Peak =
        frame.Reset()
        1

    member _.Unmarked =
        frame.Reset() // warns
        2

let unmarked () = frame.Reset() // warns

let unmarkedLambda (n: int) =
    [ 1; 2 ]
    |> List.map (fun x ->
        use _ = frame.OpenScope() // warns
        x * n)
    |> List.sum

let unmarkedInner (n: int) =
    let inner k =
        use _ = frame.OpenScope() // warns
        k + n
    inner 1 + inner 2

// Not small, so that fsc doesn't copy it into its caller: the warning then
// names this function rather than the Arena.Reset inside it.
[<RequiresUnsafe>]
let endFrames (count: int) =
    for _ in 1 .. count do
        frame.Reset()
    for _ in 1 .. count do
        frame.Reset()

let callsUnsafeFunction () = endFrames 2 // warns

// A reason is required: these two are not marks.
[<Safety "">]
let emptyReason () = frame.Reset() // warns

[<NoCompilerInlining>]
let noReason () = frame.Reset() // warns

// On a top-level `do`, the mark covers the file's top-level code.
[<Safety "the scope's values are printed, not kept">]
do
    use _ = frame.OpenObjectScope()
    let squares = [ 1; 2; 3 ] |> List.map (fun x -> x * x)
    printfn "squares %d" (List.sum squares)

unmarked ()
callsSmall ()
callsUnsafeFunction ()
emptyReason ()
noReason ()
// arena { } is unsafe to call too: it checks what the body returns, not what
// the body stores into things from outside.
let unmarkedArena (n: int) = arena frame { return n * 2 } // warns

[<Safety "returns an int and stores nothing outside">]
let markedArena (n: int) = arena frame { return n + 1 }

let unmarkedInArena () = Async.InArena(frame, async { return 1 }) // warns

let game = Game()
printfn "%d %d %d" (playFrame 10) (nested 2) (game.Peak + game.Unmarked)
printfn "%d %d" (unmarkedLambda 3) (unmarkedInner 4)
printfn "%d %d" (unmarkedArena 3) (markedArena 3)
