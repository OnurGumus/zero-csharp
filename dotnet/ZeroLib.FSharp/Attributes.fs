// What zero's own FSharp.Core (fsharp/core/attrs.fs) has that FSharp.Core on
// .NET doesn't: the marks for code that calls unsafe members, which the SDK's
// check (ZERO007) reads by name. In Microsoft.FSharp.Control, which F# opens
// by default as it does Microsoft.FSharp.Core: Control.fs and Arena.fs, in
// that namespace, see them too (a type declared in the same compilation in
// Microsoft.FSharp.Core isn't opened by default).
namespace Microsoft.FSharp.Control

    open System

    /// [<Safety "why">]: the unsafe calls inside are used correctly, for the
    /// reason given. (zero mode's FSharp.Core makes it NoCompilerInlining with
    /// the reason, so that fsc never copies a marked function into callers;
    /// FSharp.Core on .NET has that attribute without an argument, so here it's
    /// an attribute of its own, which the check reads by name. A marked
    /// function fsc does copy shows as a warning in its caller, not a miss.)
    [<AttributeUsage (AttributeTargets.Method ||| AttributeTargets.Property ||| AttributeTargets.Constructor, AllowMultiple=false)>]
    [<Sealed>]
    type SafetyAttribute(reason: string) =
        inherit Attribute()
        member _.Reason = reason

    /// The function may call unsafe members, and calling it is unsafe in turn.
    [<AttributeUsage (AttributeTargets.Method ||| AttributeTargets.Property ||| AttributeTargets.Constructor, AllowMultiple=false)>]
    [<Sealed>]
    type RequiresUnsafeAttribute() =
        inherit Attribute()
