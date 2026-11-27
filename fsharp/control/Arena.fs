// arena frame { ... }: the body's objects go into the arena, and are freed
// when it ends.
//
//   let frame = Arena.Create(4096un, "frame")
//   let round n =
//       arena frame {
//           let evens = [ 1 .. 100 ] |> List.map (fun x -> x * n)
//           return List.sum evens
//       }
//
// It's Arena.OpenObjectScope with the rule that makes it safe checked by the
// compiler where it can be: what the body returns can't hold anything from
// the arena. `return` takes an unmanaged value (numbers, bools, chars, enums,
// structs of those), which is copied out, or a string, which the builder
// copies out of the arena (ObjectScope.CopyOut) before freeing it. Anything
// else is a compile error.
//
// What it can't check, as with OpenObjectScope: storing something made
// inside into something from outside (a ref cell, a collection, a mutable
// field, a module-level value). F# already keeps the body from assigning the
// caller's `let mutable` values, as for any closure. Arena.DebugChecks catches
// static fields left pointing into the arena. So, as OpenObjectScope is,
// `arena` is unsafe to call ([<RequiresUnsafe>]): the function using it says
// why with [<Safety "...">], or the build warns (ZERO007).
//
// The scope opens when `arena frame` is evaluated, before the body: so the
// closure fsc makes for the body when it doesn't optimize is in the arena
// too, rather than outside it on every call. (Optimized, the members are
// inlined and there's no closure.) Run closes it. So write `arena frame { }`
// in one piece: `arena frame` kept in a value opens a scope that only its
// first use closes, and a second use stops the program.
#nowarn "3370"

