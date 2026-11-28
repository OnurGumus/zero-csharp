// Test: char's methods and System.Text.Encoding in F#, the parts
// docs/concepts/strings.md shows next to Unicode.cs's. Unicode.cs checks far
// more. Exit code = number of failed checks. The SDK's tests
// (fsharp_doc_samples) build it as an F# zero project's Program.fs and compare
// what it prints with tests/expected/Unicode.fs.txt.

open System
open System.Globalization
open System.Text

let mutable failures = 0
let check (name: string) ok =
    if not ok then
        failures <- failures + 1
        printfn "  failed: %s" name

// docs:char-methods
check "letters in any script"
    (Char.IsLetter 'é' && Char.IsLetter 'ж' && Char.IsLetter '中')
check "digits in any script"
    (Char.IsDigit '٣' && Char.GetNumericValue '٣' = 3.0)
check "numbers that aren't digits"
    (Char.IsNumber '½' && not (Char.IsDigit '½')
     && Char.GetNumericValue '½' = 0.5 && Char.GetNumericValue 'Ⅻ' = 12.0)
check "case"
    (Char.ToUpper 'ж' = 'Ж' && Char.ToLower 'Σ' = 'σ' && Char.IsUpper 'Σ')
check "invariant case: one char to one"
    (Char.ToUpper 'ß' = 'ß' && Char.ToUpper 'ı' = 'ı')
check "categories"
    (Char.GetUnicodeCategory '€' = UnicodeCategory.CurrencySymbol
     && Char.IsPunctuation '¿' && Char.IsSymbol '+'
     && Char.IsWhiteSpace ' ')
// docs:end

// docs:encoding
let text = "naïve ☕ 😀"
let utf8 = Encoding.UTF8.GetBytes(text) // allocated
check "UTF-8 bytes"
    (utf8.Length = 15 && Encoding.UTF8.GetString(utf8) = text)

// Into buffers made once: the span overloads allocate nothing.
let bytes: byte[] = Array.zeroCreate 64
let chars: char[] = Array.zeroCreate 64
let written = Encoding.UTF8.GetBytes(text, Span<byte>(bytes))
let read =
    Encoding.UTF8.GetChars(
        ReadOnlySpan<byte>(bytes, 0, written), Span<char>(chars))
check "into a span and back"
    (written = 15 && read = text.Length
     && Encoding.UTF8.GetByteCount(text) = 15)

// What doesn't fit becomes '?' in ASCII, a close letter in Latin-1.
let ascii = Encoding.ASCII.GetBytes("naïve")
check "ASCII" (Encoding.ASCII.GetString(ascii) = "na?ve")
let latin1 = Encoding.Latin1.GetBytes("Āb")
check "Latin-1" (Encoding.Latin1.GetString(latin1) = "Ab")
// docs:end

printfn "%s" (if failures = 0 then "All checks passed." else sprintf "%d checks FAILED." failures)
exit failures
