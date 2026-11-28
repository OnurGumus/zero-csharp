// F#'s view of .NET events: an event read as a value (timer.Elapsed) is an
// IEvent, which fsc makes with RuntimeHelpers.CreateEvent (seqcore.fs) from
// the event's add and remove methods. Add and Subscribe take a function.
#nowarn "35" "42" "44" "3370"

namespace Microsoft.FSharp.Control

    open System
    open Microsoft.FSharp.Core

    /// An event's add and remove, for handlers of its delegate type.
    type IDelegateEvent<'Delegate when 'Delegate :> Delegate> =
        abstract AddHandler: handler: 'Delegate -> unit
        abstract RemoveHandler: handler: 'Delegate -> unit

    /// An event: its delegate's handlers, and its arguments as an IObservable.
    [<Interface>]
    type IEvent<'Delegate, 'Args when 'Delegate :> Delegate> =
        inherit IDelegateEvent<'Delegate>
        inherit IObservable<'Args>

    /// Add and Subscribe with a function, on any IObservable (an event, too).
    [<AutoOpen>]
    module CommonExtensions =

        type IObservable<'T> with

            /// Calls callback with each value, for as long as the program runs.
            [<CompiledName("AddToObservable")>]
            member x.Add(callback: 'T -> unit) =
                x.Subscribe(
                    { new IObserver<'T> with
                        member _.OnNext value = callback value
                        member _.OnError _ = ()
                        member _.OnCompleted() = () })
                |> ignore

            /// Calls callback with each value, until the result is disposed.
            [<CompiledName("SubscribeToObservable")>]
            member x.Subscribe(callback: 'T -> unit) =
                x.Subscribe(
                    { new IObserver<'T> with
                        member _.OnNext value = callback value
                        member _.OnError _ = ()
                        member _.OnCompleted() = () })
