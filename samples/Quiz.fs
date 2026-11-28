// Tutorial, step 4 in F#: keep the mistakes in an arena to list them at the
// end. Quiz.cs is the same program in C#; it prints the same lines.
// Build and run: the SDK's tests (test fsharp_doc_samples) build it as an
// F# zero project's Program.fs.

open System

[<Literal>]
let Questions = 6

let mutable seed = 2026u

let next limit =
    seed <- seed * 1103515245u + 12345u
    int ((seed >>> 16) % uint limit)

let buffer: char[] = Array.zeroCreate 32

type Outcome =
    | Right
    | Wrong of answer: int64
    | Skipped
    | EndOfInput

// Asks until the answer is a number, empty, or the input ends.
let rec ask i a b =
    printf "Q%d: %d + %d = " i a b
    let length = In.ReadLine(Span<char>(buffer))
    if length < 0 then
        printfn ""
        printfn "(end of input)"
        EndOfInput
    elif length > buffer.Length then
        printfn "that's %d characters; answers are at most %d" length buffer.Length
        ask i a b
    else
        let answer = ReadOnlySpan<char>(buffer, 0, length).Trim()
        let mutable value = 0L
        if answer.Length = 0 then
            printfn "skipped"
            Skipped
        elif not (Str.TryParse(answer, &value)) then
            printfn "\"%s\" isn't a number" (answer.ToString())
            ask i a b
        elif value = int64 (a + b) then
            printfn "right"
            Right
        else
            printfn "no, it's %d" (a + b)
            Wrong value

// docs:keep
// Lives until the end: the mistakes are kept here, the list and its strings.
let keep = Arena.Create(4096un)
let mistakes = RefList<string>.Create(keep)
if not mistakes.IsCreated then
    printfn "Arena.Create failed"
    exit 1
// docs:end

// docs:record
// Kept until the end, so the line is made in `keep`, not on the heap for
// good (sprintf's) or in the buffer the next answer overwrites.
let remember a b (answer: int64) =
    let line = keep.Sprintf "  %d + %d = %d, not %d" a b (a + b) answer
    mistakes.Add(line) |> ignore
// docs:end

let rec quiz i right asked =
    if i > Questions then
        right, asked
    else
        let a = next 50
        let b = next 50
        match ask i a b with
        | EndOfInput -> right, asked
        | Right -> quiz (i + 1) (right + 1) (asked + 1)
        | Wrong answer ->
            remember a b answer
            quiz (i + 1) right (asked + 1)
        | Skipped -> quiz (i + 1) right (asked + 1)

printfn "%d questions. An empty answer skips one." Questions
let right, asked = quiz 1 0 0
// docs:report
printfn "%d of %d right" right asked
if mistakes.Count > 0 then
    printfn "Mistakes:"
    for mistake in mistakes do
        printfn "%s" mistake
// docs:end
