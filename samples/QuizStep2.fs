// Tutorial, step 2 in F#: read an answer to each question with In.ReadLine.
// QuizStep2.cs is the same program in C#; it prints the same lines.
// Build and run: the SDK's tests (test fsharp_doc_samples) build it as an
// F# zero project's Program.fs.

open System

[<Literal>]
let Questions = 6

let mutable seed = 2026u

let next limit =
    seed <- seed * 1103515245u + 12345u
    int ((seed >>> 16) % uint limit)

// docs:buffer
// Each answer is read into this, made once: nothing is allocated per
// question. A span can't be a module-level value, so the array is.
let buffer: char[] = Array.zeroCreate 32
// docs:end

// docs:ask
// The answer to one question, or None at the end of the input.
let ask i a b =
    // printf shows the prompt at once, before the program waits.
    printf "Q%d: %d + %d = " i a b
    let length = In.ReadLine(Span<char>(buffer))
    if length < 0 then
        printfn ""
        printfn "(end of input)"
        None
    else
        Some(min length buffer.Length)
// docs:end

// docs:check
// Whether the answer in the buffer is a + b. Says which, as it checks.
let check length a b =
    let answer = ReadOnlySpan<char>(buffer, 0, length)
    let mutable value = 0L
    if Str.TryParse(answer.Trim(), &value) && value = int64 (a + b) then
        printfn "right"
        true
    else
        printfn "no, it's %d" (a + b)
        false

let rec quiz i right asked =
    if i > Questions then
        right, asked
    else
        let a = next 50
        let b = next 50
        match ask i a b with
        | None -> right, asked
        | Some length ->
            let gained = if check length a b then 1 else 0
            quiz (i + 1) (right + gained) (asked + 1)
// docs:end

let right, asked = quiz 1 0 0
printfn "%d of %d right" right asked
