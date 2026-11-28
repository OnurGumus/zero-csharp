/// An F# library whose functions return lib/'s tasks: on .NET the program
/// that uses it must see the same Task type (one shared lib/ assembly).
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
            total <- total + a
        return total
    }
