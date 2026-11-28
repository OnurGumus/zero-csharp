// Test: System.Console from F#, as .NET has it: Write and WriteLine with
// every kind of value, ReadLine, Read, and ReadKey with KeyAvailable.
// Keys.cs is the same program in C#; it prints the same lines. The SDK's
// tests (fsharp_doc_samples) pipe tests/input/Keys.txt in, so ReadKey reads
// keys from it as .NET for Unix does when input isn't a terminal.

open System

let has (key: ConsoleKeyInfo) (modifier: ConsoleModifiers) =
    if key.Modifiers &&& modifier <> enum 0 then 1 else 0

// docs:write
Console.WriteLine("Hello, {0}!", "world")
Console.WriteLine($"[{42,5}] [{3.14159:F2}] [{true}]")
Console.Write(1.5m)
Console.Write(' ')
Console.Write(0.1f)
Console.Write(' ')
Console.WriteLine(UInt64.MaxValue)
// docs:end
Console.WriteLine(null: obj)
Console.WriteLine(-7L)

// docs:read
Console.Write("Your name? ")
let name = Console.ReadLine()
printfn "Hello, %s." name
let c = Console.Read()
printfn "Read: %c" (char c)
// docs:end

printfn $"KeyAvailable: {Console.KeyAvailable}"
Console.Write("echoed: ")
let first = Console.ReadKey()
Console.WriteLine()
printfn "first: %d '%c'" (int first.Key) first.KeyChar

// docs:readkey
let rec readKeys () =
    let key = Console.ReadKey(true)
    if key.KeyChar <> 'q' then
        printfn "key %3d char %4d shift %d alt %d ctrl %d"
            (int key.Key) (int key.KeyChar)
            (has key ConsoleModifiers.Shift) (has key ConsoleModifiers.Alt)
            (has key ConsoleModifiers.Control)
        readKeys ()
readKeys ()
// docs:end
Console.WriteLine("done")
