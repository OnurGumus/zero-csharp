#nowarn "35" "42" "44" "51" "60" "61" "62" "64" "69" "77" "86" "3218" "3244" "3245" "3370"

namespace Microsoft.FSharp.Core

    open System
    open System.Collections.Generic
    open Microsoft.FSharp.Core
    open Microsoft.FSharp.Core.LanguagePrimitives
    open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicOperators
    open Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicFunctions
    open Microsoft.FSharp.Collections
    open Microsoft.FSharp.Core.BasicInlinedOperations

    [<AutoOpen>]
    module Operators =

        [<CompiledName("Unbox")>]
        let inline unbox (value: obj) = UnboxGeneric(value)

        [<CompiledName("Box")>]
        let inline box (value: 'T) = (# "box !0" type ('T) value : obj #)

        [<CompiledName("TryUnbox")>]
        let inline tryUnbox (value:obj) =
            match value with
            | :? 'T as v -> Some v
            | _ -> None

        [<CompiledName("IsNull")>]
        let inline isNull (value : 'T when 'T : null) =
            match box value with
            | null -> true
            | _ -> false

        [<CompiledName("Raise")>]
        let inline raise (exn: exn) = (# "throw" exn : 'T #)

        [<CompiledName("Not")>]
        let inline not (value: bool) = (# "ceq" value false : bool #)

        let Failure message = Exception(message)

        [<CompiledName("FailurePattern")>]
        let (|Failure|_|) (error: exn) = Some error.Message

        [<CompiledName("KeyValuePattern")>]
        let (|KeyValue|) (keyValuePair: KeyValuePair<'T, 'U>) = (keyValuePair.Key, keyValuePair.Value)

        let inline (<) x y = GenericLessThan x y
        let inline (>) x y = GenericGreaterThan x y
        let inline (>=) x y = GenericGreaterOrEqual x y
        let inline (<=) x y = GenericLessOrEqual x y
        let inline (=) x y = GenericEquality x y
        let inline (<>) x y = not (GenericEquality x y)

        [<CompiledName("Compare")>]
        let inline compare (e1: 'T) (e2: 'T) = GenericComparison e1 e2

        [<CompiledName("Max")>]
        let inline max e1 e2 = if GenericLessThan e1 e2 then e2 else e1

        [<CompiledName("Min")>]
        let inline min e1 e2 = if GenericLessThan e1 e2 then e1 else e2

        [<CompiledName("FailWith")>]
        let inline failwith message = raise (Failure(message))

        [<CompiledName("InvalidArg")>]
        let inline invalidArg (argumentName:string) (message:string) = raise (new ArgumentException(message))

        [<CompiledName("NullArg")>]
        let inline nullArg (argumentName:string) = raise (new ArgumentNullException(argumentName))

        [<CompiledName("InvalidOp")>]
        let inline invalidOp message = raise (InvalidOperationException(message))

        [<CompiledName("Infinity")>]
        let infinity = Double.PositiveInfinity

        [<CompiledName("NaN")>]
        let nan = Double.NaN

        [<CompiledName("InfinitySingle")>]
        let infinityf = Single.PositiveInfinity

        [<CompiledName("NaNSingle")>]
        let nanf = Single.NaN

        /// Ends the program with an exit code, as Environment.Exit does: output
        /// is flushed and the hooks the program added run first.
        [<CompiledName("Exit")>]
        let exit (exitcode: int) : 'T =
            Environment.Exit(exitcode)
            failwith "Environment.Exit returned"

        [<CompiledName("Reraise")>]
        let inline reraise() = unbox(# "rethrow ldnull" : obj #)

        [<CompiledName("Fst")>]
        let inline fst (a, _) = a

        [<CompiledName("Snd")>]
        let inline snd (_, b) = b

        [<CompiledName("Ignore")>]
        let inline ignore _ = ()

        [<CompiledName("Ref")>]
        let ref value = { contents = value }

        let (:=) cell value = cell.contents <- value
        let (!) cell = cell.contents

        let inline (|>) arg func = func arg
        let inline (||>) (arg1, arg2) func = func arg1 arg2
        let inline (|||>) (arg1, arg2, arg3) func = func arg1 arg2 arg3
        let inline (<|) func arg1 = func arg1
        let inline (<||) func (arg1, arg2) = func arg1 arg2
        let inline (<|||) func (arg1, arg2, arg3) = func arg1 arg2 arg3
        let inline (>>) func1 func2 x = func2 (func1 x)
        let inline (<<) func2 func1 x = func2 (func1 x)

        let (^) (s1: string) (s2: string) = String.Concat(s1, s2)

        [<CompiledName("DefaultArg")>]
        let defaultArg arg defaultValue = match arg with None -> defaultValue | Some v -> v

        [<CompiledName("DefaultValueArg")>]
        let defaultValueArg arg defaultValue = match arg with ValueNone -> defaultValue | ValueSome v -> v

        [<CompiledName("Identity")>]
        let id x = x

        [<CompiledName("Hash")>]
        let inline hash (obj: 'T) = GenericHash obj

        [<CompiledName("Sizeof")>]
        let inline sizeof<'T> = (# "sizeof !0" type('T) : int #)

        [<CompiledName("Unchecked.DefaultOf")>]
        let inline defaultof<'T> = (# "ilzero !0" type ('T) : 'T #)

        let inline castToString (x:'T) = (# "" x : string #)

        [<CompiledName("CreateSequence")>]
        let seq (sequence: seq<'T>) = sequence

        [<CompiledName("TypeOf")>]
        let inline typeof<'T> = Type.GetTypeFromHandle((# "ldtoken !0" type('T) : RuntimeTypeHandle #))

        // No generic type definitions without reflection: the type itself.
        [<CompiledName("TypeDefOf")>]
        let inline typedefof<'T> = Type.GetTypeFromHandle((# "ldtoken !0" type('T) : RuntimeTypeHandle #))

        [<CompiledName("NameOf")>]
        let inline nameof (_: 'T) : string = raise (Exception "nameof is handled by the compiler")

        [<CompiledName("MethodHandleOf")>]
        let methodhandleof (_call: ('T -> 'TResult)) : RuntimeMethodHandle = raise (NotSupportedException("methodhandleof"))

        [<CompiledName("Exponentiation")>]
        let inline ( ** ) (x: ^T) (y: ^U) : ^T =
            (^T: (static member Pow : ^T * ^U -> ^T) (x,y))
            when ^T : float = (# "" (Math.Pow((# "" x : float #), (# "" y : float #))) : ^T #)
            when ^T : float32 = (# "" (MathF.Pow((# "" x : float32 #), (# "" y : float32 #))) : ^T #)

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline (~-) (n: ^T) : ^T =
             raise (NotSupportedException("UnaryNegationDynamic"))
             when ^T : int32     = (# "neg" n  : int32 #)
             when ^T : float     = (# "neg" n  : float #)
             when ^T : float32   = (# "neg" n  : float32 #)
             when ^T : int64     = (# "neg" n  : int64 #)
             when ^T : int16     = (# "neg" n  : int16 #)
             when ^T : nativeint = (# "neg" n  : nativeint #)
             when ^T : sbyte     = (# "neg" n  : sbyte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             // That is, not in the generic implementation of '*'
             when ^T : ^T = (^T : (static member (~-) : ^T -> ^T) (n))

        let inline (+) (x: ^T) (y: ^U) : ^V =
             raise (NotSupportedException("AdditionDynamic"))
             when ^T : int32       and ^U : int32      = (# "add" x y : int32 #)
             when ^T : float       and ^U : float      = (# "add" x y : float #)
             when ^T : float32     and ^U : float32    = (# "add" x y : float32 #)
             when ^T : int64       and ^U : int64      = (# "add" x y : int64 #)
             when ^T : uint64      and ^U : uint64     = (# "add" x y : uint64 #)
             when ^T : uint32      and ^U : uint32     = (# "add" x y : uint32 #)
             when ^T : nativeint   and ^U : nativeint  = (# "add" x y : nativeint #)
             when ^T : unativeint  and ^U : unativeint = (# "add" x y : unativeint #)
             when ^T : int16       and ^U : int16      = (# "conv.i2" (# "add" x y : int32 #) : int16 #)
             when ^T : uint16      and ^U : uint16     = (# "conv.u2" (# "add" x y : uint32 #) : uint16 #)
             when ^T : char        and ^U : char       = (# "conv.u2" (# "add" x y : uint32 #) : char #)
             when ^T : sbyte       and ^U : sbyte      = (# "conv.i1" (# "add" x y : int32 #) : sbyte #)
             when ^T : byte        and ^U : byte       = (# "conv.u1" (# "add" x y : uint32 #) : byte #)
             when ^T : string      and ^U : string     = (# "" (String.Concat((# "" x : string #),(# "" y : string #))) : ^T #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = ((^T or ^U): (static member (+) : ^T * ^U -> ^V) (x,y))

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline (-) (x: ^T) (y: ^U) : ^V =
             raise (NotSupportedException("SubtractionDynamic"))
             when ^T : int32      and ^U : int32      = (# "sub" x y : int32 #)
             when ^T : float      and ^U : float      = (# "sub" x y : float #)
             when ^T : float32    and ^U : float32    = (# "sub" x y : float32 #)
             when ^T : int64      and ^U : int64      = (# "sub" x y : int64 #)
             when ^T : uint64     and ^U : uint64     = (# "sub" x y : uint64 #)
             when ^T : uint32     and ^U : uint32     = (# "sub" x y : uint32 #)
             when ^T : nativeint  and ^U : nativeint  = (# "sub" x y : nativeint #)
             when ^T : unativeint and ^U : unativeint = (# "sub" x y : unativeint #)
             when ^T : int16       and ^U : int16      = (# "conv.i2" (# "sub" x y : int32 #) : int16 #)
             when ^T : uint16      and ^U : uint16     = (# "conv.u2" (# "sub" x y : uint32 #) : uint16 #)
             when ^T : char        and ^U : char       = (# "conv.u2" (# "sub" x y : uint32 #) : char #)
             when ^T : sbyte       and ^U : sbyte      = (# "conv.i1" (# "sub" x y : int32 #) : sbyte #)
             when ^T : byte        and ^U : byte       = (# "conv.u1" (# "sub" x y : uint32 #) : byte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = ((^T or ^U): (static member (-) : ^T * ^U -> ^V) (x,y))

        let inline ( * ) (x: ^T) (y: ^U) : ^V =
             raise (NotSupportedException("MultiplyDynamic"))
             when ^T : int32      and ^U : int32      = (# "mul" x y : int32 #)
             when ^T : float      and ^U : float      = (# "mul" x y : float #)
             when ^T : float32    and ^U : float32    = (# "mul" x y : float32 #)
             when ^T : int64      and ^U : int64      = (# "mul" x y : int64 #)
             when ^T : uint64     and ^U : uint64     = (# "mul" x y : uint64 #)
             when ^T : uint32     and ^U : uint32     = (# "mul" x y : uint32 #)
             when ^T : nativeint  and ^U : nativeint  = (# "mul" x y : nativeint #)
             when ^T : unativeint and ^U : unativeint = (# "mul" x y : unativeint #)
             when ^T : int16       and ^U : int16      = (# "conv.i2" (# "mul" x y : int32 #) : int16 #)
             when ^T : uint16      and ^U : uint16     = (# "conv.u2" (# "mul" x y : uint32 #) : uint16 #)
             when ^T : sbyte       and ^U : sbyte      = (# "conv.i1" (# "mul" x y : int32 #) : sbyte #)
             when ^T : byte        and ^U : byte       = (# "conv.u1" (# "mul" x y : uint32 #) : byte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = ((^T or ^U): (static member (*) : ^T * ^U -> ^V) (x,y))

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline ( / ) (x: ^T) (y: ^U) : ^V =
             raise (NotSupportedException("DivisionDynamic"))
             when ^T : int32       and ^U : int32      = (# "div" x y : int32 #)
             when ^T : float       and ^U : float      = (# "div" x y : float #)
             when ^T : float32     and ^U : float32    = (# "div" x y : float32 #)
             when ^T : int64       and ^U : int64      = (# "div" x y : int64 #)
             when ^T : uint64      and ^U : uint64     = (# "div.un" x y : uint64 #)
             when ^T : uint32      and ^U : uint32     = (# "div.un" x y : uint32 #)
             when ^T : nativeint   and ^U : nativeint  = (# "div" x y : nativeint #)
             when ^T : unativeint  and ^U : unativeint = (# "div.un" x y : unativeint #)
             when ^T : int16       and ^U : int16      = (# "conv.i2" (# "div" x y : int32 #) : int16 #)
             when ^T : uint16      and ^U : uint16     = (# "conv.u2" (# "div.un" x y : uint32 #) : uint16 #)
             when ^T : sbyte       and ^U : sbyte      = (# "conv.i1" (# "div" x y : int32 #) : sbyte #)
             when ^T : byte        and ^U : byte       = (# "conv.u1" (# "div.un" x y : uint32 #) : byte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = ((^T or ^U): (static member (/) : ^T * ^U -> ^V) (x,y))

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline ( % ) (x: ^T) (y: ^U) : ^V =
             raise (NotSupportedException("ModulusDynamic"))
             when ^T : int32       and ^U : int32      = (# "rem" x y : int32 #)
             when ^T : float       and ^U : float      = (# "rem" x y : float #)
             when ^T : float32     and ^U : float32    = (# "rem" x y : float32 #)
             when ^T : int64       and ^U : int64      = (# "rem" x y : int64 #)
             when ^T : uint64      and ^U : uint64     = (# "rem.un" x y : uint64 #)
             when ^T : uint32      and ^U : uint32     = (# "rem.un" x y : uint32 #)
             when ^T : nativeint   and ^U : nativeint  = (# "rem" x y : nativeint #)
             when ^T : unativeint  and ^U : unativeint = (# "rem.un" x y : unativeint #)
             when ^T : int16       and ^U : int16      = (# "conv.i2" (# "rem"    x y : int32  #) : int16  #)
             when ^T : uint16      and ^U : uint16     = (# "conv.u2" (# "rem.un" x y : uint32 #) : uint16 #)
             when ^T : sbyte       and ^U : sbyte      = (# "conv.i1" (# "rem"    x y : int32  #) : sbyte  #)
             when ^T : byte        and ^U : byte       = (# "conv.u1" (# "rem.un" x y : uint32 #) : byte   #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = ((^T or ^U): (static member (%) : ^T * ^U -> ^V) (x,y))

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline (~+) (value: ^T) : ^T =
             value
             when ^T : int32      = value
             when ^T : float      = value
             when ^T : float32    = value
             when ^T : int64      = value
             when ^T : uint64     = value
             when ^T : uint32     = value
             when ^T : int16      = value
             when ^T : uint16     = value
             when ^T : nativeint  = value
             when ^T : unativeint = value
             when ^T : sbyte      = value
             when ^T : byte       = value
             when ^T : ^T = (^T: (static member (~+) : ^T -> ^T) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline (<<<) (value: ^T) (shift:int) : ^T =
             raise (NotSupportedException("LeftShiftDynamic"))
             when ^T : int32      = (# "shl" value (mask shift 31) : int #)
             when ^T : uint32     = (# "shl" value (mask shift 31) : uint32 #)
             when ^T : int64      = (# "shl" value (mask shift 63) : int64 #)
             when ^T : uint64     = (# "shl" value (mask shift 63) : uint64 #)
             when ^T : nativeint  = (# "shl" value shift : nativeint #)
             when ^T : unativeint = (# "shl" value shift : unativeint #)
             when ^T : int16      = (# "conv.i2" (# "shl" value (mask shift 15) : int32  #) : int16 #)
             when ^T : uint16     = (# "conv.u2" (# "shl" value (mask shift 15) : uint32 #) : uint16 #)
             when ^T : sbyte      = (# "conv.i1" (# "shl" value (mask shift 7 ) : int32  #) : sbyte #)
             when ^T : byte       = (# "conv.u1" (# "shl" value (mask shift 7 ) : uint32 #) : byte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = (^T: (static member (<<<) : ^T * int -> ^T) (value,shift))

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline (>>>) (value: ^T) (shift:int) : ^T =
             raise (NotSupportedException("RightShiftDynamic"))
             when ^T : int32      = (# "shr"    value (mask shift 31) : int32 #)
             when ^T : uint32     = (# "shr.un" value (mask shift 31) : uint32 #)
             when ^T : int64      = (# "shr"    value (mask shift 63) : int64 #)
             when ^T : uint64     = (# "shr.un" value (mask shift 63) : uint64 #)
             when ^T : nativeint  = (# "shr"    value shift : nativeint #)
             when ^T : unativeint = (# "shr.un" value shift : unativeint #)
             when ^T : int16      = (# "conv.i2" (# "shr"    value (mask shift 15) : int32  #) : int16 #)
             when ^T : uint16     = (# "conv.u2" (# "shr.un" value (mask shift 15) : uint32 #) : uint16 #)
             when ^T : sbyte      = (# "conv.i1" (# "shr"    value (mask shift 7 ) : int32  #) : sbyte #)
             when ^T : byte       = (# "conv.u1" (# "shr.un" value (mask shift 7 ) : uint32 #) : byte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = (^T: (static member (>>>) : ^T * int -> ^T) (value, shift))

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline (&&&) (x: ^T) (y: ^T) : ^T =
             raise (NotSupportedException("BitwiseAndDynamic"))
             when ^T : int32      = (# "and" x y : int32 #)
             when ^T : int64      = (# "and" x y : int64 #)
             when ^T : uint64     = (# "and" x y : uint64 #)
             when ^T : uint32     = (# "and" x y : uint32 #)
             when ^T : int16      = (# "and" x y : int16 #)
             when ^T : uint16     = (# "and" x y : uint16 #)
             when ^T : nativeint  = (# "and" x y : nativeint #)
             when ^T : unativeint = (# "and" x y : unativeint #)
             when ^T : sbyte      = (# "and" x y : sbyte #)
             when ^T : byte       = (# "and" x y : byte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = (^T: (static member (&&&) : ^T * ^T -> ^T) (x, y))

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline (|||) (x: ^T) (y: ^T) : ^T =
             raise (NotSupportedException("BitwiseOrDynamic"))
             when ^T : int32      = (# "or" x y : int32 #)
             when ^T : int64      = (# "or" x y : int64 #)
             when ^T : uint64     = (# "or" x y : uint64 #)
             when ^T : uint32     = (# "or" x y : uint32 #)
             when ^T : int16      = (# "or" x y : int16 #)
             when ^T : uint16     = (# "or" x y : uint16 #)
             when ^T : nativeint  = (# "or" x y : nativeint #)
             when ^T : unativeint = (# "or" x y : unativeint #)
             when ^T : sbyte      = (# "or" x y : sbyte #)
             when ^T : byte       = (# "or" x y : byte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = (^T: (static member (|||) : ^T * ^T -> ^T) (x, y))

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline (^^^) (x: ^T) (y: ^T) : ^T =
             raise (NotSupportedException("ExclusiveOrDynamic"))
             when ^T : int32      = (# "xor" x y : int32 #)
             when ^T : int64      = (# "xor" x y : int64 #)
             when ^T : uint64     = (# "xor" x y : uint64 #)
             when ^T : uint32     = (# "xor" x y : uint32 #)
             when ^T : int16      = (# "xor" x y : int16 #)
             when ^T : uint16     = (# "xor" x y : uint16 #)
             when ^T : nativeint  = (# "xor" x y : nativeint #)
             when ^T : unativeint = (# "xor" x y : unativeint #)
             when ^T : sbyte      = (# "xor" x y : sbyte #)
             when ^T : byte       = (# "xor" x y : byte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = (^T: (static member (^^^) : ^T * ^T -> ^T) (x, y))

        [<NoDynamicInvocation(isLegacy=true)>]
        let inline (~~~) (value: ^T) : ^T =
             raise (NotSupportedException("LogicalNotDynamic"))
             when ^T : int32      = (# "not" value : int32 #)
             when ^T : int64      = (# "not" value : int64 #)
             when ^T : uint64     = (# "not" value : uint64 #)
             when ^T : uint32     = (# "not" value : uint32 #)
             when ^T : nativeint  = (# "not" value : nativeint #)
             when ^T : unativeint = (# "not" value : unativeint #)
             when ^T : int16      = (# "conv.i2" (# "not" value : int32  #) : int16 #)
             when ^T : uint16     = (# "conv.u2" (# "not" value : uint32 #) : uint16 #)
             when ^T : sbyte      = (# "conv.i1" (# "not" value : int32  #) : sbyte #)
             when ^T : byte       = (# "conv.u1" (# "not" value : uint32 #) : byte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = (^T: (static member (~~~) : ^T -> ^T) (value))


        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToByte")>]
        let inline byte (value: ^T) : byte =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float      = (# "conv.u1" value  : byte #)
             when ^T : float32    = (# "conv.u1" value  : byte #)
             when ^T : int64      = (# "conv.u1" value  : byte #)
             when ^T : int32      = (# "conv.u1" value  : byte #)
             when ^T : int16      = (# "conv.u1" value  : byte #)
             when ^T : nativeint  = (# "conv.u1" value  : byte #)
             when ^T : sbyte      = (# "conv.u1" value  : byte #)
             when ^T : uint64     = (# "conv.u1" value  : byte #)
             when ^T : uint32     = (# "conv.u1" value  : byte #)
             when ^T : uint16     = (# "conv.u1" value  : byte #)
             when ^T : char       = (# "conv.u1" value  : byte #)
             when ^T : unativeint = (# "conv.u1" value  : byte #)
             when ^T : byte       = (# "" value  : byte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> byte) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToSByte")>]
        let inline sbyte (value: ^T) : sbyte =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float     = (# "conv.i1" value  : sbyte #)
             when ^T : float32   = (# "conv.i1" value  : sbyte #)
             when ^T : int64     = (# "conv.i1" value  : sbyte #)
             when ^T : int32     = (# "conv.i1" value  : sbyte #)
             when ^T : int16     = (# "conv.i1" value  : sbyte #)
             when ^T : nativeint = (# "conv.i1" value  : sbyte #)
             when ^T : sbyte      = (# "" value  : sbyte #)
             when ^T : uint64     = (# "conv.i1" value  : sbyte #)
             when ^T : uint32     = (# "conv.i1" value  : sbyte #)
             when ^T : uint16     = (# "conv.i1" value  : sbyte #)
             when ^T : char       = (# "conv.i1" value  : sbyte #)
             when ^T : unativeint = (# "conv.i1" value  : sbyte #)
             when ^T : byte     = (# "conv.i1" value  : sbyte #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> sbyte) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToUInt16")>]
        let inline uint16 (value: ^T) : uint16 =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float     = (# "conv.u2" value  : uint16 #)
             when ^T : float32   = (# "conv.u2" value  : uint16 #)
             when ^T : int64     = (# "conv.u2" value  : uint16 #)
             when ^T : int32     = (# "conv.u2" value  : uint16 #)
             when ^T : int16     = (# "conv.u2" value  : uint16 #)
             when ^T : nativeint = (# "conv.u2" value  : uint16 #)
             when ^T : sbyte     = (# "conv.u2" value  : uint16 #)
             when ^T : uint64     = (# "conv.u2" value  : uint16 #)
             when ^T : uint32     = (# "conv.u2" value  : uint16 #)
             when ^T : uint16     = (# "" value  : uint16 #)
             when ^T : char       = (# "" value  : uint16 #)
             when ^T : unativeint = (# "conv.u2" value  : uint16 #)
             when ^T : byte     = (# "conv.u2" value  : uint16 #)
             // According to the somewhat subtle rules of static optimizations,
             // this condition is used whenever ^T is resolved to a nominal type or witnesses are available
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> uint16) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToInt16")>]
        let inline int16 (value: ^T) : int16 =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float     = (# "conv.i2" value  : int16 #)
             when ^T : float32   = (# "conv.i2" value  : int16 #)
             when ^T : int64     = (# "conv.i2" value  : int16 #)
             when ^T : int32     = (# "conv.i2" value  : int16 #)
             when ^T : int16     = (# "" value  : int16 #)
             when ^T : nativeint = (# "conv.i2" value  : int16 #)
             when ^T : sbyte     = (# "conv.i2" value  : int16 #)
             when ^T : uint64    = (# "conv.i2" value  : int16 #)
             when ^T : uint32    = (# "conv.i2" value  : int16 #)
             when ^T : uint16    = (# "conv.i2" value  : int16 #)
             when ^T : char      = (# "conv.i2" value  : int16 #)
             when ^T : unativeint = (# "conv.i2" value  : int16 #)
             when ^T : byte     = (# "conv.i2" value  : int16 #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> int16) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToUInt32")>]
        let inline uint32 (value: ^T) : uint32 =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float     = (# "conv.u4" value  : uint32 #)
             when ^T : float32   = (# "conv.u4" value  : uint32 #)
             when ^T : int64     = (# "conv.u4" value  : uint32 #)
             when ^T : nativeint = (# "conv.u4" value  : uint32 #)
             // For integers shorter that 32 bits, we must first
             // sign-widen the signed integer to 32 bits, and then
             // "convert" from signed int32 to unsigned int32
             // This is a no-op on IL stack (ECMA 335 Part III 1.5 Tables 8 & 9)
             when ^T : int32     = (# "" value : uint32 #)
             when ^T : int16     = (# "" value : uint32 #)
             when ^T : sbyte     = (# "" value : uint32 #)
             when ^T : uint64     = (# "conv.u4" value  : uint32 #)
             when ^T : uint32     = (# "" value  : uint32 #)
             when ^T : uint16     = (# "conv.u4" value  : uint32 #)
             when ^T : char       = (# "conv.u4" value  : uint32 #)
             when ^T : unativeint = (# "conv.u4" value  : uint32 #)
             when ^T : byte     = (# "conv.u4" value  : uint32 #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> uint32) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToInt32")>]
        let inline int32 (value: ^T) : int32 =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float     = (# "conv.i4" value  : int32 #)
             when ^T : float32   = (# "conv.i4" value  : int32 #)
             when ^T : int64     = (# "conv.i4" value  : int32 #)
             when ^T : nativeint = (# "conv.i4" value  : int32 #)
             // For integers shorter that 32 bits, we sign-widen the signed integer to 32 bits
             // This is a no-op on IL stack (ECMA 335 Part III 1.5 Tables 8 & 9)
             when ^T : int32     = (# "" value  : int32 #)
             when ^T : int16     = (# "" value  : int32 #)
             when ^T : sbyte     = (# "" value  : int32 #)
             when ^T : uint64    = (# "conv.i4" value  : int32 #)
             when ^T : uint32    = (# "" value  : int32 #) // Signed<->Unsigned conversion is a no-op on IL stack
             when ^T : uint16    = (# "conv.i4" value  : int32 #)
             when ^T : char      = (# "conv.i4" value  : int32 #)
             when ^T : unativeint = (# "conv.i4" value  : int32 #)
             when ^T : byte     = (# "conv.i4" value  : int32 #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> int32) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToUInt64")>]
        let inline uint64 (value: ^T) : uint64 =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float     = (# "conv.u8" value  : uint64 #)
             when ^T : float32   = (# "conv.u8" value  : uint64 #)
             // we must first sign-widen the signed integer to 64 bits, and then
             // "convert" from signed int64 to unsigned int64
             // conv.i8 sign-widens the input, and on IL stack,
             // conversion from signed to unsigned is a no-op (ECMA 335 Part III 1.5 Table 8)
             when ^T : int64     = (# "" value  : uint64 #)
             when ^T : int32     = (# "conv.i8" value  : uint64 #)
             when ^T : int16     = (# "conv.i8" value  : uint64 #)
             when ^T : nativeint = (# "conv.i8" value  : uint64 #)
             when ^T : sbyte     = (# "conv.i8" value  : uint64 #)
             when ^T : uint64    = (# "" value  : uint64 #)
             when ^T : uint32    = (# "conv.u8" value  : uint64 #)
             when ^T : uint16    = (# "conv.u8" value  : uint64 #)
             when ^T : char      = (# "conv.u8" value  : uint64 #)
             when ^T : unativeint = (# "conv.u8" value  : uint64 #)
             when ^T : byte     = (# "conv.u8" value  : uint64 #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> uint64) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToInt64")>]
        let inline int64 (value: ^T) : int64 =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float     = (# "conv.i8" value  : int64 #)
             when ^T : float32   = (# "conv.i8" value  : int64 #)
             when ^T : int64     = (# "" value  : int64 #)
             when ^T : int32     = (# "conv.i8" value  : int64 #)
             when ^T : int16     = (# "conv.i8" value  : int64 #)
             when ^T : nativeint = (# "conv.i8" value  : int64 #)
             when ^T : sbyte     = (# "conv.i8" value  : int64 #)
             // When converting unsigned integer, we zero-widen them, NOT sign-widen.
             // No-op for uint64, conv.u8 for uint32, for smaller types conv.u8 and conv.i8 are identical.
             // For nativeint, conv.u8 works correctly both in 32 bit and 64 bit case.
             when ^T : uint64     = (# "" value  : int64 #)
             when ^T : uint32     = (# "conv.u8" value  : int64 #)
             when ^T : uint16     = (# "conv.u8" value  : int64 #)
             when ^T : char       = (# "conv.u8" value  : int64 #)
             when ^T : unativeint = (# "conv.u8" value  : int64 #)
             when ^T : byte     = (# "conv.u8" value  : int64 #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> int64) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToSingle")>]
        let inline float32 (value: ^T) : float32 =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float     = (# "conv.r4" value  : float32 #)
             // NOTE: float32 should convert its argument to 32-bit float even when applied to a higher precision float stored in a register. See devdiv2#49888.
             when ^T : float32   = (# "conv.r4" value  : float32 #)
             when ^T : int64     = (# "conv.r4" value  : float32 #)
             when ^T : int32     = (# "conv.r4" value  : float32 #)
             when ^T : int16     = (# "conv.r4" value  : float32 #)
             when ^T : nativeint = (# "conv.r4" value  : float32 #)
             when ^T : sbyte     = (# "conv.r4" value  : float32 #)
             when ^T : uint64    = (# "conv.r.un conv.r4" value  : float32 #)
             when ^T : uint32    = (# "conv.r.un conv.r4" value  : float32 #)
             when ^T : uint16    = (# "conv.r.un conv.r4" value  : float32 #)
             when ^T : char      = (# "conv.r.un conv.r4" value  : float32 #)
             when ^T : unativeint = (# "conv.r.un conv.r4" value  : float32 #)
             when ^T : byte     = (# "conv.r.un conv.r4" value  : float32 #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> float32) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToDouble")>]
        let inline float (value: ^T) : float =
             raise (NotSupportedException("ExplicitDynamic"))
             // NOTE: float should convert its argument to 64-bit float even when applied to a higher precision float stored in a register. See devdiv2#49888.
             when ^T : float     = (# "conv.r8" value  : float #)
             when ^T : float32   = (# "conv.r8" value  : float #)
             when ^T : int64     = (# "conv.r8" value  : float #)
             when ^T : int32     = (# "conv.r8" value  : float #)
             when ^T : int16     = (# "conv.r8" value  : float #)
             when ^T : nativeint = (# "conv.r8" value  : float #)
             when ^T : sbyte     = (# "conv.r8" value  : float #)
             when ^T : uint64    = (# "conv.r.un conv.r8" value  : float #)
             when ^T : uint32    = (# "conv.r.un conv.r8" value  : float #)
             when ^T : uint16    = (# "conv.r.un conv.r8" value  : float #)
             when ^T : char      = (# "conv.r.un conv.r8" value  : float #)
             when ^T : unativeint = (# "conv.r.un conv.r8" value  : float #)
             when ^T : byte      = (# "conv.r.un conv.r8" value  : float #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> float) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToUIntPtr")>]
        let inline unativeint (value: ^T) : unativeint =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float     = (# "conv.u" value  : unativeint #)
             when ^T : float32   = (# "conv.u" value  : unativeint #)
             // Narrower signed types we sign-extend.
             // Same length signed types we leave as such (so -1 gets reinterpreted as unsigned MaxValue).
             // Wider signed types we truncate.
             // conv.i does just that for both 32 and 64 bit case of nativeint, and conversion from nativeint is no-op.
             when ^T : int64     = (# "conv.i" value  : unativeint #)
             when ^T : int32     = (# "conv.i" value  : unativeint #)
             when ^T : int16     = (# "conv.i" value  : unativeint #)
             when ^T : nativeint = (# "" value  : unativeint #)
             when ^T : sbyte     = (# "conv.i" value  : unativeint #)
             when ^T : uint64    = (# "conv.u" value  : unativeint #)
             when ^T : uint32    = (# "conv.u" value  : unativeint #)
             when ^T : uint16    = (# "conv.u" value  : unativeint #)
             when ^T : char      = (# "conv.u" value  : unativeint #)
             when ^T : unativeint = (# "" value  : unativeint #)
             when ^T : byte       = (# "conv.u" value  : unativeint #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> unativeint) (value))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToIntPtr")>]
        let inline nativeint (value: ^T) : nativeint =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float      = (# "conv.i" value  : nativeint #)
             when ^T : float32    = (# "conv.i" value  : nativeint #)
             when ^T : int64      = (# "conv.i" value  : nativeint #)
             when ^T : int32      = (# "conv.i" value  : nativeint #)
             when ^T : int16      = (# "conv.i" value  : nativeint #)
             when ^T : nativeint  = (# "" value  : nativeint #)
             when ^T : sbyte      = (# "conv.i" value  : nativeint #)
             // Narrower unsigned types we zero-extend.
             // Same length unsigned types we leave as such (so unsigned MaxValue (all-bits-set) gets reinterpreted as -1).
             // Wider unsigned types we truncate.
             // conv.u does just that for both 32- and 64-bit-wide nativeint, and conversion from unativeint is no-op.
             when ^T : uint64     = (# "conv.u" value  : nativeint #)
             when ^T : uint32     = (# "conv.u" value  : nativeint #)
             when ^T : uint16     = (# "conv.u" value  : nativeint #)
             when ^T : char       = (# "conv.u" value  : nativeint #)
             when ^T : unativeint = (# "" value  : nativeint #)
             when ^T : byte       = (# "conv.i" value  : nativeint #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> nativeint) (value))


        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToChar")>]
        let inline char (value: ^T) : char =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : float      = (# "conv.u2" value  : char #)
             when ^T : float32    = (# "conv.u2" value  : char #)
             when ^T : int64      = (# "conv.u2" value  : char #)
             when ^T : int32      = (# "conv.u2" value  : char #)
             when ^T : int16      = (# "conv.u2" value  : char #)
             when ^T : nativeint  = (# "conv.u2" value  : char #)
             when ^T : sbyte      = (# "conv.u2" value  : char #)
             when ^T : uint64     = (# "conv.u2" value  : char #)
             when ^T : uint32     = (# "conv.u2" value  : char #)
             when ^T : uint16     = (# "" value  : char #)
             when ^T : char       = (# "" value  : char #)
             when ^T : unativeint = (# "conv.u2" value  : char #)
             when ^T : byte       = (# "conv.u2" value  : char #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> char) (value))

        [<CompiledName("Append")>]
        let (@) list1 list2 =
            let rec rev acc l = match l with [] -> acc | h :: t -> rev (h :: acc) t
            match list1 with
            | [] -> list2
            | _ ->
                match list2 with
                | [] -> list1
                | _ -> rev list2 (rev [] list1) |> fun _ ->
                       let mutable acc = list2
                       let mutable r = rev [] list1
                       while (match r with [] -> false | _ -> true) do
                           match r with
                           | h :: t -> acc <- h :: acc; r <- t
                           | [] -> ()
                       acc

        [<CompiledName("Increment")>]
        let incr (cell: int ref) = cell.contents <- cell.contents + 1

        [<CompiledName("Decrement")>]
        let decr (cell: int ref) = cell.contents <- cell.contents - 1

        [<CompiledName("Int")>]
        let inline int value = int32 value

        [<CompiledName("UInt")>]
        let inline uint value = uint32 value

        [<CompiledName("ToEnum")>]
        let inline enum< ^T when ^T : enum<int32> > (value:int32) : ^T = EnumOfValue value

        [<CompiledName("ToString")>]
        let inline string (value: 'T) =
             match box value with
             | null -> ""
             | o -> o.ToString()
             when 'T : string = (match castToString value with null -> "" | s -> s)

        [<CompiledName("Abs")>]
        let inline abs (value: ^T) : ^T =
             (^T: (static member Abs : ^T -> ^T) (value))
             when ^T : int32 = let x : int32 = retype value in retype (if x < 0 then -x else x)
             when ^T : int64 = let x : int64 = retype value in retype (if x < 0L then -x else x)
             when ^T : float = let x : float = retype value in retype (if x < 0.0 then -x else x)
             when ^T : float32 = let x : float32 = retype value in retype (if x < 0.0f then -x else x)
             when ^T : int16 = let x : int16 = retype value in retype (if x < 0s then -x else x)
             when ^T : sbyte = let x : sbyte = retype value in retype (if x < 0y then -x else x)
             when ^T : decimal = retype (Decimal.Abs(retype value : decimal))

        [<NoDynamicInvocation(isLegacy=true)>]
        [<CompiledName("ToDecimal")>]
        let inline decimal (value: ^T) : decimal =
             raise (NotSupportedException("ExplicitDynamic"))
             when ^T : string     = Decimal.Parse(castToString value)
             when ^T : float      = Decimal((# "" value : float #))
             when ^T : float32    = Decimal((# "" value : float32 #))
             when ^T : int64      = Decimal((# "" value : int64 #))
             when ^T : int32      = Decimal((# "" value : int32 #))
             when ^T : int16      = Decimal((# "conv.i4" value : int32 #))
             when ^T : sbyte      = Decimal((# "conv.i4" value : int32 #))
             when ^T : nativeint  = Decimal((# "conv.i8" value : int64 #))
             when ^T : uint64     = Decimal((# "" value : uint64 #))
             when ^T : uint32     = Decimal((# "" value : uint32 #))
             when ^T : uint16     = Decimal((# "conv.u4" value : uint32 #))
             when ^T : byte       = Decimal((# "conv.u4" value : uint32 #))
             when ^T : char       = Decimal((# "conv.u4" value : uint32 #))
             when ^T : unativeint = Decimal((# "conv.u8" value : uint64 #))
             when ^T : decimal    = (# "" value : decimal #)
             when ^T : ^T = (^T : (static member op_Explicit: ^T -> decimal) (value))

        [<CompiledName("Sign")>]
        let inline sign (value: ^T) : int =
             (^T: (member Sign : int) (value))
             when ^T : int32 = (let x : int32 = retype value in if x < 0 then -1 elif x > 0 then 1 else 0)
             when ^T : int64 = (let x : int64 = retype value in if x < 0L then -1 elif x > 0L then 1 else 0)
             when ^T : int16 = (let x : int16 = retype value in if x < 0s then -1 elif x > 0s then 1 else 0)
             when ^T : sbyte = (let x : sbyte = retype value in if x < 0y then -1 elif x > 0y then 1 else 0)
             when ^T : nativeint = (let x : nativeint = retype value in if x < 0n then -1 elif x > 0n then 1 else 0)
             when ^T : float =
                (let x : float = retype value
                 if x < 0.0 then -1 elif x > 0.0 then 1 elif x = 0.0 then 0
                 else raise (ArithmeticException("Function does not accept floating point Not-a-Number values.")))
             when ^T : float32 =
                (let x : float32 = retype value
                 if x < 0.0f then -1 elif x > 0.0f then 1 elif x = 0.0f then 0
                 else raise (ArithmeticException("Function does not accept floating point Not-a-Number values.")))
             when ^T : decimal = Decimal.Sign(retype value : decimal)

        [<CompiledName("Round")>]
        let inline round (value: ^T) : ^T =
             (^T: (static member Round : ^T -> ^T) (value))
             when ^T : float = (retype (Math.Round(retype value : float)) : ^T)
             when ^T : float32 = (retype (MathF.Round(retype value : float32)) : ^T)
             when ^T : decimal = (retype (Decimal.Round(retype value : decimal)) : ^T)

        [<CompiledName("Floor")>]
        let inline floor (value: ^T) : ^T =
             (^T: (static member Floor : ^T -> ^T) (value))
             when ^T : float = (retype (Math.Floor(retype value : float)) : ^T)
             when ^T : float32 = (retype (MathF.Floor(retype value : float32)) : ^T)
             when ^T : decimal = (retype (Decimal.Floor(retype value : decimal)) : ^T)

        [<CompiledName("Ceiling")>]
        let inline ceil (value: ^T) : ^T =
             (^T: (static member Ceiling : ^T -> ^T) (value))
             when ^T : float = (retype (Math.Ceiling(retype value : float)) : ^T)
             when ^T : float32 = (retype (MathF.Ceiling(retype value : float32)) : ^T)
             when ^T : decimal = (retype (Decimal.Ceiling(retype value : decimal)) : ^T)

        [<CompiledName("Truncate")>]
        let inline truncate (value: ^T) : ^T =
             (^T: (static member Truncate : ^T -> ^T) (value))
             when ^T : float = (retype (Math.Truncate(retype value : float)) : ^T)
             when ^T : float32 = (retype (MathF.Truncate(retype value : float32)) : ^T)
             when ^T : decimal = (retype (Decimal.Truncate(retype value : decimal)) : ^T)

        // float32 goes through float where zerocore's MathF lacks the function.
        [<CompiledName("Sqrt")>]
        let inline sqrt (value: ^T) : ^U =
             (^T: (static member Sqrt : ^T -> ^U) (value))
             when ^T : float = (retype (Math.Sqrt(retype value : float)) : ^U)
             when ^T : float32 = (retype (MathF.Sqrt(retype value : float32)) : ^U)

        [<CompiledName("Exp")>]
        let inline exp (value: ^T) : ^T =
             (^T: (static member Exp : ^T -> ^T) (value))
             when ^T : float = (retype (Math.Exp(retype value : float)) : ^T)
             when ^T : float32 = (retype (MathF.Exp(retype value : float32)) : ^T)

        [<CompiledName("Log")>]
        let inline log (value: ^T) : ^T =
             (^T: (static member Log : ^T -> ^T) (value))
             when ^T : float = (retype (Math.Log(retype value : float)) : ^T)
             when ^T : float32 = (retype (MathF.Log(retype value : float32)) : ^T)

        [<CompiledName("Log10")>]
        let inline log10 (value: ^T) : ^T =
             (^T: (static member Log10 : ^T -> ^T) (value))
             when ^T : float = (retype (Math.Log10(retype value : float)) : ^T)
             when ^T : float32 = (retype (# "conv.r4" (Math.Log10((# "conv.r8" value : float #))) : float32 #) : ^T)

        [<CompiledName("Sin")>]
        let inline sin (value: ^T) : ^T =
             (^T: (static member Sin : ^T -> ^T) (value))
             when ^T : float = (retype (Math.Sin(retype value : float)) : ^T)
             when ^T : float32 = (retype (MathF.Sin(retype value : float32)) : ^T)

        [<CompiledName("Cos")>]
        let inline cos (value: ^T) : ^T =
             (^T: (static member Cos : ^T -> ^T) (value))
             when ^T : float = (retype (Math.Cos(retype value : float)) : ^T)
             when ^T : float32 = (retype (MathF.Cos(retype value : float32)) : ^T)

        [<CompiledName("Tan")>]
        let inline tan (value: ^T) : ^T =
             (^T: (static member Tan : ^T -> ^T) (value))
             when ^T : float = (retype (Math.Tan(retype value : float)) : ^T)
             when ^T : float32 = (retype (# "conv.r4" (Math.Tan((# "conv.r8" value : float #))) : float32 #) : ^T)

        [<CompiledName("Atan2")>]
        let inline atan2 (y: ^T) (x: ^T) : ^U =
             (^T: (static member Atan2 : ^T * ^T -> ^U) (y, x))
             when ^T : float = (retype (Math.Atan2((retype y : float), (retype x : float))) : ^U)
             when ^T : float32 = (retype (# "conv.r4" (Math.Atan2((# "conv.r8" y : float #), (# "conv.r8" x : float #))) : float32 #) : ^U)

        // x to the power n, by squaring; a negative n gives 1 / x^-n.
        [<CompiledName("PowInteger")>]
        let inline pown (x: ^T) (n: int) : ^T =
             let mutable result = GenericOne< ^T >
             let mutable b = if n < 0 then GenericOne< ^T > / x else x
             let mutable e = if n < 0 then 0 - n else n
             while e > 0 do
                 if (e &&& 1) = 1 then result <- result * b
                 b <- b * b
                 e <- e >>> 1
             result

        // From dotnet/fsharp's prim-types.fs (MIT): arithmetic that ends the program
        // on overflow (zero mode can't catch the OverflowException).
        module Checked =

            let inline (+) (x: ^T) (y: ^U) : ^V =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : int32       and ^U : int32      = (# "add.ovf" x y : int32 #)
                 when ^T : float       and ^U : float      = (# "add" x y : float #)
                 when ^T : float32     and ^U : float32    = (# "add" x y : float32 #)
                 when ^T : int64       and ^U : int64      = (# "add.ovf" x y : int64 #)
                 when ^T : uint64      and ^U : uint64     = (# "add.ovf.un" x y : uint64 #)
                 when ^T : uint32      and ^U : uint32     = (# "add.ovf.un" x y : uint32 #)
                 when ^T : nativeint   and ^U : nativeint  = (# "add.ovf" x y : nativeint #)
                 when ^T : unativeint  and ^U : unativeint = (# "add.ovf.un" x y : unativeint #)
                 when ^T : int16       and ^U : int16      = (# "conv.ovf.i2" (# "add.ovf" x y : int32 #) : int16 #)
                 when ^T : uint16      and ^U : uint16     = (# "conv.ovf.u2.un" (# "add.ovf.un" x y : uint32 #) : uint16 #)
                 when ^T : char        and ^U : char       = (# "conv.ovf.u2.un" (# "add.ovf.un" x y : uint32 #) : char #)
                 when ^T : sbyte       and ^U : sbyte      = (# "conv.ovf.i1" (# "add.ovf" x y : int32 #) : sbyte #)
                 when ^T : byte        and ^U : byte       = (# "conv.ovf.u1.un" (# "add.ovf.un" x y : uint32 #) : byte #)
                 when ^T : decimal     and ^U : decimal    = (# "" (Decimal.op_Addition((# "" x : decimal #),(# "" y : decimal #))) : ^V #)
                 // According to the somewhat subtle rules of static optimizations,
                 // this condition is used whenever ^T is resolved to a nominal type
                 // That is, not in the generic implementation of '+'
                 when ^T : ^T = ((^T or ^U): (static member (+) : ^T * ^U -> ^V) (x,y))

            let inline (-) (x: ^T) (y: ^U) : ^V =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : int32      and ^U : int32      = (# "sub.ovf" x y : int32 #)
                 when ^T : float      and ^U : float      = (# "sub" x y : float #)
                 when ^T : float32    and ^U : float32    = (# "sub" x y : float32 #)
                 when ^T : int64      and ^U : int64      = (# "sub.ovf" x y : int64 #)
                 when ^T : uint64     and ^U : uint64     = (# "sub.ovf.un" x y : uint64 #)
                 when ^T : uint32     and ^U : uint32     = (# "sub.ovf.un" x y : uint32 #)
                 when ^T : nativeint  and ^U : nativeint  = (# "sub.ovf" x y : nativeint #)
                 when ^T : unativeint and ^U : unativeint = (# "sub.ovf.un" x y : unativeint #)
                 when ^T : int16       and ^U : int16      = (# "conv.ovf.i2" (# "sub.ovf" x y : int32 #) : int16 #)
                 when ^T : uint16      and ^U : uint16     = (# "conv.ovf.u2.un" (# "sub.ovf.un" x y : uint32 #) : uint16 #)
                 when ^T : char        and ^U : char       = (# "conv.ovf.u2.un" (# "sub.ovf.un" x y : uint32 #) : char #)
                 when ^T : sbyte       and ^U : sbyte      = (# "conv.ovf.i1" (# "sub.ovf" x y : int32 #) : sbyte #)
                 when ^T : byte        and ^U : byte       = (# "conv.ovf.u1.un" (# "sub.ovf.un" x y : uint32 #) : byte #)
                 when ^T : decimal     and ^U : decimal    = (# "" (Decimal.op_Subtraction((# "" x : decimal #),(# "" y : decimal #))) : ^V #)
                 when ^T : ^T = ((^T or ^U): (static member (-) : ^T * ^U -> ^V) (x,y))

            [<NoDynamicInvocation(isLegacy=true)>]
            let inline (~-) (value: ^T) : ^T =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : int32     = (# "sub.ovf" 0 value  : int32 #)
                 when ^T : float     = (# "neg" value  : float #)
                 when ^T : float32   = (# "neg" value  : float32 #)
                 when ^T : int64     = (# "sub.ovf" 0L value  : int64 #)
                 when ^T : int16     = (# "sub.ovf" 0s value  : int16 #)
                 when ^T : nativeint = (# "sub.ovf" 0n value  : nativeint #)
                 when ^T : sbyte     = (# "sub.ovf" 0y value  : sbyte #)
                 when ^T : decimal   = (# "" (Decimal.op_UnaryNegation((# "" value : decimal #))) : ^T #)
                 when ^T : ^T = (^T : (static member (~-) : ^T -> ^T) (value))

            let inline ( * ) (x: ^T) (y: ^U) : ^V =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : sbyte       and ^U : sbyte     = (# "conv.ovf.i1" (# "mul.ovf" x y : int32 #) : sbyte #)
                 when ^T : int16       and ^U : int16     = (# "conv.ovf.i2" (# "mul.ovf" x y : int32 #) : int16 #)
                 when ^T : int64      and ^U : int64      = (# "mul.ovf" x y : int64 #)
                 when ^T : int32      and ^U : int32      = (# "mul.ovf" x y : int32 #)
                 when ^T : nativeint  and ^U : nativeint  = (# "mul.ovf" x y : nativeint #)
                 when ^T : byte        and ^U : byte      = (# "conv.ovf.u1.un" (# "mul.ovf.un" x y : uint32 #) : byte #)
                 when ^T : uint16      and ^U : uint16    = (# "conv.ovf.u2.un" (# "mul.ovf.un" x y : uint32 #) : uint16 #)
                 when ^T : uint32     and ^U : uint32     = (# "mul.ovf.un" x y : uint32 #)
                 when ^T : uint64     and ^U : uint64     = (# "mul.ovf.un" x y : uint64 #)
                 when ^T : unativeint and ^U : unativeint = (# "mul.ovf.un" x y : unativeint #)
                 when ^T : float      and ^U : float      = (# "mul" x y : float #)
                 when ^T : float32    and ^U : float32    = (# "mul" x y : float32 #)
                 when ^T : decimal     and ^U : decimal   = (# "" (Decimal.op_Multiply((# "" x : decimal #),(# "" y : decimal #))) : ^V #)
                 // According to the somewhat subtle rules of static optimizations,
                 // this condition is used whenever ^T is resolved to a nominal type
                 // That is, not in the generic implementation of '*'
                 when ^T : ^T = ((^T or ^U): (static member (*) : ^T * ^U -> ^V) (x,y))

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToByte")>]
            let inline byte (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.u1" value  : byte #)
                 when ^T : float32    = (# "conv.ovf.u1" value  : byte #)
                 when ^T : int64      = (# "conv.ovf.u1" value  : byte #)
                 when ^T : int32      = (# "conv.ovf.u1" value  : byte #)
                 when ^T : int16      = (# "conv.ovf.u1" value  : byte #)
                 when ^T : nativeint  = (# "conv.ovf.u1" value  : byte #)
                 when ^T : sbyte      = (# "conv.ovf.u1" value  : byte #)
                 when ^T : uint64     = (# "conv.ovf.u1.un" value  : byte #)
                 when ^T : uint32     = (# "conv.ovf.u1.un" value  : byte #)
                 when ^T : uint16     = (# "conv.ovf.u1.un" value  : byte #)
                 when ^T : char       = (# "conv.ovf.u1.un" value  : byte #)
                 when ^T : unativeint = (# "conv.ovf.u1.un" value  : byte #)
                 when ^T : byte       = (# "" value  : byte #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> byte) (value))

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToSByte")>]
            let inline sbyte (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.i1" value  : sbyte #)
                 when ^T : float32    = (# "conv.ovf.i1" value  : sbyte #)
                 when ^T : int64      = (# "conv.ovf.i1" value  : sbyte #)
                 when ^T : int32      = (# "conv.ovf.i1" value  : sbyte #)
                 when ^T : int16      = (# "conv.ovf.i1" value  : sbyte #)
                 when ^T : nativeint  = (# "conv.ovf.i1" value  : sbyte #)
                 when ^T : sbyte      = (# "" value  : sbyte #)
                 when ^T : uint64     = (# "conv.ovf.i1.un" value  : sbyte #)
                 when ^T : uint32     = (# "conv.ovf.i1.un" value  : sbyte #)
                 when ^T : uint16     = (# "conv.ovf.i1.un" value  : sbyte #)
                 when ^T : char       = (# "conv.ovf.i1.un" value  : sbyte #)
                 when ^T : unativeint = (# "conv.ovf.i1.un" value  : sbyte #)
                 when ^T : byte       = (# "conv.ovf.i1.un" value  : sbyte #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> sbyte) (value))

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToUInt16")>]
            let inline uint16 (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.u2" value  : uint16 #)
                 when ^T : float32    = (# "conv.ovf.u2" value  : uint16 #)
                 when ^T : int64      = (# "conv.ovf.u2" value  : uint16 #)
                 when ^T : int32      = (# "conv.ovf.u2" value  : uint16 #)
                 when ^T : int16      = (# "conv.ovf.u2" value  : uint16 #)
                 when ^T : nativeint  = (# "conv.ovf.u2" value  : uint16 #)
                 when ^T : sbyte      = (# "conv.ovf.u2" value  : uint16 #)
                 when ^T : uint64     = (# "conv.ovf.u2.un" value  : uint16 #)
                 when ^T : uint32     = (# "conv.ovf.u2.un" value  : uint16 #)
                 when ^T : uint16     = (# "" value  : uint16 #)
                 when ^T : char       = (# "" value  : uint16 #)
                 when ^T : unativeint = (# "conv.ovf.u2.un" value  : uint16 #)
                 when ^T : byte       = (# "conv.ovf.u2.un" value  : uint16 #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> uint16) (value))

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToChar")>]
            let inline char (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.u2" value  : char #)
                 when ^T : float32    = (# "conv.ovf.u2" value  : char #)
                 when ^T : int64      = (# "conv.ovf.u2" value  : char #)
                 when ^T : int32      = (# "conv.ovf.u2" value  : char #)
                 when ^T : int16      = (# "conv.ovf.u2" value  : char #)
                 when ^T : nativeint  = (# "conv.ovf.u2" value  : char #)
                 when ^T : sbyte      = (# "conv.ovf.u2" value  : char #)
                 when ^T : uint64     = (# "conv.ovf.u2.un" value  : char #)
                 when ^T : uint32     = (# "conv.ovf.u2.un" value  : char #)
                 when ^T : uint16     = (# "" value  : char #)
                 when ^T : char       = (# "" value  : char #)
                 when ^T : unativeint = (# "conv.ovf.u2.un" value  : char #)
                 when ^T : byte       = (# "conv.ovf.u2.un" value  : char #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> char) (value))

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToInt16")>]
            let inline int16 (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.i2" value  : int16 #)
                 when ^T : float32    = (# "conv.ovf.i2" value  : int16 #)
                 when ^T : int64      = (# "conv.ovf.i2" value  : int16 #)
                 when ^T : int32      = (# "conv.ovf.i2" value  : int16 #)
                 when ^T : int16      = (# "" value  : int16 #)
                 when ^T : nativeint  = (# "conv.ovf.i2" value  : int16 #)
                 when ^T : sbyte      = (# "conv.ovf.i2" value  : int16 #)
                 when ^T : uint64     = (# "conv.ovf.i2.un" value  : int16 #)
                 when ^T : uint32     = (# "conv.ovf.i2.un" value  : int16 #)
                 when ^T : uint16     = (# "conv.ovf.i2.un" value  : int16 #)
                 when ^T : char       = (# "conv.ovf.i2.un" value  : int16 #)
                 when ^T : unativeint = (# "conv.ovf.i2.un" value  : int16 #)
                 when ^T : byte       = (# "conv.ovf.i2.un" value  : int16 #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> int16) (value))

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToUInt32")>]
            let inline uint32 (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.u4" value  : uint32 #)
                 when ^T : float32    = (# "conv.ovf.u4" value  : uint32 #)
                 when ^T : int64      = (# "conv.ovf.u4" value  : uint32 #)
                 when ^T : int32      = (# "conv.ovf.u4" value  : uint32 #)
                 when ^T : int16      = (# "conv.ovf.u4" value  : uint32 #)
                 when ^T : nativeint  = (# "conv.ovf.u4" value  : uint32 #)
                 when ^T : sbyte      = (# "conv.ovf.u4" value  : uint32 #)
                 when ^T : uint64     = (# "conv.ovf.u4.un" value  : uint32 #)
                 when ^T : uint32     = (# "" value  : uint32 #)
                 when ^T : uint16     = (# "conv.ovf.u4.un" value  : uint32 #)
                 when ^T : char       = (# "conv.ovf.u4.un" value  : uint32 #)
                 when ^T : unativeint = (# "conv.ovf.u4.un" value  : uint32 #)
                 when ^T : byte       = (# "conv.ovf.u4.un" value  : uint32 #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> uint32) (value))

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToInt32")>]
            let inline int32 (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.i4" value  : int32 #)
                 when ^T : float32    = (# "conv.ovf.i4" value  : int32 #)
                 when ^T : int64      = (# "conv.ovf.i4" value  : int32 #)
                 when ^T : int32      = (# "" value  : int32 #)
                 when ^T : int16      = (# "conv.ovf.i4" value  : int32 #)
                 when ^T : nativeint  = (# "conv.ovf.i4" value  : int32 #)
                 when ^T : sbyte      = (# "conv.ovf.i4" value  : int32 #)
                 when ^T : uint64     = (# "conv.ovf.i4.un" value  : int32 #)
                 when ^T : uint32     = (# "conv.ovf.i4.un" value  : int32 #)
                 when ^T : uint16     = (# "conv.ovf.i4.un" value  : int32 #)
                 when ^T : char       = (# "conv.ovf.i4.un" value  : int32 #)
                 when ^T : unativeint = (# "conv.ovf.i4.un" value  : int32 #)
                 when ^T : byte       = (# "conv.ovf.i4.un" value  : int32 #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> int32) (value))

            [<CompiledName("ToInt")>]
            let inline int value = int32 value

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToUInt64")>]
            let inline uint64 (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.u8" value  : uint64 #)
                 when ^T : float32    = (# "conv.ovf.u8" value  : uint64 #)
                 when ^T : int64      = (# "conv.ovf.u8" value  : uint64 #)
                 when ^T : int32      = (# "conv.ovf.u8" value  : uint64 #)
                 when ^T : int16      = (# "conv.ovf.u8" value  : uint64 #)
                 when ^T : nativeint  = (# "conv.ovf.u8" value  : uint64 #)
                 when ^T : sbyte      = (# "conv.ovf.u8" value  : uint64 #)
                 when ^T : uint64     = (# "" value  : uint64 #)
                 when ^T : uint32     = (# "conv.ovf.u8.un" value  : uint64 #)
                 when ^T : uint16     = (# "conv.ovf.u8.un" value  : uint64 #)
                 when ^T : char       = (# "conv.ovf.u8.un" value  : uint64 #)
                 when ^T : unativeint = (# "conv.ovf.u8.un" value  : uint64 #)
                 when ^T : byte       = (# "conv.ovf.u8.un" value  : uint64 #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> uint64) (value))

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToInt64")>]
            let inline int64 (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.i8" value  : int64 #)
                 when ^T : float32    = (# "conv.ovf.i8" value  : int64 #)
                 when ^T : int64      = (# "" value  : int64 #)
                 when ^T : int32      = (# "conv.ovf.i8" value  : int64 #)
                 when ^T : int16      = (# "conv.ovf.i8" value  : int64 #)
                 when ^T : nativeint  = (# "conv.ovf.i8" value  : int64 #)
                 when ^T : sbyte      = (# "conv.ovf.i8" value  : int64 #)
                 when ^T : uint64     = (# "conv.ovf.i8.un" value  : int64 #)
                 when ^T : uint32     = (# "conv.ovf.i8.un" value  : int64 #)
                 when ^T : uint16     = (# "conv.ovf.i8.un" value  : int64 #)
                 when ^T : char       = (# "conv.ovf.i8.un" value  : int64 #)
                 when ^T : unativeint = (# "conv.ovf.i8.un" value  : int64 #)
                 when ^T : byte       = (# "conv.ovf.i8.un" value  : int64 #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> int64) (value))

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToUIntPtr")>]
            let inline unativeint (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.u" value  : unativeint #)
                 when ^T : float32    = (# "conv.ovf.u" value  : unativeint #)
                 when ^T : int64      = (# "conv.ovf.u" value  : unativeint #)
                 when ^T : int32      = (# "conv.ovf.u" value  : unativeint #)
                 when ^T : int16      = (# "conv.ovf.u" value  : unativeint #)
                 when ^T : nativeint  = (# "conv.ovf.u" value  : unativeint #)
                 when ^T : sbyte      = (# "conv.ovf.u" value  : unativeint #)
                 when ^T : uint64     = (# "conv.ovf.u.un" value  : unativeint #)
                 when ^T : uint32     = (# "conv.ovf.u.un" value  : unativeint #)
                 when ^T : uint16     = (# "conv.ovf.u.un" value  : unativeint #)
                 when ^T : char       = (# "conv.ovf.u.un" value  : unativeint #)
                 when ^T : unativeint = (# "" value  : unativeint #)
                 when ^T : byte       = (# "conv.ovf.u.un" value  : unativeint #)
                 when ^T : decimal    = (# "conv.ovf.u.un" (uint64 (# "" value : decimal #)) : unativeint #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> unativeint) (value))

            [<NoDynamicInvocation(isLegacy=true)>]
            [<CompiledName("ToIntPtr")>]
            let inline nativeint (value: ^T) =
                 raise (NotSupportedException("Checked: a type with no such operator"))
                 when ^T : float      = (# "conv.ovf.i" value  : nativeint #)
                 when ^T : float32    = (# "conv.ovf.i" value  : nativeint #)
                 when ^T : int64      = (# "conv.ovf.i" value  : nativeint #)
                 when ^T : int32      = (# "conv.ovf.i" value  : nativeint #)
                 when ^T : int16      = (# "conv.ovf.i" value  : nativeint #)
                 when ^T : nativeint  = (# "" value  : nativeint #)
                 when ^T : sbyte      = (# "conv.ovf.i" value  : nativeint #)
                 when ^T : uint64     = (# "conv.ovf.i.un" value  : nativeint #)
                 when ^T : uint32     = (# "conv.ovf.i.un" value  : nativeint #)
                 when ^T : uint16     = (# "conv.ovf.i.un" value  : nativeint #)
                 when ^T : char       = (# "conv.ovf.i.un" value  : nativeint #)
                 when ^T : unativeint = (# "conv.ovf.i.un" value  : nativeint #)
                 when ^T : byte       = (# "conv.ovf.i.un" value  : nativeint #)
                 when ^T : decimal    = (# "conv.ovf.i" (int64 (# "" value : decimal #)) : nativeint #)
                 when ^T : ^T = (^T : (static member op_Explicit: ^T -> nativeint) (value))

        module Unchecked =
            [<CompiledName("DefaultOf")>]
            let inline defaultof<'T> = (# "ilzero !0" type ('T) : 'T #)

            [<CompiledName("Compare")>]
            let inline compare (x: 'T) (y: 'T) = GenericComparison x y

            [<CompiledName("Equals")>]
            let inline equals (x: 'T) (y: 'T) = GenericEquality x y

            [<CompiledName("Hash")>]
            let inline hash (x: 'T) = GenericHash x

        module OperatorIntrinsics =
            [<Sealed>]
            type RangeInt32Enumerator(start: int, step: int, finish: int) =
                let mutable current = start - step
                let mutable started = false
                interface IEnumerator<int> with
                    member _.Current = current
                interface System.Collections.IEnumerator with
                    member _.MoveNext() =
                        if not started then
                            started <- true
                            current <- start
                        else
                            current <- current + step
                        if step > 0 then current <= finish else current >= finish
                    member _.Current = box current
                    member _.Reset() = started <- false
                interface IDisposable with
                    member _.Dispose() = ()

            [<Sealed>]
            type RangeInt32(start: int, step: int, finish: int) =
                interface IEnumerable<int> with
                    member _.GetEnumerator() = new RangeInt32Enumerator(start, step, finish) :> IEnumerator<int>
                interface System.Collections.IEnumerable with
                    member _.GetEnumerator() = new RangeInt32Enumerator(start, step, finish) :> System.Collections.IEnumerator

            let RangeInt32 start step finish : seq<int> =
                if step = 0 then invalidArg "step" "The step of a range cannot be zero."
                RangeInt32(start, step, finish) :> seq<int>

            // Any other type with + and a zero or one (int64, float, char, ...):
            // from start, adding step while the result hasn't passed finish
            // (and hasn't wrapped around at the type's end).
            [<Sealed>]
            type RangeEnumerator<'T, 'Step>(start: 'T, step: 'Step, add: 'T -> 'Step -> 'T, up: bool, finish: 'T) =
                let mutable current = start
                let mutable state = 0 // 0 = not started, 1 = going, 2 = done
                let before (a: 'T) (b: 'T) = if up then GenericLessThan a b else GenericGreaterThan a b
                interface IEnumerator<'T> with
                    member _.Current = current
                interface System.Collections.IEnumerator with
                    member _.MoveNext() =
                        if state = 0 then
                            state <- if before finish start then 2 else 1
                        elif state = 1 then
                            if before current finish then
                                let next = add current step
                                if before current next && not (before finish next) then current <- next
                                else state <- 2
                            else state <- 2
                        state = 1
                    member _.Current = box current
                    member _.Reset() = state <- 0
                interface IDisposable with
                    member _.Dispose() = ()

            [<Sealed>]
            type RangeEnumerable<'T, 'Step>(start: 'T, step: 'Step, add: 'T -> 'Step -> 'T, up: bool, finish: 'T) =
                interface IEnumerable<'T> with
                    member _.GetEnumerator() = new RangeEnumerator<'T, 'Step>(start, step, add, up, finish) :> IEnumerator<'T>
                interface System.Collections.IEnumerable with
                    member _.GetEnumerator() = new RangeEnumerator<'T, 'Step>(start, step, add, up, finish) :> System.Collections.IEnumerator

            let RangeGeneric (one: 'T) (add: 'T -> 'T -> 'T) (start: 'T) (finish: 'T) : seq<'T> =
                RangeEnumerable<'T, 'T>(start, one, add, true, finish) :> seq<'T>

            let RangeStepGeneric (zero: 'Step) (add: 'T -> 'Step -> 'T) (start: 'T) (step: 'Step) (finish: 'T) : seq<'T> =
                let c = GenericComparison step zero
                if c = 0 then invalidArg "step" "The step of a range cannot be zero."
                RangeEnumerable<'T, 'Step>(start, step, add, (c > 0), finish) :> seq<'T>

            let RangeChar (start: char) (finish: char) : seq<char> =
                RangeEnumerable<char, int>(start, 1, (fun (c: char) (n: int) -> (# "conv.u2" ((# "" c : int #) + n) : char #)), true, finish) :> seq<char>

        [<CompiledName("Range")>]
        let inline (..) (start: ^T) (finish: ^T) : seq< ^T > =
            OperatorIntrinsics.RangeGeneric (GenericOne< ^T >) (fun x y -> x + y) start finish
            when ^T : int32 = retype (OperatorIntrinsics.RangeInt32 (retype start) 1 (retype finish))
            when ^T : char = retype (OperatorIntrinsics.RangeChar (retype start) (retype finish))

        [<CompiledName("RangeStep")>]
        let inline (.. ..) (start: ^T) (step: ^Step) (finish: ^T) : seq< ^T > =
            OperatorIntrinsics.RangeStepGeneric (GenericZero< ^Step >) (fun x y -> x + y) start step finish
            when ^T : int32 and ^Step : int32 = retype (OperatorIntrinsics.RangeInt32 (retype start) (retype step) (retype finish))
