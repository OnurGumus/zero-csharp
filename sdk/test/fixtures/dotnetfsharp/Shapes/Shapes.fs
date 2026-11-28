/// An F# library, built for zero mode, for .NET with lib/'s types and for
/// .NET with .NET's own (ZeroDotnetTypes=Net): the same source each time.
module Shapes

open System.Threading.Tasks

type Shape =
    | Circle of int
    | Square of int

let area shape =
    match shape with
    | Circle r -> 3 * r * r
    | Square s -> s * s

/// Each area worked out by Task.Run (a fiber), awaited in a task { }.
let totalArea (shapes: Shape list) : Task<int> =
    task {
        let mutable total = 0
        for s in shapes do
            let! a = Task.Run(fun () -> area s)
            do! Task.Delay 1
            total <- total + a
        return total
    }

/// lib/'s arenas, whose Destroy is unsafe: [<Safety>] says why it's used
/// correctly (zero's mark, on .NET too).
[<Safety "nothing allocated from the arena is used after Destroy">]
let arenaRoundTrip () =
    let arena = Arena.Create(4096un)
    let fresh = arena.Used = 0un
    arena.Destroy()
    fresh
