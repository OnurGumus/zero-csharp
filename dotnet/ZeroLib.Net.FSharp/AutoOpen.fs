// Opens Microsoft.FSharp.Control in the projects that reference this
// assembly, so [<Safety>] and [<RequiresUnsafe>] need no open (zero's
// FSharp.Control does the same, fsharp/control/Control.fs).
namespace Microsoft.FSharp
    [<assembly: AutoOpen("Microsoft.FSharp.Control")>]
    do ()
