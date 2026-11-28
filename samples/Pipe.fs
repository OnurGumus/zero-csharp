// Example: standard input as bytes (lib/In.cs's Read), in F#. Pipe.cs is the
// same program in C#; it prints the same lines. The SDK's tests
// (fsharp_doc_samples) feed it tests/input/Pipe.txt: the line "pipe test\r\n",
// every byte value from 0 to 255 twice over, then "end" without a newline.
// Exit code = number of failed checks.

open System

let mutable failures = 0
let check (name: string) ok =
    if not ok then
        failures <- failures + 1
        printfn "  FAIL: %s" name

// A byte past "end" is wrong whatever it is: no byte is -1.
let expected index =
    if index < 512 then int (byte index)
    elif index < 512 + 3 then int ("end"[index - 512])
    else -1

let mutable total = 0
let mutable wrong = -1
let mutable hash = 2166136261u // FNV-1a

let take (b: byte) =
    if int b <> expected total && wrong < 0 then wrong <- total
    hash <- (hash ^^^ uint32 b) * 16777619u
    total <- total + 1

let line: char[] = Array.zeroCreate 32
let length = In.ReadLine(Span<char>(line))
check "the first line is text" (length = 9)
if length >= 0 && length <= line.Length then
    Console.WriteLine(ReadOnlySpan<char>(line, 0, length))

// The bytes after the line, read in small pieces: the first pieces come
// from what ReadLine read ahead, the later ones straight from read().
// docs:read
let chunk: byte[] = Array.zeroCreate 7
let mutable count = In.Read(Span<byte>(chunk))
while count > 0 do
    for i in 0 .. count - 1 do
        take chunk[i]
    count <- In.Read(Span<byte>(chunk))
// docs:end

printfn "%d bytes, FNV-1a %08X" total hash
if wrong >= 0 then printfn "first wrong byte at %d" wrong
check "every byte arrived" (total = 512 + 3)
check "every byte as it was sent" (wrong < 0)
check "0 again after the end" (In.Read(Span<byte>(chunk)) = 0)
check "0 for an empty buffer" (In.Read(Span<byte>.Empty) = 0)
check "ReadLine at the end" (In.ReadLine(Span<char>(line)) = -1)

printfn "%s" (if failures = 0 then "PASS" else "FAIL")
exit failures
