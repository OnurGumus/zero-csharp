// printf, sprintf, failwithf and the others, without reflection.
//
// FSharp.Core's printf makes the curried function a format's type asks for
// (int -> string -> string for "%d %s") at run time, from the type, with
// reflection. Here they're inline, and the function is made where they're
// called, from the type the compiler gave the format: an overload of
// PrintfBuilder.Make per number of arguments, which boxes them and formats
// (FSharp.Core's PrintfImpl). A program gets code only for the shapes it uses.
// A function wrapping them that takes the format as a parameter must be
// inline too.
#nowarn "9" "35" "42" "44" "64" "77" "3370"

namespace Microsoft.FSharp.Core

    open System
    open Microsoft.FSharp.Core
    open Microsoft.FSharp.Core.PrintfImpl

    /// Where printf's text goes that lib/'s Out doesn't take: standard error.
    module PrintfOutput =
        [<System.Runtime.InteropServices.DllImport("libc")>]
        extern nativeint write(int fd, byte* buffer, unativeint count)

        /// Standard error, as UTF-8, after what Out holds.
        let writeError (text: string) =
            Out.Flush()
            let bytes = (Array.zeroCreate (text.Length * 3 + 1) : byte array)
            let mutable n = 0
            let mutable i = 0
            while i < text.Length do
                let c = int (text.Chars(i))
                if c < 0x80 then
                    bytes.[n] <- byte c; n <- n + 1
                elif c < 0x800 then
                    bytes.[n] <- byte (0xC0 ||| (c >>> 6)); bytes.[n + 1] <- byte (0x80 ||| (c &&& 0x3F)); n <- n + 2
                elif c >= 0xD800 && c <= 0xDBFF && i + 1 < text.Length && int (text.Chars(i + 1)) >= 0xDC00 && int (text.Chars(i + 1)) <= 0xDFFF then
                    let cp = 0x10000 + ((c - 0xD800) <<< 10) + (int (text.Chars(i + 1)) - 0xDC00)
                    bytes.[n] <- byte (0xF0 ||| (cp >>> 18)); bytes.[n + 1] <- byte (0x80 ||| ((cp >>> 12) &&& 0x3F))
                    bytes.[n + 2] <- byte (0x80 ||| ((cp >>> 6) &&& 0x3F)); bytes.[n + 3] <- byte (0x80 ||| (cp &&& 0x3F))
                    n <- n + 4
                    i <- i + 1
                else
                    bytes.[n] <- byte (0xE0 ||| (c >>> 12)); bytes.[n + 1] <- byte (0x80 ||| ((c >>> 6) &&& 0x3F))
                    bytes.[n + 2] <- byte (0x80 ||| (c &&& 0x3F)); n <- n + 3
                i <- i + 1
            if n > 0 then
                use p = fixed bytes
                write(2, p, unativeint n) |> ignore

    /// Where a printf's text goes. Each is a struct type that PrintfSink
    /// carries as a type argument, so the compiler makes code only for the
    /// ones a program uses: printfn alone brings neither standard error's
    /// encoder nor arenas.
    type IPrintfTarget =
        /// Whether the text is copied out of the scope and given to the
        /// continuation (sprintf, ksprintf, failwithf).
        abstract CopiesOut: bool
        /// Writes the text, inside the scope; or returns it made in the
        /// arena (Arena.Sprintf). Unused when CopiesOut.
        abstract Write: text: string * arena: Arena -> string

    [<Struct; NoEquality; NoComparison>]
    type PrintfToOut =
        interface IPrintfTarget with
            member _.CopiesOut = false
            member _.Write(text, _) =
                Out.Write(text)
                Out.Flush()
                null

    [<Struct; NoEquality; NoComparison>]
    type PrintfToOutLine =
        interface IPrintfTarget with
            member _.CopiesOut = false
            member _.Write(text, _) =
                Out.Line(text)
                null

    [<Struct; NoEquality; NoComparison>]
    type PrintfToError =
        interface IPrintfTarget with
            member _.CopiesOut = false
            member _.Write(text, _) =
                PrintfOutput.writeError text
                null

    [<Struct; NoEquality; NoComparison>]
    type PrintfToErrorLine =
        interface IPrintfTarget with
            member _.CopiesOut = false
            member _.Write(text, _) =
                PrintfOutput.writeError text
                PrintfOutput.writeError "\n"
                null

    [<Struct; NoEquality; NoComparison>]
    type PrintfToString =
        interface IPrintfTarget with
            member _.CopiesOut = true
            member _.Write(_, _) = null

    [<Struct; NoEquality; NoComparison>]
    type PrintfToArena =
        interface IPrintfTarget with
            member _.CopiesOut = false
            member _.Write(text, arena) = Str.ToArenaString(arena, text)

    /// What a format's text becomes ('Target), with what that needs: the
    /// continuation for a copied-out string, the arena for Arena.Sprintf. A
    /// struct, so making one allocates nothing.
    [<Struct; NoEquality; NoComparison>]
    type PrintfSink<'Result, 'Target when 'Target :> IPrintfTarget and 'Target: struct> =
        val Format: string
        val Captures: obj array
        val Then: string -> 'Result
        val Arena: Arena
        new(format, captures, continuation, arena) =
            { Format = format; Captures = captures; Then = continuation; Arena = arena }

    /// The calls the printf functions end in, once they have every value.
    /// Each opens a format scope (Arena.OpenFormatScope): the values are
    /// boxed, formatted and written in a small buffer of the thread's, which
    /// the scope takes back as the call ends, so a printfn leaves nothing
    /// behind but what its call site made. Text that outlives the call is
    /// copied out; a value's ToString, which is the program's code, runs
    /// outside the scope, so what it keeps stays. Not inlined into the
    /// program, so a call site holds only the call.
    [<AbstractClass; Sealed>]
    type PrintfRun =
        /// A value's text: printf's own for numbers and the like (made
        /// inside the scope), the program's ToString for anything else.
        static member private Text(value: obj) : string =
            match value with
            | :? string as s -> s
            | :? int | :? int64 | :? int16 | :? sbyte | :? uint32 | :? uint64 | :? uint16 | :? byte
            | :? float | :? float32 | :? decimal | :? char | :? bool | :? nativeint | :? unativeint -> value.ToString()
            | _ -> Arena.ToStringOutsideFormatScope(value)

        static member val private ValueText: PrintfImpl.ValueText = (fun v -> PrintfRun.Text v)

        /// Formats inside the scope, then writes the text, or returns it:
        /// copied out, or made in the arena.
        static member private Deliver<'Target when 'Target :> IPrintfTarget and 'Target: struct>(scope: inref<Arena.FormatScope>, format: string, captures: obj array, arena: Arena, args: obj array) : string =
            let text = PrintfImpl.render PrintfRun.ValueText format captures args
            let mutable target = Unchecked.defaultof<'Target>
            if target.CopiesOut then scope.CopyOut(text) else target.Write(text, arena)

        static member private Finish(sink: PrintfSink<'Result, 'Target>, text: string) : 'Result =
            let mutable target = Unchecked.defaultof<'Target>
            if target.CopiesOut then sink.Then text else (unbox (box text) : 'Result)

        /// Keeps the closures of a printf with five or more values apart (see
        /// PrintfBuilder): a call the compiler can't see through.
        [<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
        static member Keep(value: 'T) : 'T = value

        [<NoCompilerInlining>]
        static member Run0(sink: PrintfSink<'Result, 'Target>) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, (Array.zeroCreate 0 : obj array))
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run1(sink: PrintfSink<'Result, 'Target>, a1: 'A1) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run2(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run3(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2, a3: 'A3) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2; box a3 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run4(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2, a3: 'A3, a4: 'A4) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2; box a3; box a4 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run5(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2, a3: 'A3, a4: 'A4, a5: 'A5) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2; box a3; box a4; box a5 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run6(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2, a3: 'A3, a4: 'A4, a5: 'A5, a6: 'A6) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2; box a3; box a4; box a5; box a6 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run7(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2, a3: 'A3, a4: 'A4, a5: 'A5, a6: 'A6, a7: 'A7) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2; box a3; box a4; box a5; box a6; box a7 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run8(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2, a3: 'A3, a4: 'A4, a5: 'A5, a6: 'A6, a7: 'A7, a8: 'A8) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2; box a3; box a4; box a5; box a6; box a7; box a8 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run9(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2, a3: 'A3, a4: 'A4, a5: 'A5, a6: 'A6, a7: 'A7, a8: 'A8, a9: 'A9) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2; box a3; box a4; box a5; box a6; box a7; box a8; box a9 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run10(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2, a3: 'A3, a4: 'A4, a5: 'A5, a6: 'A6, a7: 'A7, a8: 'A8, a9: 'A9, a10: 'A10) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2; box a3; box a4; box a5; box a6; box a7; box a8; box a9; box a10 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run11(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2, a3: 'A3, a4: 'A4, a5: 'A5, a6: 'A6, a7: 'A7, a8: 'A8, a9: 'A9, a10: 'A10, a11: 'A11) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2; box a3; box a4; box a5; box a6; box a7; box a8; box a9; box a10; box a11 |])
            PrintfRun.Finish(sink, text)

        [<NoCompilerInlining>]
        static member Run12(sink: PrintfSink<'Result, 'Target>, a1: 'A1, a2: 'A2, a3: 'A3, a4: 'A4, a5: 'A5, a6: 'A6, a7: 'A7, a8: 'A8, a9: 'A9, a10: 'A10, a11: 'A11, a12: 'A12) : 'Result =
            let text =
                use scope = Arena.OpenFormatScope()
                PrintfRun.Deliver<'Target>(&scope, sink.Format, sink.Captures, sink.Arena, [| box a1; box a2; box a3; box a4; box a5; box a6; box a7; box a8; box a9; box a10; box a11; box a12 |])
            PrintfRun.Finish(sink, text)

    /// Makes the function a format's type asks for (see the top of the file):
    /// one overload per number of arguments, each the whole function type, so
    /// the compiler picks one by unifying types; the sink's result type
    /// (string for sprintf) tells "no more arguments" apart. Up to 12.
    /// (A recursive overload, one argument per step, makes the compiler's
    /// work grow threefold with each argument.) The values stay as they are
    /// until the last one: PrintfRun boxes them inside its scratch scope.
    /// Called with every value at once, as printf usually is, the compiler
    /// applies the function where it's made, and no closure is created.
    // From five arguments the function is one closure per argument, with a
    // call between (PrintfRun.Keep) so the compiler doesn't merge them: a
    // merged one is a chain of OptimizedClosures (five arguments each), which
    // a call site that splits the arguments differently applies partially,
    // and bflat's compiler gives up on those partial applications' generic
    // types when they nest deeply (IL3054), with no type loader to make them
    // at run time.
    type PrintfBuilder =
        static member inline Make(_: PrintfBuilder, _: 'Result, sink: PrintfSink<'Result, 'Target>) : 'Result = PrintfRun.Run0(sink)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'Result =
            fun (a1: 'A1) -> PrintfRun.Run1(sink, a1)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'Result =
            fun (a1: 'A1) (a2: 'A2) -> PrintfRun.Run2(sink, a1, a2)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'A3 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'A3 -> 'Result =
            fun (a1: 'A1) (a2: 'A2) (a3: 'A3) -> PrintfRun.Run3(sink, a1, a2, a3)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'Result =
            fun (a1: 'A1) (a2: 'A2) (a3: 'A3) (a4: 'A4) -> PrintfRun.Run4(sink, a1, a2, a3, a4)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'Result =
            fun (a1: 'A1) ->
                let a1 = PrintfRun.Keep a1
                fun (a2: 'A2) ->
                    let a2 = PrintfRun.Keep a2
                    fun (a3: 'A3) ->
                        let a3 = PrintfRun.Keep a3
                        fun (a4: 'A4) ->
                            let a4 = PrintfRun.Keep a4
                            fun (a5: 'A5) -> PrintfRun.Run5(sink, a1, a2, a3, a4, a5)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'Result =
            fun (a1: 'A1) ->
                let a1 = PrintfRun.Keep a1
                fun (a2: 'A2) ->
                    let a2 = PrintfRun.Keep a2
                    fun (a3: 'A3) ->
                        let a3 = PrintfRun.Keep a3
                        fun (a4: 'A4) ->
                            let a4 = PrintfRun.Keep a4
                            fun (a5: 'A5) ->
                                let a5 = PrintfRun.Keep a5
                                fun (a6: 'A6) -> PrintfRun.Run6(sink, a1, a2, a3, a4, a5, a6)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'Result =
            fun (a1: 'A1) ->
                let a1 = PrintfRun.Keep a1
                fun (a2: 'A2) ->
                    let a2 = PrintfRun.Keep a2
                    fun (a3: 'A3) ->
                        let a3 = PrintfRun.Keep a3
                        fun (a4: 'A4) ->
                            let a4 = PrintfRun.Keep a4
                            fun (a5: 'A5) ->
                                let a5 = PrintfRun.Keep a5
                                fun (a6: 'A6) ->
                                    let a6 = PrintfRun.Keep a6
                                    fun (a7: 'A7) -> PrintfRun.Run7(sink, a1, a2, a3, a4, a5, a6, a7)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'A8 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'A8 -> 'Result =
            fun (a1: 'A1) ->
                let a1 = PrintfRun.Keep a1
                fun (a2: 'A2) ->
                    let a2 = PrintfRun.Keep a2
                    fun (a3: 'A3) ->
                        let a3 = PrintfRun.Keep a3
                        fun (a4: 'A4) ->
                            let a4 = PrintfRun.Keep a4
                            fun (a5: 'A5) ->
                                let a5 = PrintfRun.Keep a5
                                fun (a6: 'A6) ->
                                    let a6 = PrintfRun.Keep a6
                                    fun (a7: 'A7) ->
                                        let a7 = PrintfRun.Keep a7
                                        fun (a8: 'A8) -> PrintfRun.Run8(sink, a1, a2, a3, a4, a5, a6, a7, a8)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'A8 -> 'A9 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'A8 -> 'A9 -> 'Result =
            fun (a1: 'A1) ->
                let a1 = PrintfRun.Keep a1
                fun (a2: 'A2) ->
                    let a2 = PrintfRun.Keep a2
                    fun (a3: 'A3) ->
                        let a3 = PrintfRun.Keep a3
                        fun (a4: 'A4) ->
                            let a4 = PrintfRun.Keep a4
                            fun (a5: 'A5) ->
                                let a5 = PrintfRun.Keep a5
                                fun (a6: 'A6) ->
                                    let a6 = PrintfRun.Keep a6
                                    fun (a7: 'A7) ->
                                        let a7 = PrintfRun.Keep a7
                                        fun (a8: 'A8) ->
                                            let a8 = PrintfRun.Keep a8
                                            fun (a9: 'A9) -> PrintfRun.Run9(sink, a1, a2, a3, a4, a5, a6, a7, a8, a9)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'A8 -> 'A9 -> 'A10 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'A8 -> 'A9 -> 'A10 -> 'Result =
            fun (a1: 'A1) ->
                let a1 = PrintfRun.Keep a1
                fun (a2: 'A2) ->
                    let a2 = PrintfRun.Keep a2
                    fun (a3: 'A3) ->
                        let a3 = PrintfRun.Keep a3
                        fun (a4: 'A4) ->
                            let a4 = PrintfRun.Keep a4
                            fun (a5: 'A5) ->
                                let a5 = PrintfRun.Keep a5
                                fun (a6: 'A6) ->
                                    let a6 = PrintfRun.Keep a6
                                    fun (a7: 'A7) ->
                                        let a7 = PrintfRun.Keep a7
                                        fun (a8: 'A8) ->
                                            let a8 = PrintfRun.Keep a8
                                            fun (a9: 'A9) ->
                                                let a9 = PrintfRun.Keep a9
                                                fun (a10: 'A10) -> PrintfRun.Run10(sink, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'A8 -> 'A9 -> 'A10 -> 'A11 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'A8 -> 'A9 -> 'A10 -> 'A11 -> 'Result =
            fun (a1: 'A1) ->
                let a1 = PrintfRun.Keep a1
                fun (a2: 'A2) ->
                    let a2 = PrintfRun.Keep a2
                    fun (a3: 'A3) ->
                        let a3 = PrintfRun.Keep a3
                        fun (a4: 'A4) ->
                            let a4 = PrintfRun.Keep a4
                            fun (a5: 'A5) ->
                                let a5 = PrintfRun.Keep a5
                                fun (a6: 'A6) ->
                                    let a6 = PrintfRun.Keep a6
                                    fun (a7: 'A7) ->
                                        let a7 = PrintfRun.Keep a7
                                        fun (a8: 'A8) ->
                                            let a8 = PrintfRun.Keep a8
                                            fun (a9: 'A9) ->
                                                let a9 = PrintfRun.Keep a9
                                                fun (a10: 'A10) ->
                                                    let a10 = PrintfRun.Keep a10
                                                    fun (a11: 'A11) -> PrintfRun.Run11(sink, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11)

        static member inline Make(_: PrintfBuilder, _: 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'A8 -> 'A9 -> 'A10 -> 'A11 -> 'A12 -> 'Result, sink: PrintfSink<'Result, 'Target>) : 'A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> 'A8 -> 'A9 -> 'A10 -> 'A11 -> 'A12 -> 'Result =
            fun (a1: 'A1) ->
                let a1 = PrintfRun.Keep a1
                fun (a2: 'A2) ->
                    let a2 = PrintfRun.Keep a2
                    fun (a3: 'A3) ->
                        let a3 = PrintfRun.Keep a3
                        fun (a4: 'A4) ->
                            let a4 = PrintfRun.Keep a4
                            fun (a5: 'A5) ->
                                let a5 = PrintfRun.Keep a5
                                fun (a6: 'A6) ->
                                    let a6 = PrintfRun.Keep a6
                                    fun (a7: 'A7) ->
                                        let a7 = PrintfRun.Keep a7
                                        fun (a8: 'A8) ->
                                            let a8 = PrintfRun.Keep a8
                                            fun (a9: 'A9) ->
                                                let a9 = PrintfRun.Keep a9
                                                fun (a10: 'A10) ->
                                                    let a10 = PrintfRun.Keep a10
                                                    fun (a11: 'A11) ->
                                                        let a11 = PrintfRun.Keep a11
                                                        fun (a12: 'A12) -> PrintfRun.Run12(sink, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12)

        // ^U, the format's argument types as a tuple, is known only once the
        // compiler has read the format string: with it in the constraint, the
        // overload isn't picked before that (fsc first sees that the result is
        // applied to arguments, and would take the one-argument overload).
        // In FSharp.Core this crashes fsc 10 when a program's own inline
        // function wraps printf (FS0193 while writing its optimization data),
        // hence this library.
        static member inline Invoke(format: PrintfFormat< ^T, 'State, 'Residue, 'Result, ^U>, _target: 'Target, continuation: string -> 'Result, arena: Arena) : ^T =
            let sink = PrintfSink<'Result, 'Target>(format.Value, format.Captures, continuation, arena)
            let inline call (w: ^W) (t: ^T) (_: PrintfFormat< ^T, 'State, 'Residue, 'Result, ^U>) =
                ((^W or ^T or ^U): (static member Make: ^W * ^T * PrintfSink<'Result, 'Target> -> ^T) (w, t, sink))
            call Unchecked.defaultof<PrintfBuilder> Unchecked.defaultof< ^T> format

    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    module Printf =

        type StringFormat<'T, 'Result> = Format<'T, unit, string, 'Result>
        type StringFormat<'T> = StringFormat<'T, string>
        /// printf's format: zero mode has no TextWriter, so the state is unit.
        type TextWriterFormat<'T, 'Result> = Format<'T, unit, unit, 'Result>
        type TextWriterFormat<'T> = TextWriterFormat<'T, unit>

        /// Formats, then gives the text to the continuation.
        [<CompiledName("PrintFormatToStringThen")>]
        let inline ksprintf (continuation: string -> 'Result) (format: PrintfFormat< ^T, unit, string, 'Result, ^U>) : ^T =
            PrintfBuilder.Invoke(format, PrintfToString(), continuation, Unchecked.defaultof<Arena>)

        [<CompiledName("PrintFormatToStringThenFail")>]
        let inline failwithf (format: PrintfFormat< ^T, unit, string, 'Result, ^U>) : ^T =
            PrintfBuilder.Invoke(format, PrintfToString(), (fun (s: string) -> raise (Exception(s))), Unchecked.defaultof<Arena>)

        [<CompiledName("PrintFormatToStringThenIdentity")>]
        let inline sprintf (format: PrintfFormat< ^T, unit, string, string, ^U>) : ^T =
            PrintfBuilder.Invoke(format, PrintfToString(), (fun (s: string) -> s), Unchecked.defaultof<Arena>)

        let inline kprintf (continuation: string -> 'Result) (format: PrintfFormat< ^T, unit, string, 'Result, ^U>) : ^T =
            ksprintf continuation format

        /// Standard output, through lib/'s Out (so it keeps its order with
        /// Out's own writes), shown at once as in .NET: printf "Working... "
        /// is on the screen before the work starts.
        let writeOut (text: string) =
            Out.Write(text)
            Out.Flush()
        let writeOutLine (text: string) = Out.Line(text)

        /// Standard error, as UTF-8, after what Out holds.
        let writeError (text: string) = PrintfOutput.writeError text

        let writeErrorLine (text: string) = writeError (text + "\n")

        [<CompiledName("PrintFormatToConsole")>]
        let inline printf (format: PrintfFormat< ^T, unit, unit, unit, ^U>) : ^T =
            PrintfBuilder.Invoke(format, PrintfToOut(), Unchecked.defaultof<string -> unit>, Unchecked.defaultof<Arena>)

        [<CompiledName("PrintFormatLineToConsole")>]
        let inline printfn (format: PrintfFormat< ^T, unit, unit, unit, ^U>) : ^T =
            PrintfBuilder.Invoke(format, PrintfToOutLine(), Unchecked.defaultof<string -> unit>, Unchecked.defaultof<Arena>)

        [<CompiledName("PrintFormatToError")>]
        let inline eprintf (format: PrintfFormat< ^T, unit, unit, unit, ^U>) : ^T =
            PrintfBuilder.Invoke(format, PrintfToError(), Unchecked.defaultof<string -> unit>, Unchecked.defaultof<Arena>)

        [<CompiledName("PrintFormatLineToError")>]
        let inline eprintfn (format: PrintfFormat< ^T, unit, unit, unit, ^U>) : ^T =
            PrintfBuilder.Invoke(format, PrintfToErrorLine(), Unchecked.defaultof<string -> unit>, Unchecked.defaultof<Arena>)

    [<AutoOpen>]
    module ArenaPrintfExtensions =
        type Arena with
            /// sprintf, with the string made in this arena: it lives as long
            /// as the arena, rather than on the heap for good. F#'s
            /// Str.Format(arena, $"..."), whose handler only C# can use:
            ///   mistakes.Add(keep.Sprintf "%d + %d = %d" a b (a + b))
            /// A method, not a function taking the arena: the compiler
            /// inlines a method call whole, so a call leaves only its
            /// format object on the heap.
            member inline arena.Sprintf(format: PrintfFormat< ^T, unit, string, string, ^U>) : ^T =
                PrintfBuilder.Invoke(format, PrintfToArena(), Unchecked.defaultof<string -> string>, arena)

    [<AutoOpen>]
    module PrintfOperators =
        [<CompiledName("PrintFormatToStringThenIdentity")>]
        let inline sprintf (format: PrintfFormat< ^T, unit, string, string, ^U>) : ^T = Printf.sprintf format

        [<CompiledName("PrintFormatToStringThenFail")>]
        let inline failwithf (format: PrintfFormat< ^T, unit, string, 'Result, ^U>) : ^T = Printf.failwithf format

        [<CompiledName("PrintFormatToConsole")>]
        let inline printf (format: PrintfFormat< ^T, unit, unit, unit, ^U>) : ^T = Printf.printf format

        [<CompiledName("PrintFormatLineToConsole")>]
        let inline printfn (format: PrintfFormat< ^T, unit, unit, unit, ^U>) : ^T = Printf.printfn format

        [<CompiledName("PrintFormatToError")>]
        let inline eprintf (format: PrintfFormat< ^T, unit, unit, unit, ^U>) : ^T = Printf.eprintf format

        [<CompiledName("PrintFormatLineToError")>]
        let inline eprintfn (format: PrintfFormat< ^T, unit, unit, unit, ^U>) : ^T = Printf.eprintfn format

namespace Microsoft.FSharp.Core

    open System

    /// lib/'s Catch.Run (lib/Catch.cs) as F# would have it. A raise, failwith
    /// or any other throw inside comes back as Error; nothing between it and
    /// here runs on the way, not even try/with or try/finally, but files and
    /// sockets opened inside and left open are closed.
    ///
    ///   match Catch.result (fun () -> download url path) with
    ///   | Ok size -> printfn "%d bytes" size
    ///   | Error (:? IOException as e) -> printfn "couldn't: %s" e.Message
    ///   | Error e -> raise e
    ///
    /// Catch.Run itself works from F# as well: it returns the exception, or
    /// null.
    [<RequireQualifiedAccess>]
    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    module Catch =
        /// Runs f: Ok with what it returns, or Error with what it throws.
        [<CompiledName("Result")>]
        let result (f: unit -> 'T) : Result<'T, exn> =
            let mutable value = Unchecked.defaultof<'T>
            match global.Catch.Run(Func<'T>(f), &value) with
            | null -> Ok value
            | e -> Error e

namespace Microsoft.FSharp
    // After FSharp.Core's: these printf functions are the ones programs see.
    // The namespace too, for Printf.ksprintf: FSharp.Core's opens only its own
    // part of it.
    [<assembly: AutoOpen("Microsoft.FSharp.Core")>]
    [<assembly: AutoOpen("Microsoft.FSharp.Core.PrintfOperators")>]
    do ()
