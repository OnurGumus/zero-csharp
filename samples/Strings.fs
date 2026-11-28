// Test: strings and spans in F#, the parts docs/concepts/strings.md shows
// next to Strings.cs's. Strings.cs checks far more. Exit code = number of
// failed checks. The SDK's tests (fsharp_doc_samples) build it as an F# zero
// project's Program.fs and compare what it prints with tests/expected/Strings.fs.txt.
#nowarn "9" // reads a string's characters through a pointer, to see its terminator

open System
open Microsoft.FSharp.NativeInterop

let mutable failures = 0
let check (name: string) ok =
    if not ok then
        failures <- failures + 1
        printfn "  failed: %s" name

// docs:split
let mutable parts = 0
for part in "x,y,z".Split(',') do
    parts <- parts + 1
check "Split count" (parts = 3)
// docs:end

// docs:table
printfn "%-8s|%5s|%9s" "item" "qty" "price"
printfn "%-8s|%5d|%9.2f" "apple" 3 1.25
printfn "%-8s|%5d|%9.2f" "melon" 12 0.5
// docs:end

let arena = Arena.Create(4096un)

let terminator (s: string) =
    use p = fixed s
    NativePtr.get p s.Length

// docs:real-string
let s = arena.Sprintf "n=%d" 5
check "Length" (s.Length = 3)
check "indexer" (s[0] = 'n' && s[2] = '5')
check "null-terminated like a literal" (terminator s = '\000')
// docs:end

// docs:scratch
// Build in scratch memory and copy only the result out: the draft goes
// when the callback returns, the copy stays in `arena`.
let numbersInto =
    ScratchFuncIn<Arena, string>(fun scratch keep ->
        let draft = scratch.Text()
        for i in 0 .. 49 do
            draft.Append(i)
            draft.Append(' ')
        draft.ToArenaString(keep))
let kept = Arena.Scratch(arena, numbersInto)
// docs:end
check "scratch text kept" (kept.StartsWith("0 1 2 ") && kept.EndsWith("49 "))

printfn "%s" (if failures = 0 then "All checks passed." else sprintf "%d checks FAILED." failures)
exit failures
