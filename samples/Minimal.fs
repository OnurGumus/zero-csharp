// Example: Minimal zero-mode "Hello World" in F#: the C library's puts, called
// directly. Minimal.cs is the same program in C#. The SDK's tests
// (fsharp_doc_samples) build it as an F# zero project's Program.fs.
#nowarn "9" // a native pointer: the call below reads through it

open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

[<DllImport("libc")>]
extern int puts(nativeint s)

// puts reads up to the 0 that ends the text.
let say (text: byte[]) =
    use bytes = fixed text
    puts (NativePtr.toNativeInt bytes) |> ignore

// "..."B is the text's bytes.
say "Hello from zero-mode!\000"B
