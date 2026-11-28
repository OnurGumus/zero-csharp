// Tutorial, step 1 in F#: print the quiz's questions with printfn.
// QuizStep1.cs is the same program in C#; it prints the same lines.
// Build and run: the SDK's tests (test fsharp_doc_samples) build it as an
// F# zero project's Program.fs.

// docs:numbers
[<Literal>]
let Questions = 6

let mutable seed = 2026u

// The same questions every run, so a file of answers fits them.
let next limit =
    seed <- seed * 1103515245u + 12345u
    int ((seed >>> 16) % uint limit)
// docs:end

// docs:main
printfn "%d questions:" Questions
for i in 1 .. Questions do
    let a = next 50
    let b = next 50
    // %2d pads to two characters, so the columns line up.
    printfn "Q%d: %2d + %2d = %3d" i a b (a + b)
// docs:end
