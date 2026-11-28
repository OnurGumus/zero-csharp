// printf and friends: each line's text is what .NET's FSharp.Core prints
// (expected.txt), except where noted.
// docs:basics
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
// docs:end
printfn "%s" $"{1.5} {true}"
// .NET's alignment and format strings in the holes: %5P() and %P(F2) to fsc.
printfn $"[{42,5}] [{3.14159:F2}] [{-7,-4}] [{255:X4}] [{1234567:N0}]"
printfn "%s" $"{2.5m:F3}|{2.71828,8:F2}|{(Some 3),9}"
printfn "%f %f %b %b %f" infinity -infinity (System.Double.IsNaN nan) (System.Single.IsNaN nanf) (float infinityf)
printf "no newline "
printfn ""
let add = sprintf "%d+%d"
printfn "%s" (add 1 2)
printfn "%*d|%.*f|%%" 6 7 2 3.14159
// docs:wrapper
// A wrapper taking the format: inline, as printf's own functions are.
let inline count format =
    Printf.ksprintf (fun (text: string) -> text.Length) format
printfn "length %d" (count "%s%s" "ab" "cd")
let inline log format =
    Printf.ksprintf (fun (text: string) -> Out.Line("log: " + text)) format
log "%d items, %s" 3 "ok"
// docs:end
printfn "%d %d %d %d %d %d %d %d %d %d" 1 2 3 4 5 6 7 8 9 10
eprintfn "to standard error %d" 1