namespace Microsoft.FSharp.Control

    open System
    open System.Runtime.CompilerServices

    /// What an arena { } body ends with: a value that holds nothing from the
    /// arena (made by `return`).
    [<Struct; NoEquality; NoComparison>]
    type ArenaResult<'T> =
        val internal Value: 'T
        // A string to copy out of the arena before it's freed.
        val internal IsString: bool
        internal new(value, isString) = { Value = value; IsString = isString }

    /// Builds arena frame { ... } (see `arena`): holds the scope `arena frame`
    /// opened, for Run to close.
    [<Struct; NoEquality; NoComparison>]
    type ArenaBuilder =
        val internal Opened: Arena.OpenedObjects
        internal new(opened) = { Opened = opened }

        member _.Zero() : ArenaResult<unit> = ArenaResult((), false)

        /// A value with nothing from the arena in it: copied out.
        member _.Return(value: 'T) : ArenaResult<'T> when 'T: unmanaged = ArenaResult(value, false)

        /// A string: copied out of the arena before it's freed.
        member _.Return(text: string) : ArenaResult<string> = ArenaResult(text, true)

        member inline _.Delay([<InlineIfLambda>] body: unit -> ArenaResult<'T>) = body

        member inline _.Combine(_: ArenaResult<unit>, [<InlineIfLambda>] next: unit -> ArenaResult<'T>) = next ()

        member inline b.While([<InlineIfLambda>] condition: unit -> bool, [<InlineIfLambda>] body: unit -> ArenaResult<unit>) =
            while condition () do
                body () |> ignore
            b.Zero()

        member inline b.For(items: seq<'T>, [<InlineIfLambda>] body: 'T -> ArenaResult<unit>) =
            for item in items do
                body item |> ignore
            b.Zero()

        member inline _.TryFinally([<InlineIfLambda>] body: unit -> ArenaResult<'T>, [<InlineIfLambda>] compensation: unit -> unit) =
            try
                body ()
            finally
                compensation ()

        member inline _.Using(resource: 'R, [<InlineIfLambda>] body: 'R -> ArenaResult<'T>) : ArenaResult<'T> when 'R :> IDisposable =
            use r = resource
            body r

        member inline b.Run([<InlineIfLambda>] body: unit -> ArenaResult<'T>) : 'T =
            ArenaScope.Close(b, body ())

    /// Opens and closes arena { }'s scope: not inline, so a program's code
    /// doesn't call OpenObjectScope itself (the builder answers for its rule).
    and [<AbstractClass; Sealed>] ArenaScope =
        // Marked too: optimized fsc inlines `arena` into its callers, which
        // then call this.
        [<RequiresUnsafe>]
        static member Open(frame: Arena) = ArenaBuilder(frame.OpenObjectsForBuilder())

        /// Frees what the body made, after copying a returned string out.
        static member Close(builder: ArenaBuilder, result: ArenaResult<'T>) : 'T =
            ArenaScope.Close(builder.Opened, result)

        static member internal Close(opened: Arena.OpenedObjects, result: ArenaResult<'T>) : 'T =
            if Arena.DebugChecks then Mailboxes.checkBeforeFree opened
            use scope = Arena.ObjectsOpenedForBuilder opened
            if result.IsString then
                let text: string = unbox (box result.Value)
                let copy: 'T = unbox (box (scope.CopyOut text))
                copy
            else
                result.Value

    module internal ArenaAsync =
        // Frees the scope, then goes on. Its arguments are read before the
        // call, out of the closure that calls it, which is in the arena: a
        // local copy in the closure could be optimized back into a read after
        // the free.
        [<MethodImpl(MethodImplOptions.NoInlining)>]
        let closeThen (opened: Arena.OpenedObjects) (result: ArenaResult<'T>) (r: Runner) (scopedBefore: Arena.AllocatorSetting) (next: 'T -> unit) =
            let value = ArenaScope.Close(opened, result)
            r.Scoped <- scopedBefore
            next value

        // closeThen for a cancel: then cancels c.
        [<MethodImpl(MethodImplOptions.NoInlining)>]
        let closeThenCancel (opened: Arena.OpenedObjects) (r: Runner) (scopedBefore: Arena.AllocatorSetting) (c: AsyncContext) =
            ArenaScope.Close(opened, ArenaResult((), false))
            r.Scoped <- scopedBefore
            c.Cancel()

        // Runs computation with the objects it makes in frame, and frees them
        // when it ends (see Async.InArena).
        let inArena (frame: Arena) (computation: Async<'T>) (isString: bool) : Async<'T> =
            AsyncCode.make (fun c k ->
                let r = c.Runner
                let outerError = r.Error
                let outerCancel = c.OnCancel
                let outerScoped = r.Scoped
                let opened = frame.OpenObjectsForBuilder()
                r.Scoped <- Arena.CurrentAllocator()
                // From here to the end of the computation, each of its pieces
                // allocates in frame (Runner keeps the allocator across
                // waits). These closures too: each puts the runner back the
                // way it was, then copies what it needs into locals before
                // the scope, and the closure itself, is freed.
                r.Error <- (fun e ->
                    r.Error <- outerError
                    c.OnCancel <- outerCancel
                    // Frees nothing: the exception may be from the arena.
                    Arena.LeaveObjectsForBuilder opened
                    r.Scoped <- outerScoped
                    r.Fail e)
                c.OnCancel <- (fun () ->
                    r.Error <- outerError
                    c.OnCancel <- outerCancel
                    closeThenCancel opened r outerScoped c)
                r.Guard(fun () ->
                    computation.Invoke(c, fun v ->
                        r.Error <- outerError
                        c.OnCancel <- outerCancel
                        closeThen opened (ArenaResult(v, isString)) r outerScoped k)))

    /// Async.InArena.
    [<AutoOpen>]
    module AsyncArenaExtensions =
        type Async with
            /// Runs the computation with the objects it makes in `frame`, across
            /// its waits, and frees them when it ends: one scope per message
            /// in an agent. The result can't point into the arena: a number, a
            /// struct of numbers, or (another overload) a string, copied out.
            /// After a throw nothing is freed (the exception may be from the
            /// arena) until the arena is reset. Unsafe to call, as
            /// OpenObjectScope: nothing made inside may be stored into
            /// something from outside, posted messages included.
            [<RequiresUnsafe>]
            static member InArena(frame: Arena, computation: Async<'T>) : Async<'T> when 'T: unmanaged =
                ArenaAsync.inArena frame computation false

            /// InArena for a computation that returns a string: copied out.
            [<RequiresUnsafe>]
            static member InArena(frame: Arena, computation: Async<string>) : Async<string> =
                ArenaAsync.inArena frame computation true

            /// InArena for a computation that returns nothing.
            [<RequiresUnsafe>]
            static member InArena(frame: Arena, computation: Async<unit>) : Async<unit> =
                ArenaAsync.inArena frame computation false

    [<AutoOpen>]
    module ArenaBuilders =
        /// arena frame { ... }: the objects the body makes go into `frame`,
        /// and are freed when it ends. It returns a value with nothing from
        /// the arena in it: a number, a struct of numbers, or a string (which
        /// is copied out). Unsafe to call, as OpenObjectScope: nothing made
        /// inside may be stored into something from outside.
        [<RequiresUnsafe>]
        let arena (frame: Arena) = ArenaScope.Open frame
