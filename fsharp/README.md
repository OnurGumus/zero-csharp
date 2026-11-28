# F# on zerocore

What F# projects (`.fsproj` with `ZeroCSharp.Sdk`) compile against: a small FSharp.Core, printf, and the few .NET types the F# compiler needs that zerocore lacks. `fsharp/build-core.sh` builds them into the SDK payload's `ref/fsharp/` (`./build.sh --sdk-payload` runs it). It needs only a .NET SDK, 10 or newer (its C# and F# compilers), not bflat.

| Path | What |
|---|---|
| `core/` | FSharp.Core: the prelude and attributes (from dotnet/fsharp, MIT); `LanguagePrimitives` with structural equality, comparison and hashing through interfaces instead of reflection; `FSharpFunc` and closures; `Option`, `ValueOption`, `Result`, `Choice`, `Ref`, `list`; `Operators`, with ranges of any numeric type and `char`, the math functions (`sqrt`, `round`, `sin`, `pown`, ...) and `decimal`; `seq { }`, list and array comprehensions (`seqcore.fs`: what the compiler calls for them); the `Seq`, `List`, `Array` and `String` modules; `Map` and `Set`; `lazy` (`lazy.fs`, over zerocore's `System.Lazy<T>`); the format types printf uses |
| `core/map.fs`, `core/set.fs` | dotnet/fsharp's own, made by `core/adapt-mapset.py` (edit that, not them): the AVL trees and modules as they are, without serialization, debugger views and `IDictionary` |
| `printf/` | `FSharp.Printf.dll`: `printf`, `printfn`, `eprintf`, `sprintf`, `Arena.Sprintf`, `failwithf`, `ksprintf` and the others |
| `control/` | `FSharp.Control.dll` (in `ref/`, next to `BflatLib.dll`): `task`, `async`, `MailboxProcessor` (on lib's tasks and fibers), `arena frame { }` (on lib's object scopes) |
| `shim/` | `System.Runtime.Extensions.dll`: `Tuple` (with .NET's `ToString`), `KeyValuePair`, `IStructuralEquatable`, `IList<T>` and attributes the F# compiler emits or looks up |

`decimal` itself is zerocore's (`zerocore/System/Decimal.cs`), so C# zero programs have it too.

Not there yet: `%A` (the compiler refuses it without reflection), `asin`/`acos`/`atan`/`sinh`/`cosh`/`tanh`, `async` and `task` (computation expressions with the program's own builders work), `dict`, `Map`'s `IDictionary` view, and catching exceptions (zero mode can't: `try ... with` compiles, but a raised exception ends the program).

## How a program is built

1. fsc compiles the project against `ref/fsharp/` (zerocore as `System.Runtime.dll`, the shim, FSharp.Core) and `ref/` (`lib/`, with FSharp.Printf and FSharp.Control next to it), as C# projects compile against zerocore.
2. The SDK reads the entry point from the IL (an `[<EntryPoint>]` function, or the `main@` fsc makes for top-level code) and writes a C# `Main` that calls it through `[UnsafeAccessor]`. For a native library it writes a C# export for each `[<UnmanagedCallersOnly(EntryPoint = ...)>]` F# function instead.
3. bflat compiles that C# with the F# IL, FSharp.Core and the libraries as references, into one native program, as for C#.

## Why it's done this way

- fsc takes .NET types (`Tuple`, `IStructuralEquatable`) only from assemblies on its list of system assembly names, so the shim is named `System.Runtime.Extensions`.
- With `--targetprofile:netcore`, fsc finds its primary assembly by the file name `System.Runtime.dll`, and the IL it writes refers to that assembly's own name, `System.Private.CoreLib`, which bflat finds. Without it fsc silently uses the machine's .NET.
- zerocore's metadata version string is empty. fsc copies it and then writes ECMA-335 1.x metadata tables, which bflat misreads ("Failed to load type ... FSharpList`1<!0>"). `--metadataversion:v4.0.30319` fixes it.
- `--reflectionfree`: records and unions get no `ToString` through `sprintf`.
- bflat names the assembly it compiles after its output file, so the C# `Main` is compiled to a file in `obj/` and the program is copied out: under the program's name it would clash with the F# assembly.
- fsc takes a type as supporting comparison (`'T: comparison`) when it implements the non-generic `IComparable`, so zerocore's numbers and strings implement it.
- `Seq` functions take arrays and strings as `seq<'T>`, so zerocore's arrays (through `Array<T>`) and strings implement `IEnumerable<T>`.
- printf is a library of its own: with its overloads inside FSharp.Core, fsc 10 crashes on a user's `let inline log fmt = printfn fmt`. Each argument count has its own overload, chosen by the format's argument types, so the compiler builds a printf call's curried function at compile time without reflection.
- fsc treats `abs` (and the other operators it knows) on `decimal` as built in, and expects FSharp.Core's definition to have a `when ^T : decimal` case: without one, compiling `abs 1.5M` never finishes.
- Inside FSharp.Core, an inline function's `:?>` and a `seq { }` make fsc fail with "Undefined value" (FS0971); the core uses `castclass` and the `Seq` functions there. User code isn't affected.
- F# compilers after SDK 10.0.402 generate a `ToString` for every record and union under `--reflectionfree`, built from `Operators.box` and `Operators.string`. `core.fs` comes before `ops.fs` defines `Operators`, so its records and unions (`Choice`, `Ref`, `Option`, `Result`, `ValueOption`, `list`) write their own; otherwise those compilers fail with "did not contain ... 'Operators'". Any F# compiler from SDK 10 on builds this FSharp.Core.
