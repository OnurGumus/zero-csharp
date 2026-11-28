// Format strings: their types, the formatting itself, and sprintf for
// interpolated strings ($"x = {x}", $"%d{x}"), which pass their values with
// the format (captures) and give a string.
//
// printf, sprintf and the others for format strings with arguments are in
// fsharp/printf/ (FSharp.Printf.dll): see there.
#nowarn "9" "35" "42" "44" "64" "77" "1204" "3370"

namespace Microsoft.FSharp.Core

    open System
    open Microsoft.FSharp.Core
    open Microsoft.FSharp.Core.Operators
    open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators
    open Microsoft.FSharp.Collections

    /// A format string, typed by the compiler from its % specifiers.
    type PrintfFormat<'Printer, 'State, 'Residue, 'Result>(value: string, captures: obj array, captureTys: Type array) =
        new(value: string) = PrintfFormat<'Printer, 'State, 'Residue, 'Result>(value, null, null)
        member _.Value = value
        member _.Captures = captures
        member _.CaptureTypes = captureTys
        override _.ToString() = value

    type PrintfFormat<'Printer, 'State, 'Residue, 'Result, 'Tuple>(value: string, captures: obj array, captureTys: Type array) =
        inherit PrintfFormat<'Printer, 'State, 'Residue, 'Result>(value, captures, captureTys)
        new(value: string) = PrintfFormat<'Printer, 'State, 'Residue, 'Result, 'Tuple>(value, null, null)

    type Format<'Printer, 'State, 'Residue, 'Result> = PrintfFormat<'Printer, 'State, 'Residue, 'Result>

    type Format<'Printer, 'State, 'Residue, 'Result, 'Tuple> = PrintfFormat<'Printer, 'State, 'Residue, 'Result, 'Tuple>

    /// Formats values by a format string: what printf and the others share.
    module PrintfImpl =

        let private text (chars: char array) (count: int) =
            let b = System.Text.StringBuilder()
            for i in 0 .. count - 1 do b.Append(chars.[i]) |> ignore
            b.ToString()

        let private ofChar (c: char) = System.Text.StringBuilder().Append(c).ToString()

        /// The first index of c in s, or -1 (zerocore's string has no IndexOf).
        let indexOf (s: string) (c: char) =
            let mutable i = 0
            while i < s.Length && s.Chars(i) <> c do i <- i + 1
            if i < s.Length then i else -1

        /// The digits of a value in a radix (10, 16, 8 or 2).
        let digits (value: uint64) (radix: uint64) (upper: bool) =
            let chars = (Array.zeroCreate 64 : char array)
            let mutable v = value
            let mutable n = 0
            let mutable go = true
            while go do
                let d = int (v % radix)
                chars.[63 - n] <- (if d < 10 then char (int '0' + d) elif upper then char (int 'A' + d - 10) else char (int 'a' + d - 10))
                n <- n + 1
                v <- v / radix
                go <- v <> 0UL
            let b = System.Text.StringBuilder()
            for i in 64 - n .. 63 do b.Append(chars.[i]) |> ignore
            b.ToString()

        /// A boxed integer: whether it's negative, its magnitude, and its bits as
        /// unsigned (for %x, %o and %B, which show the two's complement).
        let private signed (v: int64) (bits: uint64) : struct (bool * uint64 * uint64) =
            let magnitude : uint64 = if v < 0L then 0UL - uint64 v else uint64 v
            struct (v < 0L, magnitude, bits)

        let private unsigned (v: uint64) : struct (bool * uint64 * uint64) = struct (false, v, v)

        let integer (value: obj) : struct (bool * uint64 * uint64) =
            match value with
            | :? int32 as v -> signed (int64 v) (uint64 (uint32 v))
            | :? int64 as v -> signed v (uint64 v)
            | :? int16 as v -> signed (int64 v) (uint64 (uint16 v))
            | :? sbyte as v -> signed (int64 v) (uint64 (byte v))
            | :? nativeint as v -> signed (int64 v) (uint64 (unativeint v))
            | :? byte as v -> unsigned (uint64 v)
            | :? uint16 as v -> unsigned (uint64 v)
            | :? uint32 as v -> unsigned (uint64 v)
            | :? uint64 as v -> unsigned v
            | :? unativeint as v -> unsigned (uint64 v)
            | :? char as v -> unsigned (uint64 v)
            | _ -> raise (ArgumentException("%d, %i, %u, %x, %o and %B take an integer"))

        let private pow10 (n: int) =
            let mutable p = 1.0
            for _ in 1 .. n do p <- p * 10.0
            p

        let private isSpecial (v: float) = v - v <> 0.0    // NaN and the infinities

        let private special (v: float) =
            if v > 0.0 then "Infinity" elif v < 0.0 then "-Infinity" else "NaN"

        /// m (finite, not negative) with p decimals, rounded half away from zero.
        let fixedDigits (m: float) (p: int) =
            let p = if p > 15 then 15 else p
            let scaled = m * pow10 p
            if scaled >= 9.2e18 then
                // Too many digits for an integer: 17 significant digits, then
                // zeros (a double has no more).
                let mutable e = 0
                let mutable x = m
                while x >= 1e17 do x <- x / 10.0; e <- e + 1
                let b = System.Text.StringBuilder()
                b.Append(digits (uint64 (Math.Floor(x + 0.5))) 10UL false) |> ignore
                for _ in 1 .. e do b.Append('0') |> ignore
                if p > 0 then
                    b.Append('.') |> ignore
                    for _ in 1 .. p do b.Append('0') |> ignore
                b.ToString()
            else
                let s = digits (uint64 (Math.Floor(scaled + 0.5))) 10UL false
                if p = 0 then s
                else
                    let b = System.Text.StringBuilder()
                    let padded = s.Length <= p
                    if padded then
                        b.Append('0').Append('.') |> ignore
                        for _ in 1 .. p - s.Length do b.Append('0') |> ignore
                        b.Append(s) |> ignore
                    else
                        b.Append(s.Substring(0, s.Length - p)).Append('.').Append(s.Substring(s.Length - p)) |> ignore
                    b.ToString()

        /// m = mantissa * 10^exponent, 1 <= mantissa < 10 (m > 0, finite).
        let private decompose (m: float) =
            let mutable e = 0
            let mutable x = m
            while x >= 10.0 do x <- x / 10.0; e <- e + 1
            while x < 1.0 do x <- x * 10.0; e <- e - 1
            struct (x, e)

        let private exponentText (e: int) (upper: bool) (minDigits: int) =
            let b = System.Text.StringBuilder()
            b.Append(if upper then 'E' else 'e').Append(if e < 0 then '-' else '+') |> ignore
            let d = digits (uint64 (if e < 0 then -e else e)) 10UL false
            for _ in 1 .. minDigits - d.Length do b.Append('0') |> ignore
            b.Append(d).ToString()

        /// d.ddddde+XXX, as .NET's "e" format.
        let scientific (m: float) (p: int) (upper: bool) (minExponentDigits: int) =
            if m = 0.0 then fixedDigits 0.0 p + exponentText 0 upper minExponentDigits
            else
                let struct (x, e) = decompose m
                let mantissa = fixedDigits x p
                if mantissa.Length > 0 && mantissa.Chars(0) = '1' && mantissa.Length > 1 && mantissa.Chars(1) = '0' then
                    fixedDigits (x / 10.0) p + exponentText (e + 1) upper minExponentDigits
                else mantissa + exponentText e upper minExponentDigits

        let private trimZeros (s: string) =
            if indexOf s '.' < 0 then s
            else
                let mutable n = s.Length
                while n > 0 && s.Chars(n - 1) = '0' do n <- n - 1
                if n > 0 && s.Chars(n - 1) = '.' then n <- n - 1
                s.Substring(0, n)

        /// .NET's "G" format with p significant digits.
        let general (m: float) (p: int) (upper: bool) =
            let p = if p <= 0 then 1 else p
            if m = 0.0 then "0"
            else
                let struct (_, e) = decompose m
                if e < -5 || e >= p then
                    let s = scientific m (p - 1) upper 2
                    let cut = indexOf s (if upper then 'E' else 'e')
                    trimZeros (s.Substring(0, cut)) + s.Substring(cut)
                else trimZeros (fixedDigits m (p - 1 - e))

        let private toFloat (value: obj) =
            match value with
            | :? float as v -> v
            | :? float32 as v -> float v
            | :? decimal as v -> float v
            | _ -> raise (ArgumentException("%f, %e and %g take a float"))

        /// A decimal's digits (its ToString without the sign) with p
        /// decimals, rounded half away from zero, as .NET's "F" format does.
        let fixedDecimal (text: string) (p: int) =
            let point = indexOf text '.'
            let whole = if point < 0 then text else text.Substring(0, point)
            let fraction = if point < 0 then "" else text.Substring(point + 1)
            if fraction.Length <= p then
                let b = System.Text.StringBuilder()
                b.Append(whole) |> ignore
                if p > 0 then
                    b.Append('.').Append(fraction) |> ignore
                    for _ in 1 .. p - fraction.Length do b.Append('0') |> ignore
                b.ToString()
            else
                // Keep whole + p fraction digits, adding one at the last kept
                // digit when the first dropped one is 5 or more.
                let source = whole + fraction.Substring(0, p)
                let kept: char array = Array.zeroCreate source.Length
                for i in 0 .. source.Length - 1 do kept.[i] <- source.[i]
                let mutable i = kept.Length - 1
                let mutable carry = fraction.[p] >= '5'
                while carry && i >= 0 do
                    if kept.[i] = '9' then
                        kept.[i] <- '0'
                        i <- i - 1
                    else
                        kept.[i] <- char (int kept.[i] + 1)
                        carry <- false
                let b = System.Text.StringBuilder()
                if carry then b.Append('1') |> ignore
                let wholeLength = (if carry then 1 else 0) + kept.Length - p
                for j in 0 .. kept.Length - 1 do
                    if j + (if carry then 1 else 0) = wholeLength then b.Append('.') |> ignore
                    b.Append(kept.[j]) |> ignore
                b.ToString()

        /// The text of a value printf didn't make: its ToString, through
        /// `text` (FSharp.Printf runs a program's ToString outside its scratch
        /// scope, so what it keeps isn't freed).
        type ValueText = obj -> string

        /// %O and {x}: the value's ToString, "<null>" for null.
        let display (text: ValueText) (value: obj) =
            match value with
            | null -> "<null>"
            | v -> text v

        /// %A, without reflection: strings and characters quoted, floats with
        /// a decimal point; other values as %O.
        let structured (text: ValueText) (value: obj) =
            match value with
            | null -> "<null>"
            | :? string as s -> "\"" + s + "\""
            | :? char as c -> "'" + ofChar c + "'"
            | :? bool as b -> (if b then "true" else "false")
            | :? float as f ->
                let s = f.ToString()
                if isSpecial f || indexOf s '.' >= 0 || indexOf s 'E' >= 0 || indexOf s 'e' >= 0 then s else s + ".0"
            | v -> text v

        /// The text for one specifier.
        let formatOne (text: ValueText) (kind: char) (flags: string) (precision: int) (value: obj) =
            let sign (negative: bool) =
                if negative then "-" elif indexOf flags '+' >= 0 then "+" elif indexOf flags ' ' >= 0 then " " else ""
            match kind with
            | 'd' | 'i' ->
                let struct (negative, magnitude, _) = integer value
                struct (sign negative, digits magnitude 10UL false)
            | 'u' ->
                let struct (_, _, bits) = integer value
                struct ("", digits bits 10UL false)
            | 'x' | 'X' ->
                let struct (_, _, bits) = integer value
                struct ("", digits bits 16UL (kind = 'X'))
            | 'o' ->
                let struct (_, _, bits) = integer value
                struct ("", digits bits 8UL false)
            | 'B' ->
                let struct (_, _, bits) = integer value
                struct ("", digits bits 2UL false)
            | 'M' ->
                // The decimal's own digits, as .NET's "G" (precision ignored).
                let digits = display text value
                if digits.Length > 0 && digits.[0] = '-' then struct ("-", digits.Substring(1))
                else struct (sign false, digits)
            | 'f' | 'F' when (match value with :? decimal -> true | _ -> false) ->
                let shown = display text value
                let negative = shown.Length > 0 && shown.[0] = '-'
                let digits = if negative then shown.Substring(1) else shown
                struct (sign negative, fixedDecimal digits (if precision < 0 then 6 else precision))
            | 'f' | 'F' | 'e' | 'E' | 'g' | 'G' ->
                let v = toFloat value
                if isSpecial v then struct ("", special v)
                else
                    let negative = v < 0.0 || (v = 0.0 && 1.0 / v < 0.0)
                    let m = if v < 0.0 then -v else v
                    let body =
                        match kind with
                        | 'f' | 'F' -> fixedDigits m (if precision < 0 then 6 else precision)
                        | 'e' | 'E' -> scientific m (if precision < 0 then 6 else precision) (kind = 'E') 3
                        | _ -> general m (if precision < 0 then 6 else precision) (kind = 'G')
                    struct (sign negative, body)
            | 's' ->
                match value with
                | null -> struct ("", "")
                | :? string as s -> struct ("", if precision >= 0 && precision < s.Length then s.Substring(0, precision) else s)
                | v -> struct ("", text v)
            | 'c' ->
                match value with
                | :? char as c -> struct ("", ofChar c)
                | _ -> raise (ArgumentException("%c takes a char"))
            | 'b' ->
                match value with
                | :? bool as b -> struct ("", if b then "true" else "false")
                | _ -> raise (ArgumentException("%b takes a bool"))
            | 'O' -> struct ("", display text value)
            | 'A' -> struct ("", structured text value)
            | _ -> raise (NotSupportedException("%" + ofChar kind + " isn't supported in zero mode (no reflection)"))

        let private pad (b: System.Text.StringBuilder) (flags: string) (width: int) (struct (sign: string, body: string)) =
            let length = sign.Length + body.Length
            let fill = if width > length then width - length else 0
            if indexOf flags '-' >= 0 then
                b.Append(sign).Append(body) |> ignore
                for _ in 1 .. fill do b.Append(' ') |> ignore
            elif indexOf flags '0' >= 0 then
                b.Append(sign) |> ignore
                for _ in 1 .. fill do b.Append('0') |> ignore
                b.Append(body) |> ignore
            else
                for _ in 1 .. fill do b.Append(' ') |> ignore
                b.Append(sign).Append(body) |> ignore

        let private isDigit (c: char) = c >= '0' && c <= '9'

        /// The format with its values: args in order (a curried printf's), and
        /// captures for the %P() holes of an interpolated string.
        let render (text: ValueText) (format: string) (captures: obj array) (args: obj array) =
            let b = System.Text.StringBuilder()
            let mutable i = 0
            let mutable nextArg = 0
            let mutable nextCapture = 0
            let takeArg () =
                let v = args.[nextArg]
                nextArg <- nextArg + 1
                v
            let takeCapture () =
                let v = captures.[nextCapture]
                nextCapture <- nextCapture + 1
                v
            // A %P(...) hole at position p: its length, or 0.
            let hole (p: int) =
                if p + 2 < format.Length && format.Chars(p) = '%' && format.Chars(p + 1) = 'P' && format.Chars(p + 2) = '(' then
                    let mutable q = p + 3
                    while q < format.Length && format.Chars(q) <> ')' do q <- q + 1
                    q + 1 - p
                else 0
            while i < format.Length do
                let c = format.Chars(i)
                if c <> '%' then
                    b.Append(c) |> ignore
                    i <- i + 1
                elif i + 1 < format.Length && format.Chars(i + 1) = '%' then
                    b.Append('%') |> ignore
                    i <- i + 2
                else
                    let h = hole i
                    if h > 0 then
                        // {x}: its ToString, "" for null.
                        match takeCapture () with
                        | null -> ()
                        | v -> b.Append(text v) |> ignore
                        i <- i + h
                    else
                        i <- i + 1
                        let flagsStart = i
                        while i < format.Length && (let f = format.Chars(i) in f = '-' || f = '+' || f = '0' || f = ' ') do i <- i + 1
                        let flags = format.Substring(flagsStart, i - flagsStart)
                        let mutable width = 0
                        if i < format.Length && format.Chars(i) = '*' then
                            width <- (unbox (takeArg ()) : int)
                            i <- i + 1
                        else
                            while i < format.Length && isDigit (format.Chars(i)) do
                                width <- width * 10 + (int (format.Chars(i)) - int '0')
                                i <- i + 1
                        let mutable precision = -1
                        if i < format.Length && format.Chars(i) = '.' then
                            i <- i + 1
                            precision <- 0
                            if i < format.Length && format.Chars(i) = '*' then
                                precision <- (unbox (takeArg ()) : int)
                                i <- i + 1
                            else
                                while i < format.Length && isDigit (format.Chars(i)) do
                                    precision <- precision * 10 + (int (format.Chars(i)) - int '0')
                                    i <- i + 1
                        let kind = format.Chars(i)
                        i <- i + 1
                        // In an interpolated string the value follows as a hole.
                        let h = hole i
                        let value =
                            if h > 0 then
                                i <- i + h
                                takeCapture ()
                            else takeArg ()
                        pad b flags width (formatOne text kind flags precision value)
            b.ToString()

        /// A value's ToString, where no scratch scope is open (interpolated
        /// strings outside printf).
        let plainText : ValueText = fun v -> v.ToString()

    open PrintfImpl

    [<AutoOpen>]
    module ExtraTopLevelOperators =
        /// For interpolated strings, which the compiler makes into this call; the
        /// result is a string. The sprintf programs call is FSharp.Printf's,
        /// whose AutoOpen comes after this one.
        [<CompiledName("PrintFormatToStringThen")>]
        let sprintf (format: PrintfFormat<'T, unit, string, string>) : 'T =
            (unbox (box (PrintfImpl.render PrintfImpl.plainText format.Value format.Captures (Array.zeroCreate 0))) : 'T)

        [<CompiledName("CreateSet")>]
        let set (elements: seq<'T>) = Microsoft.FSharp.Collections.Set<'T>(elements)

        [<assembly: AutoOpen("Microsoft.FSharp")>]
        [<assembly: AutoOpen("Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators")>]
        [<assembly: AutoOpen("Microsoft.FSharp.Core")>]
        [<assembly: AutoOpen("Microsoft.FSharp.Collections")>]
        [<assembly: AutoOpen("Microsoft.FSharp.Control")>]
        do ()
