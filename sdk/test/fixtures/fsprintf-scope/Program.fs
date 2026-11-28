// printf formats in a scratch scope that it frees as the call ends
// (fsharp/printf/Printf.fs, Arena.OpenFormatScope). What the program's own
// code makes during a call must outlive it: a ToString that keeps what it
// makes, on the object or in a static, runs outside the scope. And what a
// call leaves on the heap is only what the compiler makes at the call site
// (the format, and closures), not the 2 KB formatting a line takes.
open System

Arena.DebugChecks <- true

// A ToString that keeps what it makes: a cache on the object, one in a
// static, and a nested sprintf making the text.
type Cached(n: int) =
    let mutable text: string = null
    static let mutable last: string = null
    static member Last = last
    override _.ToString() =
        if isNull text then
            text <- sprintf "cached %d" n
            last <- String('x', n)
        text

[<Struct>]
type Point =
    { X: int; Y: int }
    override p.ToString() = sprintf "(%d, %d)" p.X p.Y

type Throws() =
    override _.ToString() = failwith "ToString threw"

let c = Cached 3
printfn "%O | %s | %O" c (c.ToString()) c
printfn $"interpolated {c} and {{ {Cached 4} }}"
printfn "struct %O, %O" { X = 1; Y = 2 } { X = 3; Y = 4 }
// Scribbles over the scratch arena where the first calls formatted.
let mutable total = 0
for i in 1 .. 50 do
    total <- total + (sprintf "%d %s %f %O" i "abcdefghijklmnopqrstuvwxyz" 3.5 (Cached i)).Length
printfn "total %d" total
printfn "kept: %s / %d" (c.ToString()) Cached.Last.Length

// A throw from inside a ToString: Catch.Run puts the allocator and the
// scratch arena back, and printf goes on working.
match Catch.result (fun () -> printfn "%O" (Throws())) with
| Ok () -> printfn "no throw?"
| Error e -> printfn "caught: %s" e.Message
printfn "after: %d %s" 5 (sprintf "%s" "fine")

// docs:arena-sprintf
// A string to keep: made in an arena of yours, which frees it with the
// rest of what it holds, rather than on the heap for good.
let keep = Arena.Create(4096un)
let s = keep.Sprintf "%d + %d = %d" 2 3 5
printfn "%s (%d characters, in the arena)" s s.Length
// docs:end

// The heap: only what the call site makes (the format, and the function
// holding what printf needs), whatever the format does.
let heapPerCall (call: int -> string) =
    call 0 |> ignore
    let before = GC.GetTotalAllocatedBytes()
    for i in 1 .. 100 do
        call i |> ignore
    (GC.GetTotalAllocatedBytes() - before) / 100L
// Four values formatted and written (to standard error, which the test
// throws away): 2 KB of work a call before printf had its scope.
let printed = heapPerCall (fun i -> eprintfn "%5d|%-8s|%.3f|%x" i "item" 2.5 255; "")
printfn "printf leaves %s on the heap per call" (if printed <= 256L then "at most 256 bytes" else string printed)
let inArena = heapPerCall (fun i -> keep.Sprintf "%5d|%-8s|%.3f|%x" i "item" 2.5 255)
printfn "Sprintf leaves %s on the heap per call" (if inArena <= 48L then "only its format" else string inArena)

// Spans and new string, as in .NET.
let spans (chars: char[]) =
    let span = ReadOnlySpan<char>(chars, 1, 2)
    let numbers = ReadOnlySpan<int>([| 1; 2 |])
    printfn "span: %s, %s, %s" (span.ToString()) (Span<char>(chars).ToString()) (numbers.ToString())
    printfn "new string: %s %s %s %s" (String(span)) (String(chars)) (String(chars, 2, 2)) (String('-', 3))
spans [| 'a'; 'b'; 'c'; 'd' |]

// exit: flushes what's written, ends with the code.
printf "bye"
exit 3
