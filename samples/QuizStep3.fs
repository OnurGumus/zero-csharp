// Tutorial, step 3 in F#: ask again when an answer isn't a number or doesn't
// fit, and let an empty answer skip a question.
// QuizStep3.cs is the same program in C#; it prints the same lines.
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

// docs:loop
type Outcome =
    | Right
    | Wrong
    | Skipped
    | EndOfInput
    | AskAgain

// Reads one answer and says what it was.
let read i a b =
    printf "Q%d: %d + %d = " i a b
    let length = In.ReadLine(Span<char>(buffer))
    if length < 0 then
        printfn ""
        printfn "(end of input)"
        EndOfInput
    // A line longer than the buffer returns its full length; the buffer
    // holds only its start, so ask again.
    elif length > buffer.Length then
        printf "that's %d characters; " length
        printfn "answers are at most %d" buffer.Length
        AskAgain
    else
        let answer = ReadOnlySpan<char>(buffer, 0, length).Trim()
        let mutable value = 0L
        if answer.Length = 0 then
            printfn "skipped"
            Skipped
        elif not (Str.TryParse(answer, &value)) then
            // A span isn't a printf argument; Console writes it as it is.
            Console.Write('"')
            Console.Write(answer)
            Console.WriteLine("\" isn't a number")
            AskAgain
        elif value = int64 (a + b) then
            printfn "right"
            Right
        else
            printfn "no, it's %d" (a + b)
            Wrong

// Asks until the answer is a number, empty, or the input ends.
let rec ask i a b =
    match read i a b with
    | AskAgain -> ask i a b
    | outcome -> outcome
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
        | Wrong | Skipped | AskAgain -> quiz (i + 1) right (asked + 1)

printfn "%d questions. An empty answer skips one." Questions
let right, asked = quiz 1 0 0
printfn "%d of %d right" right asked
