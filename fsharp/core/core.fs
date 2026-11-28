// A small FSharp.Core for zerocore: no reflection.
// The IL-inline operator definitions follow dotnet/fsharp's prim-types.fs (MIT).
#nowarn "35" "42" "44" "51" "60" "61" "62" "64" "69" "77" "3218" "3244" "3245" "3370"

namespace Microsoft.FSharp.Core

    open System
    open System.Collections
    open System.Collections.Generic

    [<MeasureAnnotatedAbbreviation>] type float<[<Measure>] 'Measure> = float
    [<MeasureAnnotatedAbbreviation>] type float32<[<Measure>] 'Measure> = float32
    [<MeasureAnnotatedAbbreviation>] type int<[<Measure>] 'Measure> = int
    [<MeasureAnnotatedAbbreviation>] type sbyte<[<Measure>] 'Measure> = sbyte
    [<MeasureAnnotatedAbbreviation>] type int16<[<Measure>] 'Measure> = int16
    [<MeasureAnnotatedAbbreviation>] type int64<[<Measure>] 'Measure> = int64
    [<MeasureAnnotatedAbbreviation>] type decimal<[<Measure>] 'Measure> = decimal

    type byref<'T> = (# "!0&" #)
    type byref<'T, 'Kind> = (# "!0&" #)

    module ByRefKinds =
        [<Sealed>] type Out() = class end
        [<Sealed>] type In() = class end
        [<Sealed>] type InOut = class end

    type inref<'T> = byref<'T, ByRefKinds.In>
    type outref<'T> = byref<'T, ByRefKinds.Out>

    module internal BasicInlinedOperations =
        let inline unboxPrim<'T>(x:obj) = (# "unbox.any !0" type ('T) x : 'T #)
        let inline box     (x:'T) = (# "box !0" type ('T) x : obj #)
        let inline not     (b:bool) = (# "ceq" b false : bool #)
        let inline (=)     (x:int)   (y:int)    = (# "ceq" x y : bool #)
        let inline (<>)    (x:int)   (y:int)    = not(# "ceq" x y : bool #)
        let inline (<=)    (x:int)   (y:int)    = not(# "cgt" x y : bool #)
        let inline (>=)    (x:int)   (y:int)    = not(# "clt" x y : bool #)
        let inline (+)     (x:int)    (y:int)    = (# "add" x y : int #)
        let inline ( * )   (x:int)    (y:int)    = (# "mul" x y : int #)
        let inline (-)     (x:int)    (y:int)    = (# "sub" x y : int #)
        let inline (>)     (x:int)    (y:int)    = (# "cgt" x y : bool #)
        let inline (<)     (x:int)    (y:int)    = (# "clt" x y : bool #)
        let inline ignore _ = ()
        let inline raise (e: Exception) = (# "throw" e : 'U #)
        let inline length (x: 'T array) = (# "ldlen conv.i4" x : int #)
        let inline zeroCreate (n:int) = (# "newarr !0" type ('T) n : 'T array #)
        let inline get (arr: 'T array) (n:int) =  (# "ldelem.any !0" type ('T) arr n : 'T #)
        let inline set (arr: 'T array) (n:int) (x:'T) =  (# "stelem.any !0" type ('T) arr n x #)
        let inline objEq (xobj:obj) (yobj:obj) = (# "ceq" xobj yobj : bool #)
        let inline unsafeDefault<'T> : 'T = (# "ilzero !0" type ('T) : 'T #)
        let inline isinstPrim<'T>(x:obj) = (# "isinst !0" type ('T) x : obj #)
        let inline notnullPrim<'T when 'T : not struct>(x:'T) = (# "ldnull cgt.un" x : bool #)
        let inline iscastPrim<'T when 'T : not struct>(x:obj) = (# "isinst !0" type ('T) x : 'T #)
        let inline mask (n:int) (m:int) = (# "and" n m : int #)
        let inline retype<'T,'U> (x:'T) : 'U = (# "" x : 'U #)

        // How the ToStrings below show a value ("null" for null), without
        // Operators: see "Their own ToString" below.
        let showValue (x: 'T) = match box x with null -> "null" | o -> o.ToString()

    open BasicInlinedOperations

    module TupleUtils =
        let inline combine (h1 : int) (h2 : int) = (# "xor" ((# "shl" h1 5 : int #) + h1) h2 : int32 #)

    module LanguagePrimitives =

        module IntrinsicOperators =
            [<CompiledName("op_Amp")>]
            let __obsoleteAnd e1 e2 = if e1 then e2 else false
            let (&&) e1 e2 = if e1 then e2 else false
            [<CompiledName("Or")>]
            let __obsoleteOr e1 e2 = if e1 then true else e2
            let (||) e1 e2 = if e1 then true else e2

            [<NoDynamicInvocation>]
            let inline (~&)  (obj : 'T) : byref<'T> =
                ignore obj
                (# "throw" (Exception("address-of is not first class")) : byref<'T> #)

            [<NoDynamicInvocation>]
            let inline (~&&) (obj : 'T) : nativeptr<'T> =
                ignore obj
                (# "throw" (Exception("address-of is not first class")) : nativeptr<'T> #)

        open IntrinsicOperators

        module IntrinsicFunctions =
            // No reflection: a null is unboxed as-is (F# types that use null
            // as a value, like None and [], come back as null too).
            let UnboxGeneric<'T>(source: obj) = unboxPrim<'T>(source)
            let inline UnboxFast<'T>(source: obj) = unboxPrim<'T>(source)
            let TypeTestGeneric<'T>(source: obj) = notnullPrim(isinstPrim<'T>(source))
            let inline TypeTestFast<'T>(source: obj) = notnullPrim(isinstPrim<'T>(source))

            let inline MakeDecimal low medium high isNegative scale = Decimal(low, medium, high, isNegative, scale)

            let inline GetString (source: string) (index:int) = source.Chars(index)

            let inline GetArray (source: 'T array) (index:int) = (# "ldelem.any !0" type ('T) source index : 'T #)
            let inline SetArray (target: 'T array) (index:int) (value:'T) = (# "stelem.any !0" type ('T) target index value #)

            let Dispose<'T when 'T :> IDisposable >(resource:'T) =
                match box resource with
                | null -> ()
                | _ -> resource.Dispose()

            let FailInit() : unit = raise (InvalidOperationException("The initialization of an object or value resulted in an object or value being accessed recursively before it was fully initialized."))
            let FailStaticInit() : unit = raise (InvalidOperationException("The static initialization of a file or type resulted in static data being accessed recursively before it was fully initialized."))

            let CheckThis (x : 'T when 'T : not struct) =
                if not (notnullPrim(x)) then FailInit()
                x

        open IntrinsicFunctions

        module HashCompare =
            let PhysicalEqualityIntrinsic (x:'T) (y:'T) : bool when 'T : not struct = objEq (box x) (box y)
            let inline PhysicalEqualityFast (x:'T) (y:'T) : bool when 'T : not struct = PhysicalEqualityIntrinsic x y
            let PhysicalHashIntrinsic (x: 'T) : int when 'T : not struct = (box x).GetHashCode()
            let inline PhysicalHashFast (x: 'T) = PhysicalHashIntrinsic x

            // Arrays' elements as objects, for the element types without a
            // loop of their own below: zerocore's arrays have no GetValue,
            // but each is an IEnumerable (each element boxed).
            let ArrayItems (a: Array) =
                match box a with
                | :? System.Collections.IEnumerable as items -> items.GetEnumerator()
                | _ -> raise (InvalidOperationException("an array that isn't an IEnumerable"))

            // Structural equality without reflection: arrays element by
            // element, as FSharp.Core compares them (zerocore's arrays have no
            // IStructuralEquatable, which is F#'s); IStructuralEquatable (F#
            // records, unions, tuples); then Equals.
            let rec GenericEqualityObj (er:bool) (comp:IEqualityComparer) (xobj:obj) (yobj:obj) : bool =
                match xobj, yobj with
                | null, null -> true
                | null, _ -> false
                | _, null -> false
                | (:? Array as x1), (:? Array as y1) -> ArrayEquality er comp x1 y1
                | (:? IStructuralEquatable as x1), _ -> x1.Equals(yobj, comp)
                | _ -> xobj.Equals(yobj)

            and ArrayEquality (er:bool) (comp:IEqualityComparer) (x: Array) (y: Array) : bool =
                if x.Length <> y.Length then false
                else
                    let n = x.Length
                    let mutable i = 0
                    let mutable same = true
                    match box x, box y with
                    | (:? (int[]) as a), (:? (int[]) as b) ->
                        while same && i < n do
                            same <- (# "ceq" (GetArray a i) (GetArray b i) : bool #)
                            i <- i + 1
                    | (:? (byte[]) as a), (:? (byte[]) as b) ->
                        while same && i < n do
                            same <- (# "ceq" (GetArray a i) (GetArray b i) : bool #)
                            i <- i + 1
                    | (:? (char[]) as a), (:? (char[]) as b) ->
                        while same && i < n do
                            same <- (# "ceq" (GetArray a i) (GetArray b i) : bool #)
                            i <- i + 1
                    | (:? (int64[]) as a), (:? (int64[]) as b) ->
                        while same && i < n do
                            same <- (# "ceq" (GetArray a i) (GetArray b i) : bool #)
                            i <- i + 1
                    | (:? (float[]) as a), (:? (float[]) as b) ->
                        // = says NaN isn't NaN; Equals (er) says it is.
                        while same && i < n do
                            let u = (GetArray a i)
                            let v = (GetArray b i)
                            same <- (# "ceq" u v : bool #) || (er && not (# "ceq" u u : bool #) && not (# "ceq" v v : bool #))
                            i <- i + 1
                    | (:? (string[]) as a), (:? (string[]) as b) ->
                        while same && i < n do
                            same <- String.Equals((GetArray a i), (GetArray b i))
                            i <- i + 1
                    | _ ->
                        let xs = ArrayItems x
                        let ys = ArrayItems y
                        while same && xs.MoveNext() && ys.MoveNext() do
                            same <- GenericEqualityObj er comp xs.Current ys.Current
                    same

            let StringCompareOrdinal (x: string) (y: string) =
                let n = if x.Length < y.Length then x.Length else y.Length
                let mutable i = 0
                let mutable r = 0
                while r = 0 && i < n do
                    let a = x.Chars(i)
                    let b = y.Chars(i)
                    r <- (# "cgt" a b : int #) - (# "clt" a b : int #)
                    i <- i + 1
                if r <> 0 then r else x.Length - y.Length

            let rec GenericCompare (comp:IComparer) (xobj:obj) (yobj:obj) : int =
                match xobj, yobj with
                | null, null -> 0
                | null, _ -> -1
                | _, null -> 1
                // Primitives first, as FSharp.Core does: floats by F#'s rule for NaN.
                | (:? int as x), (:? int as y) -> (# "cgt" x y : int #) - (# "clt" x y : int #)
                | (:? int64 as x), (:? int64 as y) -> (# "cgt" x y : int #) - (# "clt" x y : int #)
                | (:? float as x), (:? float as y) -> if (# "clt" x y : bool #) then -1 elif (# "cgt" x y : bool #) then 1 elif (# "ceq" x y : bool #) then 0 elif (# "ceq" y y : bool #) then -1 else (# "ceq" x x : int #)
                | (:? char as x), (:? char as y) -> (# "cgt" x y : int #) - (# "clt" x y : int #)
                | (:? bool as x), (:? bool as y) -> (# "" x : int #) - (# "" y : int #)
                | (:? byte as x), (:? byte as y) -> (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                | (:? uint32 as x), (:? uint32 as y) -> (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                | (:? uint64 as x), (:? uint64 as y) -> (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                | (:? int16 as x), (:? int16 as y) -> (# "cgt" x y : int #) - (# "clt" x y : int #)
                | (:? float32 as x), (:? float32 as y) -> if (# "clt" x y : bool #) then -1 elif (# "cgt" x y : bool #) then 1 else 0
                | (:? string as x), (:? string as y) -> StringCompareOrdinal x y
                | (:? Array as x), (:? Array as y) -> ArrayComparison comp x y
                | (:? IStructuralComparable as x), _ -> x.CompareTo(yobj, comp)
                | (:? IComparable as x), _ -> x.CompareTo(yobj)
                | _ -> raise (ArgumentException("The type does not support comparison"))

            // As FSharp.Core: the shorter array first, then element by element.
            and ArrayComparison (comp:IComparer) (x: Array) (y: Array) : int =
                if x.Length <> y.Length then (if x.Length < y.Length then -1 else 1)
                else
                    let mutable r = 0
                    match box x, box y with
                    | (:? (int[]) as a), (:? (int[]) as b) ->
                        let mutable i = 0
                        while r = 0 && i < a.Length do
                            r <- (# "cgt" (GetArray a i) (GetArray b i) : int #) - (# "clt" (GetArray a i) (GetArray b i) : int #)
                            i <- i + 1
                    | (:? (byte[]) as a), (:? (byte[]) as b) ->
                        let mutable i = 0
                        while r = 0 && i < a.Length do
                            r <- (# "cgt.un" (GetArray a i) (GetArray b i) : int #) - (# "clt.un" (GetArray a i) (GetArray b i) : int #)
                            i <- i + 1
                    | (:? (char[]) as a), (:? (char[]) as b) ->
                        let mutable i = 0
                        while r = 0 && i < a.Length do
                            r <- (# "cgt" (GetArray a i) (GetArray b i) : int #) - (# "clt" (GetArray a i) (GetArray b i) : int #)
                            i <- i + 1
                    | (:? (string[]) as a), (:? (string[]) as b) ->
                        let mutable i = 0
                        while r = 0 && i < a.Length do
                            r <- GenericCompare comp (GetArray a i) (GetArray b i)
                            i <- i + 1
                    | _ ->
                        let xs = ArrayItems x
                        let ys = ArrayItems y
                        while r = 0 && xs.MoveNext() && ys.MoveNext() do
                            r <- GenericCompare comp xs.Current ys.Current
                    r

            let rec GenericHashParamObj (iec : IEqualityComparer) (x: obj) : int =
                match x with
                | null -> 0
                | :? Array as a -> ArrayHash iec a
                | :? IStructuralEquatable as a -> a.GetHashCode(iec)
                | _ -> x.GetHashCode()

            // Equal arrays hash alike: their first 18 elements, combined as
            // FSharp.Core combines them.
            and ArrayHash (iec : IEqualityComparer) (a: Array) : int =
                let items = ArrayItems a
                let mutable acc = 0
                let mutable i = 0
                while i < 18 && items.MoveNext() do
                    acc <- (# "shl" acc 1 : int #) + GenericHashParamObj iec items.Current + 631 * i
                    i <- i + 1
                acc

            type GenericEqualityComparerImpl(er: bool) =
                interface IEqualityComparer with
                    override iec.Equals(x:obj, y:obj) = GenericEqualityObj er iec x y
                    override iec.GetHashCode(x:obj) = GenericHashParamObj iec x

            type GenericComparerImpl() =
                interface IComparer with
                    override c.Compare(x:obj, y:obj) = GenericCompare c x y

            let fsEqualityComparerNoHashingPER = GenericEqualityComparerImpl(false) :> IEqualityComparer
            let fsEqualityComparerNoHashingER = GenericEqualityComparerImpl(true) :> IEqualityComparer
            let fsEqualityComparerUnlimitedHashingPER = fsEqualityComparerNoHashingPER
            let fsComparerPER = GenericComparerImpl() :> IComparer
            let fsComparerER = fsComparerPER
            let fsComparerNoThrow = fsComparerPER

            let GenericEqualityIntrinsic (x : 'T) (y : 'T) : bool =
                GenericEqualityObj false fsEqualityComparerNoHashingPER (box x) (box y)
            let GenericEqualityERIntrinsic (x : 'T) (y : 'T) : bool =
                GenericEqualityObj true fsEqualityComparerNoHashingER (box x) (box y)
            let GenericEqualityWithComparerIntrinsic (comp : IEqualityComparer) (x : 'T) (y : 'T) : bool =
                comp.Equals((box x), (box y))

            let GenericComparisonIntrinsic<'T> (x:'T) (y:'T) : int = GenericCompare fsComparerPER (box x) (box y)
            let GenericComparisonWithComparerIntrinsic<'T> (comp:IComparer) (x:'T) (y:'T) : int = comp.Compare(box x, box y)
            let GenericLessThanIntrinsic (x:'T) (y:'T) = GenericComparisonIntrinsic x y < 0
            let GenericGreaterThanIntrinsic (x:'T) (y:'T) = GenericComparisonIntrinsic x y > 0
            let GenericLessOrEqualIntrinsic (x:'T) (y:'T) = GenericComparisonIntrinsic x y <= 0
            let GenericGreaterOrEqualIntrinsic (x:'T) (y:'T) = GenericComparisonIntrinsic x y >= 0

            let GenericHashIntrinsic input = GenericHashParamObj fsEqualityComparerUnlimitedHashingPER (box input)
            let GenericHashWithComparerIntrinsic<'T> (comp : IEqualityComparer) (input : 'T) : int = GenericHashParamObj comp (box input)
            let LimitedGenericHashIntrinsic (limit:int) (input:'T) = GenericHashIntrinsic input

            let inline HashChar (x:char) = (# "or" (# "shl" x 16 : int #) x : int #)
            let inline HashSByte (x:sbyte) = (# "xor" (# "shl" x 8 : int #) x : int #)
            let inline HashInt16 (x:int16) = (# "or" (# "conv.u2" x : int #) (# "shl" x 16 : int #) : int #)
            let inline HashInt64 (x:int64) = (# "xor" (# "conv.i4" x : int #) (# "conv.i4" (# "shr" x 32 : int #) : int #) : int #)
            let inline HashUInt64 (x:uint64) = (# "xor" (# "conv.i4" x : int #) (# "conv.i4" (# "shr.un" x 32 : int #) : int #) : int #)

            let inline GenericEqualityFast (x : 'T) (y : 'T) : bool =
                  GenericEqualityIntrinsic x y
                  when 'T : bool    = (# "ceq" x y : bool #)
                  when 'T : sbyte   = (# "ceq" x y : bool #)
                  when 'T : int16   = (# "ceq" x y : bool #)
                  when 'T : int32   = (# "ceq" x y : bool #)
                  when 'T : int64   = (# "ceq" x y : bool #)
                  when 'T : byte    = (# "ceq" x y : bool #)
                  when 'T : uint16  = (# "ceq" x y : bool #)
                  when 'T : uint32  = (# "ceq" x y : bool #)
                  when 'T : uint64  = (# "ceq" x y : bool #)
                  when 'T : float   = (# "ceq" x y : bool #)
                  when 'T : float32 = (# "ceq" x y : bool #)
                  when 'T : char    = (# "ceq" x y : bool #)
                  when 'T : decimal = System.Decimal.op_Equality((# "" x : decimal #), (# "" y : decimal #))
                  when 'T : nativeint  = (# "ceq" x y : bool #)
                  when 'T : unativeint  = (# "ceq" x y : bool #)
                  when 'T : string  = System.String.Equals((# "" x : string #),(# "" y : string #))

            let inline GenericEqualityERFast (x : 'T) (y : 'T) : bool =
                  GenericEqualityERIntrinsic x y
                  when 'T : bool    = (# "ceq" x y : bool #)
                  when 'T : sbyte   = (# "ceq" x y : bool #)
                  when 'T : int16   = (# "ceq" x y : bool #)
                  when 'T : int32   = (# "ceq" x y : bool #)
                  when 'T : int64   = (# "ceq" x y : bool #)
                  when 'T : byte    = (# "ceq" x y : bool #)
                  when 'T : uint16  = (# "ceq" x y : bool #)
                  when 'T : uint32  = (# "ceq" x y : bool #)
                  when 'T : uint64  = (# "ceq" x y : bool #)
                  when 'T : float   = if (# "ceq" x y : bool #) then true else (not (# "ceq" x x : bool #) && not (# "ceq" y y : bool #))
                  when 'T : float32 = if (# "ceq" x y : bool #) then true else (not (# "ceq" x x : bool #) && not (# "ceq" y y : bool #))
                  when 'T : char    = (# "ceq" x y : bool #)
                  when 'T : decimal = System.Decimal.op_Equality((# "" x : decimal #), (# "" y : decimal #))
                  when 'T : nativeint  = (# "ceq" x y : bool #)
                  when 'T : unativeint  = (# "ceq" x y : bool #)
                  when 'T : string  = System.String.Equals((# "" x : string #),(# "" y : string #))

            let inline GenericEqualityWithComparerFast (comp : IEqualityComparer) (x : 'T) (y : 'T) : bool =
                  GenericEqualityWithComparerIntrinsic comp x y
                  when 'T : bool    = (# "ceq" x y : bool #)
                  when 'T : sbyte   = (# "ceq" x y : bool #)
                  when 'T : int16   = (# "ceq" x y : bool #)
                  when 'T : int32   = (# "ceq" x y : bool #)
                  when 'T : int64   = (# "ceq" x y : bool #)
                  when 'T : byte    = (# "ceq" x y : bool #)
                  when 'T : uint16  = (# "ceq" x y : bool #)
                  when 'T : uint32  = (# "ceq" x y : bool #)
                  when 'T : uint64  = (# "ceq" x y : bool #)
                  when 'T : float   = (# "ceq" x y : bool #)
                  when 'T : float32 = (# "ceq" x y : bool #)
                  when 'T : char    = (# "ceq" x y : bool #)
                  when 'T : decimal = System.Decimal.op_Equality((# "" x : decimal #), (# "" y : decimal #))
                  when 'T : nativeint  = (# "ceq" x y : bool #)
                  when 'T : unativeint  = (# "ceq" x y : bool #)
                  when 'T : string  = System.String.Equals((# "" x : string #),(# "" y : string #))

            let inline GenericHashWithComparerFast (comp : IEqualityComparer) (input : 'T) : int =
                GenericHashWithComparerIntrinsic comp input
                when 'T : bool   = (# "" input : int #)
                when 'T : int32  = (# "" input : int #)
                when 'T : byte   = (# "" input : int #)
                when 'T : uint32 = (# "" input : int #)
                when 'T : char = HashChar (# "" input : char #)
                when 'T : decimal = (# "" input : decimal #).GetHashCode()
                when 'T : sbyte = HashSByte (# "" input : sbyte #)
                when 'T : int16 = HashInt16 (# "" input : int16 #)
                when 'T : int64 = HashInt64 (# "" input : int64 #)
                when 'T : uint64 = HashUInt64 (# "" input : uint64 #)
                when 'T : string = (# "" input : string #).GetHashCode()

            let inline GenericHashFast (input : 'T) : int =
                GenericHashIntrinsic input
                when 'T : bool   = (# "" input : int #)
                when 'T : int32  = (# "" input : int #)
                when 'T : byte   = (# "" input : int #)
                when 'T : uint32 = (# "" input : int #)
                when 'T : char = HashChar (# "" input : char #)
                when 'T : decimal = (# "" input : decimal #).GetHashCode()
                when 'T : sbyte = HashSByte (# "" input : sbyte #)
                when 'T : int16 = HashInt16 (# "" input : int16 #)
                when 'T : int64 = HashInt64 (# "" input : int64 #)
                when 'T : uint64 = HashUInt64 (# "" input : uint64 #)
                when 'T : string = (# "" input : string #).GetHashCode()

            let inline GenericComparisonFast<'T> (x:'T) (y:'T) : int =
                 GenericComparisonIntrinsic x y
                 when 'T : bool   = (# "" x : int #) - (# "" y : int #)
                 when 'T : sbyte  = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : int16  = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : int32  = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : int64  = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : nativeint  = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : byte   = (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                 when 'T : uint16 = (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                 when 'T : uint32 = (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                 when 'T : uint64 = (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                 when 'T : unativeint = (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                 when 'T : float  = if   (# "clt" x y : bool #) then (-1)
                                    elif (# "cgt" x y : bool #) then (1)
                                    elif (# "ceq" x y : bool #) then (0)
                                    elif (# "ceq" y y : bool #) then (-1)
                                    else (# "ceq" x x : int #)
                 when 'T : float32 = if   (# "clt" x y : bool #) then (-1)
                                     elif (# "cgt" x y : bool #) then (1)
                                     elif (# "ceq" x y : bool #) then (0)
                                     elif (# "ceq" y y : bool #) then (-1)
                                     else (# "ceq" x x : int #)
                 when 'T : char   = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : decimal = System.Decimal.Compare((# "" x : decimal #), (# "" y : decimal #))

            let inline GenericComparisonWithComparerFast<'T> (comp:IComparer) (x:'T) (y:'T) : int =
                 GenericComparisonWithComparerIntrinsic comp x y
                 when 'T : bool   = (# "" x : int #) - (# "" y : int #)
                 when 'T : sbyte  = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : int16  = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : int32  = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : int64  = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : nativeint  = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : byte   = (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                 when 'T : uint16 = (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                 when 'T : uint32 = (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                 when 'T : uint64 = (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                 when 'T : unativeint = (# "cgt.un" x y : int #) - (# "clt.un" x y : int #)
                 when 'T : char   = (# "cgt" x y : int #) - (# "clt" x y : int #)
                 when 'T : decimal = System.Decimal.Compare((# "" x : decimal #), (# "" y : decimal #))

            let inline GenericLessThanFast (x:'T) (y:'T) =
                GenericLessThanIntrinsic x y
                when 'T : bool   = (# "clt" x y : bool #)
                when 'T : sbyte  = (# "clt" x y : bool #)
                when 'T : int16  = (# "clt" x y : bool #)
                when 'T : int32  = (# "clt" x y : bool #)
                when 'T : int64  = (# "clt" x y : bool #)
                when 'T : byte   = (# "clt.un" x y : bool #)
                when 'T : uint16 = (# "clt.un" x y : bool #)
                when 'T : uint32 = (# "clt.un" x y : bool #)
                when 'T : uint64 = (# "clt.un" x y : bool #)
                when 'T : unativeint = (# "clt.un" x y : bool #)
                when 'T : nativeint  = (# "clt" x y : bool #)
                when 'T : float  = (# "clt" x y : bool #)
                when 'T : float32= (# "clt" x y : bool #)
                when 'T : char   = (# "clt" x y : bool #)
                when 'T : decimal = System.Decimal.op_LessThan((# "" x : decimal #), (# "" y : decimal #))

            let inline GenericGreaterThanFast (x:'T) (y:'T) =
                GenericGreaterThanIntrinsic x y
                when 'T : bool       = (# "cgt" x y : bool #)
                when 'T : sbyte      = (# "cgt" x y : bool #)
                when 'T : int16      = (# "cgt" x y : bool #)
                when 'T : int32      = (# "cgt" x y : bool #)
                when 'T : int64      = (# "cgt" x y : bool #)
                when 'T : nativeint  = (# "cgt" x y : bool #)
                when 'T : byte       = (# "cgt.un" x y : bool #)
                when 'T : uint16     = (# "cgt.un" x y : bool #)
                when 'T : uint32     = (# "cgt.un" x y : bool #)
                when 'T : uint64     = (# "cgt.un" x y : bool #)
                when 'T : unativeint = (# "cgt.un" x y : bool #)
                when 'T : float      = (# "cgt" x y : bool #)
                when 'T : float32    = (# "cgt" x y : bool #)
                when 'T : char       = (# "cgt" x y : bool #)
                when 'T : decimal = System.Decimal.op_GreaterThan((# "" x : decimal #), (# "" y : decimal #))

            let inline GenericLessOrEqualFast (x:'T) (y:'T) =
                GenericLessOrEqualIntrinsic x y
                when 'T : bool       = not (# "cgt" x y : bool #)
                when 'T : sbyte      = not (# "cgt" x y : bool #)
                when 'T : int16      = not (# "cgt" x y : bool #)
                when 'T : int32      = not (# "cgt" x y : bool #)
                when 'T : int64      = not (# "cgt" x y : bool #)
                when 'T : nativeint  = not (# "cgt" x y : bool #)
                when 'T : byte       = not (# "cgt.un" x y : bool #)
                when 'T : uint16     = not (# "cgt.un" x y : bool #)
                when 'T : uint32     = not (# "cgt.un" x y : bool #)
                when 'T : uint64     = not (# "cgt.un" x y : bool #)
                when 'T : unativeint = not (# "cgt.un" x y : bool #)
                when 'T : float      = not (# "cgt.un" x y : bool #)
                when 'T : float32    = not (# "cgt.un" x y : bool #)
                when 'T : char       = not (# "cgt" x y : bool #)
                when 'T : decimal = System.Decimal.op_LessThanOrEqual((# "" x : decimal #), (# "" y : decimal #))

            let inline GenericGreaterOrEqualFast (x:'T) (y:'T) =
                GenericGreaterOrEqualIntrinsic x y
                when 'T : bool       = not (# "clt" x y : bool #)
                when 'T : sbyte      = not (# "clt" x y : bool #)
                when 'T : int16      = not (# "clt" x y : bool #)
                when 'T : int32      = not (# "clt" x y : bool #)
                when 'T : int64      = not (# "clt" x y : bool #)
                when 'T : nativeint  = not (# "clt" x y : bool #)
                when 'T : byte       = not (# "clt.un" x y : bool #)
                when 'T : uint16     = not (# "clt.un" x y : bool #)
                when 'T : uint32     = not (# "clt.un" x y : bool #)
                when 'T : uint64     = not (# "clt.un" x y : bool #)
                when 'T : unativeint = not (# "clt.un" x y : bool #)
                when 'T : float      = not (# "clt.un" x y : bool #)
                when 'T : float32    = not (# "clt.un" x y : bool #)
                when 'T : char       = not (# "clt" x y : bool #)
                when 'T : decimal = System.Decimal.op_GreaterThanOrEqual((# "" x : decimal #), (# "" y : decimal #))

            let inline FastHashTuple2 (comparer:IEqualityComparer) (x1,x2) =
                TupleUtils.combine (GenericHashWithComparerFast comparer x1) (GenericHashWithComparerFast comparer x2)
            let inline FastHashTuple3 (comparer:IEqualityComparer) (x1,x2,x3) =
                TupleUtils.combine (TupleUtils.combine (GenericHashWithComparerFast comparer x1) (GenericHashWithComparerFast comparer x2)) (GenericHashWithComparerFast comparer x3)
            let inline FastEqualsTuple2 (comparer:IEqualityComparer) (x1,x2) (y1,y2) =
                GenericEqualityWithComparerFast comparer x1 y1 && GenericEqualityWithComparerFast comparer x2 y2
            let inline FastEqualsTuple3 (comparer:IEqualityComparer) (x1,x2,x3) (y1,y2,y3) =
                GenericEqualityWithComparerFast comparer x1 y1 && GenericEqualityWithComparerFast comparer x2 y2 && GenericEqualityWithComparerFast comparer x3 y3
            let inline FastCompareTuple2 (comparer:IComparer) (x1,x2) (y1,y2) =
                let n = GenericComparisonWithComparerFast comparer x1 y1
                if n <> 0 then n else GenericComparisonWithComparerFast comparer x2 y2
            let inline FastCompareTuple3 (comparer:IComparer) (x1,x2,x3) (y1,y2,y3) =
                let n = GenericComparisonWithComparerFast comparer x1 y1
                if n <> 0 then n else
                let n = GenericComparisonWithComparerFast comparer x2 y2
                if n <> 0 then n else GenericComparisonWithComparerFast comparer x3 y3

        open HashCompare

        let GenericEqualityComparer = fsEqualityComparerNoHashingPER
        let GenericEqualityERComparer = fsEqualityComparerNoHashingER
        let GenericComparer = fsComparerER

        let inline GenericEquality e1 e2 = GenericEqualityFast e1 e2
        let inline GenericEqualityER e1 e2 = GenericEqualityERFast e1 e2
        let inline GenericEqualityWithComparer comp e1 e2 = GenericEqualityWithComparerFast comp e1 e2
        let inline GenericComparison e1 e2 = GenericComparisonFast e1 e2
        let inline GenericComparisonWithComparer comp e1 e2 = GenericComparisonWithComparerFast comp e1 e2
        let inline GenericLessThan e1 e2 = GenericLessThanFast e1 e2
        let inline GenericGreaterThan e1 e2 = GenericGreaterThanFast e1 e2
        let inline GenericLessOrEqual e1 e2 = GenericLessOrEqualFast e1 e2
        let inline GenericGreaterOrEqual e1 e2 = GenericGreaterOrEqualFast e1 e2
        let inline GenericHash obj = GenericHashFast obj
        let inline GenericHashWithComparer comparer input = GenericHashWithComparerFast comparer input
        let inline PhysicalEquality e1 e2 = PhysicalEqualityFast e1 e2
        let inline PhysicalHash obj = PhysicalHashFast obj

        let inline EnumOfValue (value : 'T) : 'Enum when 'Enum : enum<'T> = unboxPrim<'Enum>(box value)
        let inline EnumToValue (enum : 'Enum) : 'T when 'Enum : enum<'T> = unboxPrim<'T>(box enum)

        // Comparers by F#'s rules, for Map, Set and the sorting functions.
        [<Sealed>]
        type internal FSharpComparer<'T>() =
            interface IComparer<'T> with
                member _.Compare(x, y) = GenericComparisonIntrinsic x y
            static member val Instance = FSharpComparer<'T>()

        [<Sealed>]
        type internal FSharpEqualityComparer<'T>() =
            interface IEqualityComparer<'T> with
                member _.Equals(x, y) = GenericEqualityIntrinsic x y
                member _.GetHashCode(x) = GenericHashIntrinsic x
            static member val Instance = FSharpEqualityComparer<'T>()

        let FastGenericComparer<'T> = FSharpComparer<'T>.Instance :> IComparer<'T>
        let FastGenericEqualityComparer<'T> = FSharpEqualityComparer<'T>.Instance :> IEqualityComparer<'T>

        let GenericZeroDynamic<'T> () : 'T = raise (NotSupportedException("GenericZero: a type with no static Zero"))
        let GenericOneDynamic<'T> () : 'T = raise (NotSupportedException("GenericOne: a type with no static One"))
        let DivideByIntDynamic<'T> (x: 'T) (y: int) : 'T = raise (NotSupportedException("DivideByInt: a type with no static DivideByInt"))

        let inline GenericZero< ^T when ^T : (static member Zero : ^T) > : ^T =
            GenericZeroDynamic<(^T)>()
            when ^T : int32       = 0
            when ^T : float       = 0.0
            when ^T : float32     = 0.0f
            when ^T : int64       = 0L
            when ^T : uint64      = 0UL
            when ^T : uint32      = 0ul
            when ^T : nativeint   = 0n
            when ^T : unativeint  = 0un
            when ^T : int16       = 0s
            when ^T : uint16      = 0us
            when ^T : char        = '\000'
            when ^T : sbyte       = 0y
            when ^T : byte        = 0uy
            when ^T : decimal     = 0M
            when ^T : ^T = (^T : (static member Zero : ^T) ())

        let inline GenericOne< ^T when ^T : (static member One : ^T) > : ^T =
            GenericOneDynamic<(^T)>()
            when ^T : int32       = 1
            when ^T : float       = 1.0
            when ^T : float32     = 1.0f
            when ^T : int64       = 1L
            when ^T : uint64      = 1UL
            when ^T : uint32      = 1ul
            when ^T : nativeint   = 1n
            when ^T : unativeint  = 1un
            when ^T : int16       = 1s
            when ^T : uint16      = 1us
            when ^T : char        = '\001'
            when ^T : sbyte       = 1y
            when ^T : byte        = 1uy
            when ^T : decimal     = 1M
            when ^T : ^T = (^T : (static member One : ^T) ())

        let inline DivideByInt< ^T when ^T : (static member DivideByInt : ^T * int -> ^T) > (x: ^T) (y: int) : ^T =
            DivideByIntDynamic<'T> x y
            when ^T : float = (# "div" x ((# "conv.r8" y  : float #)) : float #)
            when ^T : float32 = (# "div" x ((# "conv.r4" y  : float32 #)) : float32 #)
            when ^T : decimal = System.Decimal.Divide((# "" x : decimal #), System.Decimal(y))
            when ^T : ^T = (^T : (static member DivideByInt : ^T * int -> ^T) (x, y))


namespace Microsoft.FSharp.Core

    open System
    open System.Diagnostics
    open System.Collections
    open System.Collections.Generic
    open Microsoft.FSharp.Core.BasicInlinedOperations
    open Microsoft.FSharp.Core.LanguagePrimitives
    open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators
    open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicFunctions

    // Their own ToString: under --reflectionfree, F# compilers newer than SDK
    // 10.0.402's generate one for each record and union, from Operators.box and
    // Operators.string. Before ops.fs defines Operators, that generated code
    // can't compile (FS0193, "... did not contain ... 'Operators'"), so each
    // record and union in this file writes its own: FSharp.Core's text where it
    // has one (Option, ValueOption, list), the compiler's format otherwise.
    [<StructuralEquality; StructuralComparison>]
    [<CompiledName("FSharpChoice`2")>]
    type Choice<'T1,'T2> =
      | Choice1Of2 of 'T1
      | Choice2Of2 of 'T2

        override x.ToString() =
            match x with
            | Choice1Of2 a -> String.Concat("Choice1Of2(", showValue a, ")")
            | Choice2Of2 b -> String.Concat("Choice2Of2(", showValue b, ")")

    // Choice of 3 to 7: what multi-case active patterns with that many
    // cases return, as (|Low|Mid|High|) does.
    [<StructuralEquality; StructuralComparison>]
    [<CompiledName("FSharpChoice`3")>]
    type Choice<'T1,'T2,'T3> =
      | Choice1Of3 of 'T1
      | Choice2Of3 of 'T2
      | Choice3Of3 of 'T3

        override x.ToString() =
            match x with
            | Choice1Of3 v -> String.Concat("Choice1Of3(", showValue v, ")")
            | Choice2Of3 v -> String.Concat("Choice2Of3(", showValue v, ")")
            | Choice3Of3 v -> String.Concat("Choice3Of3(", showValue v, ")")

    [<StructuralEquality; StructuralComparison>]
    [<CompiledName("FSharpChoice`4")>]
    type Choice<'T1,'T2,'T3,'T4> =
      | Choice1Of4 of 'T1
      | Choice2Of4 of 'T2
      | Choice3Of4 of 'T3
      | Choice4Of4 of 'T4

        override x.ToString() =
            match x with
            | Choice1Of4 v -> String.Concat("Choice1Of4(", showValue v, ")")
            | Choice2Of4 v -> String.Concat("Choice2Of4(", showValue v, ")")
            | Choice3Of4 v -> String.Concat("Choice3Of4(", showValue v, ")")
            | Choice4Of4 v -> String.Concat("Choice4Of4(", showValue v, ")")

    [<StructuralEquality; StructuralComparison>]
    [<CompiledName("FSharpChoice`5")>]
    type Choice<'T1,'T2,'T3,'T4,'T5> =
      | Choice1Of5 of 'T1
      | Choice2Of5 of 'T2
      | Choice3Of5 of 'T3
      | Choice4Of5 of 'T4
      | Choice5Of5 of 'T5

        override x.ToString() =
            match x with
            | Choice1Of5 v -> String.Concat("Choice1Of5(", showValue v, ")")
            | Choice2Of5 v -> String.Concat("Choice2Of5(", showValue v, ")")
            | Choice3Of5 v -> String.Concat("Choice3Of5(", showValue v, ")")
            | Choice4Of5 v -> String.Concat("Choice4Of5(", showValue v, ")")
            | Choice5Of5 v -> String.Concat("Choice5Of5(", showValue v, ")")

    [<StructuralEquality; StructuralComparison>]
    [<CompiledName("FSharpChoice`6")>]
    type Choice<'T1,'T2,'T3,'T4,'T5,'T6> =
      | Choice1Of6 of 'T1
      | Choice2Of6 of 'T2
      | Choice3Of6 of 'T3
      | Choice4Of6 of 'T4
      | Choice5Of6 of 'T5
      | Choice6Of6 of 'T6

        override x.ToString() =
            match x with
            | Choice1Of6 v -> String.Concat("Choice1Of6(", showValue v, ")")
            | Choice2Of6 v -> String.Concat("Choice2Of6(", showValue v, ")")
            | Choice3Of6 v -> String.Concat("Choice3Of6(", showValue v, ")")
            | Choice4Of6 v -> String.Concat("Choice4Of6(", showValue v, ")")
            | Choice5Of6 v -> String.Concat("Choice5Of6(", showValue v, ")")
            | Choice6Of6 v -> String.Concat("Choice6Of6(", showValue v, ")")

    [<StructuralEquality; StructuralComparison>]
    [<CompiledName("FSharpChoice`7")>]
    type Choice<'T1,'T2,'T3,'T4,'T5,'T6,'T7> =
      | Choice1Of7 of 'T1
      | Choice2Of7 of 'T2
      | Choice3Of7 of 'T3
      | Choice4Of7 of 'T4
      | Choice5Of7 of 'T5
      | Choice6Of7 of 'T6
      | Choice7Of7 of 'T7

        override x.ToString() =
            match x with
            | Choice1Of7 v -> String.Concat("Choice1Of7(", showValue v, ")")
            | Choice2Of7 v -> String.Concat("Choice2Of7(", showValue v, ")")
            | Choice3Of7 v -> String.Concat("Choice3Of7(", showValue v, ")")
            | Choice4Of7 v -> String.Concat("Choice4Of7(", showValue v, ")")
            | Choice5Of7 v -> String.Concat("Choice5Of7(", showValue v, ")")
            | Choice6Of7 v -> String.Concat("Choice6Of7(", showValue v, ")")
            | Choice7Of7 v -> String.Concat("Choice7Of7(", showValue v, ")")



    [<AbstractClass>]
    type FSharpFunc<'T,'Res> [<DebuggerHidden>] () =
        abstract Invoke : 'T -> 'Res

    module OptimizedClosures =

          [<AbstractClass>]
          type FSharpFunc<'T,'U,'V> [<DebuggerHidden>] () =
              inherit FSharpFunc<'T, 'U -> 'V>()
              abstract Invoke : 'T * 'U -> 'V
              override f.Invoke(t) = (fun u -> f.Invoke(t,u))
              static member Adapt(func : 'T -> 'U -> 'V) =
                  match box func with
                  // Does it take two arguments without side effect?
                  | :? FSharpFunc<'T,'U,'V> as f -> f

                  | _ ->
                      { new FSharpFunc<'T,'U,'V>() with
                          member _.Invoke(t,u) = (retype func : FSharpFunc<'T,FSharpFunc<'U,'V>>).Invoke(t).Invoke(u) }

          [<AbstractClass>]
          type FSharpFunc<'T,'U,'V,'W> [<DebuggerHidden>] () =
              inherit FSharpFunc<'T, 'U -> 'V -> 'W>()
              abstract Invoke : 'T * 'U * 'V -> 'W
              override f.Invoke(t) = (fun u v -> f.Invoke(t,u,v))
              static member Adapt(func : 'T -> 'U -> 'V -> 'W) =
                  match box func with
                  // Does it take three arguments without side effect?
                  | :? FSharpFunc<'T,'U,'V,'W> as f ->
                      f

                  // Does it take two arguments without side effect?
                  | :? FSharpFunc<'T,'U,FSharpFunc<'V,'W>> as f ->
                      { new FSharpFunc<'T,'U,'V,'W>() with
                           member _.Invoke(t,u,v) = f.Invoke(t,u).Invoke(v) }

                  | _ ->
                      { new FSharpFunc<'T,'U,'V,'W>() with
                          member _.Invoke(t,u,v) = (retype func : FSharpFunc<'T, 'U -> 'V -> 'W>).Invoke(t) u v }

          [<AbstractClass>]
          type FSharpFunc<'T,'U,'V,'W,'X> [<DebuggerHidden>] () =
              inherit FSharpFunc<'T, 'U -> 'V -> 'W -> 'X>()
              abstract Invoke : 'T * 'U * 'V * 'W -> 'X
              static member Adapt(func : 'T -> 'U -> 'V -> 'W -> 'X) =
                  match box func with
                  // Does it take four arguments without side effect?
                  | :? FSharpFunc<'T,'U,'V,'W,'X> as f -> f

                  // Does it take three arguments without side effect?
                  | :? FSharpFunc<'T,'U,'V,FSharpFunc<'W,'X>> as f ->
                      { new FSharpFunc<'T,'U,'V,'W,'X>() with
                          member _.Invoke(t,u,v,w) = f.Invoke(t,u,v).Invoke(w) }

                  // Does it take two arguments without side effect?
                  | :? FSharpFunc<'T,'U, 'V -> 'W -> 'X> as f ->
                      { new FSharpFunc<'T,'U,'V,'W,'X>() with
                          member _.Invoke(t,u,v,w) = f.Invoke(t,u) v w }

                  | _ ->
                      { new FSharpFunc<'T,'U,'V,'W,'X>() with
                          member _.Invoke(t,u,v,w) = (retype func : FSharpFunc<'T, 'U -> 'V -> 'W -> 'X>).Invoke(t) u v w   }

              override f.Invoke(t) = (fun u v w -> f.Invoke(t,u,v,w))

          [<AbstractClass>]
          type FSharpFunc<'T,'U,'V,'W,'X,'Y> [<DebuggerHidden>] () =
              inherit FSharpFunc<'T, 'U -> 'V -> 'W -> 'X -> 'Y>()

              abstract Invoke : 'T * 'U * 'V * 'W * 'X -> 'Y

              override f.Invoke(t) = (fun u v w x -> f.Invoke(t,u,v,w,x))

              static member Adapt(func : 'T -> 'U -> 'V -> 'W -> 'X -> 'Y) =
                  match box func with

                  // Does it take five arguments without side effect?
                  | :? FSharpFunc<'T,'U,'V,'W,'X,'Y> as f -> f

                  // Does it take four arguments without side effect?
                  | :? FSharpFunc<'T,'U,'V,'W,FSharpFunc<'X,'Y>> as f ->
                      { new FSharpFunc<'T,'U,'V,'W,'X,'Y>() with
                          member ff.Invoke(t,u,v,w,x) = f.Invoke(t,u,v,w).Invoke(x) }

                  // Does it take three arguments without side effect?
                  | :? FSharpFunc<'T,'U,'V, 'W -> 'X -> 'Y> as f ->
                      { new FSharpFunc<'T,'U,'V,'W,'X,'Y>() with
                          member ff.Invoke(t,u,v,w,x) = f.Invoke(t,u,v) w x }

                  // Does it take two arguments without side effect?
                  | :? FSharpFunc<'T,'U, 'V -> 'W -> 'X -> 'Y> as f ->
                      { new FSharpFunc<'T,'U,'V,'W,'X,'Y>() with
                          member ff.Invoke(t,u,v,w,x) = f.Invoke(t,u) v w x }

                  | _ ->
                      { new FSharpFunc<'T,'U,'V,'W,'X,'Y>() with
                          member ff.Invoke(t,u,v,w,x) =
                              (retype func : FSharpFunc<'T, 'U -> 'V -> 'W -> 'X -> 'Y>).Invoke(t) u v w x  }

          let inline invokeFast2(f1 : FSharpFunc<'T, 'U -> 'V>, t,u) =
              match f1 with
              | :? FSharpFunc<'T,'U,'V> as f2 -> f2.Invoke(t,u)
              | _ -> f1.Invoke(t) u

          let inline invokeFast3(f1 : FSharpFunc<'T, 'U -> 'V -> 'W>, t,u,v) =
               match f1 with
               | :? FSharpFunc<'T,'U,'V,'W>      as f3 -> f3.Invoke(t,u,v)
               | :? FSharpFunc<'T,'U, 'V -> 'W> as f2 -> f2.Invoke(t,u) v
               | _ -> f1.Invoke(t) u v

          let inline invokeFast4(f1 : FSharpFunc<'T, 'U -> 'V -> 'W -> 'X>, t,u,v,w) =
               match f1 with
               | :? FSharpFunc<'T,'U,'V,'W,'X>         as f4 -> f4.Invoke(t,u,v,w)
               | :? FSharpFunc<'T,'U,'V, 'W -> 'X>    as f3 -> f3.Invoke(t,u,v) w
               | :? FSharpFunc<'T,'U, 'V -> 'W -> 'X> as f2 -> f2.Invoke(t,u) v w
               | _ -> f1.Invoke(t) u v w

          let inline invokeFast5(f1 : FSharpFunc<'T, 'U -> 'V -> 'W -> 'X -> 'Y>, t,u,v,w,x) =
               match f1 with
               | :? FSharpFunc<'T,'U,'V,'W,'X,'Y>             as f5 -> f5.Invoke(t,u,v,w,x)
               | :? FSharpFunc<'T,'U,'V,'W, 'X -> 'Y>        as f4 -> f4.Invoke(t,u,v,w) x
               | :? FSharpFunc<'T,'U,'V, 'W -> 'X -> 'Y>     as f3 -> f3.Invoke(t,u,v) w x
               | :? FSharpFunc<'T,'U, 'V -> 'W -> 'X -> 'Y>  as f2 -> f2.Invoke(t,u) v w x
               | _ -> f1.Invoke(t) u v w x


    type FSharpFunc<'T,'Res> with

        [<DebuggerStepThrough>]
        static member InvokeFast (func:FSharpFunc<_,_>, arg1: 'T, arg2: 'Res) =
            OptimizedClosures.invokeFast2(func, arg1, arg2)

        [<DebuggerStepThrough>]
        static member InvokeFast (func:FSharpFunc<_,_>, arg1: 'T, arg2: 'Res, arg3) =
            OptimizedClosures.invokeFast3(func, arg1, arg2, arg3)

        [<DebuggerStepThrough>]
        static member InvokeFast (func:FSharpFunc<_,_>, arg1: 'T, arg2: 'Res, arg3, arg4) =
            OptimizedClosures.invokeFast4(func, arg1, arg2, arg3, arg4)

        [<DebuggerStepThrough>]
        // Not invokeFast5: its type tests (OptimizedClosures.FSharpFunc`6 and
        // the others, over the function's types) are generic code bflat's
        // compiler gives up on when those types nest deeply (IL3054, generic
        // recursion), and zero mode has no type loader to make them at run
        // time. A printf with four values or more gets here with its format
        // (whose type holds the whole printer's) as the first. Each argument
        // is applied in turn, a closure each. Testing only the result of the
        // first for a four-argument closure isn't enough: that type holds the
        // rest of a longer printer's, and its adapters nest the same way.
        static member InvokeFast (func: FSharpFunc<'T, ('Res -> 'V -> 'W -> 'X -> 'Y)>, arg1: 'T, arg2: 'Res, arg3: 'V, arg4: 'W, arg5: 'X) : 'Y =
            let f2 = (# "" (func.Invoke(arg1)) : FSharpFunc<'Res, ('V -> 'W -> 'X -> 'Y)> #)
            let f3 = (# "" (f2.Invoke(arg2)) : FSharpFunc<'V, ('W -> 'X -> 'Y)> #)
            let f4 = (# "" (f3.Invoke(arg3)) : FSharpFunc<'W, ('X -> 'Y)> #)
            let f5 = (# "" (f4.Invoke(arg4)) : FSharpFunc<'X, 'Y> #)
            f5.Invoke(arg5)

namespace Microsoft.FSharp.Core

    open System
    open Microsoft.FSharp.Core.BasicInlinedOperations
    open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators

    [<DefaultAugmentation(false)>]
    [<StructuralEquality; StructuralComparison>]
    [<CompiledName("FSharpRef`1")>]
    type Ref<'T> =
        { mutable contents: 'T }
        member x.Value
            with get() = x.contents
            and set v = x.contents <- v

        override x.ToString() = String.Concat("{ contents = ", showValue x.contents, " }")

    and 'T ref = Ref<'T>

    [<DefaultAugmentation(false)>]
    [<CompilationRepresentation(CompilationRepresentationFlags.UseNullAsTrueValue)>]
    [<StructuralEquality; StructuralComparison>]
    [<CompiledName("FSharpOption`1")>]
    type Option<'T> =
        | None :       'T option
        | Some : Value:'T -> 'T option

        [<CompilationRepresentation(CompilationRepresentationFlags.Instance)>]
        member x.Value = match x with Some x -> x | None -> raise (new InvalidOperationException("Option.Value"))

        // Static, as FSharp.Core's (fsc makes them so): None is null, and an
        // instance call on it crashes where fsc doesn't inline (Optimize false).
        member x.IsNone = match x with None -> true | _ -> false

        member x.IsSome = match x with Some _ -> true | _ -> false

        static member None : 'T option = None
        static member Some (value) : 'T option = Some(value)

        // Only Some reaches here: None is null.
        override x.ToString() = String.Concat("Some(", showValue x.Value, ")")

    and 'T option = Option<'T>

    [<StructuralEquality; StructuralComparison>]
    [<CompiledName("FSharpResult`2")>]
    [<Struct>]
    type Result<'T,'TError> =
      | Ok of ResultValue:'T
      | Error of ErrorValue:'TError

        override x.ToString() =
            match x with
            | Ok v -> String.Concat("Ok(", showValue v, ")")
            | Error e -> String.Concat("Error(", showValue e, ")")

    [<StructuralEquality; StructuralComparison>]
    [<Struct>]
    [<CompiledName("FSharpValueOption`1")>]
    type ValueOption<'T> =
        | ValueNone : 'T voption
        | ValueSome : 'T -> 'T voption

        member x.Value = match x with ValueSome x -> x | ValueNone -> raise (new InvalidOperationException("ValueOption.Value"))
        member x.IsNone = match x with ValueNone -> true | _ -> false
        member x.IsSome = match x with ValueSome _ -> true | _ -> false

        override x.ToString() = match x with ValueNone -> "ValueNone" | ValueSome v -> showValue v

    and 'T voption = ValueOption<'T>

namespace Microsoft.FSharp.Collections

    open System
    open System.Collections.Generic
    open Microsoft.FSharp.Core
    open Microsoft.FSharp.Core.BasicInlinedOperations
    open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators

    [<DefaultAugmentation(false)>]
    [<StructuralEquality; StructuralComparison>]
    [<CompiledName("FSharpList`1")>]
    type List<'T> =
       | ([])  : 'T list
       | (::)  : Head: 'T * Tail: 'T list -> 'T list

        interface IEnumerable<'T> with
            member l.GetEnumerator() = new ListEnumerator<'T>(l) :> IEnumerator<'T>

        interface System.Collections.IEnumerable with
            member l.GetEnumerator() = (new ListEnumerator<'T>(l) :> System.Collections.IEnumerator)

        // As FSharp.Core's: the first three items, then "; ... ]".
        override l.ToString() =
            match l with
            | [] -> "[]"
            | [h1] -> String.Concat("[", showValue h1, "]")
            | [h1; h2] -> String.Concat([| "["; showValue h1; "; "; showValue h2; "]" |])
            | [h1; h2; h3] -> String.Concat([| "["; showValue h1; "; "; showValue h2; "; "; showValue h3; "]" |])
            | h1 :: h2 :: h3 :: _ -> String.Concat([| "["; showValue h1; "; "; showValue h2; "; "; showValue h3; "; ... ]" |])

    and 'T list = List<'T>

    and [<Sealed>] internal ListEnumerator<'T> (s: 'T list) =
        let mutable curr = s
        let mutable started = false

        member _.GetCurrent() =
            if started then
                match curr with
                | [] -> raise (new InvalidOperationException("Enumeration already finished"))
                | h :: _ -> h
            else
                raise (new InvalidOperationException("Enumeration not started"))

        interface IEnumerator<'T> with
            member x.Current = x.GetCurrent()

        interface System.Collections.IEnumerator with
            member _.MoveNext() =
                if started then
                    match curr with
                    | _ :: t ->
                        curr <- t
                        (match curr with [] -> false | _ -> true)
                    | _ -> false
                else
                    started <- true
                    (match curr with [] -> false | _ -> true)
            member x.Current = box (x.GetCurrent())
            member _.Reset() =
                started <- false
                curr <- s

        interface System.IDisposable with
            member _.Dispose() = ()

    type List<'T> with
        [<CompilationRepresentation(CompilationRepresentationFlags.Instance)>]
        member l.Length =
            let rec loop (l: 'T list) acc = match l with [] -> acc | _ :: t -> loop t (acc + 1)
            loop l 0
        [<CompilationRepresentation(CompilationRepresentationFlags.Instance)>]
        member l.Head = match l with a :: _ -> a | [] -> raise (InvalidOperationException("The input list was empty."))
        [<CompilationRepresentation(CompilationRepresentationFlags.Instance)>]
        member l.Tail = match l with _ :: b -> b | [] -> raise (InvalidOperationException("The input list was empty."))
        [<CompilationRepresentation(CompilationRepresentationFlags.Instance)>]
        member l.IsEmpty = match l with [] -> true | _ -> false
        static member Empty : 'T list = []
        static member Cons(head,tail) : 'T list = head :: tail


    type seq<'T> = IEnumerable<'T>
