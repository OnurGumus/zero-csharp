// What zero's own FSharp.Core (fsharp/core/attrs.fs) has that FSharp.Core on
// .NET doesn't: the marks for code that calls unsafe members, which the SDK's
// check (ZERO007) reads by name. In Microsoft.FSharp.Control, which F# opens
// by default as it does Microsoft.FSharp.Core: Control.fs and Arena.fs, in
// that namespace, see them too (a type declared in the same compilation in
// Microsoft.FSharp.Core isn't opened by default).
namespace Microsoft.FSharp.Control

    open System

    /// [<Safety "why">]: the unsafe calls inside are used correctly, for the
    /// reason given (zero mode's FSharp.Core has it too).
    type SafetyAttribute = NoCompilerInliningAttribute

    /// The function may call unsafe members, and calling it is unsafe in turn.
    [<AttributeUsage (AttributeTargets.Method ||| AttributeTargets.Property ||| AttributeTargets.Constructor, AllowMultiple=false)>]
    [<Sealed>]
    type RequiresUnsafeAttribute() =
        inherit Attribute()
