// FSharp.Core's Option and ValueOption modules. The functions that take a
// function are inline, as in FSharp.Core since F# 8: the function given is
// then written into the caller instead of becoming a closure, an object
// zero mode never frees.
#nowarn "35" "42" "44" "3370"

namespace Microsoft.FSharp.Core

    open System
    open Microsoft.FSharp.Core
    open Microsoft.FSharp.Core.Operators
    open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators
    open Microsoft.FSharp.Collections

    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    [<RequireQualifiedAccess>]
    module Option =

        [<CompiledName("GetValue")>]
        let get option =
            match option with
            | None -> invalidArg "option" "The option value was None"
            | Some x -> x

        [<CompiledName("IsSome")>]
        let inline isSome option = match option with None -> false | Some _ -> true

        [<CompiledName("IsNone")>]
        let inline isNone option = match option with None -> true | Some _ -> false

        [<CompiledName("DefaultValue")>]
        let inline defaultValue value option = match option with None -> value | Some v -> v

        [<CompiledName("DefaultWith")>]
        let inline defaultWith ([<InlineIfLambda>] defThunk) option = match option with None -> defThunk () | Some v -> v

        [<CompiledName("OrElse")>]
        let inline orElse (ifNone: 'T option) option = match option with None -> ifNone | Some _ -> option

        [<CompiledName("OrElseWith")>]
        let inline orElseWith ([<InlineIfLambda>] ifNoneThunk) option = match option with None -> ifNoneThunk () | Some _ -> option

        [<CompiledName("Count")>]
        let inline count option = match option with None -> 0 | Some _ -> 1

        [<CompiledName("Fold")>]
        let inline fold ([<InlineIfLambda>] folder) state option = match option with None -> state | Some x -> folder state x

        [<CompiledName("FoldBack")>]
        let inline foldBack ([<InlineIfLambda>] folder) option state = match option with None -> state | Some x -> folder x state

        [<CompiledName("Exists")>]
        let inline exists ([<InlineIfLambda>] predicate) option = match option with None -> false | Some x -> predicate x

        [<CompiledName("ForAll")>]
        let inline forall ([<InlineIfLambda>] predicate) option = match option with None -> true | Some x -> predicate x

        [<CompiledName("Contains")>]
        let inline contains value option = match option with None -> false | Some v -> v = value

        [<CompiledName("Iterate")>]
        let inline iter ([<InlineIfLambda>] action) option = match option with None -> () | Some x -> action x

        [<CompiledName("Map")>]
        let inline map ([<InlineIfLambda>] mapping) option = match option with None -> None | Some x -> Some(mapping x)

        [<CompiledName("Map2")>]
        let inline map2 ([<InlineIfLambda>] mapping) option1 option2 =
            match option1, option2 with
            | Some x, Some y -> Some(mapping x y)
            | _ -> None

        [<CompiledName("Map3")>]
        let inline map3 ([<InlineIfLambda>] mapping) option1 option2 option3 =
            match option1, option2, option3 with
            | Some x, Some y, Some z -> Some(mapping x y z)
            | _ -> None

        [<CompiledName("Bind")>]
        let inline bind ([<InlineIfLambda>] binder) option = match option with None -> None | Some x -> binder x

        [<CompiledName("Flatten")>]
        let inline flatten option = match option with None -> None | Some x -> x

        [<CompiledName("Filter")>]
        let inline filter ([<InlineIfLambda>] predicate) option =
            match option with
            | None -> None
            | Some x -> if predicate x then option else None

        [<CompiledName("ToArray")>]
        let inline toArray option = match option with None -> [||] | Some x -> [| x |]

        [<CompiledName("ToList")>]
        let inline toList option = match option with None -> [] | Some x -> [ x ]

        [<CompiledName("OfObj")>]
        let inline ofObj value = match value with null -> None | _ -> Some value

        [<CompiledName("ToObj")>]
        let inline toObj value = match value with None -> null | Some x -> x

        [<CompiledName("OfValueOption")>]
        let inline ofValueOption voption = match voption with ValueNone -> None | ValueSome x -> Some x

        [<CompiledName("ToValueOption")>]
        let inline toValueOption option = match option with None -> ValueNone | Some x -> ValueSome x

    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    [<RequireQualifiedAccess>]
    module ValueOption =

        [<CompiledName("GetValue")>]
        let get voption =
            match voption with
            | ValueNone -> invalidArg "option" "The option value was ValueNone"
            | ValueSome x -> x

        [<CompiledName("IsSome")>]
        let inline isSome voption = match voption with ValueNone -> false | ValueSome _ -> true

        [<CompiledName("IsNone")>]
        let inline isNone voption = match voption with ValueNone -> true | ValueSome _ -> false

        [<CompiledName("DefaultValue")>]
        let inline defaultValue value voption = match voption with ValueNone -> value | ValueSome v -> v

        [<CompiledName("DefaultWith")>]
        let inline defaultWith ([<InlineIfLambda>] defThunk) voption = match voption with ValueNone -> defThunk () | ValueSome v -> v

        [<CompiledName("OrElse")>]
        let inline orElse (ifNone: 'T voption) voption = match voption with ValueNone -> ifNone | ValueSome _ -> voption

        [<CompiledName("OrElseWith")>]
        let inline orElseWith ([<InlineIfLambda>] ifNoneThunk) voption = match voption with ValueNone -> ifNoneThunk () | ValueSome _ -> voption

        [<CompiledName("Count")>]
        let inline count voption = match voption with ValueNone -> 0 | ValueSome _ -> 1

        [<CompiledName("Fold")>]
        let inline fold ([<InlineIfLambda>] folder) state voption = match voption with ValueNone -> state | ValueSome x -> folder state x

        [<CompiledName("FoldBack")>]
        let inline foldBack ([<InlineIfLambda>] folder) voption state = match voption with ValueNone -> state | ValueSome x -> folder x state

        [<CompiledName("Exists")>]
        let inline exists ([<InlineIfLambda>] predicate) voption = match voption with ValueNone -> false | ValueSome x -> predicate x

        [<CompiledName("ForAll")>]
        let inline forall ([<InlineIfLambda>] predicate) voption = match voption with ValueNone -> true | ValueSome x -> predicate x

        [<CompiledName("Contains")>]
        let inline contains value voption = match voption with ValueNone -> false | ValueSome v -> v = value

        [<CompiledName("Iterate")>]
        let inline iter ([<InlineIfLambda>] action) voption = match voption with ValueNone -> () | ValueSome x -> action x

        [<CompiledName("Map")>]
        let inline map ([<InlineIfLambda>] mapping) voption = match voption with ValueNone -> ValueNone | ValueSome x -> ValueSome(mapping x)

        [<CompiledName("Map2")>]
        let inline map2 ([<InlineIfLambda>] mapping) voption1 voption2 =
            match voption1, voption2 with
            | ValueSome x, ValueSome y -> ValueSome(mapping x y)
            | _ -> ValueNone

        [<CompiledName("Map3")>]
        let inline map3 ([<InlineIfLambda>] mapping) voption1 voption2 voption3 =
            match voption1, voption2, voption3 with
            | ValueSome x, ValueSome y, ValueSome z -> ValueSome(mapping x y z)
            | _ -> ValueNone

        [<CompiledName("Bind")>]
        let inline bind ([<InlineIfLambda>] binder) voption = match voption with ValueNone -> ValueNone | ValueSome x -> binder x

        [<CompiledName("Flatten")>]
        let inline flatten voption = match voption with ValueNone -> ValueNone | ValueSome x -> x

        [<CompiledName("Filter")>]
        let inline filter ([<InlineIfLambda>] predicate) voption =
            match voption with
            | ValueNone -> ValueNone
            | ValueSome x -> if predicate x then voption else ValueNone

        [<CompiledName("ToArray")>]
        let inline toArray voption = match voption with ValueNone -> [||] | ValueSome x -> [| x |]

        [<CompiledName("ToList")>]
        let inline toList voption = match voption with ValueNone -> [] | ValueSome x -> [ x ]

        [<CompiledName("OfObj")>]
        let inline ofObj value = match value with null -> ValueNone | _ -> ValueSome value

        [<CompiledName("ToObj")>]
        let inline toObj value = match value with ValueNone -> null | ValueSome x -> x

        [<CompiledName("OfOption")>]
        let inline ofOption option = match option with None -> ValueNone | Some x -> ValueSome x

        [<CompiledName("ToOption")>]
        let inline toOption voption = match voption with ValueNone -> None | ValueSome x -> Some x

    /// FSharp.Core's Result module: functions on Ok and Error values. Those
    /// that take a function are inline, as the Option module's are.
    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    [<RequireQualifiedAccess>]
    module Result =

        [<CompiledName("Map")>]
        let inline map ([<InlineIfLambda>] mapping) result =
            match result with Error e -> Error e | Ok x -> Ok(mapping x)

        [<CompiledName("MapError")>]
        let inline mapError ([<InlineIfLambda>] mapping) result =
            match result with Error e -> Error(mapping e) | Ok x -> Ok x

        [<CompiledName("Bind")>]
        let inline bind ([<InlineIfLambda>] binder) result =
            match result with Error e -> Error e | Ok x -> binder x

        [<CompiledName("IsOk")>]
        let inline isOk result = match result with Ok _ -> true | Error _ -> false

        [<CompiledName("IsError")>]
        let inline isError result = match result with Ok _ -> false | Error _ -> true

        [<CompiledName("DefaultValue")>]
        let inline defaultValue value result = match result with Error _ -> value | Ok v -> v

        [<CompiledName("DefaultWith")>]
        let inline defaultWith ([<InlineIfLambda>] defThunk) result =
            match result with Error error -> defThunk error | Ok v -> v

        [<CompiledName("Count")>]
        let inline count result = match result with Error _ -> 0 | Ok _ -> 1

        [<CompiledName("Fold")>]
        let inline fold<'T, 'Error, 'State> ([<InlineIfLambda>] folder) (state: 'State) (result: Result<'T, 'Error>) =
            match result with Error _ -> state | Ok x -> folder state x

        [<CompiledName("FoldBack")>]
        let inline foldBack<'T, 'Error, 'State> ([<InlineIfLambda>] folder) (result: Result<'T, 'Error>) (state: 'State) =
            match result with Error _ -> state | Ok x -> folder x state

        [<CompiledName("Exists")>]
        let inline exists ([<InlineIfLambda>] predicate) result =
            match result with Error _ -> false | Ok x -> predicate x

        [<CompiledName("ForAll")>]
        let inline forall ([<InlineIfLambda>] predicate) result =
            match result with Error _ -> true | Ok x -> predicate x

        [<CompiledName("Contains")>]
        let inline contains value result =
            match result with Error _ -> false | Ok x -> x = value

        [<CompiledName("Iterate")>]
        let inline iter ([<InlineIfLambda>] action) result =
            match result with Error _ -> () | Ok x -> action x

        [<CompiledName("ToArray")>]
        let inline toArray result = match result with Error _ -> [||] | Ok x -> [| x |]

        [<CompiledName("ToList")>]
        let inline toList result = match result with Error _ -> [] | Ok x -> [ x ]

        [<CompiledName("ToOption")>]
        let inline toOption result = match result with Error _ -> None | Ok x -> Some x

        [<CompiledName("ToValueOption")>]
        let inline toValueOption result = match result with Error _ -> ValueNone | Ok x -> ValueSome x
