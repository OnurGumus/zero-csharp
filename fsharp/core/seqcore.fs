// What the compiler calls for sequence expressions and list and array
// comprehensions: the RuntimeHelpers it names, GeneratedSequenceBase (the
// base of the state machines it writes for seq { }), and ListCollector and
// ArrayCollector (which [ for ... ] and [| for ... |] fill). Adapted from
// dotnet/fsharp's seqcore.fs (MIT); zero mode has no catchable exceptions, so
// try/with in a sequence runs its body only.
#nowarn "35" "42" "44" "52" "3370"

namespace Microsoft.FSharp.Collections

    open System
    open System.Collections
    open System.Collections.Generic
    open Microsoft.FSharp.Core
    open Microsoft.FSharp.Core.Operators
    open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators

    module internal IEnumerator =

        let noReset () = raise (NotSupportedException("Reset is not supported on this enumerator."))
        let notStarted () = raise (InvalidOperationException("Enumeration has not started. Call MoveNext."))
        let alreadyFinished () = raise (InvalidOperationException("Enumeration already finished."))
        let check started = if not started then notStarted ()

        [<Sealed>]
        type EmptyEnumerator<'T>() =
            let mutable started = false
            interface IEnumerator<'T> with
                member _.Current : 'T =
                    check started
                    alreadyFinished ()
            interface IEnumerator with
                member _.Current : obj =
                    check started
                    alreadyFinished ()
                member _.MoveNext() =
                    started <- true
                    false
                member _.Reset() = noReset ()
            interface IDisposable with
                member _.Dispose() = ()

        let Empty<'T> () = (new EmptyEnumerator<'T>() :> IEnumerator<'T>)

        [<Sealed>]
        type GeneratedEnumerable<'T, 'State>(openf: unit -> 'State, compute: 'State -> 'T option, closef: 'State -> unit) =
            let mutable started = false
            let mutable curr = None
            let mutable state = Some(openf ())
            let getCurr () : 'T =
                check started
                match curr with
                | None -> alreadyFinished ()
                | Some x -> x
            let readAndClear () =
                match state with
                | None -> None
                | Some s ->
                    state <- None
                    Some s
            let dispose () = match readAndClear () with None -> () | Some s -> closef s
            let finish () =
                try dispose ()
                finally curr <- None
            interface IEnumerator<'T> with
                member _.Current = getCurr ()
            interface IEnumerator with
                member _.Current = box (getCurr ())
                member _.MoveNext() =
                    if not started then started <- true
                    match state with
                    | None -> false
                    | Some s ->
                        match compute s with
                        | None ->
                            finish ()
                            false
                        | Some _ as x ->
                            curr <- x
                            true
                member _.Reset() = noReset ()
            interface IDisposable with
                member _.Dispose() = dispose ()

        [<Sealed>]
        type Singleton<'T>(v: 'T) =
            let mutable started = false
            interface IEnumerator<'T> with
                member _.Current = v
            interface IEnumerator with
                member _.Current = box v
                member _.MoveNext() = if started then false else (started <- true; true)
                member _.Reset() = noReset ()
            interface IDisposable with
                member _.Dispose() = ()

        let Singleton x = (new Singleton<'T>(x) :> IEnumerator<'T>)

        let EnumerateThenFinally f (e: IEnumerator<'T>) =
            { new IEnumerator<'T> with
                member _.Current = e.Current
              interface IEnumerator with
                member _.Current = (e :> IEnumerator).Current
                member _.MoveNext() = e.MoveNext()
                member _.Reset() = noReset ()
              interface IDisposable with
                member _.Dispose() =
                    try e.Dispose()
                    finally f () }

        let mkSeq (f: unit -> IEnumerator<'T>) =
            { new IEnumerable<'T> with
                member _.GetEnumerator() = f ()
              interface IEnumerable with
                member _.GetEnumerator() = (f () :> IEnumerator) }

namespace Microsoft.FSharp.Core.CompilerServices

    open System
    open System.Collections
    open System.Collections.Generic
    open Microsoft.FSharp.Core
    open Microsoft.FSharp.Core.Operators
    open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators
    open Microsoft.FSharp.Collections
    open Microsoft.FSharp.Collections.IEnumerator

    module RuntimeHelpers =

        /// What fsc calls for an event read as a value: its add and remove
        /// methods, and how to make a handler of its delegate type from a
        /// function.
        [<CompiledName("CreateEvent")>]
        let CreateEvent (addHandler: 'Delegate -> unit) (removeHandler: 'Delegate -> unit) (createHandler: (obj -> 'Args -> unit) -> 'Delegate) : Microsoft.FSharp.Control.IEvent<'Delegate, 'Args> =
            { new Microsoft.FSharp.Control.IEvent<'Delegate, 'Args> with
                member _.AddHandler handler = addHandler handler
                member _.RemoveHandler handler = removeHandler handler
              interface IObservable<'Args> with
                member _.Subscribe(observer: IObserver<'Args>) =
                    let handler = createHandler (fun _ args -> observer.OnNext args)
                    addHandler handler
                    { new IDisposable with
                        member _.Dispose() = removeHandler handler } }

        let Generate openf compute closef =
            mkSeq (fun () -> new GeneratedEnumerable<_, _>(openf, compute, closef) :> IEnumerator<'T>)

        let EnumerateFromFunctions create moveNext current =
            Generate
                create
                (fun x -> if moveNext x then Some(current x) else None)
                (fun x -> match box x with :? IDisposable as id -> id.Dispose() | _ -> ())

        // seq { use x = ...; while ... }: the while loop's enumerator takes the
        // use's disposal as one more finally action, rather than a two-deep chain.
        type IFinallyEnumerator =
            abstract AppendFinallyAction: (unit -> unit) -> unit

        [<Sealed>]
        type FinallyEnumerable<'T>(compensation: unit -> unit, restf: unit -> seq<'T>) =
            interface IEnumerable<'T> with
                member _.GetEnumerator() =
                    let ie = restf().GetEnumerator()
                    match ie with
                    | :? IFinallyEnumerator as a ->
                        a.AppendFinallyAction(compensation)
                        ie
                    | _ -> IEnumerator.EnumerateThenFinally compensation ie
            interface IEnumerable with
                member x.GetEnumerator() = ((x :> IEnumerable<'T>).GetEnumerator() :> IEnumerator)

        [<Sealed>]
        type ConcatEnumerator<'T, 'U when 'U :> seq<'T>>(sources: seq<'U>) =
            let mutable outerEnum = sources.GetEnumerator()
            let mutable currInnerEnum = IEnumerator.Empty()
            let mutable started = false
            let mutable finished = false
            let mutable compensations = []

            [<DefaultValue(false)>]
            val mutable private currElement: 'T

            member _.Finish() =
                finished <- true
                try
                    match currInnerEnum with
                    | null -> ()
                    | _ ->
                        try currInnerEnum.Dispose()
                        finally currInnerEnum <- null
                finally
                    try
                        match outerEnum with
                        | null -> ()
                        | _ ->
                            try outerEnum.Dispose()
                            finally outerEnum <- null
                    finally
                        let rec iter comps =
                            match comps with
                            | [] -> ()
                            | h :: t ->
                                try h ()
                                finally iter t
                        try
                            let mutable reversed = []
                            for c in compensations do reversed <- c :: reversed
                            iter reversed
                        finally
                            compensations <- []

            member x.GetCurrent() =
                IEnumerator.check started
                if finished then IEnumerator.alreadyFinished () else x.currElement

            interface IFinallyEnumerator with
                member _.AppendFinallyAction(f) = compensations <- f :: compensations

            interface IEnumerator<'T> with
                member x.Current = x.GetCurrent()

            interface IEnumerator with
                member x.Current = box (x.GetCurrent())
                member x.MoveNext() =
                    if not started then started <- true
                    if finished then false
                    else
                        let rec takeInner () =
                            if currInnerEnum.MoveNext() then
                                x.currElement <- currInnerEnum.Current
                                true
                            else
                                let rec takeOuter () =
                                    if outerEnum.MoveNext() then
                                        let ie = outerEnum.Current
                                        match box ie with
                                        | null -> raise (ArgumentNullException("source"))
                                        | _ -> ()
                                        currInnerEnum <- ie.GetEnumerator()
                                        takeInner ()
                                    else
                                        x.Finish()
                                        false
                                currInnerEnum.Dispose()
                                currInnerEnum <- null
                                takeOuter ()
                        takeInner ()
                member _.Reset() = IEnumerator.noReset ()

            interface IDisposable with
                member x.Dispose() = if not finished then x.Finish()

        let EnumerateUsing (resource: 'T :> IDisposable) (source: 'T -> #seq<'U>) =
            (FinallyEnumerable((fun () -> match box resource with null -> () | _ -> resource.Dispose()),
                               (fun () -> source resource :> seq<_>)) :> seq<_>)

        let mkConcatSeq (sources: seq<'U :> seq<'T>>) =
            mkSeq (fun () -> new ConcatEnumerator<_, _>(sources) :> IEnumerator<'T>)

        let EnumerateWhile (guard: unit -> bool) (source: seq<'T>) : seq<'T> =
            let mutable started = false
            let mutable curr = None
            let getCurr () =
                IEnumerator.check started
                match curr with None -> IEnumerator.alreadyFinished () | Some x -> x
            let start () = if not started then started <- true
            let finish () = curr <- None
            mkConcatSeq
                (mkSeq (fun () ->
                    { new IEnumerator<_> with
                        member _.Current = getCurr ()
                      interface IEnumerator with
                        member _.Current = box (getCurr ())
                        member _.MoveNext() =
                            start ()
                            let keepGoing = guard ()
                            if keepGoing then curr <- Some source
                            else finish ()
                            keepGoing
                        member _.Reset() = IEnumerator.noReset ()
                      interface IDisposable with
                        member _.Dispose() = () }))

        let EnumerateThenFinally (source: seq<'T>) (compensation: unit -> unit) =
            (FinallyEnumerable(compensation, (fun () -> source)) :> seq<_>)

        // Nothing can be caught in zero mode: the handler never runs.
        let EnumerateTryWith (source: seq<'T>) (exceptionFilter: exn -> int) (exceptionHandler: exn -> seq<'T>) : seq<'T> =
            ignore exceptionFilter
            ignore exceptionHandler
            source

        let inline SetFreshConsTail cons tail = cons.( :: ).1 <- tail

        let inline FreshConsNoTail head = head :: (# "ldnull" : 'T list #)

    [<AbstractClass>]
    type GeneratedSequenceBase<'T>() =
        let mutable redirectTo: GeneratedSequenceBase<'T> = Unchecked.defaultof<_>
        let mutable redirect: bool = false

        abstract GetFreshEnumerator: unit -> IEnumerator<'T>
        abstract GenerateNext: result: byref<IEnumerable<'T>> -> int // 0 = Stop, 1 = Yield, 2 = Goto
        abstract Close: unit -> unit
        abstract CheckClose: bool
        abstract LastGenerated: 'T

        member x.MoveNextImpl() =
            let active = if redirect then redirectTo else x
            let mutable target: IEnumerable<'T> = null
            match active.GenerateNext(&target) with
            | 1 -> true
            | 2 ->
                match target.GetEnumerator() with
                | :? GeneratedSequenceBase<'T> as g when not active.CheckClose -> redirectTo <- g
                | e ->
                    redirectTo <-
                        { new GeneratedSequenceBase<'T>() with
                            member x.GetFreshEnumerator() = e
                            member x.GenerateNext _ = if e.MoveNext() then 1 else 0
                            member x.Close() =
                                try e.Dispose()
                                finally active.Close()
                            member x.CheckClose = true
                            member x.LastGenerated = e.Current }
                redirect <- true
                x.MoveNextImpl()
            | _ -> false

        interface IEnumerable<'T> with
            member x.GetEnumerator() = x.GetFreshEnumerator()

        interface IEnumerable with
            member x.GetEnumerator() = (x.GetFreshEnumerator() :> IEnumerator)

        interface IEnumerator<'T> with
            member x.Current = if redirect then redirectTo.LastGenerated else x.LastGenerated

        interface IDisposable with
            member x.Dispose() = if redirect then redirectTo.Close() else x.Close()

        interface IEnumerator with
            member x.Current = box (if redirect then redirectTo.LastGenerated else x.LastGenerated)
            member x.MoveNext() = x.MoveNextImpl()
            member _.Reset() = raise (NotSupportedException())

    // [ for ... ] adds to the end of a list it builds by setting fresh conses'
    // tails, which only FSharp.Core may do.
    [<Struct; NoEquality; NoComparison>]
    type ListCollector<'T> =
        [<DefaultValue(false)>]
        val mutable Result: 'T list

        [<DefaultValue(false)>]
        val mutable LastCons: 'T list

        member this.Add(value: 'T) =
            match box this.Result with
            | null ->
                let ra = RuntimeHelpers.FreshConsNoTail value
                this.Result <- ra
                this.LastCons <- ra
            | _ ->
                let ra = RuntimeHelpers.FreshConsNoTail value
                RuntimeHelpers.SetFreshConsTail this.LastCons ra
                this.LastCons <- ra

        member this.AddMany(values: seq<'T>) =
            match values with
            | :? ('T array) as valuesAsArray ->
                for v in valuesAsArray do
                    this.Add v
            | :? ('T list) as valuesAsList ->
                for v in valuesAsList do
                    this.Add v
            | _ ->
                for v in values do
                    this.Add v

        // A final F# list is stitched onto the end, not copied.
        member this.AddManyAndClose(values: seq<'T>) =
            match values with
            | :? ('T list) as valuesAsList ->
                let res =
                    match box this.Result with
                    | null -> valuesAsList
                    | _ ->
                        RuntimeHelpers.SetFreshConsTail this.LastCons valuesAsList
                        this.Result
                this.Result <- Unchecked.defaultof<_>
                this.LastCons <- Unchecked.defaultof<_>
                res
            | _ ->
                this.AddMany values
                this.Close()

        member this.Close() =
            match box this.Result with
            | null -> []
            | _ ->
                RuntimeHelpers.SetFreshConsTail this.LastCons []
                let res = this.Result
                this.Result <- Unchecked.defaultof<_>
                this.LastCons <- Unchecked.defaultof<_>
                res

    // [| for ... |]: items go into a doubling buffer (zerocore has no
    // List<T>), copied to an array of the exact size at the end.
    [<Struct; NoEquality; NoComparison>]
    type ArrayCollector<'T> =
        [<DefaultValue(false)>]
        val mutable Items: 'T array

        [<DefaultValue(false)>]
        val mutable Count: int

        member this.Add(value: 'T) =
            if isNull (box this.Items) then
                this.Items <- (# "newarr !0" type ('T) 4 : 'T array #)
            elif this.Count = this.Items.Length then
                let bigger = (# "newarr !0" type ('T) (this.Count * 2) : 'T array #)
                for i in 0 .. this.Count - 1 do bigger.[i] <- this.Items.[i]
                this.Items <- bigger
            this.Items.[this.Count] <- value
            this.Count <- this.Count + 1

        member this.AddMany(values: seq<'T>) =
            match values with
            | :? ('T array) as valuesAsArray ->
                for v in valuesAsArray do
                    this.Add v
            | :? ('T list) as valuesAsList ->
                for v in valuesAsList do
                    this.Add v
            | _ ->
                for v in values do
                    this.Add v

        member this.AddManyAndClose(values: seq<'T>) =
            this.AddMany(values)
            this.Close()

        member this.Close() =
            let res = (# "newarr !0" type ('T) this.Count : 'T array #)
            for i in 0 .. this.Count - 1 do res.[i] <- this.Items.[i]
            this.Items <- Unchecked.defaultof<_>
            this.Count <- 0
            res
