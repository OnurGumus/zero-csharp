// Test: Parse and TryParse for the numbers in F#, the parts
// docs/how-to/read-input.md shows next to Parsing.cs's. Parsing.cs checks
// far more. Exit code = number of failed checks. The SDK's tests
// (fsharp_doc_samples) build it as an F# zero project's Program.fs and compare
// what it prints with tests/expected/Parsing.fs.txt.

open System
open System.Globalization

let mutable failures = 0
let check (name: string) ok =
    if not ok then
        failures <- failures + 1
        printfn "  failed: %s" name

// docs:integers
let answer = Int32.Parse("42")
check "Int32.Parse" (answer = 42)
check "whitespace and a sign" (Int32.Parse(" -17 ") = -17)
check "thousands" (Int32.Parse("1,234,567", NumberStyles.Number) = 1234567)
check "hex" (Int32.Parse("ff", NumberStyles.HexNumber) = 255)
check "hex is the bits" (SByte.Parse("FF", NumberStyles.HexNumber) = -1y)

// TryParse's out value comes back with the result, as a pair.
check "TryParse"
    (match Int32.TryParse("12345") with
     | true, parsed -> parsed = 12345
     | false, _ -> false)
check "not a number" (Int32.TryParse("12a") = (false, 0))
check "too large" (not (fst (Int32.TryParse("2147483648"))))
check "null" (not (fst (Int32.TryParse(null: string))))
// docs:end

// docs:doubles
let pi = Double.Parse("3.14159")
check "Double.Parse" (pi = 3.14159)
check "exponent" (Double.Parse("6.02e23") = 6.02e23)
check "thousands by default" (Double.Parse("1,000.5") = 1000.5)
check "too large is Infinity" (Double.Parse("1e400") = infinity)
check "too small is 0" (Double.Parse("1e-400") = 0.0)
check "symbols"
    (Double.IsNaN(Double.Parse("NaN"))
     && Double.Parse("-Infinity") = -infinity)
check "TryParse" (Double.TryParse("2.5") = (true, 2.5))
check "not a number" (not (fst (Double.TryParse("2.5.1"))))
// docs:end

printfn "%s" (if failures = 0 then "All checks passed." else sprintf "%d checks FAILED." failures)
exit failures
